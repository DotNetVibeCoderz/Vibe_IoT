using System.Globalization;
using System.Net;
using System.Text.Json;
using IoTCom.Net.Protocols.Knx;
using IoTCom.Net.Transports;

namespace IoTCom.Net.Tests.Protocols;

public class KnxCodecTests
{
    [Fact]
    public void Addresses_parse_and_format()
    {
        Assert.Equal("1.1.250", KnxIndividualAddress.Parse("1.1.250").ToString());
        Assert.Equal(0x11FA, KnxIndividualAddress.Parse("1.1.250").Raw);
        Assert.Equal("3/0/1", KnxGroupAddress.Parse("3/0/1").ToString());
        Assert.Equal(KnxGroupAddress.Parse("3/0/1"), KnxGroupAddress.Parse("3/1"));       // two-level 3/1 = 3/0/1
        Assert.Equal(KnxGroupAddress.Parse("0/0/1"), KnxGroupAddress.Parse("1"));
        Assert.Throws<FormatException>(() => KnxGroupAddress.Parse("32/0/0"));
        Assert.Throws<FormatException>(() => KnxGroupAddress.Parse("1/8/0"));
        Assert.Throws<FormatException>(() => KnxIndividualAddress.Parse("16.0.0"));
    }

    [Fact]
    public void Datapoints_format_with_units_and_names()
    {
        Assert.Equal("21.5 °C", KnxDpt.Format("9.001", KnxDpt.Encode("9.001", 21.5)));
        Assert.Equal("On", KnxDpt.Format("1.001", KnxGroupValue.Short(1)));
        Assert.Equal("Down", KnxDpt.Format("1.008", KnxGroupValue.Short(1)));
        Assert.Equal("Comfort", KnxDpt.Format("20.102", KnxGroupValue.Of(1)));
        Assert.Equal("Increase 25 %", KnxDpt.Format("3.007", KnxGroupValue.Short(0x0B)));
        Assert.Equal(double.NaN, KnxDpt.DecodeFloat16([0x7F, 0xFF]));
        Assert.Throws<ArgumentOutOfRangeException>(() => KnxDpt.Encode("9.001", 670760.96));
        Assert.Throws<ArgumentOutOfRangeException>(() => KnxDpt.Encode("5.001", 101));
        Assert.Throws<ProtocolException>(() => KnxDpt.Decode("9.001", KnxGroupValue.Of(1)));
    }

    [Fact]
    public void Frames_describe_their_fields()
    {
        var cemi = KnxCemiFrame.GroupTelegram(KnxMessageCode.DataRequest, default, KnxGroupAddress.Parse("1/0/1"), KnxApci.GroupValueWrite, KnxGroupValue.Short(1)).Encode();
        var frame = KnxIp.TunnelingRequest(1, 7, cemi);
        var fields = KnxIp.DescribeFields(frame);
        Assert.Contains(fields, f => f.Name == "Service" && f.Value == "TUNNELING_REQUEST");
        Assert.Contains(fields, f => f.Name == "Destination" && f.Value == "1/0/1" && f.Offset == 10 + 6);
        Assert.Equal("TUNNELING_REQUEST ch 1 seq 7: L_Data.req 0.0.0 → 1/0/1 Write 0x01 (6-bit)", KnxIp.Describe(frame));
    }
}

public class KnxConformanceTests
{
    public static IEnumerable<object[]> Vectors => Conformance.Cases("knx.json");

