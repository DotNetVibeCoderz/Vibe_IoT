using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

namespace IoTCom.Net.Adapters.OpcUa;

/// <summary>OPC UA client options.</summary>
public sealed class OpcUaClientOptions
{
    /// <summary>Server endpoint, e.g. <c>opc.tcp://plc.local:4840</c>.</summary>
    public string EndpointUrl { get; set; } = "opc.tcp://localhost:4840";

    /// <summary>Pick the most secure endpoint the server offers (default). False selects SecurityPolicy None.</summary>
    public bool UseSecurity { get; set; } = true;

    /// <summary>Accept server certificates that are not in the trusted store (commissioning and tests only).</summary>
    public bool AcceptUntrustedCertificates { get; set; }

    /// <summary>User name (anonymous when null).</summary>
    public string? UserName { get; set; }

    /// <summary>Password for <see cref="UserName"/>.</summary>
    public string? Password { get; set; }

    /// <summary>Refuse writes and method calls with <see cref="ReadOnlyModeException"/>.</summary>
    public bool ReadOnly { get; set; }

    /// <summary>PKI root (own, trusted, issuer, rejected). Default: <c>%LOCALAPPDATA%/IoTCom.Net/opcua/pki</c>.</summary>
    public string? PkiPath { get; set; }

    /// <summary>Application name shown to the server.</summary>
    public string ApplicationName { get; set; } = "IoTCom.Net OPC UA Client";

    /// <summary>Session timeout.</summary>
    public TimeSpan SessionTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Friendly name.</summary>
    public string? Name { get; set; }

    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }

    /// <summary>Sets the endpoint URL.</summary>
    public OpcUaClientOptions UseEndpoint(string url)
    {
        EndpointUrl = url;
        return this;
    }

    /// <summary>Signs in with a user name and password.</summary>
    public OpcUaClientOptions WithCredentials(string user, string password)
    {
        (UserName, Password) = (user, password);
        return this;
    }
}

/// <summary>A node found by browsing.</summary>
/// <param name="NodeId">Node id in string form (<c>ns=2;s=Plant/Line1</c>).</param>
/// <param name="BrowseName">Browse name.</param>
/// <param name="DisplayName">Display name.</param>
/// <param name="NodeClass">Object, Variable, Method, …</param>
/// <param name="TypeDefinition">Type definition node id, if any.</param>
public sealed record OpcUaNode(string NodeId, string BrowseName, string DisplayName, string NodeClass, string? TypeDefinition)
{
    /// <summary>True for objects and folders (they may have children).</summary>
    public bool IsContainer => NodeClass is "Object" or "View";

    /// <inheritdoc />
    public override string ToString() => $"{DisplayName} ({NodeClass}) {NodeId}";
}

/// <summary>A value read or received from a subscription.</summary>
/// <param name="NodeId">Node id.</param>
/// <param name="Value">The value (CLR type of the Variant).</param>
/// <param name="StatusCode">OPC UA status code.</param>
/// <param name="SourceTimestamp">Source timestamp, if any.</param>
public sealed record OpcUaValue(string NodeId, object? Value, uint StatusCode, DateTimeOffset? SourceTimestamp)
{
    /// <summary>The status is Good.</summary>
    public bool IsGood => (StatusCode & 0xC0000000) == 0;

    /// <summary>Status name, e.g. Good or BadNodeIdUnknown.</summary>
    public string Status => new Opc.Ua.StatusCode(StatusCode).SymbolicId ?? $"0x{StatusCode:X8}";

    /// <summary>The value as invariant text.</summary>
    public string Text => Value switch
    {
        null => "null",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        Array a => "[" + string.Join(", ", a.Cast<object?>().Select(x => Convert.ToString(x, CultureInfo.InvariantCulture))) + "]",
        var v => v.ToString() ?? "",
    };

    /// <inheritdoc />
    public override string ToString() => $"{NodeId} = {Text} ({Status})";
}

/// <summary>
/// An OPC UA client endpoint over the OPC Foundation stack: connect with or without security (and with a user name),
/// browse, read, write (blocked in read-only mode), call methods and subscribe to data changes as an
/// <see cref="IAsyncEnumerable{T}"/>. Service calls are reported to the traffic tap.
/// </summary>
/// <example>
/// <code>
/// await using var ua = OpcUaClient.Create(o => o.UseEndpoint("opc.tcp://plc.local:4840"));
/// await ua.ConnectAsync();
/// foreach (var n in await ua.BrowseAsync()) Console.WriteLine(n);
/// var speed = await ua.ReadAsync("ns=2;s=Plant/Line1/Filler/Speed");
/// </code>
/// </example>
public sealed class OpcUaClient : EndpointBase, IClientEndpoint
{
    private readonly OpcUaClientOptions _options;
    private ISession? _session;

