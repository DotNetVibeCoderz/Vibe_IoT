using IoTCom.Net.Transport.Can;
using IoTCom.Net.Transport.Can.Adapters;
using IoTCom.Net.Transport.Usb;

namespace IoTCom.Net.Tests.Automotive;

public class CanAdapterTests
{
    private static readonly GsUsbBitTimingConstants CandleLight = new(0, 48_000_000, 1, 16, 1, 8, 4, 1, 1024, 1);

    [Theory]
    [InlineData(500_000, 6u)]
    [InlineData(1_000_000, 3u)]
    [InlineData(250_000, 12u)]
    [InlineData(125_000, 24u)]
    public void Bit_timing_for_a_48_mhz_candlelight(int bitrate, uint brp)
    {
        var t = GsUsbBitTiming.Calculate(CandleLight, bitrate);
        Assert.Equal(brp, t.Brp);
        Assert.Equal(16u, t.Quanta);
        Assert.Equal(0.875, t.SamplePoint);
        Assert.Equal(bitrate, t.BitrateAt(48_000_000));
        Assert.Equal(t, GsUsbBitTiming.Parse(t.Encode()));
        Assert.Throws<ArgumentException>(() => GsUsbBitTiming.Calculate(CandleLight, 33_333));
    }

    [Fact]
    public void Host_frames_match_the_linux_layout()
    {
        // struct gs_host_frame: echo_id, can_id (SocketCAN flags), can_dlc, channel, flags, reserved, data[8]
        var rx = GsUsbCodec.EncodeFrame(GsUsbCodec.EchoIdRx, CanFrame.Create(0x123, 0x11, 0x22));
        Assert.Equal("FFFFFFFF" + "23010000" + "02000000" + "1122000000000000", Convert.ToHexString(rx));
        var ext = GsUsbCodec.EncodeFrame(7, new CanFrame(0x18DAF110, new byte[] { 2, 0x10, 3 }, CanFrameFlags.Extended), channel: 1);
        Assert.Equal("07000000" + "10F1DA98" + "03010000", Convert.ToHexString(ext.AsSpan(0, 12)));
        var (echo, frame, channel, _) = GsUsbCodec.DecodeFrame(ext);
        Assert.Equal((7u, 0x18DAF110u, true, (byte)1), (echo, frame.Id, frame.IsExtended, channel));
        var rtr = GsUsbCodec.DecodeFrame(Convert.FromHexString("FFFFFFFF" + "23010040" + "04000000" + "0000000000000000")).Frame;
        Assert.True(rtr.IsRemote);
        Assert.Equal(4, rtr.RemoteLength);
        Assert.Throws<FormatException>(() => GsUsbCodec.DecodeFrame(new byte[10]));
        Assert.Equal(CandleLight, GsUsbBitTimingConstants.Parse(CandleLight.Encode()));
    }

    [Fact]
    public async Task Gs_usb_bus_talks_to_a_virtual_network_through_a_virtual_candlelight()
    {
        var network = new VirtualCanNetwork("gsusb-test");
        var usb = new VirtualUsbBus();
        await using var adapter = usb.Add(new VirtualGsUsbDevice(network));
        await using var ecu = network.CreateNode();
        await ecu.ConnectAsync();
        using var ecuRx = ecu.OpenReader();

        await using var bus = new GsUsbCanBus(backend: usb, options: new CanBusOptions { Bitrate = 500_000 });
        await bus.ConnectAsync();
        Assert.Equal(6u, adapter.Timing!.Brp);
        Assert.Equal((2u, 1u, 1), bus.DeviceConfig);
        using var rx = bus.OpenReader();

        await bus.SendAsync(CanFrame.Parse("7DF#0201050000000000"));
        var atEcu = await ecuRx.ReadAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        Assert.Equal(0x7DFu, atEcu.Id);

        await ecu.SendAsync(CanFrame.Parse("7E8#0441057B00000000"));
        var atHost = await rx.ReadAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        Assert.Equal(0x7E8u, atHost.Id);
        Assert.Equal(new byte[] { 4, 0x41, 5, 0x7B, 0, 0, 0, 0 }, atHost.Data.ToArray());
        Assert.Equal(1, bus.FramesReceived);   // the TX echo is not reported as a received frame

        await bus.DisconnectAsync();
        Assert.Equal(0u, adapter.ModeFlags & GsUsbCodec.FlagListenOnly);
    }

    [Fact]
    public async Task Uris_reach_the_adapters()
    {
        CanAdapters.Register();
        Assert.Contains("gsusb", CanBus.RegisteredSchemes);
        await using var g = CanBus.Create("gsusb:1d50:606f:ABC#1");
        Assert.Equal("gsusb:1d50:606f:ABC#1", g.Channel);
        Assert.IsType<GsUsbCanBus>(g);
        await using var p = CanBus.Create("pcan:usb2");
        Assert.IsType<PcanCanBus>(p);
        var bad = Assert.Throws<ArgumentException>(() => CanBus.Create("nope:x"));
        Assert.Contains("gsusb:", bad.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pcan_channels_messages_and_missing_driver()
    {
        Assert.Equal(0x51, Pcan.Channel("usb1"));
        Assert.Equal(0x58, Pcan.Channel("USB8"));
        Assert.Equal(0x509, Pcan.Channel("usb9"));
        Assert.Throws<ArgumentException>(() => Pcan.Channel("pci1"));
        Assert.Equal((ushort)0x001C, Pcan.BitrateCodes[500_000]);

        var image = Pcan.Encode(new CanFrame(0x18DAF110, new byte[] { 1, 2, 3 }, CanFrameFlags.Extended));
        Assert.Equal(16, image.Length);
        Assert.Equal("10F1DA18" + "02" + "03" + "010203", Convert.ToHexString(image.AsSpan(0, 9)));
        var back = Pcan.Decode(image)!.Value;
        Assert.Equal((0x18DAF110u, true, 3), (back.Id, back.IsExtended, back.Data.Length));
        image[4] = 0x80;
        Assert.Null(Pcan.Decode(image));   // status message

        if (Pcan.IsAvailable) return;   // a PEAK driver is installed here: do not touch the hardware
        await using var bus = new PcanCanBus("usb1");
        await Assert.ThrowsAsync<PlatformNotSupportedException>(async () => await bus.ConnectAsync());
    }
}
