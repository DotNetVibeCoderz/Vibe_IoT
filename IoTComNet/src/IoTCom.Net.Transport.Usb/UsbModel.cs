using System.Globalization;

namespace IoTCom.Net.Transport.Usb;

/// <summary>An interface of a USB device.</summary>
/// <param name="Number">bInterfaceNumber.</param>
/// <param name="Class">bInterfaceClass.</param>
/// <param name="Subclass">bInterfaceSubClass.</param>
/// <param name="Protocol">bInterfaceProtocol.</param>
/// <param name="Name">Interface string, if any.</param>
public sealed record UsbInterfaceInfo(byte Number, byte Class, byte Subclass, byte Protocol, string? Name)
{
    /// <summary>Readable class name.</summary>
    public string ClassName => UsbIds.ClassName(Class);
}

/// <summary>A USB device found by enumeration.</summary>
public sealed record UsbDeviceInfo
{
    /// <summary>Id used to open it: <c>vvvv:pppp[:serial]</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Vendor id.</summary>
    public ushort VendorId { get; init; }

    /// <summary>Product id.</summary>
    public ushort ProductId { get; init; }

    /// <summary>Manufacturer string.</summary>
    public string? Manufacturer { get; init; }

    /// <summary>Product string.</summary>
    public string? Product { get; init; }

    /// <summary>Serial number string.</summary>
    public string? Serial { get; init; }

    /// <summary>Device class (0 = per interface).</summary>
    public byte Class { get; init; }

    /// <summary>Bus id.</summary>
    public string? Bus { get; init; }

    /// <summary>Address on the bus.</summary>
    public byte Address { get; init; }

    /// <summary>Interfaces.</summary>
    public IReadOnlyList<UsbInterfaceInfo> Interfaces { get; init; } = [];

    /// <summary>Vendor name for well-known ids.</summary>
    public string? VendorName => UsbIds.Vendor(VendorId);

    /// <summary>What kind of device this is, from the vid/pid table or the interface classes.</summary>
    public string Kind => UsbIds.Known(VendorId, ProductId) ?? string.Join(" + ", Interfaces.Select(i => i.ClassName).Distinct());

    /// <inheritdoc />
    public override string ToString() => $"{VendorId:x4}:{ProductId:x4} {Manufacturer ?? VendorName} {Product ?? Kind}";
}

/// <summary>A HID device (top-level collection) found by enumeration.</summary>
public sealed record HidDeviceInfo
{
    /// <summary>Platform path used to open it.</summary>
    public required string Path { get; init; }

    /// <summary>Vendor id.</summary>
    public ushort VendorId { get; init; }

    /// <summary>Product id.</summary>
    public ushort ProductId { get; init; }

    /// <summary>Manufacturer.</summary>
    public string? Manufacturer { get; init; }

    /// <summary>Product.</summary>
    public string? Product { get; init; }

    /// <summary>Serial number.</summary>
    public string? Serial { get; init; }

    /// <summary>Usage page (0x01 generic desktop, 0x0C consumer, 0xFF00+ vendor).</summary>
    public ushort UsagePage { get; init; }

    /// <summary>Usage.</summary>
    public ushort Usage { get; init; }

    /// <summary>USB interface number (-1 when unknown).</summary>
    public int Interface { get; init; } = -1;

    /// <summary>Readable usage, e.g. "Keyboard" or "Vendor 0xFF00".</summary>
    public string UsageName => (UsagePage, Usage) switch
    {
        (0x01, 0x02) => "Mouse",
        (0x01, 0x04) => "Joystick",
        (0x01, 0x05) => "Game pad",
        (0x01, 0x06) => "Keyboard",
        (0x01, 0x80) => "System control",
        (0x0C, 0x01) => "Consumer control",
        (0x0D, _) => "Digitizer",
        (>= 0xFF00, _) => string.Create(CultureInfo.InvariantCulture, $"Vendor 0x{UsagePage:X4}"),
        _ => string.Create(CultureInfo.InvariantCulture, $"0x{UsagePage:X2}/0x{Usage:X2}"),
    };

    /// <inheritdoc />
    public override string ToString() => $"{VendorId:x4}:{ProductId:x4} {Manufacturer} {Product} ({UsageName})";
}

