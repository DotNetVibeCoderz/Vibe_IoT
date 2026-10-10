using System.Text;
using Confluent.Kafka;
using IoTCom.Net.Adapters.Kafka;

namespace IoTCom.Net.Tests.Messaging;

public sealed class KafkaTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

    private static string Topic(string name) => $"iotcom-test-{name}-{Guid.NewGuid():N}";

    private static async Task<KafkaEndpoint> ConnectAsync(Action<KafkaEndpointOptions>? configure = null)
    {
        var e = KafkaEndpoint.Create(o =>
        {
            o.UseBootstrap(KafkaFactAttribute.Bootstrap!);
            configure?.Invoke(o);
        });
        await e.ConnectAsync();
        return e;
    }

    private static async Task<List<KafkaReceived>> TakeAsync(KafkaEndpoint e, string filter, int count)
    {
        using var cts = new CancellationTokenSource(Wait);
        var list = new List<KafkaReceived>();
        try
        {
            await foreach (var m in e.ReceiveAsync(filter, cts.Token))
            {
                list.Add(m);
                if (list.Count == count) break;
            }
        }
        catch (OperationCanceledException)
        {
        }

        return list;
    }

    [KafkaFact]
    public async Task Publish_then_subscribe_with_key_headers_and_content_type()
    {
        await using var e = await ConnectAsync();
        Assert.True(e.IsConnected);
        var topic = Topic("rt");

        await e.PublishAsync(topic, "sensor-1"u8.ToArray(), "21.5"u8.ToArray(), new Dictionary<string, string> { ["site"] = "plant-7" },
            new PublishOptions { QualityOfService = QualityOfService.AtLeastOnce, ContentType = "text/plain" });

        var m = Assert.Single(await TakeAsync(e, topic, 1));
        Assert.Equal(topic, m.Topic);
        Assert.Equal("sensor-1", Encoding.UTF8.GetString(m.Key.Span));
        Assert.Equal("21.5", Encoding.UTF8.GetString(m.Value.Span));
        Assert.Equal("plant-7", m.Headers["site"]);
        Assert.Equal("text/plain", m.Headers["content-type"]);
        Assert.Equal(1, e.MessagesPublished);
    }

    [KafkaFact]
    public async Task Every_qos_level_publishes()
    {
        await using var e = await ConnectAsync();
        var topic = Topic("qos");
        foreach (var qos in Enum.GetValues<QualityOfService>())
            await e.PublishAsync(topic, Encoding.UTF8.GetBytes(qos.ToString()), new PublishOptions { QualityOfService = qos });
        await e.DisconnectAsync(); // flushes the acks=0 producer

        await using var reader = await ConnectAsync();
        var got = await TakeAsync(reader, topic, 3);
        Assert.Equal(["AtLeastOnce", "AtMostOnce", "ExactlyOnce"], got.Select(m => Encoding.UTF8.GetString(m.Value.Span)).Order().ToList());
    }

    [KafkaFact]
    public async Task Regex_filter_subscribes_to_matching_topics()
    {
        await using var e = await ConnectAsync(o => o.WithGroup($"g-{Guid.NewGuid():N}"));
        var prefix = $"iotcom-rx-{Guid.NewGuid():N}";
        await e.PublishAsync($"{prefix}-a", "A"u8.ToArray(), new PublishOptions { QualityOfService = QualityOfService.AtLeastOnce });
        await e.PublishAsync($"{prefix}-b", "B"u8.ToArray(), new PublishOptions { QualityOfService = QualityOfService.AtLeastOnce });

        var got = await TakeAsync(e, $"^{prefix}-.*", 2);

        Assert.Equal(["A", "B"], got.Select(m => Encoding.UTF8.GetString(m.Value.Span)).Order().ToList());
    }

    [KafkaFact]
    public async Task Committed_offsets_are_not_redelivered_to_the_same_group()
    {
        await using var e = await ConnectAsync(o => o.WithGroup($"g-{Guid.NewGuid():N}"));
        var topic = Topic("commit");
        for (var i = 0; i < 3; i++) await e.PublishAsync(topic, Encoding.UTF8.GetBytes($"m{i}"), new PublishOptions { QualityOfService = QualityOfService.AtLeastOnce });

        var first = await TakeAsync(e, topic, 3);
        Assert.Equal(3, first.Count);
        await Task.Delay(500);

        // same group id, new subscription: everything before the last yielded message was committed
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var again = new List<KafkaReceived>();
        try
        {
            await foreach (var m in e.ReceiveAsync(topic, cts.Token)) again.Add(m);
        }
        catch (OperationCanceledException)
        {
        }

        Assert.True(again.Count <= 1, "at most the last message (not yet committed when the loop stopped) may be redelivered");
    }

    [KafkaFact]
    public async Task Dispose_ends_running_subscriptions()
    {
        var e = await ConnectAsync();
        var pump = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in e.SubscribeAsync(Topic("end"))) { }
            }
            catch (OperationCanceledException)
            {
            }
        });
        await Task.Delay(1000);
        await e.DisposeAsync();
        await pump.WaitAsync(Wait);
        Assert.False(e.IsConnected);
    }

    [Fact]
    public void Options_have_documented_defaults_and_fluent_setters()
    {
        var o = new KafkaEndpointOptions();
        Assert.Equal("localhost:9092", o.BootstrapServers);
        Assert.Equal("iotcom", o.ClientId);
        Assert.Null(o.GroupId);
        Assert.Equal(AutoOffsetReset.Earliest, o.AutoOffsetReset);
        Assert.Equal(SecurityProtocol.Plaintext, o.SecurityProtocol);

        o.UseBootstrap("b1:9093,b2:9093").WithGroup("g").WithSasl(SaslMechanism.ScramSha256, "u", "p");
        Assert.Equal("b1:9093,b2:9093", o.BootstrapServers);
        Assert.Equal("g", o.GroupId);
        Assert.Equal(SecurityProtocol.SaslSsl, o.SecurityProtocol);
        Assert.Equal((SaslMechanism.ScramSha256, "u", "p"), (o.SaslMechanism!.Value, o.SaslUsername, o.SaslPassword));

        o.WithSasl(SaslMechanism.Plain, "u", "p", tls: false);
        Assert.Equal(SecurityProtocol.SaslPlaintext, o.SecurityProtocol);
        Assert.Throws<ArgumentNullException>(() => KafkaEndpoint.Create(null!));
    }

    [Fact]
    public async Task Connect_to_an_unreachable_broker_throws_TransportException()
    {
        await using var e = KafkaEndpoint.Create(o => { o.BootstrapServers = "127.0.0.1:1"; o.Timeout = TimeSpan.FromSeconds(2); });
        await Assert.ThrowsAsync<TransportException>(async () => await e.ConnectAsync());
        Assert.False(e.IsConnected);
    }

    [Fact]
    public async Task Empty_topic_is_rejected()
    {
        await using var e = KafkaEndpoint.Create(o => { });
        await Assert.ThrowsAsync<ArgumentException>(async () => await e.PublishAsync("", "x"u8.ToArray()));
    }
}
