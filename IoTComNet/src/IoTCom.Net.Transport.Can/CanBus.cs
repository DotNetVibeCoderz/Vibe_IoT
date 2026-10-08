using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Transport.Can;

/// <summary>Options shared by every CAN backend.</summary>
public sealed class CanBusOptions
{
    /// <summary>Nominal (arbitration) bit rate in bit/s. Applied by backends that configure the controller (slcan).</summary>
    public int Bitrate { get; set; } = 500_000;

    /// <summary>CAN FD data-phase bit rate in bit/s.</summary>
    public int DataBitrate { get; set; } = 2_000_000;

    /// <summary>Enables CAN FD frames.</summary>
    public bool Fd { get; set; }

    /// <summary>Opens the controller in listen-only (silent) mode; <see cref="ICanBus.SendAsync"/> then fails.</summary>
    public bool ListenOnly { get; set; }

    /// <summary>Delivers our own transmitted frames to our readers too (like SocketCAN's <c>CAN_RAW_RECV_OWN_MSGS</c>).</summary>
    public bool ReceiveOwnMessages { get; set; }

    /// <summary>Capacity of each reader queue; when full the oldest frame is dropped and counted.</summary>
    public int ReaderCapacity { get; set; } = 4096;

    /// <summary>Friendly name for logs and the traffic tap.</summary>
    public string? Name { get; set; }

    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }
}

/// <summary>
/// A CAN / CAN FD bus. <see cref="IClientEndpoint.ConnectAsync"/> opens the interface; frames are sent with
/// <see cref="SendAsync"/> and received through filtered <see cref="CanReader"/>s or <see cref="FrameReceived"/>.
/// </summary>
public interface ICanBus : IClientEndpoint
{
    /// <summary>Interface description, e.g. <c>socketcan:can0</c>, <c>slcan:COM5</c>, <c>virtual:demo</c>.</summary>
    string Channel { get; }

    /// <summary>True when CAN FD frames can be sent and received.</summary>
    bool SupportsFd { get; }

    /// <summary>Queues a frame for transmission.</summary>
    ValueTask SendAsync(CanFrame frame, CancellationToken ct = default);

    /// <summary>Creates a reader receiving every frame that passes <paramref name="filter"/>. Dispose it to detach.</summary>
    CanReader OpenReader(CanFilter? filter = null);

    /// <summary>Raised on the receive thread for every received frame. Keep handlers short.</summary>
    event Action<CanFrame>? FrameReceived;
}

/// <summary>A filtered, buffered view of received frames.</summary>
public sealed class CanReader : IDisposable
{
    private readonly Channel<CanFrame> _channel;
    private readonly Action<CanReader> _detach;
    private long _dropped;

    internal CanReader(CanFilter filter, int capacity, Action<CanReader> detach)
    {
        Filter = filter;
        _detach = detach;
        _channel = Channel.CreateBounded<CanFrame>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = true,
        }, _ => Interlocked.Increment(ref _dropped));
    }

    /// <summary>The acceptance filter.</summary>
    public CanFilter Filter { get; }

    /// <summary>Frames dropped because the reader was full.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    internal void Offer(in CanFrame frame)
    {
        if (Filter.Matches(frame)) _channel.Writer.TryWrite(frame);
    }

    internal void Complete(Exception? error = null) => _channel.Writer.TryComplete(error);

    /// <summary>Waits for the next frame.</summary>
    public ValueTask<CanFrame> ReadAsync(CancellationToken ct = default) => _channel.Reader.ReadAsync(ct);

    /// <summary>Returns a frame if one is buffered.</summary>
    public bool TryRead(out CanFrame frame) => _channel.Reader.TryRead(out frame);

    /// <summary>Waits for the next frame or returns null after <paramref name="timeout"/>.</summary>
    public async ValueTask<CanFrame?> ReadAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            return await _channel.Reader.ReadAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Streams frames until the bus closes or <paramref name="ct"/> is cancelled.</summary>
    public IAsyncEnumerable<CanFrame> ReadAllAsync(CancellationToken ct = default) => _channel.Reader.ReadAllAsync(ct);

    /// <summary>Detaches the reader.</summary>
    public void Dispose()
    {
        _detach(this);
        Complete();
    }
}

/// <summary>
/// Base class for CAN backends: endpoint state, reader fan-out, traffic tap and counters. Backends implement
/// <see cref="OpenCoreAsync"/>, <see cref="CloseCoreAsync"/> and <see cref="SendCoreAsync"/>, and call
/// <see cref="OnFrameReceived"/> for every frame from the interface.
/// </summary>
public abstract class CanBusBase : EndpointBase, ICanBus
{
    private readonly ConcurrentDictionary<CanReader, byte> _readers = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private long _sent, _received;

