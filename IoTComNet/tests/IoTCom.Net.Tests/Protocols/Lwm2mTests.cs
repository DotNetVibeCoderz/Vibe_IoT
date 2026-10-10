using System.Collections.Concurrent;
using System.Net;
using IoTCom.Net.Protocols.Coap;
using IoTCom.Net.Protocols.Lwm2m;
using IoTCom.Net.Serialization.SenML;
using IoTCom.Net.Transports;

namespace IoTCom.Net.Tests.Protocols;

public class Lwm2mCodecTests
{
    private static byte[] H(string hex) => Convert.FromHexString(hex.Replace(" ", "", StringComparison.Ordinal));

    // The Device object example from the LwM2M Core specification (TLV, /3/0).
    private const string SpecDevice =
        "C800144F70656E204D6F62696C6520416C6C69616E6365" +   // 0 Manufacturer "Open Mobile Alliance"
        "C801164C69676874776569676874204D324D20436C69656E74" + // 1 Model "Lightweight M2M Client"
        "C80209333435303030313233" +                         // 2 Serial "345000123"
        "C303312E30" +                                       // 3 Firmware "1.0"
        "8606 4100 01 4101 05" +                             // 6 Power sources [1, 5]
        "C1 09 64";                                          // 9 Battery 100

    [Fact]
    public void Paths_parse_and_nest()
    {
        var p = Lwm2mPath.Parse("/3/0/11/0");
        Assert.Equal((3, (ushort?)0, (ushort?)11, (ushort?)0), (p.ObjectId, p.InstanceId, p.ResourceId, p.ResourceInstanceId));
        Assert.Equal(4, p.Depth);
        Assert.Equal("/3303/0", Lwm2mPath.Parse("3303/0").ToString());
        Assert.True(Lwm2mPath.Parse("/3").Contains(p));
        Assert.False(Lwm2mPath.Parse("/3/1").Contains(p));
        Assert.Throws<FormatException>(() => Lwm2mPath.Parse("/3/x"));
        Assert.Throws<FormatException>(() => Lwm2mPath.Parse("/1/2/3/4/5"));
        Assert.Throws<FormatException>(() => Lwm2mPath.Parse("/65535"));
    }

    [Fact]
    public void Tlv_matches_the_specification_example()
    {
        var values = Lwm2mContent.DecodeTlv(Lwm2mPath.Parse("/3/0"), H(SpecDevice));
        Assert.Equal("Open Mobile Alliance", values.Single(v => v.Path == Lwm2mPath.Parse("/3/0/0")).Value);
        Assert.Equal("Lightweight M2M Client", values.Single(v => v.Path == Lwm2mPath.Parse("/3/0/1")).Value);
        Assert.Equal([1L, 5L], values.Where(v => v.Path.ResourceId == 6).Select(v => v.Value));
        Assert.Equal(100L, values.Single(v => v.Path.ResourceId == 9).Value);
        Assert.Equal(H(SpecDevice), Lwm2mContent.EncodeTlv(Lwm2mPath.Parse("/3/0"), values));

        // The same instance wrapped in an object-instance TLV when reading /3.
        var wrapped = Lwm2mContent.EncodeTlv(Lwm2mPath.Parse("/3"), values);
        Assert.Equal(0x08, wrapped[0]);                 // object instance 0, 8-bit length
        Assert.Equal(values, Lwm2mContent.DecodeTlv(Lwm2mPath.Parse("/3"), wrapped));
    }

