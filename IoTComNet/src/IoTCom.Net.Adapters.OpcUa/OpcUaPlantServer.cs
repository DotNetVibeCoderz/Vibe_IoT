using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Server;

namespace IoTCom.Net.Adapters.OpcUa;

/// <summary>Options of the simulated plant server.</summary>
public sealed class OpcUaPlantServerOptions
{
    /// <summary>TCP port (default 4840).</summary>
    public int Port { get; set; } = 4840;

    /// <summary>Host name used in the endpoint URL (default localhost).</summary>
    public string Host { get; set; } = "localhost";

    /// <summary>Offer Basic256Sha256 Sign and SignAndEncrypt next to None (creates a server certificate on first start).</summary>
    public bool EnableSecurity { get; set; } = true;

    /// <summary>Accept client certificates that are not in the trusted store (default true: it is a simulator).</summary>
    public bool AcceptUntrustedClients { get; set; } = true;

    /// <summary>Simulation step.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>PKI root (default: <c>%LOCALAPPDATA%/IoTCom.Net/opcua/pki-server</c>).</summary>
    public string? PkiPath { get; set; }

    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }
}

/// <summary>
/// An OPC UA server simulating a bottling line, for tests, demos and client development. Address space under
/// <c>Objects/Plant/Line1</c> (namespace <see cref="NamespaceUri"/>, string node ids such as
/// <c>ns=2;s=Plant/Line1/Filler/Speed</c>): a filler (speed, bottle count, running and setpoint writable), a syrup
/// tank (level, temperature, inlet valve writable, high-level alarm) and a <c>ResetCounter</c> method.
/// </summary>
public sealed class OpcUaPlantServer : IAsyncDisposable
{
    /// <summary>Namespace of the plant nodes.</summary>
    public const string NamespaceUri = "urn:iotcom:plant";

    private readonly OpcUaPlantServerOptions _options;
    private ApplicationInstance? _application;
    private PlantServer? _server;

    private OpcUaPlantServer(OpcUaPlantServerOptions options) => _options = options;

    /// <summary>Creates a server.</summary>
    public static OpcUaPlantServer Create(Action<OpcUaPlantServerOptions>? configure = null)
    {
        var o = new OpcUaPlantServerOptions();
        configure?.Invoke(o);
        return new OpcUaPlantServer(o);
    }

    /// <summary>Endpoint URL clients connect to.</summary>
    public string EndpointUrl => $"opc.tcp://{_options.Host}:{_options.Port}/iotcom/plant";

    /// <summary>Node id of a plant variable or object, e.g. <c>NodeId("Line1/Filler/Speed")</c>.</summary>
    public string NodeId(string path) => $"ns={NamespaceIndex};s=Plant/{path}";

    /// <summary>Namespace index of <see cref="NamespaceUri"/> on this server.</summary>
    public ushort NamespaceIndex => _server?.Plant?.NamespaceIndex ?? 2;

    /// <summary>Number of client sessions.</summary>
    public int SessionCount => _server?.CurrentInstance?.SessionManager?.GetSessions()?.Count ?? 0;

    /// <summary>Starts the server.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_server is not null) return;
        var config = OpcUaApplication.Create(ApplicationType.Server, "IoTCom Plant Simulator", $"urn:{System.Net.Dns.GetHostName()}:IoTCom.Net:PlantSimulator",
            _options.PkiPath ?? Path.Combine(Path.GetDirectoryName(OpcUaApplication.DefaultPkiRoot)!, "pki-server"), _options.AcceptUntrustedClients);
        var server = new ServerConfiguration
        {
            BaseAddresses = { EndpointUrl },
            MinRequestThreadCount = 2, MaxRequestThreadCount = 20, MaxQueuedRequestCount = 200,
            MinPublishingInterval = 50, MinSubscriptionLifetime = 1000,
        };
        server.SecurityPolicies.Add(new ServerSecurityPolicy { SecurityMode = MessageSecurityMode.None, SecurityPolicyUri = SecurityPolicies.None });
        if (_options.EnableSecurity)
        {
            server.SecurityPolicies.Add(new ServerSecurityPolicy { SecurityMode = MessageSecurityMode.Sign, SecurityPolicyUri = SecurityPolicies.Basic256Sha256 });
            server.SecurityPolicies.Add(new ServerSecurityPolicy { SecurityMode = MessageSecurityMode.SignAndEncrypt, SecurityPolicyUri = SecurityPolicies.Basic256Sha256 });
        }

