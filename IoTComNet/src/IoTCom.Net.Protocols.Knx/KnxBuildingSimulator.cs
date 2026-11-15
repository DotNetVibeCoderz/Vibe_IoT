namespace IoTCom.Net.Protocols.Knx;

/// <summary>One room of <see cref="KnxBuildingSimulator"/>.</summary>
public sealed class KnxRoom
{
    internal KnxRoom(int number, string name, double temperature) => (Number, Name, Temperature) = (number, name, temperature);

    /// <summary>Room number (the last part of its group addresses).</summary>
    public int Number { get; }

    /// <summary>Room name.</summary>
    public string Name { get; }

    /// <summary>Light on.</summary>
    public bool LightOn { get; internal set; }

    /// <summary>Dimming value, percent.</summary>
    public double Dim { get; internal set; }

    /// <summary>Blind position, percent closed.</summary>
    public double Blind { get; internal set; }

    /// <summary>Where the blind is moving to, percent closed.</summary>
    public double BlindTarget { get; internal set; }

    /// <summary>Room temperature, °C.</summary>
    public double Temperature { get; internal set; }

    /// <summary>Set point, °C.</summary>
    public double Setpoint { get; internal set; } = 21;

    /// <summary>HVAC mode (DPT 20.102: 1 comfort, 2 standby, 3 economy, 4 protection).</summary>
    public int Mode { get; internal set; } = 1;

    /// <summary>Heating valve, percent.</summary>
    public double Valve { get; internal set; }

    /// <summary>Presence detected.</summary>
    public bool Presence { get; internal set; }

    internal double LastSentTemperature = double.NaN;
    internal double SinceTemperature;
}

/// <summary>
/// An office floor on a KNX line: switching/dimming actuators, blind actuators, room controllers with heating valves,
/// presence detectors and a weather station, all answering group reads and writes on a <see cref="KnxBus"/> with the
/// group address plan in <see cref="GroupAddresses"/>.
/// </summary>
/// <remarks>
/// Plan (room n = 1…4): 1/0/n light switch, 1/1/n switch status, 1/2/n dim value, 1/3/n dim status; 2/0/n blind
/// up/down, 2/1/n blind position, 2/2/n position status; 3/0/n temperature, 3/1/n set point, 3/2/n HVAC mode, 3/3/n
/// valve; 4/0/n presence; 4/1/0 outdoor temperature, 4/1/1 outdoor illuminance.
/// </remarks>
public sealed class KnxBuildingSimulator
{
    private static readonly KnxIndividualAddress Lighting = new(1, 1, 1);
    private static readonly KnxIndividualAddress Blinds = new(1, 1, 2);
    private static readonly KnxIndividualAddress Weather = new(1, 1, 20);
    private readonly KnxBus _bus;
    private readonly Random _random;
    private readonly Dictionary<ushort, (KnxGroupAddressInfo Info, Func<object> Get, Action<object>? Set)> _objects = [];
    private double _sinceWeather;

    /// <summary>Attaches the floor to a line.</summary>
    public KnxBuildingSimulator(KnxBus bus, int seed = 7)
    {
        _bus = bus;
        _random = new Random(seed);
        Rooms = [new KnxRoom(1, "Meeting room", 22.4), new KnxRoom(2, "Open office", 23.1), new KnxRoom(3, "Director's office", 21.7), new KnxRoom(4, "Lobby", 24.0)];
        foreach (var r in Rooms) Define(r);
        Add(new KnxGroupAddress(4, 1, 0), "9.001", "Outdoor temperature", () => OutdoorTemperature, null, Weather);
        Add(new KnxGroupAddress(4, 1, 1), "9.004", "Outdoor illuminance", () => OutdoorLux, null, Weather);
        bus.Telegram += OnTelegram;
    }

    /// <summary>Rooms.</summary>
    public IReadOnlyList<KnxRoom> Rooms { get; }

    /// <summary>Outdoor temperature, °C.</summary>
    public double OutdoorTemperature { get; set; } = 31.5;

    /// <summary>Outdoor illuminance, lux.</summary>
    public double OutdoorLux { get; set; } = 42_000;

    /// <summary>Every group address of the floor with its datapoint type and name.</summary>
    public IReadOnlyList<KnxGroupAddressInfo> GroupAddresses => [.. _objects.Values.Select(o => o.Info).OrderBy(i => i.Address.Raw)];

    /// <summary>Raised after any change of a room.</summary>
    public event Action? Changed;

    private readonly Dictionary<ushort, KnxIndividualAddress> _owners = [];

    private void Add(KnxGroupAddress ga, string dpt, string name, Func<object> get, Action<object>? set, KnxIndividualAddress owner)
    {
        _objects[ga.Raw] = (new KnxGroupAddressInfo(ga, dpt, name), get, set);
        _owners[ga.Raw] = owner;
    }