    private static object Value(string dpt, JsonElement v) => dpt.Split('.')[0] switch
    {
        "1" => v.GetBoolean(),
        "3" or "18" => (v[0].GetBoolean(), v[1].GetInt32()),
        "10" => TimeOnly.Parse(v.GetString()!, CultureInfo.InvariantCulture),
        "11" => DateOnly.Parse(v.GetString()!, CultureInfo.InvariantCulture),
        "16" => v.GetString()!,
        "232" => ((byte)v[0].GetInt32(), (byte)v[1].GetInt32(), (byte)v[2].GetInt32()),
        _ => v.GetDouble(),
    };

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Shared_vectors(string json)
    {
        var v = Conformance.Parse(json);
        var bytes = v.Hex("hex");
        switch (v.GetProperty("kind").GetString())
        {
            case "dpt":
            {
                var dpt = v.GetProperty("dpt").GetString()!;
                var encoded = KnxDpt.Encode(dpt, Value(dpt, v.GetProperty("value")));
                Assert.Equal(new KnxGroupValue(bytes, v.GetProperty("short").GetBoolean()), encoded);
                Assert.Equal(encoded, KnxDpt.Encode(dpt, KnxDpt.Decode(dpt, encoded)));   // round trip
                break;
            }

            case "cemi":
            {
                if (v.GetProperty("code").GetString() == "error")
                {
                    Assert.ThrowsAny<ProtocolException>(() => KnxCemiFrame.Decode(bytes));
                    return;
                }

                var f = KnxCemiFrame.Decode(bytes);
                Assert.Equal(v.GetProperty("code").GetString(), f.MessageCode.ToString());
                Assert.Equal(v.GetProperty("source").GetString(), f.Source.ToString());
                Assert.Equal(v.GetProperty("destination").GetString(), f.DestinationText);
                Assert.Equal(v.GetProperty("apci").GetString(), f.Apci.ToString());
                Assert.Equal(new KnxGroupValue(Convert.FromHexString(v.GetProperty("value").GetString()!), v.GetProperty("short").GetBoolean()), f.Value);
                if (bytes[1] == 0) Assert.Equal(bytes, f.Encode());
                break;
            }

            default:
            {
                var service = v.GetProperty("service").GetString();
                if (service == "error")
                {
                    Assert.ThrowsAny<ProtocolException>(() => KnxIp.Parse(bytes));
                    return;
                }

                var (type, body) = KnxIp.Parse(bytes);
                Assert.Equal(service, KnxIp.Name(type));
                if (v.GetProperty("channel").ValueKind == JsonValueKind.Number) Assert.Equal(v.GetProperty("channel").GetInt32(), type is KnxServiceType.TunnelingRequest or KnxServiceType.TunnelingAck ? body.Span[1] : body.Span[0]);
                if (v.GetProperty("sequence").ValueKind == JsonValueKind.Number) Assert.Equal(v.GetProperty("sequence").GetInt32(), body.Span[2]);
                if (type == KnxServiceType.ConnectResponse && body.Span[1] == 0) Assert.Equal("1.1.250", KnxIp.ReadConnectResponse(body.Span).Assigned.ToString());
                if (type is KnxServiceType.TunnelingRequest) Assert.Equal("1/0/1", KnxCemiFrame.Decode(KnxIp.ReadConnectionHeader(body).Payload.Span).DestinationText);
                break;
            }
        }
    }
}

public sealed class KnxSessionTests : IAsyncLifetime
{
    private static readonly IPEndPoint GatewayAddress = new(IPAddress.Parse("10.50.0.2"), KnxIp.Port);
    private readonly InMemoryDatagramNetwork _net = new();
    private KnxBus _bus = null!;
    private KnxBuildingSimulator _floor = null!;
    private KnxIpGateway _gateway = null!;

    public async Task InitializeAsync()
    {
        _bus = new KnxBus();
        _floor = new KnxBuildingSimulator(_bus);
        _gateway = KnxIpGateway.Create(o =>
        {
            o.UseInMemory(_net, GatewayAddress);
            o.Bus = _bus;
            o.MaxTunnels = 2;
            o.AckTimeout = TimeSpan.FromMilliseconds(200);
        });
        await _gateway.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _gateway.DisposeAsync();
        await _bus.DisposeAsync();
    }

    private KnxTunnelClient Client(bool writes = false) => KnxTunnelClient.Create(o =>
    {
        o.UseInMemory(_net);
        o.UseGateway(GatewayAddress);
        o.AckTimeout = TimeSpan.FromMilliseconds(300);
        o.HeartbeatInterval = TimeSpan.FromMilliseconds(200);
        if (writes) o.AllowWrites();
    });

