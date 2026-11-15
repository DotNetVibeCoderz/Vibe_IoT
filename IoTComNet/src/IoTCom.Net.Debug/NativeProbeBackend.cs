using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using IoTCom.Net.Native;

namespace IoTCom.Net.Debug;

/// <summary>Source-generated P/Invoke bindings for <c>iotcom_probe</c> (probe-rs).</summary>
internal static unsafe partial class ProbeNativeMethods
{
    public const string Library = "iotcom_probe";

    /// <summary>ABI version this binding was written for (iotcom-ffi-support ABI_VERSION).</summary>
    public const uint ExpectedAbiVersion = 1;

    public const int ErrBufferTooSmall = -4;
    public const int ErrProbe = -40;
    public const int ErrProbeNotFound = -41;
    public const int ErrTarget = -42;
    public const int ErrFlash = -43;

    [ModuleInitializer]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2255", Justification = "Registers the native resolver before the first P/Invoke.")]
    internal static void Init() => NativeLibraryLoader.Register(typeof(ProbeNativeMethods).Assembly);

    [LibraryImport(Library, EntryPoint = "iotcom_abi_version")]
    public static partial uint AbiVersion();

    [LibraryImport(Library, EntryPoint = "iotcom_last_error")]
    public static partial int LastError(byte* buf, nuint len);

    [LibraryImport(Library, EntryPoint = "iotcom_probe_list")]
    public static partial int List(byte* buf, nuint len, out nuint written);

    [LibraryImport(Library, EntryPoint = "iotcom_probe_open")]
    public static partial int Open(byte* selector, nuint selectorLen, byte* target, nuint targetLen, int protocol, uint speedKhz, out nint handle);

    [LibraryImport(Library, EntryPoint = "iotcom_probe_free")]
    public static partial void Free(nint handle);

    [LibraryImport(Library, EntryPoint = "iotcom_probe_read")]
    public static partial int Read(ProbeHandle handle, ulong address, byte* buf, nuint len);

    [LibraryImport(Library, EntryPoint = "iotcom_probe_write")]
    public static partial int Write(ProbeHandle handle, ulong address, byte* data, nuint len);

    [LibraryImport(Library, EntryPoint = "iotcom_probe_halt")]
    public static partial int Halt(ProbeHandle handle);

    [LibraryImport(Library, EntryPoint = "iotcom_probe_run")]
    public static partial int Run(ProbeHandle handle);

    [LibraryImport(Library, EntryPoint = "iotcom_probe_reset")]
    public static partial int Reset(ProbeHandle handle, byte halt);

    [LibraryImport(Library, EntryPoint = "iotcom_probe_core")]
    public static partial int Core(ProbeHandle handle, out int state, out uint pc);

    [LibraryImport(Library, EntryPoint = "iotcom_probe_flash_add")]
    public static partial int FlashAdd(ProbeHandle handle, ulong address, byte* data, nuint len);

    [LibraryImport(Library, EntryPoint = "iotcom_probe_flash_commit")]
    public static partial int FlashCommit(ProbeHandle handle, byte verify);

    [LibraryImport(Library, EntryPoint = "iotcom_probe_erase_all")]
    public static partial int EraseAll(ProbeHandle handle);

    public static string LastErrorMessage()
    {
        Span<byte> buf = stackalloc byte[1024];
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
            ErrProbeNotFound => new DeviceException(message),
            ErrTarget or ErrFlash or ErrProbe => new DeviceException(message),
            -2 => new ArgumentException(message),
            _ => new TransportException($"iotcom_probe error {status}: {message}"),
        };
    }
}

/// <summary>Owns a native probe session.</summary>
internal sealed class ProbeHandle : SafeHandle
{
    public ProbeHandle() : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    public ProbeHandle(nint handle) : base(IntPtr.Zero, ownsHandle: true) => SetHandle(handle);

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        ProbeNativeMethods.Free(handle);
        return true;
    }
}

/// <summary>
/// Debug probes through the Rust <c>iotcom_probe</c> library (probe-rs): CMSIS-DAP v1/v2, ST-Link v2/v3, SEGGER J-Link,
/// ESP32 USB-JTAG, FTDI and WCH-Link; flash algorithms for the chips probe-rs knows. On Windows, CMSIS-DAP v2 and ST-Link
/// need the WinUSB driver (Zadig) unless the vendor driver is installed; on Linux, udev rules for the probe.
/// </summary>
public sealed class NativeProbeBackend : IProbeBackend
{
    /// <summary>Shared instance.</summary>
    public static NativeProbeBackend Instance { get; } = new();

