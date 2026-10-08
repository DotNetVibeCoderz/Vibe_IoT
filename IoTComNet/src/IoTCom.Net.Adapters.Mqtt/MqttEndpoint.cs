using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace IoTCom.Net.Adapters.Mqtt;

/// <summary>Options for <see cref="MqttEndpoint"/>.</summary>
public sealed class MqttEndpointOptions
{
    /// <summary>Broker host.</summary>
    public string Host { get; set; } = "localhost";
    /// <summary>Broker port (1883, or 8883 with TLS).</summary>
    public int Port { get; set; } = 1883;
    /// <summary>Client id (random when null).</summary>
    public string? ClientId { get; set; }
    /// <summary>User name.</summary>
    public string? UserName { get; set; }
    /// <summary>Password (never logged).</summary>
    public string? Password { get; set; }
    /// <summary>Use TLS.</summary>
    public bool UseTls { get; set; }
    /// <summary>MQTT 5.0 (default) or 3.1.1.</summary>
    public bool UseMqtt5 { get; set; } = true;
    /// <summary>Keep-alive period.</summary>
    public TimeSpan KeepAlive { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>Clean start / clean session.</summary>
    public bool CleanSession { get; set; } = true;
    /// <summary>Reconnect policy.</summary>
    public ReconnectPolicy Reconnect { get; set; } = ReconnectPolicy.Default;
    /// <summary>Optional last-will topic.</summary>
    public string? WillTopic { get; set; }
    /// <summary>Optional last-will payload.</summary>
    public byte[]? WillPayload { get; set; }

    /// <summary>Retain the will message (Sparkplug host STATE).</summary>
    public bool WillRetain { get; set; }

    /// <summary>Will QoS.</summary>
    public QualityOfService WillQualityOfService { get; set; } = QualityOfService.AtLeastOnce;
    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }
    /// <summary>Endpoint name.</summary>
    public string? Name { get; set; }

    /// <summary>Sets broker address.</summary>
    public MqttEndpointOptions UseBroker(string host, int port = 1883) { Host = host; Port = port; return this; }
    /// <summary>Sets the client id.</summary>
    public MqttEndpointOptions WithClientId(string clientId) { ClientId = clientId; return this; }
    /// <summary>Sets credentials.</summary>
    public MqttEndpointOptions WithCredentials(string userName, string password) { UserName = userName; Password = password; return this; }
    /// <summary>Enables TLS.</summary>
    public MqttEndpointOptions WithTls(bool enabled = true) { UseTls = enabled; return this; }
    /// <summary>Uses MQTT 3.1.1.</summary>
    public MqttEndpointOptions UseMqtt311() { UseMqtt5 = false; return this; }
    /// <summary>Sets a last will.</summary>
    public MqttEndpointOptions WithWill(string topic, string payload) { WillTopic = topic; WillPayload = Encoding.UTF8.GetBytes(payload); return this; }

    /// <summary>Sets a binary last-will message (e.g. a Sparkplug NDEATH).</summary>
    public MqttEndpointOptions WithWill(string topic, byte[] payload, bool retain, QualityOfService qos = QualityOfService.AtLeastOnce) { (WillTopic, WillPayload, WillRetain, WillQualityOfService) = (topic, payload, retain, qos); return this; }
    /// <summary>Sets the reconnect policy.</summary>
    public MqttEndpointOptions WithReconnect(ReconnectPolicy policy) { Reconnect = policy; return this; }
    /// <summary>Sets the logger.</summary>
    public MqttEndpointOptions WithLogger(ILogger logger) { Logger = logger; return this; }
}

/// <summary>
/// MQTT client endpoint (adapter over MQTTnet). Publisher and subscriber in one object; subscriptions are
/// <see cref="IAsyncEnumerable{T}"/>, survive reconnects and are reference counted per filter.
/// </summary>
/// <example>
/// <code>
/// await using var mqtt = MqttEndpoint.Create(o => o.UseBroker("localhost"));
/// await mqtt.ConnectAsync();
/// await mqtt.PublishStringAsync("plant/line1/temp", "23.5");
/// await foreach (var m in mqtt.SubscribeAsync("plant/#")) Console.WriteLine(m.Topic);
/// </code>
/// </example>
public sealed class MqttEndpoint : EndpointBase, IClientEndpoint, IPublisher<ReadOnlyMemory<byte>>, ISubscriber<ReadOnlyMemory<byte>>
{
    private readonly MqttEndpointOptions _options;
    private readonly IMqttClient _client;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<Channel<Message<ReadOnlyMemory<byte>>>>> _subscriptions = new(StringComparer.Ordinal);
    private CancellationTokenSource? _reconnectCts;
    private bool _userDisconnected;

    private MqttEndpoint(MqttEndpointOptions options) : base("mqtt", options.Logger)
    {
        _options = options;
        Name = options.Name;
        _client = new MqttClientFactory().CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += OnMessageAsync;
        _client.DisconnectedAsync += OnDisconnectedAsync;
    }

