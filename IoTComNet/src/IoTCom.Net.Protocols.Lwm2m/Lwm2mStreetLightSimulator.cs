namespace IoTCom.Net.Protocols.Lwm2m;

/// <summary>
/// A smart street light as an LwM2M client: Device (3) with reboot, Location (6), the LED driver's Temperature (3303)
/// and Light Control (3311) with on/off, dimmer, on-time and cumulative energy. Writes to the dimmer are checked
/// (0–100 %); <see cref="Step"/> advances temperature, energy and on-time.
/// </summary>
public sealed class Lwm2mStreetLightSimulator : IAsyncDisposable
{
    private readonly Random _random;
    private readonly Lock _gate = new();
    private double _temperature = 31, _energy, _min = 31, _max = 31;
    private long _onTime;

    private Lwm2mStreetLightSimulator(Lwm2mClient client, string serial, double latitude, double longitude, int seed)
    {
        Client = client;
        _random = new Random(seed);
        Device = client.AddInstance(3)
            .Set(0, "IoTCom Simulated Lighting").Set(1, "SL-60 LED").Set(2, serial).Set(3, "2.4.1")
            .Set(6, 0, 1L).Set(9, 100L).Set(11, 0, 0L).Set(13, DateTimeOffset.UtcNow).Set(14, "+07:00").Set(15, "Asia/Jakarta").Set(16, "U").Set(17, "street light")
            .OnExecute(4, _ =>
            {
                Reboots++;
                Rebooted?.Invoke();
                return true;
            });
        Location = client.AddInstance(6).Set(0, latitude).Set(1, longitude).Set(2, 712.0).Set(5, DateTimeOffset.UtcNow);
        Temperature = client.AddInstance(3303).Set(5700, _temperature).Set(5601, _min).Set(5602, _max).Set(5701, "Cel")
            .OnExecute(5605, _ =>
            {
                lock (_gate) (_min, _max) = (_temperature, _temperature);
                Temperature!.Set(5601, _min).Set(5602, _max);
                return true;
            });
        Light = client.AddInstance(3311).Set(5850, false).Set(5851, 80L).Set(5852, 0L).Set(5805, 0.0).Set(5820, 0.95).Set(5706, "3000K")
            .OnWrite(5851, v => Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture) is >= 0 and <= 100);
    }

    /// <summary>Creates a light; configure the server, endpoint name and transport in <paramref name="configure"/>.</summary>
    public static Lwm2mStreetLightSimulator Create(Action<Lwm2mClientOptions> configure, string serial = "SL60-000417", double latitude = -6.9147, double longitude = 107.6098, int seed = 7)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return new Lwm2mStreetLightSimulator(Lwm2mClient.Create(o =>
        {
            o.EndpointName = $"urn:dev:light:{serial}";
            configure(o);
        }), serial, latitude, longitude, seed);
    }

    /// <summary>The LwM2M client.</summary>
    public Lwm2mClient Client { get; }

    /// <summary>Device object (3/0).</summary>
    public Lwm2mInstance Device { get; }

    /// <summary>Location object (6/0).</summary>
    public Lwm2mInstance Location { get; }

    /// <summary>Driver temperature (3303/0).</summary>
    public Lwm2mInstance Temperature { get; }

    /// <summary>Light control (3311/0).</summary>
    public Lwm2mInstance Light { get; }

    /// <summary>Reboots executed.</summary>
    public int Reboots { get; private set; }

    /// <summary>Raised when the server executes Reboot.</summary>
    public event Action? Rebooted;

    /// <summary>True when the lamp is on.</summary>
    public bool IsOn => Light.Get(5850) is true;

    /// <summary>Dimmer level in percent.</summary>
    public long Dimmer => Light.Get(5851) is long d ? d : 0;

    /// <summary>Registers with the server.</summary>
    public ValueTask StartAsync(CancellationToken ct = default) => Client.ConnectAsync(ct);

    /// <summary>Advances the light: driver temperature follows the load, energy and on-time accumulate.</summary>
    public void Step(double seconds)
    {
        double temperature, energy, min, max;
        long onTime;
        lock (_gate)
        {
            var load = IsOn ? Dimmer / 100.0 : 0;
            var target = 29 + (load * 24);
            _temperature += ((target - _temperature) * Math.Min(1, seconds / 30)) + ((_random.NextDouble() - 0.5) * 0.2);
            _energy += 60 * load * seconds / 3600;
            if (IsOn) _onTime += (long)Math.Round(seconds);
            (_min, _max) = (Math.Min(_min, _temperature), Math.Max(_max, _temperature));
            (temperature, energy, onTime, min, max) = (Math.Round(_temperature, 1), Math.Round(_energy, 3), _onTime, Math.Round(_min, 1), Math.Round(_max, 1));
        }

        Temperature.Set(5700, temperature).Set(5601, min).Set(5602, max);
        Light.Set(5805, energy).Set(5852, onTime);
        Device.Set(13, DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => Client.DisposeAsync();
}
