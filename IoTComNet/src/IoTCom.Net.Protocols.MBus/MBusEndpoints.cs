using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Protocols.MBus;

/// <summary>Builds the user data of a variable data response: the 12-byte header and records.</summary>
public sealed class MBusRecordWriter
{
    private readonly List<byte> _data = [];

    /// <summary>Starts a response with the long header.</summary>
    public MBusRecordWriter(uint id, string manufacturer, byte version, byte medium, byte accessNumber, byte status = 0)
    {
        var header = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(header, id);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), MBusTelegram.ManufacturerId(manufacturer));
        header[6] = version;
        header[7] = medium;
        header[8] = accessNumber;
        header[9] = status;
        _data.AddRange(header);
    }

    /// <summary>Adds an integer record (data field 1–4, 6, 7 by size).</summary>
    public MBusRecordWriter Integer(byte vif, long value, int size = 4, int storage = 0, int tariff = 0, MBusFunction function = MBusFunction.Instantaneous, params byte[] vife)
    {
        var field = size switch { 1 => 1, 2 => 2, 3 => 3, 4 => 4, 6 => 6, 8 => 7, _ => throw new ArgumentOutOfRangeException(nameof(size)) };
        Header(field, storage, tariff, function, vif, vife);
        for (var i = 0; i < size; i++) _data.Add((byte)(value >> (8 * i)));
        return this;
    }

    /// <summary>Adds a BCD record (2, 4, 6, 8 or 12 digits).</summary>
    public MBusRecordWriter Bcd(byte vif, long value, int digits = 8, int storage = 0, int tariff = 0)
    {
        var field = digits switch { 2 => 9, 4 => 0xA, 6 => 0xB, 8 => 0xC, 12 => 0xE, _ => throw new ArgumentOutOfRangeException(nameof(digits)) };
        Header(field, storage, tariff, MBusFunction.Instantaneous, vif, []);
        var v = Math.Abs(value);
        for (var i = 0; i < digits / 2; i++)
        {
            _data.Add((byte)((((v / 10) % 10) << 4) | (v % 10)));
            v /= 100;
        }

        return this;
    }

    /// <summary>Adds a type G date (VIF 0x6C).</summary>
    public MBusRecordWriter Date(DateTime date, int storage = 0)
    {
        Header(2, storage, 0, MBusFunction.Instantaneous, 0x6C, []);
        _data.AddRange(MBusTelegram.EncodeTypeG(date));
        return this;
    }

    /// <summary>Adds a type F date-time (VIF 0x6D).</summary>
    public MBusRecordWriter DateTime(DateTime time, int storage = 0)
    {
        Header(4, storage, 0, MBusFunction.Instantaneous, 0x6D, []);
        _data.AddRange(MBusTelegram.EncodeTypeF(time));
        return this;
    }

    private void Header(int field, int storage, int tariff, MBusFunction function, byte vif, byte[] vife)
    {
        var dif = (byte)(field | ((int)function << 4) | ((storage & 1) << 6));
        var dife = new List<byte>();
        var restStorage = storage >> 1;
        var restTariff = tariff;
        while (restStorage > 0 || restTariff > 0)
        {
            dife.Add((byte)((restStorage & 0x0F) | ((restTariff & 3) << 4)));
            restStorage >>= 4;
            restTariff >>= 2;
        }

        _data.Add((byte)(dif | (dife.Count > 0 ? 0x80 : 0)));
        for (var i = 0; i < dife.Count; i++) _data.Add((byte)(dife[i] | (i < dife.Count - 1 ? 0x80 : 0)));
        _data.Add((byte)(vif | (vife.Length > 0 ? 0x80 : 0)));
        for (var i = 0; i < vife.Length; i++) _data.Add((byte)(vife[i] | (i < vife.Length - 1 ? 0x80 : 0)));
    }

    /// <summary>The user data (after CI).</summary>
    public byte[] ToArray() => [.. _data];
}

