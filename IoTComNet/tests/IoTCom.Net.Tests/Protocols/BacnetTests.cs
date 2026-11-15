using System.Net;
using System.Text.Json;
using IoTCom.Net.Adapters.Bacnet;
using IoTCom.Net.Protocols.Bacnet;
using IoTCom.Net.Transports;

namespace IoTCom.Net.Tests.Protocols;

public class BacnetCodecTests
{
    [Fact]
    public void Object_ids_parse_and_encode()
    {
        Assert.Equal(new BacnetObjectId(BacnetObjectType.AnalogInput, 1), BacnetObjectId.Parse("AI1"));
        Assert.Equal(new BacnetObjectId(BacnetObjectType.BinaryOutput, 3), BacnetObjectId.Parse("binary-output:3"));
        Assert.Equal(new BacnetObjectId(BacnetObjectType.Device, 2604), BacnetObjectId.Parse("device:2604"));
        Assert.Equal(0x0200000Au, BacnetObjectId.Parse("8:10").Raw);       // device 10 = 0x0200000A
        Assert.Equal("MSV1", BacnetObjectId.Parse("msv1").ShortName);
        Assert.Throws<FormatException>(() => BacnetObjectId.Parse("XY1"));
        Assert.Throws<FormatException>(() => BacnetObjectId.Parse("AI4194304"));
    }

    [Fact]
    public void Read_property_round_trips()
    {
        var obj = new BacnetObjectId(BacnetObjectType.AnalogInput, 1);
        var req = BacnetServices.ReadProperty(obj, new(BacnetPropertyId.PresentValue));
        Assert.Equal("0C000000011955", Convert.ToHexString(req));
        Assert.Equal((obj, new BacnetPropertyReference(BacnetPropertyId.PresentValue)), BacnetServices.DecodeReadProperty(req));
        var ack = BacnetServices.ReadPropertyAck(obj, new(BacnetPropertyId.PresentValue), [BacnetValue.Real(21.5f)]);
        Assert.Equal(21.5f, (float)BacnetServices.DecodeReadPropertyAck(ack).Values[0].Value!);
    }

    [Fact]
    public void Priority_array_commands_and_relinquishes()
    {
        var bo = BacnetObject.BinaryOutput(1, "Fan", false);
        bo.Write(new(BacnetPropertyId.PresentValue), [BacnetValue.Enumerated(1)], 8);
        Assert.Equal(1, bo.Value);
        Assert.Equal(8, bo.ActivePriority);
        bo.Write(new(BacnetPropertyId.PresentValue), [BacnetValue.Enumerated(0)], 16);
        Assert.Equal(1, bo.Value);                                  // priority 8 still wins
        bo.Write(new(BacnetPropertyId.PresentValue), [BacnetValue.Null], 8);
        Assert.Equal((0.0, 16), (bo.Value, bo.ActivePriority!.Value));
        var e = Assert.Throws<BacnetErrorException>(() => bo.Write(new(BacnetPropertyId.PresentValue), [BacnetValue.Enumerated(2)], 16));
        Assert.Equal(BacnetErrorCode.ValueOutOfRange, e.ErrorCode);
        e = Assert.Throws<BacnetErrorException>(() => bo.Write(new(BacnetPropertyId.ObjectName), [BacnetValue.Text("x")], null));
        Assert.Equal(BacnetErrorCode.WriteAccessDenied, e.ErrorCode);
        var ai = BacnetObject.AnalogInput(1, "T", BacnetUnits.DegreesCelsius, 20);
        e = Assert.Throws<BacnetErrorException>(() => ai.Write(new(BacnetPropertyId.PresentValue), [BacnetValue.Real(1)], null));
        Assert.Equal(BacnetErrorCode.WriteAccessDenied, e.ErrorCode);
        ai.OutOfService = true;                                     // inputs accept writes while out of service
        ai.Write(new(BacnetPropertyId.PresentValue), [BacnetValue.Real(99)], null);
        Assert.Equal(99, ai.Value);
        Assert.Equal("{F,F,F,T}", ai.StatusFlags.ToString());
    }

