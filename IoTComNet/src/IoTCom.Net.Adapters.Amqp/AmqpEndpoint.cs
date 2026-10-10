using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Amqp;
using Amqp.Framing;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Adapters.Amqp;

/// <summary>Options for <see cref="AmqpEndpoint"/>.</summary>
public sealed class AmqpEndpointOptions
{
    /// <summary>Broker address, e.g. <c>amqp://host:5672</c> or <c>amqps://user:pass@host:5671</c>.</summary>
    public string Address { get; set; } = "amqp://localhost:5672";

    /// <summary>User name; overrides any user in <see cref="Address"/>.</summary>
    public string? UserName { get; set; }

    /// <summary>Password. A secret: it is never logged.</summary>
    public string? Password { get; set; }

    /// <summary>AMQP container id (random when null).</summary>
    public string? ContainerId { get; set; }

    /// <summary>Time to wait for the broker to settle a confirmed publish (default 10 s).</summary>
    public TimeSpan SendTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Link credit granted to a subscription: how many messages the broker may have in flight (default 100).</summary>
    public int ReceiverCredit { get; set; } = 100;

    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }

    /// <summary>Endpoint name.</summary>
    public string? Name { get; set; }

    /// <summary>Uses a broker address.</summary>
    public AmqpEndpointOptions UseBroker(string address)
    {
        Address = address;
        return this;
    }

    /// <summary>Authenticates with SASL PLAIN.</summary>
    public AmqpEndpointOptions WithCredentials(string userName, string password)
    {
        (UserName, Password) = (userName, password);
        return this;
    }
}

/// <summary>
/// AMQP 1.0 as an IoTCom.Net endpoint, an adapter over AMQPNetLite (not a rewrite). Publishes to and subscribes from node
/// addresses (queues or topics) of any AMQP 1.0 broker. QoS maps to settlement: <see cref="QualityOfService.AtMostOnce"/>
/// uses a pre-settled link (fire and forget); <see cref="QualityOfService.AtLeastOnce"/> and
/// <see cref="QualityOfService.ExactlyOnce"/> wait for the broker's outcome and throw <see cref="DeviceException"/> when
/// the message is rejected or released. Subscriptions accept each message after it has been yielded.
/// </summary>
public sealed class AmqpEndpoint : EndpointBase, IClientEndpoint, IPublisher<ReadOnlyMemory<byte>>, ISubscriber<ReadOnlyMemory<byte>>
{
    private readonly AmqpEndpointOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<(string Address, bool Settled), SenderLink> _senders = new();
    private Connection? _connection;
    private Session? _session;
    private long _received, _published;

    private AmqpEndpoint(AmqpEndpointOptions options) : base("amqp", options.Logger)
    {
        _options = options;
        Name = options.Name;
    }

    /// <summary>Creates an endpoint.</summary>
    public static AmqpEndpoint Create(Action<AmqpEndpointOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var o = new AmqpEndpointOptions();
        configure(o);
        return new AmqpEndpoint(o);
    }

    /// <summary>True while the AMQP connection is open.</summary>
    public bool IsConnected => _connection is { IsClosed: false };

    /// <summary>Messages received by subscriptions.</summary>
    public long MessagesReceived => Interlocked.Read(ref _received);

    /// <summary>Messages published.</summary>
    public long MessagesPublished => Interlocked.Read(ref _published);

    private Address BuildAddress()
    {
        var a = new Address(_options.Address);
        if (_options.UserName is null) return a;
        return new Address(a.Host, a.Port, _options.UserName, _options.Password, a.Path, a.Scheme);
    }

    private string Describe()
    {
        var a = new Address(_options.Address);
        return $"{a.Scheme}://{a.Host}:{a.Port}";
    }