/// <summary>M-Bus master options.</summary>
public sealed class MBusMasterOptions : ITransportBuilder<MBusMasterOptions>
{
    /// <summary>Transport (serial level converter at 2400 8E1, or a TCP gateway).</summary>
    public TransportFactory? TransportFactory { get; set; }

    /// <summary>Time to wait for a reply.</summary>
    public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromMilliseconds(600);

    /// <summary>Retries after a timeout or a corrupted reply.</summary>
    public int Retries { get; set; } = 2;

    /// <summary>Friendly name.</summary>
    public string? Name { get; set; }

    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }

    /// <inheritdoc />
    public MBusMasterOptions UseTransport(TransportFactory factory)
    {
        TransportFactory = factory;
        return this;
    }
}

/// <summary>
/// An M-Bus master (EN 13757-2/-3): pings, reads (REQ_UD2 with the frame count bit), scans primary addresses and
/// selects slaves by secondary address. Reading never changes a meter; only selection uses SND_UD.
/// </summary>
/// <example>
/// <code>
/// await using var bus = MBusMaster.Create(o => o.UseSerial("COM4", 2400, Parity.Even));
/// await bus.ConnectAsync();
/// foreach (var address in await bus.ScanAsync())
///     Console.WriteLine(await bus.ReadAsync(address));
/// </code>
/// </example>
public sealed class MBusMaster : EndpointBase, IClientEndpoint
{
    private readonly MBusMasterOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<byte, bool> _fcb = new();
    private ITransport? _transport;
    private byte[] _pending = [];

    private MBusMaster(MBusMasterOptions options) : base("mbus", options.Logger)
    {
        _options = options;
        Name = options.Name;
    }

    /// <summary>Creates a master.</summary>
    public static MBusMaster Create(Action<MBusMasterOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var o = new MBusMasterOptions();
        configure(o);
        if (o.TransportFactory is null) throw new ArgumentException("A transport is required (UseSerial, UseTcp, UseInMemory).", nameof(configure));
        return new MBusMaster(o);
    }

