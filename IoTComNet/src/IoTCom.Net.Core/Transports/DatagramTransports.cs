using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace IoTCom.Net.Transports;

/// <summary>UDP datagram transport (IPv4 or IPv6), optionally joined to multicast groups.</summary>
public sealed class UdpDatagramTransport : IDatagramTransport
{
    private readonly Socket _socket;
    private readonly byte[] _buffer = new byte[65_535];

    /// <summary>Binds to <paramref name="local"/> (port 0 = any free port).</summary>
    public UdpDatagramTransport(IPEndPoint local)
    {
        ArgumentNullException.ThrowIfNull(local);
        _socket = new Socket(local.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Ignore ICMP "port unreachable" (otherwise ReceiveFrom fails with ConnectionReset).
                const int SioUdpConnReset = unchecked((int)0x9800000C);
                _socket.IOControl(SioUdpConnReset, [0], null);
            }
            _socket.Bind(local);
        }
        catch (SocketException ex)
        {
            _socket.Dispose();
            throw new TransportException($"Cannot bind UDP {local}: {ex.SocketErrorCode}", ex);
        }
    }

    /// <inheritdoc />
    public EndPoint LocalEndPoint => _socket.LocalEndPoint!;

    /// <summary>Joins an IPv4 or IPv6 multicast group (e.g. CoAP "All CoAP Nodes" 224.0.1.187).</summary>
    public void JoinMulticastGroup(IPAddress group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (group.AddressFamily == AddressFamily.InterNetworkV6)
            _socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.AddMembership, new IPv6MulticastOption(group));
        else
            _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(group));
    }

    /// <inheritdoc />
    public async ValueTask SendAsync(ReadOnlyMemory<byte> data, EndPoint remote, CancellationToken ct = default) =>
        await _socket.SendToAsync(data, SocketFlags.None, remote, ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask<Datagram> ReceiveAsync(CancellationToken ct = default)
    {
        var any = _socket.AddressFamily == AddressFamily.InterNetworkV6 ? new IPEndPoint(IPAddress.IPv6Any, 0) : new IPEndPoint(IPAddress.Any, 0);
        while (true)
        {
            try
            {
                var r = await _socket.ReceiveFromAsync(_buffer, SocketFlags.None, any, ct).ConfigureAwait(false);
                return new Datagram(_buffer.AsSpan(0, r.ReceivedBytes).ToArray(), r.RemoteEndPoint);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset) { }
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _socket.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// An in-process datagram network for tests, notebooks and the Gallery. Endpoints are addressed by <see cref="IPEndPoint"/>;
/// <see cref="LossRate"/> and <see cref="DuplicateRate"/> simulate an unreliable link so retransmission logic can be exercised.
/// </summary>
public sealed class InMemoryDatagramNetwork
{
    private readonly ConcurrentDictionary<IPEndPoint, InMemoryDatagramTransport> _bound = new();
    private readonly Random _random;
    private readonly Lock _randomGate = new();
    private int _nextPort = 49_152;
    private long _dropped, _delivered;

    /// <summary>Creates a network (the seed makes loss deterministic).</summary>
    public InMemoryDatagramNetwork(int seed = 1) => _random = new Random(seed);

    /// <summary>Probability (0–1) that a datagram is silently dropped.</summary>
    public double LossRate { get; set; }

    /// <summary>Probability (0–1) that a datagram is delivered twice.</summary>
    public double DuplicateRate { get; set; }

    /// <summary>Datagrams dropped by <see cref="LossRate"/> or sent to nobody.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Datagrams delivered.</summary>
    public long Delivered => Interlocked.Read(ref _delivered);

    /// <summary>Raised for every datagram sent on the network: sender, destination, data and whether it was delivered.</summary>
    public event Action<IPEndPoint, EndPoint, ReadOnlyMemory<byte>, bool>? Transmitted;

    /// <summary>Binds an endpoint (null or port 0 = next free port on 127.0.0.1).</summary>
    public InMemoryDatagramTransport Bind(IPEndPoint? local = null)
    {
        if (local is null || local.Port == 0)
            local = new IPEndPoint(local?.Address ?? IPAddress.Loopback, Interlocked.Increment(ref _nextPort));
        var t = new InMemoryDatagramTransport(this, local);
        if (!_bound.TryAdd(local, t)) throw new TransportException($"{local} is already bound on this in-memory network.");
        return t;
    }

    internal void Unbind(InMemoryDatagramTransport t) => _bound.TryRemove(t.Local, out _);

    private bool Chance(double p)
    {
        if (p <= 0) return false;
        lock (_randomGate) return _random.NextDouble() < p;
    }

    internal void Deliver(IPEndPoint from, ReadOnlyMemory<byte> data, EndPoint to)
    {
        if (to is not IPEndPoint target || !_bound.TryGetValue(target, out var dest) || Chance(LossRate))
        {
            Interlocked.Increment(ref _dropped);
            Transmitted?.Invoke(from, to, data, false);
            return;
        }
        var copy = data.ToArray();
        Transmitted?.Invoke(from, to, copy, true);
        dest.Enqueue(new Datagram(copy, from));
        Interlocked.Increment(ref _delivered);
        if (Chance(DuplicateRate)) dest.Enqueue(new Datagram(copy, from));
    }
}

/// <summary>An endpoint on an <see cref="InMemoryDatagramNetwork"/>.</summary>
public sealed class InMemoryDatagramTransport : IDatagramTransport
{
    private readonly InMemoryDatagramNetwork _network;
    private readonly Channel<Datagram> _inbox = Channel.CreateUnbounded<Datagram>();

    internal InMemoryDatagramTransport(InMemoryDatagramNetwork network, IPEndPoint local)
    {
        _network = network;
        Local = local;
    }

    internal IPEndPoint Local { get; }

    /// <inheritdoc />
    public EndPoint LocalEndPoint => Local;

    internal void Enqueue(Datagram d) => _inbox.Writer.TryWrite(d);

    /// <inheritdoc />
    public ValueTask SendAsync(ReadOnlyMemory<byte> data, EndPoint remote, CancellationToken ct = default)
    {
        _network.Deliver(Local, data, remote);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<Datagram> ReceiveAsync(CancellationToken ct = default) => _inbox.Reader.ReadAsync(ct);

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _network.Unbind(this);
        _inbox.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Datagram transport extensions available to every datagram endpoint builder.</summary>
public static class DatagramBuilderExtensions
{
    /// <summary>Binds a UDP socket (port 0 = any free port; clients usually leave it at 0).</summary>
    public static T UseUdp<T>(this T builder, int localPort = 0, IPAddress? address = null) where T : IDatagramBuilder<T>
        => builder.UseDatagramTransport(() => new UdpDatagramTransport(new IPEndPoint(address ?? IPAddress.Any, localPort)));

    /// <summary>Binds on an in-process network (<paramref name="local"/> null = next free address).</summary>
    public static T UseInMemory<T>(this T builder, InMemoryDatagramNetwork network, IPEndPoint? local = null) where T : IDatagramBuilder<T>
        => builder.UseDatagramTransport(() => network.Bind(local));
}
