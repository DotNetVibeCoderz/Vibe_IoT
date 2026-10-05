using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Transport.Can;

/// <summary>
/// Linux SocketCAN interface (<c>can0</c>, <c>vcan0</c>, <c>slcan0</c>, …) through a raw <c>CAN_RAW</c> socket.
/// The bit rate is a property of the network interface (<c>ip link set can0 type can bitrate 500000</c>), not of
/// the socket; <see cref="CanBusOptions.Bitrate"/> is ignored here. Requires read/write access to the interface
/// (no root needed for an interface that is already up).
/// </summary>
public sealed partial class SocketCanBus : CanBusBase
{
    private const int AfCan = 29, SockRaw = 3, CanRaw = 1, SolCanRaw = 101;
    private const int CanRawLoopback = 3, CanRawRecvOwnMsgs = 4, CanRawFdFrames = 5;
    private const uint EffFlag = 0x8000_0000, RtrFlag = 0x4000_0000, ErrFlag = 0x2000_0000;
    private const int ClassicSize = 16, FdSize = 72;
    private const short PollIn = 1;

    private readonly string _interface;
    private int _fd = -1;
    private CancellationTokenSource? _cts;
    private Task? _readLoop;

    /// <summary>Creates a bus for the network interface <paramref name="interfaceName"/>.</summary>
    public SocketCanBus(string interfaceName, CanBusOptions? options = null)
        : base("can", $"socketcan:{interfaceName}", options ?? new CanBusOptions())
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(interfaceName);
        _interface = interfaceName;
    }

    /// <summary>True on Linux, the only OS with SocketCAN.</summary>
    public static bool IsSupported => OperatingSystem.IsLinux();

    /// <summary>Names of the CAN network interfaces present (from <c>/sys/class/net/*/type</c> = 280).</summary>
    public static IReadOnlyList<string> ListInterfaces()
    {
        if (!IsSupported || !Directory.Exists("/sys/class/net")) return [];
        var result = new List<string>();
        foreach (var dir in Directory.EnumerateDirectories("/sys/class/net"))
        {
            try
            {
                if (File.ReadAllText(Path.Combine(dir, "type")).Trim() == "280") result.Add(Path.GetFileName(dir));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    /// <inheritdoc />
    protected override ValueTask OpenCoreAsync(CancellationToken ct)
    {
        if (!IsSupported) throw new PlatformNotSupportedException("SocketCAN is only available on Linux. Use slcan: or virtual: on this OS.");
        var index = IfNameToIndex(_interface);
        if (index == 0) throw new TransportException($"CAN interface '{_interface}' not found (try: sudo ip link set {_interface} up type can bitrate 500000).");
        var fd = Socket(AfCan, SockRaw, CanRaw);
        if (fd < 0) throw Errno("socket(PF_CAN)");
        try
        {
            if (Options.Fd) SetOption(fd, CanRawFdFrames, 1);
            SetOption(fd, CanRawLoopback, 1);
            SetOption(fd, CanRawRecvOwnMsgs, 0); // own frames are delivered by the base class when requested
            Span<byte> addr = stackalloc byte[24];
            addr.Clear();
            BinaryPrimitives.WriteUInt16LittleEndian(addr, AfCan);
            BitConverter.TryWriteBytes(addr[4..], index);
            unsafe
            {
                fixed (byte* p = addr)
                {
                    if (Bind(fd, p, addr.Length) < 0) throw Errno($"bind({_interface})");
                }
            }
        }
        catch
        {
            _ = Close(fd);
            throw;
        }
        _fd = fd;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _readLoop = Task.Factory.StartNew(() => ReadLoop(fd, token), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        return ValueTask.CompletedTask;
    }

    private void SetOption(int fd, int option, int value)
    {
        if (SetSockOpt(fd, SolCanRaw, option, ref value, sizeof(int)) < 0) throw Errno($"setsockopt({option})");
    }

    private static TransportException Errno(string call) =>
        new($"{call} failed: errno {Marshal.GetLastPInvokeError()} ({Marshal.GetLastPInvokeErrorMessage()})");

    private unsafe void ReadLoop(int fd, CancellationToken ct)
    {
        var buf = new byte[FdSize];
        var pfd = new PollFd { Fd = fd, Events = PollIn };
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var ready = Poll(ref pfd, 1, 200);
                if (ready == 0) continue;
                if (ready < 0)
                {
                    if (Marshal.GetLastPInvokeError() == 4) continue; // EINTR
                    throw Errno("poll");
                }
                nint n;
                fixed (byte* p = buf) n = Read(fd, p, buf.Length);
                if (n < 0) throw Errno("read");
                if (n is ClassicSize or FdSize) OnFrameReceived(Decode(buf.AsSpan(0, (int)n)));
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Logger.LogWarning(ex, "SocketCAN receive loop on {Interface} stopped", _interface);
            Fault(ex);
        }
    }

    /// <summary>Decodes a <c>struct can_frame</c> (16 bytes) or <c>struct canfd_frame</c> (72 bytes).</summary>
    public static CanFrame Decode(ReadOnlySpan<byte> raw)
    {
        var canId = BinaryPrimitives.ReadUInt32LittleEndian(raw);
        var flags = (canId & EffFlag) != 0 ? CanFrameFlags.Extended : CanFrameFlags.None;
        if ((canId & ErrFlag) != 0) flags |= CanFrameFlags.Error;
        var id = canId & ((canId & EffFlag) != 0 ? CanFrame.MaxExtendedId : CanFrame.MaxStandardId);
        var len = raw[4];
        if (raw.Length == FdSize)
        {
            flags |= CanFrameFlags.Fd;
            if ((raw[5] & 1) != 0) flags |= CanFrameFlags.BitRateSwitch;
            if ((raw[5] & 2) != 0) flags |= CanFrameFlags.ErrorStateIndicator;
            return new CanFrame(id, raw.Slice(8, Math.Min((int)len, 64)).ToArray(), flags);
        }
        if ((canId & RtrFlag) != 0) return new CanFrame(id, ReadOnlyMemory<byte>.Empty, flags | CanFrameFlags.Remote, Math.Min((int)len, 8));
        return new CanFrame(id, raw.Slice(8, Math.Min((int)len, 8)).ToArray(), flags);
    }

    /// <summary>Encodes a frame into the SocketCAN layout; returns the number of bytes written (16 or 72).</summary>
    public static int Encode(in CanFrame frame, Span<byte> raw)
    {
        var size = frame.IsFd ? FdSize : ClassicSize;
        raw[..size].Clear();
        var canId = frame.Id | (frame.IsExtended ? EffFlag : 0) | (frame.IsRemote ? RtrFlag : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(raw, canId);
        raw[4] = (byte)(frame.IsRemote ? frame.RemoteLength : frame.Data.Length);
        if (frame.IsFd)
            raw[5] = (byte)(0x04 | ((frame.Flags & CanFrameFlags.BitRateSwitch) != 0 ? 1 : 0) | ((frame.Flags & CanFrameFlags.ErrorStateIndicator) != 0 ? 2 : 0));
        frame.Data.Span.CopyTo(raw[8..]);
        return size;
    }

    /// <inheritdoc />
    protected override ValueTask SendCoreAsync(CanFrame frame, CancellationToken ct)
    {
        Span<byte> raw = stackalloc byte[FdSize];
        var size = Encode(frame, raw);
        unsafe
        {
            fixed (byte* p = raw)
            {
                // Retry briefly on ENOBUFS (105): the interface TX queue is full.
                for (var attempt = 0; ; attempt++)
                {
                    if (Write(_fd, p, size) == size) break;
                    var errno = Marshal.GetLastPInvokeError();
                    if (errno != 105 || attempt >= 50) throw Errno($"write({_interface})");
                    Thread.Sleep(1);
                }
            }
        }
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override async ValueTask CloseCoreAsync()
    {
        if (_cts is not null) await _cts.CancelAsync().ConfigureAwait(false);
        if (_readLoop is not null) await _readLoop.ConfigureAwait(false);
        if (_fd >= 0) _ = Close(_fd);
        _cts?.Dispose();
        (_fd, _cts, _readLoop) = (-1, null, null);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    [LibraryImport("libc", EntryPoint = "socket", SetLastError = true)]
    private static partial int Socket(int domain, int type, int protocol);

    [LibraryImport("libc", EntryPoint = "bind", SetLastError = true)]
    private static unsafe partial int Bind(int fd, byte* addr, int len);

    [LibraryImport("libc", EntryPoint = "setsockopt", SetLastError = true)]
    private static partial int SetSockOpt(int fd, int level, int name, ref int value, int len);

    [LibraryImport("libc", EntryPoint = "if_nametoindex", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int IfNameToIndex(string name);

    [LibraryImport("libc", EntryPoint = "poll", SetLastError = true)]
    private static partial int Poll(ref PollFd fds, nuint count, int timeoutMs);

    [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
    private static unsafe partial nint Read(int fd, byte* buf, nint count);

    [LibraryImport("libc", EntryPoint = "write", SetLastError = true)]
    private static unsafe partial nint Write(int fd, byte* buf, nint count);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int Close(int fd);
}
