using System.Net;
using IoTCom.Net.Protocols.Mdns;
using IoTCom.Net.Transports;

namespace IoTCom.Net.Tests.Protocols;

public class MdnsCodecTests
{
    [Fact]
    public void A_ptr_query_has_the_expected_bytes()
    {
        var q = new DnsMessage { Questions = [new DnsQuestion("_http._tcp.local", DnsType.Ptr)] }.Encode();
        Assert.Equal("000000000001000000000000055F68747470045F746370056C6F63616C00000C0001", Convert.ToHexString(q));
    }

    [Fact]
    public void Responses_round_trip_with_compression()
    {
        var service = new MdnsService
        {
            Instance = "Line 1 gateway", Type = "_modbus._tcp", Port = 502, Host = "gw-01.local",
            Addresses = [IPAddress.Parse("10.0.0.10")], Properties = new Dictionary<string, string> { ["unit"] = "1", ["vendor"] = "iotcom" },
        };
        var m = new DnsMessage { IsResponse = true, Authoritative = true, Answers = [.. service.ToRecords(120)] };
        var bytes = m.Encode();
        var back = DnsMessage.Decode(bytes);
        Assert.True(back.IsResponse && back.Authoritative);
        Assert.Equal(m.Answers.Select(a => a.ToString()), back.Answers.Select(a => a.ToString()));
        var srv = back.Answers.Single(a => a.Type == DnsType.Srv);
        Assert.Equal(("gw-01.local", (ushort)502, true), (srv.Target, srv.Port, srv.CacheFlush));
        Assert.Equal(["unit=1", "vendor=iotcom"], back.Answers.Single(a => a.Type == DnsType.Txt).Text);
        // "_modbus._tcp.local" appears once in full; later occurrences are 2-byte pointers.
        Assert.True(bytes.Length < 160, $"{bytes.Length} bytes");
        Assert.All(DnsMessage.Decode(new DnsMessage { IsResponse = true, Answers = [.. service.ToRecords(0)] }.Encode()).Answers, a => Assert.Equal(0u, a.Ttl));
    }

    [Theory]
    [InlineData("0000")]
    [InlineData("000000000001000000000000C00C000C0001")]      // pointer to itself
    [InlineData("0000000000010000000000003F41")]              // label past the end
    [InlineData("00000000FFFF00000000000000")]                // counts larger than the message
    public void Malformed_messages_are_rejected(string hex) => Assert.False(DnsMessage.TryDecode(Convert.FromHexString(hex), out _, out _));
}

public class MdnsDiscoveryTests
{
    private static MdnsService Gateway(string instance = "Line 1 gateway", string type = "_modbus._tcp", ushort port = 502) => new()
    {
        Instance = instance, Type = type, Port = port, Host = "gw-01.local",
        Addresses = [IPAddress.Parse("10.0.0.10")], Properties = new Dictionary<string, string> { ["unit"] = "1" },
    };

    [Fact]
    public async Task Browser_finds_resolves_and_loses_a_service()
    {
        var net = new InMemoryDatagramNetwork();
        await using var responder = MdnsResponder.Create(o => o.UseInMemory(net, IPAddress.Parse("10.0.0.10")));
        await responder.StartAsync();
        await responder.RegisterAsync(Gateway());
        await responder.RegisterAsync(Gateway("Weather station", "_coap._udp", 5683));
        await using var browser = MdnsBrowser.Create(o => o.UseInMemory(net, IPAddress.Parse("10.0.0.20")));
        await browser.StartAsync();

        var found = Assert.Single(await browser.BrowseAsync("_modbus._tcp", TimeSpan.FromMilliseconds(600)));
        Assert.Equal(("Line 1 gateway", (ushort)502, "gw-01.local"), (found.Instance, found.Port, found.Host));
        Assert.Equal(IPAddress.Parse("10.0.0.10"), found.Address);
        Assert.Equal("1", found.Properties["unit"]);
        Assert.Equal(["_coap._udp.local", "_modbus._tcp.local"], await browser.EnumerateTypesAsync(TimeSpan.FromMilliseconds(200)));

        var lost = new TaskCompletionSource<MdnsChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        browser.ServiceChanged += (_, c) => { if (c.Lost) lost.TrySetResult(c); };
        await responder.UnregisterAsync(found.FullName);
        Assert.Equal("Line 1 gateway", (await lost.Task.WaitAsync(TimeSpan.FromSeconds(5))).Service.Instance);
    }

