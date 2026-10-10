using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using IoTCom.Net.Adapters.Zenoh;

namespace IoTCom.Net.Tests.Protocols;

public class ZenohKeyExprTests
{
    [Theory]
    [InlineData("a/b/c", "a/b/c", true)]
    [InlineData("a/b/c", "a/b/d", false)]
    [InlineData("a/*/c", "a/b/c", true)]
    [InlineData("a/*/c", "a/b/x/c", false)]
    [InlineData("a/*", "a", false)]
    [InlineData("a/**", "a", true)]
    [InlineData("a/**", "a/b/c/d", true)]
    [InlineData("**", "x/y", true)]
    [InlineData("**/c", "a/b/c", true)]
    [InlineData("**/c", "c", true)]
    [InlineData("**/c", "a/b/d", false)]
    [InlineData("a/**/c", "a/c", true)]
    [InlineData("a/**/c", "a/x/y/c", true)]
    [InlineData("sensor$*/temp", "sensor42/temp", true)]
    [InlineData("sensor$*/temp", "sensor/temp", true)]
    [InlineData("sensor$*/temp", "senso/temp", false)]
    [InlineData("$*_temp", "line1_temp", true)]
    [InlineData("a$*b$*c", "aXbYc", true)]
    [InlineData("a$*b$*c", "aXcYb", false)]
    [InlineData("a/b", "a/*", false)]       // a pattern does not include a wider one
    [InlineData("a/*", "a/b$*", true)]
    [InlineData("a/**", "a/**/b", true)]
    [InlineData("a/*/b", "a/**", false)]
    public void Includes_follows_the_zenoh_rules(string pattern, string key, bool expected)
    {
        Assert.Equal(expected, ZenohKeyExpr.Includes(pattern, key));
        Assert.Equal(expected, ZenohKeyExpr.Matches(pattern, key));
    }

    [Theory]
    [InlineData("a/*/c", "a/b/*", true)]
    [InlineData("a/*/c", "a/b/d", false)]
    [InlineData("plant/**", "plant/line1/temp", true)]
    [InlineData("plant/*/temp", "plant/line1/**", true)]
    [InlineData("a/b", "a/b/c", false)]
    [InlineData("a/**", "b/**", false)]
    [InlineData("**", "x/y/z", true)]
    [InlineData("a$*", "$*b", true)]        // "ab" matches both
    [InlineData("a$*", "b$*", false)]
    [InlineData("a$*c", "ab$*", true)]      // "abc"
    [InlineData("a/**/z", "**/m/**", true)]
    public void Intersects_is_symmetric_and_handles_wildcards_on_both_sides(string a, string b, bool expected)
    {
        Assert.Equal(expected, ZenohKeyExpr.Intersects(a, b));
        Assert.Equal(expected, ZenohKeyExpr.Intersects(b, a));
    }

    [Theory]
    [InlineData("a/b", true)]
    [InlineData("a/*/c", true)]
    [InlineData("a/**", true)]
    [InlineData("a$*/b", true)]
    [InlineData("", false)]
    [InlineData("/a", false)]
    [InlineData("a/", false)]
    [InlineData("a//b", false)]
    [InlineData("a/b*", false)]
    [InlineData("a/$/b", false)]
    [InlineData("a/b?x=1", false)]
    [InlineData("a/#", false)]
    public void Validation(string key, bool valid)
    {
        Assert.Equal(valid, ZenohKeyExpr.IsValid(key));
        Assert.Equal(valid, ZenohKeyExpr.IsValid(key, out var error) && error is null);
        if (!valid) Assert.Throws<ArgumentException>(() => ZenohKeyExpr.ThrowIfInvalid(key));
    }

    [Fact]
    public void Wildcard_detection()
    {
        Assert.True(ZenohKeyExpr.IsWild("a/*"));
        Assert.True(ZenohKeyExpr.IsWild("a$*"));
        Assert.False(ZenohKeyExpr.IsWild("a/b"));
    }
}