    /// <inheritdoc />
    public async ValueTask ConnectAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsConnected) return;
            SetState(EndpointState.Connecting);
            try
            {
                var factory = new ConnectionFactory();
                if (_options.ContainerId is not null) factory.AMQP.ContainerId = _options.ContainerId;
                var connection = await factory.CreateAsync(BuildAddress(), ct).ConfigureAwait(false);
                connection.Closed += (_, error) =>
                {
                    if (ReferenceEquals(_connection, connection) && !IsDisposed) SetState(EndpointState.Disconnected, error is null ? null : new TransportException(error.ToString()));
                };
                _connection = connection;
                _session = new Session(connection);
            }
            catch (Exception ex) when (ex is AmqpException or System.Net.Sockets.SocketException or IOException or TimeoutException or UriFormatException)
            {
                SetState(EndpointState.Disconnected, ex);
                throw new TransportException($"Cannot connect to AMQP broker at {Describe()}: {ex.Message}", ex);
            }

            SetState(EndpointState.Connected);
            Logger.LogInformation("AMQP connected to {Broker}", Describe());
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisconnectAsync(CancellationToken ct = default)
    {
        Connection? connection;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            connection = _connection;
            _connection = null;
            _session = null;
            _senders.Clear();
        }
        finally
        {
            _gate.Release();
        }

        if (connection is null) return;
        SetState(EndpointState.Stopping);
        try
        {
            await connection.CloseAsync().ConfigureAwait(false);
        }
        catch (AmqpException)
        {
        }

        SetState(EndpointState.Disconnected);
    }

    private async ValueTask<(Connection Connection, Session Session)> OpenAsync(CancellationToken ct)
    {
        ThrowIfDisposed();
        if (!IsConnected) await ConnectAsync(ct).ConfigureAwait(false);
        return (_connection ?? throw new TransportException("Not connected to the AMQP broker."), _session ?? throw new TransportException("Not connected to the AMQP broker."));
    }

    private async Task<SenderLink> SenderAsync(string address, bool settled, Session session)
    {
        if (_senders.TryGetValue((address, settled), out var existing) && existing.LinkState == LinkState.Attached) return existing;
        var link = await Task.Run(() =>
        {
            var attach = new Attach
            {
                Role = false,
                SndSettleMode = settled ? SenderSettleMode.Settled : SenderSettleMode.Unsettled,
                Source = new Source(),
                Target = new Target { Address = address },
            };
            return new SenderLink(session, $"iotcom-{Guid.NewGuid():N}", attach, null);
        }).ConfigureAwait(false);
        _senders[(address, settled)] = link;
        return link;
    }

    /// <summary>Publishes to a node address (queue or topic).</summary>
    /// <exception cref="DeviceException">The broker rejected or released the message (confirmed QoS only).</exception>
    public async ValueTask PublishAsync(string topic, ReadOnlyMemory<byte> message, PublishOptions? options = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(topic);
        options ??= PublishOptions.Default;
        var settled = options.QualityOfService == QualityOfService.AtMostOnce;
        var (_, session) = await OpenAsync(ct).ConfigureAwait(false);
        Tap(FrameDirection.Outbound, message.Span, () => $"TRANSFER {topic} ({message.Length} B, {(settled ? "settled" : "confirmed")})");
        var msg = new Message { BodySection = new Data { Binary = message.ToArray() }, Properties = new Properties { To = topic } };
        if (options.ContentType is not null) msg.Properties.ContentType = options.ContentType;
        try
        {
            var sender = await SenderAsync(topic, settled, session).ConfigureAwait(false);
            await sender.SendAsync(msg, _options.SendTimeout).WaitAsync(ct).ConfigureAwait(false);
        }
        catch (AmqpException ex)
        {
            throw new DeviceException($"AMQP broker did not accept the message for \"{topic}\": {ex.Error?.Condition} {ex.Error?.Description}".TrimEnd(), ex);
        }
        catch (TimeoutException ex)
        {
            throw new IoTComTimeoutException($"AMQP broker did not settle the message for \"{topic}\" within {_options.SendTimeout.TotalSeconds:0.#} s.", ex);
        }

        Interlocked.Increment(ref _published);
    }

    /// <summary>
    /// Subscribes to a node address with a receiver link. Each message is accepted after the consumer has processed it
    /// (when the loop moves on), so a consumer that stops mid-way leaves the message for redelivery.
    /// </summary>
    public async IAsyncEnumerable<Message<ReadOnlyMemory<byte>>> SubscribeAsync(string filter, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(filter);
        var (connection, _) = await OpenAsync(ct).ConfigureAwait(false);
        var session = new Session(connection);
        ReceiverLink receiver;
        try
        {
            receiver = await Task.Run(() => new ReceiverLink(session, $"iotcom-{Guid.NewGuid():N}", filter), ct).ConfigureAwait(false);
        }
        catch (AmqpException ex)
        {
            await CloseQuietlyAsync(session).ConfigureAwait(false);
            throw new DeviceException($"AMQP broker refused the subscription to \"{filter}\": {ex.Error?.Description ?? ex.Message}", ex);
        }

        try
        {
            receiver.SetCredit(_options.ReceiverCredit);
            while (!ct.IsCancellationRequested && !IsDisposed)
            {
                global::Amqp.Message? msg;
                try
                {
                    msg = await receiver.ReceiveAsync(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is AmqpException or ObjectDisposedException)
                {
                    if (IsDisposed || ct.IsCancellationRequested) yield break;
                    throw new TransportException($"AMQP receiver on \"{filter}\" failed: {ex.Message}", ex);
                }

                if (msg is null)
                {
                    if (receiver.IsClosed) yield break;
                    continue;
                }

                var data = msg.Body switch
                {
                    byte[] b => b,
                    string s => System.Text.Encoding.UTF8.GetBytes(s),
                    _ => [],
                };
                Interlocked.Increment(ref _received);
                Tap(FrameDirection.Inbound, data, () => $"TRANSFER {filter} ({data.Length} B)");
                yield return new Message<ReadOnlyMemory<byte>>(msg.Properties?.To ?? filter, data, DateTimeOffset.UtcNow);
                try
                {
                    receiver.Accept(msg);
                }
                catch (Exception ex) when (ex is AmqpException or ObjectDisposedException)
                {
                    if (IsDisposed || ct.IsCancellationRequested) yield break;
                    throw new TransportException($"AMQP receiver on \"{filter}\" failed: {ex.Message}", ex);
                }
            }
        }
        finally
        {
            await CloseQuietlyAsync(session).ConfigureAwait(false);
        }
    }

    private static async Task CloseQuietlyAsync(Session session)
    {
        try
        {
            await session.CloseAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AmqpException or ObjectDisposedException or IOException)
        {
        }
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
