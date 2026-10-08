using System.Globalization;
using System.Threading.Channels;
using IoTCom.Net.Transport.Can;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IoTCom.Net.Protocols.CanOpen;

/// <summary>
/// A CANopen device (CiA 301 slave) on an <see cref="ICanBus"/>: boot-up and NMT state machine, heartbeat producer
/// (object 0x1017), SDO server (expedited and segmented, with aborts), TPDOs (event-driven with event timer or every
/// n-th SYNC), RPDOs written into the object dictionary, and emergency messages.
/// </summary>
public sealed class CanOpenNode : IAsyncDisposable
{
    private readonly ICanBus _bus;
    private readonly ILogger _log;
    private readonly Channel<CanFrame> _inbox = Channel.CreateBounded<CanFrame>(new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, long> _lastTpdo = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> _pendingTpdo = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private Task? _timers;
    private SdoTransfer? _transfer;
    private int _syncCount;

    private sealed class SdoTransfer
    {
        public required CanOpenEntry Entry { get; init; }
        public required bool Upload { get; init; }
        public byte[] Data { get; set; } = [];
        public int Offset { get; set; }
        public bool Toggle { get; set; }
        public uint ExpectedSize { get; init; }
    }

    private CanOpenNode(ICanBus bus, byte nodeId, ObjectDictionary od, ILogger? logger)
    {
        if (nodeId is < 1 or > 127) throw new ArgumentOutOfRangeException(nameof(nodeId), "CANopen node ids are 1–127.");
        (_bus, NodeId, Dictionary, _log) = (bus, nodeId, od, logger ?? NullLogger.Instance);
    }

    /// <summary>Creates a node; call <see cref="StartAsync"/> to boot it.</summary>
    public static CanOpenNode Create(ICanBus bus, byte nodeId, ObjectDictionary dictionary, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(dictionary);
        return new CanOpenNode(bus, nodeId, dictionary, logger);
    }

    /// <summary>Node id (1–127).</summary>
    public byte NodeId { get; }

    /// <summary>Object dictionary.</summary>
    public ObjectDictionary Dictionary { get; }

    /// <summary>NMT state.</summary>
    public NmtState State { get; private set; } = NmtState.BootUp;

    /// <summary>Raised when the NMT state changes.</summary>
    public event Action<NmtState>? StateChanged;

    /// <summary>Raised when an SDO download or RPDO changed an entry.</summary>
    public event Action<CanOpenEntry>? RemoteWrite;

    /// <summary>Connects the bus if needed, sends the boot-up message and enters pre-operational.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_cts is not null) return;
        if (_bus.State != EndpointState.Connected) await _bus.ConnectAsync(ct).ConfigureAwait(false);
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _bus.FrameReceived += OnFrame;
        _loop = Task.Run(() => LoopAsync(token), CancellationToken.None);
        foreach (var e in Dictionary.Entries.Where(e => e.PdoMappable)) e.Changed += OnLocalChange;
        await BootAsync(ct).ConfigureAwait(false);
        _timers = Task.Run(() => TimersAsync(token), CancellationToken.None);
    }

    private async Task BootAsync(CancellationToken ct)
    {
        _transfer = null;
        await _bus.SendAsync(CanOpenCodec.Heartbeat(NodeId, NmtState.BootUp), ct).ConfigureAwait(false);
        SetState(NmtState.PreOperational);
    }

    private void SetState(NmtState s)
    {
        if (State == s) return;
        State = s;
        _log.LogInformation("CANopen node {Node}: {State}", NodeId, s);
        StateChanged?.Invoke(s);
    }

