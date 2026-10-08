using System.Text;
using IoTCom.Net.Transport.Usb;

namespace IoTCom.Net.Tests.Transport;

public class UsbTests
{
    private static (VirtualUsbBus Bus, VirtualLoopbackDevice Loop, VirtualHidRelayBoard Relay) Bus()
    {
        var bus = new VirtualUsbBus();
        var loop = bus.Add(new VirtualLoopbackDevice());
        var relay = bus.AddHid(new VirtualHidRelayBoard(relays: 4));
        return (bus, loop, relay);
    }

    [Fact]
    public void Enumeration_names_classes_and_known_devices()
    {
        var (bus, _, _) = Bus();
        var dev = Assert.Single(UsbDevice.List(bus));
        Assert.Equal("1209:0001:LB-0001", dev.Id);
        Assert.Equal("Vendor specific", dev.Interfaces[0].ClassName);
        Assert.Equal("candleLight / gs_usb CAN adapter", UsbIds.Known(0x1D50, 0x606F));
        var hid = Assert.Single(HidDevice.List(bus));
        Assert.Equal("Vendor 0xFF00", hid.UsageName);
        Assert.Equal((ushort)0x16C0, hid.VendorId);
    }

    [Fact]
    public async Task Control_and_bulk_transfers_with_read_only_guard()
    {
        var (bus, _, _) = Bus();
        await using var usb = UsbDevice.Create(o => { o.UseVirtual(bus); o.DeviceId = "1209:0001"; });
        await usb.ConnectAsync();
        Assert.Equal("1.4.2", Encoding.ASCII.GetString(await usb.ControlInAsync(UsbSetup.Vendor(0x01), 16)));
        var descriptor = await usb.ControlInAsync(UsbSetup.GetDescriptor(0x01), 18);
        Assert.Equal((18, 0x1209), (descriptor[0], BitConverter.ToUInt16(descriptor, 8)));
        await Assert.ThrowsAsync<DeviceException>(() => usb.ControlInAsync(UsbSetup.Vendor(0x7F), 4));   // stall

        await usb.WriteAsync(0x01, "ping"u8.ToArray());
        Assert.Equal("ping", Encoding.ASCII.GetString((await usb.ReadAsync(0x81))!));
        await usb.ControlOutAsync(UsbSetup.Vendor(0x02, value: 1));
        await usb.WriteAsync(0x01, "pong"u8.ToArray());
        Assert.Equal("PONG", Encoding.ASCII.GetString((await usb.ReadAsync(0x81))!));
        Assert.Null(await usb.ReadAsync(0x81, timeout: TimeSpan.FromMilliseconds(50)));
        await Assert.ThrowsAsync<ArgumentException>(() => usb.WriteAsync(0x81, new byte[] { 1 }));

        await using var ro = UsbDevice.Create(o => { o.UseVirtual(bus); o.DeviceId = "1209:0001"; o.ReadOnly = true; });
        await ro.ConnectAsync();
        await Assert.ThrowsAsync<ReadOnlyModeException>(() => ro.WriteAsync(0x01, new byte[] { 1 }));
        await Assert.ThrowsAsync<ReadOnlyModeException>(() => ro.ControlOutAsync(UsbSetup.Vendor(0x02)));
    }

    [Fact]
    public async Task Missing_device_and_unplugging_are_reported()
    {
        var (bus, loop, _) = Bus();
        await using var nope = UsbDevice.Create(o => { o.UseVirtual(bus); o.DeviceId = "dead:beef"; });
        await Assert.ThrowsAsync<DeviceException>(async () => await nope.ConnectAsync());
        await using var usb = UsbDevice.Create(o => { o.UseVirtual(bus); o.DeviceId = loop.Info.Id; });
        await usb.ConnectAsync();
        bus.Remove(loop.Info.Id);
        await Assert.ThrowsAsync<TransportException>(() => usb.WriteAsync(0x01, new byte[] { 1 }));
    }

