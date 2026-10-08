using System.Collections.Concurrent;
using System.Text;

namespace IoTCom.Net.Transport.Usb;

/// <summary>A simulated USB device on a <see cref="VirtualUsbBus"/>.</summary>
public abstract class VirtualUsbDevice
{
    private readonly ConcurrentDictionary<byte, BlockingCollection<byte[]>> _in = new();

    /// <summary>Descriptor information reported by enumeration.</summary>
    public abstract UsbDeviceInfo Info { get; }

    /// <summary>Answers a control IN request; return null to stall.</summary>
    protected internal virtual byte[]? ControlIn(UsbSetup setup, int length) => null;

    /// <summary>Handles a control OUT request; return false to stall.</summary>
    protected internal virtual bool ControlOut(UsbSetup setup, byte[] data) => false;

    /// <summary>Handles data written to an OUT endpoint.</summary>
    protected internal virtual void Received(byte endpoint, byte[] data)
    {
    }

    /// <summary>Queues data on an IN endpoint (what the host reads next).</summary>
    public void Send(byte endpoint, byte[] data) => Queue(endpoint).Add(data);

    internal BlockingCollection<byte[]> Queue(byte endpoint) => _in.GetOrAdd(endpoint, _ => []);
}

/// <summary>A simulated HID collection.</summary>
public abstract class VirtualHidDevice
{
    private readonly BlockingCollection<byte[]> _input = [];

    /// <summary>Enumeration information.</summary>
    public abstract HidDeviceInfo Info { get; }

    /// <summary>Handles an output report.</summary>
    protected internal virtual void Output(byte[] report)
    {
    }

    /// <summary>Handles a feature report; return false to fail it.</summary>
    protected internal virtual bool SetFeature(byte[] report) => false;

    /// <summary>Returns a feature report (including the report id).</summary>
    protected internal virtual byte[] GetFeature(byte reportId, int length) => new byte[length];

    /// <summary>Queues an input report.</summary>
    public void Input(byte[] report) => _input.Add(report);

    internal byte[]? Take(TimeSpan timeout) => _input.TryTake(out var r, timeout) ? r : null;
}

/// <summary>
/// A virtual bus implementing <see cref="IUsbBackend"/>: tests, notebooks, the Gallery and <c>--sim</c> run USB and HID
/// code against simulated devices.
/// </summary>
public sealed class VirtualUsbBus : IUsbBackend
{
    private readonly ConcurrentDictionary<string, VirtualUsbDevice> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, VirtualHidDevice> _hid = new(StringComparer.Ordinal);

    /// <summary>Plugs a USB device in.</summary>
    public T Add<T>(T device) where T : VirtualUsbDevice
    {
        ArgumentNullException.ThrowIfNull(device);
        _devices[device.Info.Id] = device;
        return device;
    }

    /// <summary>Plugs a HID device in.</summary>
    public T AddHid<T>(T device) where T : VirtualHidDevice
    {
        ArgumentNullException.ThrowIfNull(device);
        _hid[device.Info.Path] = device;
        return device;
    }

    /// <summary>Unplugs a device by id or HID path.</summary>
    public void Remove(string idOrPath)
    {
        _devices.TryRemove(idOrPath, out _);
        _hid.TryRemove(idOrPath, out _);
    }

    /// <inheritdoc />
    public IReadOnlyList<UsbDeviceInfo> List() => [.. _devices.Values.Select(d => d.Info)];

    /// <inheritdoc />
    public IReadOnlyList<HidDeviceInfo> ListHid() => [.. _hid.Values.Select(d => d.Info)];

    /// <inheritdoc />
    public IUsbDeviceHandle Open(string id)
    {
        var device = _devices.GetValueOrDefault(id) ?? _devices.Values.FirstOrDefault(d => d.Info.Id.StartsWith(id + ":", StringComparison.OrdinalIgnoreCase))
            ?? throw new DeviceException($"no USB device {id}");
        return new Handle(this, device);
    }

    /// <inheritdoc />
    public IHidHandle OpenHid(string path) => new HidHandle(this, path, _hid.GetValueOrDefault(path) ?? throw new DeviceException($"no HID device {path}"));

    private sealed class Handle(VirtualUsbBus bus, VirtualUsbDevice device) : IUsbDeviceHandle
    {
        private readonly HashSet<byte> _claimed = [];

        private void Present()
        {
            if (!bus._devices.ContainsKey(device.Info.Id)) throw new TransportException("USB device disconnected");
        }

        public void Claim(byte number, bool detachKernelDriver)
        {
            Present();
            if (device.Info.Interfaces.All(i => i.Number != number)) throw new TransportException($"USB: interface {number} does not exist");
            _claimed.Add(number);
        }

        private void Claimed()
        {
            Present();
            if (_claimed.Count == 0) throw new ArgumentException("claim an interface first");
        }

        public byte[] ControlIn(byte claimedInterface, UsbSetup setup, int length, TimeSpan timeout)
        {
            Claimed();
            var data = device.ControlIn(setup, length) ?? throw new DeviceException("USB endpoint stalled");
            return data.Length > length ? data[..length] : data;
        }

        public void ControlOut(byte claimedInterface, UsbSetup setup, ReadOnlySpan<byte> data, TimeSpan timeout)
        {
            Claimed();
            if (!device.ControlOut(setup, data.ToArray())) throw new DeviceException("USB endpoint stalled");
        }

        public int Write(byte endpoint, bool interrupt, ReadOnlySpan<byte> data, TimeSpan timeout)
        {
            Claimed();
            if ((endpoint & 0x80) != 0) throw new ArgumentException($"0x{endpoint:x2} is an IN endpoint");
            device.Received(endpoint, data.ToArray());
            return data.Length;
        }