    /// <summary>True when the native library can be loaded on this machine.</summary>
    public static bool IsSupported => NativeLibraryLoader.IsAvailable(ProbeNativeMethods.Library, typeof(ProbeNativeMethods).Assembly);

    private static void EnsureCompatible()
    {
        uint abi;
        try
        {
            abi = ProbeNativeMethods.AbiVersion();
        }
        catch (DllNotFoundException ex)
        {
            throw new PlatformNotSupportedException(
                $"Native library '{ProbeNativeMethods.Library}' for {NativeLibraryLoader.RuntimeIdentifier} was not found. Set {NativeLibraryLoader.OverrideVariable} or use a supported RID.", ex);
        }

        if (abi != ProbeNativeMethods.ExpectedAbiVersion)
            throw new PlatformNotSupportedException($"Native ABI mismatch: library reports {abi}, binding expects {ProbeNativeMethods.ExpectedAbiVersion}.");
    }

    /// <summary>Parses the JSON probe list of the native library.</summary>
    public static IReadOnlyList<ProbeInfo> ParseProbes(ReadOnlySpan<byte> json)
    {
        using var doc = JsonDocument.Parse(json.ToArray());
        return [.. doc.RootElement.EnumerateArray().Select(p => new ProbeInfo(p.GetProperty("selector").GetString()!, p.GetProperty("name").GetString() ?? "",
            p.GetProperty("kind").GetString() ?? "", p.TryGetProperty("serial", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null))];
    }

    /// <inheritdoc />
    public unsafe IReadOnlyList<ProbeInfo> List()
    {
        EnsureCompatible();
        var buf = new byte[16 * 1024];
        while (true)
        {
            int status;
            nuint written;
            fixed (byte* p = buf) status = ProbeNativeMethods.List(p, (nuint)buf.Length, out written);
            if (status == ProbeNativeMethods.ErrBufferTooSmall)
            {
                buf = new byte[(int)written];
                continue;
            }

            ProbeNativeMethods.Check(status);
            return ParseProbes(buf.AsSpan(0, (int)written));
        }
    }

    /// <inheritdoc />
    public unsafe IProbeSession Open(string? selector, string target, ProbeProtocol protocol, int speedKhz)
    {
        EnsureCompatible();
        var sel = Encoding.UTF8.GetBytes(selector ?? "");
        var tgt = Encoding.UTF8.GetBytes(target);
        nint handle;
        fixed (byte* s = sel)
        fixed (byte* t = tgt)
            ProbeNativeMethods.Check(ProbeNativeMethods.Open(s, (nuint)sel.Length, t, (nuint)tgt.Length, (int)protocol, (uint)speedKhz, out handle));
        return new Session(new ProbeHandle(handle), target, selector ?? "probe");
    }

    private sealed unsafe class Session(ProbeHandle handle, string target, string probe) : IProbeSession
    {
        public string Probe => probe;

        public string Target => target;

        public byte[] Read(ulong address, int length)
        {
            var buf = new byte[length];
            fixed (byte* p = buf) ProbeNativeMethods.Check(ProbeNativeMethods.Read(handle, address, p, (nuint)length));
            return buf;
        }

        public void Write(ulong address, ReadOnlySpan<byte> data)
        {
            fixed (byte* p = data) ProbeNativeMethods.Check(ProbeNativeMethods.Write(handle, address, p, (nuint)data.Length));
        }

        public void Halt() => ProbeNativeMethods.Check(ProbeNativeMethods.Halt(handle));

        public void Run() => ProbeNativeMethods.Check(ProbeNativeMethods.Run(handle));

        public void Reset(bool halt) => ProbeNativeMethods.Check(ProbeNativeMethods.Reset(handle, (byte)(halt ? 1 : 0)));

        public (CoreState State, uint Pc) Core()
        {
            ProbeNativeMethods.Check(ProbeNativeMethods.Core(handle, out var state, out var pc));
            return ((CoreState)state, pc);
        }

        public void Flash(FirmwareImage image, Action<FlashProgress>? progress)
        {
            foreach (var s in image.Segments)
                fixed (byte* p = s.Data)
                    ProbeNativeMethods.Check(ProbeNativeMethods.FlashAdd(handle, s.Address, p, (nuint)s.Data.Length));
            progress?.Invoke(new FlashProgress("Programming", 0));
            ProbeNativeMethods.Check(ProbeNativeMethods.FlashCommit(handle, 1));   // erase, program and verify in probe-rs
            progress?.Invoke(new FlashProgress("Verifying", 1));
        }

        public void EraseAll() => ProbeNativeMethods.Check(ProbeNativeMethods.EraseAll(handle));

        public void Dispose() => handle.Dispose();
    }
}
