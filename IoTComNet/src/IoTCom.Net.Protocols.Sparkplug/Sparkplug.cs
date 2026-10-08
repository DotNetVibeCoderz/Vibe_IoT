using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using IoTCom.Net.Adapters.Mqtt;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Protocols.Sparkplug;

/// <summary>A metric definition with its current value, owned by an edge node or a device.</summary>
public sealed class SparkplugTag
{
    internal SparkplugTag(string name, SparkplugDataType type, object? value, ulong alias, bool writable)
    {
        Name = name;
        DataType = type;
        Value = value;
        Alias = alias;
        Writable = writable;
    }

    /// <summary>Name.</summary>
    public string Name { get; }

    /// <summary>Data type.</summary>
    public SparkplugDataType DataType { get; }

    /// <summary>Alias announced in the birth.</summary>
    public ulong Alias { get; }

    /// <summary>Accepts writes from the host (DCMD/NCMD).</summary>
    public bool Writable { get; }

    /// <summary>Current value.</summary>
    public object? Value { get; internal set; }

    internal SparkplugMetric Birth() => SparkplugMetric.Of(Name, DataType, Value, Alias);

    internal SparkplugMetric Data() => new() { Alias = Alias, DataType = DataType, Value = Value, IsNull = Value is null };
}

/// <summary>A device behind an edge node.</summary>
public sealed class SparkplugDevice
{
    private readonly SparkplugEdgeNode _node;
    internal readonly ConcurrentDictionary<string, SparkplugTag> Tags = new(StringComparer.Ordinal);

    internal SparkplugDevice(SparkplugEdgeNode node, string id)
    {
        _node = node;
        Id = id;
    }

    /// <summary>Device id.</summary>
    public string Id { get; }

    /// <summary>Metrics.</summary>
    public IReadOnlyCollection<SparkplugTag> Metrics => [.. Tags.Values];

    /// <summary>Defines a metric (before <see cref="SparkplugEdgeNode.StartAsync"/>, or followed by a rebirth).</summary>
    public SparkplugDevice Metric(string name, SparkplugDataType type, object? initial, bool writable = false)
    {
        Tags[name] = new SparkplugTag(name, type, initial, _node.NextAlias(), writable);
        return this;
    }

    /// <summary>Changes metrics and publishes them (DDATA, report by exception).</summary>
    public Task SetAsync(IReadOnlyDictionary<string, object?> values, CancellationToken ct = default) => _node.PublishDataAsync(this, values, ct);

    /// <summary>Changes one metric and publishes it.</summary>
    public Task SetAsync(string name, object? value, CancellationToken ct = default) => SetAsync(new Dictionary<string, object?> { [name] = value }, ct);
}

/// <summary>A command the host sent to a metric.</summary>
/// <param name="Device">Device id, or null for a node metric.</param>
/// <param name="Metric">The metric.</param>
/// <param name="Value">The requested value.</param>
public sealed record SparkplugCommand(string? Device, string Metric, object? Value);

/// <summary>Edge node options.</summary>
public sealed class SparkplugEdgeNodeOptions
{
    /// <summary>Group id.</summary>
    public string Group { get; set; } = "Plant";

    /// <summary>Edge node id.</summary>
    public string EdgeNode { get; set; } = "EdgeNode1";

    /// <summary>MQTT connection (broker, credentials, TLS).</summary>
    public Action<MqttEndpointOptions> Mqtt { get; set; } = o => o.UseBroker("localhost");

    /// <summary>Starting bdSeq (birth/death sequence; incremented on every new MQTT session).</summary>
    public ulong BdSeq { get; set; }

    /// <summary>Accept writes from hosts. When false every NCMD/DCMD write (except rebirth) is ignored.</summary>
    public bool AcceptWrites { get; set; } = true;

    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }
}

