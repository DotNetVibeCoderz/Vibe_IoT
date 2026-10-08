using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Transport.Can.Adapters;

/// <summary>TPCANMsg (classic CAN message) as laid out by PCANBasic.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PcanMessage
{
    public uint Id;
    public byte MsgType;
    public byte Len;
    public fixed byte Data[8];
}

/// <summary>TPCANTimestamp.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PcanTimestamp
{
    public uint Millis;
    public ushort MillisOverflow;
    public ushort Micros;
}

/// <summary>P/Invoke bindings for PEAK-System's PCANBasic (PCANBasic.dll, libpcanbasic.so, libPCBUSB.dylib).</summary>
internal static unsafe partial class PcanNative
{
    public const string Library = "PCANBasic";

    public const uint Ok = 0;
    public const uint ReceiveQueueEmpty = 0x20;
    public const byte ParameterListenOnly = 0x08;

    public const byte TypeStandard = 0x00, TypeRtr = 0x01, TypeExtended = 0x02, TypeStatus = 0x80, TypeErrorFrame = 0x40;

    [ModuleInitializer]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2255", Justification = "Resolves the PCANBasic library name per platform before the first P/Invoke.")]
    internal static void Init() => NativeLibrary.SetDllImportResolver(typeof(PcanNative).Assembly, Resolve);

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (name != Library) return 0;
        string[] candidates = OperatingSystem.IsWindows() ? ["PCANBasic.dll"]
            : OperatingSystem.IsMacOS() ? ["libPCBUSB.dylib", "libPCBUSB.0.dylib"]
            : ["libpcanbasic.so", "libpcanbasic.so.4", "libPCANBasic.so"];
        foreach (var c in candidates)
            if (NativeLibrary.TryLoad(c, assembly, path, out var handle)) return handle;
        return 0;
    }

    [LibraryImport(Library, EntryPoint = "CAN_Initialize")]
    public static partial uint Initialize(ushort channel, ushort btr0btr1, byte hwType, uint ioPort, ushort interrupt);

    [LibraryImport(Library, EntryPoint = "CAN_Uninitialize")]
    public static partial uint Uninitialize(ushort channel);

    [LibraryImport(Library, EntryPoint = "CAN_Read")]
    public static partial uint Read(ushort channel, out PcanMessage message, out PcanTimestamp timestamp);

    [LibraryImport(Library, EntryPoint = "CAN_Write")]
    public static partial uint Write(ushort channel, ref PcanMessage message);

    [LibraryImport(Library, EntryPoint = "CAN_SetValue")]
    public static partial uint SetValue(ushort channel, byte parameter, ref uint value, uint length);

    [LibraryImport(Library, EntryPoint = "CAN_GetErrorText")]
    public static partial uint GetErrorText(uint error, ushort language, byte* buffer);

    public static string ErrorText(uint status)
    {
        var buf = stackalloc byte[256];
        return GetErrorText(status, 0x09, buf) == Ok ? Marshal.PtrToStringAnsi((nint)buf) ?? $"0x{status:X}" : $"PCAN error 0x{status:X}";
    }
}

/// <summary>PEAK channel names, bit-rate codes and message conversion.</summary>
public static class Pcan
{
    /// <summary>BTR0/BTR1 codes of PCANBasic for the standard classic bit rates.</summary>
    public static IReadOnlyDictionary<int, ushort> BitrateCodes { get; } = new Dictionary<int, ushort>
    {
        [1_000_000] = 0x0014, [800_000] = 0x0016, [500_000] = 0x001C, [250_000] = 0x011C, [125_000] = 0x031C, [100_000] = 0x432F,
        [95_000] = 0xC34E, [83_000] = 0x852B, [50_000] = 0x472F, [47_000] = 0x1414, [33_000] = 0x8B2F, [20_000] = 0x532F, [10_000] = 0x672F, [5_000] = 0x7F7F,
    };

