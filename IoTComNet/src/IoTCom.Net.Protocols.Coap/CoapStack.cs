using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Protocols.Coap;

/// <summary>Transmission parameters (RFC 7252 §4.8). Shorten them for tests and lossy-link demos.</summary>
public sealed class CoapTransmission
{
    /// <summary>ACK_TIMEOUT: initial retransmission timeout.</summary>
    public TimeSpan AckTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>ACK_RANDOM_FACTOR: the initial timeout is randomised in [AckTimeout, AckTimeout × factor].</summary>
    public double AckRandomFactor { get; set; } = 1.5;

    /// <summary>MAX_RETRANSMIT: retransmissions before giving up.</summary>
    public int MaxRetransmit { get; set; } = 4;

    /// <summary>How long a client waits for a separate (or non-confirmable) response.</summary>
    public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long received message IDs are remembered for deduplication (EXCHANGE_LIFETIME is 247 s).</summary>
    public TimeSpan DeduplicationLifetime { get; set; } = TimeSpan.FromSeconds(247);

    /// <summary>Preferred block size for Block-wise transfers (16–1024).</summary>
    public int BlockSize { get; set; } = 1024;
}

/// <summary>Counters of the message layer.</summary>
public sealed class CoapStatistics
{
    internal long Sent, Received, Retransmissions, Duplicates, Resets, Timeouts;

    /// <summary>Datagrams sent.</summary>
    public long MessagesSent => Interlocked.Read(ref Sent);

    /// <summary>Datagrams received (valid CoAP).</summary>
    public long MessagesReceived => Interlocked.Read(ref Received);

    /// <summary>CON retransmissions.</summary>
    public long RetransmissionCount => Interlocked.Read(ref Retransmissions);

    /// <summary>Duplicates detected and suppressed.</summary>
    public long DuplicateCount => Interlocked.Read(ref Duplicates);

    /// <summary>Resets sent or received.</summary>
    public long ResetCount => Interlocked.Read(ref Resets);

    /// <summary>Exchanges that ran out of retransmissions.</summary>
    public long TimeoutCount => Interlocked.Read(ref Timeouts);
}

/// <summary>The peer answered a confirmable message with RST.</summary>
public sealed class CoapResetException : ProtocolException
{
    /// <summary>Creates the exception.</summary>
    public CoapResetException() : base("The CoAP peer rejected the message (RST).") { }

    /// <summary>Creates the exception.</summary>
    public CoapResetException(string message) : base(message) { }

    /// <summary>Creates the exception.</summary>
    public CoapResetException(string message, Exception? inner) : base(message, inner) { }
}

/// <summary>
/// The CoAP message layer shared by <see cref="CoapClient"/> and <see cref="CoapServer"/>: message IDs, tokens,
/// confirmable retransmission with exponential back-off, ACK/RST matching, deduplication with a reply cache,
/// separate-response acknowledgement and RST for unknown tokens.
/// </summary>
internal sealed class CoapStack : IAsyncDisposable
{
    private readonly IDatagramTransport _transport;
    private readonly CoapTransmission _params;
    private readonly ILogger _log;
    private readonly Action<FrameDirection, byte[], CoapMessage> _tap;
    private readonly ConcurrentDictionary<(string, ushort), TaskCompletionSource<CoapMessage>> _pendingAcks = new();
    private readonly ConcurrentDictionary<(string, string), Action<CoapMessage, EndPoint>> _tokens = new();
    private readonly ConcurrentDictionary<(string, ushort), Dedup> _seen = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private int _mid = RandomNumberGenerator.GetInt32(ushort.MaxValue);
    private long _lastPurge;

    private sealed class Dedup(long at)
    {
        public long At = at;
        public byte[]? Reply;
    }

    public CoapStack(IDatagramTransport transport, CoapTransmission parameters, ILogger log, Action<FrameDirection, byte[], CoapMessage> tap)
    {
        _transport = transport;
        _params = parameters;
        _log = log;
        _tap = tap;
        _loop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
    }

    public CoapStatistics Statistics { get; } = new();

    public EndPoint LocalEndPoint => _transport.LocalEndPoint;

    /// <summary>Handles requests (server side). Must eventually call <see cref="ReplyAsync"/> for CON requests.</summary>
    public Func<CoapMessage, EndPoint, Task>? RequestHandler { get; set; }

    /// <summary>Raised when a RST arrives for a message we sent (MID), e.g. an observer leaving.</summary>
    public event Action<EndPoint, ushort>? ResetReceived;

    public ushort NextMessageId() => (ushort)Interlocked.Increment(ref _mid);

    public static byte[] NewToken(int length = 4) => RandomNumberGenerator.GetBytes(length);

    private static string Key(EndPoint ep) => ep.ToString() ?? "";

    /// <summary>Routes responses with <paramref name="token"/> from <paramref name="remote"/> to <paramref name="handler"/>.</summary>
    public IDisposable RegisterToken(EndPoint remote, ReadOnlyMemory<byte> token, Action<CoapMessage, EndPoint> handler)
    {
        var key = (Key(remote), Convert.ToHexString(token.Span));
        _tokens[key] = handler;
        return new Registration(() => _tokens.TryRemove(key, out _));
    }

    private sealed class Registration(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }

    public async Task SendAsync(CoapMessage message, EndPoint remote, CancellationToken ct)
    {
        var bytes = message.Encode();
        _tap(FrameDirection.Outbound, bytes, message);
        Interlocked.Increment(ref Statistics.Sent);
        await _transport.SendAsync(bytes, remote, ct).ConfigureAwait(false);
    }

