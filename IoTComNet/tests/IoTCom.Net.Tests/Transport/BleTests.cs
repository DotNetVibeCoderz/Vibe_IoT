using System.Text;
using IoTCom.Net.Transport.Ble;

namespace IoTCom.Net.Tests.Transport;

public class BleCodecTests
{
    [Fact]
    public void Short_uuids_expand_on_the_sig_base()
    {
        Assert.Equal(Guid.Parse("00002a37-0000-1000-8000-00805f9b34fb"), BleUuid.Parse("2a37"));
        Assert.Equal(BleUuid.Parse("0x180D"), BleUuid.FromShort(0x180D));
        Assert.Equal((ushort)0x2A19, BleUuid.ToShort(BleUuid.Parse("2A19")));
        Assert.Null(BleUuid.ToShort(VirtualBleNetwork.SmartPlugService));
        Assert.Equal("Heart Rate Measurement", BleUuid.Name(BleUuid.Parse("2a37")));
        Assert.Throws<FormatException>(() => BleUuid.Parse("zz"));
    }

    [Fact]
    public void Heart_rate_measurement_flags_and_rr_intervals()
    {
        // flags 0x16: 8-bit value, contact supported + detected, RR present; 72 bpm; RR 0x0400 (1.000 s), 0x0340 (0.8125 s)
        var hr = GattValue.ParseHeartRate(Convert.FromHexString("1648000440 03".Replace(" ", "")));
        Assert.Equal(72, hr.BeatsPerMinute);
        Assert.True(hr.SensorContact);
        Assert.Equal([1.0, 0.813], hr.RrIntervals);
        var sixteen = GattValue.ParseHeartRate([0x01, 0x2C, 0x01]);   // 16-bit 300 bpm, contact not supported
        Assert.Equal(300, sixteen.BeatsPerMinute);
        Assert.Null(sixteen.SensorContact);
        Assert.Equal(hr.BeatsPerMinute, GattValue.ParseHeartRate(GattValue.EncodeHeartRate(72, true, [1.0])).BeatsPerMinute);
        Assert.Throws<FormatException>(() => GattValue.ParseHeartRate([0x00]));
    }

    [Fact]
    public void Environmental_values_render_with_units()
    {
        Assert.Equal("27.95 °C", GattValue.Describe(BleUuid.Parse("2a6e"), [0xEB, 0x0A]));
        Assert.Equal("71.20 %", GattValue.Describe(BleUuid.Parse("2a6f"), [0xD0, 0x1B]));
        Assert.Equal("1009.2 hPa", GattValue.Describe(BleUuid.Parse("2a6d"), BitConverter.GetBytes(1_009_200u)));
        Assert.Equal("87 %", GattValue.Describe(BleUuid.Parse("2a19"), [87]));
        Assert.Equal("chest", GattValue.Describe(BleUuid.Parse("2a38"), [1]));
        Assert.Equal("0102", GattValue.Describe(Guid.NewGuid(), [1, 2]));
    }

    [Fact]
    public void Advertising_data_round_trip_and_ibeacon()
    {
        // Flags, 16-bit service 0x180D, name "HRM": a typical heart-rate strap advertisement.
        var raw = Convert.FromHexString("020106" + "03030D18" + "040948524D");
        var ad = AdvertisingData.Parse("x", raw, -60);
        Assert.Equal("HRM", ad.Name);
        Assert.Equal([BleUuid.FromShort(0x180D)], ad.Services);
        Assert.Equal(raw, AdvertisingData.Encode(ad));
        Assert.Contains(AdvertisingData.Describe(raw), f => f.Value == "Name");
        Assert.Throws<FormatException>(() => AdvertisingData.Parse("x", [0x05, 0x09, 0x41]));

        // Apple iBeacon (company 0x004C): UUID E2C56DB5-DFFB-48D2-B060-D0F5A71096E0, major 7, minor 1, -59 dBm.
        var beacon = Convert.FromHexString("1AFF4C000215E2C56DB5DFFB48D2B060D0F5A71096E000070001C5");
        var b = AdvertisingData.Parse("b", beacon, -79).IBeacon!;
        Assert.Equal(Guid.Parse("e2c56db5-dffb-48d2-b060-d0f5a71096e0"), b.ProximityUuid);
        Assert.Equal((7, 1, -59), (b.Major, b.Minor, b.MeasuredPower));
        Assert.Equal(beacon[4..], b.Encode());
        Assert.Equal(2.82, AdvertisingData.Parse("b", beacon, -68).EstimatedDistance);   // 9 dB below the 1 m reference
        Assert.Equal(1.0, (ad with { TxPower = -4, Rssi = -45 }).EstimatedDistance);
    }

