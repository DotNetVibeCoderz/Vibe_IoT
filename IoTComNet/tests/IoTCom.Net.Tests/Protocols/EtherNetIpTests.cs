using System.Buffers;
using System.Globalization;
using System.Net;
using IoTCom.Net.Protocols.EtherNetIp;
using IoTCom.Net.Transports;

namespace IoTCom.Net.Tests.Protocols;

public class EtherNetIpCodecTests
{
    [Fact]
    public void Tag_paths_encode_padded_symbols_and_elements()
    {
        var p = CipPath.Tag("Program:Main.Zone_Temps[2]");
        Assert.Equal(["Program:Main", "Zone_Temps"], p.Symbols);
        Assert.Equal(2u, p.Element);
        Assert.Equal("Program:Main.Zone_Temps[2]", p.TagName);
        var bytes = p.Encode();
        Assert.Equal(0, bytes.Length % 2);
        Assert.Equal(p, CipPath.Decode(bytes) with { Symbols = p.Symbols });
        Assert.Equal("class 0x01/instance 1/attribute 7", CipPath.Logical(1, 1, 7).ToString());
    }

    [Fact]
    public void Tag_values_convert_types()
    {
        var v = CipTagValue.Of(CipDataType.Real, 1.5, -2);
        Assert.Equal((2, 1.5, -2.0), (v.Count, v[0], v[1]));
        Assert.Equal("BOOL TRUE", CipTagValue.Of(CipDataType.Bool, 1).ToString());
        Assert.Equal(CipDataType.Dint, CipTagValue.ParseType("dint"));
        Assert.Throws<OverflowException>(() => CipTagValue.Of(CipDataType.Sint, 300));
    }

    [Fact]
    public void Multiple_service_packets_split_back()
    {
        byte[][] parts = [[1, 2, 3], [4], [5, 6]];
        var data = EnipClient.MultipleServicePacket(parts);
        Assert.Equal(parts, EnipClient.SplitMultiple(data));
        Assert.Throws<ProtocolException>(() => EnipClient.SplitMultiple([2, 0, 6, 0]));
    }
}

public class EtherNetIpConformanceTests
{
    public static IEnumerable<object[]> Vectors => Conformance.Cases("enip.json");

    private static string Path(CipPath p)
    {
        var parts = p.Symbols.Select(s => $"symbol:{s}").ToList();
        if (p.Element is { } e) parts.Add($"element:{e}");
        if (p.Class is { } c) parts.Add($"class:{c}");
        if (p.Instance is { } i) parts.Add($"instance:{i}");
        if (p.Attribute is { } a) parts.Add($"attribute:{a}");
        return string.Join(",", parts);
    }

    private static string Describe(string kind, byte[] b)
    {
        try
        {
            switch (kind)
            {
                case "encap":
                    var e = EnipPacket.Decode(b);
                    Assert.Equal(b, e.Encode());
                    var s = $"command=0x{(ushort)e.Command:X4} session=0x{e.Session:X8} status={(uint)e.Status} length={e.Data.Length}";
                    if (e.Command is EnipCommand.SendRRData or EnipCommand.SendUnitData && e.Data.Length > 0)
                    {
                        if (e.Data.Length < 6) return "error";
                        s += " items=" + string.Join(",", EnipPacket.DecodeCpf(e.Data.AsSpan(6)).Select(i => $"{(ushort)i.Type:X4}:{i.Data.Length}"));
                    }

                    return s;
                case "request":
                    var r = CipRequest.Decode(b);
                    Assert.Equal(b, r.Encode());
                    return $"service=0x{(byte)r.Service:X2} path={Path(r.Path)} data={Convert.ToHexString(r.Data)}";
                case "response":
                    var x = CipResponse.Decode(b);
                    Assert.Equal(b, x.Encode());
                    return $"service=0x{(byte)x.Service:X2} status=0x{(byte)x.Status:X2} additional={string.Join(",", x.Additional.Select(a => a.ToString("X4", CultureInfo.InvariantCulture)))} data={Convert.ToHexString(x.Data)}";
                default:
                    var id = EnipIdentity.Decode(b);
                    Assert.Equal(b, id.Encode());
                    return $"vendor={id.VendorId} type={id.DeviceType} product={id.ProductCode} rev={id.RevisionMajor}.{id.RevisionMinor} status=0x{id.Status:X4} serial=0x{id.SerialNumber:X8} name={id.ProductName} state={id.State} address={id.Address}";
            }
        }
        catch (ProtocolException)
        {
            return "error";
        }
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Shared_vectors(string json)
    {
        var v = Conformance.Parse(json);
        Assert.Equal(v.GetProperty("expected").GetString(), Describe(v.GetProperty("kind").GetString()!, v.Hex("hex")));
    }
}

public sealed class EtherNetIpSessionTests : IAsyncLifetime
{
    private readonly InMemoryTransportListener _listener = new("plc");
    private readonly InMemoryDatagramNetwork _net = new();
    private static readonly IPEndPoint DiscoveryAddress = new(IPAddress.Parse("10.70.0.2"), EnipPacket.Port);
    private EnipPlcSimulator _plc = null!;

