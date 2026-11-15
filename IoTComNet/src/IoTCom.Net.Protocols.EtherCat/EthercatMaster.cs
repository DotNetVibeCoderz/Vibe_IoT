using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Protocols.EtherCat;

/// <summary>An EtherCAT error with the slave and AL status code or SDO abort code.</summary>
public sealed class EthercatException : IoTComException
{
    /// <summary>Creates an exception.</summary>
    public EthercatException(string message, EthercatSlave? slave = null, ushort alStatusCode = 0, uint abortCode = 0) : base(message)
        => (Slave, AlStatusCode, AbortCode) = (slave, alStatusCode, abortCode);

    /// <summary>Slave concerned.</summary>
    public EthercatSlave? Slave { get; }

    /// <summary>AL status code (state errors).</summary>
    public ushort AlStatusCode { get; }

    /// <summary>SDO abort code (mailbox errors).</summary>
    public uint AbortCode { get; }
}

/// <summary>A slave found by <see cref="EthercatMaster.ScanAsync"/>.</summary>
public sealed class EthercatSlave
{
    internal EthercatSlave(int position, ushort station, EcatSii.Info info) => (Position, Station, Info) = (position, station, info);

    /// <summary>Position on the line (0 = first after the master).</summary>
    public int Position { get; }

    /// <summary>Configured station address (0x1001 + position).</summary>
    public ushort Station { get; }

    internal EcatSii.Info Info { get; }

    /// <summary>Vendor ID.</summary>
    public uint Vendor => Info.Vendor;

    /// <summary>Product code.</summary>
    public uint Product => Info.Product;

    /// <summary>Revision.</summary>
    public uint Revision => Info.Revision;

    /// <summary>Serial number.</summary>
    public uint Serial => Info.Serial;

    /// <summary>Name from the SII.</summary>
    public string Name => Info.Name;

    /// <summary>True when the slave supports CoE.</summary>
    public bool HasCoe => Info.Mailbox.RxSize > 0 && (Info.Mailbox.Protocols & 0x0004) != 0;

    /// <summary>Output process data bytes.</summary>
    public int OutputBytes => (Info.OutputBits + 7) / 8;

    /// <summary>Input process data bytes.</summary>
    public int InputBytes => (Info.InputBits + 7) / 8;

    /// <summary>Offset of the outputs in the master's process image.</summary>
    public int OutputOffset { get; internal set; }

    /// <summary>Offset of the inputs in the master's process image.</summary>
    public int InputOffset { get; internal set; }

    /// <summary>Last AL state read.</summary>
    public EcatState State { get; internal set; }

    /// <summary>Last AL status code (0 = no error).</summary>
    public ushort StatusCode { get; internal set; }

    internal byte MailboxCounter { get; set; }

    /// <inheritdoc />
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"#{Position} 0x{Station:X4} {Name} (vendor 0x{Vendor:X8}, product 0x{Product:X8}) {EcatAlStatus.Name(State)} in {InputBytes} B / out {OutputBytes} B");
}

/// <summary>Statistics of the cyclic exchange.</summary>
/// <param name="Cycles">Exchanges done.</param>
/// <param name="WorkingCounterErrors">Exchanges whose working counter was not the expected one.</param>
/// <param name="LostFrames">Exchanges whose frame did not come back.</param>
/// <param name="LastWorkingCounter">Last working counter.</param>
/// <param name="ExpectedWorkingCounter">Expected working counter.</param>
/// <param name="LastRoundTrip">Last round-trip time.</param>
/// <param name="MaxRoundTrip">Largest round-trip time.</param>
public readonly record struct EthercatCycleStats(long Cycles, long WorkingCounterErrors, long LostFrames, int LastWorkingCounter, int ExpectedWorkingCounter,
    TimeSpan LastRoundTrip, TimeSpan MaxRoundTrip);

/// <summary>Options for <see cref="EthercatMaster"/>.</summary>
public sealed class EthercatMasterOptions
{
    internal Func<IEthercatLink>? LinkFactory { get; private set; }