    private OpcUaClient(OpcUaClientOptions options) : base("opcua", options.Logger)
    {
        _options = options;
        Name = options.Name;
    }

    /// <summary>Creates a client.</summary>
    public static OpcUaClient Create(Action<OpcUaClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var o = new OpcUaClientOptions();
        configure(o);
        return new OpcUaClient(o);
    }

    /// <summary>The underlying session (for features the adapter does not wrap).</summary>
    public ISession? Session => _session;

    /// <summary>Security policy of the session (e.g. Basic256Sha256 or None).</summary>
    public string? SecurityPolicy => _session?.Endpoint?.SecurityPolicyUri?.Split('#').LastOrDefault();

    /// <summary>Security mode of the session.</summary>
    public string? SecurityMode => _session?.Endpoint?.SecurityMode.ToString();

    /// <summary>Namespace table of the server.</summary>
    public IReadOnlyList<string> Namespaces => _session is null ? [] : [.. _session.NamespaceUris.ToArray()];

    /// <inheritdoc />
    public async ValueTask ConnectAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_session is { Connected: true }) return;
        SetState(EndpointState.Connecting);
        try
        {
            var config = OpcUaApplication.Create(ApplicationType.Client, _options.ApplicationName,
                $"urn:{System.Net.Dns.GetHostName()}:IoTCom.Net:OpcUaClient", _options.PkiPath, _options.AcceptUntrustedCertificates);
            await config.ValidateAsync(ApplicationType.Client, ct).ConfigureAwait(false);
            if (_options.AcceptUntrustedCertificates)
                config.CertificateValidator.CertificateValidation += (_, e) => e.Accept = e.Error.StatusCode == StatusCodes.BadCertificateUntrusted || e.Accept;
            var app = new ApplicationInstance(config, OpcUaApplication.Telemetry);
            if (_options.UseSecurity) await app.CheckApplicationInstanceCertificatesAsync(true, null, ct).ConfigureAwait(false);
            var description = await CoreClientUtils.SelectEndpointAsync(config, _options.EndpointUrl, _options.UseSecurity, OpcUaApplication.Telemetry, ct).ConfigureAwait(false);
            var endpoint = new ConfiguredEndpoint(null, description, EndpointConfiguration.Create(config));
            IUserIdentity identity = _options.UserName is { } user ? new UserIdentity(user, Encoding.UTF8.GetBytes(_options.Password ?? "")) : new UserIdentity();
            _session = await new DefaultSessionFactory(OpcUaApplication.Telemetry).CreateAsync(config, endpoint, false, false, _options.ApplicationName,
                (uint)_options.SessionTimeout.TotalMilliseconds, identity, null, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ServiceResultException or IOException or System.Net.Sockets.SocketException)
        {
            SetState(EndpointState.Disconnected, ex);
            throw new TransportException($"OPC UA connect to {_options.EndpointUrl} failed: {ex.Message}", ex);
        }

        Tap(FrameDirection.Outbound, Encoding.UTF8.GetBytes($"CreateSession {_options.EndpointUrl}"), () => $"session {SecurityPolicy}/{SecurityMode}");
        SetState(EndpointState.Connected);
        Logger.LogInformation("OPC UA connected to {Url} ({Policy}, {Mode})", _options.EndpointUrl, SecurityPolicy, SecurityMode);
    }

    /// <inheritdoc />
    public async ValueTask DisconnectAsync(CancellationToken ct = default)
    {
        if (_session is null) return;
        SetState(EndpointState.Stopping);
        try
        {
            await _session.CloseAsync(5000, true, ct).ConfigureAwait(false);
        }
        catch (ServiceResultException)
        {
        }

        _session.Dispose();
        _session = null;
        SetState(EndpointState.Disconnected);
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore() => await DisconnectAsync().ConfigureAwait(false);

    private ISession Live => _session is { Connected: true } s ? s : throw new InvalidOperationException("Connect first.");

    private NodeId Parse(string nodeId)
    {
        try
        {
            return NodeId.Parse(nodeId);
        }
        catch (ServiceResultException ex)
        {
            throw new ArgumentException($"'{nodeId}' is not a node id (e.g. ns=2;s=Plant/Line1 or i=85).", nameof(nodeId), ex);
        }
    }

    private void TapService(FrameDirection direction, string text) => Tap(direction, Encoding.UTF8.GetBytes(text), () => text);

    /// <summary>Lists the children of a node (default: the Objects folder).</summary>
    public async Task<IReadOnlyList<OpcUaNode>> BrowseAsync(string? nodeId = null, CancellationToken ct = default)
    {
        var session = Live;
        var start = nodeId is null ? ObjectIds.ObjectsFolder : Parse(nodeId);
        var request = new BrowseDescriptionCollection
        {
            new BrowseDescription
            {
                NodeId = start, BrowseDirection = BrowseDirection.Forward, ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences, IncludeSubtypes = true,
                NodeClassMask = (uint)(NodeClass.Object | NodeClass.Variable | NodeClass.Method | NodeClass.View), ResultMask = (uint)BrowseResultMask.All,
            },
        };
        TapService(FrameDirection.Outbound, $"Browse {start}");
        var response = await session.BrowseAsync(null, null, 0, request, ct).ConfigureAwait(false);
        var result = response.Results[0];
        Check(result.StatusCode, start.ToString());
        var references = new List<ReferenceDescription>(result.References);
        var continuation = result.ContinuationPoint;
        while (continuation is { Length: > 0 })
        {
            var next = await session.BrowseNextAsync(null, false, [continuation], ct).ConfigureAwait(false);
            references.AddRange(next.Results[0].References);
            continuation = next.Results[0].ContinuationPoint;
        }

        var nodes = references.Select(r => new OpcUaNode(
            ExpandedNodeId.ToNodeId(r.NodeId, session.NamespaceUris).ToString(),
            r.BrowseName.ToString(), r.DisplayName.Text, r.NodeClass.ToString(),
            r.TypeDefinition is { } td && !NodeId.IsNull(td) ? ExpandedNodeId.ToNodeId(td, session.NamespaceUris)?.ToString() : null)).ToList();
        TapService(FrameDirection.Inbound, $"Browse {start}: {nodes.Count} references");
        return nodes;
    }

    /// <summary>Reads the value of a variable.</summary>
    public async Task<OpcUaValue> ReadAsync(string nodeId, CancellationToken ct = default) => (await ReadAsync([nodeId], ct).ConfigureAwait(false))[0];

    /// <summary>Reads several values in one request.</summary>
    public async Task<IReadOnlyList<OpcUaValue>> ReadAsync(IReadOnlyList<string> nodeIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        var session = Live;
        var request = new ReadValueIdCollection(nodeIds.Select(id => new ReadValueId { NodeId = Parse(id), AttributeId = Attributes.Value }));
        TapService(FrameDirection.Outbound, $"Read {string.Join(", ", nodeIds)}");
        var response = await session.ReadAsync(null, 0, TimestampsToReturn.Both, request, ct).ConfigureAwait(false);
        var values = nodeIds.Select((id, i) => ToValue(id, response.Results[i])).ToList();
        TapService(FrameDirection.Inbound, "Read: " + string.Join(", ", values.Select(v => $"{v.Text} ({v.Status})")));
        return values;
    }

    /// <summary>Reads one attribute of a node (DisplayName, DataType, AccessLevel, …).</summary>
    public async Task<OpcUaValue> ReadAttributeAsync(string nodeId, uint attributeId, CancellationToken ct = default)
    {
        var session = Live;
        var response = await session.ReadAsync(null, 0, TimestampsToReturn.Neither, [new ReadValueId { NodeId = Parse(nodeId), AttributeId = attributeId }], ct).ConfigureAwait(false);
        return ToValue(nodeId, response.Results[0]);
    }

    private static OpcUaValue ToValue(string nodeId, DataValue d)
    {
        var value = d.WrappedValue.Value switch
        {
            LocalizedText t => t.Text,
            QualifiedName q => q.ToString(),
            NodeId n => n.ToString(),
            Uuid u => u.ToString(),
            var v => v,
        };
        return new OpcUaValue(nodeId, value, d.StatusCode.Code, d.SourceTimestamp == DateTime.MinValue ? null : new DateTimeOffset(DateTime.SpecifyKind(d.SourceTimestamp, DateTimeKind.Utc)));
    }

    /// <summary>
    /// Writes a value. The value is converted to the variable's current CLR type (so "42" or 42 both write an
    /// Int32 variable). Throws <see cref="ReadOnlyModeException"/> in read-only mode and <see cref="DeviceException"/>
    /// when the server refuses.
    /// </summary>
    public async Task WriteAsync(string nodeId, object? value, CancellationToken ct = default)
    {
        if (_options.ReadOnly) throw new ReadOnlyModeException();
        var session = Live;
        var current = await ReadAsync(nodeId, ct).ConfigureAwait(false);
        var converted = Coerce(value, current.Value);
        TapService(FrameDirection.Outbound, $"Write {nodeId} = {Convert.ToString(converted, CultureInfo.InvariantCulture)}");
        var response = await session.WriteAsync(null, [new WriteValue { NodeId = Parse(nodeId), AttributeId = Attributes.Value, Value = new DataValue(new Variant(converted)) }], ct).ConfigureAwait(false);
        Check(response.Results[0], nodeId);
        TapService(FrameDirection.Inbound, $"Write {nodeId}: Good");
    }

    private static object? Coerce(object? value, object? current)
    {
        if (value is null || current is null || value.GetType() == current.GetType()) return value;
        if (current is bool && value is string s) return s.Trim().ToLowerInvariant() is "1" or "true" or "on" or "yes";
        return current is IConvertible && value is IConvertible
            ? Convert.ChangeType(value, current.GetType(), CultureInfo.InvariantCulture)
            : value;
    }

    /// <summary>Calls a method on an object; returns the output arguments.</summary>
    public async Task<IReadOnlyList<object?>> CallAsync(string objectId, string methodId, IReadOnlyList<object?>? inputs = null, CancellationToken ct = default)
    {
        if (_options.ReadOnly) throw new ReadOnlyModeException();
        var session = Live;
        var request = new CallMethodRequest { ObjectId = Parse(objectId), MethodId = Parse(methodId), InputArguments = new VariantCollection((inputs ?? []).Select(a => new Variant(a))) };
        TapService(FrameDirection.Outbound, $"Call {methodId} on {objectId}");
        var response = await session.CallAsync(null, [request], ct).ConfigureAwait(false);
        var result = response.Results[0];
        Check(result.StatusCode, methodId);
        var outputs = result.OutputArguments.Select(v => v.Value).ToList();
        TapService(FrameDirection.Inbound, $"Call {methodId}: {string.Join(", ", outputs)}");
        return outputs;
    }

    /// <summary>
    /// Subscribes to value changes of <paramref name="nodeIds"/> (publishing and sampling at <paramref name="interval"/>,
    /// default 500 ms). The first value of each node arrives right away; the subscription is deleted when enumeration stops.
    /// </summary>
    public async IAsyncEnumerable<OpcUaValue> SubscribeAsync(IReadOnlyList<string> nodeIds, TimeSpan? interval = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        var session = Live;
        var ms = (int)(interval ?? TimeSpan.FromMilliseconds(500)).TotalMilliseconds;
        var channel = Channel.CreateUnbounded<OpcUaValue>(new UnboundedChannelOptions { SingleReader = true });
        var subscription = new Subscription(OpcUaApplication.Telemetry, new SubscriptionOptions
        {
            DisplayName = "IoTCom.Net", PublishingInterval = ms, KeepAliveCount = 10, LifetimeCount = 100, PublishingEnabled = true, TimestampsToReturn = TimestampsToReturn.Both,
        });
        session.AddSubscription(subscription);
        await subscription.CreateAsync(ct).ConfigureAwait(false);
        foreach (var id in nodeIds)
        {
            var item = new MonitoredItem(OpcUaApplication.Telemetry, new MonitoredItemOptions
            {
                DisplayName = id, StartNodeId = Parse(id), AttributeId = Attributes.Value, SamplingInterval = ms, QueueSize = 10, DiscardOldest = true,
            });
            item.Notification += (_, e) =>
            {
                if (e.NotificationValue is not MonitoredItemNotification n) return;
                var v = ToValue(id, n.Value);
                Tap(FrameDirection.Inbound, Encoding.UTF8.GetBytes($"DataChange {id} = {v.Text}"), () => $"data change {id} = {v.Text}");
                channel.Writer.TryWrite(v);
            };
            subscription.AddItem(item);
        }

        await subscription.ApplyChangesAsync(ct).ConfigureAwait(false);
        TapService(FrameDirection.Outbound, $"CreateSubscription {ms} ms: {string.Join(", ", nodeIds)}");
        try
        {
            await foreach (var v in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false)) yield return v;
        }
        finally
        {
            try
            {
                await session.RemoveSubscriptionAsync(subscription, CancellationToken.None).ConfigureAwait(false);
            }
            catch (ServiceResultException)
            {
            }

            subscription.Dispose();
        }
    }

    private static void Check(StatusCode status, string target)
    {
        if (StatusCode.IsBad(status)) throw new DeviceException($"OPC UA {target}: {status.SymbolicId ?? status.ToString()}");
    }
}
