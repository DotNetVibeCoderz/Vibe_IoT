namespace IoTCom.Net.Transport.Usb;

/// <summary>Access to USB and HID devices: the native library or a virtual bus.</summary>
public interface IUsbBackend
{
    /// <summary>Lists USB devices.</summary>
    IReadOnlyList<UsbDeviceInfo> List();

    /// <summary>Lists HID collections.</summary>
    IReadOnlyList<HidDeviceInfo> ListHid();

    /// <summary>Opens a USB device by <c>vvvv:pppp[:serial]</c>.</summary>
    IUsbDeviceHandle Open(string id);

    /// <summary>Opens a HID collection by path.</summary>
    IHidHandle OpenHid(string path);
}

/// <summary>An open USB device. Calls block for at most their timeout; implementations are not thread-safe per handle.</summary>
public interface IUsbDeviceHandle : IDisposable
{
    /// <summary>Claims an interface (detaching a kernel driver on Linux when asked).</summary>
    void Claim(byte number, bool detachKernelDriver);

    /// <summary>Control IN transfer through a claimed interface (0xFF = any claimed one).</summary>
    byte[] ControlIn(byte claimedInterface, UsbSetup setup, int length, TimeSpan timeout);

    /// <summary>Control OUT transfer.</summary>
    void ControlOut(byte claimedInterface, UsbSetup setup, ReadOnlySpan<byte> data, TimeSpan timeout);

    /// <summary>Writes to an OUT endpoint; returns bytes sent.</summary>
    int Write(byte endpoint, bool interrupt, ReadOnlySpan<byte> data, TimeSpan timeout);

    /// <summary>Reads one transfer from an IN endpoint; null when the timeout passed without data.</summary>
    byte[]? Read(byte endpoint, bool interrupt, int length, TimeSpan timeout);
}

/// <summary>An open HID collection.</summary>
public interface IHidHandle : IDisposable
{
    /// <summary>Writes an output report (byte 0 = report id).</summary>
    int Write(ReadOnlySpan<byte> report);

    /// <summary>Reads an input report; null on timeout.</summary>
    byte[]? Read(int length, TimeSpan timeout);

    /// <summary>Sends a feature report (byte 0 = report id).</summary>
    void SendFeature(ReadOnlySpan<byte> report);

    /// <summary>Gets a feature report of <paramref name="length"/> bytes for <paramref name="reportId"/>.</summary>
    byte[] GetFeature(byte reportId, int length);
}