    /// <summary>Creates the base.</summary>
    protected CanBusBase(string protocol, string channel, CanBusOptions options) : base(protocol, options.Logger)
    {
        Channel = channel;
        Options = options;
        Name = options.Name ?? channel;
    }

    /// <inheritdoc />
    public string Channel { get; }

    /// <summary>Options.</summary>
    public CanBusOptions Options { get; }

    /// <inheritdoc />
    public virtual bool SupportsFd => Options.Fd;

    /// <summary>Frames sent.</summary>
    public long FramesSent => Interlocked.Read(ref _sent);

    /// <summary>Frames received.</summary>
    public long FramesReceived => Interlocked.Read(ref _received);

    /// <inheritdoc />
    public event Action<CanFrame>? FrameReceived;

    /// <inheritdoc />
    public async ValueTask ConnectAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (State == EndpointState.Connected) return;
            SetState(EndpointState.Connecting);
            try
            {
                await OpenCoreAsync(ct).ConfigureAwait(false);
                SetState(EndpointState.Connected);
            }
            catch (Exception ex)
            {
                SetState(EndpointState.Faulted, ex);
                throw;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisconnectAsync(CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (State == EndpointState.Disconnected) return;
            SetState(EndpointState.Stopping);
            await CloseCoreAsync().ConfigureAwait(false);
            SetState(EndpointState.Disconnected);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask SendAsync(CanFrame frame, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (State != EndpointState.Connected) throw new InvalidOperationException($"CAN bus {Channel} is not open; call ConnectAsync first.");
        if (Options.ListenOnly) throw new InvalidOperationException($"CAN bus {Channel} is open in listen-only mode.");
        if (frame.IsFd && !SupportsFd) throw new NotSupportedException($"CAN bus {Channel} is not configured for CAN FD (set Fd = true).");
        await SendCoreAsync(frame, ct).ConfigureAwait(false);
        Interlocked.Increment(ref _sent);
        Tap(FrameDirection.Outbound, ToTapBytes(frame), frame.ToString);
        if (Options.ReceiveOwnMessages) Deliver(frame.WithTimestamp(DateTimeOffset.UtcNow));
    }

    /// <inheritdoc />
    public CanReader OpenReader(CanFilter? filter = null)
    {
        ThrowIfDisposed();
        var reader = new CanReader(filter ?? CanFilter.All, Options.ReaderCapacity, r => _readers.TryRemove(r, out _));
        _readers[reader] = 0;
        return reader;
    }

    /// <summary>Streams every frame that passes <paramref name="filter"/>.</summary>
    public async IAsyncEnumerable<CanFrame> ReadAllAsync(CanFilter? filter = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var reader = OpenReader(filter);
        await foreach (var f in reader.ReadAllAsync(ct).ConfigureAwait(false)) yield return f;
    }

    /// <summary>Called by backends for every frame received from the interface.</summary>
    protected void OnFrameReceived(CanFrame frame)
    {
        if (frame.Timestamp == default) frame = frame.WithTimestamp(DateTimeOffset.UtcNow);
        Interlocked.Increment(ref _received);
        Tap(FrameDirection.Inbound, ToTapBytes(frame), frame.ToString);
        Deliver(frame);
    }

    private void Deliver(in CanFrame frame)
    {
        foreach (var r in _readers.Keys) r.Offer(frame);
        try
        {
            FrameReceived?.Invoke(frame);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "CAN FrameReceived handler failed on {Channel}", Channel);
        }
    }

    /// <summary>Faults the bus (e.g. the adapter was unplugged) and completes every reader with the error.</summary>
    protected void Fault(Exception error)
    {
        SetState(EndpointState.Faulted, error);
        foreach (var r in _readers.Keys) r.Complete(error);
    }

    /// <summary>
    /// Wire image used by the traffic tap: the SocketCAN layout (4-byte big-endian identifier with
    /// EFF/RTR/ERR flag bits, length, flags, 2 reserved bytes, data).
    /// </summary>
    public static byte[] ToTapBytes(in CanFrame frame)
    {
        var buf = new byte[8 + frame.Data.Length];
        var id = frame.Id | (frame.IsExtended ? 0x8000_0000u : 0) | (frame.IsRemote ? 0x4000_0000u : 0) | (frame.IsError ? 0x2000_0000u : 0);
        BinaryPrimitives.WriteUInt32BigEndian(buf, id);
        buf[4] = (byte)(frame.IsRemote ? frame.RemoteLength : frame.Data.Length);
        buf[5] = (byte)(frame.IsFd ? 0x04 | ((frame.Flags & CanFrameFlags.BitRateSwitch) != 0 ? 1 : 0) | ((frame.Flags & CanFrameFlags.ErrorStateIndicator) != 0 ? 2 : 0) : 0);
        frame.Data.Span.CopyTo(buf.AsSpan(8));
        return buf;
    }

    /// <summary>Opens the interface and starts receiving.</summary>
    protected abstract ValueTask OpenCoreAsync(CancellationToken ct);

    /// <summary>Stops receiving and closes the interface.</summary>
    protected abstract ValueTask CloseCoreAsync();

    /// <summary>Transmits one frame.</summary>
    protected abstract ValueTask SendCoreAsync(CanFrame frame, CancellationToken ct);

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore()
    {
        if (State is EndpointState.Connected or EndpointState.Connecting or EndpointState.Faulted)
        {
            try
            {
                await CloseCoreAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Closing CAN bus {Channel}", Channel);
            }
        }
        foreach (var r in _readers.Keys) r.Complete();
        _readers.Clear();
        _lifecycle.Dispose();
    }
}

/// <summary>Opens CAN interfaces by URI.</summary>
public static class CanBus
{
    private static readonly ConcurrentDictionary<string, Func<string, string, CanBusOptions, ICanBus>> Schemes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers a URI scheme (e.g. <c>gsusb</c>, <c>pcan</c>) implemented in another package. The factory receives the
    /// part after the colon, the whole URI and the options.
    /// </summary>
    public static void RegisterScheme(string scheme, Func<string, string, CanBusOptions, ICanBus> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scheme);
        ArgumentNullException.ThrowIfNull(factory);
        Schemes[scheme] = factory;
    }