    [Fact]
    public void Frame_lane_names_the_service()
    {
        var packet = new BacnetPacket
        {
            PduType = BacnetPduType.ConfirmedRequest, ExpectingReply = true, InvokeId = 7, Service = (byte)BacnetConfirmedService.ReadProperty,
            Data = BacnetServices.ReadProperty(new BacnetObjectId(BacnetObjectType.AnalogInput, 1), new(BacnetPropertyId.PresentValue)),
        }.Encode();
        var fields = BacnetPacket.DescribeFields(packet);
        Assert.Contains(fields, f => f.Name == "APDU" && f.Value == "ReadProperty request #7");
        Assert.Equal("Service data", fields[^1].Name);
        Assert.Equal(packet.Length, fields[^1].Offset + fields[^1].Length);
    }
}

public class BacnetConformanceTests
{
    public static IEnumerable<object[]> Vectors => Conformance.Cases("bacnet.json");

    private static BacnetValue Value(JsonElement v) => v.GetProperty("tag").GetString() switch
    {
        "Null" => BacnetValue.Null,
        "Boolean" => BacnetValue.Boolean(v.GetProperty("value").GetBoolean()),
        "UnsignedInteger" => BacnetValue.Unsigned(v.GetProperty("value").GetUInt32()),
        "SignedInteger" => BacnetValue.Signed(v.GetProperty("value").GetInt32()),
        "Real" => BacnetValue.Real(v.GetProperty("value").GetSingle()),
        "Double" => new BacnetValue(BacnetApplicationTag.Double, v.GetProperty("value").GetDouble()),
        "OctetString" => new BacnetValue(BacnetApplicationTag.OctetString, Convert.FromHexString(v.GetProperty("value").GetString()!)),
        "CharacterString" => BacnetValue.Text(v.GetProperty("value").GetString()!),
        "BitString" => BacnetValue.Bits(new BacnetBitString([.. v.GetProperty("value").GetString()!.Select(c => c == '1')])),
        "Enumerated" => BacnetValue.Enumerated(v.GetProperty("value").GetUInt32()),
        "Date" => new BacnetValue(BacnetApplicationTag.Date, new BacnetDate(v.GetProperty("value")[0].GetInt32(), v.GetProperty("value")[1].GetInt32(), v.GetProperty("value")[2].GetInt32(), v.GetProperty("value")[3].GetInt32())),
        "Time" => new BacnetValue(BacnetApplicationTag.Time, new BacnetTime(v.GetProperty("value")[0].GetInt32(), v.GetProperty("value")[1].GetInt32(), v.GetProperty("value")[2].GetInt32(), v.GetProperty("value")[3].GetInt32())),
        "ObjectIdentifier" => BacnetValue.Object(new BacnetObjectId((BacnetObjectType)v.GetProperty("value")[0].GetInt32(), v.GetProperty("value")[1].GetUInt32())),
        var t => throw new InvalidOperationException(t),
    };

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Shared_vectors(string json)
    {
        var v = Conformance.Parse(json);
        var bytes = v.Hex("hex");
        switch (v.GetProperty("kind").GetString())
        {
            case "value":
            {
                var value = Value(v);
                Assert.Equal(bytes, new BacnetWriter().Application(value).ToArray());
                var r = new BacnetReader(bytes);
                var back = r.Application();
                Assert.True(r.End);
                Assert.Equal(value.Tag, back.Tag);
                Assert.Equal(bytes, new BacnetWriter().Application(back).ToArray());
                break;
            }

            case "packet":
            {
                var expected = v.GetProperty("describe").GetString();
                if (expected == "error")
                {
                    Assert.ThrowsAny<ProtocolException>(() => BacnetPacket.Decode(bytes));
                    return;
                }

                var p = BacnetPacket.Decode(bytes);
                Assert.Equal(expected, p.Describe());
                Assert.Equal(v.GetProperty("data").GetString(), Convert.ToHexString(p.Data));
                if (v.GetProperty("canonical").GetBoolean()) Assert.Equal(bytes, p.Encode());
                break;
            }
        }
    }
}