/// <summary>
/// A Sparkplug B edge node: registers NDEATH (with bdSeq) as its MQTT will, publishes NBIRTH and DBIRTH with aliases,
/// reports changes with NDATA/DDATA (seq 0–255), answers "Node Control/Rebirth" and applies writes to writable
/// metrics from NCMD/DCMD.
/// </summary>
/// <example>
/// <code>
/// await using var node = SparkplugEdgeNode.Create(o => { o.Group = "Plant"; o.EdgeNode = "Line1"; o.Mqtt = m => m.UseBroker("broker"); });
/// node.Device("Filler").Metric("Speed", SparkplugDataType.Float, 0f).Metric("Running", SparkplugDataType.Boolean, false, writable: true);
/// await node.StartAsync();
/// await node.Devices["Filler"].SetAsync("Speed", 118.5f);
/// </code>
/// </example>
public sealed class SparkplugEdgeNode : IAsyncDisposable
{
    private readonly SparkplugEdgeNodeOptions _options;
    private readonly ConcurrentDictionary<string, SparkplugTag> _tags = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SparkplugDevice> _devices = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _publish = new(1, 1);
    private MqttEndpoint? _mqtt;
    private CancellationTokenSource? _cts;
    private ulong _alias;
    private int _seq;

    private SparkplugEdgeNode(SparkplugEdgeNodeOptions options)
    {
        _options = options;
        BdSeq = options.BdSeq;
        _tags["Node Control/Rebirth"] = new SparkplugTag("Node Control/Rebirth", SparkplugDataType.Boolean, false, NextAlias(), writable: true);
    }

    /// <summary>Creates an edge node.</summary>
    public static SparkplugEdgeNode Create(Action<SparkplugEdgeNodeOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var o = new SparkplugEdgeNodeOptions();
        configure(o);
        return new SparkplugEdgeNode(o);
    }

    /// <summary>Raised for every write command accepted (after the value changed and DDATA/NDATA went out).</summary>
    public event EventHandler<SparkplugCommand>? CommandReceived;

    /// <summary>Raised for every message published (topic, payload) — for logs and demos.</summary>
    public event EventHandler<(SparkplugTopic Topic, SparkplugPayload Payload)>? Published;

    /// <summary>The MQTT endpoint (attach traffic taps here after start).</summary>
    public MqttEndpoint? Mqtt => _mqtt;

    /// <summary>Current birth/death sequence.</summary>
    public ulong BdSeq { get; private set; }

    /// <summary>Devices.</summary>
    public IReadOnlyDictionary<string, SparkplugDevice> Devices => _devices;

    /// <summary>Node metrics.</summary>
    public IReadOnlyCollection<SparkplugTag> Metrics => [.. _tags.Values];

    internal ulong NextAlias() => Interlocked.Increment(ref _alias);

    /// <summary>Defines a node metric.</summary>
    public SparkplugEdgeNode Metric(string name, SparkplugDataType type, object? initial, bool writable = false)
    {
        _tags[name] = new SparkplugTag(name, type, initial, NextAlias(), writable);
        return this;
    }

    /// <summary>Adds (or returns) a device.</summary>
    public SparkplugDevice Device(string id) => _devices.GetOrAdd(id, i => new SparkplugDevice(this, i));

    private string Topic(SparkplugMessageType type, string? device = null) => new SparkplugTopic(type, _options.Group, _options.EdgeNode, device).ToString();

    private ulong NextSeq() => (ulong)(Interlocked.Increment(ref _seq) & 0xFF);