    private void OnFrame(CanFrame f)
    {
        if (!f.IsExtended && !f.IsRemote) _inbox.Writer.TryWrite(f);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var f in _inbox.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await HandleAsync(f, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is FormatException or IoTComException or InvalidOperationException)
                {
                    _log.LogDebug(ex, "CANopen node {Node}: ignored frame {Frame}", NodeId, f);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task HandleAsync(CanFrame f, CancellationToken ct)
    {
        var d = f.Data.ToArray();
        if (f.Id == 0x000 && d.Length >= 2 && (d[1] == 0 || d[1] == NodeId))
        {
            switch ((NmtCommand)d[0])
            {
                case NmtCommand.Start: SetState(NmtState.Operational); break;
                case NmtCommand.Stop: SetState(NmtState.Stopped); break;
                case NmtCommand.EnterPreOperational: SetState(NmtState.PreOperational); break;
                case NmtCommand.ResetNode or NmtCommand.ResetCommunication:
                    State = NmtState.BootUp;
                    await BootAsync(ct).ConfigureAwait(false);
                    break;
            }

            return;
        }

        if (f.Id == 0x080 && State == NmtState.Operational)
        {
            var count = Interlocked.Increment(ref _syncCount);
            foreach (var n in Pdos(transmit: true))
            {
                var type = TransmissionType(true, n);
                if (type is >= 1 and <= 240 && count % type == 0) await SendTpdoAsync(n, ct).ConfigureAwait(false);
            }

            return;
        }

        if (f.Id == 0x600u + NodeId && State != NmtState.Stopped)
        {
            var response = Sdo(CanOpenCodec.DecodeSdo(d, fromServer: false));
            if (response is not null) await _bus.SendAsync(new CanFrame(0x580u + NodeId, CanOpenCodec.EncodeSdo(response)), ct).ConfigureAwait(false);
            return;
        }

        if (State == NmtState.Operational)
        {
            foreach (var n in Pdos(transmit: false))
            {
                if (CobId(false, n) != f.Id) continue;
                foreach (var (o, raw) in Dictionary.Mapping(false, n).Unpack(d))
                {
                    var e = Dictionary[o.Index, o.SubIndex];
                    e.Raw = raw;
                    RemoteWrite?.Invoke(e);
                }
            }
        }
    }

    private IEnumerable<int> Pdos(bool transmit) =>
        Enumerable.Range(1, 4).Where(n => Dictionary.Find((ushort)((transmit ? 0x1800 : 0x1400) + n - 1), 1) is not null);

    private uint CobId(bool transmit, int n) => Convert.ToUInt32(Dictionary[(ushort)((transmit ? 0x1800 : 0x1400) + n - 1), 1].Value, CultureInfo.InvariantCulture) & 0x7FF;

    private int TransmissionType(bool transmit, int n) => Convert.ToInt32(Dictionary[(ushort)((transmit ? 0x1800 : 0x1400) + n - 1), 2].Value, CultureInfo.InvariantCulture);

    private async Task SendTpdoAsync(int n, CancellationToken ct)
    {
        if (State != NmtState.Operational) return;
        var cob = Convert.ToUInt32(Dictionary[(ushort)(0x1800 + n - 1), 1].Value, CultureInfo.InvariantCulture);
        if ((cob & 0x8000_0000) != 0) return;   // PDO disabled
        _lastTpdo[n] = Environment.TickCount64;
        await _bus.SendAsync(new CanFrame(cob & 0x7FF, Dictionary.Mapping(true, n).Pack(Dictionary)), ct).ConfigureAwait(false);
    }

    private void OnLocalChange(CanOpenEntry e)
    {
        if (State != NmtState.Operational || _cts is null) return;
        foreach (var n in Pdos(transmit: true))
        {
            if (TransmissionType(true, n) < 0xFE) continue;
            if (!Dictionary.Mapping(true, n).Objects.Any(o => o.Index == e.Index && o.SubIndex == e.SubIndex)) continue;
            if (!_pendingTpdo.TryAdd(n, 0)) continue;   // coalesce changes made together into one PDO (like an inhibit time)
            var token = _cts.Token;
            var pdo = n;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(2, token).ConfigureAwait(false);
                    _pendingTpdo.TryRemove(pdo, out _);
                    await SendTpdoAsync(pdo, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }, CancellationToken.None);
        }
    }

    private async Task TimersAsync(CancellationToken ct)
    {
        long lastHeartbeat = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(10, ct).ConfigureAwait(false);
                var now = Environment.TickCount64;
                var hb = Convert.ToInt32(Dictionary.Find(0x1017, 0)?.Value ?? 0UL, CultureInfo.InvariantCulture);
                if (hb > 0 && now - lastHeartbeat >= hb && State != NmtState.BootUp)
                {
                    lastHeartbeat = now;
                    await _bus.SendAsync(CanOpenCodec.Heartbeat(NodeId, State), ct).ConfigureAwait(false);
                }

                if (State != NmtState.Operational) continue;
                foreach (var n in Pdos(transmit: true))
                {
                    var timer = Dictionary.Find((ushort)(0x1800 + n - 1), 5) is { } t ? Convert.ToInt32(t.Value, CultureInfo.InvariantCulture) : 0;
                    if (timer > 0 && TransmissionType(true, n) >= 0xFE && now - _lastTpdo.GetValueOrDefault(n) >= timer) await SendTpdoAsync(n, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IoTComException or InvalidOperationException)
        {
            _log.LogWarning(ex, "CANopen node {Node}: timers stopped", NodeId);
        }
    }

    private static SdoFrame Abort(ushort index, byte sub, uint code) => new() { Kind = SdoKind.Abort, Index = index, SubIndex = sub, AbortCode = code };

    /// <summary>The SDO server: answers one request (null for client aborts).</summary>
    private SdoFrame? Sdo(SdoFrame req)
    {
        switch (req.Kind)
        {
            case SdoKind.Abort:
                _transfer = null;
                return null;
            case SdoKind.InitiateUploadRequest:
            {
                _transfer = null;
                var e = Dictionary.Find(req.Index, req.SubIndex);
                if (e is null) return Abort(req.Index, req.SubIndex, Dictionary.HasIndex(req.Index) ? SdoAbort.NoSubIndex : SdoAbort.NoObject);
                if (e.Access == CanOpenAccess.WriteOnly) return Abort(req.Index, req.SubIndex, SdoAbort.WriteOnly);
                var raw = e.Raw;
                if (raw.Length is >= 1 and <= 4)
                    return new SdoFrame { Kind = SdoKind.InitiateUploadResponse, Index = req.Index, SubIndex = req.SubIndex, Expedited = true, SizeIndicated = true, Data = raw };
                _transfer = new SdoTransfer { Entry = e, Upload = true, Data = raw };
                return new SdoFrame { Kind = SdoKind.InitiateUploadResponse, Index = req.Index, SubIndex = req.SubIndex, SizeIndicated = true, Size = (uint)raw.Length };
            }

            case SdoKind.UploadSegmentRequest:
            {
                if (_transfer is not { Upload: true } t) return Abort(0, 0, SdoAbort.InvalidCommand);
                if (req.Toggle != t.Toggle)
                {
                    _transfer = null;
                    return Abort(t.Entry.Index, t.Entry.SubIndex, SdoAbort.ToggleBit);
                }

                var n = Math.Min(7, t.Data.Length - t.Offset);
                var chunk = t.Data.AsSpan(t.Offset, n).ToArray();
                t.Offset += n;
                var last = t.Offset >= t.Data.Length;
                var reply = new SdoFrame { Kind = SdoKind.UploadSegmentResponse, Toggle = t.Toggle, Last = last, Data = chunk };
                t.Toggle = !t.Toggle;
                if (last) _transfer = null;
                return reply;
            }

            case SdoKind.InitiateDownloadRequest:
            {
                _transfer = null;
                var e = Dictionary.Find(req.Index, req.SubIndex);
                if (e is null) return Abort(req.Index, req.SubIndex, Dictionary.HasIndex(req.Index) ? SdoAbort.NoSubIndex : SdoAbort.NoObject);
                if (e.Access is CanOpenAccess.ReadOnly or CanOpenAccess.Const) return Abort(req.Index, req.SubIndex, SdoAbort.ReadOnly);
                if (req.Expedited) return Apply(e, req.Data) ?? new SdoFrame { Kind = SdoKind.InitiateDownloadResponse, Index = req.Index, SubIndex = req.SubIndex };
                _transfer = new SdoTransfer { Entry = e, Upload = false, ExpectedSize = req.Size };
                return new SdoFrame { Kind = SdoKind.InitiateDownloadResponse, Index = req.Index, SubIndex = req.SubIndex };
            }

            case SdoKind.DownloadSegmentRequest:
            {
                if (_transfer is not { Upload: false } t) return Abort(0, 0, SdoAbort.InvalidCommand);
                if (req.Toggle != t.Toggle)
                {
                    _transfer = null;
                    return Abort(t.Entry.Index, t.Entry.SubIndex, SdoAbort.ToggleBit);
                }

                t.Data = [.. t.Data, .. req.Data];
                var reply = new SdoFrame { Kind = SdoKind.DownloadSegmentResponse, Toggle = t.Toggle };
                t.Toggle = !t.Toggle;
                if (!req.Last) return reply;
                _transfer = null;
                if (t.ExpectedSize != 0 && t.Data.Length != t.ExpectedSize) return Abort(t.Entry.Index, t.Entry.SubIndex, SdoAbort.LengthMismatch);
                return Apply(t.Entry, t.Data) ?? reply;
            }

            default:
                return Abort(req.Index, req.SubIndex, SdoAbort.InvalidCommand);
        }
    }

    /// <summary>Writes a value received over SDO; returns an abort frame when it is rejected.</summary>
    private SdoFrame? Apply(CanOpenEntry e, byte[] data)
    {
        if (CanOpenValue.Size(e.Type) is { } size && data.Length != size) return Abort(e.Index, e.SubIndex, SdoAbort.LengthMismatch);
        if (e.Validate?.Invoke(data) is { } code) return Abort(e.Index, e.SubIndex, code);
        e.Raw = data;
        RemoteWrite?.Invoke(e);
        return null;
    }

    /// <summary>Sends an emergency message and updates the error register (0x1001).</summary>
    public async Task RaiseEmergencyAsync(ushort errorCode, byte errorRegister, byte[]? manufacturer = null, CancellationToken ct = default)
    {
        if (Dictionary.Find(0x1001, 0) is { } reg) reg.Raw = [errorRegister];
        await _bus.SendAsync(new CanFrame(0x080u + NodeId, new CanOpenEmergency(errorCode, errorRegister, manufacturer ?? new byte[5]).Encode()), ct).ConfigureAwait(false);
    }

    /// <summary>Stops the node (no goodbye exists in CANopen; consumers notice the missing heartbeat).</summary>
    public async Task StopAsync()
    {
        if (_cts is null) return;
        _bus.FrameReceived -= OnFrame;
        foreach (var e in Dictionary.Entries.Where(e => e.PdoMappable)) e.Changed -= OnLocalChange;
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null) await _loop.ConfigureAwait(false);
        if (_timers is not null) await _timers.ConfigureAwait(false);
        _cts.Dispose();
        _cts = null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