    /// <summary>
    /// The PCAN handle for a channel name: <c>usb1</c>–<c>usb16</c> (PCAN_USBBUS1 = 0x51 … 8 = 0x58, 9 = 0x509 … 16 = 0x510).
    /// </summary>
    public static ushort Channel(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var t = name.Trim().ToLowerInvariant();
        if (t.StartsWith("usb", StringComparison.Ordinal) && int.TryParse(t.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 16)
            return (ushort)(n <= 8 ? 0x50 + n : 0x500 + n);
        throw new ArgumentException($"'{name}' is not a PCAN USB channel (usb1 … usb16).", nameof(name));
    }

    internal static unsafe PcanMessage ToNative(in CanFrame frame)
    {
        if (frame.IsFd) throw new NotSupportedException("PCAN classic channels cannot send CAN FD frames.");
        var m = new PcanMessage
        {
            Id = frame.Id,
            MsgType = (byte)((frame.IsExtended ? PcanNative.TypeExtended : 0) | (frame.IsRemote ? PcanNative.TypeRtr : 0)),
            Len = (byte)(frame.IsRemote ? frame.RemoteLength : frame.Data.Length),
        };
        var span = frame.Data.Span;
        for (var i = 0; i < span.Length && i < 8; i++) m.Data[i] = span[i];
        return m;
    }

    internal static unsafe CanFrame? FromNative(in PcanMessage m)
    {
        if ((m.MsgType & PcanNative.TypeStatus) != 0) return null;
        var flags = ((m.MsgType & PcanNative.TypeExtended) != 0 ? CanFrameFlags.Extended : CanFrameFlags.None)
            | ((m.MsgType & PcanNative.TypeRtr) != 0 ? CanFrameFlags.Remote : CanFrameFlags.None)
            | ((m.MsgType & PcanNative.TypeErrorFrame) != 0 ? CanFrameFlags.Error : CanFrameFlags.None);
        var remote = (flags & CanFrameFlags.Remote) != 0;
        var data = new byte[remote ? 0 : Math.Min((int)m.Len, 8)];
        for (var i = 0; i < data.Length; i++) data[i] = m.Data[i];
        return new CanFrame(m.Id, data, flags, remote ? m.Len : 0);
    }

    /// <summary>Encodes a frame as a TPCANMsg image (16 bytes: id, type, length, data, padding) — for tests and tools.</summary>
    public static unsafe byte[] Encode(in CanFrame frame)
    {
        var m = ToNative(frame);
        var b = new byte[sizeof(PcanMessage)];
        fixed (byte* p = b) *(PcanMessage*)p = m;
        return b;
    }

    /// <summary>Decodes a TPCANMsg image; null for status messages.</summary>
    public static unsafe CanFrame? Decode(ReadOnlySpan<byte> image)
    {
        if (image.Length < sizeof(PcanMessage)) throw new FormatException($"TPCANMsg is {sizeof(PcanMessage)} bytes.");
        fixed (byte* p = image) return FromNative(*(PcanMessage*)p);
    }

    /// <summary>True when the PCANBasic library can be loaded (the PEAK driver package is installed).</summary>
    public static bool IsAvailable
    {
        get
        {
            try
            {
                _ = PcanNative.Uninitialize(0);   // PCAN_NONEBUS: harmless, only proves the library loads
                return true;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return false;
            }
        }
    }
}

/// <summary>
/// A PEAK-System PCAN-USB adapter through PCANBasic (<c>pcan:usb1</c>). Classic CAN at the standard bit rates; install
/// the PEAK driver (Windows), the PCAN Linux driver with libpcanbasic, or PCBUSB on macOS.
/// </summary>
public sealed class PcanCanBus : CanBusBase
{
    private readonly ushort _handle;
    private CancellationTokenSource? _cts;
    private Task? _rx;

    /// <summary>Creates a bus on <paramref name="channel"/> (usb1 … usb16).</summary>
    public PcanCanBus(string channel, CanBusOptions? options = null) : base("can-pcan", $"pcan:{channel}", options ?? new CanBusOptions())
        => _handle = Pcan.Channel(channel);

    /// <inheritdoc />
    public override bool SupportsFd => false;

