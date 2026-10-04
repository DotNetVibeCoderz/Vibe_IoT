using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Protocols.Dmx;

/// <summary>Options shared by <see cref="ArtNetNode"/> and <see cref="SacnNode"/>.</summary>
public sealed class DmxNodeOptions
{
    /// <summary>Local bind address (default any).</summary>
    public IPAddress BindAddress { get; set; } = IPAddress.Any;
    /// <summary>Local port (default: protocol port; 0 = ephemeral, useful in tests).</summary>
    public int? Port { get; set; }
    /// <summary>Default destination. Art-Net: broadcast; sACN: per-universe multicast when null.</summary>
    public IPEndPoint? Destination { get; set; }
    /// <summary>Node / source name.</summary>
    public string Name { get; set; } = "IoTCom.Net";
    /// <summary>Answer ArtPoll (Art-Net only).</summary>
    public bool RespondToPoll { get; set; } = true;
    /// <summary>sACN priority.</summary>
    public byte Priority { get; set; } = SacnPacket.DefaultPriority;
    /// <summary>Stable component id for sACN.</summary>
    public Guid Cid { get; set; } = Guid.NewGuid();
    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }

    /// <summary>Binds to a specific address/port.</summary>
    public DmxNodeOptions Bind(IPAddress address, int port) { BindAddress = address; Port = port; return this; }
    /// <summary>Sends to a fixed destination (unicast).</summary>
    public DmxNodeOptions SendTo(IPEndPoint destination) { Destination = destination; return this; }
    /// <summary>Sets the node/source name.</summary>
    public DmxNodeOptions WithName(string name) { Name = name; return this; }
    /// <summary>Sets the sACN priority.</summary>
    public DmxNodeOptions WithPriority(byte priority) { Priority = priority; return this; }
    /// <summary>Sets the logger.</summary>
    public DmxNodeOptions WithLogger(ILogger logger) { Logger = logger; return this; }
}

/// <summary>Common UDP plumbing: socket, receive loop, subscriptions, per-universe sequence numbers.</summary>
public abstract class DmxNodeBase : EndpointBase, IServerEndpoint, IPublisher<ReadOnlyMemory<byte>>, ISubscriber<ReadOnlyMemory<byte>>
{
    private readonly ConcurrentDictionary<int, byte> _sequence = new();
    private readonly List<Channel<DmxFrame>> _subscribers = [];
    private readonly Lock _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>Creates the base.</summary>
    protected DmxNodeBase(string protocol, DmxNodeOptions options) : base(protocol, options.Logger)
    {
        Options = options;
        Name = options.Name;
    }

    /// <summary>Options.</summary>
    protected DmxNodeOptions Options { get; }

    /// <summary>The bound socket (after start).</summary>
    protected Socket? Socket { get; private set; }

    /// <summary>Protocol default port.</summary>
    protected abstract int DefaultPort { get; }

    /// <summary>Local endpoint after <see cref="StartAsync"/>.</summary>
    public IPEndPoint? LocalEndPoint => Socket?.LocalEndPoint as IPEndPoint;

    /// <summary>Raised for every received DMX frame (on the receive thread).</summary>
    public event Action<DmxFrame>? DmxReceived;