    /// <inheritdoc />
    public async ValueTask ConnectAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_transport is not null) return;
        SetState(EndpointState.Connecting);
        var t = _options.TransportFactory!();
        try
        {
            await t.OpenAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await t.DisposeAsync().ConfigureAwait(false);
            SetState(EndpointState.Disconnected, ex);
            throw;
        }

        _transport = t;
        SetState(EndpointState.Connected);
    }

    /// <inheritdoc />
    public async ValueTask DisconnectAsync(CancellationToken ct = default)
    {
        var t = Interlocked.Exchange(ref _transport, null);
        if (t is null) return;
        await t.DisposeAsync().ConfigureAwait(false);
        SetState(EndpointState.Disconnected);
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    /// <summary>SND_NKE: true when the slave at <paramref name="address"/> acknowledges.</summary>
    public async Task<bool> PingAsync(byte address, CancellationToken ct = default)
    {
        _fcb[address] = true; // after SND_NKE the next REQ_UD2 carries FCB = 1
        try
        {
            return (await RequestAsync(MBusFrame.Short(MBusControl.SndNke, address), ct, retries: 0).ConfigureAwait(false))?.Type == MBusFrameType.Ack;
        }
        catch (IoTComTimeoutException)
        {
            return false;
        }
    }

    /// <summary>REQ_UD2: reads the slave's data.</summary>
    public async Task<MBusTelegram> ReadAsync(byte address, CancellationToken ct = default)
    {
        var fcb = _fcb.GetOrAdd(address, true);
        var control = (byte)(MBusControl.ReqUd2 | (fcb ? MBusControl.Fcb : 0));
        var reply = await RequestAsync(MBusFrame.Short(control, address), ct).ConfigureAwait(false) ?? throw new IoTComTimeoutException($"No answer from M-Bus address {address}.");
        _fcb[address] = !fcb;
        if (reply.Type != MBusFrameType.Long || (reply.Control & 0x0F) != MBusControl.RspUd) throw new ProtocolException($"Expected RSP_UD, got {reply}.");
        return MBusTelegram.FromFrame(reply);
    }

    /// <summary>Pings addresses <paramref name="from"/>…<paramref name="to"/> and returns those that answered.</summary>
    public async Task<IReadOnlyList<byte>> ScanAsync(byte from = 0, byte to = 250, IProgress<byte>? progress = null, CancellationToken ct = default)
    {
        var found = new List<byte>();
        for (var a = from; a <= to; a++)
        {
            progress?.Report(a);
            if (await PingAsync(a, ct).ConfigureAwait(false)) found.Add(a);
            if (a == byte.MaxValue) break;
        }

        return found;
    }

    /// <summary>
    /// Selects a slave by secondary address (SND_UD to 253, CI 0x52). <paramref name="id"/> may contain 'F' wildcards
    /// (e.g. "1234FFFF"); null manufacturer/version/medium match any. Returns true when a slave acknowledged.
    /// </summary>
    public async Task<bool> SelectAsync(string id, string? manufacturer = null, byte? version = null, byte? medium = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (id.Length != 8 || !id.All(Uri.IsHexDigit)) throw new ArgumentException("A secondary address ID has 8 BCD digits ('F' = wildcard).", nameof(id));
        var data = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(data, Convert.ToUInt32(id, 16));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), manufacturer is null ? (ushort)0xFFFF : MBusTelegram.ManufacturerId(manufacturer));
        data[6] = version ?? 0xFF;
        data[7] = medium ?? 0xFF;
        try
        {
            _fcb[MBusControl.AddressNetworkLayer] = true;
            return (await RequestAsync(MBusFrame.Long(MBusControl.SndUd, MBusControl.AddressNetworkLayer, MBusCi.SelectSlave, data), ct, retries: 0).ConfigureAwait(false))?.Type == MBusFrameType.Ack;
        }
        catch (IoTComTimeoutException)
        {
            return false;
        }
    }

    /// <summary>Selects a slave by its ID and reads it through address 253.</summary>
    public async Task<MBusTelegram> ReadSecondaryAsync(string id, CancellationToken ct = default)
    {
        if (!await SelectAsync(id, ct: ct).ConfigureAwait(false)) throw new DeviceException($"No M-Bus slave with secondary address {id} answered the selection.");
        return await ReadAsync(MBusControl.AddressNetworkLayer, ct).ConfigureAwait(false);
    }

    private async Task<MBusFrame?> RequestAsync(MBusFrame request, CancellationToken ct, int? retries = null)
    {
        var t = _transport ?? throw new TransportException("Not connected (call ConnectAsync).");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var attempts = 1 + (retries ?? _options.Retries);
            for (var attempt = 0; attempt < attempts; attempt++)
            {
                _pending = [];
                var bytes = request.Encode();
                Tap(FrameDirection.Outbound, bytes, request.ToString);
                await t.Pipe.Output.WriteAsync(bytes, ct).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(_options.ResponseTimeout);
                try
                {
                    var reply = await ReadFrameAsync(t.Pipe, timeout.Token).ConfigureAwait(false);
                    if (reply is not null) return reply;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                }
            }

            throw new IoTComTimeoutException($"No M-Bus answer to {request} after {attempts} attempt(s).");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<MBusFrame?> ReadFrameAsync(IDuplexPipe pipe, CancellationToken ct)
    {
        while (true)
        {
            while (_pending.Length > 0)
            {
                var status = MBusFrame.TryRead(_pending, out var frame, out var n, out var error);
                if (status == MBusFrame.ReadStatus.NeedMore) break;
                var raw = _pending[..n];
                _pending = _pending[n..];
                if (status == MBusFrame.ReadStatus.Frame)
                {
                    Tap(FrameDirection.Inbound, raw, frame!.ToString);
                    return frame;
                }

                Logger.LogDebug("M-Bus: skipped bytes ({Error})", error);
            }

            var result = await pipe.Input.ReadAsync(ct).ConfigureAwait(false);
            _pending = [.. _pending, .. result.Buffer.ToArray()];
            pipe.Input.AdvanceTo(result.Buffer.End);
            if (result.IsCompleted && result.Buffer.IsEmpty) throw new TransportException("The M-Bus link closed.");
        }
    }
}

