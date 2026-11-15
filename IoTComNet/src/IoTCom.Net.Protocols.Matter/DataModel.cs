namespace IoTCom.Net.Protocols.Matter;

/// <summary>Who is asking: the session a request arrived on.</summary>
public sealed class MatterRequestContext
{
    internal MatterRequestContext(bool isPase, byte? fabricIndex, ulong? peerNodeId, byte[]? attestationChallenge)
        => (IsPase, FabricIndex, PeerNodeId, AttestationChallenge) = (isPase, fabricIndex, peerNodeId, attestationChallenge);

    /// <summary>True on the PASE commissioning session.</summary>
    public bool IsPase { get; }

    /// <summary>Fabric index of a CASE session (or of the fabric added during commissioning).</summary>
    public byte? FabricIndex { get; internal set; }

    /// <summary>Peer operational node id (CASE).</summary>
    public ulong? PeerNodeId { get; }

    internal byte[]? AttestationChallenge { get; }
}

/// <summary>A command's outcome: a status, or a response command with fields.</summary>
/// <param name="Status">Status (Success with a response command).</param>
/// <param name="ResponseCommand">Response command id, if any.</param>
/// <param name="Fields">Response fields.</param>
/// <param name="ClusterStatus">Cluster-specific status.</param>
public readonly record struct CommandResult(MatterStatus Status, uint? ResponseCommand = null, TlvElement? Fields = null, byte? ClusterStatus = null)
{
    /// <summary>Success without a response command.</summary>
    public static CommandResult Ok => new(MatterStatus.Success);

    /// <summary>A response command.</summary>
    public static CommandResult Respond(uint command, params TlvElement[] fields) => new(MatterStatus.Success, command, TlvElement.Structure(TlvTag.Anonymous, fields));
}

/// <summary>Handles a command.</summary>
public delegate CommandResult MatterCommandHandler(TlvElement fields, MatterRequestContext context);

/// <summary>A cluster on an endpoint: attributes (with optional write handlers), commands and a data version.</summary>
public class MatterCluster
{
    private readonly Dictionary<uint, (Func<MatterRequestContext?, TlvElement> Get, Func<TlvElement, MatterRequestContext, MatterStatus>? Set)> _attributes = [];
    private readonly Dictionary<uint, MatterCommandHandler> _commands = [];
    private readonly HashSet<uint> _generated = [];

    /// <summary>Creates a cluster.</summary>
    public MatterCluster(uint id, string name, ushort revision = 1, uint featureMap = 0)
    {
        (Id, Name, Revision, FeatureMap) = (id, name, revision, featureMap);
        DataVersion = (uint)Random.Shared.Next();
    }

    /// <summary>Cluster id.</summary>
    public uint Id { get; }

    /// <summary>Name.</summary>
    public string Name { get; }

    /// <summary>Cluster revision.</summary>
    public ushort Revision { get; }

    /// <summary>Feature map.</summary>
    public uint FeatureMap { get; }

    /// <summary>Data version, incremented on every change.</summary>
    public uint DataVersion { get; private set; }

    /// <summary>Raised after a change (with the attribute id).</summary>
    public event Action<MatterCluster, uint>? Changed;

    /// <summary>Attribute ids, including the global ones.</summary>
    public IEnumerable<uint> AttributeIds => _attributes.Keys.Concat([0xFFF8u, 0xFFF9u, 0xFFFBu, 0xFFFCu, 0xFFFDu]);

    /// <summary>Accepted command ids.</summary>
    public IEnumerable<uint> CommandIds => _commands.Keys;

    /// <summary>Adds an attribute (writable when <paramref name="set"/> is given).</summary>
    public MatterCluster Attribute(uint id, Func<TlvElement> get, Func<TlvElement, MatterStatus>? set = null)
    {
        _attributes[id] = (_ => get(), set is null ? null : (v, _) => set(v));
        return this;
    }

    /// <summary>Adds an attribute whose value depends on the requester (fabric-scoped lists).</summary>
    public MatterCluster Attribute(uint id, Func<MatterRequestContext?, TlvElement> get, Func<TlvElement, MatterRequestContext, MatterStatus>? set = null)
    {
        _attributes[id] = (get, set);
        return this;
    }

    /// <summary>Adds a command; <paramref name="responseCommand"/> lists the response command it generates.</summary>
    public MatterCluster Command(uint id, MatterCommandHandler handler, uint? responseCommand = null)
    {
        _commands[id] = handler;
        if (responseCommand is { } r) _generated.Add(r);
        return this;
    }

    /// <summary>Marks an attribute changed (bumps the data version and notifies subscriptions).</summary>
    public void NotifyChanged(uint attribute)
    {
        DataVersion++;
        Changed?.Invoke(this, attribute);
    }

