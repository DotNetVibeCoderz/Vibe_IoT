using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Channels;
using IoTCom.Net.Transport.Can;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IoTCom.Net.Protocols.CanOpen;

/// <summary>An SDO transfer was aborted by the server (or by us after a timeout).</summary>
public sealed class CanOpenSdoException : DeviceException
{
    /// <summary>Creates the exception.</summary>
    public CanOpenSdoException(byte node, ushort index, byte subIndex, uint abortCode)
        : base($"SDO {node}:{index:X4}:{subIndex:X2} aborted: 0x{abortCode:X8} {SdoAbort.Describe(abortCode)}")
        => (Node, Index, SubIndex, AbortCode) = (node, index, subIndex, abortCode);

    /// <summary>Creates an exception.</summary>
    public CanOpenSdoException() { }

    /// <summary>Creates an exception.</summary>
    public CanOpenSdoException(string message) : base(message) { }

    /// <summary>Creates an exception.</summary>
    public CanOpenSdoException(string message, Exception inner) : base(message, inner) { }

    /// <summary>Node.</summary>
    public byte Node { get; }

    /// <summary>Index.</summary>
    public ushort Index { get; }

    /// <summary>Sub-index.</summary>
    public byte SubIndex { get; }

    /// <summary>CiA 301 abort code.</summary>
    public uint AbortCode { get; }
}

/// <summary>A node seen by the master.</summary>
public sealed class CanOpenNodeInfo
{
    internal CanOpenNodeInfo(byte id) => Id = id;

    /// <summary>Node id.</summary>
    public byte Id { get; }

    /// <summary>Last NMT state from a heartbeat or boot-up.</summary>
    public NmtState? State { get; internal set; }

    /// <summary>Last heartbeat or boot-up.</summary>
    public DateTimeOffset LastSeen { get; internal set; }

    /// <summary>Device name (0x1008) if read by a scan.</summary>
    public string? Name { get; internal set; }

    /// <summary>Identity (0x1018: vendor, product, revision, serial) if read by a scan.</summary>
    public (uint Vendor, uint Product, uint Revision, uint Serial)? Identity { get; internal set; }

    /// <summary>Device type (0x1000) if read by a scan.</summary>
    public uint? DeviceType { get; internal set; }

    /// <inheritdoc />
    public override string ToString() => $"node {Id} {State?.ToString() ?? "?"} {Name}";
}

/// <summary>Master options.</summary>
public sealed class CanOpenMasterOptions
{
    /// <summary>SDO response timeout.</summary>
    public TimeSpan SdoTimeout { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Refuse SDO downloads and NMT commands with <see cref="ReadOnlyModeException"/>.</summary>
    public bool ReadOnly { get; set; }

    /// <summary>A node is considered lost when no heartbeat arrives for this many heartbeat periods (default 3).</summary>
    public TimeSpan NodeTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }
}

/// <summary>
/// A CANopen master on an <see cref="ICanBus"/>: NMT commands, heartbeat consumer, SDO client (expedited and
/// segmented; downloads blocked in read-only mode), SYNC producer, PDO and emergency events, and a network scan.
/// </summary>
public sealed class CanOpenMaster : IAsyncDisposable
{
    private readonly ICanBus _bus;
    private readonly CanOpenMasterOptions _options;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<byte, CanOpenNodeInfo> _nodes = new();
    private readonly ConcurrentDictionary<byte, SemaphoreSlim> _sdoLocks = new();
    private readonly ConcurrentDictionary<byte, Channel<SdoFrame>> _sdoReplies = new();
    private CancellationTokenSource? _cts;

    private CanOpenMaster(ICanBus bus, CanOpenMasterOptions options)
    {
        (_bus, _options) = (bus, options);
        _log = options.Logger ?? NullLogger.Instance;
    }

    /// <summary>Creates a master.</summary>
    public static CanOpenMaster Create(ICanBus bus, Action<CanOpenMasterOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(bus);
        var o = new CanOpenMasterOptions();
        configure?.Invoke(o);
        return new CanOpenMaster(bus, o);
    }

    /// <summary>Nodes seen (heartbeat, boot-up or scan).</summary>
    public IReadOnlyCollection<CanOpenNodeInfo> Nodes => [.. _nodes.Values.OrderBy(n => n.Id)];

