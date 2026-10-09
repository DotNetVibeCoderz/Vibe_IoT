namespace IoTCom.Net.Protocols.Iec104;

/// <summary>
/// A 20 kV feeder bay behind an IEC 104 RTU: circuit breaker, busbar disconnector and earthing switch with interlocks,
/// protection trip, busbar voltage, feeder current, active/reactive power, frequency, transformer oil temperature, an
/// on-load tap changer and an energy counter. Commands follow the usual rules: the breaker needs select-before-operate,
/// switches only move when the interlocks allow it, and a protection trip opens the breaker until it is reset.
/// </summary>
public sealed class Iec104SubstationSimulator : IAsyncDisposable
{
    /// <summary>Point addresses.</summary>
    public static class Ioa
    {
        /// <summary>Q0 circuit breaker position (double point).</summary>
        public const uint Breaker = 1001;
        /// <summary>Q1 busbar disconnector position (double point).</summary>
        public const uint Disconnector = 1002;
        /// <summary>Q8 earthing switch position (double point).</summary>
        public const uint EarthingSwitch = 1003;
        /// <summary>Protection trip (single point).</summary>
        public const uint ProtectionTrip = 1101;
        /// <summary>SF6 gas pressure low (single point).</summary>
        public const uint GasPressureLow = 1102;
        /// <summary>Bay in remote control (single point).</summary>
        public const uint Remote = 1103;
        /// <summary>Busbar voltage in kV (float).</summary>
        public const uint Voltage = 2001;
        /// <summary>Feeder current in A (float).</summary>
        public const uint Current = 2002;
        /// <summary>Active power in MW (float).</summary>
        public const uint ActivePower = 2003;
        /// <summary>Reactive power in Mvar (float).</summary>
        public const uint ReactivePower = 2004;
        /// <summary>Frequency in Hz (float).</summary>
        public const uint Frequency = 2005;
        /// <summary>Transformer oil temperature in °C (scaled).</summary>
        public const uint OilTemperature = 2006;
        /// <summary>Tap changer position (step position).</summary>
        public const uint TapPosition = 2007;
        /// <summary>Active energy import in kWh (integrated totals).</summary>
        public const uint Energy = 3001;
        /// <summary>Breaker command (C_DC_NA_1, select-before-operate).</summary>
        public const uint BreakerCommand = 5001;
        /// <summary>Disconnector command (C_DC_NA_1).</summary>
        public const uint DisconnectorCommand = 5002;
        /// <summary>Earthing switch command (C_DC_NA_1).</summary>
        public const uint EarthingCommand = 5003;
        /// <summary>Tap changer command (C_RC_NA_1).</summary>
        public const uint TapCommand = 5004;
        /// <summary>Protection reset (C_SC_NA_1).</summary>
        public const uint TripReset = 5005;
        /// <summary>Reactive power set point in Mvar (C_SE_NC_1).</summary>
        public const uint ReactiveSetpoint = 6001;
    }

    private readonly Random _random = new(104);
    private readonly Lock _gate = new();
    private double _hours = 9.5, _reactiveTarget = 1.2, _energy = 1_284_350, _oil = 52;
    private int _tap;