    [Fact]
    public void Eddystone_url_uid_and_tlm()
    {
        var url = EddystoneFrame.TryParse(Convert.FromHexString("10EB03676F6F676C6507"))!;   // https:// google .com
        Assert.Equal(("URL", "https://google.com", -21), (url.Kind, url.Description, url.TxPower));
        var uid = EddystoneFrame.TryParse(Convert.FromHexString("00E2" + "0102030405060708090A" + "0B0C0D0E0F10"))!;
        Assert.Equal("0102030405060708090A/0B0C0D0E0F10", uid.Description);
        var tlm = EddystoneFrame.TryParse(Convert.FromHexString("2000" + "0BB8" + "1A80" + "00000064" + "00000000"))!;
        Assert.Equal("3000 mV, 26.5 °C, 100 adv", tlm.Description);
        Assert.Null(EddystoneFrame.TryParse([0x99, 0x00]));
    }

    [Fact]
    public void Native_events_are_parsed_from_json()
    {
        var ad = Assert.IsType<BleAdvertisementEvent>(NativeBleAdapter.Parse(Encoding.UTF8.GetBytes(
            "{\"type\":\"advertisement\",\"id\":\"AA:BB\",\"address\":\"AA:BB\",\"name\":\"HRM \\\"Pro\\\"\",\"rssi\":-61,\"services\":[\"0000180d-0000-1000-8000-00805f9b34fb\"]," +
            "\"manufacturer\":{\"76\":\"0215\"},\"service_data\":{\"0000feaa-0000-1000-8000-00805f9b34fb\":\"10eb03\"}}"))).Advertisement;
        Assert.Equal(("AA:BB", "HRM \"Pro\"", -61), (ad.Id, ad.Name, ad.Rssi));
        Assert.Equal(new byte[] { 2, 0x15 }, ad.ManufacturerData[76]);
        var n = Assert.IsType<BleNotificationEvent>(NativeBleAdapter.Parse(Encoding.UTF8.GetBytes(
            "{\"type\":\"notification\",\"id\":\"AA:BB\",\"uuid\":\"00002a37-0000-1000-8000-00805f9b34fb\",\"value\":\"0648\"}")));
        Assert.Equal(new byte[] { 6, 0x48 }, n.Value);
        Assert.Equal(new BleConnectionEvent("AA:BB", false), NativeBleAdapter.Parse(Encoding.UTF8.GetBytes("{\"type\":\"disconnected\",\"id\":\"AA:BB\"}")));
    }
}

public class BleCentralTests
{
    private static (VirtualBleNetwork Net, BleCentral Ble) Setup(bool readOnly = false)
    {
        var net = new VirtualBleNetwork();
        net.AddHeartRateStrap();
        net.AddEnvironmentSensor();
        net.AddBeacon();
        net.AddSmartPlug();
        return (net, BleCentral.Create(o => { o.UseVirtual(net); o.ReadOnly = readOnly; }));
    }