    [Fact]
    public async Task Known_answers_keep_the_responder_quiet_and_qu_questions_get_unicast()
    {
        var net = new InMemoryDatagramNetwork();
        await using var responder = MdnsResponder.Create(o => o.UseInMemory(net, IPAddress.Parse("10.0.0.10")));
        await responder.StartAsync();
        await responder.RegisterAsync(Gateway());
        await using var asker = net.Bind(new IPEndPoint(IPAddress.Parse("10.0.0.30"), 40000));   // a one-shot (legacy) querier

        var ptr = new DnsRecord { Name = "_modbus._tcp.local", Type = DnsType.Ptr, Target = "Line 1 gateway._modbus._tcp.local", Ttl = 4500 };
        await asker.SendAsync(new DnsMessage { Questions = [new DnsQuestion("_modbus._tcp.local", DnsType.Ptr)], Answers = [ptr] }.Encode(), MdnsAddresses.Endpoint4);
        await Task.Delay(200);
        Assert.Equal(0, responder.QueriesAnswered);

        await asker.SendAsync(new DnsMessage { Id = 77, Questions = [new DnsQuestion("_modbus._tcp.local", DnsType.Ptr, UnicastResponse: true)] }.Encode(), MdnsAddresses.Endpoint4);
        var reply = DnsMessage.Decode((await asker.ReceiveAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2))).Data.Span);
        Assert.Equal(77, reply.Id);
        Assert.Contains(reply.Answers, a => a.Type == DnsType.Ptr);
        Assert.Contains(reply.Additionals, a => a.Type == DnsType.Srv && a.Port == 502);
    }
}

public class MdnsSimulatorTests
{
    [Fact]
    public async Task Simulated_plant_is_discoverable_and_unplugging_sends_goodbye()
    {
        await using var plant = new MdnsSimulator();
        await plant.StartAsync();
        await using var browser = plant.Browser();
        await browser.StartAsync();
        var types = await browser.EnumerateTypesAsync(TimeSpan.FromMilliseconds(400));
        Assert.Contains("_modbus._tcp.local", types);
        Assert.Contains("_ipp._tcp.local", types);
        var gateways = await browser.BrowseAsync("_modbus._tcp", TimeSpan.FromMilliseconds(400));
        var gw = Assert.Single(gateways);
        Assert.Equal(502, gw.Port);
        Assert.Equal("1-8", gw.Properties["units"]);
        Assert.Equal("10.20.0.11", gw.Address!.ToString());

        var lost = new TaskCompletionSource<MdnsChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        browser.ServiceChanged += (_, c) => { if (c.Lost) lost.TrySetResult(c); };
        await plant.UnplugAsync("Line 1 Modbus gateway");
        var change = await lost.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Line 1 Modbus gateway", change.Service.Instance);
    }

    [Fact]
    public void Dns_anatomy_lists_questions_and_answers()
    {
        var m = new DnsMessage { IsResponse = true, Authoritative = true, Answers = [.. MdnsSimulator.Devices[0].ToRecords()] };
        var fields = DnsMessage.Describe(m.Encode());
        Assert.Equal("response", fields[1].Value);
        Assert.Equal(m.Answers.Count, fields.Count(f => f.Name == "Answer"));
        Assert.Equal(FrameFieldKind.Error, DnsMessage.Describe([1, 2, 3]).Single().Kind);
    }
}