    [Fact]
    public void Values_encode_with_minimal_sizes()
    {
        Assert.Equal(H("05"), Lwm2mContent.Bytes(5L));
        Assert.Equal(H("FF38"), Lwm2mContent.Bytes(-200L));
        Assert.Equal(H("0000EA60"), Lwm2mContent.Bytes(60_000L));                  // too big for 2 bytes: 4
        Assert.Equal(H("41480000"), Lwm2mContent.Bytes(12.5));                     // fits a float
        Assert.Equal(8, Lwm2mContent.Bytes(0.1).Length);                           // needs a double
        Assert.Equal(H("01"), Lwm2mContent.Bytes(true));
        Assert.Equal(H("000C00E5"), Lwm2mContent.Bytes(new Lwm2mObjectLink(0x0C, 0xE5)));
        Assert.Equal(H("FF"), Lwm2mContent.Bytes(255UL));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_760_000_000), Lwm2mContent.FromBytes(Lwm2mContent.Bytes(DateTimeOffset.FromUnixTimeSeconds(1_760_000_000)), Lwm2mType.Time));
        Assert.Equal(-200L, Lwm2mContent.FromBytes(H("FF38"), Lwm2mType.Integer));
        Assert.Equal(12.5, Lwm2mContent.FromBytes(H("41480000"), Lwm2mType.Float));
        Assert.Throws<ProtocolException>(() => Lwm2mContent.FromBytes(H("000000"), Lwm2mType.Integer));
        Assert.Throws<ProtocolException>(() => Lwm2mContent.FromBytes(H("02"), Lwm2mType.Boolean));
    }

    [Fact]
    public void SenML_and_text_round_trip_and_bad_payloads_fail()
    {
        var path = Lwm2mPath.Parse("/3311/0");
        var values = new List<Lwm2mValue>
        {
            new(Lwm2mPath.Parse("/3311/0/5850"), true), new(Lwm2mPath.Parse("/3311/0/5851"), 60L),
            new(Lwm2mPath.Parse("/3311/0/5805"), 12.25), new(Lwm2mPath.Parse("/3311/0/5706"), "3000K"),
        };
        var json = Lwm2mContent.Encode(Lwm2mFormat.SenMLJson, path, values);
        Assert.Contains("\"bn\":\"/3311/0/\"", System.Text.Encoding.UTF8.GetString(json), StringComparison.Ordinal);
        Assert.Equal(values.OrderBy(v => v.Path.ToString(), StringComparer.Ordinal), Lwm2mContent.Decode(Lwm2mFormat.SenMLJson, path, json));
        Assert.Equal(values.OrderBy(v => v.Path.ToString(), StringComparer.Ordinal), Lwm2mContent.Decode(Lwm2mFormat.SenMLCbor, path, Lwm2mContent.Encode(Lwm2mFormat.SenMLCbor, path, values)));

        Assert.Equal(60L, Lwm2mContent.Decode(Lwm2mFormat.Text, Lwm2mPath.Parse("/3311/0/5851"), "60"u8.ToArray()).Single().Value);
        Assert.Equal(true, Lwm2mContent.Decode(Lwm2mFormat.Text, Lwm2mPath.Parse("/3311/0/5850"), "1"u8.ToArray()).Single().Value);
        Assert.Throws<ProtocolException>(() => Lwm2mContent.Decode(Lwm2mFormat.Text, Lwm2mPath.Parse("/3311/0/5851"), "sixty"u8.ToArray()));
        Assert.Throws<ArgumentException>(() => Lwm2mContent.Encode(Lwm2mFormat.Text, path, values));
        Assert.Throws<ProtocolException>(() => Lwm2mContent.DecodeTlv(Lwm2mPath.Parse("/3/0"), H("C8 00 14 4F70")));   // length past the end
        Assert.Throws<ProtocolException>(() => Lwm2mContent.DecodeTlv(Lwm2mPath.Parse("/3/0"), H("41 00 01")));        // resource instance outside a resource
        Assert.Throws<ProtocolException>(() => Lwm2mContent.Decode(Lwm2mFormat.SenMLJson, path, "[{\"n\":\"x\",\"v\":1}]"u8.ToArray()));
        Assert.Contains(Lwm2mContent.DescribeTlv(H(SpecDevice)), f => f.Name == "multi-resource 6");
    }
}

public class Lwm2mSessionTests
{
    private static readonly IPEndPoint ServerAddress = new(IPAddress.Parse("10.30.0.1"), 5683);

    private static async Task Until(Func<bool> condition, string what)
    {
        for (var i = 0; i < 300 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition(), $"timed out waiting for {what}");
    }

    private static async Task<(InMemoryDatagramNetwork Net, Lwm2mServer Server, Lwm2mStreetLightSimulator Light)> SetupAsync(bool allowWrites = false, Action<Lwm2mClientOptions>? client = null)
    {
        var net = new InMemoryDatagramNetwork();
        var fast = new CoapTransmission { AckTimeout = TimeSpan.FromMilliseconds(150), MaxRetransmit = 2, ResponseTimeout = TimeSpan.FromSeconds(3) };
        var server = Lwm2mServer.Create(o =>
        {
            o.UseInMemory(net, ServerAddress);
            o.Transmission = fast;
            o.RequestTimeout = TimeSpan.FromSeconds(3);
            o.ExpiryGrace = TimeSpan.FromMilliseconds(300);
            if (allowWrites) o.AllowWrites();
        });
        await server.StartAsync();
        var light = Lwm2mStreetLightSimulator.Create(o =>
        {
            o.UseInMemory(net);
            o.UseServer(ServerAddress);
            o.Transmission = fast;
            client?.Invoke(o);
        });
        await light.StartAsync();
        return (net, server, light);
    }

