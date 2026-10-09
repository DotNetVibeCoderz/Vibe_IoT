using System.Collections.Concurrent;
using IoTCom.Net.Adapters.Mqtt;
using IoTCom.Net.Protocols.Sparkplug;

namespace IoTCom.Net.Tests.Protocols;

public class SparkplugPayloadTests
{
    [Fact]
    public void Encodes_tahu_field_numbers()
    {
        // Payload { timestamp = 1, metrics = [{ name "a", alias 1, datatype Int32, int_value 5 }], seq = 0 }, hand-encoded.
        var p = new SparkplugPayload { Timestamp = 1, Seq = 0, Metrics = [SparkplugMetric.Of("a", SparkplugDataType.Int32, 5, alias: 1)] };
        Assert.Equal("08011209" + "0A0161" + "1001" + "2003" + "5005" + "1800", Convert.ToHexString(p.Encode()));
    }

    [Fact]
    public void Round_trips_every_scalar_type_and_negative_ints()
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_000_000);
        var p = new SparkplugPayload
        {
            Timestamp = SparkplugPayload.Millis(now),
            Seq = 7,
            Uuid = "u",
            Body = [1, 2],
            Metrics =
            [
                SparkplugMetric.Of("i8", SparkplugDataType.Int8, -5),
                SparkplugMetric.Of("i32", SparkplugDataType.Int32, -100000),
                SparkplugMetric.Of("i64", SparkplugDataType.Int64, -9_000_000_000L),
                SparkplugMetric.Of("u64", SparkplugDataType.UInt64, ulong.MaxValue),
                SparkplugMetric.Of("f", SparkplugDataType.Float, 1.5f),
                SparkplugMetric.Of("d", SparkplugDataType.Double, 2.25),
                SparkplugMetric.Of("b", SparkplugDataType.Boolean, true),
                SparkplugMetric.Of("s", SparkplugDataType.String, "Cikarang"),
                SparkplugMetric.Of("t", SparkplugDataType.DateTime, now),
                SparkplugMetric.Of("raw", SparkplugDataType.Bytes, new byte[] { 0xAB }),
                SparkplugMetric.Of("n", SparkplugDataType.Double, null),
            ],
        };
        var back = SparkplugPayload.Decode(p.Encode());
        var m = back.Metrics.ToDictionary(x => x.Name!);
        Assert.Equal(7UL, back.Seq);
        Assert.Equal("u", back.Uuid);
        Assert.Equal(-5L, m["i8"].Value);
        Assert.Equal(-100000L, m["i32"].Value);
        Assert.Equal(-9_000_000_000L, m["i64"].Value);
        Assert.Equal(ulong.MaxValue, m["u64"].Value);
        Assert.Equal(1.5f, m["f"].Value);
        Assert.Equal(2.25, m["d"].Value);
        Assert.Equal(true, m["b"].Value);
        Assert.Equal("Cikarang", m["s"].Value);
        Assert.Equal(now, m["t"].Value);
        Assert.Equal(new byte[] { 0xAB }, m["raw"].Value);
        Assert.True(m["n"].IsNull);
        Assert.Null(m["n"].Value);
    }

    [Fact]
    public void Rejects_garbage_and_parses_topics()
    {
        Assert.Throws<ProtocolException>(() => SparkplugPayload.Decode([0x12, 0x09, 0x0A]));
        Assert.True(SparkplugTopic.TryParse("spBv1.0/Plant/DDATA/Line1/Filler", out var t));
        Assert.Equal(new SparkplugTopic(SparkplugMessageType.DData, "Plant", "Line1", "Filler"), t);
        Assert.Equal("spBv1.0/Plant/DDATA/Line1/Filler", t!.ToString());
        Assert.True(SparkplugTopic.TryParse("spBv1.0/STATE/scada", out var s));
        Assert.Equal(SparkplugMessageType.State, s!.Type);
        Assert.False(SparkplugTopic.TryParse("spBv1.0/Plant/XDATA/Line1", out _));
        Assert.False(SparkplugTopic.TryParse("other/Plant/NDATA/Line1", out _));
        Assert.Equal((true, 5L), SparkplugHost.ParseState("{\"online\":true,\"timestamp\":5}"u8));
    }
}

