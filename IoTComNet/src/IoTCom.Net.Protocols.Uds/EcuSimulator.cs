using System.Buffers.Binary;
using System.Text;
using IoTCom.Net.Protocols.IsoTp;
using IoTCom.Net.Transport.Can;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Protocols.Uds;

/// <summary>
/// A simple, deterministic vehicle model used by <see cref="EcuSimulator"/>: a 60-second urban drive cycle
/// (idle, acceleration through the gears, cruise, braking) with engine warm-up and slow fuel use.
/// </summary>
public sealed class VehicleSimulation
{
    private static readonly double[] GearRatios = [0, 3.6, 2.1, 1.4, 1.0, 0.8];
    private readonly Random _random;
    private double _cycle;

    /// <summary>Creates the model.</summary>
    public VehicleSimulation(int seed = 42) => _random = new Random(seed);

    /// <summary>Engine speed, rpm.</summary>
    public double Rpm { get; private set; } = 820;
    /// <summary>Vehicle speed, km/h.</summary>
    public double SpeedKmh { get; private set; }
    /// <summary>Coolant temperature, °C.</summary>
    public double CoolantC { get; private set; } = 32;
    /// <summary>Engine oil temperature, °C.</summary>
    public double OilC { get; private set; } = 30;
    /// <summary>Intake air temperature, °C.</summary>
    public double IntakeC { get; private set; } = 31;
    /// <summary>Ambient temperature, °C.</summary>
    public double AmbientC { get; set; } = 29;
    /// <summary>Throttle position, %.</summary>
    public double ThrottlePercent { get; private set; }
    /// <summary>Calculated engine load, %.</summary>
    public double LoadPercent { get; private set; } = 18;
    /// <summary>Mass air flow, g/s.</summary>
    public double MafGs { get; private set; } = 3;
    /// <summary>Intake manifold pressure, kPa.</summary>
    public double MapKpa { get; private set; } = 32;
    /// <summary>Fuel tank level, %.</summary>
    public double FuelPercent { get; private set; } = 64;
    /// <summary>Fuel rate, L/h.</summary>
    public double FuelRateLh { get; private set; } = 0.8;
    /// <summary>Control module voltage, V.</summary>
    public double Voltage { get; private set; } = 14.2;
    /// <summary>Seconds since engine start.</summary>
    public double RunTimeS { get; private set; }
    /// <summary>Current gear (0 = neutral).</summary>
    public int Gear { get; private set; }
    /// <summary>Extra coolant heat (°C) injected to simulate a cooling fault.</summary>
    public double CoolingFault { get; set; }

    /// <summary>Starts from a warm engine (coolant and oil at <paramref name="coolantC"/>).</summary>
    public void WarmUp(double coolantC = 86)
    {
        CoolantC = coolantC;
        OilC = coolantC + 4;
    }