    [Fact]
    public async Task Registers_and_answers_reads_in_every_format()
    {
        var (_, server, light) = await SetupAsync();
        await using var _s = server;
        await using var _l = light;
        var r = Assert.Single(server.Registrations);
        Assert.Equal("urn:dev:light:SL60-000417", r.Endpoint);
        Assert.Equal(["/3/0", "/6/0", "/3303/0", "/3311/0"], r.Objects.Select(o => o.ToString()));
        Assert.Equal("1.1", r.Version);
        Assert.Equal(light.Client.RegistrationId, r.Id);

        var ep = r.Endpoint;
        Assert.Equal("IoTCom Simulated Lighting", await server.ReadValueAsync(ep, Lwm2mPath.Parse("/3/0/0")));
        var device = await server.ReadAsync(ep, Lwm2mPath.Parse("/3/0"));
        Assert.Equal("SL60-000417", device.Single(v => v.Path.ResourceId == 2).Value);
        Assert.Contains(device, v => v.Path.ToString() == "/3/0/11/0");
        Assert.DoesNotContain(device, v => v.Path.ResourceId == 4);                                  // executable, not readable
        var senml = await server.ReadAsync(ep, Lwm2mPath.Parse("/6/0"), Lwm2mFormat.SenMLJson);
        Assert.Equal(-6.9147, senml.Single(v => v.Path.ResourceId == 0).Value);
        var cbor = await server.ReadAsync(ep, Lwm2mPath.Parse("/3311"), Lwm2mFormat.SenMLCbor);
        Assert.Equal(false, cbor.Single(v => v.Path.ResourceId == 5850).Value);

        var links = await server.DiscoverAsync(ep, Lwm2mPath.Parse("/3311/0"));
        Assert.Contains(links, l => l.Path == "/3311/0/5851");
        var missing = await Assert.ThrowsAsync<Lwm2mException>(() => server.ReadAsync(ep, Lwm2mPath.Parse("/9/0")));
        Assert.Equal(CoapCode.NotFound, missing.Status);
        Assert.Equal(CoapCode.MethodNotAllowed, (await Assert.ThrowsAsync<Lwm2mException>(() => server.ReadAsync(ep, Lwm2mPath.Parse("/3/0/4")))).Status);
        await Assert.ThrowsAsync<ReadOnlyModeException>(() => server.WriteAsync(ep, Lwm2mPath.Parse("/3311/0/5850"), true));
        await Assert.ThrowsAsync<ReadOnlyModeException>(() => server.ExecuteAsync(ep, Lwm2mPath.Parse("/3/0/4")));
    }

    [Fact]
    public async Task Writes_executes_and_their_refusals()
    {
        var (_, server, light) = await SetupAsync(allowWrites: true);
        await using var _s = server;
        await using var _l = light;
        const string ep = "urn:dev:light:SL60-000417";
        var handled = new ConcurrentQueue<Lwm2mRequestRecord>();
        light.Client.RequestHandled += handled.Enqueue;

        await server.WriteAsync(ep, Lwm2mPath.Parse("/3311/0/5850"), true);
        Assert.True(light.IsOn);
        await server.WriteAsync(ep, Lwm2mPath.Parse("/3311/0"), [new(Lwm2mPath.Parse("/3311/0/5851"), 35L), new(Lwm2mPath.Parse("/3311/0/5706"), "4000K")], replace: false);
        Assert.Equal(35, light.Dimmer);
        await server.WriteAsync(ep, Lwm2mPath.Parse("/3311/0/5851"), [new(Lwm2mPath.Parse("/3311/0/5851"), 50L)], format: Lwm2mFormat.SenMLJson);
        Assert.Equal(50, light.Dimmer);
        Assert.Equal(CoapCode.BadRequest, (await Assert.ThrowsAsync<Lwm2mException>(() => server.WriteAsync(ep, Lwm2mPath.Parse("/3311/0/5851"), 150L))).Status);
        Assert.Equal(50, light.Dimmer);
        Assert.Equal(CoapCode.MethodNotAllowed, (await Assert.ThrowsAsync<Lwm2mException>(() => server.WriteAsync(ep, Lwm2mPath.Parse("/3/0/0"), "Someone else"))).Status);

        await server.ExecuteAsync(ep, Lwm2mPath.Parse("/3/0/4"));
        Assert.Equal(1, light.Reboots);
        Assert.Equal(CoapCode.MethodNotAllowed, (await Assert.ThrowsAsync<Lwm2mException>(() => server.ExecuteAsync(ep, Lwm2mPath.Parse("/3/0/0")))).Status);
        Assert.Contains(handled, h => h.Operation == "Execute" && h.Result == CoapCode.Changed);
    }