    /// <summary>Connects (NDEATH as will), subscribes to commands and publishes the births.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_mqtt is not null) return;
        var death = new SparkplugPayload { Timestamp = SparkplugPayload.Millis(DateTimeOffset.UtcNow), Metrics = [SparkplugMetric.Of("bdSeq", SparkplugDataType.UInt64, BdSeq)] }.Encode();
        _mqtt = MqttEndpoint.Create(o =>
        {
            _options.Mqtt(o);
            o.ClientId ??= $"{_options.Group}-{_options.EdgeNode}";
            o.WithWill(Topic(SparkplugMessageType.NDeath), death, retain: false);
            if (_options.Logger is { } l) o.WithLogger(l);
        });
        await _mqtt.ConnectAsync(ct).ConfigureAwait(false);
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(() => CommandLoopAsync($"{SparkplugTopic.Namespace}/{_options.Group}/NCMD/{_options.EdgeNode}", token), CancellationToken.None);
        _ = Task.Run(() => CommandLoopAsync($"{SparkplugTopic.Namespace}/{_options.Group}/DCMD/{_options.EdgeNode}/+", token), CancellationToken.None);
        await Task.Delay(100, ct).ConfigureAwait(false);
        await BirthAsync(ct).ConfigureAwait(false);
    }

    private async Task CommandLoopAsync(string filter, CancellationToken ct)
    {
        try
        {
            await foreach (var m in _mqtt!.SubscribeAsync(filter, ct).ConfigureAwait(false)) await OnCommandAsync(m.Topic, m.Payload, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Publishes NBIRTH (seq 0) and every DBIRTH.</summary>
    public async Task BirthAsync(CancellationToken ct = default)
    {
        await _publish.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Interlocked.Exchange(ref _seq, 0);
            var now = SparkplugPayload.Millis(DateTimeOffset.UtcNow);
            var node = new SparkplugPayload
            {
                Timestamp = now,
                Seq = 0,
                Metrics = [SparkplugMetric.Of("bdSeq", SparkplugDataType.UInt64, BdSeq), .. _tags.Values.OrderBy(t => t.Alias).Select(t => t.Birth())],
            };
            await PublishAsync(SparkplugMessageType.NBirth, null, node, ct).ConfigureAwait(false);
            foreach (var d in _devices.Values.OrderBy(d => d.Id, StringComparer.Ordinal))
                await PublishAsync(SparkplugMessageType.DBirth, d.Id, new SparkplugPayload { Timestamp = now, Seq = NextSeq(), Metrics = [.. d.Tags.Values.OrderBy(t => t.Alias).Select(t => t.Birth())] }, ct).ConfigureAwait(false);
        }
        finally
        {
            _publish.Release();
        }
    }

    private async Task PublishAsync(SparkplugMessageType type, string? device, SparkplugPayload payload, CancellationToken ct)
    {
        var topic = new SparkplugTopic(type, _options.Group, _options.EdgeNode, device);
        await _mqtt!.PublishAsync(topic.ToString(), payload.Encode(), new PublishOptions { QualityOfService = QualityOfService.AtMostOnce, ContentType = "application/x-protobuf" }, ct).ConfigureAwait(false);
        Published?.Invoke(this, (topic, payload));
    }

    internal async Task PublishDataAsync(SparkplugDevice? device, IReadOnlyDictionary<string, object?> values, CancellationToken ct)
    {
        var tags = device?.Tags ?? _tags;
        var changed = new List<SparkplugMetric>();
        foreach (var (name, value) in values)
        {
            if (!tags.TryGetValue(name, out var tag)) throw new KeyNotFoundException($"Metric '{name}' is not defined{(device is null ? "" : $" on device {device.Id}")}.");
            tag.Value = value;
            changed.Add(tag.Data() with { Timestamp = SparkplugPayload.Millis(DateTimeOffset.UtcNow) });
        }

        if (_mqtt is null || changed.Count == 0) return;
        await _publish.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await PublishAsync(device is null ? SparkplugMessageType.NData : SparkplugMessageType.DData, device?.Id,
                new SparkplugPayload { Timestamp = SparkplugPayload.Millis(DateTimeOffset.UtcNow), Seq = NextSeq(), Metrics = changed }, ct).ConfigureAwait(false);
        }
        finally
        {
            _publish.Release();
        }
    }

    /// <summary>Changes node metrics and publishes NDATA.</summary>
    public Task SetAsync(string name, object? value, CancellationToken ct = default) => PublishDataAsync(null, new Dictionary<string, object?> { [name] = value }, ct);

    private async Task OnCommandAsync(string topicText, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (!SparkplugTopic.TryParse(topicText, out var topic) || topic!.Type is not (SparkplugMessageType.NCmd or SparkplugMessageType.DCmd)) return;
        SparkplugPayload p;
        try
        {
            p = SparkplugPayload.Decode(payload.Span);
        }
        catch (ProtocolException)
        {
            return;
        }

        var device = topic.Device is { } id && _devices.TryGetValue(id, out var d) ? d : null;
        if (topic.Type == SparkplugMessageType.DCmd && device is null) return;
        var tags = device?.Tags ?? _tags;
        var updates = new Dictionary<string, object?>();
        foreach (var m in p.Metrics)
        {
            var tag = m.Name is { } n && tags.TryGetValue(n, out var byName) ? byName : tags.Values.FirstOrDefault(t => t.Alias == m.Alias);
            if (tag is null) continue;
            if (device is null && tag.Name == "Node Control/Rebirth" && m.Value is true)
            {
                await BirthAsync(ct).ConfigureAwait(false);
                continue;
            }

            if (!tag.Writable || !_options.AcceptWrites) continue;
            updates[tag.Name] = m.Value;
        }

        if (updates.Count == 0) return;
        await PublishDataAsync(device, updates, ct).ConfigureAwait(false);
        foreach (var (name, value) in updates) CommandReceived?.Invoke(this, new SparkplugCommand(device?.Id, name, value));
    }

    /// <summary>Publishes DDEATH for every device and NDEATH, then disconnects (the next session uses bdSeq + 1).</summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_mqtt is null) return;
        try
        {
            foreach (var d in _devices.Values)
                await PublishAsync(SparkplugMessageType.DDeath, d.Id, new SparkplugPayload { Timestamp = SparkplugPayload.Millis(DateTimeOffset.UtcNow), Seq = NextSeq() }, ct).ConfigureAwait(false);
            await PublishAsync(SparkplugMessageType.NDeath, null, new SparkplugPayload { Timestamp = SparkplugPayload.Millis(DateTimeOffset.UtcNow), Metrics = [SparkplugMetric.Of("bdSeq", SparkplugDataType.UInt64, BdSeq)] }, ct).ConfigureAwait(false);
        }
        catch (IoTComException)
        {
        }

        if (_cts is not null) await _cts.CancelAsync().ConfigureAwait(false);
        await _mqtt.DisposeAsync().ConfigureAwait(false);
        _cts?.Dispose();
        (_mqtt, _cts) = (null, null);
        BdSeq = (BdSeq + 1) % 256;
    }

    /// <summary>Simulates a lost connection: the broker publishes the NDEATH will (for tests and demos).</summary>
    public async Task DropConnectionAsync()
    {
        if (_mqtt is null) return;
        if (_cts is not null) await _cts.CancelAsync().ConfigureAwait(false);
        await _mqtt.AbortAsync().ConfigureAwait(false);
        await _mqtt.DisposeAsync().ConfigureAwait(false);
        _cts?.Dispose();
        (_mqtt, _cts) = (null, null);
        BdSeq = (BdSeq + 1) % 256;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _publish.Dispose();
    }
}