public sealed class BacnetInteropTests : IAsyncLifetime
{
    // The adapter (ela-compil's BACnet client) against IoTCom's own BacnetDevice on an in-memory network.
    private static readonly IPEndPoint Broadcast = new(IPAddress.Parse("239.255.186.192"), BacnetDeviceOptions.DefaultPort);
    private readonly InMemoryDatagramNetwork _net = new();
    private BacnetAhuSimulator _ahu = null!;
    private BacnetIpClient _client = null!;

    public async Task InitializeAsync()
    {
        _ahu = BacnetAhuSimulator.Create(o =>
        {
            o.UseInMemory(_net, new IPEndPoint(IPAddress.Parse("10.60.0.2"), BacnetDeviceOptions.DefaultPort));
            o.BroadcastAddress = Broadcast;
            o.ApduTimeout = TimeSpan.FromMilliseconds(500);
        });
        await _ahu.StartAsync();
        _client = Client(writes: true);
        await _client.ConnectAsync();
    }

    private BacnetIpClient Client(bool writes, string ip = "10.60.0.20") => BacnetIpClient.Create(o =>
    {
        o.UseInMemory(_net, new IPEndPoint(IPAddress.Parse(ip), BacnetDeviceOptions.DefaultPort));
        o.BroadcastAddress = Broadcast;
        o.Timeout = TimeSpan.FromSeconds(1);
        if (writes) o.AllowWrites();
    });

    public async Task DisposeAsync()
    {
        await _client.DisposeAsync();
        await _ahu.DisposeAsync();
    }

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
    public async Task Who_is_finds_the_device_and_object_list_reads()
    {
        var found = await _client.DiscoverAsync(TimeSpan.FromMilliseconds(300));
        var d = Assert.Single(found);
        Assert.Equal((2604u, 999u, BacnetSegmentation.None), (d.Instance, d.VendorId, d.Segmentation));
        Assert.Empty(await _client.DiscoverAsync(TimeSpan.FromMilliseconds(200), low: 1, high: 10));

        var objects = await _client.ReadObjectListAsync(2604);
        Assert.Equal(14, objects.Count);                     // the device + 13 AHU objects
        Assert.Contains(BacnetObjectId.Parse("MSV1"), objects);
        var name = await _client.ReadPropertyAsync(2604, new(BacnetObjectType.Device, 2604), BacnetPropertyId.ObjectName);
        Assert.Equal("AHU-1 Lantai 3", name[0].Value);
        var count = await _client.ReadPropertyAsync(2604, new(BacnetObjectType.Device, 2604), BacnetPropertyId.ObjectList, arrayIndex: 0);
        Assert.Equal(14u, count[0].Value);
    }

