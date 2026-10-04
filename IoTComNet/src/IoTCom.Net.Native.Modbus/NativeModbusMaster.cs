using IoTCom.Net.Native.Modbus.Interop;
using IoTCom.Net.Protocols.Modbus;

namespace IoTCom.Net.Native.Modbus;

/// <summary>An event produced by <see cref="NativeModbusMaster"/>.</summary>
/// <param name="Id">Request id from <see cref="NativeModbusMaster.Submit"/>.</param>
/// <param name="IsTimeout">True when the request expired.</param>
/// <param name="UnitId">Responding unit.</param>
/// <param name="Pdu">Response PDU (empty on timeout).</param>
public readonly record struct NativeModbusEvent(uint Id, bool IsTimeout, byte UnitId, byte[] Pdu);

/// <summary>
/// Thin, allocation-light wrapper over the Rust sans-I/O Modbus master. It performs no I/O: feed it bytes,
/// drain frames and events, and call <see cref="HandleTimeout"/> at <see cref="PollTimeout"/>.
/// Not thread-safe — serialise calls (the <see cref="NativeModbusClient"/> driver does).
/// </summary>
public sealed unsafe class NativeModbusMaster : IDisposable
{
    private readonly ModbusMasterHandle _handle;

    /// <summary>Creates a master.</summary>
    public NativeModbusMaster(ModbusFramingMode framing, TimeSpan timeout, int maxInFlight = 16, int maxQueue = 256)
    {
        EnsureCompatible();
        var cfg = new ModbusMasterCfg
        {
            Framing = (byte)framing,
            TimeoutMs = (uint)Math.Clamp(timeout.TotalMilliseconds, 1, uint.MaxValue),
            MaxInFlight = (ushort)Math.Clamp(maxInFlight, 1, ushort.MaxValue),
            MaxQueue = (ushort)Math.Clamp(maxQueue, 1, ushort.MaxValue),
        };
        NativeMethods.Check(NativeMethods.MasterNew(cfg, out var raw));
        _handle = new ModbusMasterHandle(raw);
    }

    /// <summary>True when the native library can be loaded on this machine.</summary>
    public static bool IsSupported => NativeLibraryLoader.IsAvailable(NativeMethods.Library, typeof(NativeMethods).Assembly);

    /// <summary>ABI version reported by the loaded library.</summary>
    public static uint AbiVersion => NativeMethods.AbiVersion();

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
                $"Use the managed ModbusClient, or set {NativeLibraryLoader.OverrideVariable}.", ex);
        }
        if (abi != NativeMethods.ExpectedAbiVersion)
            throw new PlatformNotSupportedException($"Native ABI mismatch: library reports {abi}, binding expects {NativeMethods.ExpectedAbiVersion}.");
    }

    /// <summary>Requests queued or in flight.</summary>
    public int Pending => NativeMethods.Check(NativeMethods.MasterPending(_handle));

    /// <summary>Queues a request; returns its id.</summary>
    public uint Submit(ulong nowMicros, byte unitId, ReadOnlySpan<byte> pdu)
    {
        fixed (byte* p = pdu)
        {
            NativeMethods.Check(NativeMethods.MasterSubmit(_handle, nowMicros, unitId, p, (nuint)pdu.Length, out var id));
            return id;
        }
    }

    /// <summary>Feeds received bytes.</summary>
    public void HandleInput(ulong nowMicros, ReadOnlySpan<byte> data)
    {
        fixed (byte* p = data) NativeMethods.Check(NativeMethods.MasterHandleInput(_handle, nowMicros, p, (nuint)data.Length));
    }

    /// <summary>Expires timers.</summary>
    public void HandleTimeout(ulong nowMicros) => NativeMethods.Check(NativeMethods.MasterHandleTimeout(_handle, nowMicros));

    /// <summary>Copies the next frame to send into <paramref name="buffer"/>; returns its length (0 = none).</summary>
    public int PollTransmit(Span<byte> buffer)
    {
        fixed (byte* p = buffer) return NativeMethods.Check(NativeMethods.MasterPollTransmit(_handle, p, (nuint)buffer.Length, out _));
    }

    /// <summary>Returns the next event, if any.</summary>
    public bool TryPollEvent(out NativeModbusEvent evt)
    {
        if (NativeMethods.Check(NativeMethods.MasterPollEvent(_handle, out var e)) == 0)
        {
            evt = default;
            return false;
        }
        var pdu = e.DataLen == 0 ? [] : new ReadOnlySpan<byte>(e.Data, checked((int)e.DataLen)).ToArray();
        evt = new NativeModbusEvent(e.Id, e.Kind == NativeMethods.EventTimeout, e.UnitId, pdu);
        return true;
    }

    /// <summary>Next deadline in microseconds, if any.</summary>
    public ulong? PollTimeout() => NativeMethods.Check(NativeMethods.MasterPollTimeout(_handle, out var deadline)) == 1 ? deadline : null;

    /// <inheritdoc />
    public void Dispose() => _handle.Dispose();
}