        public byte[]? Read(byte endpoint, bool interrupt, int length, TimeSpan timeout)
        {
            Claimed();
            if ((endpoint & 0x80) == 0) throw new ArgumentException($"0x{endpoint:x2} is an OUT endpoint");
            if (!device.Queue(endpoint).TryTake(out var data, timeout)) return null;
            Present();
            return data.Length > length ? data[..length] : data;
        }

        public void Dispose()
        {
        }
    }

    private sealed class HidHandle(VirtualUsbBus bus, string path, VirtualHidDevice device) : IHidHandle
    {
        private void Present()
        {
            if (!bus._hid.ContainsKey(path)) throw new TransportException("HID device disconnected");
        }

        public int Write(ReadOnlySpan<byte> report)
        {
            Present();
            device.Output(report.ToArray());
            return report.Length;
        }

        public byte[]? Read(int length, TimeSpan timeout)
        {
            Present();
            return device.Take(timeout);
        }

        public void SendFeature(ReadOnlySpan<byte> report)
        {
            Present();
            if (!device.SetFeature(report.ToArray())) throw new TransportException("HID: feature report rejected");
        }

        public byte[] GetFeature(byte reportId, int length)
        {
            Present();
            return device.GetFeature(reportId, length);
        }

        public void Dispose()
        {
        }
    }
}

/// <summary>
/// A vendor-class loopback device (pid.codes test id 1209:0001): bulk OUT 0x01 is echoed on bulk IN 0x81 (upper-cased
/// after vendor request 0x02 with wValue 1); vendor request 0x01 IN returns the firmware version.
/// </summary>
public sealed class VirtualLoopbackDevice(string serial = "LB-0001") : VirtualUsbDevice
{
    private bool _upper;

    /// <inheritdoc />
    public override UsbDeviceInfo Info { get; } = new()
    {
        Id = $"1209:0001:{serial}", VendorId = 0x1209, ProductId = 0x0001, Manufacturer = "IoTCom.Net", Product = "Loopback", Serial = serial,
        Class = 0xFF, Bus = "virtual", Address = 1, Interfaces = [new UsbInterfaceInfo(0, 0xFF, 0, 0, "Loopback")],
    };

    /// <summary>Firmware version reported by request 0x01.</summary>
    public string Firmware { get; set; } = "1.4.2";

    /// <inheritdoc />
    protected internal override byte[]? ControlIn(UsbSetup setup, int length) => setup.Request switch
    {
        0x01 => Encoding.ASCII.GetBytes(Firmware),
        0x06 when setup.Value >> 8 == 0x01 => [18, 1, 0x00, 0x02, 0xFF, 0, 0, 64, 0x09, 0x12, 0x01, 0x00, 0x00, 0x01, 1, 2, 3, 1],
        _ => null,
    };

    /// <inheritdoc />
    protected internal override bool ControlOut(UsbSetup setup, byte[] data)
    {
        if (setup.Request != 0x02) return false;
        _upper = setup.Value == 1;
        return true;
    }

    /// <inheritdoc />
    protected internal override void Received(byte endpoint, byte[] data)
    {
        if (endpoint == 0x01) Send(0x81, _upper ? Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(data).ToUpperInvariant()) : data);
    }
}

/// <summary>
/// A simulated "USBRelay2/4/8" HID relay board (V-USB, 16c0:05df), the common dcttech protocol: feature report 0
/// returns the 5-character serial and the relay bit mask at byte 8; commands 0xFF n (on), 0xFD n (off), 0xFE (all on)
/// and 0xFC (all off).
/// </summary>
public sealed class VirtualHidRelayBoard : VirtualHidDevice
{
    private readonly string _serial;

    /// <summary>Creates a board with <paramref name="relays"/> relays.</summary>
    public VirtualHidRelayBoard(int relays = 2, string serial = "IOTCM", string path = "virtual-hid:relay")
    {
        Relays = relays;
        _serial = serial.PadRight(5)[..5];
        Info = new HidDeviceInfo
        {
            Path = path, VendorId = 0x16C0, ProductId = 0x05DF, Manufacturer = "www.dcttech.com", Product = $"USBRelay{relays}",
            UsagePage = 0xFF00, Usage = 1, Interface = 0,
        };
    }

    /// <inheritdoc />
    public override HidDeviceInfo Info { get; }

    /// <summary>Number of relays.</summary>
    public int Relays { get; }

    /// <summary>Relay state bits (bit 0 = relay 1).</summary>
    public byte State { get; private set; }

    /// <summary>Raised when a relay changes.</summary>
    public event Action<byte>? Changed;

    /// <inheritdoc />
    protected internal override bool SetFeature(byte[] report)
    {
        if (report.Length < 2) return false;
        var mask = (byte)((1 << Relays) - 1);
        var n = report.Length > 2 ? report[2] : 0;
        var bit = n is >= 1 and <= 8 ? (byte)(1 << (n - 1)) : (byte)0;
        switch (report[1])
        {
            case 0xFF when bit != 0 && n <= Relays: State |= bit; break;
            case 0xFD when bit != 0 && n <= Relays: State &= (byte)~bit; break;
            case 0xFE: State = mask; break;
            case 0xFC: State = 0; break;
            default: return false;
        }

        Changed?.Invoke(State);
        return true;
    }

    /// <inheritdoc />
    protected internal override byte[] GetFeature(byte reportId, int length)
    {
        var b = new byte[Math.Max(length, 9)];
        b[0] = reportId;
        Encoding.ASCII.GetBytes(_serial).CopyTo(b, 1);
        b[8] = State;
        return b[..Math.Max(length, 9)];
    }
}