/// <summary>An edge node or device as seen by a host application.</summary>
public sealed class SparkplugNodeView
{
    internal SparkplugNodeView(string group, string node, string? device)
    {
        Group = group;
        EdgeNode = node;
        Device = device;
    }

    internal readonly ConcurrentDictionary<ulong, string> Aliases = new();

    /// <summary>Group.</summary>
    public string Group { get; }

    /// <summary>Edge node.</summary>
    public string EdgeNode { get; }

    /// <summary>Device (null for the node itself).</summary>
    public string? Device { get; }

    /// <summary>Online (born and not dead).</summary>
    public bool Online { get; internal set; }

    /// <summary>bdSeq of the current session (node only).</summary>
    public ulong? BdSeq { get; internal set; }

    /// <summary>Last metric values by name.</summary>
    public ConcurrentDictionary<string, SparkplugMetric> Metrics { get; } = new(StringComparer.Ordinal);

    /// <summary>Last message time.</summary>
    public DateTimeOffset LastSeen { get; internal set; }

    /// <summary>"Group/Node[/Device]".</summary>
    public string Key => Device is null ? $"{Group}/{EdgeNode}" : $"{Group}/{EdgeNode}/{Device}";

    /// <inheritdoc />
    public override string ToString() => $"{Key} {(Online ? "online" : "offline")} {Metrics.Count} metrics";
}

/// <summary>Host application options.</summary>
public sealed class SparkplugHostOptions
{
    /// <summary>Host application id (STATE topic).</summary>
    public string HostId { get; set; } = "IoTComHost";

    /// <summary>MQTT connection.</summary>
    public Action<MqttEndpointOptions> Mqtt { get; set; } = o => o.UseBroker("localhost");

    /// <summary>Ask a node to rebirth when its sequence numbers jump or a message refers to unknown aliases.</summary>
    public bool RequestRebirthOnGap { get; set; } = true;

    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }
}

/// <summary>
/// A Sparkplug B host application: publishes its STATE (online, retained; offline as will), follows births, deaths
/// and data from every edge node, resolves aliases, detects sequence gaps and asks for a rebirth, and sends commands.
/// </summary>
public sealed class SparkplugHost : IAsyncDisposable
{
    private readonly SparkplugHostOptions _options;
    private readonly ConcurrentDictionary<string, SparkplugNodeView> _views = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ulong> _lastSeq = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _rebirthAsked = new(StringComparer.Ordinal);
    private MqttEndpoint? _mqtt;
    private CancellationTokenSource? _cts;

