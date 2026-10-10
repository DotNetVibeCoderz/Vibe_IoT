using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Adapters.Kafka;

/// <summary>Options for <see cref="KafkaEndpoint"/>.</summary>
public sealed class KafkaEndpointOptions
{
    /// <summary>Bootstrap servers, comma separated (default localhost:9092).</summary>
    public string BootstrapServers { get; set; } = "localhost:9092";

    /// <summary>Client id shown to the brokers (default "iotcom").</summary>
    public string ClientId { get; set; } = "iotcom";

    /// <summary>Consumer group id. When null every subscription gets its own random group (every subscriber sees every message).</summary>
    public string? GroupId { get; set; }

    /// <summary>Where a new group starts reading (default earliest).</summary>
    public AutoOffsetReset AutoOffsetReset { get; set; } = AutoOffsetReset.Earliest;

    /// <summary>Security protocol (plaintext by default).</summary>
    public SecurityProtocol SecurityProtocol { get; set; } = SecurityProtocol.Plaintext;

    /// <summary>SASL mechanism, when SASL is used.</summary>
    public SaslMechanism? SaslMechanism { get; set; }

    /// <summary>SASL user name.</summary>
    public string? SaslUsername { get; set; }

    /// <summary>SASL password. A secret: it is never logged.</summary>
    public string? SaslPassword { get; set; }

    /// <summary>Path of the CA certificate(s) used to verify the brokers.</summary>
    public string? SslCaLocation { get; set; }

    /// <summary>Path of the client certificate (mutual TLS).</summary>
    public string? SslCertificateLocation { get; set; }

    /// <summary>Path of the client private key (mutual TLS).</summary>
    public string? SslKeyLocation { get; set; }

    /// <summary>Password of the client private key. A secret: it is never logged.</summary>
    public string? SslKeyPassword { get; set; }

    /// <summary>Time allowed for a connection check and for a confirmed publish (default 10 s).</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Extra librdkafka settings applied last (e.g. <c>linger.ms</c>). Values are never logged.</summary>
    public Dictionary<string, string> Extra { get; } = new(StringComparer.Ordinal);

    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }

    /// <summary>Endpoint name.</summary>
    public string? Name { get; set; }

    /// <summary>Uses bootstrap servers.</summary>
    public KafkaEndpointOptions UseBootstrap(string servers)
    {
        BootstrapServers = servers;
        return this;
    }

    /// <summary>Sets the consumer group id.</summary>
    public KafkaEndpointOptions WithGroup(string groupId)
    {
        GroupId = groupId;
        return this;
    }

    /// <summary>Uses SASL (with TLS when <paramref name="tls"/>).</summary>
    public KafkaEndpointOptions WithSasl(SaslMechanism mechanism, string userName, string password, bool tls = true)
    {
        (SaslMechanism, SaslUsername, SaslPassword) = (mechanism, userName, password);
        SecurityProtocol = tls ? SecurityProtocol.SaslSsl : SecurityProtocol.SaslPlaintext;
        return this;
    }

    /// <summary>Uses TLS with an optional CA file.</summary>
    public KafkaEndpointOptions WithTls(string? caLocation = null)
    {
        SecurityProtocol = SaslMechanism is null ? SecurityProtocol.Ssl : SecurityProtocol.SaslSsl;
        SslCaLocation = caLocation;
        return this;
    }
}

/// <summary>A message received from Kafka, with its key, partition and offset.</summary>
/// <param name="Topic">Topic.</param>
/// <param name="Key">Record key (empty when none).</param>
/// <param name="Value">Record value.</param>
/// <param name="Partition">Partition.</param>
/// <param name="Offset">Offset in the partition.</param>
/// <param name="Headers">Record headers, values decoded as UTF-8.</param>
public sealed record KafkaReceived(string Topic, ReadOnlyMemory<byte> Key, ReadOnlyMemory<byte> Value, int Partition, long Offset, IReadOnlyDictionary<string, string> Headers);

/// <summary>
/// Apache Kafka as an IoTCom.Net endpoint, an adapter over Confluent.Kafka (not a rewrite). QoS maps to producer
/// settings: <see cref="QualityOfService.AtMostOnce"/> = acks 0, <see cref="QualityOfService.AtLeastOnce"/> = acks 1
/// (leader), <see cref="QualityOfService.ExactlyOnce"/> = acks all with the idempotent producer. Subscriptions run a
/// background consume loop; offsets are committed after each message has been yielded and the consumer asked for the
/// next one (at-least-once). A filter starting with <c>^</c> is a regular expression over topic names.
/// </summary>
public sealed class KafkaEndpoint : EndpointBase, IClientEndpoint, IPublisher<ReadOnlyMemory<byte>>, ISubscriber<ReadOnlyMemory<byte>>
{
    private readonly KafkaEndpointOptions _options;
    private readonly ConcurrentDictionary<QualityOfService, IProducer<byte[], byte[]>> _producers = new();
    private readonly CancellationTokenSource _lifetime = new();
    private bool _connected;
    private long _received, _published;