    public async Task InitializeAsync()
    {
        _plc = EnipPlcSimulator.Create(o => { o.ListenInMemory(_listener); o.UseInMemory(_net, DiscoveryAddress); });
        await _plc.StartAsync();
    }

    public async Task DisposeAsync() => await _plc.DisposeAsync();

    private EnipClient Client(bool writes = false, byte? slot = null) => EnipClient.Create(o =>
    {
        o.UseInMemory(_listener);
        o.Timeout = TimeSpan.FromSeconds(2);
        if (slot is { } s) o.UseSlot(s);
        if (writes) o.AllowWrites();
    });

    [Fact]
    public async Task Session_identity_and_tags_read()
    {
        await using var c = Client();
        await c.ConnectAsync();
        Assert.NotEqual(0u, c.Session);
        Assert.Equal(1, _plc.Adapter.Sessions);
        var id = await c.ReadIdentityAsync();
        Assert.Equal(("IoTCom PackLine-1 PLC", (byte)33, 0x0BADCAFEu), (id.ProductName, id.RevisionMajor, id.SerialNumber));
        Assert.Equal("IoTCom PackLine-1 PLC", (await c.ListIdentityAsync()).ProductName);
        var name = await c.GetAttributeSingleAsync(CipClass.Identity, 1, 7);
        Assert.Equal("IoTCom PackLine-1 PLC", System.Text.Encoding.ASCII.GetString(name, 1, name[0]));
        Assert.Equal(120.0, (await c.ReadTagAsync("Line_Speed")).Value);
        Assert.Equal(172.4, (await c.ReadTagAsync("Zone_Temps[2]")).Value, 1);
        Assert.Equal(4, (await c.ReadTagAsync("Zone_Temps", 4)).Count);
        var e = await Assert.ThrowsAsync<CipException>(() => c.ReadTagAsync("No_Such_Tag"));
        Assert.Equal(CipStatus.PathDestinationUnknown, e.Status);
        e = await Assert.ThrowsAsync<CipException>(() => c.ReadTagAsync("Zone_Temps[3]", 2));
        Assert.Equal(CipStatus.PathSegmentError, e.Status);
    }

    [Fact]
    public async Task Multiple_tags_read_in_one_packet()
    {
        await using var c = Client();
        await c.ConnectAsync();
        var results = await c.ReadTagsAsync(["Count_Good", "Recipe", "Missing", "Motor_Run"]);
        Assert.Equal(18204.0, results[0].Value!.Value);
        Assert.Equal(3.0, results[1].Value!.Value);
        Assert.Equal(CipStatus.PathDestinationUnknown, results[2].Error!.Status);
        Assert.Equal(1.0, results[3].Value!.Value);
    }

