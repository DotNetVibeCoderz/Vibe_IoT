using IoTCom.Net.Native;
using IoTCom.Net.Protocols.IsoTp.Interop;

namespace IoTCom.Net.Protocols.IsoTp;

/// <summary>Why an ISO-TP transfer failed (values match the Rust <c>IsoTpError</c>).</summary>
public enum IsoTpError
{
    /// <summary>No flow control within N_Bs (we were sending).</summary>
    TimeoutBs = 1,
    /// <summary>No consecutive frame within N_Cr (we were receiving).</summary>
    TimeoutCr = 2,
    /// <summary>Consecutive frame with a wrong sequence number.</summary>
    WrongSequence = 3,
    /// <summary>The receiver answered FC.OVFLW (message too large for it).</summary>
    Overflow = 4,
    /// <summary>Too many FC.WAIT frames.</summary>
    TooManyWaits = 5,
    /// <summary>Flow control with an invalid status.</summary>
    InvalidFlowStatus = 6,
    /// <summary>A new message interrupted a reception.</summary>
    Interrupted = 7,
    /// <summary>The incoming message exceeds the configured maximum (FC.OVFLW sent).</summary>
    RxTooLarge = 8,
}

/// <summary>Kind of <see cref="IsoTpEvent"/>.</summary>
public enum IsoTpEventKind
{
    /// <summary>A complete message arrived (<see cref="IsoTpEvent.Data"/>).</summary>
    Received = 1,
    /// <summary>A first frame announced <see cref="IsoTpEvent.Value"/> bytes.</summary>
    RxStarted = 2,
    /// <summary>The message being sent has been fully handed to the bus.</summary>
    Sent = 3,
    /// <summary>A transfer failed (<see cref="IsoTpEvent.Error"/>).</summary>
    Error = 4,
}

/// <summary>An event from <see cref="IsoTpMachine"/>.</summary>
/// <param name="Kind">Kind.</param>
/// <param name="Value">Announced length or error code.</param>
/// <param name="Data">Received message (empty otherwise).</param>
public readonly record struct IsoTpEvent(IsoTpEventKind Kind, uint Value, byte[] Data)
{
    /// <summary>The error for <see cref="IsoTpEventKind.Error"/>.</summary>
    public IsoTpError Error => (IsoTpError)Value;

    /// <summary>True for errors that abort a transmission (as opposed to a reception).</summary>
    public bool IsTransmitError => Kind == IsoTpEventKind.Error && Error is IsoTpError.TimeoutBs or IsoTpError.Overflow or IsoTpError.TooManyWaits or IsoTpError.InvalidFlowStatus;
}

/// <summary>
/// Thin wrapper over the Rust sans-I/O ISO-TP machine (<c>iotcom_isotp</c>). It performs no I/O: feed received
/// frame payloads, drain frames to send and events, and call <see cref="HandleTimeout"/> at <see cref="PollTimeout"/>.
/// Not thread-safe — <see cref="IsoTpChannel"/> serialises calls.
/// </summary>
public sealed unsafe class IsoTpMachine : IDisposable
{
    private readonly IsoTpHandle _handle;

    /// <summary>Creates a machine from <paramref name="options"/>.</summary>
    public IsoTpMachine(IsoTpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        EnsureCompatible();
        var cfg = new IsoTpCfgNative
        {
            TxDl = (byte)options.EffectiveTxDataLength,
            Padding = options.Padding ?? 0xCC,
            Flags = (byte)((options.Padding is null ? 0 : 1) | (options.TxAddressExtension is null ? 0 : 2) | (options.RxAddressExtension is null ? 0 : 4)),
            ExtAddrTx = options.TxAddressExtension ?? 0,
            ExtAddrRx = options.RxAddressExtension ?? 0,
            BlockSize = options.BlockSize,
            StMin = options.SeparationTime,
            MaxWaitFrames = options.MaxWaitFrames,
            NBsMs = (uint)Math.Clamp(options.FlowControlTimeout.TotalMilliseconds, 1, uint.MaxValue),
            NCrMs = (uint)Math.Clamp(options.ConsecutiveFrameTimeout.TotalMilliseconds, 1, uint.MaxValue),
            MaxRxLen = (uint)options.MaxMessageLength,
        };
        NativeMethods.Check(NativeMethods.New(cfg, out var raw));
        _handle = new IsoTpHandle(raw);
    }

    /// <summary>True when the native library can be loaded on this machine.</summary>
    public static bool IsSupported => NativeLibraryLoader.IsAvailable(NativeMethods.Library, typeof(NativeMethods).Assembly);

    /// <summary>Throws when the native library is missing or has an incompatible ABI.</summary>
    public static void EnsureCompatible()
    {
        uint abi;
        try
        {
            abi = NativeMethods.AbiVersion();
        }
        catch (DllNotFoundException ex)
        {
            throw new PlatformNotSupportedException(
                $"Native library '{NativeMethods.Library}' for {NativeLibraryLoader.RuntimeIdentifier} was not found. " +
                $"Install the IoTCom.Net.Protocols.IsoTp package for a supported RID, or set {NativeLibraryLoader.OverrideVariable}.", ex);
        }
        if (abi != NativeMethods.ExpectedAbiVersion)
            throw new PlatformNotSupportedException($"Native ABI mismatch: library reports {abi}, binding expects {NativeMethods.ExpectedAbiVersion}.");
    }

    /// <summary>Starts sending a message (throws <see cref="InvalidOperationException"/> while another one is in progress).</summary>
    public void Send(ulong nowMicros, ReadOnlySpan<byte> message)
    {
        fixed (byte* p = message) NativeMethods.Check(NativeMethods.Send(_handle, nowMicros, p, (nuint)message.Length));
    }

    /// <summary>Feeds the payload of one received CAN frame.</summary>
    public void HandleFrame(ulong nowMicros, ReadOnlySpan<byte> frame)
    {
        fixed (byte* p = frame) NativeMethods.Check(NativeMethods.HandleFrame(_handle, nowMicros, p, (nuint)frame.Length));
    }

    /// <summary>Processes expired timers and paces consecutive frames.</summary>
    public void HandleTimeout(ulong nowMicros) => NativeMethods.Check(NativeMethods.HandleTimeout(_handle, nowMicros));

    /// <summary>Aborts the current transfers.</summary>
    public void Reset() => NativeMethods.Check(NativeMethods.Reset(_handle));

    /// <summary>Next CAN frame payload to send, or null.</summary>
    public byte[]? PollFrame()
    {
        Span<byte> buf = stackalloc byte[64];
        fixed (byte* p = buf)
        {
            var n = NativeMethods.Check(NativeMethods.PollFrame(_handle, p, (nuint)buf.Length));
            return n == 0 ? null : buf[..n].ToArray();
        }
    }

    /// <summary>Next event, or null.</summary>
    public IsoTpEvent? PollEvent()
    {
        if (NativeMethods.Check(NativeMethods.PollEvent(_handle, out var e)) == 0) return null;
        var data = e.Kind == NativeMethods.EventReceived ? new ReadOnlySpan<byte>(e.Data, checked((int)e.DataLen)).ToArray() : [];
        return new IsoTpEvent((IsoTpEventKind)e.Kind, e.Value, data);
    }

    /// <summary>Next deadline in microseconds, or null.</summary>
    public ulong? PollTimeout() => NativeMethods.Check(NativeMethods.PollTimeout(_handle, out var d)) == 1 ? d : null;

    /// <inheritdoc />
    public void Dispose() => _handle.Dispose();
}