    private SparkplugHost(SparkplugHostOptions options) => _options = options;

    /// <summary>Creates a host application.</summary>
    public static SparkplugHost Create(Action<SparkplugHostOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var o = new SparkplugHostOptions();
        configure(o);
        return new SparkplugHost(o);
    }

    /// <summary>Raised for every metric value received (birth or data).</summary>
    public event EventHandler<(SparkplugNodeView View, SparkplugMetric Metric)>? MetricUpdated;

    /// <summary>Raised when a node or device goes online or offline.</summary>
    public event EventHandler<SparkplugNodeView>? StateChanged;

    /// <summary>Raised for every Sparkplug message (topic, payload) — for logs and demos.</summary>
    public event EventHandler<(SparkplugTopic Topic, SparkplugPayload? Payload)>? MessageReceived;

    /// <summary>Rebirth requests sent.</summary>
    public int RebirthRequests { get; private set; }

    /// <summary>Nodes and devices seen.</summary>
    public IReadOnlyCollection<SparkplugNodeView> Views => [.. _views.Values];

    /// <summary>The MQTT endpoint.</summary>
    public MqttEndpoint? Mqtt => _mqtt;

    /// <summary>A node or device view.</summary>
    public SparkplugNodeView? Find(string group, string node, string? device = null) => _views.GetValueOrDefault(device is null ? $"{group}/{node}" : $"{group}/{node}/{device}");

    private static byte[] StateJson(bool online) =>
        Encoding.UTF8.GetBytes($"{{\"online\":{(online ? "true" : "false")},\"timestamp\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}}}");