/// <summary>A simulated slave: identity and a record generator.</summary>
public sealed class MBusSimulatedDevice
{
    /// <summary>Creates a slave.</summary>
    public MBusSimulatedDevice(byte primaryAddress, uint id, string manufacturer, byte version, byte medium, Func<MBusSimulatedDevice, MBusRecordWriter, MBusRecordWriter> records)
    {
        PrimaryAddress = primaryAddress;
        Id = id;
        Manufacturer = manufacturer;
        Version = version;
        Medium = medium;
        Records = records;
    }

    /// <summary>Primary address.</summary>
    public byte PrimaryAddress { get; }

    /// <summary>Identification number (BCD digits as hex, e.g. 0x12345678).</summary>
    public uint Id { get; }

    /// <summary>Manufacturer code.</summary>
    public string Manufacturer { get; }

    /// <summary>Version.</summary>
    public byte Version { get; }

    /// <summary>Medium.</summary>
    public byte Medium { get; }

    /// <summary>Adds the records of one response.</summary>
    public Func<MBusSimulatedDevice, MBusRecordWriter, MBusRecordWriter> Records { get; }

    /// <summary>Access number (incremented per response).</summary>
    public byte AccessNumber { get; internal set; }

    /// <summary>Responses sent.</summary>
    public int Reads { get; internal set; }

    internal bool Selected;

    internal byte[] Response()
    {
        AccessNumber++;
        Reads++;
        return Records(this, new MBusRecordWriter(Id, Manufacturer, Version, Medium, AccessNumber)).ToArray();
    }

    internal bool Matches(ReadOnlySpan<byte> selection)
    {
        if (selection.Length < 8) return false;
        var idPattern = BinaryPrimitives.ReadUInt32LittleEndian(selection).ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
        var id = Id.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
        for (var i = 0; i < 8; i++)
            if (idPattern[i] != 'F' && idPattern[i] != id[i]) return false;
        var m = BinaryPrimitives.ReadUInt16LittleEndian(selection[4..]);
        return (m == 0xFFFF || m == MBusTelegram.ManufacturerId(Manufacturer)) && (selection[6] == 0xFF || selection[6] == Version) && (selection[7] == 0xFF || selection[7] == Medium);
    }
}

/// <summary>M-Bus slave simulator options.</summary>
public sealed class MBusSlaveSimulatorOptions : IListenerBuilder<MBusSlaveSimulatorOptions>
{
    /// <summary>Listener (TCP gateway, in-memory).</summary>
    public TransportListenerFactory? ListenerFactory { get; set; }

    /// <summary>A single transport (serial port).</summary>
    public TransportFactory? TransportFactory { get; set; }

    /// <summary>Friendly name.</summary>
    public string? Name { get; set; }

    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }

    /// <inheritdoc />
    public MBusSlaveSimulatorOptions UseListener(TransportListenerFactory factory)
    {
        (ListenerFactory, TransportFactory) = (factory, null);
        return this;
    }

    /// <inheritdoc />
    public MBusSlaveSimulatorOptions UseTransport(TransportFactory factory)
    {
        (TransportFactory, ListenerFactory) = (factory, null);
        return this;
    }
}

/// <summary>
/// A simulated M-Bus segment: several slaves sharing one bus. Answers SND_NKE with E5, REQ_UD2 with RSP_UD (variable
/// data, CI 0x72), and secondary-address selection. The default segment has a heat meter (address 1), a water meter
/// (2) and an electricity meter (3) whose readings advance with time.
/// </summary>
public sealed class MBusSlaveSimulator : EndpointBase, IServerEndpoint
{
    private readonly MBusSlaveSimulatorOptions _options;
    private readonly List<MBusSimulatedDevice> _devices = [];
    private ITransportListener? _listener;
    private CancellationTokenSource? _cts;

