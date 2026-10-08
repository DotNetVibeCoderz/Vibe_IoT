namespace IoTCom.Net.Protocols.Dlms;

/// <summary>
/// A simulated three-phase smart meter on a <see cref="DlmsServer"/>: a household with a 3 kWp rooftop solar system
/// in Indonesia (230 V, 50 Hz, PLN time-of-use tariff: WBP peak 17:00–22:00, LWBP otherwise). It serves energy
/// registers (import and export, per tariff), instantaneous values per phase, the clock, a 15-minute load profile
/// with two days of history, and a disconnect relay.
/// </summary>
/// <remarks>
/// OBIS codes: 1.0.1.8.0/1/2 and 1.0.2.8.0 (Wh), 1.0.1.7.0 and 1.0.2.7.0 (W), 1.0.32/52/72.7.0 (V), 1.0.31/51/71.7.0
/// (A), 1.0.21/41/61.7.0 (W), 1.0.13.7.0 (PF), 1.0.14.7.0 (Hz), 0.0.96.14.0 (tariff), 0.0.1.0.0 (clock),
/// 1.0.99.1.0 (load profile: clock, +A, −A, voltage L1), 0.0.96.3.10 (disconnect control), 0.0.96.1.0 (serial).
/// </remarks>
public sealed class DlmsMeterSimulator : IAsyncDisposable
{
    private readonly Random _random;
    private readonly Lock _gate = new();
    private readonly CosemProfileGeneric _profile;
    private readonly double[] _voltage = [230, 230, 230];
    private readonly double[] _current = new double[3];
    private readonly double[] _phasePower = new double[3];
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private DateTimeOffset _lastStep;
    private DateTimeOffset _lastCapture;