    [Fact]
    public async Task Scans_filters_and_reads()
    {
        var (_, ble) = Setup();
        await using var _ = ble;
        await ble.ConnectAsync();
        var all = await ble.ScanAsync(TimeSpan.FromMilliseconds(500));
        Assert.Equal(4, all.Count);
        Assert.Equal("HRM-Pro 2107", all[0].Name);   // strongest first
        Assert.NotNull(all.Single(a => a.Name == "Dock-07").IBeacon);
        var hrOnly = await ble.ScanAsync(TimeSpan.FromMilliseconds(400), [BleUuid.FromShort(0x180D)]);
        Assert.Equal("HRM-Pro 2107", Assert.Single(hrOnly).Name);

        await using var sensor = await ble.OpenAsync(all.Single(a => a.Name!.StartsWith("Greenhouse", StringComparison.Ordinal)).Id);
        Assert.Contains(sensor.Services, s => s.Name == "Environmental Sensing");
        Assert.Equal("27.95 °C", GattValue.Describe(BleUuid.Parse("2a6e"), await sensor.ReadAsync("2a6e")));
        await Assert.ThrowsAsync<DeviceException>(() => sensor.ReadAsync("2a37"));
        await Assert.ThrowsAsync<DeviceException>(() => ble.OpenAsync(all.Single(a => a.Name == "Dock-07").Id));   // beacons do not connect
    }

    [Fact]
    public async Task Notifications_flow_and_stop_on_disconnect()
    {
        var (net, ble) = Setup();
        await using var _ = ble;
        await ble.ConnectAsync();
        await ble.ScanAsync(TimeSpan.FromMilliseconds(300));
        await using var strap = await ble.OpenAsync("C4:7C:8D:6A:21:0F");
        var sim = new VirtualBleSimulator(net);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var beats = new List<int>();
        var pump = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested && beats.Count < 3)
            {
                sim.Step();
                await Task.Delay(50);
            }
        });
        await foreach (var v in strap.SubscribeAsync(BleUuid.FromShort(0x2A37), cts.Token))
        {
            beats.Add(GattValue.ParseHeartRate(v).BeatsPerMinute);
            if (beats.Count == 3) break;
        }

        await pump;
        Assert.All(beats, b => Assert.InRange(b, 58, 168));

        var lost = Assert.ThrowsAsync<TransportException>(async () =>
        {
            await foreach (var __ in strap.SubscribeAsync(BleUuid.FromShort(0x2A37), cts.Token)) { }
        });
        await Task.Delay(100);
        net.Remove("C4:7C:8D:6A:21:0F");
        await lost;
    }

    [Fact]
    public async Task Writes_respect_read_only_mode()
    {
        var (net, ble) = Setup(readOnly: true);
        await using var _ = ble;
        await ble.ConnectAsync();
        await ble.ScanAsync(TimeSpan.FromMilliseconds(300));
        await using var plug = await ble.OpenAsync("D0:8E:3A:55:10:C2");
        await Assert.ThrowsAsync<ReadOnlyModeException>(() => plug.WriteAsync(VirtualBleNetwork.SmartPlugRelay, new byte[] { 1 }));

        await using var writer = BleCentral.Create(o => o.UseVirtual(net));
        await writer.ConnectAsync();
        await writer.ScanAsync(TimeSpan.FromMilliseconds(300));
        await using var plug2 = await writer.OpenAsync("D0:8E:3A:55:10:C2");
        await plug2.WriteAsync(VirtualBleNetwork.SmartPlugRelay, new byte[] { 1 });
        new VirtualBleSimulator(net).Step();
        Assert.InRange(BitConverter.ToUInt16(await plug2.ReadAsync(VirtualBleNetwork.SmartPlugPower)), 360, 380);
        await Assert.ThrowsAsync<DeviceException>(() => plug2.WriteAsync(BleUuid.FromShort(0x2A6E), new byte[] { 1 }));
    }

    [Fact]
    public async Task Native_radio_opens_or_reports_why_not()
    {
        if (!NativeBleAdapter.IsSupported) return;   // native library not built on this machine
        await using var ble = BleCentral.Create(o => o.UseNative());
        try
        {
            await ble.ConnectAsync();
        }
        catch (TransportException ex)
        {
            Assert.Contains("Bluetooth", ex.Message, StringComparison.Ordinal);   // no radio on this machine (CI)
            return;
        }

        Assert.False(string.IsNullOrEmpty(ble.Adapter!.Description));
        await ble.ScanAsync(TimeSpan.FromSeconds(1));
    }
}