/// <summary>A USB control request (SETUP packet).</summary>
/// <param name="RequestType">bmRequestType (direction bit 7 is set by the call: IN or OUT).</param>
/// <param name="Request">bRequest.</param>
/// <param name="Value">wValue.</param>
/// <param name="Index">wIndex.</param>
public readonly record struct UsbSetup(byte RequestType, byte Request, ushort Value, ushort Index)
{
    /// <summary>A vendor request to the device.</summary>
    public static UsbSetup Vendor(byte request, ushort value = 0, ushort index = 0) => new(0x40, request, value, index);

    /// <summary>A vendor request to an interface.</summary>
    public static UsbSetup VendorInterface(byte request, ushort value = 0, ushort index = 0) => new(0x41, request, value, index);

    /// <summary>A class request to an interface (CDC, HID, DFU…).</summary>
    public static UsbSetup ClassInterface(byte request, ushort value = 0, ushort index = 0) => new(0x21, request, value, index);

    /// <summary>GET_DESCRIPTOR (standard, device).</summary>
    public static UsbSetup GetDescriptor(byte type, byte index = 0, ushort language = 0) => new(0x80, 0x06, (ushort)((type << 8) | index), language);
}

/// <summary>Names for USB classes and a few well-known vendor/product ids.</summary>
public static class UsbIds
{
    private static readonly Dictionary<ushort, string> Vendors = new()
    {
        [0x0403] = "FTDI", [0x0483] = "STMicroelectronics", [0x046D] = "Logitech", [0x04D8] = "Microchip", [0x067B] = "Prolific",
        [0x0C72] = "PEAK-System", [0x10C4] = "Silicon Labs", [0x16C0] = "Van Ooijen (V-USB)", [0x1A86] = "WCH", [0x1D50] = "OpenMoko (open hardware)",
        [0x1366] = "SEGGER", [0x2341] = "Arduino", [0x2E8A] = "Raspberry Pi", [0x303A] = "Espressif", [0x8087] = "Intel", [0x0BDA] = "Realtek",
        [0x0A5C] = "Broadcom", [0x0BDB] = "Ericsson", [0x1209] = "pid.codes",
    };

    private static readonly Dictionary<(ushort, ushort), string> Products = new()
    {
        [(0x0403, 0x6001)] = "FT232R USB-serial", [(0x0403, 0x6010)] = "FT2232 USB-serial/JTAG", [(0x0403, 0x6015)] = "FT231X USB-serial",
        [(0x10C4, 0xEA60)] = "CP210x USB-serial", [(0x1A86, 0x7523)] = "CH340 USB-serial", [(0x067B, 0x2303)] = "PL2303 USB-serial",
        [(0x1D50, 0x606F)] = "candleLight / gs_usb CAN adapter", [(0x0C72, 0x000C)] = "PCAN-USB", [(0x0C72, 0x0011)] = "PCAN-USB Pro FD",
        [(0x16C0, 0x05DF)] = "USB HID relay board", [(0x0483, 0xDF11)] = "STM32 DFU bootloader", [(0x0483, 0x374B)] = "ST-LINK/V2-1",
        [(0x1366, 0x0101)] = "J-Link", [(0x2E8A, 0x0003)] = "RP2040 boot", [(0x303A, 0x1001)] = "ESP32-S3 USB JTAG/serial",
    };

    /// <summary>Vendor name for a well-known vendor id.</summary>
    public static string? Vendor(ushort vid) => Vendors.GetValueOrDefault(vid);

    /// <summary>Product description for a well-known vid/pid.</summary>
    public static string? Known(ushort vid, ushort pid) => Products.GetValueOrDefault((vid, pid));

    /// <summary>USB-IF class name.</summary>
    public static string ClassName(byte c) => c switch
    {
        0x00 => "Per interface", 0x01 => "Audio", 0x02 => "CDC control", 0x03 => "HID", 0x05 => "Physical", 0x06 => "Image",
        0x07 => "Printer", 0x08 => "Mass storage", 0x09 => "Hub", 0x0A => "CDC data", 0x0B => "Smart card", 0x0D => "Content security",
        0x0E => "Video", 0x0F => "Personal healthcare", 0x10 => "Audio/video", 0x11 => "Billboard", 0xDC => "Diagnostic",
        0xE0 => "Wireless controller", 0xEF => "Miscellaneous", 0xFE => "Application specific", 0xFF => "Vendor specific",
        _ => string.Create(CultureInfo.InvariantCulture, $"0x{c:X2}"),
    };
}