    private Iec104SubstationSimulator(Iec104Server server)
    {
        Server = server;
        server.Define(Ioa.Breaker, Iec104TypeId.DoublePoint, 2, "Q0 circuit breaker", group: 1);
        server.Define(Ioa.Disconnector, Iec104TypeId.DoublePoint, 2, "Q1 busbar disconnector", group: 1);
        server.Define(Ioa.EarthingSwitch, Iec104TypeId.DoublePoint, 1, "Q8 earthing switch", group: 1);
        server.Define(Ioa.ProtectionTrip, Iec104TypeId.SinglePoint, 0, "Protection trip", group: 1);
        server.Define(Ioa.GasPressureLow, Iec104TypeId.SinglePoint, 0, "SF6 gas pressure low", group: 1);
        server.Define(Ioa.Remote, Iec104TypeId.SinglePoint, 1, "Remote control enabled", group: 1);
        server.Define(Ioa.Voltage, Iec104TypeId.MeasuredFloat, 20.1, "Busbar voltage (kV)", group: 2);
        server.Define(Ioa.Current, Iec104TypeId.MeasuredFloat, 0, "Feeder current (A)", group: 2);
        server.Define(Ioa.ActivePower, Iec104TypeId.MeasuredFloat, 0, "Active power (MW)", group: 2);
        server.Define(Ioa.ReactivePower, Iec104TypeId.MeasuredFloat, 0, "Reactive power (Mvar)", group: 2);
        server.Define(Ioa.Frequency, Iec104TypeId.MeasuredFloat, 50, "Frequency (Hz)", group: 2);
        server.Define(Ioa.OilTemperature, Iec104TypeId.MeasuredScaled, 52, "Transformer oil temperature (°C)", group: 2);
        server.Define(Ioa.TapPosition, Iec104TypeId.StepPosition, 0, "Tap changer position", group: 2);
        server.Define(Ioa.Energy, Iec104TypeId.IntegratedTotals, _energy, "Active energy import (kWh)");
        server.MapCommand(Ioa.BreakerCommand, Iec104TypeId.DoubleCommand, OnBreaker);
        server.MapCommand(Ioa.DisconnectorCommand, Iec104TypeId.DoubleCommand, c => OnSwitch(c, Ioa.Disconnector));
        server.MapCommand(Ioa.EarthingCommand, Iec104TypeId.DoubleCommand, c => OnSwitch(c, Ioa.EarthingSwitch));
        server.MapCommand(Ioa.TapCommand, Iec104TypeId.RegulatingStep, OnTap);
        server.MapCommand(Ioa.TripReset, Iec104TypeId.SingleCommand, OnReset);
        server.MapCommand(Ioa.ReactiveSetpoint, Iec104TypeId.SetpointFloat, OnSetpoint);
        Step(0);
    }

    /// <summary>Creates the simulator on a server configured by <paramref name="configure"/> (listener, common address…).</summary>
    public static Iec104SubstationSimulator Create(Action<Iec104ServerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return new Iec104SubstationSimulator(Iec104Server.Create(o =>
        {
            o.RequireSelectBeforeOperate = false;
            configure(o);
        }));
    }

    /// <summary>The RTU.</summary>
    public Iec104Server Server { get; }

    /// <summary>Time the breaker takes to move (default 80 ms; it reports "intermediate" meanwhile).</summary>
    public TimeSpan BreakerTravel { get; set; } = TimeSpan.FromMilliseconds(80);

    /// <summary>Simulated time of day in hours (drives the load curve).</summary>
    public double HourOfDay => _hours;

    private double Get(uint ioa) => Server.Points[ioa].Value;

    /// <summary>Starts the RTU.</summary>
    public ValueTask StartAsync(CancellationToken ct = default) => Server.StartAsync(ct);

    private bool OnBreaker(Iec104Command c)
    {
        var close = c.Command.DoublePoint == Iec104DoublePoint.On;
        if (c.Command.DoublePoint is Iec104DoublePoint.Intermediate or Iec104DoublePoint.Indeterminate) return false;
        if (close && (Get(Ioa.ProtectionTrip) != 0 || Get(Ioa.EarthingSwitch) == 2 || Get(Ioa.Disconnector) != 2 || Get(Ioa.GasPressureLow) != 0)) return false;
        if (!c.IsSelect && !c.Command.Select) _ = MoveBreakerAsync(close);
        return true;
    }

    private async Task MoveBreakerAsync(bool close)
    {
        Server.Update(Ioa.Breaker, 0, cause: Iec104Cause.ReturnRemote);
        await Task.Delay(BreakerTravel).ConfigureAwait(false);
        Server.Update(Ioa.Breaker, close ? 2 : 1, cause: Iec104Cause.ReturnRemote);
        Step(0);
    }

    private bool OnSwitch(Iec104Command c, uint position)
    {
        var close = c.Command.DoublePoint == Iec104DoublePoint.On;
        if (c.Command.DoublePoint is Iec104DoublePoint.Intermediate or Iec104DoublePoint.Indeterminate) return false;
        if (Get(Ioa.Breaker) != 1) return false;                                                    // switches never break load
        if (position == Ioa.EarthingSwitch && close && Get(Ioa.Disconnector) != 1) return false;   // earth only an isolated feeder
        if (position == Ioa.Disconnector && close && Get(Ioa.EarthingSwitch) != 1) return false;   // never connect an earthed feeder
        if (!c.IsSelect) Server.Update(position, close ? 2 : 1, cause: Iec104Cause.ReturnRemote);
        return true;
    }