        server.UserTokenPolicies.Add(new UserTokenPolicy(UserTokenType.Anonymous));
        config.ServerConfiguration = server;
        await config.ValidateAsync(ApplicationType.Server, ct).ConfigureAwait(false);
        if (_options.AcceptUntrustedClients)
            config.CertificateValidator.CertificateValidation += (_, e) => e.Accept = e.Error.StatusCode == StatusCodes.BadCertificateUntrusted || e.Accept;
        _application = new ApplicationInstance(config, OpcUaApplication.Telemetry);
        await _application.CheckApplicationInstanceCertificatesAsync(true, null, ct).ConfigureAwait(false);
        _server = new PlantServer(_options.Interval);
        await _application.StartAsync(_server).ConfigureAwait(false);
        _options.Logger?.LogInformation("OPC UA plant simulator on {Url}", EndpointUrl);
    }

    /// <summary>Stops the server.</summary>
    public async Task StopAsync()
    {
        if (_application is null) return;
        await _application.StopAsync().ConfigureAwait(false);
        _server?.Dispose();
        (_application, _server) = (null, null);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private sealed class PlantServer(TimeSpan interval) : StandardServer
    {
        public PlantNodeManager? Plant { get; private set; }

        protected override MasterNodeManager CreateMasterNodeManager(IServerInternal server, ApplicationConfiguration configuration)
        {
            Plant = new PlantNodeManager(server, configuration, interval);
            return new MasterNodeManager(server, configuration, null, Plant);
        }

        protected override ServerProperties LoadServerProperties() => new()
        {
            ManufacturerName = "Gravicode Studios",
            ProductName = "IoTCom.Net OPC UA Plant Simulator",
            ProductUri = "https://github.com/DotNetVibeCoderz/Vibe_IoT/IoTComNet",
            SoftwareVersion = IoTComInfo.Version,
            BuildNumber = IoTComInfo.Version,
            BuildDate = DateTime.UtcNow,
        };
    }

    private sealed class PlantNodeManager : CustomNodeManager2
    {
        private readonly TimeSpan _interval;
        private readonly Random _random = new(7);
        private readonly Dictionary<string, BaseDataVariableState> _vars = new(StringComparer.Ordinal);
        private Timer? _timer;

        public PlantNodeManager(IServerInternal server, ApplicationConfiguration configuration, TimeSpan interval) : base(server, configuration, NamespaceUri)
        {
            _interval = interval;
            SystemContext.NodeIdFactory = this;
        }

        public override NodeId New(ISystemContext context, NodeState node) => node.NodeId;

        public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
        {
            lock (Lock)
            {
                if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out var references))
                    externalReferences[ObjectIds.ObjectsFolder] = references = new List<IReference>();

                var plant = Folder(null, "Plant", "Plant");
                plant.AddReference(ReferenceTypeIds.Organizes, true, ObjectIds.ObjectsFolder);
                references.Add(new NodeStateReference(ReferenceTypeIds.Organizes, false, plant.NodeId));
                Variable(plant, "Plant/Location", "Location", DataTypeIds.String, "Cikarang, West Java", writable: false);

                var line = Folder(plant, "Plant/Line1", "Line1");
                Variable(line, "Plant/Line1/State", "State", DataTypeIds.String, "Running", writable: false);
                var filler = Folder(line, "Plant/Line1/Filler", "Filler");
                Variable(filler, "Plant/Line1/Filler/Speed", "Speed", DataTypeIds.Double, 119.6, writable: false, unit: "bottles/min");
                Variable(filler, "Plant/Line1/Filler/BottleCount", "BottleCount", DataTypeIds.Int64, 120_400L, writable: false);
                Variable(filler, "Plant/Line1/Filler/Running", "Running", DataTypeIds.Boolean, true, writable: true);
                Variable(filler, "Plant/Line1/Filler/Setpoint", "Setpoint", DataTypeIds.Double, 120.0, writable: true, unit: "bottles/min");
                var tank = Folder(line, "Plant/Line1/Tank7", "Tank7");
                Variable(tank, "Plant/Line1/Tank7/Level", "Level", DataTypeIds.Double, 62.0, writable: false, unit: "%");
                Variable(tank, "Plant/Line1/Tank7/Temperature", "Temperature", DataTypeIds.Double, 21.5, writable: false, unit: "°C");
                Variable(tank, "Plant/Line1/Tank7/InletValve", "InletValve", DataTypeIds.Boolean, false, writable: true);
                Variable(tank, "Plant/Line1/Tank7/HighLevelAlarm", "HighLevelAlarm", DataTypeIds.Boolean, false, writable: false);

                var reset = new MethodState(line)
                {
                    SymbolicName = "ResetCounter", NodeId = new NodeId("Plant/Line1/ResetCounter", NamespaceIndex),
                    BrowseName = new QualifiedName("ResetCounter", NamespaceIndex), DisplayName = "ResetCounter",
                    ReferenceTypeId = ReferenceTypeIds.HasComponent, Executable = true, UserExecutable = true,
                    OutputArguments = new PropertyState<Argument[]>(null),
                };
                reset.OutputArguments.NodeId = new NodeId("Plant/Line1/ResetCounter/OutputArguments", NamespaceIndex);
                reset.OutputArguments.BrowseName = BrowseNames.OutputArguments;
                reset.OutputArguments.DisplayName = reset.OutputArguments.BrowseName.Name;
                reset.OutputArguments.TypeDefinitionId = VariableTypeIds.PropertyType;
                reset.OutputArguments.ReferenceTypeId = ReferenceTypeIds.HasProperty;
                reset.OutputArguments.DataType = DataTypeIds.Argument;
                reset.OutputArguments.ValueRank = ValueRanks.OneDimension;
                reset.OutputArguments.Value = [new Argument { Name = "Previous", Description = "Count before the reset", DataType = DataTypeIds.Int64, ValueRank = ValueRanks.Scalar }];
                reset.OnCallMethod = (context, method, inputs, outputs) =>
                {
                    lock (Lock)
                    {
                        var counter = _vars["Plant/Line1/Filler/BottleCount"];
                        outputs[0] = counter.Value;
                        Set(counter, 0L);
                    }

                    return ServiceResult.Good;
                };
                line.AddChild(reset);

                AddPredefinedNode(SystemContext, plant);
            }

            _timer = new Timer(_ => Step(), null, _interval, _interval);
        }

        private FolderState Folder(NodeState? parent, string path, string name)
        {
            var folder = new FolderState(parent)
            {
                SymbolicName = name, ReferenceTypeId = ReferenceTypeIds.Organizes, TypeDefinitionId = ObjectTypeIds.FolderType,
                NodeId = new NodeId(path, NamespaceIndex), BrowseName = new QualifiedName(name, NamespaceIndex), DisplayName = name,
                EventNotifier = EventNotifiers.None,
            };
            parent?.AddChild(folder);
            return folder;
        }

        private void Variable(NodeState parent, string path, string name, NodeId dataType, object value, bool writable, string? unit = null)
        {
            var access = writable ? (byte)(AccessLevels.CurrentRead | AccessLevels.CurrentWrite) : AccessLevels.CurrentRead;
            var v = new BaseDataVariableState(parent)
            {
                SymbolicName = name, ReferenceTypeId = ReferenceTypeIds.Organizes, TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
                NodeId = new NodeId(path, NamespaceIndex), BrowseName = new QualifiedName(name, NamespaceIndex), DisplayName = name,
                Description = unit is null ? null : new LocalizedText($"Unit: {unit}"),
                DataType = dataType, ValueRank = ValueRanks.Scalar, AccessLevel = access, UserAccessLevel = access,
                Historizing = false, Value = value, StatusCode = StatusCodes.Good, Timestamp = DateTime.UtcNow,
            };
            parent.AddChild(v);
            _vars[path] = v;
        }

        private void Set(BaseDataVariableState v, object value)
        {
            v.Value = value;
            v.Timestamp = DateTime.UtcNow;
            v.ClearChangeMasks(SystemContext, false);
        }

        private void Step()
        {
            lock (Lock)
            {
                var running = _vars["Plant/Line1/Filler/Running"].Value is true;
                var setpoint = Convert.ToDouble(_vars["Plant/Line1/Filler/Setpoint"].Value, System.Globalization.CultureInfo.InvariantCulture);
                var speed = running ? Math.Round(setpoint + ((_random.NextDouble() - 0.5) * 4), 1) : 0.0;
                Set(_vars["Plant/Line1/Filler/Speed"], speed);
                Set(_vars["Plant/Line1/Filler/BottleCount"], (long)_vars["Plant/Line1/Filler/BottleCount"].Value + (long)Math.Round(speed * _interval.TotalSeconds / 60.0));
                Set(_vars["Plant/Line1/State"], running ? "Running" : "Stopped");
                var inlet = _vars["Plant/Line1/Tank7/InletValve"].Value is true;
                var level = Math.Clamp((double)_vars["Plant/Line1/Tank7/Level"].Value + (inlet ? 0.8 : 0) - (speed / 400.0), 0, 100);
                Set(_vars["Plant/Line1/Tank7/Level"], Math.Round(level, 2));
                Set(_vars["Plant/Line1/Tank7/Temperature"], Math.Round(21.5 + ((_random.NextDouble() - 0.5) * 0.6), 2));
                Set(_vars["Plant/Line1/Tank7/HighLevelAlarm"], level > 90);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _timer?.Dispose();
            base.Dispose(disposing);
        }
    }
}