    /// <summary>Schemes registered by adapter packages.</summary>
    public static IReadOnlyCollection<string> RegisteredSchemes => [.. Schemes.Keys];

    /// <summary>
    /// Creates (but does not open) a bus:
    /// <list type="bullet">
    /// <item><c>virtual:&lt;name&gt;</c> — in-process bus shared by every node with the same name;</item>
    /// <item><c>socketcan:&lt;ifname&gt;</c> — Linux SocketCAN (<c>can0</c>, <c>vcan0</c>);</item>
    /// <item><c>slcan:&lt;serial port&gt;</c> — Lawicel/slcan USB adapters (CANable, CANtact, USBtin…);</item>
    /// <item><c>slcan-tcp:&lt;host&gt;:&lt;port&gt;</c> — slcan over TCP (serial servers, simulators).</item>
    /// </list>
    /// </summary>
    public static ICanBus Create(string uri, Action<CanBusOptions>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        var options = new CanBusOptions();
        configure?.Invoke(options);
        var colon = uri.IndexOf(':', StringComparison.Ordinal);
        var scheme = colon < 0 ? uri : uri[..colon];
        var target = colon < 0 ? "" : uri[(colon + 1)..];
        return scheme.ToLowerInvariant() switch
        {
            "virtual" or "vcan" => new VirtualCanBus(string.IsNullOrEmpty(target) ? "default" : target, options),
            "socketcan" => new SocketCanBus(target, options),
            "slcan" => new SlcanBus(SlcanBus.SerialFactory(target), $"slcan:{target}", options),
            "slcan-tcp" => new SlcanBus(SlcanBus.TcpFactory(target), uri, options),
            _ when Schemes.TryGetValue(scheme, out var factory) => factory(target, uri, options),
            _ => throw new ArgumentException($"Unknown CAN interface '{uri}'. Use virtual:, socketcan:, slcan:, slcan-tcp:{(Schemes.IsEmpty ? "" : ", " + string.Join(", ", Schemes.Keys.Order(StringComparer.Ordinal).Select(k => k + ":")))} (gsusb: and pcan: need IoTCom.Net.Transport.Can.Adapters).", nameof(uri)),
        };
    }

    /// <summary>Creates and opens a bus.</summary>
    public static async Task<ICanBus> OpenAsync(string uri, Action<CanBusOptions>? configure = null, CancellationToken ct = default)
    {
        var bus = Create(uri, configure);
        try
        {
            await bus.ConnectAsync(ct).ConfigureAwait(false);
            return bus;
        }
        catch
        {
            await bus.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
