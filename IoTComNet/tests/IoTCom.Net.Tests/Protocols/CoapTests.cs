using System.Net;
using System.Text;
using IoTCom.Net.Protocols.Coap;
using IoTCom.Net.Serialization.SenML;
using IoTCom.Net.Transports;

namespace IoTCom.Net.Tests.Protocols;

public sealed class CoapCodecTests
{
    public static IEnumerable<object[]> Vectors => Conformance.Cases("coap.json");

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Shared_conformance_vectors(string json)
    {
        var v = Conformance.Parse(json);
        var wire = v.Hex("wire");
        var ok = CoapMessage.TryDecode(wire, out var m, out var error);
        if (!v.GetProperty("valid").GetBoolean())
        {
            Assert.False(ok, v.GetProperty("name").GetString());
            return;
        }
        Assert.True(ok, error);
        Assert.Equal(v.GetProperty("type").GetInt32(), (int)m!.Type);
        Assert.Equal(v.GetProperty("code").GetInt32(), m.Code.Value);
        Assert.Equal(v.GetProperty("messageId").GetInt32(), m.MessageId);
        Assert.Equal(v.Hex("token"), m.Token.ToArray());
        Assert.Equal(v.Hex("payload"), m.Payload.ToArray());
        Assert.Equal(v.GetProperty("options").GetString(), string.Join(';', m.Options.Select(o => $"{o.Number}:{Convert.ToHexString(o.Value.Span)}")));
        Assert.Equal(wire, m.Encode());
    }

    [Fact]
    public void Builds_requests_with_sorted_options()
    {
        var m = new CoapMessage { Code = CoapCode.Get, MessageId = 0x7D34 };
        m.Accept = CoapContentFormat.SenMLJson;
        m.UriPath = "/sensors/temp";
        m.Observe = 0;
        Assert.Equal([6, 11, 11, 17], m.Options.Select(o => (int)o.Number));
        Assert.Equal("/sensors/temp", m.UriPath);
        var back = CoapMessage.Decode(m.Encode());
        Assert.Equal(CoapContentFormat.SenMLJson, back.Accept);
        Assert.Equal(0u, back.Observe);
        // Assigning null removes the option (regression: it used to add an empty value = format 0).
        var plain = new CoapMessage { Code = CoapCode.Changed, ContentFormat = null, MaxAge = null, Accept = null, Observe = null, Block2 = null };
        Assert.Empty(plain.Options);
        Assert.Null(plain.ContentFormat);
        Assert.Equal("2.05 Content", CoapCode.Content.ToString());
        Assert.Equal("4.04 Not Found", CoapCode.NotFound.ToString());
        Assert.Equal(new CoapBlock(3, true, 6), CoapBlock.Decode(new CoapBlock(3, true, 6).Encode()));
        Assert.Contains(CoapAnatomy.Describe(m.Encode()), f => f.Name == "Uri-Path" && f.Value == "sensors");
    }

    [Fact]
    public void Link_format_round_trips()
    {
        const string doc = "</sensors/temp>;rt=\"temperature-c\";if=\"sensor\";obs;ct=0,</actuators/fan>;title=\"Fan, main\"";
        var links = CoapLinkFormat.Parse(doc);
        Assert.Equal(2, links.Count);
        Assert.True(links[0].Observable);
        Assert.Equal("temperature-c", links[0].ResourceType);
        Assert.Equal((ushort)0, links[0].ContentFormat);
        Assert.Equal("Fan, main", links[1].Title);
        Assert.Equal(links.Count, CoapLinkFormat.Parse(CoapLinkFormat.Format(links)).Count);
    }
}

public sealed class CoapEndpointTests
{
    private static readonly IPEndPoint ServerAddress = new(IPAddress.Parse("10.0.0.40"), 5683);

    private static async Task<(CoapServer Server, CoapDeviceSimulator Device, CoapClient Client, InMemoryDatagramNetwork Net)> RigAsync(
        double loss = 0, double duplicates = 0, bool readOnly = false)
    {
        var net = new InMemoryDatagramNetwork(seed: 7);
        var fast = new CoapTransmission { AckTimeout = TimeSpan.FromMilliseconds(40), MaxRetransmit = 8, ResponseTimeout = TimeSpan.FromSeconds(5) };
        var server = CoapServer.Create(o => o.UseInMemory(net, ServerAddress).Transmission = fast);
        var device = new CoapDeviceSimulator(server);
        await server.StartAsync();
        var client = CoapClient.Create(o =>
        {
            o.UseInMemory(net).UseServer(ServerAddress);
            o.Transmission = fast;
            o.ReadOnly = readOnly;
        });
        await client.ConnectAsync();
        net.LossRate = loss;
        net.DuplicateRate = duplicates;
        return (server, device, client, net);
    }