    private MBusSlaveSimulator(MBusSlaveSimulatorOptions options) : base("mbus", options.Logger)
    {
        _options = options;
        Name = options.Name;
    }

    /// <summary>Creates an empty segment (add devices with <see cref="Add"/> or call <see cref="AddDefaultDevices"/>).</summary>
    public static MBusSlaveSimulator Create(Action<MBusSlaveSimulatorOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var o = new MBusSlaveSimulatorOptions();
        configure(o);
        if (o.ListenerFactory is null && o.TransportFactory is null) throw new ArgumentException("A listener or transport is required.", nameof(configure));
        return new MBusSlaveSimulator(o);
    }

    /// <summary>Slaves on the segment.</summary>
    public IReadOnlyList<MBusSimulatedDevice> Devices => _devices;

    /// <summary>Adds a slave.</summary>
    public MBusSimulatedDevice Add(MBusSimulatedDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        _devices.Add(device);
        return device;
    }

    /// <summary>Adds a heat meter (1), a water meter (2) and an electricity meter (3).</summary>
    public MBusSlaveSimulator AddDefaultDevices(Func<DateTime>? clock = null)
    {
        var now = clock ?? (() => DateTime.Now);
        var start = now();
        double Hours() => (now() - start).TotalHours;
        Add(new MBusSimulatedDevice(1, 0x26100001, "IOT", 0x01, MBusMedium.Heat, (d, w) =>
        {
            var h = Hours();
            var flow = 0.42 + (0.05 * Math.Sin(h * 6));
            var supply = 68.0 + (1.5 * Math.Sin(h * 3));
            var ret = supply - 21.3;
            return w.Integer(0x06, (long)(18_244 + (h * 9.8)))                    // energy kWh
                .Integer(0x14, (long)((1_842.37 + (h * flow)) * 100))               // volume 0.01 m³
                .Integer(0x2B, (long)(flow * 1000 * 1.163 * (supply - ret)), 3)     // power W
                .Integer(0x3B, (long)(flow * 1000), 3)                              // volume flow l/h
                .Integer(0x5A, (long)(supply * 10), 2)                              // flow temperature 0.1 °C
                .Integer(0x5E, (long)(ret * 10), 2)                                 // return temperature 0.1 °C
                .Integer(0x62, (long)((supply - ret) * 10), 2)                      // ΔT 0.1 K
                .DateTime(now())
                .Integer(0x06, 17_102, storage: 1)                                  // energy at the last billing date
                .Date(new DateTime(now().Year, 1, 1), storage: 1);
        }));
        Add(new MBusSimulatedDevice(2, 0x26200002, "IOT", 0x01, MBusMedium.Water, (d, w) =>
        {
            var h = Hours();
            return w.Integer(0x13, (long)(412_377 + (h * 37)))                      // volume litres
                .Integer(0x3B, (long)(30 + (25 * Math.Abs(Math.Sin(h * 20)))), 2)  // flow l/h
                .Integer(0x13, 398_120, storage: 1)
                .Date(new DateTime(now().Year, now().Month, 1), storage: 1)
                .Integer(0xFD, 3, 1, vife: 0x17);                                  // error flags
        }));
        Add(new MBusSimulatedDevice(3, 0x26300003, "IOT", 0x02, MBusMedium.Electricity, (d, w) =>
        {
            var h = Hours();
            var power = 1_850 + (400 * Math.Sin(h * 8));
            return w.Bcd(0x03, (long)(5_412_870 + (h * power)), 8)                  // energy Wh, tariff 0
                .Bcd(0x03, 3_901_110, 8, tariff: 1)
                .Bcd(0x03, 1_511_760 + (long)(h * power), 8, tariff: 2)
                .Integer(0x2B, (long)power, 4)                                      // power W
                .Integer(0xFD, 2_305, 2, vife: 0x48)                                 // voltage 0.1 V
                .Integer(0xFD, (long)(power / 230.5 * 100), 2, vife: 0x5A);          // current 0.01 A
        }));
        return this;
    }

