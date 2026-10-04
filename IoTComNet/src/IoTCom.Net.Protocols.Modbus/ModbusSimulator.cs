namespace IoTCom.Net.Protocols.Modbus;

/// <summary>Numeric encoding of a simulated register signal.</summary>
public enum ModbusValueType
{
    /// <summary>One register, unsigned.</summary>
    UInt16,
    /// <summary>One register, two's complement.</summary>
    Int16,
    /// <summary>Two registers, IEEE-754 float (big-endian word order).</summary>
    Float32,
    /// <summary>Two registers, signed 32-bit.</summary>
    Int32,
}

/// <summary>
/// Animates a <see cref="ModbusDataStore"/> so a <see cref="ModbusServer"/> behaves like a live device.
/// Deterministic when driven manually with <see cref="Tick"/> (tests, notebooks); periodic with <see cref="Start"/>.
/// </summary>
public sealed class ModbusSimulator : IAsyncDisposable
{
    private readonly List<Action<double, double>> _behaviors = [];
    private readonly Lock _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private double _time;

    /// <summary>Creates a simulator for <paramref name="store"/>.</summary>
    public ModbusSimulator(ModbusDataStore store, TimeSpan? interval = null)
    {
        Store = store ?? throw new ArgumentNullException(nameof(store));
        Interval = interval ?? TimeSpan.FromMilliseconds(250);
    }

    /// <summary>Animated store.</summary>
    public ModbusDataStore Store { get; }

    /// <summary>Update period when running.</summary>
    public TimeSpan Interval { get; }

    /// <summary>Simulated seconds elapsed.</summary>
    public double ElapsedSeconds { get { lock (_gate) return _time; } }

    /// <summary>True while the periodic loop runs.</summary>
    public bool IsRunning => _cts is not null;

    /// <summary>Drives a register from a function of time (seconds).</summary>
    public ModbusSimulator AddSignal(ModbusTable table, ushort address, Func<double, double> signal, ModbusValueType type = ModbusValueType.UInt16, double scale = 1)
    {
        if (table is not (ModbusTable.HoldingRegisters or ModbusTable.InputRegisters))
            throw new ArgumentException("Signals target register tables; use AddBit for coils/discrete inputs.", nameof(table));
        var bank = table == ModbusTable.HoldingRegisters ? Store.HoldingRegisters : Store.InputRegisters;
        return AddBehavior((t, _) => WriteValue(bank, address, signal(t) * scale, type));
    }

    /// <summary>Drives a bit from a function of time (seconds).</summary>
    public ModbusSimulator AddBit(ModbusTable table, ushort address, Func<double, bool> signal)
    {
        var bank = table switch
        {
            ModbusTable.Coils => Store.Coils,
            ModbusTable.DiscreteInputs => Store.DiscreteInputs,
            _ => throw new ArgumentException("Bits live in Coils or DiscreteInputs.", nameof(table)),
        };
        return AddBehavior((t, _) => bank[address] = signal(t));
    }

    /// <summary>Adds custom logic called on every tick with (time, delta) in seconds.</summary>
    public ModbusSimulator AddBehavior(Action<double, double> behavior)
    {
        ArgumentNullException.ThrowIfNull(behavior);
        lock (_gate) _behaviors.Add(behavior);
        return this;
    }

    /// <summary>Advances simulated time by <paramref name="deltaSeconds"/> and updates every signal.</summary>
    public void Tick(double deltaSeconds)
    {
        Action<double, double>[] behaviors;
        double t;
        lock (_gate)
        {
            _time += deltaSeconds;
            t = _time;
            behaviors = [.. _behaviors];
        }
        foreach (var b in behaviors) b(t, deltaSeconds);
    }