    /// <summary>Advances the model by <paramref name="dt"/>.</summary>
    public void Step(TimeSpan dt)
    {
        var s = dt.TotalSeconds;
        RunTimeS += s;
        _cycle = (_cycle + s) % 60;
        double targetSpeed = _cycle switch
        {
            < 8 => 0,
            < 20 => (_cycle - 8) / 12 * 72,
            < 40 => 72 + 10 * Math.Sin((_cycle - 20) / 20 * Math.PI),
            < 52 => 72 * (1 - (_cycle - 40) / 12),
            _ => 0,
        };
        var accel = (targetSpeed - SpeedKmh) / Math.Max(s, 0.01);
        SpeedKmh = Math.Max(0, SpeedKmh + Math.Clamp(targetSpeed - SpeedKmh, -12 * s, 9 * s));
        Gear = SpeedKmh < 1 ? 0 : SpeedKmh < 18 ? 1 : SpeedKmh < 34 ? 2 : SpeedKmh < 52 ? 3 : SpeedKmh < 70 ? 4 : 5;
        var targetRpm = Gear == 0 ? 820 : Math.Clamp(SpeedKmh * GearRatios[Gear] * 32 + 900, 900, 5200);
        Rpm += (targetRpm - Rpm) * Math.Min(1, 4 * s) + (_random.NextDouble() - 0.5) * 20;
        ThrottlePercent = Math.Clamp(accel > 0.5 ? 18 + accel * 3 : Gear == 0 ? 0 : 9, 0, 100) + _random.NextDouble();
        LoadPercent = Math.Clamp(15 + ThrottlePercent * 0.8 + _random.NextDouble() * 2, 0, 100);
        MapKpa = Math.Clamp(28 + LoadPercent * 0.7, 20, 101);
        MafGs = Math.Max(1.5, Rpm / 1000 * LoadPercent / 100 * 18);
        FuelRateLh = MafGs / 14.7 * 3600 / 740;
        FuelPercent = Math.Max(0, FuelPercent - FuelRateLh * s / 3600 / 50 * 100);
        var heat = 90 + CoolingFault;
        // A failed cooling system heats up much faster than a cold engine warms.
        CoolantC += (heat - CoolantC) * Math.Min(1, s / (CoolingFault > 0 ? 12 : 90)) + (_random.NextDouble() - 0.5) * 0.05;
        OilC += (CoolantC + 6 - OilC) * Math.Min(1, s / 150);
        IntakeC = SpeedKmh < 10 ? AmbientC + 6 : AmbientC + 2;
        Voltage = 14.1 + (_random.NextDouble() - 0.5) * 0.1;
    }

    /// <summary>Value of an OBD mode 01 PID, or null when not simulated.</summary>
    public double? Pid(byte pid) => pid switch
    {
        0x04 => LoadPercent,
        0x05 => CoolantC,
        0x0B => MapKpa,
        0x0C => Rpm,
        0x0D => SpeedKmh,
        0x0F => IntakeC,
        0x10 => MafGs,
        0x11 => ThrottlePercent,
        0x1F => RunTimeS,
        0x2F => FuelPercent,
        0x42 => Voltage,
        0x46 => AmbientC,
        0x5C => OilC,
        0x5E => FuelRateLh,
        _ => null,
    };
}

/// <summary>ECU simulator options.</summary>
public sealed class EcuSimulatorOptions
{
    /// <summary>Physical request identifier we listen on (0x7E0 = engine).</summary>
    public uint RequestId { get; set; } = 0x7E0;

    /// <summary>Response identifier (0x7E8).</summary>
    public uint ResponseId { get; set; } = 0x7E8;

    /// <summary>Functional (broadcast) request identifier (0x7DF).</summary>
    public uint FunctionalId { get; set; } = 0x7DF;

    /// <summary>VIN (17 characters; the default is the common documentation example).</summary>
    public string Vin { get; set; } = "1HGCM82633A004352";

    /// <summary>ECU part number (DID F187).</summary>
    public string PartNumber { get; set; } = "IOTCOM-ECU-0001";

    /// <summary>Software version (DID F189).</summary>
    public string SoftwareVersion { get; set; } = "1.4.2";

    /// <summary>Simulation speed factor (1 = real time).</summary>
    public double TimeScale { get; set; } = 1;

    /// <summary>Random seed (vehicle noise, security seeds).</summary>
    public int Seed { get; set; } = 42;

    /// <summary>Use CAN FD frames.</summary>
    public bool Fd { get; set; }

    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }
}

/// <summary>A request handled by the simulator (for logs and the Gallery).</summary>
/// <param name="Request">Request bytes.</param>
/// <param name="Response">Final response bytes (null when suppressed).</param>
/// <param name="Functional">True when it arrived on the functional identifier.</param>
public readonly record struct EcuExchange(byte[] Request, byte[]? Response, bool Functional);

