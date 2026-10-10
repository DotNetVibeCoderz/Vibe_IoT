using System.Text;
using Amqp;
using IoTCom.Net.Adapters.Amqp;

namespace IoTCom.Net.Tests.Messaging;

public sealed class AmqpTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static async Task<(AmqpMiniBroker Broker, AmqpEndpoint Client)> StartAsync()
    {
        var broker = AmqpMiniBroker.Create();
        await broker.StartAsync();
        var client = AmqpEndpoint.Create(o => o.UseBroker(broker.Address));
        await client.ConnectAsync();
        return (broker, client);
    }

    private static async Task<List<string>> TakeAsync(AmqpEndpoint client, string address, int count, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Wait);
        var list = new List<string>();
        try
        {
            await foreach (var m in client.SubscribeAsync(address, cts.Token))
            {
                list.Add(Encoding.UTF8.GetString(m.Payload.Span));
                if (list.Count == count) break;
            }
        }
        catch (OperationCanceledException)
        {
        }

        return list;
    }

    [Fact]
    public async Task Confirmed_publish_then_subscribe_round_trips()
    {
        var (broker, client) = await StartAsync();
        await using var _ = broker;
        await using var __ = client;
        Assert.True(client.IsConnected);

        await client.PublishAsync("plant.temp", "21.5"u8.ToArray(), new PublishOptions { QualityOfService = QualityOfService.AtLeastOnce });
        await client.PublishAsync("plant.temp", "22.5"u8.ToArray(), new PublishOptions { QualityOfService = QualityOfService.ExactlyOnce });

        Assert.Equal(["21.5", "22.5"], await TakeAsync(client, "plant.temp", 2));
        Assert.Equal(2, client.MessagesPublished);
        Assert.Equal(2, client.MessagesReceived);
    }

    [Fact]
    public async Task Settled_publish_is_delivered()
    {
        var (broker, client) = await StartAsync();
        await using var _ = broker;
        await using var __ = client;

        for (var i = 0; i < 5; i++) await client.PublishAsync("q.settled", Encoding.UTF8.GetBytes($"m{i}"));

        Assert.Equal(["m0", "m1", "m2", "m3", "m4"], await TakeAsync(client, "q.settled", 5));
    }

    [Fact]
    public async Task Subscription_started_first_receives_later_publishes()
    {
        var (broker, client) = await StartAsync();
        await using var _ = broker;
        await using var __ = client;

        var received = TakeAsync(client, "q.live", 1);
        await Task.Delay(300);
        await client.PublishAsync("q.live", "hello"u8.ToArray(), new PublishOptions { QualityOfService = QualityOfService.AtLeastOnce });

        Assert.Equal(["hello"], await received);
    }

    [Fact]
    public async Task Content_type_is_carried_in_the_message_properties()
    {
        var (broker, client) = await StartAsync();
        await using var _ = broker;
        await using var __ = client;

        await client.PublishAsync("q.json", "{}"u8.ToArray(), new PublishOptions { ContentType = "application/json", QualityOfService = QualityOfService.AtLeastOnce });

        var connection = await Connection.Factory.CreateAsync(new Address(broker.Address));
        try
        {
            var session = new Session(connection);
            var receiver = new ReceiverLink(session, "raw", "q.json");
            var message = await receiver.ReceiveAsync(Wait);
            Assert.NotNull(message);
            Assert.Equal("application/json", (string)message.Properties.ContentType);
            Assert.Equal("q.json", message.Properties.To);
            Assert.Equal("{}"u8.ToArray(), (byte[])message.Body);
            receiver.Accept(message);
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    [Fact]
    public async Task Competing_consumers_share_a_queue_without_duplicates()
    {
        var (broker, client) = await StartAsync();
        await using var _ = broker;
        await using var __ = client;
        await using var second = AmqpEndpoint.Create(o => o.UseBroker(broker.Address));

        using var stop = new CancellationTokenSource();
        var a = new List<string>();
        var b = new List<string>();
        async Task Consume(AmqpEndpoint e, List<string> sink)
        {
            try
            {
                await foreach (var m in e.SubscribeAsync("q.work", stop.Token))
                    lock (sink) sink.Add(Encoding.UTF8.GetString(m.Payload.Span));
            }
            catch (OperationCanceledException)
            {
            }
        }

        var ta = Consume(client, a);
        var tb = Consume(second, b);
        await Task.Delay(500);
        for (var i = 0; i < 20; i++) await client.PublishAsync("q.work", Encoding.UTF8.GetBytes($"job{i}"), new PublishOptions { QualityOfService = QualityOfService.AtLeastOnce });

        var deadline = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < deadline && a.Count + b.Count < 20) await Task.Delay(50);
        await stop.CancelAsync();
        await Task.WhenAll(ta, tb).WaitAsync(Wait);

        var all = a.Concat(b).ToList();
        Assert.Equal(20, all.Count);
        Assert.Equal(20, all.Distinct().Count());
        Assert.NotEmpty(a);
        Assert.NotEmpty(b);
    }

    [Fact]
    public async Task Addresses_are_independent_queues()
    {
        var (broker, client) = await StartAsync();
        await using var _ = broker;
        await using var __ = client;

        await client.PublishAsync("q.a", "A"u8.ToArray(), new PublishOptions { QualityOfService = QualityOfService.AtLeastOnce });
        await client.PublishAsync("q.b", "B"u8.ToArray(), new PublishOptions { QualityOfService = QualityOfService.AtLeastOnce });

        Assert.Equal(1, broker.QueueLength("q.a"));
        Assert.Equal(["B"], await TakeAsync(client, "q.b", 1));
        Assert.Equal(["A"], await TakeAsync(client, "q.a", 1));
    }

    [Fact]
    public async Task Unprocessed_message_is_redelivered_to_the_next_subscriber()
    {
        var (broker, client) = await StartAsync();
        await using var _ = broker;
        await using var __ = client;

        for (var i = 0; i < 3; i++) await client.PublishAsync("q.redeliver", Encoding.UTF8.GetBytes($"r{i}"), new PublishOptions { QualityOfService = QualityOfService.AtLeastOnce });

        // take one message and stop without letting the loop accept it
        await foreach (var m in client.SubscribeAsync("q.redeliver"))
        {
            Assert.Equal("r0", Encoding.UTF8.GetString(m.Payload.Span));
            break;
        }

        var all = await TakeAsync(client, "q.redeliver", 3);
        Assert.Equal(["r0", "r1", "r2"], all.Order().ToList());
    }

    [Fact]
    public async Task Rejected_message_throws_DeviceException_when_confirmed_but_not_when_settled()
    {
        var (broker, client) = await StartAsync();
        await using var _ = broker;
        await using var __ = client;
        broker.RejectedAddresses.Add("q.closed");

        await Assert.ThrowsAsync<DeviceException>(async () =>
            await client.PublishAsync("q.closed", "x"u8.ToArray(), new PublishOptions { QualityOfService = QualityOfService.AtLeastOnce }));

        await client.PublishAsync("q.closed", "x"u8.ToArray()); // pre-settled: the outcome is not awaited
        Assert.Equal(0, broker.QueueLength("q.closed"));
    }

    [Fact]
    public async Task Connect_to_a_closed_port_throws_TransportException()
    {
        var broker = AmqpMiniBroker.Create();
        await broker.StartAsync();
        var address = broker.Address;
        await broker.DisposeAsync();

        await using var client = AmqpEndpoint.Create(o => o.UseBroker(address));
        await Assert.ThrowsAsync<TransportException>(async () => await client.ConnectAsync());
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task Credentials_are_checked_by_the_broker()
    {
        var broker = AmqpMiniBroker.Create(userName: "plant", password: "s3cret");
        await broker.StartAsync();
        await using var _ = broker;

        await using var good = AmqpEndpoint.Create(o => o.UseBroker(broker.Address).WithCredentials("plant", "s3cret"));
        await good.ConnectAsync();
        await good.PublishAsync("q.auth", "ok"u8.ToArray(), new PublishOptions { QualityOfService = QualityOfService.AtLeastOnce });
        Assert.Equal(["ok"], await TakeAsync(good, "q.auth", 1));

        await using var bad = AmqpEndpoint.Create(o => o.UseBroker(broker.Address).WithCredentials("plant", "wrong"));
        await Assert.ThrowsAsync<TransportException>(async () => await bad.ConnectAsync());
    }

    [Fact]
    public async Task Dispose_ends_running_subscriptions_and_is_idempotent()
    {
        var (broker, client) = await StartAsync();
        await using var _ = broker;

        var pump = Task.Run(async () =>
        {
            var n = 0;
            await foreach (var _ in client.SubscribeAsync("q.dispose")) n++;
            return n;
        });
        await Task.Delay(400);

        await client.DisposeAsync();
        await client.DisposeAsync();

        Assert.Equal(0, await pump.WaitAsync(Wait));
        Assert.False(client.IsConnected);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await client.PublishAsync("q.dispose", "x"u8.ToArray()));
    }

    [Fact]
    public async Task Taps_see_published_and_received_traffic()
    {
        var (broker, client) = await StartAsync();
        await using var _ = broker;
        await using var __ = client;
        var tap = new RecordingTap(50);
        using var sub = client.AddTap(tap);

        await client.PublishAsync("q.tap", "abc"u8.ToArray(), new PublishOptions { QualityOfService = QualityOfService.AtLeastOnce });
        await TakeAsync(client, "q.tap", 1);

        Assert.Contains(tap.Snapshot(), f => f.Direction == FrameDirection.Outbound);
        Assert.Contains(tap.Snapshot(), f => f.Direction == FrameDirection.Inbound);
    }

    [Fact]
    public void Options_have_documented_defaults_and_fluent_setters()
    {
        var o = new AmqpEndpointOptions();
        Assert.Equal("amqp://localhost:5672", o.Address);
        Assert.Equal(100, o.ReceiverCredit);
        o.UseBroker("amqps://h:5671").WithCredentials("u", "p");
        Assert.Equal(("amqps://h:5671", "u", "p"), (o.Address, o.UserName, o.Password));
        Assert.Throws<ArgumentNullException>(() => AmqpEndpoint.Create(null!));
    }
}