    [Fact]
    public async Task Present_values_and_multiple_properties_read()
    {
        await _client.DiscoverAsync(TimeSpan.FromMilliseconds(300));
        var supply = await _client.ReadPresentValueAsync(2604, BacnetObjectId.Parse("AI1"));
        Assert.Equal(24.0, supply.AsDouble()!.Value, 1);
        var results = await _client.ReadPropertiesAsync(2604, BacnetObjectId.Parse("AI4"), [BacnetPropertyId.ObjectName, BacnetPropertyId.PresentValue, BacnetPropertyId.Units, BacnetPropertyId.StatusFlags, BacnetPropertyId.PriorityArray]);
        Assert.Equal("AHU-1 Return CO2", results[0].Values![0].Value);
        Assert.Equal((uint)BacnetUnits.PartsPerMillion, results[2].Values![0].Value);
        Assert.Equal("{F,F,F,F}", results[3].Values![0].Value!.ToString());
        Assert.False(results[4].Ok);                         // an input has no priority array
        Assert.Equal(BacnetErrorCode.UnknownProperty, results[4].ErrorCode);
        var all = await _client.ReadPropertiesAsync(2604, BacnetObjectId.Parse("MSV1"), [BacnetPropertyId.All]);
        Assert.Contains(all, r => r.Reference.Property == BacnetPropertyId.StateText && r.Values!.Count == 3);
        var e = await Assert.ThrowsAsync<DeviceException>(() => _client.ReadPresentValueAsync(2604, BacnetObjectId.Parse("AI99")));
        Assert.Contains("UNKNOWN_OBJECT", e.Message.ToUpperInvariant().Replace(" ", "_", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Writes_need_the_lock_and_use_priorities()
    {
        await using var viewer = Client(writes: false, ip: "10.60.0.21");
        await viewer.ConnectAsync();
        await viewer.DiscoverAsync(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAsync<ReadOnlyModeException>(() => viewer.WritePropertyAsync(2604, BacnetObjectId.Parse("BO1"), BacnetPropertyId.PresentValue, BacnetValue.Enumerated(0), 8));

        await _client.DiscoverAsync(TimeSpan.FromMilliseconds(300));
        await _client.WritePropertyAsync(2604, BacnetObjectId.Parse("BO1"), BacnetPropertyId.PresentValue, BacnetValue.Enumerated(0), 8);
        Assert.Equal((0.0, 8), (_ahu.FanCommand.Value, _ahu.FanCommand.ActivePriority!.Value));
        var array = await _client.ReadPropertyAsync(2604, BacnetObjectId.Parse("BO1"), BacnetPropertyId.PriorityArray);
        Assert.Equal(16, array.Count);
        Assert.Equal(0u, array[7].Value);
        await _client.WritePropertyAsync(2604, BacnetObjectId.Parse("BO1"), BacnetPropertyId.PresentValue, BacnetValue.Null, 8);
        Assert.Equal(1.0, _ahu.FanCommand.Value);            // back to the relinquish default
        await _client.WritePropertyAsync(2604, BacnetObjectId.Parse("AV1"), BacnetPropertyId.PresentValue, BacnetValue.Real(13.5f), 16);
        Assert.Equal(13.5, _ahu.SupplySetpoint.Value, 2);
        var bad = await Assert.ThrowsAsync<DeviceException>(() => _client.WritePropertyAsync(2604, BacnetObjectId.Parse("MSV1"), BacnetPropertyId.PresentValue, BacnetValue.Unsigned(9), 16));
        Assert.Contains("VALUE_OUT_OF_RANGE", bad.Message.ToUpperInvariant().Replace(" ", "_", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cov_subscriptions_notify_on_significant_change(bool confirmed)
    {
        await _client.DiscoverAsync(TimeSpan.FromMilliseconds(300));
        var seen = new List<double>();
        await using (await _client.SubscribeCovAsync(2604, BacnetObjectId.Parse("AI4"), n => { lock (seen) seen.Add(n.Values[0].Values[0].AsDouble()!.Value); }, confirmed, lifetime: 60))
        {
            await Until(() => { lock (seen) return seen.Count == 1; });   // the initial notification
            _ahu.Co2.Update(650);                                           // below the 25 ppm COV increment
            _ahu.Co2.Update(700);                                           // significant
            await Until(() => { lock (seen) return seen.Count == 2; });
            Assert.Single(_ahu.Device.Subscriptions);
        }

        lock (seen) Assert.Equal([640.0, 700.0], seen);
        await Until(() => _ahu.Device.Subscriptions.Count == 0);
    }

    [Fact]
    public async Task The_simulated_unit_responds_to_commands()
    {
        _ahu.Step(30);
        Assert.True(_ahu.FanStatus.Value == 1);
        Assert.True(_ahu.StaticPressure.Value > 200);
        _ahu.FanCommand.Write(new(BacnetPropertyId.PresentValue), [BacnetValue.Enumerated(0)], 8);
        _ahu.Step(30);
        Assert.Equal(0.0, _ahu.FanStatus.Value);
        _ahu.CoolingValve.Write(new(BacnetPropertyId.PresentValue), [BacnetValue.Real(55)], 8);   // manual override stops the loop
        _ahu.Step(10);
        Assert.Equal(55.0, _ahu.CoolingValve.Value, 1);
    }
}