    /// <inheritdoc />
    public async ValueTask StartAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_cts is not null) return;
        var cts = _cts = new CancellationTokenSource();
        if (_options.ListenerFactory is { } factory)
        {
            var listener = _listener = factory();
            await listener.StartAsync(ct).ConfigureAwait(false);
            _ = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        var peer = await listener.AcceptAsync(cts.Token).ConfigureAwait(false);
                        _ = Task.Run(() => ServeAsync(peer, cts.Token), CancellationToken.None);
                    }
                    catch (Exception) when (cts.IsCancellationRequested)
                    {
                        break;
                    }
                }
            }, CancellationToken.None);
        }
        else
        {
            var t = _options.TransportFactory!();
            await t.OpenAsync(ct).ConfigureAwait(false);
            _ = Task.Run(() => ServeAsync(t, cts.Token), CancellationToken.None);
        }

        SetState(EndpointState.Listening);
    }

    /// <inheritdoc />
    public async ValueTask StopAsync(CancellationToken ct = default)
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts is null) return;
        await cts.CancelAsync().ConfigureAwait(false);
        if (_listener is not null) await _listener.DisposeAsync().ConfigureAwait(false);
        _listener = null;
        cts.Dispose();
        SetState(EndpointState.Disconnected);
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore() => await StopAsync().ConfigureAwait(false);

    private async Task ServeAsync(ITransport transport, CancellationToken ct)
    {
        var pending = Array.Empty<byte>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await transport.Pipe.Input.ReadAsync(ct).ConfigureAwait(false);
                pending = [.. pending, .. result.Buffer.ToArray()];
                transport.Pipe.Input.AdvanceTo(result.Buffer.End);
                while (true)
                {
                    var status = MBusFrame.TryRead(pending, out var frame, out var n, out _);
                    if (status == MBusFrame.ReadStatus.NeedMore) break;
                    var raw = pending[..n];
                    pending = pending[n..];
                    if (status != MBusFrame.ReadStatus.Frame) continue;
                    Tap(FrameDirection.Inbound, raw, frame!.ToString);
                    if (Respond(frame) is { } reply)
                    {
                        var bytes = reply.Encode();
                        Tap(FrameDirection.Outbound, bytes, reply.ToString);
                        await transport.Pipe.Output.WriteAsync(bytes, ct).ConfigureAwait(false);
                    }
                }

                if (result.IsCompleted) break;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
        finally
        {
            await transport.DisposeAsync().ConfigureAwait(false);
        }
    }

    private MBusFrame? Respond(MBusFrame frame)
    {
        lock (_devices)
        {
            IEnumerable<MBusSimulatedDevice> Targets(byte address) => address switch
            {
                MBusControl.AddressNetworkLayer => _devices.Where(d => d.Selected),
                MBusControl.AddressBroadcastReply => _devices.Count == 1 ? _devices : [],
                MBusControl.AddressBroadcast => [],
                _ => _devices.Where(d => d.PrimaryAddress == address),
            };

            var control = (byte)(frame.Control & ~MBusControl.Fcb);
            if (frame.Type == MBusFrameType.Short && control == MBusControl.SndNke)
            {
                if (frame.Address == MBusControl.AddressNetworkLayer) foreach (var d in _devices) d.Selected = false;
                return Targets(frame.Address).Any() ? MBusFrame.Ack : null;
            }

            if (frame.Type == MBusFrameType.Short && control == MBusControl.ReqUd2)
            {
                var target = Targets(frame.Address).ToList();
                return target.Count == 1 ? MBusFrame.Long(MBusControl.RspUd, target[0].PrimaryAddress, MBusCi.VariableLong, target[0].Response()) : null;
            }

            if (frame.Type is MBusFrameType.Long or MBusFrameType.Control && control == MBusControl.SndUd && frame.Ci == MBusCi.SelectSlave && frame.Address == MBusControl.AddressNetworkLayer)
            {
                var matched = 0;
                foreach (var d in _devices)
                {
                    d.Selected = d.Matches(frame.UserData);
                    if (d.Selected) matched++;
                }

                return matched == 1 ? MBusFrame.Ack : null;   // several matches would collide on a real bus
            }

            return null;
        }
    }
}