/// <summary>
/// A simulated engine ECU answering UDS (sessions, security access, DIDs, DTCs, routines, reset) and OBD-II
/// (modes 01, 03, 04, 07, 09) over ISO-TP — enough to develop and test diagnostic tools without a car.
/// </summary>
/// <remarks>
/// Security access level 0x01 uses a demo algorithm, <see cref="ComputeKey"/>; real ECUs use secret OEM algorithms.
/// DID F198 (repair shop code) is writable in the extended session after security access; routine 0x0203 is a
/// self-test that answers "response pending" first.
/// </remarks>
public sealed class EcuSimulator : EndpointBase, IServerEndpoint
{
    private readonly ICanBus _bus;
    private readonly EcuSimulatorOptions _options;
    private readonly IsoTpChannel _channel;
    private readonly Lock _gate = new();
    private readonly Random _random;
    private readonly List<Dtc> _dtcs = [];
    private CancellationTokenSource? _cts;
    private Task[] _loops = [];
    private byte[]? _seed;
    private int _failedKeys;
    private DateTimeOffset _lockedUntil;
    private DateTimeOffset _lastActivity = DateTimeOffset.UtcNow;
    private string _repairShopCode = "WORKSHOP-00";

    private EcuSimulator(ICanBus bus, EcuSimulatorOptions options) : base("uds-ecu", options.Logger)
    {
        _bus = bus;
        _options = options;
        _random = new Random(options.Seed);
        Vehicle = new VehicleSimulation(options.Seed);
        _channel = IsoTpChannel.Create(bus, o =>
        {
            o.TxId = options.ResponseId;
            o.RxId = options.RequestId;
            o.Fd = options.Fd;
            o.Logger = options.Logger;
        });
        ResetDtcs();
        Name = $"ECU {options.RequestId:X3}";
    }

    /// <summary>Creates a simulator on <paramref name="bus"/>.</summary>
    public static EcuSimulator Create(ICanBus bus, Action<EcuSimulatorOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(bus);
        var options = new EcuSimulatorOptions();
        configure?.Invoke(options);
        if (options.Vin.Length != 17) throw new ArgumentException("A VIN has 17 characters.", nameof(configure));
        return new EcuSimulator(bus, options);
    }

    /// <summary>The vehicle model behind mode 01 and the live-data DID.</summary>
    public VehicleSimulation Vehicle { get; }

    /// <summary>Options.</summary>
    public EcuSimulatorOptions Options => _options;

    /// <summary>Active session.</summary>
    public UdsSession Session { get; private set; } = UdsSession.Default;

    /// <summary>True after a successful security access in the current session.</summary>
    public bool SecurityUnlocked { get; private set; }

    /// <summary>Current DTCs (snapshot).</summary>
    public IReadOnlyList<Dtc> Dtcs
    {
        get
        {
            lock (_gate) return [.. _dtcs];
        }
    }

    /// <summary>Raised after every handled request.</summary>
    public event Action<EcuExchange>? RequestHandled;

    /// <summary>The demo seed/key algorithm of security level 0x01 (rotate-left of seed XOR 0x5A, plus index).</summary>
    public static byte[] ComputeKey(byte[] seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        var key = new byte[seed.Length];
        for (var i = 0; i < seed.Length; i++)
        {
            var x = (byte)(seed[i] ^ 0x5A);
            key[i] = (byte)(((x << 1) | (x >> 7)) + i);
        }
        return key;
    }

    /// <summary>Adds (or updates) a DTC, e.g. to simulate a fault from the UI.</summary>
    public void SetDtc(Dtc dtc)
    {
        lock (_gate)
        {
            _dtcs.RemoveAll(d => d.Code == dtc.Code);
            _dtcs.Add(dtc);
        }
    }

    private void ResetDtcs()
    {
        lock (_gate)
        {
            _dtcs.Clear();
            _dtcs.Add(new Dtc(Dtc.Parse("P0301").Code, DtcStatus.TestFailed | DtcStatus.Confirmed | DtcStatus.TestFailedSinceLastClear | DtcStatus.WarningIndicatorRequested));
            _dtcs.Add(new Dtc(Dtc.Parse("P0420").Code, DtcStatus.Pending | DtcStatus.TestFailedThisOperationCycle | DtcStatus.TestFailedSinceLastClear));
        }
    }