    [Fact]
    public async Task Bulk_endpoints_carry_a_byte_stream_transport()
    {
        var (bus, _, _) = Bus();
        await using var t = new UsbBulkTransport(bus, "1209:0001", 0x81, 0x01, 0);
        await t.OpenAsync(CancellationToken.None);
        await t.Pipe.Output.WriteAsync("hello over usb"u8.ToArray());
        var got = new List<byte>();
        while (got.Count < 14)
        {
            var r = await t.Pipe.Input.ReadAsync();
            got.AddRange(System.Buffers.BuffersExtensions.ToArray(r.Buffer));
            t.Pipe.Input.AdvanceTo(r.Buffer.End);
        }

        Assert.Equal("hello over usb", Encoding.ASCII.GetString(got.ToArray()));
        Assert.Equal(TransportKind.Usb, t.Info.Kind);
    }

    [Fact]
    public async Task Hid_relay_board_protocol_and_read_only_mode()
    {
        var (bus, _, board) = Bus();
        await using var hid = HidDevice.Create(o => { o.UseVirtual(bus); o.UseDevice(HidRelayBoard.VendorId, HidRelayBoard.ProductId); });
        await hid.ConnectAsync();
        var relays = new HidRelayBoard(hid);
        Assert.Equal(4, relays.Relays);
        Assert.Equal("IOTCM", await relays.GetSerialAsync());
        await relays.SetAsync(2, true);
        await relays.SetAsync(4, true);
        var states = await relays.GetStatesAsync();
        Assert.Equal(new[] { false, true, false, true }, states);
        Assert.Equal(0b1010, board.State);
        await relays.SetAllAsync(false);
        Assert.Equal(0, board.State);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => relays.SetAsync(5, true));

        await using var ro = HidDevice.Create(o => { o.UseVirtual(bus); o.UseDevice(HidRelayBoard.VendorId, HidRelayBoard.ProductId); o.ReadOnly = true; });
        await ro.ConnectAsync();
        await Assert.ThrowsAsync<ReadOnlyModeException>(() => new HidRelayBoard(ro).SetAsync(1, true));
        Assert.All(await new HidRelayBoard(ro).GetStatesAsync(), Assert.False);
    }

    [Fact]
    public void Native_json_lists_are_parsed()
    {
        var devices = NativeUsbBackend.ParseDevices(Encoding.UTF8.GetBytes(
            "[{\"id\":\"1d50:606f:0042\",\"vid\":7504,\"pid\":24687,\"manufacturer\":\"bytewerk\",\"product\":\"candleLight USB to CAN adapter\",\"serial\":\"0042\",\"class\":0,\"bus\":\"1\",\"address\":5," +
            "\"interfaces\":[{\"number\":0,\"class\":255,\"subclass\":255,\"protocol\":255,\"name\":null}]}]"));
        var d = Assert.Single(devices);
        Assert.Equal("candleLight / gs_usb CAN adapter", d.Kind);
        Assert.Equal("OpenMoko (open hardware)", d.VendorName);
        var hid = NativeUsbBackend.ParseHid(Encoding.UTF8.GetBytes(
            "[{\"path\":\"/dev/hidraw3\",\"vid\":1133,\"pid\":50479,\"manufacturer\":\"Logitech\",\"product\":\"USB Receiver\",\"serial\":\"\",\"usage_page\":1,\"usage\":2,\"interface\":1}]"));
        Assert.Equal(("Mouse", null), (hid[0].UsageName, hid[0].Serial));
    }

    [Fact]
    public void Native_library_enumerates_when_present()
    {
        if (!NativeUsbBackend.IsSupported) return;   // native library not built on this machine
        try
        {
            var devices = UsbDevice.List();
            Assert.All(devices, d => Assert.Matches("^[0-9a-f]{4}:[0-9a-f]{4}", d.Id));
            _ = HidDevice.List();
        }
        catch (TransportException ex) when (ex.Message.Contains("USB", StringComparison.Ordinal) || ex.Message.Contains("HID", StringComparison.Ordinal))
        {
            // CI containers may have no USB subsystem.
        }
    }
}