    /// <inheritdoc />
    protected override ValueTask OpenCoreAsync(CancellationToken ct)
    {
        if (Options.Fd) throw new NotSupportedException("PcanCanBus supports classic CAN; CAN FD needs CAN_InitializeFD and is not wrapped yet.");
        if (!Pcan.BitrateCodes.TryGetValue(Options.Bitrate, out var code))
            throw new ArgumentException($"PCAN supports {string.Join(", ", Pcan.BitrateCodes.Keys.Order())} bit/s; {Options.Bitrate} is not one of them.");
        try
        {
            if (Options.ListenOnly)
            {
                var on = 1u;
                _ = PcanNative.SetValue(_handle, PcanNative.ParameterListenOnly, ref on, 4);
            }

            var status = PcanNative.Initialize(_handle, code, 0, 0, 0);
            if (status != PcanNative.Ok) throw new TransportException($"PCAN {Channel}: {PcanNative.ErrorText(status)}");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new PlatformNotSupportedException("PCANBasic was not found. Install the PEAK-System driver package (PCANBasic.dll / libpcanbasic.so / PCBUSB).", ex);
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _rx = Task.Run(() => ReceiveLoop(token), CancellationToken.None);
        Logger.LogInformation("PCAN {Channel} at {Bitrate} bit/s", Channel, Options.Bitrate);
        return ValueTask.CompletedTask;
    }

    private void ReceiveLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var status = PcanNative.Read(_handle, out var m, out _);
            if (status == PcanNative.ReceiveQueueEmpty)
            {
                Thread.Sleep(1);
                continue;
            }

            if (status != PcanNative.Ok)
            {
                if ((status & 0x1400) != 0)   // illegal handle / no driver: the adapter was unplugged
                {
                    Fault(new TransportException($"PCAN {Channel}: {PcanNative.ErrorText(status)}"));
                    return;
                }

                Logger.LogDebug("PCAN {Channel}: {Status}", Channel, PcanNative.ErrorText(status));
                continue;
            }

            if (Pcan.FromNative(m) is { } frame) OnFrameReceived(frame);
        }
    }

    /// <inheritdoc />
    protected override ValueTask SendCoreAsync(CanFrame frame, CancellationToken ct)
    {
        var m = Pcan.ToNative(frame);
        var status = PcanNative.Write(_handle, ref m);
        return status == PcanNative.Ok ? ValueTask.CompletedTask : ValueTask.FromException(new TransportException($"PCAN {Channel}: {PcanNative.ErrorText(status)}"));
    }

    /// <inheritdoc />
    protected override async ValueTask CloseCoreAsync()
    {
        if (_cts is not null) await _cts.CancelAsync().ConfigureAwait(false);
        if (_rx is not null) await _rx.ConfigureAwait(false);
        _ = PcanNative.Uninitialize(_handle);
        _cts?.Dispose();
        _cts = null;
    }
}

/// <summary>Registers the <c>gsusb:</c> and <c>pcan:</c> schemes with <see cref="CanBus.Create"/>.</summary>
public static class CanAdapters
{
    private static int _registered;

    /// <summary>Registers the schemes (idempotent; also runs when this assembly loads).</summary>
    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) != 0) return;
        CanBus.RegisterScheme("gsusb", (target, _, options) =>
        {
            // gsusb: | gsusb:vvvv:pppp[:serial] | …#channel
            var hash = target.LastIndexOf('#');
            var channel = hash >= 0 ? byte.Parse(target.AsSpan(hash + 1), CultureInfo.InvariantCulture) : (byte)0;
            var id = hash >= 0 ? target[..hash] : target;
            return new GsUsbCanBus(string.IsNullOrEmpty(id) ? null : id, channel, options);
        });
        CanBus.RegisterScheme("pcan", (target, _, options) => new PcanCanBus(string.IsNullOrEmpty(target) ? "usb1" : target, options));
    }

    [ModuleInitializer]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2255", Justification = "Makes gsusb: and pcan: available as soon as the package is loaded.")]
    internal static void Init() => Register();

    /// <summary>Describes the adapters this package supports (for CLI and docs).</summary>
    public static string Describe() => new StringBuilder()
        .Append("gsusb:[vvvv:pppp[:serial]][#channel] — candleLight / gs_usb (default 1d50:606f); ")
        .Append("pcan:usbN — PEAK PCAN-USB through PCANBasic").ToString();
}