    /// <summary>Connects (STATE offline as will), publishes STATE online and subscribes to the namespace.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_mqtt is not null) return;
        var stateTopic = new SparkplugTopic(SparkplugMessageType.State, "", _options.HostId).ToString();
        _mqtt = MqttEndpoint.Create(o =>
        {
            _options.Mqtt(o);
            o.ClientId ??= $"host-{_options.HostId}";
            o.WithWill(stateTopic, StateJson(false), retain: true);
            if (_options.Logger is { } l) o.WithLogger(l);
        });
        await _mqtt.ConnectAsync(ct).ConfigureAwait(false);
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var subscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () =>
        {
            try
            {
                var stream = _mqtt.SubscribeAsync($"{SparkplugTopic.Namespace}/#", token).GetAsyncEnumerator(token);
                await using var _ = stream.ConfigureAwait(false);
                var next = stream.MoveNextAsync();
                subscribed.TrySetResult();
                while (await next.ConfigureAwait(false))
                {
                    await OnMessageAsync(stream.Current.Topic, stream.Current.Payload, token).ConfigureAwait(false);
                    next = stream.MoveNextAsync();
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);
        await subscribed.Task.WaitAsync(ct).ConfigureAwait(false);
        await Task.Delay(100, ct).ConfigureAwait(false);
        await _mqtt.PublishAsync(stateTopic, StateJson(true), new PublishOptions { QualityOfService = QualityOfService.AtLeastOnce, Retain = true }, ct).ConfigureAwait(false);
    }

    private SparkplugNodeView View(SparkplugTopic t) => _views.GetOrAdd(t.Device is null ? $"{t.Group}/{t.EdgeNode}" : $"{t.Group}/{t.EdgeNode}/{t.Device}", _ => new SparkplugNodeView(t.Group, t.EdgeNode, t.Device));

    private async Task OnMessageAsync(string topicText, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (!SparkplugTopic.TryParse(topicText, out var topic)) return;
        if (topic!.Type == SparkplugMessageType.State)
        {
            MessageReceived?.Invoke(this, (topic, null));
            return;
        }

        SparkplugPayload p;
        try
        {
            p = SparkplugPayload.Decode(payload.Span);
        }
        catch (ProtocolException)
        {
            return;
        }

        MessageReceived?.Invoke(this, (topic, p));
        if (topic.Type is SparkplugMessageType.NCmd or SparkplugMessageType.DCmd) return;
        var nodeKey = $"{topic.Group}/{topic.EdgeNode}";
        var view = View(topic);
        view.LastSeen = DateTimeOffset.UtcNow;

        if (topic.Type == SparkplugMessageType.NDeath)
        {
            var bd = p.Metrics.FirstOrDefault(m => m.Name == "bdSeq")?.Value as ulong?;
            if (bd is not null && view.BdSeq is not null && bd != view.BdSeq) return;   // a stale death from an older session
            view.Online = false;
            StateChanged?.Invoke(this, view);
            foreach (var dev in _views.Values.Where(v => v.Device is not null && v.Group == topic.Group && v.EdgeNode == topic.EdgeNode && v.Online))
            {
                dev.Online = false;
                StateChanged?.Invoke(this, dev);
            }

            return;
        }

        // Sequence numbers run across all messages of one edge node session.
        if (p.Seq is { } seq)
        {
            if (topic.Type == SparkplugMessageType.NBirth) _lastSeq[nodeKey] = seq;
            else if (_lastSeq.TryGetValue(nodeKey, out var last) && seq != ((last + 1) & 0xFF))
            {
                _lastSeq[nodeKey] = seq;
                if (_options.RequestRebirthOnGap) await AskRebirthOnceAsync(topic.Group, topic.EdgeNode, ct).ConfigureAwait(false);
            }
            else
            {
                _lastSeq[nodeKey] = seq;
            }
        }

        switch (topic.Type)
        {
            case SparkplugMessageType.NBirth or SparkplugMessageType.DBirth:
                view.Aliases.Clear();
                view.Metrics.Clear();
                if (topic.Type == SparkplugMessageType.NBirth)
                {
                    view.BdSeq = p.Metrics.FirstOrDefault(m => m.Name == "bdSeq")?.Value as ulong?;
                    _rebirthAsked.TryRemove(nodeKey, out _);
                }

                foreach (var m in p.Metrics)
                {
                    if (m.Name is null) continue;
                    if (m.Alias is { } a) view.Aliases[a] = m.Name;
                    view.Metrics[m.Name] = m;
                    MetricUpdated?.Invoke(this, (view, m));
                }

                if (!view.Online)
                {
                    view.Online = true;
                    StateChanged?.Invoke(this, view);
                }

                break;
            case SparkplugMessageType.DDeath:
                view.Online = false;
                StateChanged?.Invoke(this, view);
                break;
            case SparkplugMessageType.NData or SparkplugMessageType.DData:
                if (!view.Online)
                {
                    // Data before a birth (we joined late, or lost the birth): ask for one.
                    if (_options.RequestRebirthOnGap) await AskRebirthOnceAsync(topic.Group, topic.EdgeNode, ct).ConfigureAwait(false);
                    break;
                }

                foreach (var m in p.Metrics)
                {
                    var name = m.Name ?? (m.Alias is { } a && view.Aliases.TryGetValue(a, out var n) ? n : null);
                    if (name is null) continue;
                    var resolved = m with { Name = name };
                    view.Metrics[name] = resolved;
                    MetricUpdated?.Invoke(this, (view, resolved));
                }

                break;
        }
    }

    // One request per node until its NBIRTH arrives (or five seconds pass): several devices' data can arrive first.
    private Task AskRebirthOnceAsync(string group, string node, CancellationToken ct)
    {
        var key = $"{group}/{node}";
        var now = DateTimeOffset.UtcNow;
        if (_rebirthAsked.TryGetValue(key, out var asked) && now - asked < TimeSpan.FromSeconds(5)) return Task.CompletedTask;
        _rebirthAsked[key] = now;
        return RequestRebirthAsync(group, node, ct);
    }

    /// <summary>Sends "Node Control/Rebirth" to an edge node.</summary>
    public async Task RequestRebirthAsync(string group, string node, CancellationToken ct = default)
    {
        RebirthRequests++;
        await SendAsync(new SparkplugTopic(SparkplugMessageType.NCmd, group, node), [SparkplugMetric.Of("Node Control/Rebirth", SparkplugDataType.Boolean, true)], ct).ConfigureAwait(false);
    }

    /// <summary>Writes a metric on a node (NCMD) or device (DCMD); the type comes from the birth certificate.</summary>
    public async Task WriteAsync(string group, string node, string? device, string metric, object? value, CancellationToken ct = default)
    {
        var view = Find(group, node, device) ?? throw new KeyNotFoundException($"{group}/{node}{(device is null ? "" : "/" + device)} has not been born.");
        var type = view.Metrics.TryGetValue(metric, out var known) ? known.DataType : throw new KeyNotFoundException($"Metric '{metric}' is not in the birth certificate.");
        var topic = new SparkplugTopic(device is null ? SparkplugMessageType.NCmd : SparkplugMessageType.DCmd, group, node, device);
        await SendAsync(topic, [SparkplugMetric.Of(metric, type, value)], ct).ConfigureAwait(false);
    }

    private async Task SendAsync(SparkplugTopic topic, IReadOnlyList<SparkplugMetric> metrics, CancellationToken ct)
    {
        var mqtt = _mqtt ?? throw new InvalidOperationException("Start the host first.");
        var payload = new SparkplugPayload { Timestamp = SparkplugPayload.Millis(DateTimeOffset.UtcNow), Metrics = metrics };
        await mqtt.PublishAsync(topic.ToString(), payload.Encode(), new PublishOptions { QualityOfService = QualityOfService.AtMostOnce }, ct).ConfigureAwait(false);
    }

    /// <summary>Publishes STATE offline and disconnects.</summary>
    public async Task StopAsync()
    {
        if (_mqtt is null) return;
        try
        {
            await _mqtt.PublishAsync(new SparkplugTopic(SparkplugMessageType.State, "", _options.HostId).ToString(), StateJson(false), new PublishOptions { QualityOfService = QualityOfService.AtLeastOnce, Retain = true }).ConfigureAwait(false);
        }
        catch (IoTComException)
        {
        }

        if (_cts is not null) await _cts.CancelAsync().ConfigureAwait(false);
        await _mqtt.DisposeAsync().ConfigureAwait(false);
        _cts?.Dispose();
        (_mqtt, _cts) = (null, null);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>Parses a STATE payload (Sparkplug 3.0 JSON).</summary>
    public static (bool Online, long Timestamp)? ParseState(ReadOnlySpan<byte> payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload.ToArray());
            return (doc.RootElement.GetProperty("online").GetBoolean(), doc.RootElement.GetProperty("timestamp").GetInt64());
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>
/// A simulated bottling line as a Sparkplug edge node: a filler (speed, bottle count, running — writable) and a
/// syrup tank (level, temperature, inlet valve — writable). Values change every <see cref="Step"/>.
/// </summary>
public sealed class SparkplugLineSimulator(SparkplugEdgeNode node, int seed = 5)
{
    private readonly Random _random = new(seed);
    private long _count = 120_400;
    private double _level = 62;

    /// <summary>Defines the devices and metrics on the edge node (call before StartAsync).</summary>
    public SparkplugLineSimulator Define()
    {
        node.Metric("Properties/Location", SparkplugDataType.String, "Cikarang, Line 1");
        node.Device("Filler")
            .Metric("Speed", SparkplugDataType.Float, 0f)
            .Metric("BottleCount", SparkplugDataType.Int64, _count)
            .Metric("Running", SparkplugDataType.Boolean, true, writable: true)
            .Metric("Setpoint", SparkplugDataType.Float, 120f, writable: true);
        node.Device("Tank7")
            .Metric("Level", SparkplugDataType.Double, _level)
            .Metric("Temperature", SparkplugDataType.Float, 21.5f)
            .Metric("InletValve", SparkplugDataType.Boolean, false, writable: true);
        return this;
    }

    /// <summary>Advances one second and publishes what changed (DDATA).</summary>
    public async Task Step(CancellationToken ct = default)
    {
        var filler = node.Devices["Filler"];
        var tank = node.Devices["Tank7"];
        var running = filler.Metrics.First(m => m.Name == "Running").Value is true;
        var setpoint = Convert.ToSingle(filler.Metrics.First(m => m.Name == "Setpoint").Value, CultureInfo.InvariantCulture);
        var speed = running ? setpoint + (float)((_random.NextDouble() - 0.5) * 4) : 0f;
        _count += (long)(speed / 60);
        var inlet = tank.Metrics.First(m => m.Name == "InletValve").Value is true;
        _level = Math.Clamp(_level + (inlet ? 0.6 : 0) - (speed / 1000.0), 0, 100);
        await filler.SetAsync(new Dictionary<string, object?> { ["Speed"] = speed, ["BottleCount"] = _count }, ct).ConfigureAwait(false);
        await tank.SetAsync(new Dictionary<string, object?> { ["Level"] = Math.Round(_level, 2), ["Temperature"] = 21.5f + (float)((_random.NextDouble() - 0.5) * 0.4) }, ct).ConfigureAwait(false);
    }
}
