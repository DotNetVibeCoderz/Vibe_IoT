using System.Net;
using System.Text;
using IoTCom.Net.Adapters.Mqtt;
using IoTCom.Net.Hosting;
using IoTCom.Net.Protocols.Dmx;
using IoTCom.Net.Protocols.Modbus;
using IoTCom.Net.Protocols.Nmea;
using IoTCom.Net.Serialization.SenML;
using IoTCom.Net.Transports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace IoTCom.Net.Tests.Protocols;

public class NmeaTests
{
    [Fact]
    public void Parses_gga_with_checksum()
    {
        var m = Assert.IsType<GgaMessage>(NmeaParser.Parse("$GPGGA,123519,4807.038,N,01131.000,E,1,08,0.9,545.4,M,46.9,M,,*47"));
        Assert.Equal(48.1173, m.Latitude!.Value, 4);
        Assert.Equal(11.516667, m.Longitude!.Value, 5);
        Assert.Equal(GpsFixQuality.Gps, m.FixQuality);
        Assert.Equal(8, m.Satellites);
        Assert.Equal(545.4, m.AltitudeMeters);
        Assert.Equal(new TimeOnly(12, 35, 19), m.Time);
    }

    [Fact]
    public void Parses_rmc_and_rejects_bad_checksum()
    {
        var rmc = Assert.IsType<RmcMessage>(NmeaParser.Parse("$GPRMC,123519,A,4807.038,N,01131.000,E,022.4,084.4,230394,003.1,W*6A"));
        Assert.True(rmc.Active);
        Assert.Equal(new DateTimeOffset(1994, 3, 23, 12, 35, 19, TimeSpan.Zero), rmc.Timestamp);
        Assert.Equal(22.4 * 1.852, rmc.SpeedKmh!.Value, 3);
        Assert.Equal(-3.1, rmc.MagneticVariation);
        Assert.Null(NmeaParser.Parse("$GPRMC,123519,A,4807.038,N,01131.000,E,022.4,084.4,230394,003.1,W*6B"));
    }

    [Fact]
    public void Builder_produces_valid_sentences_and_coordinates_roundtrip()
    {
        var (lat, h) = NmeaParser.FormatCoordinate(-6.9147, isLatitude: true);
        Assert.Equal("S", h);
        Assert.Equal(-6.9147, NmeaParser.Coordinate(lat, h)!.Value, 6);
        var s = NmeaSentence.Build("GP", "GLL", lat, h, "10736.588", "E", "120000.00", "A");
        Assert.True(NmeaSentence.TryParse(s, out var parsed));
        Assert.True(parsed!.ChecksumValid);
    }

    [Fact]
    public async Task Simulator_server_and_reader_end_to_end()
    {
        var listener = new InMemoryTransportListener();
        await using var server = NmeaServer.Create(o => o.ListenInMemory(listener));
        await server.StartAsync();
        await using var reader = NmeaReader.Create(o => o.UseInMemory(listener));
        await reader.ConnectAsync();
        for (var i = 0; i < 50 && server.ClientCount == 0; i++) await Task.Delay(10);

        var sim = new NmeaSimulator();
        var start = DateTimeOffset.UtcNow;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var gga = reader.ReadAsync<GgaMessage>(cts.Token).GetAsyncEnumerator(cts.Token);
        var moveNext = gga.MoveNextAsync();
        await Task.Delay(50);
        foreach (var line in sim.GenerateEpoch(start, TimeSpan.FromSeconds(10))) await server.BroadcastAsync(line);
        Assert.True(await moveNext);
        Assert.Equal(GpsFixQuality.Gps, gga.Current.FixQuality);
        await gga.DisposeAsync();

        for (var i = 0; i < 50 && reader.Gnss.Current.SatellitesInView.Count == 0; i++) await Task.Delay(10);
        var fix = reader.Gnss.Current;
        Assert.True(fix.HasFix);
        Assert.Equal(-6.91, fix.Latitude!.Value, 1);
        Assert.Equal(36, fix.SpeedKmh!.Value, 0);
        Assert.Equal(10, fix.SatellitesInView.Count);
        Assert.Equal(0, reader.DroppedSentences);
    }
}