    internal bool OwnsLink { get; private set; }

    /// <summary>Endpoint name (hosting, logs).</summary>
    public string? Name { get; set; }

    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }

    /// <summary>Uses a link (a <see cref="VirtualEthercatSegment"/> or a raw interface). The caller keeps ownership: the master does not dispose it.</summary>
    public EthercatMasterOptions UseLink(IEthercatLink link)
    {
        LinkFactory = () => link;
        OwnsLink = false;
        return this;
    }

    /// <summary>Uses a raw Ethernet interface (AF_PACKET on Linux, Npcap/libpcap elsewhere; needs CAP_NET_RAW or administrator rights).</summary>
    public EthercatMasterOptions UseInterface(string name)
    {
        LinkFactory = () => RawEthernetLink.Open(name);
        OwnsLink = true;
        return this;
    }

    /// <summary>How long to wait for a frame to come back (default 50 ms).</summary>
    public TimeSpan FrameTimeout { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>Retries of a lost acyclic frame (default 3).</summary>
    public int Retries { get; set; } = 3;

    /// <summary>How long a state change may take (default 3 s).</summary>
    public TimeSpan StateTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Read-only (default): no outputs, no OP, no SDO or register writes.</summary>
    public bool ReadOnly { get; set; } = true;

    /// <summary>Allows outputs, OP and writes.</summary>
    public EthercatMasterOptions AllowWrites()
    {
        ReadOnly = false;
        return this;
    }
}