    private KafkaEndpoint(KafkaEndpointOptions options) : base("kafka", options.Logger)
    {
        _options = options;
        Name = options.Name;
    }

    /// <summary>Creates an endpoint.</summary>
    public static KafkaEndpoint Create(Action<KafkaEndpointOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var o = new KafkaEndpointOptions();
        configure(o);
        return new KafkaEndpoint(o);
    }

    /// <summary>True after a successful <see cref="ConnectAsync"/>.</summary>
    public bool IsConnected => _connected;

    /// <summary>Messages received by subscriptions.</summary>
    public long MessagesReceived => Interlocked.Read(ref _received);

    /// <summary>Messages published (acknowledged as the QoS requires).</summary>
    public long MessagesPublished => Interlocked.Read(ref _published);

    private void ApplyCommon(ClientConfig c)
    {
        c.BootstrapServers = _options.BootstrapServers;
        c.ClientId = _options.ClientId;
        c.SecurityProtocol = _options.SecurityProtocol;
        c.SaslMechanism = _options.SaslMechanism;
        c.SaslUsername = _options.SaslUsername;
        c.SaslPassword = _options.SaslPassword;
        c.SslCaLocation = _options.SslCaLocation;
        c.SslCertificateLocation = _options.SslCertificateLocation;
        c.SslKeyLocation = _options.SslKeyLocation;
        c.SslKeyPassword = _options.SslKeyPassword;
        foreach (var (k, v) in _options.Extra) c.Set(k, v);
    }

    private void LogLine(LogMessage m) => Logger.LogDebug("librdkafka {Facility}: {Message}", m.Facility, m.Message);

