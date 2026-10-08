using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using IoTCom.Net.Native;

namespace IoTCom.Net.Transport.Usb;

/// <summary>Source-generated P/Invoke bindings for <c>iotcom_usb</c> (AOT and trimming friendly).</summary>
internal static unsafe partial class UsbNativeMethods
{
    public const string Library = "iotcom_usb";

    /// <summary>ABI version this binding was written for (iotcom-ffi-support ABI_VERSION).</summary>
    public const uint ExpectedAbiVersion = 1;

    public const int ErrBufferTooSmall = -4;
    public const int ErrUsb = -30;
    public const int ErrTimeout = -31;
    public const int ErrStall = -32;
    public const int ErrDisconnected = -33;
    public const int ErrNotFound = -34;

    [ModuleInitializer]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2255", Justification = "Registers the native resolver before the first P/Invoke.")]
    internal static void Init() => NativeLibraryLoader.Register(typeof(UsbNativeMethods).Assembly);

    [LibraryImport(Library, EntryPoint = "iotcom_abi_version")]
    public static partial uint AbiVersion();

    [LibraryImport(Library, EntryPoint = "iotcom_last_error")]
    public static partial int LastError(byte* buf, nuint len);

    [LibraryImport(Library, EntryPoint = "iotcom_usb_list")]
    public static partial int List(byte* buf, nuint len, out nuint written);

    [LibraryImport(Library, EntryPoint = "iotcom_usb_open")]
    public static partial int Open(byte* id, nuint idLen, out nint handle);

    [LibraryImport(Library, EntryPoint = "iotcom_usb_free")]
    public static partial void Free(nint handle);

    [LibraryImport(Library, EntryPoint = "iotcom_usb_claim")]
    public static partial int Claim(UsbHandle handle, byte iface, byte detach);

    [LibraryImport(Library, EntryPoint = "iotcom_usb_control_in")]
    public static partial int ControlIn(UsbHandle handle, byte iface, byte requestType, byte request, ushort value, ushort index, byte* buf, nuint len, uint timeoutMs, out nuint written);

    [LibraryImport(Library, EntryPoint = "iotcom_usb_control_out")]
    public static partial int ControlOut(UsbHandle handle, byte iface, byte requestType, byte request, ushort value, ushort index, byte* data, nuint len, uint timeoutMs);

    [LibraryImport(Library, EntryPoint = "iotcom_usb_write")]
    public static partial int Write(UsbHandle handle, byte endpoint, byte interrupt, byte* data, nuint len, uint timeoutMs);

    [LibraryImport(Library, EntryPoint = "iotcom_usb_read")]
    public static partial int Read(UsbHandle handle, byte endpoint, byte interrupt, byte* buf, nuint len, uint timeoutMs, out nuint written);

    [LibraryImport(Library, EntryPoint = "iotcom_hid_list")]
    public static partial int HidList(byte* buf, nuint len, out nuint written);

    [LibraryImport(Library, EntryPoint = "iotcom_hid_open")]
    public static partial int HidOpen(byte* path, nuint len, out nint handle);

    [LibraryImport(Library, EntryPoint = "iotcom_hid_free")]
    public static partial void HidFree(nint handle);

    [LibraryImport(Library, EntryPoint = "iotcom_hid_write")]
    public static partial int HidWrite(HidHandle handle, byte* data, nuint len);

    [LibraryImport(Library, EntryPoint = "iotcom_hid_read")]
    public static partial int HidRead(HidHandle handle, byte* buf, nuint len, int timeoutMs, out nuint written);

    [LibraryImport(Library, EntryPoint = "iotcom_hid_send_feature")]
    public static partial int HidSendFeature(HidHandle handle, byte* data, nuint len);

    [LibraryImport(Library, EntryPoint = "iotcom_hid_get_feature")]
    public static partial int HidGetFeature(HidHandle handle, byte* buf, nuint len, out nuint written);

    public static string LastErrorMessage()
    {
        Span<byte> buf = stackalloc byte[512];
        fixed (byte* p = buf)
        {
            var n = LastError(p, (nuint)buf.Length);
            return n <= 0 ? "unknown native error" : Encoding.UTF8.GetString(buf[..Math.Min(n, buf.Length)]);
        }
    }

    public static int Check(int status)
    {
        if (status >= 0) return status;
        var message = LastErrorMessage();
        throw status switch
        {
            ErrTimeout => new IoTComTimeoutException(message),
            ErrStall => new DeviceException(message),
            ErrDisconnected => new TransportException(message),
            ErrNotFound => new DeviceException(message),
            -2 => new ArgumentException(message),
            _ => new TransportException($"iotcom_usb error {status}: {message}"),
        };
    }

    public static uint Ms(TimeSpan t) => (uint)Math.Clamp(t.TotalMilliseconds, 1, uint.MaxValue);
}

