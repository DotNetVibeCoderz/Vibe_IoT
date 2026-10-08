using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using IoTCom.Net.Gallery.Infrastructure;
using IoTCom.Net.Transport.Usb;

namespace IoTCom.Net.Gallery.Demos;

/// <summary>
/// A USB bench: the devices plugged into this computer (enumeration only, never opened), a simulated 4-channel USB HID
/// relay board switched through feature reports, and a vendor-class loopback device driven with control and bulk transfers.
/// </summary>
public sealed partial class UsbDemo : GalleryDemo
{
    public override string Id => "usb-bench";
    public override Text Title => new("USB bench", "Meja kerja USB");
    public override Text Summary => new(
        "See what is plugged into this computer, switch a USB HID relay board through feature reports, and talk to a vendor device with control and bulk transfers.",
        "Lihat apa yang tersambung ke komputer ini, nyalakan papan relay USB HID lewat feature report, dan bicara dengan perangkat vendor lewat transfer control dan bulk.");
    public override Text Docs => new(
        "Every USB device describes itself with descriptors: vendor and product ids, strings, and interfaces with a class (HID, CDC, vendor-specific…) and endpoints. The host talks to endpoint 0 with control transfers (an 8-byte SETUP packet: request type, request, value, index, length) and to the other endpoints with bulk or interrupt transfers.\n\nHID devices are reached through the operating system's HID driver with reports: input, output and feature. The cheap USB relay modules sold everywhere are HID devices that switch a relay when they receive a feature report such as FF 02 (relay 2 on).\n\nIoTCom.Net reaches real hardware through a Rust library built on nusb (raw transfers: WinUSB, usbfs, IOKit) and hidapi. This demo lists the real devices without opening them and drives simulated ones on a virtual bus.",
        "Setiap perangkat USB menggambarkan dirinya dengan descriptor: vendor id dan product id, string, serta interface dengan kelas (HID, CDC, vendor-specific…) dan endpoint. Host berbicara ke endpoint 0 dengan transfer control (paket SETUP 8 byte: request type, request, value, index, length) dan ke endpoint lain dengan transfer bulk atau interrupt.\n\nPerangkat HID dijangkau lewat driver HID sistem operasi dengan report: input, output, dan feature. Modul relay USB murah yang dijual di mana-mana adalah perangkat HID yang menyalakan relay saat menerima feature report seperti FF 02 (relay 2 nyala).\n\nIoTCom.Net menjangkau perangkat keras sungguhan lewat pustaka Rust di atas nusb (transfer mentah: WinUSB, usbfs, IOKit) dan hidapi. Demo ini mendaftar perangkat sungguhan tanpa membukanya dan menggerakkan perangkat simulasi di bus virtual.");
    public override string Category => "Workbench";
    public override IReadOnlyList<string> Protocols => ["USB", "HID", "WinUSB"];
    public override Difficulty Difficulty => Difficulty.Beginner;
    public override string DocsPath => "docs/en/protocols/usb.md";

    private VirtualUsbBus? _bus;
    private VirtualHidRelayBoard? _boardSim;
    private HidDevice? _reader;
    private HidDevice? _writer;
    private UsbDevice? _loop;

    /// <summary>Devices on this computer.</summary>
    public ObservableCollection<UsbRow> Local { get; } = [];

    /// <summary>Relay lamps.</summary>
    public ObservableCollection<RelayLamp> Relays { get; } = [];

    /// <summary>Loopback console lines.</summary>
    public ObservableCollection<string> Console { get; } = [];

    [ObservableProperty] private bool _allowWrite;
    [ObservableProperty] private string _lastAction = "";
    [ObservableProperty] private string _localNote = "";
    [ObservableProperty] private string _message = "hello from the gallery";
    [ObservableProperty] private bool _upper;

    private static readonly IReadOnlyList<UsbDeviceInfo> SampleBench =
    [
        new() { Id = "0403:6001", VendorId = 0x0403, ProductId = 0x6001, Manufacturer = "FTDI", Product = "FT232R USB UART", Interfaces = [new UsbInterfaceInfo(0, 0xFF, 0xFF, 0xFF, null)] },
        new() { Id = "1d50:606f", VendorId = 0x1D50, ProductId = 0x606F, Manufacturer = "bytewerk", Product = "candleLight USB to CAN adapter", Interfaces = [new UsbInterfaceInfo(0, 0xFF, 0xFF, 0xFF, null)] },
        new() { Id = "0483:374b", VendorId = 0x0483, ProductId = 0x374B, Manufacturer = "STMicroelectronics", Product = "STM32 STLink", Interfaces = [new UsbInterfaceInfo(0, 0xFF, 0xFF, 0xFF, null), new UsbInterfaceInfo(1, 0x08, 0x06, 0x50, null), new UsbInterfaceInfo(2, 0x02, 0x02, 0x01, null)] },
        new() { Id = "16c0:05df", VendorId = 0x16C0, ProductId = 0x05DF, Manufacturer = "www.dcttech.com", Product = "USBRelay4", Interfaces = [new UsbInterfaceInfo(0, 0x03, 0, 0, null)] },
        new() { Id = "303a:1001", VendorId = 0x303A, ProductId = 0x1001, Manufacturer = "Espressif", Product = "USB JTAG/serial debug unit", Interfaces = [new UsbInterfaceInfo(0, 0x02, 0x02, 0x00, null), new UsbInterfaceInfo(2, 0xFF, 0xFF, 0x01, null)] },
    ];

