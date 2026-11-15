namespace IoTCom.Net.Protocols.Matter;

/// <summary>
/// A Matter bridge (core spec §9.13): an Aggregator endpoint whose PartsList holds one endpoint per bridged device, each
/// with Bridged Device Basic Information (label, reachable, unique id) and the device's own clusters. Use it to expose
/// devices on other protocols — KNX, Modbus, Zigbee, a proprietary bus — to Matter controllers.
/// </summary>
public sealed class MatterBridge
{
    private readonly MatterDevice _device;
    private ushort _next;

    /// <summary>Adds an Aggregator endpoint to <paramref name="device"/>.</summary>
    public MatterBridge(MatterDevice device, ushort aggregatorEndpoint = 1)
    {
        _device = device;
        Aggregator = device.AddEndpoint(new MatterEndpoint(aggregatorEndpoint, (MatterIds.Aggregator, 1)));
        _next = (ushort)(aggregatorEndpoint + 1);
    }

    /// <summary>The Aggregator endpoint.</summary>
    public MatterEndpoint Aggregator { get; }

    /// <summary>Adds a bridged device of <paramref name="deviceType"/>; add its application clusters to the returned endpoint.</summary>
    public (MatterEndpoint Endpoint, MatterClusters.BridgedInfoCluster Info) AddDevice(string label, string uniqueId, uint deviceType, ushort deviceTypeRevision = 1)
    {
        var ep = new MatterEndpoint(_next++, (deviceType, deviceTypeRevision), (MatterIds.BridgedNode, 2)) { Parent = Aggregator.Id };
        var info = ep.Add(new MatterClusters.BridgedInfoCluster(label, uniqueId));
        _device.AddEndpoint(ep);
        return (ep, info);
    }

    /// <summary>Adds a bridged on/off light.</summary>
    public (MatterEndpoint Endpoint, MatterClusters.OnOffCluster OnOff, MatterClusters.BridgedInfoCluster Info) AddLight(string label, string uniqueId)
    {
        var (ep, info) = AddDevice(label, uniqueId, MatterIds.OnOffLight, 3);
        return (ep, ep.Add(new MatterClusters.OnOffCluster()), info);
    }

    /// <summary>Adds a bridged temperature sensor.</summary>
    public (MatterEndpoint Endpoint, MatterClusters.TemperatureCluster Temperature, MatterClusters.BridgedInfoCluster Info) AddTemperatureSensor(string label, string uniqueId, double celsius)
    {
        var (ep, info) = AddDevice(label, uniqueId, MatterIds.TemperatureSensor, 2);
        return (ep, ep.Add(new MatterClusters.TemperatureCluster(celsius)), info);
    }
}

/// <summary>
/// A simulated smart-home bridge for a house: four bridged lights (living room, kitchen, terrace, bedroom), two
/// temperature sensors (living room, terrace) and a dimmable desk lamp on its own endpoint. <see cref="Step"/> moves the
/// temperatures with the time of day; <see cref="SetReachable"/> takes a bridged device off line.
/// </summary>
public sealed class MatterBridgeSimulator
{
    private double _clock;

    /// <summary>Builds the bridge on <paramref name="device"/> (not started).</summary>
    public MatterBridgeSimulator(MatterDevice device)
    {
        Device = device;
        Bridge = new MatterBridge(device);
        foreach (var (label, id) in new[] { ("Living room", "light-living"), ("Kitchen", "light-kitchen"), ("Terrace", "light-terrace"), ("Bedroom", "light-bedroom") })
        {
            var (ep, onOff, info) = Bridge.AddLight(label, id);
            Lights.Add((label, ep.Id, onOff, info));
        }

        var living = Bridge.AddTemperatureSensor("Living room temperature", "temp-living", 23.5);
        var terrace = Bridge.AddTemperatureSensor("Terrace temperature", "temp-terrace", 29.0);
        Sensors.Add(("Living room", living.Endpoint.Id, living.Temperature, living.Info));
        Sensors.Add(("Terrace", terrace.Endpoint.Id, terrace.Temperature, terrace.Info));
        var lampEp = device.AddEndpoint(new MatterEndpoint(20, (MatterIds.DimmableLight, 3)));
        LampOnOff = lampEp.Add(new MatterClusters.OnOffCluster());
        Lamp = lampEp.Add(new MatterClusters.LevelControlCluster(180, LampOnOff));
    }

    /// <summary>The device.</summary>
    public MatterDevice Device { get; }

    /// <summary>The bridge.</summary>
    public MatterBridge Bridge { get; }

    /// <summary>Bridged lights.</summary>
    public List<(string Label, ushort Endpoint, MatterClusters.OnOffCluster OnOff, MatterClusters.BridgedInfoCluster Info)> Lights { get; } = [];

    /// <summary>Bridged temperature sensors.</summary>
    public List<(string Label, ushort Endpoint, MatterClusters.TemperatureCluster Temperature, MatterClusters.BridgedInfoCluster Info)> Sensors { get; } = [];

    /// <summary>Desk lamp level (endpoint 20).</summary>
    public MatterClusters.LevelControlCluster Lamp { get; }

    /// <summary>Desk lamp on/off (endpoint 20).</summary>
    public MatterClusters.OnOffCluster LampOnOff { get; }

    /// <summary>Takes a bridged device off line (or back).</summary>
    public void SetReachable(ushort endpoint, bool reachable)
    {
        foreach (var l in Lights.Where(l => l.Endpoint == endpoint)) l.Info.Reachable = reachable;
        foreach (var s in Sensors.Where(s => s.Endpoint == endpoint)) s.Info.Reachable = reachable;
    }

    /// <summary>Advances the simulated clock (temperatures follow a daily curve).</summary>
    public void Step(double seconds)
    {
        _clock += seconds;
        var day = Math.Sin(_clock / 600.0);
        Sensors[0].Temperature.Celsius = Math.Round(23.5 + (0.8 * day), 2);
        Sensors[1].Temperature.Celsius = Math.Round(29.0 + (3.5 * day), 2);
    }
}