/// <summary>Frame-lane description of M-Bus frames and their records.</summary>
public static class MBusAnatomy
{
    /// <summary>Describes a frame: start, length, control, address, CI, header, records, checksum, stop.</summary>
    public static IReadOnlyList<FrameField> Describe(ReadOnlySpan<byte> data)
    {
        if (MBusFrame.TryRead(data, out var frame, out var n, out var error) != MBusFrame.ReadStatus.Frame || n != data.Length)
            return [new FrameField("Invalid", 0, data.Length, FrameFieldKind.Error, error ?? "incomplete frame")];
        switch (frame!.Type)
        {
            case MBusFrameType.Ack:
                return [new FrameField("ACK", 0, 1, FrameFieldKind.Function, "E5")];
            case MBusFrameType.Short:
                return
                [
                    new FrameField("Start", 0, 1, FrameFieldKind.Delimiter),
                    new FrameField("C", 1, 1, FrameFieldKind.Function, MBusControl.Name(frame.Control)),
                    new FrameField("A", 2, 1, FrameFieldKind.Address, frame.Address.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    new FrameField("CS", 3, 1, FrameFieldKind.Checksum),
                    new FrameField("Stop", 4, 1, FrameFieldKind.Delimiter),
                ];
        }

        var fields = new List<FrameField>
        {
            new("Start", 0, 4, FrameFieldKind.Header, $"L={data[1]}"),
            new("C", 4, 1, FrameFieldKind.Function, MBusControl.Name(frame.Control)),
            new("A", 5, 1, FrameFieldKind.Address, frame.Address.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("CI", 6, 1, FrameFieldKind.Function, $"0x{frame.Ci:X2}"),
        };
        var user = frame.UserData;
        if (frame.Ci == MBusCi.VariableLong && MBusTelegram.TryParse(user, out var t, out _))
        {
            fields.Add(new FrameField("ID", 7, 4, FrameFieldKind.Address, t!.Id.ToString("X8", System.Globalization.CultureInfo.InvariantCulture)));
            fields.Add(new FrameField("Header", 11, 8, FrameFieldKind.Header, $"{t.Manufacturer} v{t.Version} {t.MediumName} #{t.AccessNumber}"));
            foreach (var r in t.Records) fields.Add(new FrameField(r.Quantity, 7 + r.Offset, r.Length, FrameFieldKind.Data, r.FormattedValue));
            var covered = fields.Sum(f => f.Length);
            if (7 + user.Length > covered) fields.Add(new FrameField("Manufacturer data", covered, 7 + user.Length - covered, FrameFieldKind.Data));
        }
        else if (frame.Ci == MBusCi.SelectSlave && user.Length >= 8)
        {
            fields.Add(new FrameField("Select", 7, user.Length, FrameFieldKind.Address, BinaryPrimitives.ReadUInt32LittleEndian(user).ToString("X8", System.Globalization.CultureInfo.InvariantCulture)));
        }
        else if (user.Length > 0)
        {
            fields.Add(new FrameField("Data", 7, user.Length, FrameFieldKind.Data));
        }

        fields.Add(new FrameField("CS", data.Length - 2, 1, FrameFieldKind.Checksum));
        fields.Add(new FrameField("Stop", data.Length - 1, 1, FrameFieldKind.Delimiter));
        return fields;
    }
}