/// <summary>Owns a native USB device handle.</summary>
internal sealed class UsbHandle : SafeHandle
{
    public UsbHandle() : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    public UsbHandle(nint handle) : base(IntPtr.Zero, ownsHandle: true) => SetHandle(handle);

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        UsbNativeMethods.Free(handle);
        return true;
    }
}

/// <summary>Owns a native HID handle.</summary>
internal sealed class HidHandle : SafeHandle
{
    public HidHandle() : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    public HidHandle(nint handle) : base(IntPtr.Zero, ownsHandle: true) => SetHandle(handle);

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        UsbNativeMethods.HidFree(handle);
        return true;
    }
}

/// <summary>
/// The platform USB stack through the Rust <c>iotcom_usb</c> library: nusb (WinUSB on Windows, usbfs on Linux, IOKit on
/// macOS) for raw transfers and hidapi for HID. Raw access on Windows needs the WinUSB driver on the interface (Zadig).
/// </summary>
public sealed class NativeUsbBackend : IUsbBackend
{
    /// <summary>Shared instance.</summary>
    public static NativeUsbBackend Instance { get; } = new();

    /// <summary>True when the native library can be loaded on this machine.</summary>
    public static bool IsSupported => NativeLibraryLoader.IsAvailable(UsbNativeMethods.Library, typeof(UsbNativeMethods).Assembly);

    private static void EnsureCompatible()
    {
        uint abi;
        try
        {
            abi = UsbNativeMethods.AbiVersion();
        }
        catch (DllNotFoundException ex)
        {
            throw new PlatformNotSupportedException(
                $"Native library '{UsbNativeMethods.Library}' for {NativeLibraryLoader.RuntimeIdentifier} was not found. Set {NativeLibraryLoader.OverrideVariable} or use a supported RID.", ex);
        }

        if (abi != UsbNativeMethods.ExpectedAbiVersion)
            throw new PlatformNotSupportedException($"Native ABI mismatch: library reports {abi}, binding expects {UsbNativeMethods.ExpectedAbiVersion}.");
    }

    internal unsafe delegate int Fetcher(byte* buf, nuint len, out nuint written);

    internal static unsafe byte[] Fetch(Fetcher call, int initial = 16 * 1024)
    {
        var buf = new byte[initial];
        while (true)
        {
            int status;
            nuint written;
            fixed (byte* p = buf) status = call(p, (nuint)buf.Length, out written);
            if (status == UsbNativeMethods.ErrBufferTooSmall)
            {
                buf = new byte[(int)written];
                continue;
            }

            UsbNativeMethods.Check(status);
            return buf[..(int)written];
        }
    }

    /// <summary>Parses the JSON device list of the native library (exposed for tests and tools).</summary>
    public static IReadOnlyList<UsbDeviceInfo> ParseDevices(ReadOnlySpan<byte> json)
    {
        using var doc = JsonDocument.Parse(json.ToArray());
        return [.. doc.RootElement.EnumerateArray().Select(d => new UsbDeviceInfo
        {
            Id = d.GetProperty("id").GetString()!,
            VendorId = d.GetProperty("vid").GetUInt16(),
            ProductId = d.GetProperty("pid").GetUInt16(),
            Manufacturer = d.GetProperty("manufacturer").GetString(),
            Product = d.GetProperty("product").GetString(),
            Serial = d.GetProperty("serial").GetString(),
            Class = d.GetProperty("class").GetByte(),
            Bus = d.GetProperty("bus").GetString(),
            Address = d.GetProperty("address").GetByte(),
            Interfaces = [.. d.GetProperty("interfaces").EnumerateArray().Select(i => new UsbInterfaceInfo(
                i.GetProperty("number").GetByte(), i.GetProperty("class").GetByte(), i.GetProperty("subclass").GetByte(), i.GetProperty("protocol").GetByte(),
                i.GetProperty("name").GetString()))],
        })];
    }