public class SparkplugSessionTests
{
    private static async Task Until(Func<bool> condition, string what)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition(), $"timed out waiting for {what}");
    }

    [Fact]
    public async Task Host_follows_births_data_commands_and_death()
    {
        var port = SparkplugPorts.Free();
        await using var broker = MqttBroker.Create(port);
        await broker.StartAsync();
        await using var host = SparkplugHost.Create(o => { o.HostId = "scada"; o.Mqtt = m => m.UseBroker("127.0.0.1", port); });
        await host.StartAsync();

        await using var node = SparkplugEdgeNode.Create(o => { o.Group = "Plant"; o.EdgeNode = "Line1"; o.Mqtt = m => m.UseBroker("127.0.0.1", port); });
        var sim = new SparkplugLineSimulator(node).Define();
        var commands = new ConcurrentQueue<SparkplugCommand>();
        node.CommandReceived += (_, c) => commands.Enqueue(c);
        await node.StartAsync();

        await Until(() => host.Find("Plant", "Line1", "Tank7")?.Online == true, "DBIRTH");
        var filler = host.Find("Plant", "Line1", "Filler")!;
        Assert.Equal(SparkplugDataType.Float, filler.Metrics["Speed"].DataType);
        Assert.Equal(0UL, host.Find("Plant", "Line1")!.BdSeq);

        await sim.Step();
        await Until(() => filler.Metrics["Speed"].Value is float f && f > 100, "DDATA by alias");
        Assert.Equal(0, host.RebirthRequests);

        await host.WriteAsync("Plant", "Line1", "Filler", "Running", false);
        await Until(() => commands.Any(c => c.Metric == "Running"), "DCMD");
        await Until(() => filler.Metrics["Running"].Value is false, "DDATA echo");
        await sim.Step();
        await Until(() => filler.Metrics["Speed"].Value is 0f, "stopped filler");

        // Not writable: ignored.
        await host.WriteAsync("Plant", "Line1", "Filler", "Speed", 5f);
        await Task.Delay(200);
        Assert.DoesNotContain(commands, c => c.Metric == "Speed");

        await node.DropConnectionAsync();
        await Until(() => host.Find("Plant", "Line1")?.Online == false, "NDEATH will");
        Assert.False(filler.Online);
        Assert.Equal(1UL, node.BdSeq);
    }

    [Fact]
    public async Task Rebirth_on_request_and_read_only_node_ignores_writes()
    {
        var port = SparkplugPorts.Free();
        await using var broker = MqttBroker.Create(port);
        await broker.StartAsync();
        await using var node = SparkplugEdgeNode.Create(o => { o.EdgeNode = "RO"; o.AcceptWrites = false; o.Mqtt = m => m.UseBroker("127.0.0.1", port); });
        node.Device("Pump").Metric("Enable", SparkplugDataType.Boolean, false, writable: true);
        var births = 0;
        var deviceBirths = 0;
        node.Published += (_, e) =>
        {
            if (e.Topic.Type == SparkplugMessageType.NBirth) Interlocked.Increment(ref births);
            if (e.Topic.Type == SparkplugMessageType.DBirth) Interlocked.Increment(ref deviceBirths);
        };
        await node.StartAsync();
        // The first NBIRTH and DBIRTH must have reached the broker before the host subscribes, or the host simply sees
        // them and never asks for a rebirth (slow runners showed both orders).
        await Until(() => Volatile.Read(ref births) >= 1 && Volatile.Read(ref deviceBirths) >= 1, "first NBIRTH and DBIRTH");
        await Task.Delay(200);

        await using var host = SparkplugHost.Create(o => { o.HostId = "late"; o.Mqtt = m => m.UseBroker("127.0.0.1", port); });
        await host.StartAsync();
        // The host joined after the births: data triggers a rebirth request.
        await node.Devices["Pump"].SetAsync("Enable", false);
        await Until(() => host.Find("Plant", "RO", "Pump")?.Online == true, "rebirth after late join");
        Assert.True(host.RebirthRequests >= 1);
        await Until(() => Volatile.Read(ref births) >= 2, "second NBIRTH");

        await host.WriteAsync("Plant", "RO", "Pump", "Enable", true);
        await Task.Delay(300);
        Assert.Equal(false, node.Devices["Pump"].Metrics.Single().Value);
    }
}

internal static class SparkplugPorts
{
    public static int Free()
    {
        using var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        return ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
    }
}