    [Fact]
    public async Task Writes_need_the_lock_and_respect_the_tag()
    {
        await using var viewer = Client();
        await viewer.ConnectAsync();
        await Assert.ThrowsAsync<ReadOnlyModeException>(() => viewer.WriteTagAsync("Line_Speed", CipTagValue.Of(CipDataType.Real, 90)));

        await using var c = Client(writes: true);
        await c.ConnectAsync();
        await c.WriteTagAsync("Line_Speed", CipTagValue.Of(CipDataType.Real, 90.5));
        Assert.Equal(90.5, _plc.LineSpeed.Value.Value, 3);
        var e = await Assert.ThrowsAsync<CipException>(() => c.WriteTagAsync("Line_Speed", CipTagValue.Of(CipDataType.Dint, 90)));
        Assert.Equal(CipStatus.ObjectStateConflict, e.Status);
        e = await Assert.ThrowsAsync<CipException>(() => c.WriteTagAsync("Count_Good", CipTagValue.Of(CipDataType.Dint, 0)));
        Assert.Equal(CipStatus.PrivilegeViolation, e.Status);

        await c.SetAttributeSingleAsync(CipClass.Assembly, 150, 3, [1, 2, 3, 4, 5, 6, 7, 8]);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], await c.GetAttributeSingleAsync(CipClass.Assembly, 150, 3));
        e = await Assert.ThrowsAsync<CipException>(() => c.SetAttributeSingleAsync(CipClass.Assembly, 100, 3, new byte[16]));
        Assert.Equal(CipStatus.AttributeNotSettable, e.Status);
        e = await Assert.ThrowsAsync<CipException>(() => c.SetAttributeSingleAsync(CipClass.Identity, 1, 7, [0]));
        Assert.Equal(CipStatus.AttributeNotSettable, e.Status);
    }

    [Fact]
    public async Task Routed_requests_unwrap_unconnected_send()
    {
        await using var c = Client(slot: 0);
        await c.ConnectAsync();
        Assert.Equal(3.0, (await c.ReadTagAsync("Recipe")).Value);
        var input = await c.GetAttributeSingleAsync(CipClass.Assembly, 100, 3);
        Assert.Equal(18204, BitConverter.ToInt32(input, 0));
    }

    [Fact]
    public async Task The_line_counts_and_a_cold_seal_raises_rejects()
    {
        _plc.Step(60);
        Assert.InRange(_plc.CountGood.Value.Value, 18204 + 110, 18204 + 120);
        var rejects = _plc.CountReject.Value.Value;
        _plc.SealSetpoint.Set(150);
        for (var i = 0; i < 10; i++) _plc.Step(30);
        Assert.True(_plc.CountReject.Value.Value - rejects > 50);
    }

    [Fact]
    public async Task List_identity_over_udp()
    {
        await using var probe = _net.Bind();
        var found = await EnipDiscovery.ListIdentityAsync(probe, DiscoveryAddress, TimeSpan.FromMilliseconds(300));
        var id = Assert.Single(found);
        Assert.Equal("IoTCom PackLine-1 PLC", id.ProductName);
        Assert.Equal(DiscoveryAddress, id.Address);
    }

    [Fact]
    public async Task Requests_without_a_session_are_refused()
    {
        var t = _listener.Connect();
        await t.OpenAsync(CancellationToken.None);
        var cip = new CipRequest(CipService.ReadTag, CipPath.Tag("Recipe"), [1, 0]).Encode();
        await t.Pipe.Output.WriteAsync(new EnipPacket { Command = EnipCommand.SendRRData, Session = 0x4242, Data = EnipPacket.SendRRData(cip) }.Encode());
        var reply = EnipPacket.Decode(await ReadAsync(t));
        Assert.Equal(EnipStatus.InvalidSessionHandle, reply.Status);
        await t.DisposeAsync();
    }

    private static async Task<byte[]> ReadAsync(ITransport t)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (true)
        {
            var r = await t.Pipe.Input.ReadAsync(cts.Token);
            if (r.Buffer.Length >= EnipPacket.HeaderSize)
            {
                var head = r.Buffer.Slice(0, EnipPacket.HeaderSize).ToArray();
                var total = EnipPacket.FrameLength(head);
                if (r.Buffer.Length >= total)
                {
                    var frame = r.Buffer.Slice(0, total).ToArray();
                    t.Pipe.Input.AdvanceTo(r.Buffer.GetPosition(total));
                    return frame;
                }
            }

            t.Pipe.Input.AdvanceTo(r.Buffer.Start, r.Buffer.End);
        }
    }
}