    private void Define(KnxRoom r)
    {
        var n = r.Number;
        var controller = new KnxIndividualAddress(1, 1, 3 + n);
        Add(new(1, 0, n), "1.001", $"{r.Name} light", () => r.LightOn, v => SetLight(r, (bool)v), Lighting);
        Add(new(1, 1, n), "1.001", $"{r.Name} light status", () => r.LightOn, null, Lighting);
        Add(new(1, 2, n), "5.001", $"{r.Name} dim value", () => r.Dim, v => SetDim(r, (double)v), Lighting);
        Add(new(1, 3, n), "5.001", $"{r.Name} dim status", () => r.Dim, null, Lighting);
        Add(new(2, 0, n), "1.008", $"{r.Name} blind up/down", () => r.BlindTarget >= 50, v => r.BlindTarget = (bool)v ? 100 : 0, Blinds);
        Add(new(2, 1, n), "5.001", $"{r.Name} blind position", () => r.BlindTarget, v => r.BlindTarget = (double)v, Blinds);
        Add(new(2, 2, n), "5.001", $"{r.Name} blind position status", () => Math.Round(r.Blind), null, Blinds);
        Add(new(3, 0, n), "9.001", $"{r.Name} temperature", () => Math.Round(r.Temperature, 1), null, controller);
        Add(new(3, 1, n), "9.001", $"{r.Name} set point", () => r.Setpoint, v => r.Setpoint = Math.Clamp((double)v, 5, 35), controller);
        Add(new(3, 2, n), "20.102", $"{r.Name} HVAC mode", () => (double)r.Mode, v => r.Mode = (int)(double)v, controller);
        Add(new(3, 3, n), "5.001", $"{r.Name} heating valve", () => Math.Round(r.Valve), null, controller);
        Add(new(4, 0, n), "1.018", $"{r.Name} presence", () => r.Presence, null, new KnxIndividualAddress(1, 1, 10 + n));
    }

    /// <summary>Looks up the name and datapoint type of a group address.</summary>
    public KnxGroupAddressInfo? Find(KnxGroupAddress address) => _objects.TryGetValue(address.Raw, out var o) ? o.Info : null;

    private void SetLight(KnxRoom r, bool on)
    {
        r.LightOn = on;
        if (on && r.Dim == 0) r.Dim = 100;
        if (!on) r.Dim = 0;
        Status(r.Number, 1, 3);
    }

    private void SetDim(KnxRoom r, double dim)
    {
        r.Dim = dim;
        r.LightOn = dim > 0;
        Status(r.Number, 1, 3);
    }

    private void Status(int room, params int[] middles)
    {
        foreach (var m in middles) Transmit(new KnxGroupAddress(1, m, room));
    }

    private void Transmit(KnxGroupAddress ga, KnxApci service = KnxApci.GroupValueWrite)
    {
        if (!_objects.TryGetValue(ga.Raw, out var o)) return;
        _bus.Send(KnxCemiFrame.GroupTelegram(KnxMessageCode.DataIndication, _owners[ga.Raw], ga, service, KnxDpt.Encode(o.Info.Dpt, o.Get())), this);
    }

    private void OnTelegram(KnxCemiFrame f, object? origin)
    {
        if (ReferenceEquals(origin, this) || !f.IsGroup || !_objects.TryGetValue(f.Destination, out var o)) return;
        switch (f.Apci)
        {
            case KnxApci.GroupValueRead:
                Transmit(f.Group, KnxApci.GroupValueResponse);
                break;
            case KnxApci.GroupValueWrite when o.Set is not null:
                try
                {
                    o.Set(KnxDpt.Decode(o.Info.Dpt, f.Value));
                }
                catch (Exception ex) when (ex is ProtocolException or InvalidCastException or ArgumentException)
                {
                    return;   // a device ignores values it cannot decode
                }

                Changed?.Invoke();
                break;
        }
    }

    /// <summary>Advances the floor: blinds move, rooms heat or cool, presence changes, sensors send.</summary>
    public void Step(double seconds)
    {
        foreach (var r in Rooms)
        {
            if (Math.Abs(r.Blind - r.BlindTarget) > 0.01)
            {
                var stepPct = 5 * seconds;                    // a blind runs fully in 20 s
                r.Blind = r.Blind < r.BlindTarget ? Math.Min(r.BlindTarget, r.Blind + stepPct) : Math.Max(r.BlindTarget, r.Blind - stepPct);
                Transmit(new KnxGroupAddress(2, 2, r.Number));
            }

            var setpoint = r.Mode switch { 1 => r.Setpoint, 2 => r.Setpoint - 2, 3 => r.Setpoint - 4, _ => 7 };
            var valve = Math.Clamp((setpoint - r.Temperature) * 60, 0, 100);
            if (Math.Abs(valve - r.Valve) >= 5) { r.Valve = valve; Transmit(new KnxGroupAddress(3, 3, r.Number)); }
            var solar = (100 - r.Blind) / 100 * OutdoorLux / 100_000 * 0.6;
            var people = r.Presence ? 0.15 : 0;
            var cooling = r.Temperature > setpoint + 0.5 ? (r.Temperature - setpoint) * 0.5 : 0;   // the building's cooling
            r.Temperature += (r.Valve / 100 * 1.2 + solar + people - cooling - (r.Temperature - OutdoorTemperature) * 0.02) * seconds / 60;
            r.SinceTemperature += seconds;
            if (double.IsNaN(r.LastSentTemperature) || Math.Abs(r.Temperature - r.LastSentTemperature) >= 0.2 || r.SinceTemperature >= 300)
            {
                r.LastSentTemperature = r.Temperature;
                r.SinceTemperature = 0;
                Transmit(new KnxGroupAddress(3, 0, r.Number));
            }

            if (_random.NextDouble() < 0.01 * seconds)
            {
                r.Presence = !r.Presence;
                Transmit(new KnxGroupAddress(4, 0, r.Number));
            }
        }

        _sinceWeather += seconds;
        if (_sinceWeather >= 600)
        {
            _sinceWeather = 0;
            Transmit(new KnxGroupAddress(4, 1, 0));
            Transmit(new KnxGroupAddress(4, 1, 1));
        }

        Changed?.Invoke();
    }
}
