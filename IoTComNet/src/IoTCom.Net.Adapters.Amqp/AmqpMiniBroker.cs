using Amqp;
using Amqp.Framing;
using Amqp.Listener;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Adapters.Amqp;

/// <summary>
/// A small in-process AMQP 1.0 broker on AMQPNetLite's <see cref="ContainerHost"/>, for tests, notebooks and demos
/// (not for production). Every address is an in-memory queue: messages published to an address wait there until a
/// receiver attaches; with several receivers on one address the messages are shared between them (competing consumers).
/// Accepted messages are removed; released or modified ones go back to the queue, also when a receiver disconnects
/// with messages in flight. Nothing is persisted.
/// </summary>
public sealed class AmqpMiniBroker : EndpointBase, IServerEndpoint
{
    private readonly int _requestedPort;
    private readonly string? _user, _password;
    private readonly Dictionary<string, BrokerQueue> _queues = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private ContainerHost? _host;
    private long _accepted;

    private AmqpMiniBroker(int port, string? user, string? password, ILogger? logger) : base("amqp-broker", logger)
    {
        _requestedPort = port;
        (_user, _password) = (user, password);
    }

    /// <summary>Creates a broker (call <see cref="StartAsync"/>). Port 0 picks a free port.</summary>
    /// <param name="port">TCP port to listen on (0 = any free port, see <see cref="Port"/>).</param>
    /// <param name="userName">When set (with <paramref name="password"/>), only SASL PLAIN with these credentials is accepted.</param>
    /// <param name="password">Password for <paramref name="userName"/>.</param>
    /// <param name="logger">Optional logger.</param>
    public static AmqpMiniBroker Create(int port = 0, string? userName = null, string? password = null, ILogger? logger = null) => new(port, userName, password, logger);

    /// <summary>The TCP port actually listened on (valid after <see cref="StartAsync"/>).</summary>
    public int Port { get; private set; }

    /// <summary>The address clients connect to (valid after <see cref="StartAsync"/>), without credentials.</summary>
    public string Address => $"amqp://127.0.0.1:{Port}";

    /// <summary>Messages accepted by receivers so far.</summary>
    public long MessagesDelivered => Interlocked.Read(ref _accepted);

    /// <summary>Addresses whose messages are rejected (for testing failure paths of publishers).</summary>
    public ISet<string> RejectedAddresses { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Number of messages waiting on <paramref name="address"/>.</summary>
    public int QueueLength(string address)
    {
        lock (_lock) return _queues.TryGetValue(address, out var q) ? q.Pending.Count : 0;
    }

    /// <inheritdoc />
    public ValueTask StartAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_host is not null) return ValueTask.CompletedTask;
        SetState(EndpointState.Connecting);
        var port = _requestedPort != 0 ? _requestedPort : FreePort();
        var host = new ContainerHost(new global::Amqp.Address($"amqp://127.0.0.1:{port}"));
        if (_user is not null)
        {
            var sasl = host.Listeners[0].SASL;
            sasl.EnableAnonymousMechanism = false;
            sasl.EnablePlainMechanism(_user, _password ?? string.Empty);
        }

        host.RegisterLinkProcessor(new Processor(this));
        host.Open();
        _host = host;
        Port = port;
        SetState(EndpointState.Connected);
        Logger.LogInformation("AMQP mini broker listening on {Address}", Address);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask StopAsync(CancellationToken ct = default)
    {
        var host = Interlocked.Exchange(ref _host, null);
        if (host is null) return ValueTask.CompletedTask;
        SetState(EndpointState.Stopping);
        host.Close();
        SetState(EndpointState.Disconnected);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore() => await StopAsync().ConfigureAwait(false);

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        try
        {
            return ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        }
        finally
        {
            l.Stop();
        }
    }

    private BrokerQueue Queue(string address)
    {
        lock (_lock)
        {
            if (!_queues.TryGetValue(address, out var q)) _queues[address] = q = new BrokerQueue();
            return q;
        }
    }

    private void Enqueue(string address, Message message)
    {
        var q = Queue(address);
        // a fresh message: the received one is bound to the incoming delivery
        var copy = new Message { BodySection = message.BodySection, Properties = message.Properties, ApplicationProperties = message.ApplicationProperties };
        lock (_lock) q.Pending.Enqueue(copy);
        Tap(FrameDirection.Inbound, BodyBytes(message), () => $"PUBLISH {address}");
        Dispatch(q);
    }

