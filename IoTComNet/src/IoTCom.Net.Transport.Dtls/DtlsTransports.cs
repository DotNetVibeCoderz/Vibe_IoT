using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Org.BouncyCastle.Tls;

namespace IoTCom.Net.Transport.Dtls;

/// <summary>
/// A DTLS 1.2 client as an <see cref="IDatagramTransport"/>: everything sent goes encrypted to one server, everything
/// received is decrypted. The handshake runs on first use (or <see cref="HandshakeAsync"/>). Protocols such as CoAP and
/// LwM2M use it unchanged (<c>coaps://</c>).
/// </summary>
public sealed class DtlsClientTransport : IDatagramTransport
{
    private readonly IDatagramTransport _inner;
    private readonly EndPoint _server;
    private readonly DtlsClientOptions _options;
    private readonly ILogger? _logger;
    private readonly PeerDatagramTransport _peer;
    private readonly Channel<Datagram> _plain = Channel.CreateUnbounded<Datagram>();
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _handshake = new(1, 1);
    private readonly Lock _sendGate = new();
    private DtlsTransport? _dtls;
    private Task? _pump, _app;
    private int _disposed;
    private Exception? _failure;

    /// <summary>Wraps <paramref name="inner"/>, talking to <paramref name="server"/>.</summary>
    public DtlsClientTransport(IDatagramTransport inner, EndPoint server, DtlsClientOptions options, ILogger? logger = null)
    {
        _inner = inner;
        _server = server;
        _options = options;
        _logger = logger;
        if (options.Psk is null && options.PinnedFingerprints.Count == 0 && options.TrustedRoots.Count == 0)
            throw new ArgumentException("Configure a PSK, or pinned fingerprints / trusted roots for certificate mode.", nameof(options));
        _peer = new PeerDatagramTransport(d => _inner.SendAsync(d, _server).AsTask().GetAwaiter().GetResult());
    }

    /// <summary>Negotiated cipher suite (after the handshake).</summary>
    public string? CipherSuite { get; private set; }

    /// <summary>The server.</summary>
    public EndPoint Server => _server;

    /// <inheritdoc />
    public EndPoint LocalEndPoint => _inner.LocalEndPoint;

    /// <summary>Runs the handshake now (otherwise it runs on first send or receive).</summary>
    /// <exception cref="TransportException">The handshake failed (wrong key, untrusted certificate, no answer).</exception>
    public async Task HandshakeAsync(CancellationToken ct = default)
    {
        if (_dtls is not null) return;
        if (_failure is not null) throw _failure;
        await _handshake.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_dtls is not null) return;
            if (_failure is not null) throw _failure;
            _pump ??= Task.Run(PumpAsync, CancellationToken.None);
            var crypto = DtlsBc.NewCrypto();
            Func<int> suite;
            TlsClient client;
            if (_options.Psk is not null)
            {
                var c = new IoTComPskClient(crypto, _options);
                (client, suite) = (c, () => c.NegotiatedSuite);
            }
            else
            {
                var c = new IoTComCertificateClient(crypto, _options);
                (client, suite) = (c, () => c.NegotiatedSuite);
            }

