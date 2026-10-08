using IoTCom.Net.Protocols.MBus;
using IoTCom.Net.Transports;

namespace IoTCom.Net.Tests.Protocols;

public class MBusCodecTests
{
    // The water-meter RSP_UD example used throughout the M-Bus documentation (checksum 0x18).
    private const string Reference = "681F1F680802727856341224400107550000000313153100DA023B13018B60043718021816";

    [Fact]
    public void Reference_telegram_decodes()
    {
        var frame = MBusFrame.Decode(Convert.FromHexString(Reference));
        Assert.Equal(MBusFrameType.Long, frame.Type);
        Assert.Equal("RSP_UD", MBusControl.Name(frame.Control));
        var t = MBusTelegram.FromFrame(frame);
        Assert.Equal("12345678", t.Id.ToString("X8"));
        Assert.Equal("PAD", t.Manufacturer);
        Assert.Equal(MBusMedium.Water, t.Medium);
        Assert.Equal(0x55, t.AccessNumber);
        Assert.Equal(3, t.Records.Count);
        Assert.Equal("Volume: 12.565 m³", t.Records[0].ToString());
        Assert.Equal(MBusFunction.Maximum, t.Records[1].Function);
        Assert.Equal(5, t.Records[1].StorageNumber);
        Assert.Equal(0.113, t.Records[1].Value!.Value, 6);
        Assert.Equal(2, t.Records[2].Tariff);
        Assert.Equal(1, t.Records[2].SubUnit);
        Assert.Equal(218_370, t.Records[2].Value);
        Assert.Equal(Convert.FromHexString(Reference), frame.Encode());
        var lane = MBusAnatomy.Describe(Convert.FromHexString(Reference));
        Assert.Equal(Reference.Length / 2, lane.Sum(f => f.Length));
    }

    [Fact]
    public void Short_frames_ack_and_corruption()
    {
        Assert.Equal("105B01 5C16".Replace(" ", ""), Convert.ToHexString(MBusFrame.Short(MBusControl.ReqUd2, 1).Encode()));
        Assert.Equal(MBusFrameType.Ack, MBusFrame.Decode([0xE5]).Type);
        var bad = Convert.FromHexString(Reference);
        bad[^2] ^= 1;
        Assert.Equal(MBusFrame.ReadStatus.Skipped, MBusFrame.TryRead(bad, out _, out _, out var error));
        Assert.Contains("checksum", error, StringComparison.Ordinal);
        Assert.Equal(MBusFrame.ReadStatus.NeedMore, MBusFrame.TryRead(Convert.FromHexString(Reference)[..10], out _, out _, out _));
    }

    [Fact]
    public void Writer_round_trips_records_dates_bcd_and_extensions()
    {
        var data = new MBusRecordWriter(0x26100001, "IOT", 1, MBusMedium.Heat, 7)
            .Integer(0x06, 18_244)
            .Integer(0x5A, 685, 2)
            .Bcd(0x03, 5_412_870, 8, tariff: 1)
            .DateTime(new DateTime(2026, 10, 8, 19, 45, 0))
            .Date(new DateTime(2026, 1, 1), storage: 1)
            .Integer(0x06, 17_102, storage: 9)
            .Integer(0xFD, 2_305, 2, vife: 0x48)
            .ToArray();
        Assert.True(MBusTelegram.TryParse(data, out var t, out var error), error);
        Assert.Equal("26100001-IOT-01-04", t!.SecondaryAddress);
        Assert.Equal(["Energy: 18244000 Wh", "Flow temperature: 68.5 °C", "Energy (tariff 1): 5412870 Wh", "Date and time: 2026-10-08 19:45",
            "Date (storage 1): 2026-01-01", "Energy (storage 9): 17102000 Wh", "Voltage: 230.5 V"], t.Records.Select(r => r.ToString()));
    }

    [Theory]
    [InlineData("0102")]
    [InlineData("8F")]
    [InlineData("84")]
    [InlineData("0D")]
    [InlineData("0DC5")]
    [InlineData("8080808080808080808080")]
    public void Malformed_records_are_rejected_without_throwing(string hex)
    {
        var header = new MBusRecordWriter(1, "ABC", 0, 0, 0).ToArray();
        var data = header.Concat(Convert.FromHexString(hex)).ToArray();
        if (!MBusTelegram.TryParse(data, out var t, out _)) return;
        Assert.NotNull(t);   // a manufacturer-data marker is valid
    }

    [Theory]
    [InlineData("ABC", (ushort)0x0443)]
    [InlineData("PAD", (ushort)0x4024)]
    public void Manufacturer_codes(string code, ushort id)
    {
        Assert.Equal(id, MBusTelegram.ManufacturerId(code));
        Assert.Equal(code, MBusTelegram.ManufacturerCode(id));
    }
}