    /// <inheritdoc />
    public ValueTask StartAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (Socket is not null) return ValueTask.CompletedTask;
        SetState(EndpointState.Connecting);
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { EnableBroadcast = true };
        try
        {
            s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            s.Bind(new IPEndPoint(Options.BindAddress, Options.Port ?? DefaultPort));
        }
        catch (SocketException ex)
        {
            s.Dispose();
            SetState(EndpointState.Faulted, ex);
            throw new TransportException($"Cannot bind UDP {Options.BindAddress}:{Options.Port ?? DefaultPort}: {ex.SocketErrorCode}", ex);
        }
        Socket = s;
        OnStarted(s);
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => ReceiveLoopAsync(s, _cts.Token), CancellationToken.None);
        SetState(EndpointState.Listening);
        return ValueTask.CompletedTask;
    }

    /// <summary>Hook after bind (e.g. join multicast groups).</summary>
    protected virtual void OnStarted(Socket socket) { }

    /// <inheritdoc />
    public async ValueTask StopAsync(CancellationToken ct = default)
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts is null) return;
        SetState(EndpointState.Stopping);
        await cts.CancelAsync().ConfigureAwait(false);
        Socket?.Dispose();
        Socket = null;
        if (_loop is not null) await _loop.ConfigureAwait(false);
        cts.Dispose();
        lock (_gate) foreach (var s in _subscribers) s.Writer.TryComplete();
        SetState(EndpointState.Disconnected);
    }

    /// <summary>Sends one DMX frame for <paramref name="universe"/>.</summary>
    public abstract ValueTask SendDmxAsync(int universe, ReadOnlyMemory<byte> data, IPEndPoint? destination = null, CancellationToken ct = default);

    /// <summary>Publishes DMX data; <paramref name="topic"/> is the universe number.</summary>
    public ValueTask PublishAsync(string topic, ReadOnlyMemory<byte> message, PublishOptions? options = null, CancellationToken ct = default)
        => SendDmxAsync(int.Parse(topic, CultureInfo.InvariantCulture), message, null, ct);

    /// <summary>Streams frames of universe <paramref name="filter"/> (a number) or every universe (<c>"*"</c>).</summary>
    public async IAsyncEnumerable<Message<ReadOnlyMemory<byte>>> SubscribeAsync(string filter, [EnumeratorCancellation] CancellationToken ct = default)
    {
        int? universe = filter is "*" or "#" ? null : int.Parse(filter, CultureInfo.InvariantCulture);
        await foreach (var f in ReceiveAsync(universe, ct).ConfigureAwait(false))
            yield return new Message<ReadOnlyMemory<byte>>(f.Universe.ToString(CultureInfo.InvariantCulture), f.Data, DateTimeOffset.UtcNow);
    }

    /// <summary>Streams received frames, optionally for a single universe.</summary>
    public async IAsyncEnumerable<DmxFrame> ReceiveAsync(int? universe = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var ch = Channel.CreateBounded<DmxFrame>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });
        lock (_gate) _subscribers.Add(ch);
        try
        {
            await foreach (var f in ch.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                if (universe is null || f.Universe == universe) yield return f;
        }
        finally
        {
            lock (_gate) _subscribers.Remove(ch);
        }
    }

    /// <summary>Next sequence number for a universe (1..255, wrapping, 0 skipped).</summary>
    protected byte NextSequence(int universe) => _sequence.AddOrUpdate(universe, 1, (_, s) => s == 255 ? (byte)1 : (byte)(s + 1));

    /// <summary>Sends a datagram, tapping it first.</summary>
    protected async ValueTask SendAsync(ReadOnlyMemory<byte> datagram, IPEndPoint destination, Func<string?> summary, CancellationToken ct)
    {
        var s = Socket ?? throw new InvalidOperationException("Node not started. Call StartAsync first.");
        Tap(FrameDirection.Outbound, datagram.Span, summary);
        await s.SendToAsync(datagram, SocketFlags.None, destination, ct).ConfigureAwait(false);
    }

    /// <summary>Delivers a decoded frame to subscribers.</summary>
    protected void Deliver(DmxFrame frame)
    {
        DmxReceived?.Invoke(frame);
        Channel<DmxFrame>[] subs;
        lock (_gate) subs = [.. _subscribers];
        foreach (var s in subs) s.Writer.TryWrite(frame);
    }

    /// <summary>Handles one received datagram.</summary>
    protected abstract ValueTask OnDatagramAsync(ReadOnlyMemory<byte> datagram, IPEndPoint from, CancellationToken ct);

    private async Task ReceiveLoopAsync(Socket socket, CancellationToken ct)
    {
        var buffer = new byte[2048];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var r = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, ct).ConfigureAwait(false);
                await OnDatagramAsync(buffer.AsMemory(0, r.ReceivedBytes), (IPEndPoint)r.RemoteEndPoint, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset) { /* ICMP port unreachable on Windows */ }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "{Protocol} receive error", Protocol);
            }
        }
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore() => await StopAsync().ConfigureAwait(false);
}

