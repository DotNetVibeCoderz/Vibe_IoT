using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using IoTCom.Net.Native;

namespace IoTCom.Net.Protocols.IsoTp.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct IsoTpCfgNative
{
    public byte TxDl;
    public byte Padding;
    public byte Flags;
    public byte ExtAddrTx;
    public byte ExtAddrRx;
    public byte BlockSize;
    public byte StMin;
    public ushort MaxWaitFrames;
    public uint NBsMs;
    public uint NCrMs;
    public uint MaxRxLen;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct IsoTpEventNative
{
    public byte Kind;
    public uint Value;
    public byte* Data;
    public nuint DataLen;
}

/// <summary>Source-generated P/Invoke bindings for <c>iotcom_isotp</c> (AOT and trimming friendly).</summary>
internal static unsafe partial class NativeMethods
{
    public const string Library = "iotcom_isotp";

    /// <summary>ABI version this binding was written for (iotcom-ffi-support ABI_VERSION).</summary>
    public const uint ExpectedAbiVersion = 1;

    public const byte EventReceived = 1;
    public const byte EventRxStarted = 2;
    public const byte EventSent = 3;
    public const byte EventError = 4;

    public const int Ok = 0;
    public const int ErrNull = -1;
    public const int ErrInvalidArg = -2;
    public const int ErrBusy = -6;

    [ModuleInitializer]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2255", Justification = "Registers the native resolver before the first P/Invoke.")]
    internal static void Init() => NativeLibraryLoader.Register(typeof(NativeMethods).Assembly);

    [LibraryImport(Library, EntryPoint = "iotcom_abi_version")]
    public static partial uint AbiVersion();

    [LibraryImport(Library, EntryPoint = "iotcom_last_error")]
    public static partial int LastError(byte* buf, nuint len);

    [LibraryImport(Library, EntryPoint = "iotcom_isotp_new")]
    public static partial int New(in IsoTpCfgNative cfg, out nint handle);

    [LibraryImport(Library, EntryPoint = "iotcom_isotp_free")]
    public static partial void Free(nint handle);

    [LibraryImport(Library, EntryPoint = "iotcom_isotp_send")]
    public static partial int Send(IsoTpHandle handle, ulong nowUs, byte* data, nuint len);

    [LibraryImport(Library, EntryPoint = "iotcom_isotp_handle_frame")]
    public static partial int HandleFrame(IsoTpHandle handle, ulong nowUs, byte* data, nuint len);

    [LibraryImport(Library, EntryPoint = "iotcom_isotp_handle_timeout")]
    public static partial int HandleTimeout(IsoTpHandle handle, ulong nowUs);

    [LibraryImport(Library, EntryPoint = "iotcom_isotp_reset")]
    public static partial int Reset(IsoTpHandle handle);

    [LibraryImport(Library, EntryPoint = "iotcom_isotp_poll_frame")]
    public static partial int PollFrame(IsoTpHandle handle, byte* buf, nuint cap);

    [LibraryImport(Library, EntryPoint = "iotcom_isotp_poll_event")]
    public static partial int PollEvent(IsoTpHandle handle, out IsoTpEventNative evt);

    [LibraryImport(Library, EntryPoint = "iotcom_isotp_poll_timeout")]
    public static partial int PollTimeout(IsoTpHandle handle, out ulong deadlineUs);

    public static string GetLastError()
    {
        Span<byte> buf = stackalloc byte[512];
        fixed (byte* p = buf)
        {
            var n = LastError(p, (nuint)buf.Length);
            return n <= 0 ? "unknown native error" : System.Text.Encoding.UTF8.GetString(buf[..Math.Min(n, buf.Length)]);
        }
    }

    public static int Check(int status)
    {
        if (status >= 0) return status;
        var message = GetLastError();
        throw status switch
        {
            ErrInvalidArg or ErrNull => new ArgumentException(message),
            ErrBusy => new InvalidOperationException("An ISO-TP transmission is already in progress."),
            _ => new IoTComException($"Native ISO-TP error {status}: {message}"),
        };
    }
}

/// <summary>Owns a native <c>IsoTpChannel*</c>; released exactly once via <c>iotcom_isotp_free</c>.</summary>
internal sealed class IsoTpHandle : SafeHandle
{
    public IsoTpHandle() : base(IntPtr.Zero, ownsHandle: true) { }

    internal IsoTpHandle(nint handle) : this() => SetHandle(handle);

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        NativeMethods.Free(handle);
        return true;
    }
}