    /// <summary>Starts the periodic update loop.</summary>
    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Tick(0);
        _loop = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(Interval);
            var last = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                {
                    var now = System.Diagnostics.Stopwatch.GetTimestamp();
                    Tick(System.Diagnostics.Stopwatch.GetElapsedTime(last, now).TotalSeconds);
                    last = now;
                }
            }
            catch (OperationCanceledException) { }
        }, CancellationToken.None);
    }

    /// <summary>Stops the loop.</summary>
    public async ValueTask StopAsync()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts is null) return;
        await cts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null) await _loop.ConfigureAwait(false);
        cts.Dispose();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => StopAsync();

    private static void WriteValue(ModbusRegisterBank bank, ushort address, double value, ModbusValueType type)
    {
        switch (type)
        {
            case ModbusValueType.UInt16: bank[address] = (ushort)Math.Clamp(Math.Round(value), 0, ushort.MaxValue); break;
            case ModbusValueType.Int16: bank[address] = unchecked((ushort)(short)Math.Clamp(Math.Round(value), short.MinValue, short.MaxValue)); break;
            case ModbusValueType.Float32: bank.SetSingle(address, (float)value); break;
            case ModbusValueType.Int32: bank.SetInt32(address, (int)Math.Clamp(Math.Round(value), int.MinValue, int.MaxValue)); break;
        }
    }

    /// <summary>
    /// Creates the "virtual PLC" used by samples, notebooks and the Gallery. Register map:
    /// <list type="table">
    /// <item><term>IR 0</term><description>Temperature ×10 °C (sine around the setpoint)</description></item>
    /// <item><term>IR 1</term><description>Humidity ×10 %</description></item>
    /// <item><term>IR 2</term><description>Pressure hPa</description></item>
    /// <item><term>IR 3</term><description>Motor speed rpm (0 when the motor coil is off)</description></item>
    /// <item><term>IR 4–5</term><description>Power kW (float32)</description></item>
    /// <item><term>IR 6–7</term><description>Energy kWh (float32, accumulating)</description></item>
    /// <item><term>HR 0</term><description>Temperature setpoint ×10 °C (writable, default 250)</description></item>
    /// <item><term>HR 1</term><description>Production counter (increments while running)</description></item>
    /// <item><term>HR 2</term><description>Alarm word (bit0 high temp, bit1 e-stop)</description></item>
    /// <item><term>HR 10–21</term><description>Device name, ASCII (24 chars)</description></item>
    /// <item><term>Coil 0</term><description>Motor run command (writable, default on)</description></item>
    /// <item><term>Coil 1</term><description>Cooling pump (auto on above setpoint + 1 °C)</description></item>
    /// <item><term>DI 0</term><description>Door closed</description></item>
    /// <item><term>DI 1</term><description>Emergency stop healthy</description></item>
    /// <item><term>DI 2</term><description>High temperature alarm</description></item>
    /// </list>
    /// </summary>
    public static ModbusSimulator CreateVirtualPlc(ModbusDataStore store, TimeSpan? interval = null, int seed = 42)
    {
        var sim = new ModbusSimulator(store, interval);
        var rng = new Random(seed);
        store.HoldingRegisters[0] = 250;
        store.Coils[0] = true;
        store.DiscreteInputs[0] = true;
        store.DiscreteInputs[1] = true;
        var name = "IoTCom.Net Virtual PLC"u8;
        var nameRegs = new ushort[12];
        for (var i = 0; i < nameRegs.Length; i++)
        {
            var hi = i * 2 < name.Length ? name[i * 2] : (byte)0;
            var lo = i * 2 + 1 < name.Length ? name[i * 2 + 1] : (byte)0;
            nameRegs[i] = (ushort)(hi << 8 | lo);
        }
        store.HoldingRegisters.Write(10, nameRegs);

        double energy = 0, counterFraction = 0, speed = 0;
        sim.AddBehavior((t, dt) =>
        {
            var running = store.Coils[0];
            var setpoint = store.HoldingRegisters[0] / 10.0;
            var temperature = setpoint + 2.5 * Math.Sin(2 * Math.PI * t / 60) + (running ? 1.0 : -1.5) + (rng.NextDouble() - 0.5) * 0.2;
            var humidity = 55 + 8 * Math.Sin(2 * Math.PI * t / 90 + 1) + (rng.NextDouble() - 0.5);
            var pressure = 1013 + 3 * Math.Sin(2 * Math.PI * t / 300);
            var targetSpeed = running ? 1450 + 25 * Math.Sin(2 * Math.PI * t / 7) : 0;
            speed += (targetSpeed - speed) * Math.Min(1, dt * 2);
            var power = running ? Math.Max(0.05, 7.5 * (speed / 1450) + (rng.NextDouble() - 0.5) * 0.1) : 0.05;
            energy += power * dt / 3600;
            if (running)
            {
                counterFraction += dt * 2; // two parts per second
                var whole = (int)counterFraction;
                counterFraction -= whole;
                if (whole > 0) store.HoldingRegisters[1] = unchecked((ushort)(store.HoldingRegisters[1] + whole));
            }

            store.InputRegisters.Write(0, [(ushort)Math.Round(temperature * 10), (ushort)Math.Round(humidity * 10), (ushort)Math.Round(pressure), (ushort)Math.Round(speed)]);
            store.InputRegisters.SetSingle(4, (float)power);
            store.InputRegisters.SetSingle(6, (float)energy);

            var highTemp = temperature > setpoint + 3;
            var pump = temperature > setpoint + 1;
            if (store.Coils[1] != pump) store.Coils[1] = pump;
            store.DiscreteInputs[2] = highTemp;
            var alarm = (ushort)((highTemp ? 1 : 0) | (store.DiscreteInputs[1] ? 0 : 2));
            if (store.HoldingRegisters[2] != alarm) store.HoldingRegisters[2] = alarm;
        });
        return sim;
    }
}