/// <summary>
/// Art-Net 4 node: sends and receives ArtDmx, discovers nodes with ArtPoll and answers polls.
/// </summary>
/// <example>
/// <code>
/// await using var node = ArtNetNode.Create(o => o.WithName("Stage left"));
/// await node.StartAsync();
/// await node.SendDmxAsync(universe: 0, rgb);
/// </code>
/// </example>
public sealed class ArtNetNode : DmxNodeBase
{
    private readonly ConcurrentDictionary<IPAddress, ArtNetNodeInfo> _nodes = new();

    private ArtNetNode(DmxNodeOptions options) : base("artnet", options) { }

    /// <summary>Creates a node.</summary>
    public static ArtNetNode Create(Action<DmxNodeOptions>? configure = null)
    {
        var o = new DmxNodeOptions();
        configure?.Invoke(o);
        return new ArtNetNode(o);
    }

    /// <inheritdoc />
    protected override int DefaultPort => ArtNetPacket.Port;

    /// <summary>Nodes discovered through ArtPollReply.</summary>
    public IReadOnlyCollection<ArtNetNodeInfo> Nodes => _nodes.Values.ToArray();

    /// <summary>Raised when a node answers a poll.</summary>
    public event Action<ArtNetNodeInfo>? NodeDiscovered;

    private IPEndPoint DefaultDestination => Options.Destination ?? new IPEndPoint(IPAddress.Broadcast, ArtNetPacket.Port);

    /// <inheritdoc />
    public override ValueTask SendDmxAsync(int universe, ReadOnlyMemory<byte> data, IPEndPoint? destination = null, CancellationToken ct = default)
    {
        var packet = new byte[ArtNetPacket.DmxHeaderLength + 512];
        var n = ArtNetPacket.WriteDmx(packet, universe, data.Span, NextSequence(universe));
        return SendAsync(packet.AsMemory(0, n), destination ?? DefaultDestination, () => string.Create(CultureInfo.InvariantCulture, $"ArtDmx universe={universe} channels={data.Length}"), ct);
    }

    /// <summary>Broadcasts an ArtPoll; replies populate <see cref="Nodes"/>.</summary>
    public ValueTask PollAsync(IPEndPoint? destination = null, CancellationToken ct = default)
    {
        var packet = new byte[14];
        ArtNetPacket.WritePoll(packet);
        return SendAsync(packet, destination ?? DefaultDestination, () => "ArtPoll", ct);
    }

    /// <summary>Sends ArtSync so receivers output buffered frames simultaneously.</summary>
    public ValueTask SyncAsync(IPEndPoint? destination = null, CancellationToken ct = default)
    {
        var packet = new byte[14];
        ArtNetPacket.WriteSync(packet);
        return SendAsync(packet, destination ?? DefaultDestination, () => "ArtSync", ct);
    }

    /// <inheritdoc />
    protected override async ValueTask OnDatagramAsync(ReadOnlyMemory<byte> datagram, IPEndPoint from, CancellationToken ct)
    {
        var span = datagram.Span;
        switch (ArtNetPacket.GetOpCode(span))
        {
            case ArtNetOpCode.Dmx when ArtNetPacket.TryReadDmx(span, out var universe, out var seq, out var data):
                Tap(FrameDirection.Inbound, span, () => string.Create(CultureInfo.InvariantCulture, $"ArtDmx universe={universe} seq={seq} from {from.Address}"));
                Deliver(new DmxFrame(universe, data.ToArray(), seq, from.ToString()));
                break;
            case ArtNetOpCode.Poll when Options.RespondToPoll:
                Tap(FrameDirection.Inbound, span, () => $"ArtPoll from {from.Address}");
                var reply = new byte[ArtNetPacket.PollReplyLength];
                var local = (LocalEndPoint?.Address is { } a && !a.Equals(IPAddress.Any)) ? a : IPAddress.Loopback;
                ArtNetPacket.WritePollReply(reply, local, Options.Name, $"{Options.Name} — {IoTComInfo.Product} {IoTComInfo.Version}", 0);
                await SendAsync(reply, from, () => "ArtPollReply", ct).ConfigureAwait(false);
                break;
            case ArtNetOpCode.PollReply when ArtNetPacket.TryReadPollReply(span, out var info):
                Tap(FrameDirection.Inbound, span, () => $"ArtPollReply {info!.ShortName} @ {from.Address}");
                var keyed = info! with { Address = from.Address };
                _nodes[from.Address] = keyed;
                NodeDiscovered?.Invoke(keyed);
                break;
        }
    }
}