    [Fact]
    public async Task Observations_follow_attributes_and_cancel()
    {
        var (_, server, light) = await SetupAsync();
        await using var _s = server;
        await using var _l = light;
        const string ep = "urn:dev:light:SL60-000417";
        var path = Lwm2mPath.Parse("/3303/0/5700");
        await server.WriteAttributesAsync(ep, path, pmin: 0, pmax: 1);
        Assert.Contains(await server.DiscoverAsync(ep, Lwm2mPath.Parse("/3303/0")), l => l.Path == "/3303/0/5700" && l.Attributes.GetValueOrDefault("pmax") == "1");

        var seen = new ConcurrentQueue<double>();
        var observation = await server.ObserveAsync(ep, path, values => seen.Enqueue((double)values.Single().Value));
        Assert.Equal(31.0, observation.Initial.Single().Value);
        light.Light.Set(5850, true);
        light.Step(60);                                            // the driver warms up: a change
        await Until(() => !seen.IsEmpty, "a notification after a change");
        var count = seen.Count;
        await Until(() => seen.Count > count, "a notification after pmax with no change");
        Assert.Equal(1, light.Client.ObservationCount);
        await observation.DisposeAsync();
        await Until(() => light.Client.ObservationCount == 0, "observation cancelled");
    }

    [Fact]
    public async Task Updates_deregistration_and_expiry()
    {
        var (net, server, light) = await SetupAsync(client: o => o.Lifetime = TimeSpan.FromSeconds(1));
        await using var _s = server;
        var events = new ConcurrentQueue<string>();
        server.Updated += r => events.Enqueue("updated");
        server.Deregistered += (r, why) => events.Enqueue(why.ToString());
        await Task.Delay(1500);
        Assert.Contains("updated", events);                      // the client renews at 80 % of its lifetime
        Assert.Single(server.Registrations);

        await light.DisposeAsync();
        await Until(() => events.Contains("Deregistered"), "deregistration");
        Assert.Empty(server.Registrations);

        await using var silent = Lwm2mClient.Create(o =>
        {
            o.EndpointName = "silent";
            o.UseInMemory(net);
            o.UseServer(ServerAddress);
            o.Lifetime = TimeSpan.FromSeconds(1);
            o.Transmission = new CoapTransmission { AckTimeout = TimeSpan.FromMilliseconds(100), MaxRetransmit = 0 };
        });
        silent.AddInstance(3).Set(0, "x");
        await silent.ConnectAsync();
        net.LossRate = 1;                                        // the device drops off the network
        await Until(() => events.Contains("Expired"), "expiry");
        net.LossRate = 0;
    }
}

public class Lwm2mConformanceTests
{
    public static IEnumerable<object[]> Vectors => Conformance.Cases("lwm2m.json");

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Shared_vectors(string json)
    {
        var v = Conformance.Parse(json);
        var path = Lwm2mPath.Parse(v.GetProperty("path").GetString()!);
        var data = v.Hex("data");
        var expected = v.GetProperty("fields").GetString();
        if (expected == "error")
        {
            Assert.ThrowsAny<ProtocolException>(() => Lwm2mContent.DecodeTlv(path, data));
            return;
        }

        var values = Lwm2mContent.DecodeTlv(path, data);
        Assert.Equal(expected, string.Join(";", values.Select(x => $"{x.Path}={x.Type}:{Lwm2mValue.Format(x.Value)}")));
        Assert.Equal(data, Lwm2mContent.EncodeTlv(path, values));
    }
}
