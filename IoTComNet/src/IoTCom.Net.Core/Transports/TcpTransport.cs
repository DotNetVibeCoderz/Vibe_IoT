using System.Net;
using System.Net.Sockets;

namespace IoTCom.Net.Transports;

/// <summary>TCP client transport (also used for RTU-over-TCP, NMEA over TCP, etc.).</summary>
public sealed class TcpClientTransport : StreamTransport
{
    private readonly string _host;
    private readonly int _port;
    private readonly TimeSpan _connectTimeout;
    private Socket? _socket;
    private TransportInfo _info;

    /// <summary>Creates a TCP client transport.</summary>
    /// <param name="host">Host name or IP address.</param>
    /// <param name="port">TCP port.</param>
    /// <param name="connectTimeout">Connect timeout (default 5 s).</param>
    public TcpClientTransport(string host, int port, TimeSpan? connectTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        _host = host;
        _port = port;
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(5);
        _info = new TransportInfo(TransportKind.Tcp, null, $"{host}:{port}");
    }

    /// <inheritdoc />
    public override TransportInfo Info => _info;

    /// <inheritdoc />
    protected override async ValueTask<Stream> OpenStreamAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_connectTimeout);
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(_host, _port, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            socket.Dispose();
            throw new IoTComTimeoutException($"Timed out connecting to {_host}:{_port} after {_connectTimeout.TotalMilliseconds:0} ms.");
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            throw new TransportException($"Cannot connect to {_host}:{_port}: {ex.SocketErrorCode}", ex);
        }
        _socket = socket;
        _info = new TransportInfo(TransportKind.Tcp, socket.LocalEndPoint?.ToString(), socket.RemoteEndPoint?.ToString() ?? $"{_host}:{_port}");
        return new NetworkStream(socket, ownsSocket: true);
    }

    /// <inheritdoc />
    protected override ValueTask CloseCoreAsync()
    {
        _socket?.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Server-side transport wrapping an accepted socket.</summary>
public sealed class TcpAcceptedTransport : StreamTransport
{
    private readonly Socket _socket;

    internal TcpAcceptedTransport(Socket socket)
    {
        _socket = socket;
        _socket.NoDelay = true;
        Info = new TransportInfo(TransportKind.Tcp, socket.LocalEndPoint?.ToString(), socket.RemoteEndPoint?.ToString());
    }

    /// <inheritdoc />
    public override TransportInfo Info { get; }

    /// <inheritdoc />
    protected override ValueTask<Stream> OpenStreamAsync(CancellationToken ct) => ValueTask.FromResult<Stream>(new NetworkStream(_socket, ownsSocket: true));
}

/// <summary>TCP listener producing <see cref="TcpAcceptedTransport"/> instances.</summary>
public sealed class TcpTransportListener(IPEndPoint endPoint, int backlog = 128) : ITransportListener
{
    private Socket? _listener;

    /// <summary>Creates a listener on <paramref name="address"/>:<paramref name="port"/> (port 0 = ephemeral).</summary>
    public TcpTransportListener(IPAddress address, int port) : this(new IPEndPoint(address, port)) { }

    /// <inheritdoc />
    public string LocalAddress => _listener?.LocalEndPoint?.ToString() ?? endPoint.ToString();

    /// <summary>Bound endpoint (resolves port 0 after <see cref="StartAsync"/>).</summary>
    public IPEndPoint BoundEndPoint => (IPEndPoint?)_listener?.LocalEndPoint ?? endPoint;

    /// <inheritdoc />
    public ValueTask StartAsync(CancellationToken ct)
    {
        if (_listener is not null) return ValueTask.CompletedTask;
        var s = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            if (endPoint.Address.Equals(IPAddress.IPv6Any)) s.DualMode = true;
            s.Bind(endPoint);
            s.Listen(backlog);
        }
        catch (SocketException ex)
        {
            s.Dispose();
            throw new TransportException($"Cannot listen on {endPoint}: {ex.SocketErrorCode}", ex);
        }
        _listener = s;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask<ITransport> AcceptAsync(CancellationToken ct)
    {
        var listener = _listener ?? throw new InvalidOperationException("Listener not started.");
        var socket = await listener.AcceptAsync(ct).ConfigureAwait(false);
        var t = new TcpAcceptedTransport(socket);
        await t.OpenAsync(ct).ConfigureAwait(false);
        return t;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _listener?.Dispose();
        _listener = null;
        return ValueTask.CompletedTask;
    }
}