    private bool OnTap(Iec104Command c)
    {
        var higher = c.Command.DoublePoint == Iec104DoublePoint.On;
        lock (_gate)
        {
            var next = _tap + (higher ? 1 : -1);
            if (c.Command.DoublePoint is Iec104DoublePoint.Intermediate or Iec104DoublePoint.Indeterminate || next is < -8 or > 8) return false;
            if (c.IsSelect) return true;
            _tap = next;
        }

        Server.Update(Ioa.TapPosition, _tap, cause: Iec104Cause.ReturnRemote);
        Step(0);
        return true;
    }

    private bool OnReset(Iec104Command c)
    {
        if (!c.Command.IsOn) return false;
        if (!c.IsSelect) Server.Update(Ioa.ProtectionTrip, 0, cause: Iec104Cause.ReturnRemote);
        return true;
    }

    private bool OnSetpoint(Iec104Command c)
    {
        if (c.Command.Value is < -5 or > 5 || double.IsNaN(c.Command.Value)) return false;
        if (!c.IsSelect) lock (_gate) _reactiveTarget = c.Command.Value;
        return true;
    }

    /// <summary>A short circuit on the feeder: protection trips and the breaker opens.</summary>
    public void Trip()
    {
        Server.Update(Ioa.ProtectionTrip, 1);
        Server.Update(Ioa.Breaker, 1);
        Step(0);
    }

    /// <summary>Sets the SF6 gas pressure alarm (the breaker then refuses to close).</summary>
    public void SetGasPressureLow(bool low) => Server.Update(Ioa.GasPressureLow, low ? 1 : 0);

    /// <summary>Advances the simulation: load follows the time of day, measurements move, energy accumulates.</summary>
    public void Step(double seconds)
    {
        lock (_gate)
        {
            _hours = (_hours + (seconds / 60.0)) % 24;   // one simulated hour per real minute
            var closed = Get(Ioa.Breaker) == 2;
            var curve = 0.55 + (0.35 * Math.Sin((_hours - 8) / 24 * 2 * Math.PI)) + (_hours is > 17 and < 22 ? 0.25 : 0);
            var p = closed ? Math.Round((6.5 * curve) + ((_random.NextDouble() - 0.5) * 0.08), 3) : 0;
            var q = closed ? Math.Round(_reactiveTarget + ((_random.NextDouble() - 0.5) * 0.04), 3) : 0;
            var v = Math.Round((20.0 * (1 + (_tap * 0.0125))) - (closed ? p * 0.06 : 0) + ((_random.NextDouble() - 0.5) * 0.02), 3);
            var i = closed ? Math.Round(Math.Sqrt((p * p) + (q * q)) * 1000 / (Math.Sqrt(3) * v), 1) : 0;
            var f = Math.Round(50 + ((_random.NextDouble() - 0.5) * 0.04), 3);
            _energy += p * 1000 * seconds / 3600 * 60;       // kWh at simulated speed
            _oil += ((38 + (p * 6) - _oil) * Math.Min(1, seconds / 120));
            Deadband(Ioa.ActivePower, p, 0.05);
            Deadband(Ioa.ReactivePower, q, 0.05);
            Deadband(Ioa.Voltage, v, 0.05);
            Deadband(Ioa.Current, i, 2);
            Deadband(Ioa.Frequency, f, 0.01);
            Deadband(Ioa.OilTemperature, Math.Round(_oil), 1);
            Server.IncrementCounter(Ioa.Energy, Math.Floor(_energy) - Get(Ioa.Energy));
        }
    }

    private void Deadband(uint ioa, double value, double band)
    {
        if (Math.Abs(Get(ioa) - value) >= band || (value == 0 && Get(ioa) != 0)) Server.Update(ioa, value);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => Server.DisposeAsync();
}