    /// <summary>Creates the meter's objects on <paramref name="server"/>.</summary>
    /// <param name="server">The server to populate.</param>
    /// <param name="serialNumber">Meter serial number.</param>
    /// <param name="seed">Random seed.</param>
    /// <param name="clock">Time source (default: local time in UTC+07:00).</param>
    public DlmsMeterSimulator(DlmsServer server, string serialNumber = "IOT2026000017", int seed = 4, Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        Server = server;
        SerialNumber = serialNumber;
        _random = new Random(seed);
        Clock = clock ?? (() => DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7)));
        var o = ObisCode.Parse;
        var meterClock = server.Add(new CosemClock(o("0.0.1.0.0.255"), Clock));
        MeterClock = meterClock;
        server.Add(new CosemDataObject(o("0.0.42.0.0.255"), () => CosemData.OctetString(System.Text.Encoding.ASCII.GetBytes(("IOT" + serialNumber).PadRight(16, '0')[..16]))));
        server.Add(new CosemDataObject(o("0.0.96.1.0.255"), () => CosemData.VisibleString(serialNumber)));
        server.Add(new CosemDataObject(o("0.0.96.1.1.255"), () => CosemData.VisibleString("IoTCom.Net meter simulator")));
        server.Add(new CosemDataObject(o("1.0.0.2.0.255"), () => CosemData.VisibleString("1.4.2")));
        server.Add(new CosemDataObject(o("0.0.96.14.0.255"), () => CosemData.UInt8((byte)Tariff)));
        server.Add(new CosemDataObject(o("0.0.96.7.21.255"), () => CosemData.UInt16((ushort)PowerFailures)));

        server.Add(new CosemRegister(o("1.0.1.8.0.255"), () => ImportWh, 0, CosemUnit.WattHour));
        server.Add(new CosemRegister(o("1.0.1.8.1.255"), () => ImportTariffWh[0], 0, CosemUnit.WattHour));
        server.Add(new CosemRegister(o("1.0.1.8.2.255"), () => ImportTariffWh[1], 0, CosemUnit.WattHour));
        server.Add(new CosemRegister(o("1.0.2.8.0.255"), () => ExportWh, 0, CosemUnit.WattHour));
        server.Add(new CosemRegister(o("1.0.1.7.0.255"), () => ImportPower, 0, CosemUnit.Watt, CosemDataType.Int32));
        server.Add(new CosemRegister(o("1.0.2.7.0.255"), () => ExportPower, 0, CosemUnit.Watt, CosemDataType.Int32));
        string[] voltage = ["1.0.32.7.0.255", "1.0.52.7.0.255", "1.0.72.7.0.255"];
        string[] current = ["1.0.31.7.0.255", "1.0.51.7.0.255", "1.0.71.7.0.255"];
        string[] power = ["1.0.21.7.0.255", "1.0.41.7.0.255", "1.0.61.7.0.255"];
        for (var i = 0; i < 3; i++)
        {
            var phase = i;
            server.Add(new CosemRegister(o(voltage[i]), () => Voltage(phase), -1, CosemUnit.Volt, CosemDataType.UInt16));
            server.Add(new CosemRegister(o(current[i]), () => Current(phase), -2, CosemUnit.Ampere, CosemDataType.UInt16));
            server.Add(new CosemRegister(o(power[i]), () => PhasePower(phase), 0, CosemUnit.Watt, CosemDataType.Int32));
        }

        server.Add(new CosemRegister(o("1.0.13.7.0.255"), () => PowerFactor, -3, CosemUnit.Count, CosemDataType.Int16));
        server.Add(new CosemRegister(o("1.0.14.7.0.255"), () => Frequency, -2, CosemUnit.Hertz, CosemDataType.UInt16));

        Relay = server.Add(new CosemDisconnectControl(o("0.0.96.3.10.255")));
        _profile = server.Add(new CosemProfileGeneric(o("1.0.99.1.0.255"),
        [
            new CaptureObject(CosemClass.Clock, o("0.0.1.0.0.255")),
            new CaptureObject(CosemClass.Register, o("1.0.1.8.0.255")),
            new CaptureObject(CosemClass.Register, o("1.0.2.8.0.255")),
            new CaptureObject(CosemClass.Register, o("1.0.32.7.0.255")),
        ], 900, 2000));

        // Two days of history: run the load model backwards from the meter reading.
        var now = Clock();
        var start = Floor(now.AddDays(-2));
        ImportWh = 4_812_000;
        ExportWh = 1_206_000;
        ImportTariffWh[0] = 3_610_000;
        ImportTariffWh[1] = 1_202_000;
        for (var t = start; t <= Floor(now); t = t.AddMinutes(15))
        {
            Model(t, TimeSpan.FromMinutes(15));
            CaptureRow(t);
        }

        _lastStep = now;
        _lastCapture = Floor(now);
    }

    /// <summary>The server.</summary>
    public DlmsServer Server { get; }

    /// <summary>Serial number.</summary>
    public string SerialNumber { get; }

    /// <summary>Time source.</summary>
    public Func<DateTimeOffset> Clock { get; }

    /// <summary>The clock object (SET attribute 2 adjusts it).</summary>
    public CosemClock MeterClock { get; }

    /// <summary>The supply relay.</summary>
    public CosemDisconnectControl Relay { get; }

    /// <summary>Active energy import, total (Wh).</summary>
    public double ImportWh { get; private set; }

    /// <summary>Active energy import per tariff: [0] LWBP (off-peak), [1] WBP (peak 17:00–22:00).</summary>
    public double[] ImportTariffWh { get; } = new double[2];

    /// <summary>Active energy export (Wh), from the rooftop solar system.</summary>
    public double ExportWh { get; private set; }

    /// <summary>Active power import (W).</summary>
    public double ImportPower { get; private set; }

    /// <summary>Active power export (W).</summary>
    public double ExportPower { get; private set; }

    /// <summary>Household consumption (W).</summary>
    public double LoadPower { get; private set; }

    /// <summary>Solar production (W).</summary>
    public double SolarPower { get; private set; }

    /// <summary>Power factor.</summary>
    public double PowerFactor { get; private set; } = 0.96;

    /// <summary>Supply frequency (Hz).</summary>
    public double Frequency { get; private set; } = 50;

    /// <summary>Current tariff: 1 LWBP (off-peak), 2 WBP (peak).</summary>
    public int Tariff { get; private set; } = 1;

    /// <summary>Power failures counted.</summary>
    public int PowerFailures { get; private set; } = 3;

    /// <summary>Voltage of a phase (V).</summary>
    public double Voltage(int phase)
    {
        lock (_gate) return _voltage[phase];
    }

    /// <summary>Current of a phase (A).</summary>
    public double Current(int phase)
    {
        lock (_gate) return _current[phase];
    }

    /// <summary>Signed active power of a phase (W, negative when exporting).</summary>
    public double PhasePower(int phase)
    {
        lock (_gate) return _phasePower[phase];
    }

    /// <summary>The load profile object.</summary>
    public CosemProfileGeneric LoadProfile => _profile;

    /// <summary>Starts updating once per second.</summary>
    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _loop = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            try
            {
                while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) Step();
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);
    }

    /// <summary>Advances the model to the clock's current time (captures profile rows on 15-minute boundaries).</summary>
    public void Step()
    {
        var now = Clock();
        var dt = now - _lastStep;
        if (dt <= TimeSpan.Zero) return;
        _lastStep = now;
        Model(now, dt);
        for (var t = _lastCapture.AddMinutes(15); t <= now; t = t.AddMinutes(15))
        {
            CaptureRow(t);
            _lastCapture = t;
        }
    }

    private static DateTimeOffset Floor(DateTimeOffset t) => new(t.Year, t.Month, t.Day, t.Hour, t.Minute / 15 * 15, 0, t.Offset);

    private void CaptureRow(DateTimeOffset t) => _profile.Capture(
        CosemData.DateTime(t),
        CosemData.UInt32((uint)ImportWh),
        CosemData.UInt32((uint)ExportWh),
        CosemData.UInt16((ushort)Math.Round(Voltage(0) * 10)));

    private void Model(DateTimeOffset t, TimeSpan dt)
    {
        var h = t.Hour + (t.Minute / 60.0);
        double Bump(double centre, double width) => Math.Exp(-Math.Pow((h - centre) / width, 2));
        var load = 350 + (900 * Bump(6.5, 1.0)) + (1400 * Bump(13.5, 2.0)) + (2600 * Bump(19.5, 1.6)) + (_random.NextDouble() * 250);
        var solar = h is > 6 and < 18 ? 3000 * Math.Pow(Math.Sin((h - 6) / 12 * Math.PI), 1.6) * (0.8 + (_random.NextDouble() * 0.2)) : 0;
        if (!Relay.Connected) (load, solar) = (0, 0);
        var net = load - solar;
        lock (_gate)
        {
            LoadPower = load;
            SolarPower = solar;
            ImportPower = Math.Max(0, net);
            ExportPower = Math.Max(0, -net);
            Tariff = h is >= 17 and < 22 ? 2 : 1;
            PowerFactor = Relay.Connected ? 0.93 + (_random.NextDouble() * 0.05) : 1;
            Frequency = 50 + ((_random.NextDouble() - 0.5) * 0.08);
            double[] share = [0.42, 0.33, 0.25];
            for (var i = 0; i < 3; i++)
            {
                _phasePower[i] = net * share[i];
                _voltage[i] = Relay.Connected ? 231 - (Math.Abs(_phasePower[i]) / 600) + ((_random.NextDouble() - 0.5) * 1.6) : 0;
                _current[i] = _voltage[i] > 0 ? Math.Abs(_phasePower[i]) / (_voltage[i] * PowerFactor) : 0;
            }

            var importWh = ImportPower * dt.TotalHours;
            ImportWh += importWh;
            ImportTariffWh[Tariff - 1] += importWh;
            ExportWh += ExportPower * dt.TotalHours;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_cts is null) return;
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null) await _loop.ConfigureAwait(false);
        _cts.Dispose();
        _cts = null;
    }
}