/// <summary>
/// sACN (E1.31) source and receiver. Sends to the universe multicast group (or a unicast destination) and
/// receives universes joined with <see cref="JoinUniverse"/>, dropping out-of-order packets per E1.31 §6.7.2.
/// </summary>
public sealed class SacnNode : DmxNodeBase
{
    private readonly ConcurrentDictionary<int, byte> _lastSequence = new();
    private readonly HashSet<int> _joined = [];

    private SacnNode(DmxNodeOptions options) : base("sacn", options) { }

    /// <summary>Creates a node.</summary>
    public static SacnNode Create(Action<DmxNodeOptions>? configure = null)
    {
        var o = new DmxNodeOptions();
        configure?.Invoke(o);
        return new SacnNode(o);
    }

    /// <inheritdoc />
    protected override int DefaultPort => SacnPacket.Port;

    /// <summary>Joins the multicast group of <paramref name="universe"/> to receive it.</summary>
    public void JoinUniverse(int universe)
    {
        lock (_joined)
        {
            if (!_joined.Add(universe) || Socket is null) return;
            Join(Socket, universe);
        }
    }

    /// <inheritdoc />
    protected override void OnStarted(Socket socket)
    {
        lock (_joined) foreach (var u in _joined) Join(socket, u);
    }

    private void Join(Socket socket, int universe)
    {
        try
        {
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(SacnPacket.MulticastAddress(universe), Options.BindAddress));
        }
        catch (SocketException ex)
        {
            Logger.LogWarning(ex, "Cannot join sACN universe {Universe}", universe);
        }
    }

    /// <inheritdoc />
    public override ValueTask SendDmxAsync(int universe, ReadOnlyMemory<byte> data, IPEndPoint? destination = null, CancellationToken ct = default)
    {
        var packet = new byte[SacnPacket.HeaderLength + 512];
        var n = SacnPacket.WriteData(packet, Options.Cid, Options.Name, universe, data.Span, NextSequence(universe), Options.Priority);
        var to = destination ?? Options.Destination ?? new IPEndPoint(SacnPacket.MulticastAddress(universe), SacnPacket.Port);
        return SendAsync(packet.AsMemory(0, n), to, () => string.Create(CultureInfo.InvariantCulture, $"sACN universe={universe} slots={data.Length} prio={Options.Priority}"), ct);
    }

    /// <inheritdoc />
    protected override ValueTask OnDatagramAsync(ReadOnlyMemory<byte> datagram, IPEndPoint from, CancellationToken ct)
    {
        var span = datagram.Span;
        if (!SacnPacket.TryReadData(span, out var universe, out var seq, out var prio, out var name, out var slots, out var terminated))
            return ValueTask.CompletedTask;
        if (_lastSequence.TryGetValue(universe, out var last) && SacnPacket.IsOutOfOrder(last, seq)) return ValueTask.CompletedTask;
        _lastSequence[universe] = seq;
        if (terminated) return ValueTask.CompletedTask;
        Tap(FrameDirection.Inbound, span, () => string.Create(CultureInfo.InvariantCulture, $"sACN universe={universe} seq={seq} from {name}"));
        Deliver(new DmxFrame(universe, slots.ToArray(), seq, $"{name} ({from.Address})", prio));
        return ValueTask.CompletedTask;
    }
}