public class DmxTests
{
    [Fact]
    public void ArtDmx_roundtrip_and_port_address()
    {
        var buf = new byte[600];
        var pa = ArtNetPacket.PortAddress(net: 1, subNet: 2, universe: 3);
        Assert.Equal(0x0123, pa);
        var n = ArtNetPacket.WriteDmx(buf, pa, [255, 128, 0], sequence: 9);
        Assert.Equal(18 + 4, n); // padded to even length
        Assert.True(ArtNetPacket.TryReadDmx(buf.AsSpan(0, n), out var u, out var seq, out var data));
        Assert.Equal((pa, (byte)9), (u, seq));
        Assert.Equal(new byte[] { 255, 128, 0, 0 }, data.ToArray());
    }

    [Fact]
    public void Sacn_roundtrip_multicast_and_sequence_rules()
    {
        var buf = new byte[700];
        var slots = Enumerable.Range(0, 512).Select(i => (byte)i).ToArray();
        var n = SacnPacket.WriteData(buf, Guid.NewGuid(), "IoTCom", 7, slots, sequence: 42, priority: 150);
        Assert.Equal(638, n);
        Assert.True(SacnPacket.TryReadData(buf.AsSpan(0, n), out var u, out var seq, out var prio, out var name, out var data, out var term));
        Assert.Equal((7, (byte)42, (byte)150, "IoTCom", false), (u, seq, prio, name, term));
        Assert.Equal(slots, data.ToArray());
        Assert.Equal(IPAddress.Parse("239.255.0.7"), SacnPacket.MulticastAddress(7));
        Assert.True(SacnPacket.IsOutOfOrder(100, 95));
        Assert.False(SacnPacket.IsOutOfOrder(255, 1));
        Assert.False(SacnPacket.IsOutOfOrder(100, 50)); // large jump backwards = restart, accepted
    }

    [Fact]
    public async Task ArtNet_nodes_exchange_dmx_and_discover_each_other()
    {
        await using var receiver = ArtNetNode.Create(o => o.Bind(IPAddress.Loopback, 0).WithName("Receiver"));
        await receiver.StartAsync();
        await using var sender = ArtNetNode.Create(o => o.Bind(IPAddress.Loopback, 0).WithName("Console").SendTo(receiver.LocalEndPoint!));
        await sender.StartAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var frames = receiver.ReceiveAsync(universe: 1, ct: cts.Token).GetAsyncEnumerator(cts.Token);
        var next = frames.MoveNextAsync();
        await Task.Delay(50);
        await sender.SendDmxAsync(1, new byte[] { 10, 20, 30 });
        Assert.True(await next);
        Assert.Equal(new byte[] { 10, 20, 30, 0 }, frames.Current.Data.ToArray());
        await frames.DisposeAsync();

        var discovered = new TaskCompletionSource<ArtNetNodeInfo>();
        sender.NodeDiscovered += i => discovered.TrySetResult(i);
        await sender.PollAsync();
        var info = await discovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Receiver", info.ShortName);
    }

