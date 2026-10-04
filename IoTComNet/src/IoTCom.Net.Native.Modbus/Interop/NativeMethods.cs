using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using IoTCom.Net.Native;

namespace IoTCom.Net.Native.Modbus.Interop;

/// <summary>Status codes shared by every IoTCom.Net native library (iotcom-ffi-support).</summary>
internal static class NativeStatus
{
    public const int Ok = 0;
    public const int ErrNull = -1;
    public const int ErrInvalidArg = -2;
    public const int ErrPanic = -3;
    public const int ErrBufferTooSmall = -4;
    public const int ErrProtocol = -5;
    public const int ErrBusy = -6;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ModbusMasterCfg
{
    public byte Framing;
    public uint TimeoutMs;
    public ushort MaxInFlight;
    public ushort MaxQueue;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ModbusEventNative
{
    public byte Kind;
    public byte UnitId;
    public byte ExceptionCode;
    public uint Id;
    public byte* Data;
    public nuint DataLen;
}

/// <summary>Source-generated P/Invoke bindings for <c>iotcom_modbus</c> (AOT and trimming friendly).</summary>
internal static unsafe partial class NativeMethods
{
    public const string Library = "iotcom_modbus";

    /// <summary>ABI version this binding was written for (must match iotcom-ffi-support ABI_VERSION).</summary>
    public const uint ExpectedAbiVersion = 1;

    public const byte EventResponse = 1;
    public const byte EventTimeout = 2;

    [ModuleInitializer]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2255", Justification = "Registers the native resolver before the first P/Invoke.")]
    internal static void Init() => NativeLibraryLoader.Register(typeof(NativeMethods).Assembly);

    [LibraryImport(Library, EntryPoint = "iotcom_abi_version")]
    public static partial uint AbiVersion();

    [LibraryImport(Library, EntryPoint = "iotcom_last_error")]
    public static partial int LastError(byte* buf, nuint len);

    [LibraryImport(Library, EntryPoint = "iotcom_modbus_master_new")]
    public static partial int MasterNew(in ModbusMasterCfg cfg, out nint handle);

    [LibraryImport(Library, EntryPoint = "iotcom_modbus_master_free")]
    public static partial void MasterFree(nint handle);

    [LibraryImport(Library, EntryPoint = "iotcom_modbus_master_submit")]
    public static partial int MasterSubmit(ModbusMasterHandle handle, ulong nowUs, byte unitId, byte* pdu, nuint pduLen, out uint id);

    [LibraryImport(Library, EntryPoint = "iotcom_modbus_master_handle_input")]
    public static partial int MasterHandleInput(ModbusMasterHandle handle, ulong nowUs, byte* data, nuint len);

    [LibraryImport(Library, EntryPoint = "iotcom_modbus_master_handle_timeout")]
    public static partial int MasterHandleTimeout(ModbusMasterHandle handle, ulong nowUs);

    [LibraryImport(Library, EntryPoint = "iotcom_modbus_master_poll_transmit")]
    public static partial int MasterPollTransmit(ModbusMasterHandle handle, byte* buf, nuint cap, out nuint required);

    [LibraryImport(Library, EntryPoint = "iotcom_modbus_master_poll_event")]
    public static partial int MasterPollEvent(ModbusMasterHandle handle, out ModbusEventNative evt);

    [LibraryImport(Library, EntryPoint = "iotcom_modbus_master_poll_timeout")]
    public static partial int MasterPollTimeout(ModbusMasterHandle handle, out ulong deadlineUs);

    [LibraryImport(Library, EntryPoint = "iotcom_modbus_master_pending")]
    public static partial int MasterPending(ModbusMasterHandle handle);

    /// <summary>Reads the calling thread's last error message.</summary>
    public static string GetLastError()
    {
        Span<byte> buf = stackalloc byte[512];
        fixed (byte* p = buf)
        {
            var n = LastError(p, (nuint)buf.Length);
            return n <= 0 ? "unknown native error" : System.Text.Encoding.UTF8.GetString(buf[..Math.Min(n, buf.Length)]);
        }
    }

    /// <summary>Throws the matching exception for a negative status code.</summary>
    public static int Check(int status)
    {
        if (status >= 0) return status;
        var message = GetLastError();
        throw status switch
        {
            NativeStatus.ErrInvalidArg or NativeStatus.ErrNull => new ArgumentException(message),
            NativeStatus.ErrProtocol => new ProtocolException(message),
            NativeStatus.ErrBusy => new IoTComException("Native Modbus master is busy (queue full): " + message),
            NativeStatus.ErrPanic => new IoTComException("Native panic: " + message),
            _ => new IoTComException($"Native error {status}: {message}"),
        };
    }
}

/// <summary>Owns a native <c>ModbusMaster*</c>; released exactly once via <c>iotcom_modbus_master_free</c>.</summary>
internal sealed class ModbusMasterHandle : SafeHandle
{
    public ModbusMasterHandle() : base(IntPtr.Zero, ownsHandle: true) { }

    internal ModbusMasterHandle(nint handle) : this() => SetHandle(handle);

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        NativeMethods.MasterFree(handle);
        return true;
    }
}