    internal (TlvElement? Value, MatterStatus Status) Read(uint attribute, MatterRequestContext? context)
    {
        if (_attributes.TryGetValue(attribute, out var a)) return (a.Get(context), MatterStatus.Success);
        return attribute switch
        {
            0xFFFD => (TlvElement.UInt(TlvTag.Anonymous, Revision), MatterStatus.Success),
            0xFFFC => (TlvElement.UInt(TlvTag.Anonymous, FeatureMap), MatterStatus.Success),
            0xFFFB => (TlvElement.Array(TlvTag.Anonymous, [.. AttributeIds.Order().Select(i => TlvElement.UInt(TlvTag.Anonymous, i))]), MatterStatus.Success),
            0xFFF9 => (TlvElement.Array(TlvTag.Anonymous, [.. _commands.Keys.Order().Select(i => TlvElement.UInt(TlvTag.Anonymous, i))]), MatterStatus.Success),
            0xFFF8 => (TlvElement.Array(TlvTag.Anonymous, [.. _generated.Order().Select(i => TlvElement.UInt(TlvTag.Anonymous, i))]), MatterStatus.Success),
            _ => (null, MatterStatus.UnsupportedAttribute),
        };
    }

    internal MatterStatus Write(uint attribute, TlvElement value, MatterRequestContext context)
    {
        if (!_attributes.TryGetValue(attribute, out var a)) return MatterStatus.UnsupportedAttribute;
        if (a.Set is null) return MatterStatus.UnsupportedWrite;
        MatterStatus status;
        try
        {
            status = a.Set(value, context);
        }
        catch (ProtocolException)
        {
            status = MatterStatus.InvalidDataType;
        }

        if (status == MatterStatus.Success) NotifyChanged(attribute);
        return status;
    }

    internal CommandResult Invoke(uint command, TlvElement fields, MatterRequestContext context)
    {
        if (!_commands.TryGetValue(command, out var h)) return new CommandResult(MatterStatus.UnsupportedCommand);
        try
        {
            return h(fields, context);
        }
        catch (ProtocolException)
        {
            return new CommandResult(MatterStatus.InvalidCommand);
        }
    }
}

/// <summary>An endpoint: device types and clusters.</summary>
public sealed class MatterEndpoint
{
    private readonly Dictionary<uint, MatterCluster> _clusters = [];

    /// <summary>Creates an endpoint.</summary>
    public MatterEndpoint(ushort id, params (uint Type, ushort Revision)[] deviceTypes) => (Id, DeviceTypes) = (id, deviceTypes);

    /// <summary>Endpoint number.</summary>
    public ushort Id { get; }

    /// <summary>Device types.</summary>
    public IReadOnlyList<(uint Type, ushort Revision)> DeviceTypes { get; }

    /// <summary>Endpoint whose PartsList includes this one (aggregator for bridged devices; 0 otherwise).</summary>
    public ushort? Parent { get; init; }

    /// <summary>Clusters.</summary>
    public IReadOnlyCollection<MatterCluster> Clusters => _clusters.Values;

    /// <summary>Adds a cluster.</summary>
    public T Add<T>(T cluster) where T : MatterCluster
    {
        _clusters[cluster.Id] = cluster;
        ClusterAdded?.Invoke(cluster);
        return cluster;
    }

    internal event Action<MatterCluster>? ClusterAdded;

    /// <summary>A cluster by id.</summary>
    public MatterCluster? Cluster(uint id) => _clusters.GetValueOrDefault(id);
}

/// <summary>Cluster and device-type ids used by this package.</summary>
public static class MatterIds
{
    /// <summary>On/Off.</summary>
    public const uint OnOff = 0x0006;
    /// <summary>Level Control.</summary>
    public const uint LevelControl = 0x0008;
    /// <summary>Descriptor.</summary>
    public const uint Descriptor = 0x001D;
    /// <summary>Access Control.</summary>
    public const uint AccessControl = 0x001F;
    /// <summary>Basic Information.</summary>
    public const uint BasicInformation = 0x0028;
    /// <summary>General Commissioning.</summary>
    public const uint GeneralCommissioning = 0x0030;
    /// <summary>Bridged Device Basic Information.</summary>
    public const uint BridgedDeviceBasicInformation = 0x0039;
    /// <summary>Operational Credentials.</summary>
    public const uint OperationalCredentials = 0x003E;
    /// <summary>Temperature Measurement.</summary>
    public const uint TemperatureMeasurement = 0x0402;

    /// <summary>Root node device type.</summary>
    public const uint RootNode = 0x0016;
    /// <summary>Aggregator (bridge) device type.</summary>
    public const uint Aggregator = 0x000E;
    /// <summary>Bridged node device type.</summary>
    public const uint BridgedNode = 0x0013;
    /// <summary>On/Off light.</summary>
    public const uint OnOffLight = 0x0100;
    /// <summary>Dimmable light.</summary>
    public const uint DimmableLight = 0x0101;
    /// <summary>Temperature sensor.</summary>
    public const uint TemperatureSensor = 0x0302;
}