    [Fact]
    public async Task Get_put_discover_and_errors()
    {
        var (server, device, client, _) = await RigAsync();
        await using (server)
        await using (device)
        await using (client)
        {
            Assert.Equal("27.5", (await client.GetAsync("/sensors/temperature")).PayloadText);
            Assert.Equal(CoapCode.Changed, (await client.PutAsync("/actuators/fan", "on")).Code);
            Assert.True(device.FanOn);
            Assert.Equal(CoapCode.BadRequest, (await client.PutAsync("/actuators/valve", "150")).Code);
            Assert.Equal(CoapCode.NotFound, (await client.GetAsync("/nope")).Code);
            Assert.Equal(CoapCode.MethodNotAllowed, (await client.DeleteAsync("/sensors/temperature")).Code);

            var all = await client.DiscoverAsync();
            Assert.Contains(all, l => l.Path == "/sensors/soil" && l.Observable);
            var temps = await client.DiscoverAsync("rt=temperature*");
            Assert.Equal(["/sensors/temperature"], temps.Select(l => l.Path));

            var senml = await client.GetAsync("/sensors/humidity", CoapContentFormat.SenMLJson);
            Assert.Equal(CoapContentFormat.SenMLJson, senml.ContentFormat);
            var record = SenMLCodec.Resolve(SenMLCodec.ParseJson(senml.Payload.Span)).Single();
            Assert.Equal("urn:dev:greenhouse-node-01:humidity", record.Name);
            Assert.Equal(68, record.Value);

            var unknownCritical = new CoapMessage { Code = CoapCode.Get, UriPath = "/sensors/soil" };
            unknownCritical.AddOption(65001, "x");
            Assert.Equal(CoapCode.BadOption, (await client.SendAsync(unknownCritical)).Code);
            Assert.True((await client.PingAsync()) < TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task Read_only_client_blocks_state_changes()
    {
        var (server, device, client, _) = await RigAsync(readOnly: true);
        await using (server)
        await using (device)
        await using (client)
        {
            await Assert.ThrowsAsync<ReadOnlyModeException>(() => client.PutAsync("/actuators/fan", "on"));
            Assert.False(device.FanOn);
            Assert.True((await client.GetAsync("/actuators/fan")).IsSuccess);
        }
    }

    [Fact]
    public async Task Block_wise_transfers_in_both_directions()
    {
        var (server, device, client, _) = await RigAsync();
        await using (server)
        await using (device)
        await using (client)
        {
            client.Options.Transmission.BlockSize = 256;
            var log = await client.GetAsync("/device/log");
            Assert.True(log.Payload.Length > 3000, $"log is {log.Payload.Length} bytes");
            Assert.Contains("boot step 59", log.PayloadText);

            var image = Enumerable.Range(0, 5000).Select(i => (byte)(i * 13)).ToArray();
            Assert.Equal(CoapCode.Changed, (await client.PutAsync("/firmware", image, CoapContentFormat.OctetStream)).Code);
            Assert.Equal($"5000 bytes, sum {image.Aggregate(0u, (a, b) => a + b)}", (await client.GetAsync("/firmware")).PayloadText);
        }
    }

    [Fact]
    public async Task Slow_handler_gets_a_separate_response()
    {
        var (server, device, client, _) = await RigAsync();
        await using (server)
        await using (device)
        await using (client)
        {
            var r = await client.PostAsync("/device/calibrate", default);
            Assert.Equal("calibrated", r.PayloadText);
            Assert.Equal(CoapType.Confirmable, r.Message.Type); // separate response, not piggybacked
        }
    }

    [Fact]
    public async Task Observe_streams_notifications_and_cancels()
    {
        var (server, device, client, _) = await RigAsync();
        await using (server)
        await using (device)
        await using (client)
        {
            var temp = server.Resources.Single(r => r.Path == "/sensors/temperature");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var seen = new List<CoapResponse>();
            await foreach (var n in client.ObserveAsync("/sensors/temperature", ct: cts.Token))
            {
                seen.Add(n);
                if (seen.Count == 4) break;
                for (var i = 0; i < 12; i++) await device.StepAsync(); // at least one notification (every 10 ticks)
            }
            Assert.Equal(4, seen.Count);
            Assert.All(seen, n => Assert.NotNull(n.ObserveSequence));
            Assert.True(seen.Select(n => n.ObserveSequence!.Value).SequenceEqual(seen.Select(n => n.ObserveSequence!.Value).Order()));
            // Leaving the loop deregisters (Observe = 1).
            for (var i = 0; i < 50 && temp.ObserverCount > 0; i++) await Task.Delay(20);
            Assert.Equal(0, temp.ObserverCount);
        }
    }

    [Fact]
    public async Task Survives_a_lossy_link_with_duplicates()
    {
        var (server, device, client, net) = await RigAsync(loss: 0.3, duplicates: 0.3);
        await using (server)
        await using (device)
        await using (client)
        {
            for (var i = 0; i < 20; i++) Assert.Equal(CoapCode.Changed, (await client.PutAsync("/actuators/valve", (i % 100).ToString(System.Globalization.CultureInfo.InvariantCulture))).Code);
            Assert.Equal(19, device.ValvePercent);
            Assert.True(client.Statistics.RetransmissionCount > 0, "expected retransmissions");
            Assert.True(server.Statistics.DuplicateCount > 0, "expected duplicates to be suppressed");
            Assert.True(net.Dropped > 0);
            // Each PUT reached the handler exactly once despite retransmissions and duplicates.
            Assert.Equal(20, server.RequestCount);
        }
    }

    [Fact]
    public async Task Block2_survives_a_lossy_link()
    {
        var (server, device, client, net) = await RigAsync(loss: 0.2);
        await using (server)
        await using (device)
        await using (client)
        {
            client.Options.Transmission.BlockSize = 256;
            for (var i = 0; i < 2; i++)
                Assert.True((await client.GetAsync("/device/log")).Payload.Length > 3000);
            Assert.True(net.Dropped > 0);
        }
    }

    [Fact]
    public async Task Works_over_real_udp()
    {
        await using var server = CoapServer.Create(o => o.UseUdp(0, IPAddress.Loopback));
        server.Map("/hello", get: (r, _) => ValueTask.FromResult(CoapReply.Content("hello " + r.Remote.ToString()!.Split(':')[0])));
        await server.StartAsync();
        await using var client = CoapClient.Create(o => o.UseServer(server.LocalEndPoint!));
        await client.ConnectAsync();
        Assert.Equal("hello 127.0.0.1", (await client.GetAsync("/hello")).PayloadText);
        Assert.Equal(Encoding.UTF8.GetBytes("hello 127.0.0.1"), (await client.GetAsync("/hello")).Payload.ToArray());
    }
}