public class ZenohVirtualTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static async Task<T> ReadAsync<T>(Channel<T> channel)
    {
        using var cts = new CancellationTokenSource(Wait);
        return await channel.Reader.ReadAsync(cts.Token);
    }

    private static async Task<ZenohSession> OpenAsync(VirtualZenohNetwork net, Action<ZenohOptions>? configure = null)
    {
        var session = ZenohSession.Create(o =>
        {
            o.UseVirtual(net);
            configure?.Invoke(o);
        });
        await session.ConnectAsync();
        return session;
    }

    [Fact]
    public async Task Put_reaches_matching_subscribers_on_other_sessions()
    {
        var net = new VirtualZenohNetwork();
        await using var pub = await OpenAsync(net);
        await using var sub = await OpenAsync(net);
        var got = Channel.CreateUnbounded<ZenohSample>();
        var other = Channel.CreateUnbounded<ZenohSample>();
        using var s1 = sub.Subscribe("plant/*/temp", s => got.Writer.TryWrite(s));
        using var s2 = sub.Subscribe("plant/line2/**", s => other.Writer.TryWrite(s));

        await pub.PutAsync("plant/line1/temp", "21.5");
        await pub.PutAsync("plant/line2/valve/state", new byte[] { 1 });

        var a = await ReadAsync(got);
        Assert.Equal("plant/line1/temp", a.Key);
        Assert.Equal(ZenohSampleKind.Put, a.Kind);
        Assert.Equal("21.5", a.Text);
        Assert.Equal("text/plain", a.Encoding);
        var b = await ReadAsync(other);
        Assert.Equal("plant/line2/valve/state", b.Key);
        Assert.Equal(new byte[] { 1 }, b.Payload);
        Assert.Null(b.Encoding);
        Assert.False(got.Reader.TryRead(out _));
        Assert.NotEqual(pub.Zid, sub.Zid);
    }

    [Fact]
    public async Task A_session_also_receives_its_own_publications_and_the_event_fires()
    {
        var net = new VirtualZenohNetwork();
        await using var z = await OpenAsync(net);
        var raised = Channel.CreateUnbounded<ZenohSample>();
        z.SampleReceived += (_, s) => raised.Writer.TryWrite(s);
        using var sub = z.Subscribe("loop/back", _ => { });
        await z.PutAsync("loop/back", "x");
        Assert.Equal("x", (await ReadAsync(raised)).Text);
    }

    [Fact]
    public async Task Delete_is_delivered_as_a_delete_sample_and_stops_after_unsubscribe()
    {
        var net = new VirtualZenohNetwork();
        await using var z = await OpenAsync(net);
        var got = Channel.CreateUnbounded<ZenohSample>();
        var sub = z.Subscribe("cfg/**", s => got.Writer.TryWrite(s));
        await z.PutAsync("cfg/mode", "auto");
        await z.DeleteAsync("cfg/mode");
        Assert.Equal(ZenohSampleKind.Put, (await ReadAsync(got)).Kind);
        var del = await ReadAsync(got);
        Assert.Equal(ZenohSampleKind.Delete, del.Kind);
        Assert.Equal("cfg/mode", del.Key);
        Assert.Empty(del.Payload);

        sub.Dispose();
        await z.PutAsync("cfg/mode", "manual");
        await Task.Delay(100);
        Assert.False(got.Reader.TryRead(out _));
    }

    [Fact]
    public async Task WatchAsync_streams_until_cancelled()
    {
        var net = new VirtualZenohNetwork();
        await using var pub = await OpenAsync(net);
        await using var sub = await OpenAsync(net);
        using var cts = new CancellationTokenSource(Wait);
        var seen = new List<string>();
        var consumer = Task.Run(async () =>
        {
            await foreach (var s in sub.WatchAsync("n/**", cts.Token))
            {
                seen.Add(s.Text);
                if (seen.Count == 3) await cts.CancelAsync();
            }
        });

        // The subscription is declared when enumeration starts; keep publishing until it caught the three values.
        for (var i = 0; i < 100 && !consumer.IsCompleted; i++)
        {
            await pub.PutAsync("n/x", $"v{i}");
            await Task.Delay(10);
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consumer);
        Assert.Equal(3, seen.Count);
    }

    [Fact]
    public async Task Shared_publisher_and_subscriber_abstractions_carry_payloads_and_skip_deletes()
    {
        var net = new VirtualZenohNetwork();
        await using var z = await OpenAsync(net);
        IPublisher<string> publisher = z;
        ISubscriber<string> subscriber = z;
        using var cts = new CancellationTokenSource(Wait);
        var received = new List<Message<string>>();
        var consumer = Task.Run(async () =>
        {
            await foreach (var m in subscriber.SubscribeAsync("topic/#".Replace("#", "**", StringComparison.Ordinal), cts.Token))
            {
                received.Add(m);
                await cts.CancelAsync();
            }
        });

        for (var i = 0; i < 100 && !consumer.IsCompleted; i++)
        {
            await z.DeleteAsync("topic/a");
            await publisher.PublishAsync("topic/a", "hello");
            await Task.Delay(10);
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consumer);
        Assert.Equal("topic/a", received[0].Topic);
        Assert.Equal("hello", received[0].Payload);
    }

    [Fact]
    public async Task Get_collects_replies_from_every_matching_queryable_and_passes_parameters_and_body()
    {
        var net = new VirtualZenohNetwork();
        await using var server1 = await OpenAsync(net);
        await using var server2 = await OpenAsync(net);
        await using var client = await OpenAsync(net);
        using var q1 = server1.DeclareQueryable("dev/a/info", async q =>
        {
            await q.ReplyAsync("dev/a/info", $"a:{q.Parameters}:{Encoding.UTF8.GetString(q.Payload ?? [])}");
        });
        using var q2 = server2.DeclareQueryable("dev/**", async q =>
        {
            await q.ReplyAsync("dev/b/info", "b");
            await q.ReplyErrorAsync("partial");
        });

        var replies = await client.GetAsync("dev/*/info?x=1", "req");
        Assert.Equal(3, replies.Count);
        Assert.Contains(replies, r => r.Sample is { Key: "dev/a/info" } s && s.Text == "a:x=1:req");
        Assert.Contains(replies, r => r.Sample is { Key: "dev/b/info" } s && s.Text == "b");
        Assert.Contains(replies, r => r.IsError && r.ErrorText == "partial");
    }

    [Fact]
    public async Task Get_without_queryables_returns_nothing_and_unmatched_keys_are_ignored()
    {
        var net = new VirtualZenohNetwork();
        await using var server = await OpenAsync(net);
        await using var client = await OpenAsync(net);
        using var q = server.DeclareQueryable("only/this", q => q.ReplyAsync("only/this", "x"));
        Assert.Empty(await client.GetAsync("nobody/home"));
        Assert.Single(await client.GetAsync("only/*"));
    }

    [Fact]
    public async Task A_queryable_that_forgets_to_answer_or_throws_still_finishes_the_query()
    {
        var net = new VirtualZenohNetwork();
        await using var server = await OpenAsync(net);
        await using var client = await OpenAsync(net);
        using var silent = server.DeclareQueryable("silent", _ => ValueTask.CompletedTask);
        using var broken = server.DeclareQueryable("broken", _ => throw new InvalidOperationException("boom"));
        Assert.Empty(await client.GetAsync("silent", timeout: Wait));
        Assert.Empty(await client.GetAsync("broken", timeout: Wait));
    }

    [Fact]
    public async Task Reply_key_must_intersect_the_query()
    {
        var net = new VirtualZenohNetwork();
        await using var server = await OpenAsync(net);
        await using var client = await OpenAsync(net);
        using var q = server.DeclareQueryable("a/**", q => q.ReplyAsync("elsewhere", "x"));
        Assert.Empty(await client.GetAsync("a/b", timeout: Wait));   // the handler's ArgumentException is logged and the query finished
    }

    [Fact]
    public async Task Undeclared_queryables_no_longer_answer()
    {
        var net = new VirtualZenohNetwork();
        await using var server = await OpenAsync(net);
        await using var client = await OpenAsync(net);
        var q = server.DeclareQueryable("svc", q => q.ReplyAsync("svc", "up"));
        Assert.Single(await client.GetAsync("svc"));
        q.Dispose();
        Assert.Empty(await client.GetAsync("svc"));
    }

    [Fact]
    public async Task Read_only_refuses_put_delete_and_reply_but_allows_subscribe_and_get()
    {
        var net = new VirtualZenohNetwork();
        await using var writer = await OpenAsync(net);
        await using var ro = await OpenAsync(net, o => o.ReadOnly = true);
        Assert.True(ro.ReadOnly);
        await Assert.ThrowsAsync<ReadOnlyModeException>(() => ro.PutAsync("a/b", "x"));
        await Assert.ThrowsAsync<ReadOnlyModeException>(() => ro.DeleteAsync("a/b"));
        await Assert.ThrowsAsync<ReadOnlyModeException>(async () => await ((IPublisher<string>)ro).PublishAsync("a/b", "x"));

        var got = Channel.CreateUnbounded<ZenohSample>();
        using var sub = ro.Subscribe("a/**", s => got.Writer.TryWrite(s));
        await writer.PutAsync("a/b", "seen");
        Assert.Equal("seen", (await ReadAsync(got)).Text);

        // A read-only server cannot reply (the handler's ReadOnlyModeException ends the query without replies).
        using var q = ro.DeclareQueryable("svc", q => q.ReplyAsync("svc", "nope"));
        Assert.Empty(await writer.GetAsync("svc", timeout: Wait));
        using var ok = writer.DeclareQueryable("svc2", q => q.ReplyAsync("svc2", "yes"));
        Assert.Single(await ro.GetAsync("svc2", timeout: Wait));
    }

    [Fact]
    public async Task Invalid_keys_and_closed_sessions_are_rejected()
    {
        var net = new VirtualZenohNetwork();
        var z = ZenohSession.Create(o => o.UseVirtual(net));
        await Assert.ThrowsAsync<InvalidOperationException>(() => z.PutAsync("a/b", "x"));   // not connected
        await z.ConnectAsync();
        Assert.Equal(1, net.SessionCount);
        await Assert.ThrowsAsync<ArgumentException>(() => z.PutAsync("a//b", "x"));
        Assert.Throws<ArgumentException>(() => z.Subscribe("a/b*", _ => { }));
        await Assert.ThrowsAsync<ArgumentException>(() => z.GetAsync("bad//key?x=1"));
        await z.DisposeAsync();
        Assert.Equal(0, net.SessionCount);
    }

    [Fact]
    public async Task State_changes_and_traffic_tap()
    {
        var net = new VirtualZenohNetwork();
        await using var z = ZenohSession.Create(o => o.UseVirtual(net));
        var states = new List<EndpointState>();
        z.StateChanged += (_, e) => states.Add(e.Current);
        var frames = new List<TrafficFrame>();
        z.AddTap(new DelegateTap(frames.Add));
        await z.ConnectAsync();
        await z.PutAsync("t/k", "v");
        await z.DisconnectAsync();
        Assert.Equal([EndpointState.Connecting, EndpointState.Connected, EndpointState.Stopping, EndpointState.Disconnected], states);
        Assert.Contains(frames, f => f.Direction == FrameDirection.Outbound && f.Summary == "put t/k" && f.Protocol == "zenoh");
    }

    private sealed class DelegateTap(Action<TrafficFrame> onFrame) : ITrafficTap
    {
        public void OnFrame(in TrafficFrame frame) => onFrame(frame);
    }
}

