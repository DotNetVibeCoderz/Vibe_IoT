using System.Collections.Concurrent;
using System.Text;
using IoTCom.Net.Protocols.J1939;
using IoTCom.Net.Transport.Can;

namespace IoTCom.Net.Tests.Automotive;

public class J1939CodecTests
{
    [Theory]
    [InlineData(0x0CF00400u, 3, 0xF004u, 0xFF, 0x00)]   // EEC1 from the engine
    [InlineData(0x18FEF100u, 6, 0xFEF1u, 0xFF, 0x00)]   // CCVS1
    [InlineData(0x18EA00F9u, 6, 0xEA00u, 0x00, 0xF9)]   // request from a service tool to the engine
    [InlineData(0x1CECFF00u, 7, 0xEC00u, 0xFF, 0x00)]   // TP.CM broadcast
    [InlineData(0x18EEFF00u, 6, 0xEE00u, 0xFF, 0x00)]   // address claimed
    [InlineData(0x1DF00403u, 7, 0x1F004u, 0xFF, 0x03)]  // data page 1
    public void Identifiers_split_and_rebuild(uint canId, byte priority, uint pgn, byte destination, byte source)
    {
        var id = J1939Id.FromCanId(canId);
        Assert.Equal(new J1939Id(priority, pgn, destination, source), id);
        Assert.Equal(canId, id.ToCanId());
    }

    [Fact]
    public void Names_spns_and_dm1()
    {
        var name = new J1939Name(0x0A2B3, 0x146, 0, 0, 0, 0, 0, 1, false);
        Assert.Equal(name, J1939Name.Parse(name.Encode()));
        Assert.Equal(0x1000_0000_28C0_A2B3UL, name.Value);   // identity | manufacturer << 21 | industry group 1 << 60

        var eec1 = new byte[] { 0xF1, 0x7D, 0x8C, 0xE0, 0x2E, 0xFF, 0xFF, 0xFF };   // 1500 rpm, demand 0 %, actual 15 %
        var values = J1939Spn.Decode(Pgn.Eec1, eec1);
        Assert.Equal(1500, values.Single(v => v.Spn == 190).Value);
        Assert.Equal(15, values.Single(v => v.Spn == 513).Value);
        Assert.Null(values.Single(v => v.Spn == 1483).Value);                         // 0xFF = not available
        Assert.Equal(88, J1939Spn.Decode(Pgn.Ccvs1, [0xFF, 0x00, 0x58, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]).Single().Value);
        Assert.Equal(90, J1939Spn.Decode(Pgn.Et1, [130, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF])[0].Value);

        var dm1 = new J1939Dm1(0, 0, 1, 0, [new J1939Dtc(100, 1, 1)]);
        Assert.Equal("04FF640001 01FFFF".Replace(" ", ""), Convert.ToHexString(dm1.Encode()));
        Assert.Equal(dm1.Dtcs, J1939Dm1.Parse(dm1.Encode()).Dtcs);
        Assert.Empty(J1939Dm1.Parse(new J1939Dm1(0, 0, 0, 0, []).Encode()).Dtcs);
        var wide = new J1939Dtc(520192, 31, 126);                                       // 19-bit SPN
        Assert.Equal("00F0FF7E", Convert.ToHexString(wide.Encode()));
        Assert.Equal(wide, J1939Dtc.Parse(wide.Encode()));

        var bam = new TpConnectionMessage(TpControl.Broadcast, 18, 3, 0, 0, 0, Pgn.VehicleIdentification);
        Assert.Equal("201200 03FFECFE00".Replace(" ", ""), Convert.ToHexString(bam.Encode()));
        Assert.Equal(bam, TpConnectionMessage.Parse(bam.Encode()));
        var fields = J1939Spn.Describe(new J1939Id(3, Pgn.Eec1, 0xFF, 0).Frame(eec1));
        Assert.Contains(fields, f => f.Name == "SPNs" && f.Value!.Contains("1500 rpm", StringComparison.Ordinal));
    }
}

public class J1939SessionTests
{
    private static async Task Until(Func<bool> condition, string what)
    {
        for (var i = 0; i < 300 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition(), $"timed out waiting for {what}");
    }

    [Fact]
    public async Task Engine_broadcasts_answers_requests_and_reports_an_oil_leak()
    {
        var net = new VirtualCanNetwork();
        await using var engine = J1939EngineSimulator.Create(net.CreateNode());
        await using var tool = J1939Node.Create(net.CreateNode(), o => o.Address = 0xF9);
        var messages = new ConcurrentQueue<J1939Message>();
        tool.MessageReceived += messages.Enqueue;
        await tool.StartAsync();
        await engine.StartAsync();
        await Until(() => tool.Claims.ContainsKey(0x00), "engine address claim");
        Assert.Equal(0u, tool.Claims[0x00].Function);

        engine.Throttle = 60;
        await Until(() => messages.Any(m => m.Pgn == Pgn.Eec1 && m.Values.Single(v => v.Spn == 190).Value > 900), "engine speed rising");

        var vin = await tool.RequestAsync(Pgn.VehicleIdentification, 0x00);             // destination specific → RTS/CTS
        Assert.Equal("IOTJ1939SIMTRUCK1*", Encoding.ASCII.GetString(vin.Data));
        var ci = await tool.RequestAsync(Pgn.ComponentIdentification);                   // global → BAM
        Assert.StartsWith("IOTCOM*D13 SIM*", Encoding.ASCII.GetString(ci.Data), StringComparison.Ordinal);
        var hours = await tool.RequestAsync(Pgn.Hours, 0x00);
        Assert.InRange(hours.Values.Single(v => v.Spn == 247).Value!.Value, 12_843, 12_844);

        engine.OilLeak();
        for (var i = 0; i < 100; i++) engine.Step(0.5);
        await Until(() => messages.Any(m => m.Pgn == Pgn.Dm1 && J1939Dm1.Parse(m.Data).Dtcs.Any(d => d.Spn == 100 && d.Fmi == 1)), "DM1 SPN 100 FMI 1");
        Assert.Equal(1, J1939Dm1.Parse(messages.Last(m => m.Pgn == Pgn.Dm1).Data).AmberWarningLamp);
    }

