using System.Text;
using IoTCom.Net.Adapters.Nats;

namespace IoTCom.Net.Tests.Messaging;

public sealed class NatsTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static string Subject(string name) => $"iotcom.test.{Guid.NewGuid():N}.{name}";

    private static async Task<NatsEndpoint> ConnectAsync()
    {
        var e = NatsEndpoint.Create(o => o.UseServer(NatsFactAttribute.Url!));
        await e.ConnectAsync();
        return e;
    }

    /// <summary>Starts a subscription, gives the server a moment to register it, and returns the collector.</summary>
    private static async Task<(Task<List<NatsReceived>> Result, CancellationTokenSource Cts)> ListenAsync(NatsEndpoint e, string subject, int count, string? queue = null)
    {
        var cts = new CancellationTokenSource(Wait);
        var task = Task.Run(async () =>
        {
            var list = new List<NatsReceived>();
            try
            {
                await foreach (var m in e.ReceiveAsync(subject, queue, cts.Token))
                {
                    list.Add(m);
                    if (list.Count == count) break;
                }
            }
            catch (OperationCanceledException)
            {
            }

            return list;
        });
        await Task.Delay(500);
        return (task, cts);
    }

    [NatsFact]
    public async Task Publish_and_subscribe_round_trips()
    {
        await using var e = await ConnectAsync();
        Assert.True(e.IsConnected);
        var subject = Subject("temp");
        var (result, cts) = await ListenAsync(e, subject, 2);

        await e.PublishAsync(subject, "21.5"u8.ToArray());
        await e.PublishAsync(subject, "22.5"u8.ToArray(), new PublishOptions { ContentType = "text/plain" });

        var got = await result;
        cts.Dispose();
        Assert.Equal(["21.5", "22.5"], got.Select(m => Encoding.UTF8.GetString(m.Data.Span)));
        Assert.Equal("text/plain", got[1].Headers["Content-Type"]);
        Assert.Equal(2, e.MessagesPublished);
        Assert.Equal(2, e.MessagesReceived);
    }

    [NatsFact]
    public async Task Wildcards_match_one_token_or_the_whole_tail()
    {
        await using var e = await ConnectAsync();
        var root = $"iotcom.test.{Guid.NewGuid():N}";
        var (star, c1) = await ListenAsync(e, $"{root}.plant.*.temp", 1);
        var (tail, c2) = await ListenAsync(e, $"{root}.plant.>", 3);

        await e.PublishAsync($"{root}.plant.line1.temp", "a"u8.ToArray());
        await e.PublishAsync($"{root}.plant.line1.pressure", "b"u8.ToArray());
        await e.PublishAsync($"{root}.plant.line2.deep.value", "c"u8.ToArray());

        Assert.Equal([$"{root}.plant.line1.temp"], (await star).Select(m => m.Subject));
        Assert.Equal(3, (await tail).Count);
        c1.Dispose();
        c2.Dispose();
    }

    [NatsFact]
    public async Task Queue_group_delivers_each_message_to_one_member()
    {
        await using var a = await ConnectAsync();
        await using var b = await ConnectAsync();
        await using var publisher = await ConnectAsync();
        var subject = Subject("jobs");
        var (ra, ca) = await ListenAsync(a, subject, 100, "workers");
        var (rb, cb) = await ListenAsync(b, subject, 100, "workers");

        for (var i = 0; i < 20; i++) await publisher.PublishAsync(subject, Encoding.UTF8.GetBytes($"job{i}"));
        await Task.Delay(1000);
        await ca.CancelAsync();
        await cb.CancelAsync();

        var all = (await ra).Concat(await rb).Select(m => Encoding.UTF8.GetString(m.Data.Span)).ToList();
        ca.Dispose();
        cb.Dispose();
        Assert.Equal(20, all.Count);
        Assert.Equal(20, all.Distinct().Count());
    }

    [NatsFact]
    public async Task Headers_travel_with_the_message()
    {
        await using var e = await ConnectAsync();
        var subject = Subject("hdr");
        var (result, cts) = await ListenAsync(e, subject, 1);

        await e.PublishAsync(subject, "x"u8.ToArray(), new Dictionary<string, string> { ["site"] = "plant-7", ["seq"] = "42" }, "application/json");

        var m = Assert.Single(await result);
        cts.Dispose();
        Assert.Equal("plant-7", m.Headers["site"]);
        Assert.Equal("42", m.Headers["seq"]);
        Assert.Equal("application/json", m.Headers["Content-Type"]);
    }

    [NatsFact]
    public async Task Request_reply_with_a_responder()
    {
        await using var server = await ConnectAsync();
        await using var client = await ConnectAsync();
        var subject = Subject("rpc");
        using var stop = new CancellationTokenSource();
        var serving = server.ServeAsync(subject, req => ValueTask.FromResult<ReadOnlyMemory<byte>>(Encoding.UTF8.GetBytes("pong:" + Encoding.UTF8.GetString(req.Data.Span))), ct: stop.Token);
        await Task.Delay(500);

        var reply = await client.RequestAsync(subject, "ping"u8.ToArray(), Wait);

        Assert.Equal("pong:ping", Encoding.UTF8.GetString(reply.Data.Span));
        await stop.CancelAsync();
        await serving.WaitAsync(Wait);
    }

    [NatsFact]
    public async Task Request_without_a_responder_throws_DeviceException()
    {
        await using var client = await ConnectAsync();
        var ex = await Assert.ThrowsAsync<DeviceException>(async () => await client.RequestAsync(Subject("nobody"), "ping"u8.ToArray(), TimeSpan.FromSeconds(2)));
        Assert.Contains("No NATS responder", ex.Message);
    }

    [NatsFact]
    public async Task Slow_responder_throws_IoTComTimeoutException()
    {
        await using var server = await ConnectAsync();
        await using var client = await ConnectAsync();
        var subject = Subject("slow");
        using var stop = new CancellationTokenSource();
        var serving = server.ServeAsync(subject, async _ =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            return ReadOnlyMemory<byte>.Empty;
        }, ct: stop.Token);
        await Task.Delay(500);

        await Assert.ThrowsAsync<IoTComTimeoutException>(async () => await client.RequestAsync(subject, "x"u8.ToArray(), TimeSpan.FromMilliseconds(300)));
        await stop.CancelAsync();
        try
        {
            await serving.WaitAsync(Wait);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
        }
    }

    [NatsFact]
    public async Task Dispose_ends_subscriptions_and_disconnects()
    {
        var e = await ConnectAsync();
        var pump = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in e.SubscribeAsync(Subject("end"))) { }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
            }
        });
        await Task.Delay(300);
        await e.DisposeAsync();
        await pump.WaitAsync(Wait);
        Assert.False(e.IsConnected);
    }

    [Fact]
    public async Task Connect_to_a_closed_port_fails()
    {
        await using var e = NatsEndpoint.Create(o => { o.Url = "nats://127.0.0.1:1"; o.ClientName = "t"; });
        using var cts = new CancellationTokenSource(Wait);
        await Assert.ThrowsAnyAsync<Exception>(async () => await e.ConnectAsync(cts.Token));
        Assert.False(e.IsConnected);
    }

    [Fact]
    public void Options_have_documented_defaults_and_fluent_setters()
    {
        var o = new NatsEndpointOptions();
        Assert.Equal("nats://localhost:4222", o.Url);
        Assert.Equal(TimeSpan.FromSeconds(5), o.RequestTimeout);
        o.UseServer("nats://h:4223").WithCredentials("u", "p");
        Assert.Equal(("nats://h:4223", "u", "p"), (o.Url, o.UserName, o.Password));
        o.WithToken("tok");
        Assert.Equal("tok", o.Token);
        Assert.Throws<ArgumentNullException>(() => NatsEndpoint.Create(null!));
    }

    [Fact]
    public async Task Empty_subject_is_rejected_before_connecting()
    {
        await using var e = NatsEndpoint.Create(o => { });
        await Assert.ThrowsAsync<ArgumentException>(async () => await e.PublishAsync("", "x"u8.ToArray()));
    }
}