    [Fact]
    public async Task Sacn_unicast_between_nodes()
    {
        await using var receiver = SacnNode.Create(o => o.Bind(IPAddress.Loopback, 0));
        await receiver.StartAsync();
        await using var source = SacnNode.Create(o => o.Bind(IPAddress.Loopback, 0).WithName("Desk").SendTo(receiver.LocalEndPoint!));
        await source.StartAsync();
        var got = new TaskCompletionSource<DmxFrame>();
        receiver.DmxReceived += f => got.TrySetResult(f);
        await source.SendDmxAsync(3, new byte[] { 1, 2, 3 });
        var frame = await got.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, frame.Universe);
        Assert.Equal(new byte[] { 1, 2, 3 }, frame.Data.ToArray());
        Assert.StartsWith("Desk", frame.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void Universe_crossfade()
    {
        var u = new DmxUniverse(1);
        var from = u.Snapshot();
        var to = new byte[512];
        to[0] = 200;
        u.Crossfade(from, to, 0.5);
        Assert.Equal(100, u[1]);
    }
}

public class SenMLTests
{
    [Fact]
    public void Json_matches_rfc_example_and_resolves()
    {
        // RFC 8428 §5.1.2
        var json = """[{"bn":"urn:dev:ow:10e2073a01080063:","bt":1.320067464e+09,"bu":"%RH","v":20},{"u":"lon","v":24.30621},{"u":"lat","v":60.07965},{"t":60,"v":20.3},{"u":"lon","t":60,"v":24.30622},{"u":"lat","t":60,"v":60.07965},{"t":120,"v":20.7},{"u":"lon","t":120,"v":24.30623},{"u":"lat","t":120,"v":60.07966},{"u":"%EL","t":150,"v":98},{"t":180,"v":21.2},{"u":"lon","t":180,"v":24.30628},{"u":"lat","t":180,"v":60.07967}]"""u8;
        var pack = SenMLCodec.ParseJson(json);
        Assert.Equal(13, pack.Count);
        var resolved = SenMLCodec.Resolve(pack);
        Assert.All(resolved, r => Assert.Equal("urn:dev:ow:10e2073a01080063:", r.Name));
        Assert.Equal("%RH", resolved[0].Unit);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1320067464 + 180), resolved[^1].Time);
    }

    [Fact]
    public void Cbor_and_json_roundtrip_through_builder()
    {
        var now = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        var pack = new SenMLPackBuilder("urn:dev:mac:0024befffe804ff1/").At(now)
            .Add("temperature", 23.5, "Cel").Add("door", true).Add("label", "line-1").Build();
        foreach (var decoded in new[] { SenMLCodec.ParseJson(SenMLCodec.ToJson(pack)), SenMLCodec.ParseCbor(SenMLCodec.ToCbor(pack)) })
        {
            var r = SenMLCodec.Resolve(decoded);
            Assert.Equal("urn:dev:mac:0024befffe804ff1/temperature", r[0].Name);
            Assert.Equal(23.5, r[0].Value);
            Assert.Equal(now, r[0].Time);
            Assert.True(r[1].BoolValue);
            Assert.Equal("line-1", r[2].StringValue);
        }
    }

    [Fact]
    public void Invalid_payload_throws_protocol_exception() => Assert.Throws<ProtocolException>(() => SenMLCodec.ParseJson("{\"n\":1}"u8));
}

public class MqttTests
{
    [Fact]
    public async Task Publish_subscribe_through_embedded_broker()
    {
        var port = FreePort();
        await using var broker = MqttBroker.Create(port);
        await broker.StartAsync();
        await using var pub = MqttEndpoint.Create(o => o.UseBroker("127.0.0.1", port).WithClientId("pub"));
        await using var sub = MqttEndpoint.Create(o => o.UseBroker("127.0.0.1", port).WithClientId("sub"));
        await sub.ConnectAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var stream = sub.SubscribeStringAsync("plant/+/temp", cts.Token).GetAsyncEnumerator(cts.Token);
        var next = stream.MoveNextAsync();
        await Task.Delay(300); // let the SUBSCRIBE reach the broker
        await pub.PublishStringAsync("plant/line1/temp", "23.5", new PublishOptions { QualityOfService = QualityOfService.AtLeastOnce });
        await pub.PublishStringAsync("plant/line1/pressure", "ignored");
        Assert.True(await next);
        Assert.Equal(("plant/line1/temp", "23.5"), (stream.Current.Topic, stream.Current.Payload));
        await stream.DisposeAsync();
        Assert.Equal(EndpointState.Connected, pub.State);
    }

    internal static int FreePort()
    {
        using var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }
}

public class HostingTests
{
    [Fact]
    public async Task Host_starts_endpoints_and_reports_health()
    {
        var listener = new InMemoryTransportListener();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddIoTCom(iot => iot
            .AddModbusServer("sim", o => o.ListenInMemory(listener))
            .AddModbusClient("plc", o => o.UseInMemory(listener))
            .AddHealthChecks());
        using var host = builder.Build();
        await host.StartAsync();

        var endpoints = host.Services.GetRequiredService<IoTComEndpoints>();
        var sim = endpoints.GetRequired<ModbusServer>("sim");
        sim.Store.HoldingRegisters[0] = 99;
        var plc = host.Services.GetRequiredKeyedService<ModbusClient>("plc");
        Assert.Equal(99, (await plc.ReadHoldingRegistersAsync(0, 1))[0]);
        Assert.Equal(EndpointState.Connected, endpoints.States()["plc"]);
        Assert.NotEmpty(endpoints.Tap.Snapshot());

        var report = await host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync();
        Assert.Equal(HealthStatus.Healthy, report.Status);
        Assert.Equal(2, report.Entries.Count);
        await host.StopAsync();
    }

    [Fact]
    public void Credits_are_exposed() => Assert.Contains("Kang Fadhil", IoTComInfo.CreditEn, StringComparison.Ordinal);
}