/// <summary>
/// An EtherCAT master: scans the line (station addresses, SII identity, names and process data sizes), walks the
/// state machine, maps a process image with FMMUs and SyncManagers, exchanges it cyclically with LRW, and reads and
/// writes CoE objects through the mailbox. <b>Read-only by default</b>: without <see cref="EthercatMasterOptions.AllowWrites"/>
/// the line stops at SAFE-OP and only inputs are read. Cycle timing runs on the .NET scheduler: it is not hard
/// real-time (expect jitter in the 0.1–1 ms range on a desktop OS).
/// </summary>
public sealed class EthercatMaster : EndpointBase, IClientEndpoint
{
    private readonly EthercatMasterOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<EthercatSlave> _slaves = [];
    private readonly Channel<byte[]> _frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });
    private IEthercatLink? _link;
    private CancellationTokenSource? _cts;
    private Task? _pump;
    private byte _index;
    private byte[] _image = [];
    private int _outputLength, _expectedWkc;
    private long _cycles, _wkcErrors, _lost;
    private int _lastWkc;
    private TimeSpan _lastRtt, _maxRtt;

    private EthercatMaster(EthercatMasterOptions options, ILogger? logger) : base("ethercat", logger) => _options = options;

    /// <summary>Creates a master.</summary>
    public static EthercatMaster Create(Action<EthercatMasterOptions> configure, ILogger? logger = null)
    {
        var options = new EthercatMasterOptions();
        configure(options);
        if (options.LinkFactory is null) throw new ArgumentException("Configure UseLink(...) or UseInterface(...).", nameof(configure));
        return new EthercatMaster(options, options.Logger ?? logger) { Name = options.Name };
    }

    /// <summary>Slaves of the last scan.</summary>
    public IReadOnlyList<EthercatSlave> Slaves => _slaves;

    /// <summary>True when outputs and OP are allowed.</summary>
    public bool WritesAllowed => !_options.ReadOnly;

    /// <summary>The link name.</summary>
    public string? Interface => _link?.Name;

    /// <summary>Cyclic statistics.</summary>
    public EthercatCycleStats Statistics => new(Interlocked.Read(ref _cycles), Interlocked.Read(ref _wkcErrors), Interlocked.Read(ref _lost), _lastWkc, ExpectedWorkingCounter, _lastRtt, _maxRtt);

    // LRW counts 2 per output slave and 1 per input slave; a read-only master uses LRD over the inputs (1 per input slave).
    private int ExpectedWorkingCounter => _options.ReadOnly ? _slaves.Count(s => s.InputBytes > 0) : _expectedWkc;

    /// <summary>Raised after every cyclic exchange.</summary>
    public event Action<EthercatCycleStats>? Cycle;

    /// <summary>Opens the link.</summary>
    public ValueTask ConnectAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_link is not null) return ValueTask.CompletedTask;
        SetState(EndpointState.Connecting);
        try
        {
            _link = _options.LinkFactory!();
            _cts = new CancellationTokenSource();
            var pumpToken = _cts.Token;   // read now: a concurrent teardown may null _cts before the task runs
            _pump = Task.Run(() => PumpAsync(pumpToken), CancellationToken.None);
            SetState(EndpointState.Connected);
        }
        catch (Exception ex)
        {
            SetState(EndpointState.Faulted, ex);
            throw;
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Brings the line back to PRE-OP (outputs safe) and closes the link; <see cref="ConnectAsync"/> opens it again.</summary>
    public async ValueTask DisconnectAsync(CancellationToken ct = default)
    {
        if (_link is null) return;
        SetState(EndpointState.Stopping);
        if (_slaves.Any(s => s.State > EcatState.PreOp))
        {
            try
            {
                using var bye = CancellationTokenSource.CreateLinkedTokenSource(ct);
                bye.CancelAfter(TimeSpan.FromMilliseconds(500));
                foreach (var s in _slaves) await WriteAsync(s.Station, EcatRegister.AlControl, [(byte)EcatState.PreOp, 0], bye.Token).ConfigureAwait(false);
                foreach (var s in _slaves) s.State = EcatState.PreOp;
            }
            catch (Exception ex) when (ex is IoTComException or OperationCanceledException)
            {
                Logger.LogWarning("EtherCAT line did not confirm PRE-OP on disconnect: {Message}", ex.Message);
            }
        }

        if (_cts is not null) await _cts.CancelAsync().ConfigureAwait(false);
        if (_options.OwnsLink) await _link.DisposeAsync().ConfigureAwait(false);
        if (_pump is not null) await _pump.ConfigureAwait(false);
        _cts?.Dispose();
        (_link, _cts, _pump) = (null, null, null);
        (_image, _outputLength, _expectedWkc) = ([], 0, 0);
        SetState(EndpointState.Disconnected);
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await _link!.ReceiveAsync(ct).ConfigureAwait(false);
                if (EcatFrame.IsReturned(frame)) _frames.Writer.TryWrite(frame);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ChannelClosedException or ObjectDisposedException)
        {
        }
    }

    // ---- frames

    private async Task<IReadOnlyList<EcatDatagram>> TransactAsync(IReadOnlyList<EcatDatagram> datagrams, int retries, CancellationToken ct)
    {
        if (_link is null) await ConnectAsync(ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; attempt <= retries; attempt++)
            {
                var index = unchecked(++_index);
                var sent = datagrams.Select((d, i) => new EcatDatagram { Command = d.Command, Index = (byte)(index + i), Address = d.Address, Data = (byte[])d.Data.Clone() }).ToList();
                var frame = EcatFrame.Encode(sent);
                while (_frames.Reader.TryRead(out _))
                {
                }

                Tap(FrameDirection.Outbound, frame, () => Summary(sent));
                await _link!.SendAsync(frame, ct).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(_options.FrameTimeout);
                try
                {
                    while (true)
                    {
                        var back = await _frames.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
                        if (!EcatFrame.TryDecode(back, out var returned, out _) || returned.Count != sent.Count || returned[0].Index != index) continue;
                        Tap(FrameDirection.Inbound, back, () => Summary(returned));
                        return returned;
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    Logger.LogDebug("EtherCAT frame {Index} did not come back (attempt {Attempt})", index, attempt + 1);
                }
            }

            throw new IoTComTimeoutException($"No EtherCAT frame came back on {_link!.Name} after {retries + 1} attempts: check the cable to the first slave and that the interface is up.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string Summary(IReadOnlyList<EcatDatagram> d) => d.Count == 1 ? d[0].ToString() : $"{d.Count} datagrams: {d[0]} …";

    private async Task<EcatDatagram> OneAsync(EcatDatagram d, CancellationToken ct) => (await TransactAsync([d], _options.Retries, ct).ConfigureAwait(false))[0];

    private async Task<byte[]> ReadAsync(ushort station, ushort register, int length, CancellationToken ct)
    {
        var r = await OneAsync(EcatDatagram.Configured(EcatCommand.Fprd, station, register, new byte[length]), ct).ConfigureAwait(false);
        if (r.WorkingCounter != 1) throw new EthercatException($"Slave 0x{station:X4} did not answer a read of 0x{register:X4} (working counter {r.WorkingCounter}).", Find(station));
        return r.Data;
    }

    private async Task WriteAsync(ushort station, ushort register, byte[] data, CancellationToken ct)
    {
        var r = await OneAsync(EcatDatagram.Configured(EcatCommand.Fpwr, station, register, data), ct).ConfigureAwait(false);
        if (r.WorkingCounter != 1) throw new EthercatException($"Slave 0x{station:X4} did not accept a write to 0x{register:X4} (working counter {r.WorkingCounter}).", Find(station));
    }

    private EthercatSlave? Find(ushort station) => _slaves.FirstOrDefault(s => s.Station == station);

    // ---- scan

    /// <summary>Counts the slaves, gives them station addresses 0x1001…, reads their SII and resets them to INIT.</summary>
    public async Task<IReadOnlyList<EthercatSlave>> ScanAsync(CancellationToken ct = default)
    {
        var count = (await OneAsync(EcatDatagram.Broadcast(EcatCommand.Brd, EcatRegister.Type, new byte[1]), ct).ConfigureAwait(false)).WorkingCounter;
        _slaves.Clear();
        (_image, _outputLength, _expectedWkc) = ([], 0, 0);
        if (count == 0) return _slaves;
        await OneAsync(EcatDatagram.Broadcast(EcatCommand.Bwr, EcatRegister.AlControl, [0x11, 0]), ct).ConfigureAwait(false);   // INIT + acknowledge
        await OneAsync(EcatDatagram.Broadcast(EcatCommand.Bwr, EcatRegister.Fmmu0, new byte[256]), ct).ConfigureAwait(false);
        await OneAsync(EcatDatagram.Broadcast(EcatCommand.Bwr, EcatRegister.Sm0, new byte[128]), ct).ConfigureAwait(false);
        for (var i = 0; i < count; i++)
        {
            var station = (ushort)(0x1001 + i);
            var w = await OneAsync(EcatDatagram.Positional(EcatCommand.Apwr, i, EcatRegister.StationAddress, BitConverter.GetBytes(station)), ct).ConfigureAwait(false);
            if (w.WorkingCounter != 1) throw new EthercatException($"Slave at position {i} did not take station address 0x{station:X4}.");
        }

        for (var i = 0; i < count; i++)
        {
            var station = (ushort)(0x1001 + i);
            var sii = await ReadSiiAsync(station, ct).ConfigureAwait(false);
            var slave = new EthercatSlave(i, station, EcatSii.Parse(sii));
            _slaves.Add(slave);
        }

        await RefreshStatesAsync(ct).ConfigureAwait(false);
        return _slaves;
    }

    private async Task<byte[]> ReadSiiAsync(ushort station, CancellationToken ct)
    {
        var image = new List<byte>(await ReadSiiWordsAsync(station, 0, EcatSii.FirstCategory, ct).ConfigureAwait(false));
        var word = EcatSii.FirstCategory;
        for (var guard = 0; guard < 64; guard++)
        {
            var header = await ReadSiiWordsAsync(station, word, 2, ct).ConfigureAwait(false);
            image.AddRange(header);
            var type = BinaryPrimitives.ReadUInt16LittleEndian(header);
            if (type == EcatSii.End) break;
            var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2));
            image.AddRange(await ReadSiiWordsAsync(station, word + 2, length, ct).ConfigureAwait(false));
            word += 2 + length;
        }

        return [.. image];
    }

    private async Task<byte[]> ReadSiiWordsAsync(ushort station, int start, int words, CancellationToken ct)
    {
        var result = new byte[words * 2];
        for (var w = 0; w < words; w += 2)
        {
            var command = new byte[6];
            command[1] = 0x01;   // read
            BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(2), (uint)(start + w));
            await WriteAsync(station, EcatRegister.SiiControl, command, ct).ConfigureAwait(false);
            byte[] status;
            var sw = Stopwatch.StartNew();
            do
            {
                status = await ReadAsync(station, EcatRegister.SiiControl, 10, ct).ConfigureAwait(false);
                if ((status[1] & 0x80) == 0) break;
            }
            while (sw.Elapsed < TimeSpan.FromMilliseconds(100));

            if ((status[1] & 0x20) != 0) throw new EthercatException($"Slave 0x{station:X4} reported an SII read error at word 0x{start + w:X4}.", Find(station));
            status.AsSpan(6, Math.Min(4, result.Length - (w * 2))).CopyTo(result.AsSpan(w * 2));
        }

        return result;
    }

    /// <summary>Reads every slave's AL state and status code.</summary>
    public async Task RefreshStatesAsync(CancellationToken ct = default)
    {
        foreach (var s in _slaves)
        {
            var b = await ReadAsync(s.Station, EcatRegister.AlStatus, 6, ct).ConfigureAwait(false);
            s.State = (EcatState)(b[0] & 0x0F);
            s.StatusCode = (b[0] & 0x10) != 0 ? BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(4)) : (ushort)0;
        }
    }

    // ---- state machine

    /// <summary>
    /// Brings every slave to <paramref name="target"/>, configuring mailboxes on the way to PRE-OP and the process image on
    /// the way to SAFE-OP. OP needs <see cref="EthercatMasterOptions.AllowWrites"/>.
    /// </summary>
    /// <exception cref="EthercatException">A slave refused the state (the AL status code says why).</exception>
    public async Task RequestStateAsync(EcatState target, CancellationToken ct = default)
    {
        if (_slaves.Count == 0) await ScanAsync(ct).ConfigureAwait(false);
        if (target == EcatState.Op && _options.ReadOnly) throw new ReadOnlyModeException("OP drives the outputs; this master is read-only until AllowWrites() (SAFE-OP reads inputs).");
        if (target == EcatState.Boot) throw new NotSupportedException("BOOT (firmware update over FoE) is not supported.");
        await RefreshStatesAsync(ct).ConfigureAwait(false);
        var current = _slaves.Min(s => s.State);
        if (target < current)
        {
            await StepAllAsync(target, ct).ConfigureAwait(false);
            return;
        }

        if (current < EcatState.PreOp && target >= EcatState.PreOp)
        {
            await ConfigureMailboxesAsync(ct).ConfigureAwait(false);
            await StepAllAsync(EcatState.PreOp, ct).ConfigureAwait(false);
        }

        if (current < EcatState.SafeOp && target >= EcatState.SafeOp)
        {
            await ConfigureProcessDataAsync(ct).ConfigureAwait(false);
            await StepAllAsync(EcatState.SafeOp, ct).ConfigureAwait(false);
        }

        if (target == EcatState.Op && current < EcatState.Op)
        {
            await ExchangeAsync(ct).ConfigureAwait(false);   // valid outputs before OP, as the slaves' watchdogs expect
            await StepAllAsync(EcatState.Op, ct).ConfigureAwait(false);
        }

        if (target == EcatState.Init) await StepAllAsync(EcatState.Init, ct).ConfigureAwait(false);
    }

    /// <summary>Acknowledges an AL error on one slave and requests <paramref name="state"/> again.</summary>
    public async Task AcknowledgeErrorAsync(EthercatSlave slave, EcatState state, CancellationToken ct = default)
    {
        if (state == EcatState.Op && _options.ReadOnly) throw new ReadOnlyModeException("OP drives the outputs; this master is read-only until AllowWrites().");
        await WriteAsync(slave.Station, EcatRegister.AlControl, [(byte)((byte)state | 0x10), 0], ct).ConfigureAwait(false);
        await WaitStateAsync(slave, state, ct).ConfigureAwait(false);
    }

    private async Task StepAllAsync(EcatState state, CancellationToken ct)
    {
        foreach (var s in _slaves) await WriteAsync(s.Station, EcatRegister.AlControl, [(byte)state, 0], ct).ConfigureAwait(false);
        foreach (var s in _slaves) await WaitStateAsync(s, state, ct).ConfigureAwait(false);
    }

    private async Task WaitStateAsync(EthercatSlave s, EcatState state, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var b = await ReadAsync(s.Station, EcatRegister.AlStatus, 6, ct).ConfigureAwait(false);
            s.State = (EcatState)(b[0] & 0x0F);
            s.StatusCode = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(4));
            if ((b[0] & 0x10) != 0)
                throw new EthercatException($"{s.Name} (#{s.Position}) refused {EcatAlStatus.Name(state)}: {EcatAlStatus.Describe(s.StatusCode)} (AL status code 0x{s.StatusCode:X4}); it stays in {EcatAlStatus.Name(s.State)}.", s, s.StatusCode);
            if (s.State == state)
            {
                s.StatusCode = 0;
                return;
            }

            if (sw.Elapsed > _options.StateTimeout) throw new IoTComTimeoutException($"{s.Name} (#{s.Position}) did not reach {EcatAlStatus.Name(state)} within {_options.StateTimeout.TotalSeconds:0.#} s; it is in {EcatAlStatus.Name(s.State)}.");
            await Task.Delay(1, ct).ConfigureAwait(false);
        }
    }

    private async Task ConfigureMailboxesAsync(CancellationToken ct)
    {
        foreach (var s in _slaves.Where(s => s.Info.Mailbox.RxSize > 0))
        {
            var sms = s.Info.SyncManagers;
            for (var n = 0; n < sms.Count && n < 2; n++) await WriteSmAsync(s, n, sms[n].Start, sms[n].Length, sms[n].Control, ct).ConfigureAwait(false);
            s.MailboxCounter = 0;
        }
    }

    private async Task ConfigureProcessDataAsync(CancellationToken ct)
    {
        var offset = 0;
        foreach (var s in _slaves)
        {
            s.OutputOffset = offset;
            offset += s.OutputBytes;
        }

        _outputLength = offset;
        foreach (var s in _slaves)
        {
            s.InputOffset = offset;
            offset += s.InputBytes;
        }

        if (offset > EcatFrame.MaxPayload - EcatDatagram.Overhead)
            throw new NotSupportedException($"The process image needs {offset} bytes; this master exchanges at most {EcatFrame.MaxPayload - EcatDatagram.Overhead} bytes in one frame.");
        _image = new byte[offset];
        _expectedWkc = 0;
        foreach (var s in _slaves)
        {
            var sms = s.Info.SyncManagers;
            var fmmu = 0;
            for (var n = 0; n < sms.Count; n++)
            {
                var (start, length, control, type) = sms[n];
                if (type is not (3 or 4) || length == 0) continue;
                await WriteSmAsync(s, n, start, length, control, ct).ConfigureAwait(false);
                var logical = (uint)(type == 3 ? s.OutputOffset : s.InputOffset);
                var f = new byte[16];
                BinaryPrimitives.WriteUInt32LittleEndian(f, logical);
                BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(4), length);
                f[7] = 7;   // logical stop bit
                BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(8), start);
                f[11] = (byte)(type == 3 ? 2 : 1);
                f[12] = 1;
                await WriteAsync(s.Station, (ushort)(EcatRegister.Fmmu0 + (fmmu++ * 16)), f, ct).ConfigureAwait(false);
            }

            if (s.OutputBytes > 0) _expectedWkc += 2;
            if (s.InputBytes > 0) _expectedWkc += 1;
        }
    }

    private Task WriteSmAsync(EthercatSlave s, int n, ushort start, ushort length, byte control, CancellationToken ct)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(b, start);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), length);
        b[4] = control;
        b[6] = 1;
        return WriteAsync(s.Station, (ushort)(EcatRegister.Sm0 + (n * 8)), b, ct);
    }

    // ---- process data

    /// <summary>The process image (outputs, then inputs).</summary>
    public ReadOnlySpan<byte> Image => _image;

    /// <summary>Inputs of one slave from the last exchange.</summary>
    public ReadOnlySpan<byte> InputsOf(EthercatSlave slave) => _image.AsSpan(slave.InputOffset, slave.InputBytes);

    /// <summary>Outputs of one slave as they will be sent.</summary>
    public ReadOnlySpan<byte> OutputsOf(EthercatSlave slave) => _image.AsSpan(slave.OutputOffset, slave.OutputBytes);

    /// <summary>Sets a slave's outputs for the next exchange.</summary>
    public void SetOutputs(EthercatSlave slave, ReadOnlySpan<byte> data)
    {
        if (_options.ReadOnly) throw new ReadOnlyModeException("Outputs drive machines; this master is read-only until AllowWrites().");
        if (data.Length != slave.OutputBytes) throw new ArgumentException($"{slave.Name} has {slave.OutputBytes} output bytes.", nameof(data));
        data.CopyTo(_image.AsSpan(slave.OutputOffset));
    }

    /// <summary>Sets one output bit.</summary>
    public void SetOutputBit(EthercatSlave slave, int bit, bool value)
    {
        if (_options.ReadOnly) throw new ReadOnlyModeException("Outputs drive machines; this master is read-only until AllowWrites().");
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(bit, slave.OutputBytes * 8);
        ref var b = ref _image[slave.OutputOffset + (bit / 8)];
        b = value ? (byte)(b | (1 << (bit % 8))) : (byte)(b & ~(1 << (bit % 8)));
    }

    /// <summary>
    /// One process-data exchange: LRW over the image (LRD of the inputs when read-only). Returns the working counter;
    /// a mismatch with the expected value is counted in <see cref="Statistics"/>, not thrown.
    /// </summary>
    public async Task<int> ExchangeAsync(CancellationToken ct = default)
    {
        if (_image.Length == 0) throw new InvalidOperationException("No process image: request SAFE-OP first.");
        var readOnly = _options.ReadOnly;
        var data = readOnly ? new byte[_image.Length - _outputLength] : (byte[])_image.Clone();
        var datagram = readOnly
            ? EcatDatagram.Logical(EcatCommand.Lrd, (uint)_outputLength, data)
            : EcatDatagram.Logical(EcatCommand.Lrw, 0, data);
        var expected = ExpectedWorkingCounter;
        var sw = Stopwatch.StartNew();
        EcatDatagram back;
        try
        {
            back = (await TransactAsync([datagram], 0, ct).ConfigureAwait(false))[0];
        }
        catch (IoTComTimeoutException)
        {
            Interlocked.Increment(ref _lost);
            Interlocked.Increment(ref _cycles);
            _lastWkc = 0;
            Cycle?.Invoke(Statistics);
            return 0;
        }

        _lastRtt = sw.Elapsed;
        if (_lastRtt > _maxRtt) _maxRtt = _lastRtt;
        _lastWkc = back.WorkingCounter;
        if (back.WorkingCounter != expected) Interlocked.Increment(ref _wkcErrors);
        else back.Data.AsSpan(readOnly ? 0 : _outputLength, _image.Length - _outputLength).CopyTo(_image.AsSpan(_outputLength));
        Interlocked.Increment(ref _cycles);
        Cycle?.Invoke(Statistics);
        return back.WorkingCounter;
    }

    /// <summary>Exchanges the image every <paramref name="period"/> until cancelled (best effort, not hard real-time).</summary>
    public async Task RunAsync(TimeSpan period, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(period);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) await ExchangeAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    // ---- registers and CoE

    /// <summary>Reads ESC registers of a slave.</summary>
    public Task<byte[]> ReadRegisterAsync(EthercatSlave slave, ushort register, int length, CancellationToken ct = default) => ReadAsync(slave.Station, register, length, ct);

    /// <summary>Reads a CoE object (SDO upload).</summary>
    public async Task<byte[]> SdoReadAsync(EthercatSlave slave, ushort index, byte subIndex, CancellationToken ct = default)
    {
        var reply = await MailboxAsync(slave, CoeSdo.UploadRequest(index, subIndex), index, subIndex, ct).ConfigureAwait(false);
        return reply.Data;
    }

    /// <summary>Writes a CoE object (SDO download). Needs <see cref="EthercatMasterOptions.AllowWrites"/>.</summary>
    public async Task SdoWriteAsync(EthercatSlave slave, ushort index, byte subIndex, byte[] value, CancellationToken ct = default)
    {
        if (_options.ReadOnly) throw new ReadOnlyModeException($"Writing 0x{index:X4}:{subIndex:X2} changes the slave; this master is read-only until AllowWrites().");
        await MailboxAsync(slave, CoeSdo.DownloadRequest(index, subIndex, value), index, subIndex, ct).ConfigureAwait(false);
    }

    private async Task<CoeSdo.Message> MailboxAsync(EthercatSlave slave, byte[] coe, ushort index, byte subIndex, CancellationToken ct)
    {
        if (!slave.HasCoe) throw new EthercatException($"{slave.Name} (#{slave.Position}) has no CoE mailbox.", slave);
        if (slave.State < EcatState.PreOp) throw new EthercatException($"{slave.Name} is in {EcatAlStatus.Name(slave.State)}; the mailbox works from PRE-OP.", slave);
        var (rxOffset, rxSize, txOffset, txSize, _) = slave.Info.Mailbox;
        slave.MailboxCounter = (byte)((slave.MailboxCounter % 7) + 1);
        await WriteAsync(slave.Station, rxOffset, new EcatMailbox(EcatMailboxType.CoE, slave.MailboxCounter, coe).Encode(rxSize), ct).ConfigureAwait(false);
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var status = await ReadAsync(slave.Station, (ushort)(EcatRegister.Sm0 + 8 + 5), 1, ct).ConfigureAwait(false);
            if ((status[0] & 0x08) != 0) break;
            if (sw.Elapsed > TimeSpan.FromSeconds(1)) throw new IoTComTimeoutException($"{slave.Name} did not answer the CoE request for 0x{index:X4}:{subIndex:X2}.");
            await Task.Delay(1, ct).ConfigureAwait(false);
        }

        var raw = await ReadAsync(slave.Station, txOffset, txSize, ct).ConfigureAwait(false);
        if (!EcatMailbox.TryDecode(raw, out var mbx, out var error) || mbx is null) throw new ProtocolException($"Bad mailbox from {slave.Name}: {error}");
        if (mbx.Type == EcatMailboxType.Error) throw new EthercatException($"{slave.Name} answered with a mailbox error (code 0x{(mbx.Payload.Length >= 4 ? BinaryPrimitives.ReadUInt16LittleEndian(mbx.Payload.AsSpan(2)) : 0):X4}).", slave);
        if (!CoeSdo.TryDecode(mbx.Payload, out var sdo, out error) || sdo is null) throw new ProtocolException($"Bad CoE reply from {slave.Name}: {error}");
        if (sdo.IsAbort) throw new EthercatException($"{slave.Name} refused 0x{index:X4}:{subIndex:X2}: {CoeSdo.AbortText(sdo.AbortCode)}.", slave, abortCode: sdo.AbortCode);
        return sdo;
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