/// <summary>Application clusters with their state.</summary>
public static class MatterClusters
{
    private static TlvElement U(ulong v) => TlvElement.UInt(TlvTag.Anonymous, v);

    /// <summary>On/Off with Off, On and Toggle.</summary>
    public sealed class OnOffCluster : MatterCluster
    {
        private bool _on;

        /// <summary>Creates the cluster.</summary>
        public OnOffCluster(bool on = false) : base(MatterIds.OnOff, "On/Off", 6)
        {
            _on = on;
            Attribute(0, () => TlvElement.Bool(TlvTag.Anonymous, _on));
            Command(0, (_, _) => Set(false));
            Command(1, (_, _) => Set(true));
            Command(2, (_, _) => Set(!_on));
        }

        /// <summary>Current state.</summary>
        public bool On
        {
            get => _on;
            set => Set(value);
        }

        private CommandResult Set(bool value)
        {
            if (_on != value)
            {
                _on = value;
                NotifyChanged(0);
            }

            return CommandResult.Ok;
        }
    }

    /// <summary>Level Control with MoveToLevel and MoveToLevelWithOnOff (transitions are immediate).</summary>
    public sealed class LevelControlCluster : MatterCluster
    {
        private byte _level;
        private readonly OnOffCluster? _onOff;

        /// <summary>Creates the cluster (linked to an On/Off cluster for the WithOnOff variant).</summary>
        public LevelControlCluster(byte level = 254, OnOffCluster? onOff = null) : base(MatterIds.LevelControl, "Level Control", 5, featureMap: 0x03)
        {
            (_level, _onOff) = (level, onOff);
            Attribute(0, () => U(_level));
            Attribute(2, () => U(1));
            Attribute(3, () => U(254));
            Attribute(0x0F, () => U(0), v => v.AsUInt() <= 255 ? MatterStatus.Success : MatterStatus.ConstraintError);
            Command(0, (f, _) => Move(f, withOnOff: false));
            Command(4, (f, _) => Move(f, withOnOff: true));
        }

        /// <summary>Current level (1–254).</summary>
        public byte Level => _level;

        private CommandResult Move(TlvElement f, bool withOnOff)
        {
            var level = f.Required(0).AsUInt();
            if (level is < 1 or > 254) return new CommandResult(MatterStatus.ConstraintError);
            if (_level != (byte)level)
            {
                _level = (byte)level;
                NotifyChanged(0);
            }

            if (withOnOff && _onOff is not null) _onOff.On = true;
            return CommandResult.Ok;
        }
    }

    /// <summary>Temperature Measurement (0.01 °C).</summary>
    public sealed class TemperatureCluster : MatterCluster
    {
        private short _value;

        /// <summary>Creates the cluster.</summary>
        public TemperatureCluster(double celsius = 21) : base(MatterIds.TemperatureMeasurement, "Temperature Measurement", 4)
        {
            _value = (short)Math.Round(celsius * 100);
            Attribute(0, () => TlvElement.Int(TlvTag.Anonymous, _value));
            Attribute(1, () => TlvElement.Int(TlvTag.Anonymous, -4000));
            Attribute(2, () => TlvElement.Int(TlvTag.Anonymous, 8500));
        }

        /// <summary>Measured value, °C.</summary>
        public double Celsius
        {
            get => _value / 100.0;
            set
            {
                var v = (short)Math.Round(value * 100);
                if (v == _value) return;
                _value = v;
                NotifyChanged(0);
            }
        }
    }

    /// <summary>Bridged Device Basic Information.</summary>
    public sealed class BridgedInfoCluster : MatterCluster
    {
        private string _label;
        private bool _reachable = true;

        /// <summary>Creates the cluster.</summary>
        public BridgedInfoCluster(string label, string uniqueId, string vendor = "IoTCom", string product = "Bridged device") : base(MatterIds.BridgedDeviceBasicInformation, "Bridged Device Basic Information", 4)
        {
            _label = label;
            Attribute(1, () => TlvElement.String(TlvTag.Anonymous, vendor));
            Attribute(3, () => TlvElement.String(TlvTag.Anonymous, product));
            Attribute(5, () => TlvElement.String(TlvTag.Anonymous, _label), v =>
            {
                var s = v.AsString();
                if (s.Length > 32) return MatterStatus.ConstraintError;
                _label = s;
                return MatterStatus.Success;
            });
            Attribute(0x11, () => TlvElement.Bool(TlvTag.Anonymous, _reachable));
            Attribute(0x12, () => TlvElement.String(TlvTag.Anonymous, uniqueId));
        }

        /// <summary>Node label.</summary>
        public string Label => _label;

        /// <summary>Whether the bridged device answers.</summary>
        public bool Reachable
        {
            get => _reachable;
            set
            {
                if (_reachable == value) return;
                _reachable = value;
                NotifyChanged(0x11);
            }
        }
    }
}