/// <summary>Runs only when the Rust library is available (cargo build --release -p iotcom-zenoh-native, or a packaged runtime).</summary>
public sealed class ZenohNativeFactAttribute : FactAttribute
{
    public ZenohNativeFactAttribute()
    {
        if (!NativeZenohBackend.IsSupported) Skip = "iotcom_zenoh native library not built (run: cargo build --release -p iotcom-zenoh-native in rust/).";
    }
}

public sealed class ZenohNativeTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    [ZenohNativeFact]
    public async Task Two_native_sessions_exchange_put_delete_and_a_query()
    {
        var endpoint = $"tcp/127.0.0.1:{FreePort()}";
        await using var a = ZenohSession.Create(o =>
        {
            o.MulticastScouting = false;
            o.Listen(endpoint);
        });
        await using var b = ZenohSession.Create(o =>
        {
            o.MulticastScouting = false;
            o.Connect(endpoint);
        });
        await a.ConnectAsync();
        await b.ConnectAsync();
        Assert.NotEmpty(a.Zid);
        Assert.NotEqual(a.Zid, b.Zid);

        var samples = Channel.CreateUnbounded<ZenohSample>();
        using var sub = b.Subscribe("it/**", s => samples.Writer.TryWrite(s));
        using var queryable = a.DeclareQueryable("it/echo", async q => await q.ReplyAsync("it/echo", $"echo:{Encoding.UTF8.GetString(q.Payload ?? [])}:{q.Parameters}"));

        // Declarations propagate asynchronously: publish until the subscriber sees one.
        using var cts = new CancellationTokenSource(Wait);
        ZenohSample first;
        while (true)
        {
            await a.PutAsync("it/temp", "21.5");
            await Task.Delay(100, cts.Token);
            if (samples.Reader.TryRead(out first!)) break;
        }

        Assert.Equal("it/temp", first.Key);
        Assert.Equal(ZenohSampleKind.Put, first.Kind);
        Assert.Equal("21.5", first.Text);
        Assert.Equal("text/plain", first.Encoding);

        await a.DeleteAsync("it/temp");
        ZenohSample deleted;
        do deleted = await samples.Reader.ReadAsync(cts.Token); while (deleted.Kind != ZenohSampleKind.Delete);
        Assert.Equal("it/temp", deleted.Key);

        var replies = await b.GetAsync("it/echo?mode=x", "ping", TimeSpan.FromSeconds(10), cts.Token);
        var reply = Assert.Single(replies);
        Assert.Equal("echo:ping:mode=x", reply.Sample!.Text);

        Assert.Empty(await b.GetAsync("it/nobody", timeout: TimeSpan.FromSeconds(2), ct: cts.Token));
    }

    [ZenohNativeFact]
    public async Task A_native_session_opens_with_the_documented_defaults_and_validates_its_mode()
    {
        await using var z = ZenohSession.Create(o =>
        {
            o.MulticastScouting = false;
            o.Listen($"tcp/127.0.0.1:{FreePort()}");
        });
        await z.ConnectAsync();
        var info = ((NativeZenohBackend)z.Backend!).ReadInfo();
        Assert.Equal(z.Zid, info.Zid);
        Assert.Empty(info.Routers);
    }

    [ZenohNativeFact]
    public async Task A_bad_endpoint_is_reported_as_a_transport_error()
    {
        await using var z = ZenohSession.Create(o =>
        {
            o.MulticastScouting = false;
            o.ListenEndpoints.Add("bogus/not-an-endpoint");
        });
        await Assert.ThrowsAsync<TransportException>(async () => await z.ConnectAsync());
        Assert.Equal(EndpointState.Disconnected, z.State);
    }
}