    private void Dispatch(BrokerQueue q)
    {
        while (true)
        {
            Consumer? consumer;
            Message? message;
            lock (_lock)
            {
                consumer = null;
                message = null;
                if (q.Pending.Count == 0 || q.Consumers.Count == 0) return;
                // round-robin over consumers that have credit
                for (var i = 0; i < q.Consumers.Count; i++)
                {
                    var c = q.Consumers[(q.Next + i) % q.Consumers.Count];
                    if (c.Credit > 0)
                    {
                        consumer = c;
                        q.Next = (q.Next + i + 1) % q.Consumers.Count;
                        break;
                    }
                }

                if (consumer is null) return;
                message = q.Pending.Dequeue();
                consumer.Credit--;
                consumer.InFlight.Add(message);
            }

            try
            {
                Tap(FrameDirection.Outbound, BodyBytes(message), () => "DELIVER");
                consumer.Link.SendMessage(message);
            }
            catch (Exception ex) when (ex is AmqpException or ObjectDisposedException or InvalidOperationException)
            {
                lock (_lock)
                {
                    consumer.InFlight.Remove(message);
                    q.Pending.Enqueue(message);
                    q.Consumers.Remove(consumer);
                }

                return;
            }
        }
    }

    private static byte[] BodyBytes(Message m) => m.Body switch
    {
        byte[] b => b,
        string s => System.Text.Encoding.UTF8.GetBytes(s),
        _ => [],
    };

    private sealed class BrokerQueue
    {
        public Queue<Message> Pending { get; } = new();
        public List<Consumer> Consumers { get; } = [];
        public int Next { get; set; }
    }

    private sealed class Consumer(ListenerLink link)
    {
        public ListenerLink Link { get; } = link;
        public int Credit { get; set; }
        public List<Message> InFlight { get; } = [];
    }

    private sealed class Processor(AmqpMiniBroker broker) : ILinkProcessor
    {
        public void Process(AttachContext attachContext)
        {
            if (attachContext.Attach.Role)
            {
                // client is the receiver: we send
                var address = (attachContext.Attach.Source as Source)?.Address;
                if (string.IsNullOrEmpty(address))
                {
                    attachContext.Complete(new Error(ErrorCode.InvalidField) { Description = "source address required" });
                    return;
                }

                attachContext.Complete(new OutgoingEndpoint(broker, address), 0);
            }
            else
            {
                var address = (attachContext.Attach.Target as Target)?.Address;
                if (string.IsNullOrEmpty(address))
                {
                    attachContext.Complete(new Error(ErrorCode.InvalidField) { Description = "target address required" });
                    return;
                }

                attachContext.Complete(new IncomingEndpoint(broker, address), 100);
            }
        }
    }

    private sealed class IncomingEndpoint(AmqpMiniBroker broker, string address) : LinkEndpoint
    {
        public override void OnMessage(MessageContext messageContext)
        {
            bool reject;
            lock (broker._lock) reject = broker.RejectedAddresses.Contains(address);
            if (reject)
            {
                messageContext.Complete(new Error(ErrorCode.UnauthorizedAccess) { Description = $"address {address} rejects messages" });
                return;
            }

            broker.Enqueue(address, messageContext.Message);
            messageContext.Complete();
        }

        public override void OnFlow(FlowContext flowContext)
        {
        }

        public override void OnDisposition(DispositionContext dispositionContext)
        {
        }
    }

    private sealed class OutgoingEndpoint(AmqpMiniBroker broker, string address) : LinkEndpoint
    {
        private Consumer? _consumer;

        public override void OnMessage(MessageContext messageContext)
        {
        }

        public override void OnFlow(FlowContext flowContext)
        {
            var q = broker.Queue(address);
            lock (broker._lock)
            {
                if (_consumer is null)
                {
                    _consumer = new Consumer(flowContext.Link);
                    q.Consumers.Add(_consumer);
                }

                _consumer.Credit = flowContext.Messages;
            }

            broker.Dispatch(q);
        }

        public override void OnDisposition(DispositionContext dispositionContext)
        {
            var q = broker.Queue(address);
            var state = dispositionContext.DeliveryState;
            lock (broker._lock)
            {
                if (_consumer is null) return;
                var message = dispositionContext.Message;
                _consumer.InFlight.Remove(message);
                if (state is Released or Modified) q.Pending.Enqueue(message);
                else if (state is Accepted) Interlocked.Increment(ref broker._accepted);
            }

            if (state is Released or Modified) broker.Dispatch(q);
            dispositionContext.Complete();
        }

        public override void OnLinkClosed(ListenerLink link, Error error)
        {
            var q = broker.Queue(address);
            lock (broker._lock)
            {
                if (_consumer is null) return;
                q.Consumers.Remove(_consumer);
                foreach (var m in _consumer.InFlight) q.Pending.Enqueue(m);
                _consumer.InFlight.Clear();
                _consumer = null;
            }

            broker.Dispatch(q);
        }
    }
}