    [Fact]
    public async Task Large_messages_cross_with_rts_cts_and_address_conflicts_resolve()
    {
        var net = new VirtualCanNetwork();
        await using var a = J1939Node.Create(net.CreateNode(), o => { o.Address = 0x80; o.Name = new J1939Name(1, 1, 0, 0, 30, 0, 0, 1, true); });
        await using var b = J1939Node.Create(net.CreateNode(), o => { o.Address = 0x81; o.Name = new J1939Name(2, 1, 0, 0, 30, 0, 0, 1, true); });
        var got = new TaskCompletionSource<J1939Message>();
        b.MessageReceived += m => { if (m.Pgn == 0xEF00) got.TrySetResult(m); };
        await a.StartAsync();
        await b.StartAsync();
        var payload = Enumerable.Range(0, 1785).Select(i => (byte)(i * 7)).ToArray();
        await a.SendAsync(0xEF00, payload, destination: 0x81);                         // proprietary A, 255 packets
        var m = await got.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(payload, m.Data);
        Assert.Equal((byte)0x80, m.Source);

        // A third node with a higher NAME claims 0x80: it loses and moves to a free address.
        await using var c = J1939Node.Create(net.CreateNode(), o => { o.Address = 0x80; o.Name = new J1939Name(9, 1, 0, 0, 30, 0, 0, 1, true); });
        await c.StartAsync();
        await Until(() => c.Address != 0x80, "lost claim");
        Assert.NotEqual(J1939Id.Null, c.Address);
        Assert.Equal((byte)0x80, a.Address);

        await using var spy = J1939Node.Create(net.CreateNode(), o => o.ReadOnly = true);
        await spy.StartAsync();
        await Assert.ThrowsAsync<ReadOnlyModeException>(() => spy.SendAsync(0xEF00, new byte[] { 1 }, 0x80));
        await Assert.ThrowsAsync<IoTComTimeoutException>(() => spy.RequestAsync(Pgn.VehicleIdentification, 0x33, TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task Listen_only_nodes_follow_transfers_between_others_and_send_nothing()
    {
        var net = new VirtualCanNetwork();
        await using var engine = J1939EngineSimulator.Create(net.CreateNode());
        await using var tool = J1939Node.Create(net.CreateNode());
        await using var listener = J1939Node.Create(net.CreateNode(), o => { o.ListenOnly = true; o.Address = 0x55; });
        var vinSeen = new TaskCompletionSource<J1939Message>();
        listener.MessageReceived += m => { if (m.Pgn == Pgn.VehicleIdentification) vinSeen.TrySetResult(m); };
        await listener.StartAsync();
        await tool.StartAsync();
        await engine.StartAsync();

        await tool.RequestAsync(Pgn.VehicleIdentification, 0x00);                        // RTS/CTS between the engine and the tool
        var seen = await vinSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("IOTJ1939SIMTRUCK1*", Encoding.ASCII.GetString(seen.Data));
        Assert.False(tool.Claims.ContainsKey(0x55));                                     // never claimed an address
        await Assert.ThrowsAsync<ReadOnlyModeException>(() => listener.RequestAsync(Pgn.Hours));
    }
}

public class J1939ConformanceTests
{
    public static IEnumerable<object[]> Vectors => Conformance.Cases("j1939.json");

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Shared_vectors(string json)
    {
        var v = Conformance.Parse(json);
        var fields = v.GetProperty("fields").GetString();
        switch (v.GetProperty("kind").GetString())
        {
            case "id":
                var canId = v.GetProperty("can_id").GetUInt32();
                var id = J1939Id.FromCanId(canId);
                Assert.Equal(fields, $"{id.Priority}|{id.Pgn}|{id.Destination}|{id.Source}");
                Assert.Equal(canId, id.ToCanId());
                break;
            case "name":
                var n = J1939Name.Parse(v.Hex("data"));
                Assert.Equal(fields, $"{n.IdentityNumber}|{n.ManufacturerCode}|{n.EcuInstance}|{n.FunctionInstance}|{n.Function}|{n.VehicleSystem}|{n.VehicleSystemInstance}|{n.IndustryGroup}|{(n.ArbitraryAddressCapable ? 1 : 0)}");
                Assert.Equal(v.Hex("data"), n.Encode());
                break;
            case "dtc":
                var d = J1939Dtc.Parse(v.Hex("data"));
                Assert.Equal(fields, $"{d.Spn}|{d.Fmi}|{d.OccurrenceCount}");
                Assert.Equal(v.Hex("data"), d.Encode());
                break;
            case "tp":
                var t = TpConnectionMessage.Parse(v.Hex("data"));
                Assert.Equal(fields, $"{t.Control}|{t.Size}|{t.Packets}|{t.NextPacket}|{t.MaxPerCts}|{t.AbortReason}|{t.Pgn}");
                Assert.Equal(v.Hex("data"), t.Encode());
                break;
            case "spn":
                var values = J1939Spn.Decode(v.GetProperty("pgn").GetUInt32(), v.Hex("data"));
                Assert.Equal(fields, string.Join("|", values.Select(x => $"{x.Spn}={(x.Value is { } val ? val.ToString(System.Globalization.CultureInfo.InvariantCulture) : "na")}")));
                break;
        }
    }
}