    /// <summary>Raised when a node's NMT state changes (from heartbeat or boot-up).</summary>
    public event Action<CanOpenNodeInfo>? NodeStateChanged;

    /// <summary>Raised for every TPDO received (node, PDO number 1–4, data).</summary>
    public event Action<byte, int, byte[]>? PdoReceived;

    /// <summary>Raised for every emergency message.</summary>
    public event Action<byte, CanOpenEmergency>? EmergencyReceived;

    /// <summary>Connects the bus if needed and starts listening.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_cts is not null) return;
        if (_bus.State != EndpointState.Connected) await _bus.ConnectAsync(ct).ConfigureAwait(false);
        _cts = new CancellationTokenSource();
        _bus.FrameReceived += OnFrame;
    }

    private CanOpenNodeInfo Node(byte id) => _nodes.GetOrAdd(id, i => new CanOpenNodeInfo(i));

    private void OnFrame(CanFrame f)
    {
        if (f.IsExtended || f.IsRemote) return;
        var (function, node, pdo) = CanOpenCodec.Classify(f.Id);
        var d = f.Data.Span;
        try
        {
            switch (function)
            {
                case CanOpenFunction.Heartbeat when d.Length >= 1:
                    var info = Node(node);
                    var state = (NmtState)(d[0] & 0x7F);
                    info.LastSeen = DateTimeOffset.UtcNow;
                    if (info.State != state)
                    {
                        info.State = state;
                        NodeStateChanged?.Invoke(info);
                    }

                    break;
                case CanOpenFunction.SdoResponse when d.Length >= 8:
                    if (_sdoReplies.TryGetValue(node, out var replies)) replies.Writer.TryWrite(CanOpenCodec.DecodeSdo(d, fromServer: true));
                    break;
                case CanOpenFunction.Tpdo:
                    PdoReceived?.Invoke(node, pdo, d.ToArray());
                    break;
                case CanOpenFunction.Emergency when d.Length >= 8:
                    EmergencyReceived?.Invoke(node, CanOpenEmergency.Parse(d));
                    break;
            }
        }
        catch (FormatException ex)
        {
            _log.LogDebug(ex, "CANopen master: ignored {Frame}", f);
        }
    }

    /// <summary>Sends an NMT command (<paramref name="node"/> 0 = all nodes); refused in read-only mode.</summary>
    public async Task NmtAsync(NmtCommand command, byte node, CancellationToken ct = default)
    {
        if (_options.ReadOnly) throw new ReadOnlyModeException();
        await _bus.SendAsync(CanOpenCodec.Nmt(command, node), ct).ConfigureAwait(false);
    }

    /// <summary>Sends one SYNC message.</summary>
    public async Task SyncAsync(CancellationToken ct = default) => await _bus.SendAsync(new CanFrame(0x080, ReadOnlyMemory<byte>.Empty), ct).ConfigureAwait(false);

    private async Task<SdoFrame> ExchangeAsync(byte node, SdoFrame request, Channel<SdoFrame> replies, CancellationToken ct)
    {
        await _bus.SendAsync(new CanFrame(0x600u + node, CanOpenCodec.EncodeSdo(request)), ct).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.SdoTimeout);
        try
        {
            return await replies.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await _bus.SendAsync(new CanFrame(0x600u + node, CanOpenCodec.EncodeSdo(new SdoFrame { Kind = SdoKind.Abort, Index = request.Index, SubIndex = request.SubIndex, AbortCode = SdoAbort.Timeout })), CancellationToken.None).ConfigureAwait(false);
            throw new IoTComTimeoutException($"Node {node} did not answer the SDO request within {_options.SdoTimeout.TotalMilliseconds:0} ms.");
        }
    }

    private async Task<T> TransactAsync<T>(byte node, Func<Channel<SdoFrame>, Task<T>> body, CancellationToken ct)
    {
        if (node is < 1 or > 127) throw new ArgumentOutOfRangeException(nameof(node), "CANopen node ids are 1–127.");
        if (_cts is null) throw new InvalidOperationException("Call StartAsync first.");
        var gate = _sdoLocks.GetOrAdd(node, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        var replies = Channel.CreateUnbounded<SdoFrame>();
        _sdoReplies[node] = replies;
        try
        {
            return await body(replies).ConfigureAwait(false);
        }
        finally
        {
            _sdoReplies.TryRemove(node, out _);
            gate.Release();
        }
    }

    /// <summary>Reads an object over SDO (expedited or segmented upload).</summary>
    public Task<byte[]> UploadAsync(byte node, ushort index, byte subIndex, CancellationToken ct = default) => TransactAsync(node, async replies =>
    {
        var r = await ExchangeAsync(node, new SdoFrame { Kind = SdoKind.InitiateUploadRequest, Index = index, SubIndex = subIndex }, replies, ct).ConfigureAwait(false);
        if (r.Kind == SdoKind.Abort) throw new CanOpenSdoException(node, index, subIndex, r.AbortCode);
        if (r.Kind != SdoKind.InitiateUploadResponse) throw new ProtocolException($"Unexpected SDO answer {r.Kind}.");
        if (r.Expedited) return r.Data;
        var data = new List<byte>((int)Math.Min(r.Size, 1 << 20));
        var toggle = false;
        while (true)
        {
            var s = await ExchangeAsync(node, new SdoFrame { Kind = SdoKind.UploadSegmentRequest, Toggle = toggle, Index = index, SubIndex = subIndex }, replies, ct).ConfigureAwait(false);
            if (s.Kind == SdoKind.Abort) throw new CanOpenSdoException(node, index, subIndex, s.AbortCode);
            if (s.Kind != SdoKind.UploadSegmentResponse || s.Toggle != toggle) throw new ProtocolException("SDO segment out of sequence.");
            data.AddRange(s.Data);
            if (s.Last) break;
            toggle = !toggle;
        }

        if (r.SizeIndicated && data.Count != r.Size) throw new ProtocolException($"SDO upload announced {r.Size} bytes and delivered {data.Count}.");
        return data.ToArray();
    }, ct);

    /// <summary>Writes an object over SDO (expedited up to 4 bytes, segmented above); refused in read-only mode.</summary>
    public Task DownloadAsync(byte node, ushort index, byte subIndex, ReadOnlyMemory<byte> value, CancellationToken ct = default)
    {
        if (_options.ReadOnly) throw new ReadOnlyModeException();
        var data = value.ToArray();
        return TransactAsync(node, async replies =>
        {
            if (data.Length is >= 1 and <= 4)
            {
                var r = await ExchangeAsync(node, new SdoFrame { Kind = SdoKind.InitiateDownloadRequest, Index = index, SubIndex = subIndex, Expedited = true, SizeIndicated = true, Data = data }, replies, ct).ConfigureAwait(false);
                if (r.Kind == SdoKind.Abort) throw new CanOpenSdoException(node, index, subIndex, r.AbortCode);
                return true;
            }

            var init = await ExchangeAsync(node, new SdoFrame { Kind = SdoKind.InitiateDownloadRequest, Index = index, SubIndex = subIndex, SizeIndicated = true, Size = (uint)data.Length }, replies, ct).ConfigureAwait(false);
            if (init.Kind == SdoKind.Abort) throw new CanOpenSdoException(node, index, subIndex, init.AbortCode);
            var toggle = false;
            for (var pos = 0; pos < data.Length || data.Length == 0; pos += 7)
            {
                var chunk = data.AsSpan(pos, Math.Min(7, data.Length - pos)).ToArray();
                var last = pos + 7 >= data.Length;
                var s = await ExchangeAsync(node, new SdoFrame { Kind = SdoKind.DownloadSegmentRequest, Toggle = toggle, Last = last, Data = chunk, Index = index, SubIndex = subIndex }, replies, ct).ConfigureAwait(false);
                if (s.Kind == SdoKind.Abort) throw new CanOpenSdoException(node, index, subIndex, s.AbortCode);
                if (s.Kind != SdoKind.DownloadSegmentResponse || s.Toggle != toggle) throw new ProtocolException("SDO segment acknowledgement out of sequence.");
                toggle = !toggle;
                if (last) break;
            }

            return true;
        }, ct);
    }

    /// <summary>Reads and decodes a typed value.</summary>
    public async Task<object> ReadAsync(byte node, ushort index, byte subIndex, CanOpenDataType type, CancellationToken ct = default) =>
        CanOpenValue.Decode(type, await UploadAsync(node, index, subIndex, ct).ConfigureAwait(false));

    /// <summary>Encodes and writes a typed value; refused in read-only mode.</summary>
    public Task WriteAsync(byte node, ushort index, byte subIndex, CanOpenDataType type, object value, CancellationToken ct = default) =>
        DownloadAsync(node, index, subIndex, CanOpenValue.Encode(type, value), ct);

    /// <summary>Reads the mapping of TPDO <paramref name="number"/> of a node.</summary>
    public async Task<PdoMapping> ReadTpdoMappingAsync(byte node, int number, CancellationToken ct = default)
    {
        var map = (ushort)(0x1A00 + number - 1);
        var count = (await UploadAsync(node, map, 0, ct).ConfigureAwait(false))[0];
        var objects = new List<PdoMappedObject>();
        for (byte i = 1; i <= count; i++)
            objects.Add(PdoMapping.Parse(Convert.ToUInt32(CanOpenValue.Decode(CanOpenDataType.Unsigned32, await UploadAsync(node, map, i, ct).ConfigureAwait(false)), CultureInfo.InvariantCulture)));
        return new PdoMapping(objects);
    }

    /// <summary>
    /// Finds nodes in <paramref name="from"/>..<paramref name="to"/> by reading the device type (0x1000) with a short
    /// timeout, then their name and identity.
    /// </summary>
    public async Task<IReadOnlyList<CanOpenNodeInfo>> ScanAsync(byte from = 1, byte to = 127, TimeSpan? perNode = null, CancellationToken ct = default)
    {
        var saved = _options.SdoTimeout;
        var found = new List<CanOpenNodeInfo>();
        for (var id = from; id <= to && id != 0; id++)
        {
            _options.SdoTimeout = perNode ?? TimeSpan.FromMilliseconds(100);
            try
            {
                var type = Convert.ToUInt32(await ReadAsync(id, 0x1000, 0, CanOpenDataType.Unsigned32, ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
                _options.SdoTimeout = saved;
                var info = Node(id);
                info.DeviceType = type;
                try
                {
                    info.Name = (string)await ReadAsync(id, 0x1008, 0, CanOpenDataType.VisibleString, ct).ConfigureAwait(false);
                }
                catch (CanOpenSdoException)
                {
                }

                try
                {
                    async Task<uint> U(byte sub) => Convert.ToUInt32(await ReadAsync(id, 0x1018, sub, CanOpenDataType.Unsigned32, ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
                    info.Identity = (await U(1).ConfigureAwait(false), await U(2).ConfigureAwait(false), await U(3).ConfigureAwait(false), await U(4).ConfigureAwait(false));
                }
                catch (CanOpenSdoException)
                {
                }

                found.Add(info);
            }
            catch (Exception ex) when (ex is IoTComTimeoutException or CanOpenSdoException)
            {
            }
            finally
            {
                _options.SdoTimeout = saved;
            }

            if (id == 127) break;
        }

        return found;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_cts is null) return;
        _bus.FrameReceived -= OnFrame;
        await _cts.CancelAsync().ConfigureAwait(false);
        _cts.Dispose();
        _cts = null;
        foreach (var l in _sdoLocks.Values) l.Dispose();
    }
}

/// <summary>
/// A simulated CiA 401-style I/O module: 8 digital inputs (0x6000:01), 8 digital outputs (0x6200:01, writable by
/// RPDO1 or SDO), two analog inputs (0x6401:01–02) and a temperature (0x2000:00, 0.1 °C). TPDO1 carries inputs and
/// analog values (event-driven with an event timer), TPDO2 the temperature on every SYNC.
/// </summary>
public sealed class CanOpenIoModuleSimulator : IAsyncDisposable
{
    private readonly Random _random;
    private double _phase;

    private CanOpenIoModuleSimulator(CanOpenNode node, int seed)
    {
        Node = node;
        _random = new Random(seed);
    }

    /// <summary>The device.</summary>
    public CanOpenNode Node { get; }

    /// <summary>Creates the module (call <see cref="StartAsync"/>).</summary>
    public static CanOpenIoModuleSimulator Create(ICanBus bus, byte nodeId = 5, ushort heartbeatMs = 500, ushort eventTimerMs = 200, int seed = 3)
    {
        var od = ObjectDictionary.CreateStandard(0x000F_0191, "IoTCom IO-8", 0x0000_0ABC, 0x0401_0008, 0x0001_0002, 0x2026_0000u + nodeId, heartbeatMs);
        od.Add(0x2000, 0, "Temperature (0.1 °C)", CanOpenDataType.Integer16, CanOpenAccess.ReadOnly, (short)238, pdoMappable: true);
        od.Add(0x6000, 0, "Digital inputs: entries", CanOpenDataType.Unsigned8, CanOpenAccess.Const, (byte)1);
        od.Add(0x6000, 1, "Digital inputs 1–8", CanOpenDataType.Unsigned8, CanOpenAccess.ReadOnly, (byte)0b0000_0101, pdoMappable: true);
        od.Add(0x6200, 0, "Digital outputs: entries", CanOpenDataType.Unsigned8, CanOpenAccess.Const, (byte)1);
        od.Add(0x6200, 1, "Digital outputs 1–8", CanOpenDataType.Unsigned8, CanOpenAccess.ReadWrite, (byte)0, pdoMappable: true);
        od.Add(0x6401, 0, "Analog inputs: entries", CanOpenDataType.Unsigned8, CanOpenAccess.Const, (byte)2);
        od.Add(0x6401, 1, "Analog input 1 (pressure, mbar)", CanOpenDataType.Integer16, CanOpenAccess.ReadOnly, (short)1013, pdoMappable: true);
        od.Add(0x6401, 2, "Analog input 2 (flow, 0.1 l/min)", CanOpenDataType.Integer16, CanOpenAccess.ReadOnly, (short)0, pdoMappable: true);
        od.Add(0x2100, 0, "Location", CanOpenDataType.VisibleString, CanOpenAccess.ReadWrite, "Pump skid 2, Cikarang");
        od.DefinePdo(transmit: true, 1, CanOpenCodec.TpdoCobId(1, nodeId), [(0x6000, 1), (0x6401, 1), (0x6401, 2)], 0xFF, eventTimerMs);
        od.DefinePdo(transmit: true, 2, CanOpenCodec.TpdoCobId(2, nodeId), [(0x2000, 0)], transmissionType: 1);
        od.DefinePdo(transmit: false, 1, CanOpenCodec.RpdoCobId(1, nodeId), [(0x6200, 1)]);
        return new CanOpenIoModuleSimulator(CanOpenNode.Create(bus, nodeId, od), seed);
    }

    /// <summary>Boots the module.</summary>
    public Task StartAsync(CancellationToken ct = default) => Node.StartAsync(ct);

    /// <summary>
    /// Advances the process: output 1 runs the pump (flow and pressure follow it), input 1 mirrors it after a moment,
    /// input 3 toggles now and then, the temperature drifts.
    /// </summary>
    public void Step()
    {
        var od = Node.Dictionary;
        _phase += 0.2;
        var outputs = (byte)(ulong)od[0x6200, 1].Value;
        var pumpOn = (outputs & 1) != 0;
        var inputs = (byte)(ulong)od[0x6000, 1].Value;
        inputs = (byte)((inputs & ~1) | (pumpOn ? 1 : 0));
        if (_random.NextDouble() < 0.1) inputs ^= 0b100;
        od.Set(0x6000, 1, inputs);
        var flow = pumpOn ? 482 + (int)(Math.Sin(_phase) * 12) + _random.Next(-3, 4) : 0;
        od.Set(0x6401, 2, (short)flow);
        od.Set(0x6401, 1, (short)(1013 + (pumpOn ? 2200 + (int)(Math.Cos(_phase) * 40) : 0)));
        var temp = Convert.ToInt16(od[0x2000, 0].Value, CultureInfo.InvariantCulture);
        od.Set(0x2000, 0, (short)Math.Clamp(temp + _random.Next(-2, 3) + (pumpOn ? 1 : -1), 200, 650));
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => Node.DisposeAsync();
}