    /// <summary>Parses the JSON HID list of the native library.</summary>
    public static IReadOnlyList<HidDeviceInfo> ParseHid(ReadOnlySpan<byte> json)
    {
        using var doc = JsonDocument.Parse(json.ToArray());
        return [.. doc.RootElement.EnumerateArray().Select(d => new HidDeviceInfo
        {
            Path = d.GetProperty("path").GetString()!,
            VendorId = d.GetProperty("vid").GetUInt16(),
            ProductId = d.GetProperty("pid").GetUInt16(),
            Manufacturer = NullIfEmpty(d.GetProperty("manufacturer").GetString()),
            Product = NullIfEmpty(d.GetProperty("product").GetString()),
            Serial = NullIfEmpty(d.GetProperty("serial").GetString()),
            UsagePage = d.GetProperty("usage_page").GetUInt16(),
            Usage = d.GetProperty("usage").GetUInt16(),
            Interface = d.GetProperty("interface").GetInt32(),
        })];
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    /// <inheritdoc />
    public unsafe IReadOnlyList<UsbDeviceInfo> List()
    {
        EnsureCompatible();
        return ParseDevices(Fetch((byte* b, nuint l, out nuint w) => UsbNativeMethods.List(b, l, out w)));
    }

    /// <inheritdoc />
    public unsafe IReadOnlyList<HidDeviceInfo> ListHid()
    {
        EnsureCompatible();
        return ParseHid(Fetch((byte* b, nuint l, out nuint w) => UsbNativeMethods.HidList(b, l, out w)));
    }

    /// <inheritdoc />
    public unsafe IUsbDeviceHandle Open(string id)
    {
        EnsureCompatible();
        var bytes = Encoding.UTF8.GetBytes(id);
        nint raw;
        fixed (byte* p = bytes) UsbNativeMethods.Check(UsbNativeMethods.Open(p, (nuint)bytes.Length, out raw));
        return new NativeDevice(new UsbHandle(raw));
    }

    /// <inheritdoc />
    public unsafe IHidHandle OpenHid(string path)
    {
        EnsureCompatible();
        var bytes = Encoding.UTF8.GetBytes(path);
        nint raw;
        fixed (byte* p = bytes) UsbNativeMethods.Check(UsbNativeMethods.HidOpen(p, (nuint)bytes.Length, out raw));
        return new NativeHid(new HidHandle(raw));
    }

    private sealed unsafe class NativeDevice(UsbHandle handle) : IUsbDeviceHandle
    {
        public void Claim(byte number, bool detachKernelDriver) => UsbNativeMethods.Check(UsbNativeMethods.Claim(handle, number, detachKernelDriver ? (byte)1 : (byte)0));

        public byte[] ControlIn(byte claimedInterface, UsbSetup setup, int length, TimeSpan timeout)
        {
            var buf = new byte[length];
            nuint written;
            fixed (byte* p = buf)
                UsbNativeMethods.Check(UsbNativeMethods.ControlIn(handle, claimedInterface, (byte)(setup.RequestType | 0x80), setup.Request, setup.Value, setup.Index, p, (nuint)buf.Length, UsbNativeMethods.Ms(timeout), out written));
            return buf[..(int)written];
        }

        public void ControlOut(byte claimedInterface, UsbSetup setup, ReadOnlySpan<byte> data, TimeSpan timeout)
        {
            fixed (byte* p = data)
                UsbNativeMethods.Check(UsbNativeMethods.ControlOut(handle, claimedInterface, (byte)(setup.RequestType & 0x7F), setup.Request, setup.Value, setup.Index, p, (nuint)data.Length, UsbNativeMethods.Ms(timeout)));
        }

        public int Write(byte endpoint, bool interrupt, ReadOnlySpan<byte> data, TimeSpan timeout)
        {
            fixed (byte* p = data) return UsbNativeMethods.Check(UsbNativeMethods.Write(handle, endpoint, interrupt ? (byte)1 : (byte)0, p, (nuint)data.Length, UsbNativeMethods.Ms(timeout)));
        }

        public byte[]? Read(byte endpoint, bool interrupt, int length, TimeSpan timeout)
        {
            var buf = new byte[length];
            int status;
            nuint written;
            fixed (byte* p = buf) status = UsbNativeMethods.Read(handle, endpoint, interrupt ? (byte)1 : (byte)0, p, (nuint)buf.Length, UsbNativeMethods.Ms(timeout), out written);
            if (status == UsbNativeMethods.ErrTimeout) return null;
            UsbNativeMethods.Check(status);
            return buf[..(int)written];
        }

        public void Dispose() => handle.Dispose();
    }

    private sealed unsafe class NativeHid(HidHandle handle) : IHidHandle
    {
        public int Write(ReadOnlySpan<byte> report)
        {
            fixed (byte* p = report) return UsbNativeMethods.Check(UsbNativeMethods.HidWrite(handle, p, (nuint)report.Length));
        }

        public byte[]? Read(int length, TimeSpan timeout)
        {
            var buf = new byte[length];
            nuint written;
            fixed (byte* p = buf) UsbNativeMethods.Check(UsbNativeMethods.HidRead(handle, p, (nuint)buf.Length, (int)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue), out written));
            return written == 0 ? null : buf[..(int)written];
        }

        public void SendFeature(ReadOnlySpan<byte> report)
        {
            fixed (byte* p = report) UsbNativeMethods.Check(UsbNativeMethods.HidSendFeature(handle, p, (nuint)report.Length));
        }

        public byte[] GetFeature(byte reportId, int length)
        {
            var buf = new byte[length];
            buf[0] = reportId;
            nuint written;
            fixed (byte* p = buf) UsbNativeMethods.Check(UsbNativeMethods.HidGetFeature(handle, p, (nuint)buf.Length, out written));
            return buf[..(int)written];
        }

        public void Dispose() => handle.Dispose();
    }
}