            var started = Environment.TickCount64;
            try
            {
                _dtls = await Task.Run(() => new DtlsClientProtocol().Connect(client, _peer), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TlsException or IOException)
            {
                // A session that failed once stays failed: the peer transport may hold stale records, and CoAP's
                // receive loop would otherwise start a second handshake that reports a misleading error.
                _failure = Describe(ex, Environment.TickCount64 - started >= (long)_options.HandshakeTimeout.TotalMilliseconds - 50);
                throw _failure;
            }

            CipherSuite = DtlsBc.SuiteName(suite());
            _logger?.LogDebug("DTLS session with {Server}: {Suite}", _server, CipherSuite);
            _app = Task.Factory.StartNew(AppLoop, TaskCreationOptions.LongRunning);
        }
        finally
        {
            _handshake.Release();
        }
    }

    private IoTComException Describe(Exception ex, bool timedOut)
    {
        var hint = _options.Psk is not null ? "check the PSK identity and key" : "check the certificate pin or trusted roots";
        return ex switch
        {
            TlsFatalAlertReceived r => new TransportException($"DTLS handshake with {_server} refused by the server: {AlertDescription.GetText(r.AlertDescription)} ({hint}).", ex),
            TlsFatalAlert a when a.AlertDescription == AlertDescription.bad_certificate => new TransportException($"DTLS handshake with {_server} failed: the server certificate is not trusted ({hint}).", ex),
            TlsTimeoutException => new IoTComTimeoutException($"DTLS handshake with {_server} timed out after {_options.HandshakeTimeout.TotalSeconds:0} s ({hint}; is a DTLS server listening?).", ex),
            _ when timedOut => new IoTComTimeoutException($"DTLS handshake with {_server} got no valid answer within {_options.HandshakeTimeout.TotalSeconds:0} s ({hint}; is a DTLS server listening?).", ex),
            TlsFatalAlert a => new TransportException($"DTLS handshake with {_server} failed: {AlertDescription.GetText(a.AlertDescription)} ({hint}).", ex),
            _ => new TransportException($"DTLS handshake with {_server} failed: {ex.Message} ({hint}).", ex),
        };
    }

    private async Task PumpAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var d = await _inner.ReceiveAsync(_cts.Token).ConfigureAwait(false);
                if (Equals(d.Remote, _server) || _server is DnsEndPoint) _peer.Deliver(d.Data.ToArray());
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ObjectDisposedException or ChannelClosedException)
            {
                if (_cts.IsCancellationRequested) break;
            }
        }
    }

    private void AppLoop()
    {
        var buffer = new byte[65535];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var n = _dtls!.Receive(buffer, 0, buffer.Length, 500);
                if (n >= 0) _plain.Writer.TryWrite(new Datagram(buffer.AsSpan(0, n).ToArray(), _server));
            }
        }
        catch (Exception ex) when (ex is TlsException or IOException)
        {
            _logger?.LogDebug(ex, "DTLS session with {Server} ended", _server);
        }
        finally
        {
            _plain.Writer.TryComplete();
        }
    }

    /// <summary>Encrypts and sends to the server (<paramref name="remote"/> is ignored: this transport has one peer).</summary>
    public async ValueTask SendAsync(ReadOnlyMemory<byte> data, EndPoint remote, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await HandshakeAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_sendGate) _dtls!.Send(data.Span);
        }
        catch (Exception ex) when (ex is TlsException or IOException)
        {
            throw new TransportException($"DTLS send to {_server} failed: {ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    public async ValueTask<Datagram> ReceiveAsync(CancellationToken ct = default)
    {
        await HandshakeAsync(ct).ConfigureAwait(false);
        return await _plain.Reader.ReadAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Sends close_notify and closes the inner transport.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            _dtls?.Close();
        }
        catch (Exception ex) when (ex is TlsException or IOException)
        {
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        _peer.Close();
        if (_app is not null) await _app.ConfigureAwait(false);
        await _inner.DisposeAsync().ConfigureAwait(false);
        if (_pump is not null) await _pump.ConfigureAwait(false);
        _cts.Dispose();
        _handshake.Dispose();
    }
}

/// <summary>A peer as the DTLS server sees it.</summary>
/// <param name="Remote">Peer address.</param>
/// <param name="CipherSuite">Negotiated suite.</param>
/// <param name="PskIdentity">PSK identity the peer used (PSK mode).</param>
public sealed record DtlsSession(EndPoint Remote, string CipherSuite, string? PskIdentity);

/// <summary>
/// A DTLS 1.2 server as an <see cref="IDatagramTransport"/>: one handshake per peer (with HelloVerifyRequest cookies),
/// then decrypted datagrams from every peer arrive through <see cref="ReceiveAsync"/> and sends are encrypted for the
/// addressed peer. CoAP and LwM2M servers use it unchanged.
/// </summary>
public sealed class DtlsServerTransport : IDatagramTransport
{
    private readonly IDatagramTransport _inner;
    private readonly DtlsServerOptions _options;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<string, Peer> _peers = new(StringComparer.Ordinal);
    private readonly Channel<Datagram> _plain = Channel.CreateUnbounded<Datagram>();
    private readonly CancellationTokenSource _cts = new();
    private readonly DtlsVerifier _verifier = new(DtlsBc.NewCrypto());
    private readonly Task _pump, _sweep;
    private int _disposed;

    private sealed class Peer(EndPoint remote, PeerDatagramTransport transport)
    {
        public EndPoint Remote { get; } = remote;
        public PeerDatagramTransport Transport { get; } = transport;
        public DtlsTransport? Dtls { get; set; }
        public DtlsSession? Session { get; set; }
        public long LastSeen = Environment.TickCount64;
        public readonly Lock SendGate = new();
    }

    /// <summary>Wraps <paramref name="inner"/> (typically UDP 5684).</summary>
    public DtlsServerTransport(IDatagramTransport inner, DtlsServerOptions options, ILogger? logger = null)
    {
        _inner = inner;
        _options = options;
        _logger = logger;
        if (options.PskLookup is null && options.Certificate is null) throw new ArgumentException("Configure PskLookup and/or a Certificate.", nameof(options));
        if (options.Certificate is { } c && c.GetECDsaPrivateKey() is null) throw new ArgumentException("The certificate needs an ECDSA private key (P-256).", nameof(options));
        _pump = Task.Run(PumpAsync);
        _sweep = Task.Run(SweepAsync);
    }

    /// <summary>Established sessions.</summary>
    public IReadOnlyCollection<DtlsSession> Sessions => [.. _peers.Values.Select(p => p.Session).OfType<DtlsSession>()];

    /// <summary>Raised when a handshake completes.</summary>
    public event Action<DtlsSession>? SessionEstablished;

    /// <summary>Raised when a handshake fails (peer, reason).</summary>
    public event Action<EndPoint, string>? HandshakeFailed;

    /// <inheritdoc />
    public EndPoint LocalEndPoint => _inner.LocalEndPoint;

    private async Task PumpAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            Datagram d;
            try
            {
                d = await _inner.ReceiveAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ObjectDisposedException or ChannelClosedException)
            {
                if (_cts.IsCancellationRequested) break;
                continue;
            }

            var key = d.Remote.ToString()!;
            var data = d.Data.ToArray();
            if (_peers.TryGetValue(key, out var peer))
            {
                peer.LastSeen = Environment.TickCount64;
                peer.Transport.Deliver(data);
                continue;
            }

            var remote = d.Remote;
            DtlsRequest? request = null;
            if (_options.VerifyRequests)
            {
                try
                {
                    request = _verifier.VerifyRequest(EndPointBytes.Of(remote), data, 0, data.Length, new Sender(this, remote));
                }
                catch (Exception ex) when (ex is TlsException or IOException)
                {
                    continue;
                }

                if (request is null) continue;   // a HelloVerifyRequest went back; wait for the ClientHello with the cookie
            }

            var newPeer = new Peer(remote, new PeerDatagramTransport(b => _inner.SendAsync(b, remote).AsTask().GetAwaiter().GetResult()));
            if (!_peers.TryAdd(key, newPeer)) continue;
            if (request is null) newPeer.Transport.Deliver(data);
            _ = Task.Factory.StartNew(() => Serve(newPeer, request), TaskCreationOptions.LongRunning);
        }
    }

    private sealed class Sender(DtlsServerTransport owner, EndPoint remote) : DatagramSender
    {
        public int GetSendLimit() => 1400;

        public void Send(byte[] buf, int off, int len) => Send(buf.AsSpan(off, len));

        public void Send(ReadOnlySpan<byte> buffer) => owner._inner.SendAsync(buffer.ToArray(), remote).AsTask().GetAwaiter().GetResult();
    }

    private void Serve(Peer peer, DtlsRequest? request)
    {
        var psk = new PskManager(_options.PskLookup);
        var server = new IoTComServer(DtlsBc.NewCrypto(), _options, psk);
        try
        {
            var protocol = new DtlsServerProtocol { VerifyRequests = false };
            peer.Dtls = request is null ? protocol.Accept(server, peer.Transport) : protocol.Accept(server, peer.Transport, request);
        }
        catch (Exception ex) when (ex is TlsException or IOException)
        {
            _peers.TryRemove(peer.Remote.ToString()!, out _);
            var reason = ex is TlsFatalAlert a ? AlertDescription.GetText(a.AlertDescription) : ex is TlsFatalAlertReceived r ? "peer sent " + AlertDescription.GetText(r.AlertDescription) : ex.Message;
            _logger?.LogDebug(ex, "DTLS handshake with {Peer} failed", peer.Remote);
            HandshakeFailed?.Invoke(peer.Remote, reason);
            return;
        }

        peer.Session = new DtlsSession(peer.Remote, DtlsBc.SuiteName(server.NegotiatedSuite), psk.Identity);
        SessionEstablished?.Invoke(peer.Session);
        var buffer = new byte[65535];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var n = peer.Dtls.Receive(buffer, 0, buffer.Length, 500);
                if (n >= 0) _plain.Writer.TryWrite(new Datagram(buffer.AsSpan(0, n).ToArray(), peer.Remote));
            }
        }
        catch (Exception ex) when (ex is TlsException or IOException)
        {
            _logger?.LogDebug(ex, "DTLS session with {Peer} ended", peer.Remote);
        }
        finally
        {
            _peers.TryRemove(peer.Remote.ToString()!, out _);
        }
    }

    private async Task SweepAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), _cts.Token).ConfigureAwait(false);
                var limit = (long)_options.IdleTimeout.TotalMilliseconds;
                foreach (var (key, p) in _peers)
                {
                    if (Environment.TickCount64 - p.LastSeen <= limit || !_peers.TryRemove(key, out _)) continue;
                    try
                    {
                        p.Dtls?.Close();
                    }
                    catch (Exception ex) when (ex is TlsException or IOException)
                    {
                    }

                    p.Transport.Close();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Encrypts and sends to an established peer.</summary>
    /// <exception cref="TransportException">No DTLS session with <paramref name="remote"/>.</exception>
    public ValueTask SendAsync(ReadOnlyMemory<byte> data, EndPoint remote, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_peers.TryGetValue(remote.ToString()!, out var p) || p.Dtls is null) throw new TransportException($"No DTLS session with {remote}.");
        try
        {
            lock (p.SendGate) p.Dtls.Send(data.Span);
        }
        catch (Exception ex) when (ex is TlsException or IOException)
        {
            throw new TransportException($"DTLS send to {remote} failed: {ex.Message}", ex);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<Datagram> ReceiveAsync(CancellationToken ct = default) => _plain.Reader.ReadAsync(ct);

    /// <summary>Closes every session (close_notify) and the inner transport.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var p in _peers.Values)
        {
            try
            {
                p.Dtls?.Close();
            }
            catch (Exception ex) when (ex is TlsException or IOException)
            {
            }

            p.Transport.Close();
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        await _inner.DisposeAsync().ConfigureAwait(false);
        await _pump.ConfigureAwait(false);
        await _sweep.ConfigureAwait(false);
        _plain.Writer.TryComplete();
        _cts.Dispose();
    }
}

/// <summary>DTLS for every datagram endpoint builder (CoAP, LwM2M…).</summary>
public static class DtlsBuilderExtensions
{
    /// <summary>Secures the client side: <paramref name="inner"/> (for example <c>() => new UdpDatagramTransport(...)</c>) talks DTLS to <paramref name="server"/>.</summary>
    public static T UseDtlsClient<T>(this T builder, DatagramTransportFactory inner, EndPoint server, DtlsClientOptions options) where T : IDatagramBuilder<T>
        => builder.UseDatagramTransport(() => new DtlsClientTransport(inner(), server, options));

    /// <summary>Secures the server side: <paramref name="inner"/> (for example UDP 5684) accepts DTLS peers.</summary>
    public static T UseDtlsServer<T>(this T builder, DatagramTransportFactory inner, DtlsServerOptions options) where T : IDatagramBuilder<T>
        => builder.UseDatagramTransport(() => new DtlsServerTransport(inner(), options));
}