    /// <summary>Checks that a broker answers (fetches cluster metadata). Producers and consumers are created on demand.</summary>
    public async ValueTask ConnectAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_connected) return;
        SetState(EndpointState.Connecting);
        try
        {
            await Task.Run(() =>
            {
                var cfg = new AdminClientConfig();
                ApplyCommon(cfg);
                using var admin = new AdminClientBuilder(cfg).SetLogHandler((_, m) => LogLine(m)).Build();
                admin.GetMetadata(_options.Timeout);
            }, ct).ConfigureAwait(false);
        }
        catch (KafkaException ex)
        {
            SetState(EndpointState.Disconnected, ex);
            throw new TransportException($"Cannot reach Kafka at {_options.BootstrapServers}: {ex.Error.Reason}", ex);
        }

        _connected = true;
        SetState(EndpointState.Connected);
    }

    /// <inheritdoc />
    public async ValueTask DisconnectAsync(CancellationToken ct = default)
    {
        if (!_connected && _producers.IsEmpty) return;
        _connected = false;
        SetState(EndpointState.Stopping);
        foreach (var key in _producers.Keys.ToArray())
        {
            if (!_producers.TryRemove(key, out var p)) continue;
            try
            {
                await Task.Run(() => p.Flush(TimeSpan.FromSeconds(5)), CancellationToken.None).ConfigureAwait(false);
            }
            catch (KafkaException)
            {
            }

            p.Dispose();
        }

        SetState(EndpointState.Disconnected);
    }

    private IProducer<byte[], byte[]> Producer(QualityOfService qos) =>
        _producers.GetOrAdd(qos, q =>
        {
            var cfg = new ProducerConfig();
            ApplyCommon(cfg);
            switch (q)
            {
                case QualityOfService.AtMostOnce:
                    cfg.Acks = Acks.None;
                    break;
                case QualityOfService.AtLeastOnce:
                    cfg.Acks = Acks.Leader;
                    break;
                default:
                    cfg.Acks = Acks.All;
                    cfg.EnableIdempotence = true;
                    break;
            }

            return new ProducerBuilder<byte[], byte[]>(cfg).SetLogHandler((_, m) => LogLine(m)).Build();
        });

    /// <summary>Publishes a record without a key.</summary>
    public ValueTask PublishAsync(string topic, ReadOnlyMemory<byte> message, PublishOptions? options = null, CancellationToken ct = default) =>
        PublishAsync(topic, ReadOnlyMemory<byte>.Empty, message, null, options, ct);

    /// <summary>Publishes a record with an optional key (records with the same key go to the same partition) and headers.</summary>
    /// <exception cref="DeviceException">The broker refused the record.</exception>
    public async ValueTask PublishAsync(string topic, ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> value,
        IReadOnlyDictionary<string, string>? headers = null, PublishOptions? options = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(topic);
        ThrowIfDisposed();
        options ??= PublishOptions.Default;
        var producer = Producer(options.QualityOfService);
        var record = new Message<byte[], byte[]> { Key = key.IsEmpty ? null! : key.ToArray(), Value = value.ToArray() };
        if (headers is { Count: > 0 } || options.ContentType is not null)
        {
            record.Headers = [];
            foreach (var (k, v) in headers ?? new Dictionary<string, string>()) record.Headers.Add(k, Encoding.UTF8.GetBytes(v));
            if (options.ContentType is not null) record.Headers.Add("content-type", Encoding.UTF8.GetBytes(options.ContentType));
        }

        Tap(FrameDirection.Outbound, value.Span, () => $"PRODUCE {topic} ({value.Length} B, {options.QualityOfService})");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_options.Timeout);
            await producer.ProduceAsync(topic, record, timeout.Token).ConfigureAwait(false);
        }
        catch (ProduceException<byte[], byte[]> ex)
        {
            throw new DeviceException($"Kafka refused the record for \"{topic}\": {ex.Error.Reason}", ex.Error.IsFatal ? 1 : 0);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IoTComTimeoutException($"Kafka did not acknowledge the record for \"{topic}\" within {_options.Timeout.TotalSeconds:0.#} s.");
        }

        Interlocked.Increment(ref _published);
    }

    /// <summary>Subscribes to a topic, or to every topic matching a regular expression when the filter starts with <c>^</c>.</summary>
    public async IAsyncEnumerable<Message<ReadOnlyMemory<byte>>> SubscribeAsync(string filter, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var m in ReceiveAsync(filter, ct).ConfigureAwait(false))
            yield return new Message<ReadOnlyMemory<byte>>(m.Topic, m.Value, DateTimeOffset.UtcNow);
    }

    /// <summary>Subscribes and yields full records (key, partition, offset, headers).</summary>
    public async IAsyncEnumerable<KafkaReceived> ReceiveAsync(string filter, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(filter);
        ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        var cfg = new ConsumerConfig();
        ApplyCommon(cfg);
        cfg.GroupId = _options.GroupId ?? $"iotcom-{Guid.NewGuid():N}";
        cfg.AutoOffsetReset = _options.AutoOffsetReset;
        cfg.EnableAutoCommit = false;
        cfg.AllowAutoCreateTopics = true;

        var channel = Channel.CreateBounded<ConsumeResult<byte[], byte[]>>(new BoundedChannelOptions(256) { SingleReader = true, SingleWriter = true });
        var commits = new ConcurrentQueue<ConsumeResult<byte[], byte[]>>();
        var loop = Task.Run(() => ConsumeLoop(cfg, filter, channel.Writer, commits, linked.Token), CancellationToken.None);
        try
        {
            await foreach (var result in channel.Reader.ReadAllAsync(linked.Token).ConfigureAwait(false))
            {
                var value = result.Message.Value ?? [];
                Interlocked.Increment(ref _received);
                Tap(FrameDirection.Inbound, value, () => $"FETCH {result.Topic}[{result.Partition.Value}]@{result.Offset.Value} ({value.Length} B)");
                var headers = new Dictionary<string, string>(StringComparer.Ordinal);
                if (result.Message.Headers is { } h)
                    foreach (var header in h) headers[header.Key] = Encoding.UTF8.GetString(header.GetValueBytes());
                yield return new KafkaReceived(result.Topic, result.Message.Key ?? [], value, result.Partition.Value, result.Offset.Value, headers);
                commits.Enqueue(result);
            }
        }
        finally
        {
            await linked.CancelAsync().ConfigureAwait(false);
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private void ConsumeLoop(ConsumerConfig cfg, string filter, ChannelWriter<ConsumeResult<byte[], byte[]>> writer, ConcurrentQueue<ConsumeResult<byte[], byte[]>> commits, CancellationToken ct)
    {
        Exception? failure = null;
        IConsumer<byte[], byte[]>? consumer = null;
        try
        {
            consumer = new ConsumerBuilder<byte[], byte[]>(cfg).SetLogHandler((_, m) => LogLine(m)).Build();
            consumer.Subscribe(filter);
            while (!ct.IsCancellationRequested)
            {
                while (commits.TryDequeue(out var done))
                {
                    try
                    {
                        consumer.Commit(done);
                    }
                    catch (KafkaException ex)
                    {
                        Logger.LogWarning("Kafka commit failed: {Reason}", ex.Error.Reason);
                    }
                }

                ConsumeResult<byte[], byte[]>? result;
                try
                {
                    result = consumer.Consume(TimeSpan.FromMilliseconds(100));
                }
                catch (ConsumeException ex) when (!ex.Error.IsFatal)
                {
                    Logger.LogWarning("Kafka consume error: {Reason}", ex.Error.Reason);
                    continue;
                }

                if (result is null || result.IsPartitionEOF) continue;
                // bounded channel: wait for room without blocking shutdown
                while (!writer.TryWrite(result))
                {
                    if (ct.IsCancellationRequested) return;
                    Thread.Sleep(10);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (KafkaException ex)
        {
            failure = new TransportException($"Kafka consumer failed: {ex.Error.Reason}", ex);
        }
        finally
        {
            while (commits.TryDequeue(out var done))
            {
                try
                {
                    consumer?.Commit(done);
                }
                catch (KafkaException)
                {
                }
            }

            try
            {
                consumer?.Close();
            }
            catch (KafkaException)
            {
            }

            consumer?.Dispose();
            writer.TryComplete(failure);
        }
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await DisconnectAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }
}