    /// <inheritdoc />
    public async ValueTask StartAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (State == EndpointState.Listening) return;
        SetState(EndpointState.Connecting);
        await _channel.ConnectAsync(ct).ConfigureAwait(false);
        var functional = _bus.OpenReader(CanFilter.Exact(_options.FunctionalId));
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loops =
        [
            Task.Run(() => PhysicalLoopAsync(token), CancellationToken.None),
            Task.Run(() => FunctionalLoopAsync(functional, token), CancellationToken.None),
            Task.Run(() => VehicleLoopAsync(token), CancellationToken.None),
        ];
        SetState(EndpointState.Listening);
    }

    /// <inheritdoc />
    public async ValueTask StopAsync(CancellationToken ct = default)
    {
        if (_cts is null) return;
        SetState(EndpointState.Stopping);
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_loops).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        await _channel.DisconnectAsync(ct).ConfigureAwait(false);
        _cts.Dispose();
        _cts = null;
        SetState(EndpointState.Disconnected);
    }

    private async Task PhysicalLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var request in _channel.ReadAllAsync(ct).ConfigureAwait(false))
                await HandleAsync(request, functional: false, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task FunctionalLoopAsync(CanReader reader, CancellationToken ct)
    {
        try
        {
            await foreach (var frame in reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                var d = frame.Data.Span;
                // Functional requests are single frames: PCI 0x0L (classic) or 0x00 + length (CAN FD).
                if (d.Length < 2 || d[0] >> 4 != 0) continue;
                var len = d[0] & 0x0F;
                var start = 1;
                if (len == 0 && d.Length > 8)
                {
                    len = d[1];
                    start = 2;
                }
                if (len == 0 || start + len > d.Length) continue;
                await HandleAsync(d.Slice(start, len).ToArray(), functional: true, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            reader.Dispose();
        }
    }

    private async Task VehicleLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                Vehicle.Step(TimeSpan.FromMilliseconds(100 * _options.TimeScale));
                if (Vehicle.CoolantC > 112) SetDtc(new Dtc(Dtc.Parse("P0217").Code, DtcStatus.TestFailed | DtcStatus.Pending | DtcStatus.TestFailedThisOperationCycle));
                // S3 server timer: a non-default session falls back after 5 s without requests.
                if (Session != UdsSession.Default && DateTimeOffset.UtcNow - _lastActivity > TimeSpan.FromSeconds(5))
                {
                    Session = UdsSession.Default;
                    SecurityUnlocked = false;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task HandleAsync(byte[] request, bool functional, CancellationToken ct)
    {
        _lastActivity = DateTimeOffset.UtcNow;
        Tap(FrameDirection.Inbound, request, () => UdsService.Name(request[0]));
        byte[]? response;
        try
        {
            if (request[0] == UdsService.RoutineControl && request.Length >= 4 && request[1] == 0x01 && request[2] == 0x02 && request[3] == 0x03 && Session != UdsSession.Default)
            {
                // Self-test takes a while: answer "response pending" first (NRC 0x78).
                await SendAsync([UdsService.NegativeResponse, UdsService.RoutineControl, (byte)UdsNrc.ResponsePending], ct).ConfigureAwait(false);
                await Task.Delay(300, ct).ConfigureAwait(false);
            }
            response = Handle(request, functional);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            response = Negative(request[0], UdsNrc.IncorrectMessageLengthOrInvalidFormat);
        }
        // Functional requests the ECU does not support stay silent (ISO 14229: no NRC 0x11/0x12/0x31 on functional).
        if (functional && response is [UdsService.NegativeResponse, _, var nrc] && nrc is 0x11 or 0x12 or 0x31) response = null;
        if (response is not null) await SendAsync(response, ct).ConfigureAwait(false);
        RequestHandled?.Invoke(new EcuExchange(request, response, functional));
    }

    private async Task SendAsync(byte[] response, CancellationToken ct)
    {
        Tap(FrameDirection.Outbound, response, () => UdsService.Name(response[0]));
        try
        {
            await _channel.SendAsync(response, ct).ConfigureAwait(false);
        }
        catch (IsoTpException ex)
        {
            Logger.LogDebug(ex, "ECU simulator: response not delivered");
        }
    }

    private static byte[] Negative(byte sid, UdsNrc nrc) => [UdsService.NegativeResponse, sid, (byte)nrc];

    private static byte[] Positive(byte sid, params ReadOnlySpan<byte> data)
    {
        var r = new byte[1 + data.Length];
        r[0] = (byte)(sid + UdsService.PositiveOffset);
        data.CopyTo(r.AsSpan(1));
        return r;
    }

    /// <summary>Computes the response to one request (null = suppressed positive response).</summary>
    internal byte[]? Handle(byte[] req, bool functional)
    {
        var sid = req[0];
        return sid switch
        {
            0x01 => ObdMode01(req),
            0x03 => ObdDtcs(0x43, DtcStatus.Confirmed),
            0x07 => ObdDtcs(0x47, DtcStatus.Pending),
            0x04 => ObdClear(),
            0x09 => ObdMode09(req),
            UdsService.DiagnosticSessionControl => SessionControl(req),
            UdsService.EcuReset => EcuReset(req),
            UdsService.TesterPresent => req.Length < 2 ? Negative(sid, UdsNrc.IncorrectMessageLengthOrInvalidFormat)
                : (req[1] & 0x7F) != 0 ? Negative(sid, UdsNrc.SubFunctionNotSupported)
                : (req[1] & 0x80) != 0 ? null : Positive(sid, 0x00),
            UdsService.ReadDataByIdentifier => ReadDids(req),
            UdsService.WriteDataByIdentifier => WriteDid(req),
            UdsService.SecurityAccess => Security(req),
            UdsService.ReadDtcInformation => ReadDtcInfo(req),
            UdsService.ClearDiagnosticInformation => ClearDtcs(req),
            UdsService.RoutineControl => Routine(req),
            _ => functional ? null : Negative(sid, UdsNrc.ServiceNotSupported),
        };
    }

    private byte[]? SessionControl(byte[] req)
    {
        if (req.Length != 2) return Negative(req[0], UdsNrc.IncorrectMessageLengthOrInvalidFormat);
        var session = (UdsSession)(req[1] & 0x7F);
        if (session is not (UdsSession.Default or UdsSession.Extended or UdsSession.Programming)) return Negative(req[0], UdsNrc.SubFunctionNotSupported);
        if (session != Session) SecurityUnlocked = false;
        Session = session;
        // P2server = 50 ms, P2*server = 5000 ms (in 10 ms units).
        return (req[1] & 0x80) != 0 ? null : Positive(req[0], (byte)session, 0x00, 0x32, 0x01, 0xF4);
    }

    private byte[]? EcuReset(byte[] req)
    {
        if (req.Length != 2) return Negative(req[0], UdsNrc.IncorrectMessageLengthOrInvalidFormat);
        var type = req[1] & 0x7F;
        if (type is < 1 or > 3) return Negative(req[0], UdsNrc.SubFunctionNotSupported);
        Session = UdsSession.Default;
        SecurityUnlocked = false;
        _seed = null;
        return (req[1] & 0x80) != 0 ? null : Positive(req[0], (byte)type);
    }

    private byte[] ReadDids(byte[] req)
    {
        if (req.Length < 3 || (req.Length - 1) % 2 != 0) return Negative(req[0], UdsNrc.IncorrectMessageLengthOrInvalidFormat);
        var response = new List<byte> { (byte)(req[0] + UdsService.PositiveOffset) };
        for (var i = 1; i < req.Length; i += 2)
        {
            var did = BinaryPrimitives.ReadUInt16BigEndian(req.AsSpan(i));
            var data = ReadDid(did);
            if (data is null) return Negative(req[0], UdsNrc.RequestOutOfRange);
            response.Add((byte)(did >> 8));
            response.Add((byte)did);
            response.AddRange(data);
        }
        return [.. response];
    }

    private byte[]? ReadDid(ushort did) => did switch
    {
        UdsDid.Vin => Encoding.ASCII.GetBytes(_options.Vin),
        UdsDid.SparePartNumber => Encoding.ASCII.GetBytes(_options.PartNumber),
        UdsDid.SoftwareVersion => Encoding.ASCII.GetBytes(_options.SoftwareVersion),
        UdsDid.SerialNumber => "SIM00042"u8.ToArray(),
        UdsDid.SystemName => "ENGINE"u8.ToArray(),
        UdsDid.ActiveSession => [(byte)Session],
        UdsDid.RepairShopCode => Encoding.ASCII.GetBytes(_repairShopCode),
        // 0x0100: live data record — rpm (u16, 0.25 rpm), speed (km/h), coolant (°C + 40).
        0x0100 => [.. ObdPids.EngineRpm.Encode(Vehicle.Rpm), .. ObdPids.VehicleSpeed.Encode(Vehicle.SpeedKmh), .. ObdPids.CoolantTemperature.Encode(Vehicle.CoolantC)],
        _ => null,
    };

    private byte[] WriteDid(byte[] req)
    {
        if (req.Length < 4) return Negative(req[0], UdsNrc.IncorrectMessageLengthOrInvalidFormat);
        var did = BinaryPrimitives.ReadUInt16BigEndian(req.AsSpan(1));
        if (did != UdsDid.RepairShopCode) return Negative(req[0], ReadDid(did) is null ? UdsNrc.RequestOutOfRange : UdsNrc.SecurityAccessDenied);
        if (Session == UdsSession.Default) return Negative(req[0], UdsNrc.ServiceNotSupportedInActiveSession);
        if (!SecurityUnlocked) return Negative(req[0], UdsNrc.SecurityAccessDenied);
        if (req.Length - 3 > 16) return Negative(req[0], UdsNrc.IncorrectMessageLengthOrInvalidFormat);
        _repairShopCode = Encoding.ASCII.GetString(req, 3, req.Length - 3);
        return Positive(req[0], req[1], req[2]);
    }

    private byte[] Security(byte[] req)
    {
        if (req.Length < 2) return Negative(req[0], UdsNrc.IncorrectMessageLengthOrInvalidFormat);
        if (Session == UdsSession.Default) return Negative(req[0], UdsNrc.ServiceNotSupportedInActiveSession);
        var sub = req[1] & 0x7F;
        if (sub == 0x01)
        {
            if (DateTimeOffset.UtcNow < _lockedUntil) return Negative(req[0], UdsNrc.RequiredTimeDelayNotExpired);
            if (SecurityUnlocked) return Positive(req[0], 0x01, 0, 0, 0, 0);
            _seed = new byte[4];
            lock (_gate) _random.NextBytes(_seed);
            if (_seed.All(b => b == 0)) _seed[0] = 1;
            return Positive(req[0], [0x01, .. _seed]);
        }
        if (sub == 0x02)
        {
            if (_seed is null) return Negative(req[0], UdsNrc.RequestSequenceError);
            if (!req.AsSpan(2).SequenceEqual(ComputeKey(_seed)))
            {
                _seed = null;
                if (++_failedKeys >= 3)
                {
                    _failedKeys = 0;
                    _lockedUntil = DateTimeOffset.UtcNow.AddSeconds(10);
                    return Negative(req[0], UdsNrc.ExceededNumberOfAttempts);
                }
                return Negative(req[0], UdsNrc.InvalidKey);
            }
            _seed = null;
            _failedKeys = 0;
            SecurityUnlocked = true;
            return Positive(req[0], 0x02);
        }
        return Negative(req[0], UdsNrc.SubFunctionNotSupported);
    }

    private byte[] ReadDtcInfo(byte[] req)
    {
        if (req.Length < 2) return Negative(req[0], UdsNrc.IncorrectMessageLengthOrInvalidFormat);
        var sub = req[1] & 0x7F;
        var mask = sub is 0x01 or 0x02 ? (req.Length >= 3 ? req[2] : (byte)0) : (byte)0xFF;
        if (sub is not (0x01 or 0x02 or 0x0A)) return Negative(req[0], UdsNrc.SubFunctionNotSupported);
        List<Dtc> matches;
        lock (_gate) matches = _dtcs.Where(d => sub == 0x0A || ((byte)d.Status & mask) != 0).ToList();
        if (sub == 0x01) return Positive(req[0], 0x01, 0xFF, 0x01, (byte)(matches.Count >> 8), (byte)matches.Count);
        var r = new List<byte> { (byte)(req[0] + UdsService.PositiveOffset), (byte)sub, 0xFF };
        foreach (var d in matches) r.AddRange([(byte)(d.Code >> 16), (byte)(d.Code >> 8), (byte)d.Code, (byte)d.Status]);
        return [.. r];
    }

    private byte[] ClearDtcs(byte[] req)
    {
        if (req.Length != 4) return Negative(req[0], UdsNrc.IncorrectMessageLengthOrInvalidFormat);
        lock (_gate) _dtcs.Clear();
        Vehicle.CoolingFault = 0;
        return Positive(req[0]);
    }

    private byte[] Routine(byte[] req)
    {
        if (req.Length < 4) return Negative(req[0], UdsNrc.IncorrectMessageLengthOrInvalidFormat);
        var id = BinaryPrimitives.ReadUInt16BigEndian(req.AsSpan(2));
        if (id != 0x0203) return Negative(req[0], UdsNrc.RequestOutOfRange);
        if (Session == UdsSession.Default) return Negative(req[0], UdsNrc.ServiceNotSupportedInActiveSession);
        return req[1] switch
        {
            0x01 or 0x03 => Positive(req[0], req[1], req[2], req[3], 0x00), // 0x00 = self-test passed
            _ => Negative(req[0], UdsNrc.SubFunctionNotSupported),
        };
    }

    private byte[]? ObdMode01(byte[] req)
    {
        if (req.Length < 2 || req.Length > 7) return null;
        var r = new List<byte> { 0x41 };
        foreach (var pid in req.AsSpan(1))
        {
            if (pid % 0x20 == 0)
            {
                // Supported-PID bitmaps: 0x00 → 01–20, 0x20 → 21–40, 0x40 → 41–60, 0x60 → nothing more.
                uint mask = 0;
                foreach (var p in ObdPids.All.Keys)
                    if (p > pid && p <= pid + 0x20) mask |= 0x8000_0000u >> (p - pid - 1);
                if (pid < 0x40) mask |= 1; // next range available
                if (pid >= 0x60) continue;
                r.Add(pid);
                r.AddRange([(byte)(mask >> 24), (byte)(mask >> 16), (byte)(mask >> 8), (byte)mask]);
                continue;
            }
            if (ObdPids.All.TryGetValue(pid, out var def) && Vehicle.Pid(pid) is { } value)
            {
                r.Add(pid);
                r.AddRange(def.Encode(value));
            }
        }
        return r.Count > 1 ? [.. r] : null;
    }

    private byte[] ObdDtcs(byte responseSid, DtcStatus status)
    {
        List<Dtc> list;
        lock (_gate) list = _dtcs.Where(d => (d.Status & status) != 0).ToList();
        var r = new List<byte> { responseSid, (byte)list.Count };
        foreach (var d in list) r.AddRange([(byte)(d.SaeCode >> 8), (byte)d.SaeCode]);
        return [.. r];
    }

    private byte[] ObdClear()
    {
        lock (_gate) _dtcs.Clear();
        Vehicle.CoolingFault = 0;
        return [0x44];
    }

    private byte[]? ObdMode09(byte[] req)
    {
        if (req.Length < 2) return null;
        return req[1] switch
        {
            0x00 => [0x49, 0x00, 0x40, 0x00, 0x00, 0x00], // PID 02 (VIN) supported
            0x02 => [0x49, 0x02, 0x01, .. Encoding.ASCII.GetBytes(_options.Vin)],
            _ => null,
        };
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore()
    {
        await StopAsync().ConfigureAwait(false);
        await _channel.DisposeAsync().ConfigureAwait(false);
    }
}