    /// <summary>Creates an endpoint.</summary>
    public static MqttEndpoint Create(Action<MqttEndpointOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var o = new MqttEndpointOptions();
        configure(o);
        return new MqttEndpoint(o);
    }

    /// <summary>True when connected to the broker.</summary>
    public bool IsConnected => _client.IsConnected;

    /// <summary>Messages received since creation.</summary>
    public long MessagesReceived { get; private set; }

    /// <summary>Messages published since creation.</summary>
    public long MessagesPublished { get; private set; }

    /// <inheritdoc />
    public async ValueTask ConnectAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_client.IsConnected) return;
        _userDisconnected = false;
        SetState(EndpointState.Connecting);
        try
        {
            await _client.ConnectAsync(BuildOptions(), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetState(EndpointState.Disconnected, ex);
            throw new TransportException($"MQTT connect to {_options.Host}:{_options.Port} failed: {ex.Message}", ex);
        }
        await ResubscribeAsync(ct).ConfigureAwait(false);
        SetState(EndpointState.Connected);
        Logger.LogInformation("MQTT connected to {Host}:{Port}", _options.Host, _options.Port);
    }

    /// <inheritdoc />
    public async ValueTask DisconnectAsync(CancellationToken ct = default)
    {
        _userDisconnected = true;
        if (_reconnectCts is { } cts) await cts.CancelAsync().ConfigureAwait(false);
        if (_client.IsConnected)
        {
            SetState(EndpointState.Stopping);
            await _client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), ct).ConfigureAwait(false);
        }
        SetState(EndpointState.Disconnected);
    }

    /// <summary>
    /// Drops the connection without DISCONNECT, so the broker publishes the will; the endpoint cannot be used
    /// afterwards. Use it to simulate a lost device.
    /// </summary>
    public async ValueTask AbortAsync(CancellationToken ct = default)
    {
        _userDisconnected = true;
        if (_reconnectCts is { } cts) await cts.CancelAsync().ConfigureAwait(false);
        if (_client.IsConnected)
        {
            SetState(EndpointState.Stopping);
            _client.Dispose();
        }
        SetState(EndpointState.Disconnected);
    }

    /// <inheritdoc />
    public async ValueTask PublishAsync(string topic, ReadOnlyMemory<byte> message, PublishOptions? options = null, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        options ??= PublishOptions.Default;
        if (!_client.IsConnected) await ConnectAsync(ct).ConfigureAwait(false);
        var builder = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(message.ToArray())
            .WithQualityOfServiceLevel((MqttQualityOfServiceLevel)(int)options.QualityOfService)
            .WithRetainFlag(options.Retain);
        if (options.ContentType is not null && _options.UseMqtt5) builder.WithContentType(options.ContentType);
        Tap(FrameDirection.Outbound, message.Span, () => $"PUBLISH {topic} ({message.Length} B, QoS {(int)options.QualityOfService}{(options.Retain ? ", retain" : "")})");
        var result = await _client.PublishAsync(builder.Build(), ct).ConfigureAwait(false);
        if (!result.IsSuccess) throw new ProtocolException($"MQTT publish to '{topic}' failed: {result.ReasonCode} {result.ReasonString}");
        MessagesPublished++;
    }

    /// <summary>Publishes UTF-8 text.</summary>
    public ValueTask PublishStringAsync(string topic, string text, PublishOptions? options = null, CancellationToken ct = default)
        => PublishAsync(topic, Encoding.UTF8.GetBytes(text), options, ct);

    /// <summary>Publishes JSON using source-generated metadata (trim/AOT safe).</summary>
    public ValueTask PublishJsonAsync<T>(string topic, T value, JsonTypeInfo<T> typeInfo, PublishOptions? options = null, CancellationToken ct = default)
        => PublishAsync(topic, JsonSerializer.SerializeToUtf8Bytes(value, typeInfo), (options ?? PublishOptions.Default) with { ContentType = "application/json" }, ct);

    /// <inheritdoc />
    public async IAsyncEnumerable<Message<ReadOnlyMemory<byte>>> SubscribeAsync(string filter, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var channel = Channel.CreateBounded<Message<ReadOnlyMemory<byte>>>(new BoundedChannelOptions(4096) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        bool first;
        lock (_gate)
        {
            first = !_subscriptions.TryGetValue(filter, out var list);
            if (first) _subscriptions[filter] = list = [];
            list!.Add(channel);
        }
        try
        {
            if (first)
            {
                if (!_client.IsConnected) await ConnectAsync(ct).ConfigureAwait(false);
                else await SubscribeOnBrokerAsync(filter, ct).ConfigureAwait(false);
            }
            await foreach (var m in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false)) yield return m;
        }
        finally
        {
            bool last;
            lock (_gate)
            {
                var list = _subscriptions[filter];
                list.Remove(channel);
                last = list.Count == 0;
                if (last) _subscriptions.Remove(filter);
            }
            if (last && _client.IsConnected)
            {
                try { await _client.UnsubscribeAsync(filter, CancellationToken.None).ConfigureAwait(false); } catch { /* best effort */ }
            }
        }
    }

    /// <summary>Subscribes and decodes UTF-8 text.</summary>
    public async IAsyncEnumerable<Message<string>> SubscribeStringAsync(string filter, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var m in SubscribeAsync(filter, ct).ConfigureAwait(false))
            yield return new Message<string>(m.Topic, Encoding.UTF8.GetString(m.Payload.Span), m.Timestamp);
    }

    /// <summary>Subscribes and deserialises JSON (invalid payloads are skipped).</summary>
    public async IAsyncEnumerable<Message<T>> SubscribeJsonAsync<T>(string filter, JsonTypeInfo<T> typeInfo, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var m in SubscribeAsync(filter, ct).ConfigureAwait(false))
        {
            T? value;
            try { value = JsonSerializer.Deserialize(m.Payload.Span, typeInfo); }
            catch (JsonException) { continue; }
            if (value is not null) yield return new Message<T>(m.Topic, value, m.Timestamp);
        }
    }

    private MqttClientOptions BuildOptions()
    {
        var b = new MqttClientOptionsBuilder()
            .WithTcpServer(_options.Host, _options.Port)
            .WithClientId(_options.ClientId ?? $"iotcom-{Guid.NewGuid():N}"[..20])
            .WithKeepAlivePeriod(_options.KeepAlive)
            .WithCleanSession(_options.CleanSession)
            .WithProtocolVersion(_options.UseMqtt5 ? MqttProtocolVersion.V500 : MqttProtocolVersion.V311);
        if (_options.UserName is not null) b.WithCredentials(_options.UserName, _options.Password);
        if (_options.UseTls) b.WithTlsOptions(o => o.UseTls());
        if (_options.WillTopic is not null)
            b.WithWillTopic(_options.WillTopic).WithWillPayload(_options.WillPayload ?? []).WithWillRetain(_options.WillRetain)
                .WithWillQualityOfServiceLevel((MqttQualityOfServiceLevel)(int)_options.WillQualityOfService);
        return b.Build();
    }

    private async Task SubscribeOnBrokerAsync(string filter, CancellationToken ct)
    {
        var options = new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter(f => f.WithTopic(filter).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
            .Build();
        await _client.SubscribeAsync(options, ct).ConfigureAwait(false);
    }

    private async Task ResubscribeAsync(CancellationToken ct)
    {
        string[] filters;
        lock (_gate) filters = [.. _subscriptions.Keys];
        foreach (var f in filters) await SubscribeOnBrokerAsync(f, ct).ConfigureAwait(false);
    }

    private Task OnMessageAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        var msg = e.ApplicationMessage;
        var payload = msg.Payload.ToArray();
        MessagesReceived++;
        Tap(FrameDirection.Inbound, payload, () => $"PUBLISH {msg.Topic} ({payload.Length} B)");
        var message = new Message<ReadOnlyMemory<byte>>(msg.Topic, payload, DateTimeOffset.UtcNow);
        List<Channel<Message<ReadOnlyMemory<byte>>>> targets = [];
        lock (_gate)
        {
            foreach (var (filter, channels) in _subscriptions)
                if (MqttTopicFilterComparer.Compare(msg.Topic, filter) == MqttTopicFilterCompareResult.IsMatch) targets.AddRange(channels);
        }
        foreach (var t in targets) t.Writer.TryWrite(message);
        return Task.CompletedTask;
    }

    private Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs e)
    {
        if (_userDisconnected || IsDisposed) return Task.CompletedTask;
        SetState(EndpointState.Disconnected, e.Exception);
        if (!_options.Reconnect.Enabled) return Task.CompletedTask;
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _reconnectCts, cts)?.Dispose();
        _ = Task.Run(() => ReconnectLoopAsync(cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task ReconnectLoopAsync(CancellationToken ct)
    {
        for (var attempt = 1; _options.Reconnect.CanRetry(attempt) && !ct.IsCancellationRequested; attempt++)
        {
            try
            {
                await Task.Delay(_options.Reconnect.GetDelay(attempt), ct).ConfigureAwait(false);
                IoTComDiagnostics.Reconnects.Add(1, new KeyValuePair<string, object?>("protocol", Protocol));
                await ConnectAsync(ct).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "MQTT reconnect attempt {Attempt} failed", attempt);
            }
        }
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore()
    {
        try { await DisconnectAsync().ConfigureAwait(false); } catch { /* shutting down */ }
        lock (_gate)
        {
            foreach (var list in _subscriptions.Values)
                foreach (var c in list) c.Writer.TryComplete();
        }
        _client.Dispose();
        _reconnectCts?.Dispose();
    }
}