    protected override async Task OnStartAsync()
    {
        Local.Clear();
        Console.Clear();
        try
        {
            if (Environment.GetEnvironmentVariable("IOTCOM_GALLERY_SAMPLE_USB") == "1")
            {
                // Screenshots show a typical bench rather than the machine that rendered them.
                foreach (var d in SampleBench) Local.Add(new UsbRow(d));
                LocalNote = Loc.L($"{SampleBench.Count} devices on this computer · listed, never opened", $"{SampleBench.Count} perangkat di komputer ini · didaftar, tidak dibuka");
            }
            else if (NativeUsbBackend.IsSupported)
            {
                var devices = await Task.Run(() => UsbDevice.List());
                foreach (var d in devices.OrderBy(d => d.Id, StringComparer.Ordinal)) Local.Add(new UsbRow(d));
                LocalNote = Loc.L($"{devices.Count} devices on this computer · listed, never opened", $"{devices.Count} perangkat di komputer ini · didaftar, tidak dibuka");
            }
            else
            {
                LocalNote = Loc.L("The native USB library is not available here; the bench below is simulated.", "Pustaka USB native tidak tersedia di sini; meja di bawah disimulasikan.");
            }
        }
        catch (Exception ex) when (ex is IoTComException or PlatformNotSupportedException)
        {
            LocalNote = ex.Message;
        }

        _bus = new VirtualUsbBus();
        _bus.Add(new VirtualLoopbackDevice());
        _boardSim = _bus.AddHid(new VirtualHidRelayBoard(4));
        _reader = HidDevice.Create(o => { o.UseVirtual(_bus); o.UseDevice(HidRelayBoard.VendorId, HidRelayBoard.ProductId); o.ReadOnly = true; });
        _reader.AddTap(Tap);
        await _reader.ConnectAsync();
        _loop = UsbDevice.Create(o => { o.UseVirtual(_bus); o.DeviceId = "1209:0001"; });
        _loop.AddTap(Tap);
        await _loop.ConnectAsync();
        var version = Encoding.ASCII.GetString(await _loop.ControlInAsync(UsbSetup.Vendor(0x01), 16));
        Console.Add($"control IN 0xC0/0x01 → firmware {version}");
        await RefreshRelaysAsync();
        SetStatus(new Text("Virtual bus · USBRelay4 (16c0:05df, HID) and a loopback device (1209:0001, vendor class).",
            "Bus virtual · USBRelay4 (16c0:05df, HID) dan perangkat loopback (1209:0001, kelas vendor)."));
    }

    private async Task RefreshRelaysAsync()
    {
        var states = await new HidRelayBoard(_reader!).GetStatesAsync();
        Ui(() =>
        {
            Relays.Clear();
            for (var i = 0; i < states.Length; i++) Relays.Add(new RelayLamp(i + 1, states[i]));
        });
    }

    /// <summary>Toggles a relay through the guarded path.</summary>
    public async Task ToggleRelayAsync(int relay)
    {
        if (_bus is null || _reader is null) return;
        var on = !Relays.First(r => r.Number == relay).On;
        try
        {
            var device = _reader;
            if (AllowWrite)
            {
                if (_writer is null)
                {
                    _writer = HidDevice.Create(o => { o.UseVirtual(_bus); o.UseDevice(HidRelayBoard.VendorId, HidRelayBoard.ProductId); });
                    _writer.AddTap(Tap);
                    await _writer.ConnectAsync();
                }

                device = _writer;
            }

            await new HidRelayBoard(device).SetAsync(relay, on);
            LastAction = Loc.L($"Feature report 00 {(on ? "FF" : "FD")} {relay:X2} → relay {relay} {(on ? "on" : "off")}", $"Feature report 00 {(on ? "FF" : "FD")} {relay:X2} → relay {relay} {(on ? "nyala" : "mati")}");
        }
        catch (ReadOnlyModeException)
        {
            LastAction = Loc.L("Refused: the HID device is read-only. Tick “Allow writes” first.", "Ditolak: perangkat HID hanya-baca. Centang “Izinkan penulisan” dulu.");
        }

        await RefreshRelaysAsync();
    }

    /// <summary>Sends the message through the loopback device.</summary>
    public async Task SendAsync()
    {
        if (_loop is null) return;
        await _loop.ControlOutAsync(UsbSetup.Vendor(0x02, value: Upper ? (ushort)1 : (ushort)0));
        await _loop.WriteAsync(0x01, Encoding.UTF8.GetBytes(Message));
        var echo = await _loop.ReadAsync(0x81);
        Console.Insert(0, $"bulk OUT 0x01 \"{Message}\" → bulk IN 0x81 \"{(echo is null ? "(timeout)" : Encoding.UTF8.GetString(echo))}\"");
        while (Console.Count > 8) Console.RemoveAt(Console.Count - 1);
    }

    protected override async Task OnStopAsync()
    {
        if (_writer is not null) await _writer.DisposeAsync();
        if (_reader is not null) await _reader.DisposeAsync();
        if (_loop is not null) await _loop.DisposeAsync();
        (_writer, _reader, _loop, _bus, _boardSim) = (null, null, null, null, null);
        AllowWrite = false;
        Status = "";
    }
}

/// <summary>A device on this computer.</summary>
public sealed record UsbRow(string Id, string Name, string Kind)
{
    public UsbRow(UsbDeviceInfo d) : this($"{d.VendorId:x4}:{d.ProductId:x4}", $"{d.Manufacturer ?? d.VendorName ?? "?"} · {d.Product ?? "?"}", d.Kind)
    {
    }
}

/// <summary>A relay lamp.</summary>
public sealed record RelayLamp(int Number, bool On);