    /// <summary>Sends a confirmable message and retransmits until ACK (returned) or RST/timeout (thrown).</summary>
    public async Task<CoapMessage> SendConfirmableAsync(CoapMessage message, EndPoint remote, CancellationToken ct)
    {
        message.Type = CoapType.Confirmable;
        var key = (Key(remote), message.MessageId);
        var tcs = new TaskCompletionSource<CoapMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingAcks[key] = tcs;
        try
        {
            var timeout = _params.AckTimeout * (1 + Random.Shared.NextDouble() * (_params.AckRandomFactor - 1));
            for (var attempt = 0; ; attempt++)
            {
                await SendAsync(message, remote, ct).ConfigureAwait(false);
                var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout, ct)).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (done == tcs.Task)
                {
                    var reply = await tcs.Task.ConfigureAwait(false);
                    if (reply.Type == CoapType.Reset) throw new CoapResetException();
                    return reply;
                }
                if (attempt >= _params.MaxRetransmit)
                {
                    Interlocked.Increment(ref Statistics.Timeouts);
                    throw new IoTComTimeoutException($"No acknowledgement from {remote} after {attempt + 1} transmissions ({message}).");
                }
                Interlocked.Increment(ref Statistics.Retransmissions);
                timeout *= 2;
            }
        }
        finally
        {
            _pendingAcks.TryRemove(key, out _);
        }
    }

    /// <summary>Sends the reply to a request and caches it for duplicates of that request.</summary>
    public async Task ReplyAsync(CoapMessage request, CoapMessage reply, EndPoint remote, CancellationToken ct)
    {
        if (request.Type == CoapType.Confirmable && reply.Type == CoapType.Acknowledgement)
        {
            reply.MessageId = request.MessageId;
            if (_seen.TryGetValue((Key(remote), request.MessageId), out var entry)) entry.Reply = reply.Encode();
        }
        await SendAsync(reply, remote, ct).ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Datagram d;
            try
            {
                d = await _transport.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (System.Threading.Channels.ChannelClosedException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "CoAP receive failed");
                continue;
            }
            try
            {
                await HandleAsync(d, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "CoAP message from {Remote} failed", d.Remote);
            }
        }
    }

    private async Task HandleAsync(Datagram d, CancellationToken ct)
    {
        if (!CoapMessage.TryDecode(d.Data.Span, out var m, out var error))
        {
            _log.LogDebug("Ignoring invalid CoAP datagram from {Remote}: {Error}", d.Remote, error);
            return;
        }
        Interlocked.Increment(ref Statistics.Received);
        _tap(FrameDirection.Inbound, d.Data.ToArray(), m);
        var remote = d.Remote;
        var key = Key(remote);

        if (m.Type is CoapType.Acknowledgement or CoapType.Reset)
        {
            if (m.Type == CoapType.Reset)
            {
                Interlocked.Increment(ref Statistics.Resets);
                ResetReceived?.Invoke(remote, m.MessageId);
            }
            if (_pendingAcks.TryRemove((key, m.MessageId), out var tcs)) tcs.TrySetResult(m);
            return;
        }

        // CON / NON: deduplicate by (endpoint, MID).
        PurgeDedup();
        var fresh = new Dedup(Environment.TickCount64);
        var entry = _seen.GetOrAdd((key, m.MessageId), fresh);
        if (!ReferenceEquals(entry, fresh))
        {
            Interlocked.Increment(ref Statistics.Duplicates);
            if (m.Type == CoapType.Confirmable && entry.Reply is { } cached)
            {
                Interlocked.Increment(ref Statistics.Sent);
                await _transport.SendAsync(cached, remote, ct).ConfigureAwait(false);
            }
            return;
        }

        if (m.Code.Value == 0)
        {
            // CoAP ping: an empty CON is answered with RST.
            if (m.Type == CoapType.Confirmable) await Reset(m, remote, entry, ct).ConfigureAwait(false);
            return;
        }

        if (m.Code.IsRequest)
        {
            if (RequestHandler is null)
            {
                await Reset(m, remote, entry, ct).ConfigureAwait(false);
                return;
            }
            await RequestHandler(m, remote).ConfigureAwait(false);
            return;
        }

        // A response (separate or notification): route by token.
        if (_tokens.TryGetValue((key, Convert.ToHexString(m.Token.Span)), out var handler))
        {
            if (m.Type == CoapType.Confirmable)
            {
                var ack = CoapMessage.Empty(CoapType.Acknowledgement, m.MessageId);
                entry.Reply = ack.Encode();
                await SendAsync(ack, remote, ct).ConfigureAwait(false);
            }
            handler(m, remote);
        }
        else
        {
            // Nobody waits for this token (e.g. a cancelled observation): tell the sender to stop.
            await Reset(m, remote, entry, ct).ConfigureAwait(false);
        }
    }

    private async Task Reset(CoapMessage m, EndPoint remote, Dedup entry, CancellationToken ct)
    {
        var rst = CoapMessage.Empty(CoapType.Reset, m.MessageId);
        entry.Reply = rst.Encode();
        Interlocked.Increment(ref Statistics.Resets);
        await SendAsync(rst, remote, ct).ConfigureAwait(false);
    }

    private void PurgeDedup()
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastPurge);
        if (now - last < 1000 || Interlocked.CompareExchange(ref _lastPurge, now, last) != last) return;
        var lifetime = (long)_params.DeduplicationLifetime.TotalMilliseconds;
        foreach (var (k, v) in _seen)
            if (now - v.At > lifetime) _seen.TryRemove(k, out _);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        await _transport.DisposeAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        foreach (var t in _pendingAcks.Values) t.TrySetCanceled();
        _cts.Dispose();
    }
}