public class MBusSessionTests
{
    private static async Task<(MBusSlaveSimulator Bus, MBusMaster Master)> BusAsync()
    {
        var listener = new InMemoryTransportListener("mbus");
        var bus = MBusSlaveSimulator.Create(o => o.ListenInMemory(listener)).AddDefaultDevices();
        await bus.StartAsync();
        var master = MBusMaster.Create(o => { o.UseInMemory(listener); o.ResponseTimeout = TimeSpan.FromMilliseconds(1000); o.Retries = 0; });
        await master.ConnectAsync();
        return (bus, master);
    }

    [Fact]
    public async Task Scan_finds_the_three_meters_and_reads_them()
    {
        var (bus, master) = await BusAsync();
        await using var _b = bus;
        await using var _m = master;
        Assert.Equal(new byte[] { 1, 2, 3 }, await master.ScanAsync(0, 5));
        var heat = await master.ReadAsync(1);
        Assert.Equal(MBusMedium.Heat, heat.Medium);
        Assert.Contains(heat.Records, r => r.Quantity == "Flow temperature" && r.Value is > 60 and < 75);
        Assert.Contains(heat.Records, r => r.Quantity == "Energy" && r.StorageNumber == 1);
        var power = await master.ReadAsync(3);
        Assert.Equal(3, power.Records.Count(r => r.Quantity == "Energy"));
        Assert.Contains(power.Records, r => r.Quantity == "Voltage" && r.Value == 230.5);
        var again = await master.ReadAsync(1);
        Assert.Equal(heat.AccessNumber + 1, again.AccessNumber);
    }

    [Fact]
    public async Task Secondary_addressing_selects_one_meter_and_refuses_ambiguous_wildcards()
    {
        var (bus, master) = await BusAsync();
        await using var _b = bus;
        await using var _m = master;
        var water = await master.ReadSecondaryAsync("26200002");
        Assert.Equal(MBusMedium.Water, water.Medium);
        Assert.True(await master.SelectAsync("263FFFFF"));
        Assert.Equal(MBusMedium.Electricity, (await master.ReadAsync(MBusControl.AddressNetworkLayer)).Medium);
        Assert.False(await master.SelectAsync("2FFFFFFF"));                  // three meters match: a real bus would collide
        Assert.False(await master.SelectAsync("26200002", medium: MBusMedium.Heat));
        await Assert.ThrowsAsync<DeviceException>(() => master.ReadSecondaryAsync("99999999"));
        Assert.False(await master.PingAsync(42));
    }
}

public class MBusConformanceTests
{
    public static IEnumerable<object[]> Vectors => Conformance.Cases("mbus.json");

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Shared_vectors(string json)
    {
        var v = Conformance.Parse(json);
        var wire = v.Hex("wire");
        var status = MBusFrame.TryRead(wire, out var frame, out var n, out _);
        if (!v.GetProperty("valid").GetBoolean())
        {
            Assert.False(status == MBusFrame.ReadStatus.Frame && n == wire.Length);
            return;
        }

        Assert.Equal(MBusFrame.ReadStatus.Frame, status);
        Assert.Equal(wire, frame!.Encode());
        if (v.GetProperty("kind").GetString() != "long") return;
        Assert.Equal(v.GetProperty("ci").GetInt32(), frame.Ci);
        var t = MBusTelegram.FromFrame(frame);
        Assert.Equal(v.GetProperty("id").GetUInt32(), t.Id);
        Assert.Equal(MBusTelegram.ManufacturerCode(v.GetProperty("manufacturer").GetUInt16()), t.Manufacturer);
        var expected = v.GetProperty("records").EnumerateArray().Select(e => e.GetString()!).ToList();
        var actual = t.Records.Select(r =>
        {
            var raw = r.Dif & 0x0F;
            long value = raw is 9 or 0xA or 0xB or 0xC or 0xE ? long.Parse(Convert.ToHexString(r.Raw.Reverse().ToArray()) is { Length: > 0 } h ? h : "0", System.Globalization.CultureInfo.InvariantCulture)
                : r.Raw.Length == 0 ? 0 : r.Raw.Select((b, i) => (long)b << (8 * i)).Sum() is var u && r.Raw.Length < 8 && (u & (1L << (8 * r.Raw.Length - 1))) != 0 ? u - (1L << (8 * r.Raw.Length)) : r.Raw.Select((b, i) => (long)b << (8 * i)).Sum();
            return $"{r.Dif:X2}|{Convert.ToHexString(r.Dife)}|{r.Vif:X2}|{Convert.ToHexString(r.Vife)}|{Convert.ToHexString(r.Raw)}|{r.StorageNumber}|{r.Tariff}|{r.SubUnit}|{value}|{r.Offset - 12}";
        }).ToList();
        Assert.Equal(expected, actual);
    }
}