    private static async Task Until(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException("condition not reached");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Tunnel_reads_the_floor_and_writes_need_the_lock()
    {
        await using var client = Client();
        await client.ConnectAsync();
        Assert.Equal("1.1.250", client.Address.ToString());
        Assert.Equal(22.4, (double)await client.ReadAsync("3/0/1", "9.001"), 1);
        Assert.Equal(1.0, (double)await client.ReadAsync("3/2/1", "20.102"));
        await Assert.ThrowsAsync<ReadOnlyModeException>(() => client.WriteAsync("1/0/1", "1.001", true));
        await Assert.ThrowsAsync<IoTComTimeoutException>(() => client.ReadAsync(KnxGroupAddress.Parse("7/7/7"), TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task A_write_reaches_the_actuator_and_every_tunnel_sees_the_status()
    {
        await using var operatorClient = Client(writes: true);
        await using var monitor = Client();
        await operatorClient.ConnectAsync();
        await monitor.ConnectAsync();
        Assert.Equal("1.1.251", monitor.Address.ToString());
        var seen = new List<KnxGroupEvent>();
        monitor.GroupValueReceived += e => { lock (seen) seen.Add(e); };
        var operatorSeen = new List<KnxGroupEvent>();
        operatorClient.GroupValueReceived += e => { lock (operatorSeen) operatorSeen.Add(e); };

        await operatorClient.WriteAsync("1/2/2", "5.001", 60);
        await Until(() => _floor.Rooms[1].LightOn);
        Assert.Equal(60, _floor.Rooms[1].Dim, 0);
        await Until(() => { lock (seen) return seen.Any(e => e.Destination.ToString() == "1/3/2"); });
        lock (seen)
        {
            Assert.Contains(seen, e => e.Destination.ToString() == "1/2/2" && e.Source.ToString() == "1.1.250" && e.Service == KnxApci.GroupValueWrite);
            Assert.Equal(60.0, (double)KnxDpt.Decode("5.001", seen.First(e => e.Destination.ToString() == "1/3/2").Value), 0);
        }

        lock (operatorSeen) Assert.DoesNotContain(operatorSeen, e => e.Destination.ToString() == "1/2/2");   // the sender gets L_Data.con, not its own telegram
    }

    [Fact]
    public async Task Heartbeats_keep_the_tunnel_and_disconnect_frees_it()
    {
        await using var a = Client();
        await using var b = Client();
        await using var c = Client();
        await a.ConnectAsync();
        await b.ConnectAsync();
        var refused = await Assert.ThrowsAsync<DeviceException>(async () => await c.ConnectAsync());
        Assert.Equal(KnxIpStatus.NoMoreConnections, refused.Code);
        await Task.Delay(700);                                    // a few heartbeats
        Assert.Equal(EndpointState.Connected, a.State);
        await a.DisconnectAsync();
        await Until(() => _gateway.Tunnels.Count == 1);
        await using var d = Client();
        await d.ConnectAsync();
        Assert.Equal("1.1.250", d.Address.ToString());
    }

    [Fact]
    public async Task A_repeated_request_is_acknowledged_but_applied_once()
    {
        var raw = _net.Bind();
        await raw.SendAsync(KnxIp.ConnectRequest(null, null), GatewayAddress);
        var (channel, _, _, _) = KnxIp.ReadConnectResponse(KnxIp.Parse((await raw.ReceiveAsync()).Data).Body.Span);
        var telegrams = 0;
        _bus.Telegram += (f, _) => { if (f.Group.ToString() == "1/0/4") Interlocked.Increment(ref telegrams); };
        var cemi = KnxCemiFrame.GroupTelegram(KnxMessageCode.DataRequest, default, KnxGroupAddress.Parse("1/0/4"), KnxApci.GroupValueWrite, KnxGroupValue.Short(1)).Encode();
        for (var i = 0; i < 2; i++) await raw.SendAsync(KnxIp.TunnelingRequest(channel, 0, cemi), GatewayAddress);
        var acks = 0;
        using var window = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            while (true)
            {
                var d = await raw.ReceiveAsync(window.Token);
                var (service, body) = KnxIp.Parse(d.Data);
                if (service == KnxServiceType.TunnelingAck) acks++;
                if (service == KnxServiceType.TunnelingRequest)
                {
                    var (ch, seq, _, _) = KnxIp.ReadConnectionHeader(body);
                    await raw.SendAsync(KnxIp.TunnelingAck(ch, seq, 0), GatewayAddress);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }

        Assert.Equal(2, acks);
        Assert.Equal(1, telegrams);
        Assert.True(_floor.Rooms[3].LightOn);
    }

    [Fact]
    public async Task A_client_that_stops_acknowledging_is_dropped()
    {
        var raw = _net.Bind();
        var closed = new TaskCompletionSource<string>();
        _gateway.TunnelClosed += (_, why) => closed.TrySetResult(why);
        await raw.SendAsync(KnxIp.ConnectRequest(null, null), GatewayAddress);
        await raw.ReceiveAsync();
        _floor.Step(30);                                          // the floor talks; nobody acknowledges
        Assert.Equal("no TUNNELING_ACK after a retry", await closed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Discovery_finds_the_interface()
    {
        await using var search = _net.Bind();
        var found = await KnxDiscovery.SearchAsync(search, TimeSpan.FromMilliseconds(300));
        var device = Assert.Single(found);
        Assert.Equal(("IoTCom KNX IP Router", "1.1.0", true, true), (device.FriendlyName, device.Address.ToString(), device.SupportsTunnelling, device.SupportsRouting));
        Assert.Equal(GatewayAddress, device.ControlEndpoint);
    }

    [Fact]
    public async Task Routing_participants_see_the_line_and_can_write()
    {
        await using var router = KnxRouter.Create(o => { o.UseInMemory(_net, new IPEndPoint(IPAddress.Parse("10.50.0.9"), KnxIp.Port)); o.AllowWrites(); });
        await router.ConnectAsync();
        var seen = new List<KnxGroupEvent>();
        router.GroupValueReceived += e => { lock (seen) seen.Add(e); };
        await router.WriteAsync("3/1/2", "9.001", 19.5);
        await Until(() => Math.Abs(_floor.Rooms[1].Setpoint - 19.5) < 0.01);
        Assert.Equal(23.1, (double)await router.ReadAsync("3/0/2", "9.001"), 1);
        _floor.Step(1);
        await Until(() => { lock (seen) return seen.Any(e => e.Destination.ToString() == "3/0/1"); });
    }
}
