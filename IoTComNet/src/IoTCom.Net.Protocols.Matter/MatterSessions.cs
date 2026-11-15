using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Protocols.Matter;

/// <summary>MRP (Message Reliability Protocol) timing.</summary>
public sealed class MatterReliability
{
    /// <summary>Initial retransmission interval (spec default for active peers 300 ms).</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Retransmissions before giving up (spec: 4).</summary>
    public int MaxRetransmissions { get; set; } = 4;

    /// <summary>How long to wait for a reply to piggyback an acknowledgement on before sending a standalone one.</summary>
    public TimeSpan AckDelay { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>How long a request waits for its response.</summary>
    public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>An established PASE or CASE session.</summary>
internal sealed class SecureSession(ushort localId, ushort peerId, byte[] sendKey, byte[] receiveKey, byte[] attestationChallenge, EndPoint peer, bool isPase, ulong localNodeId, ulong peerNodeId)
{
    private readonly HashSet<uint> _seen = [];
    private readonly Queue<uint> _order = new();
    private int _counter = Random.Shared.Next(1, 1 << 28);

    public ushort LocalId { get; } = localId;
    public ushort PeerId { get; } = peerId;
    public byte[] SendKey { get; } = sendKey;
    public byte[] ReceiveKey { get; } = receiveKey;
    public byte[] AttestationChallenge { get; } = attestationChallenge;
    public EndPoint Peer { get; set; } = peer;
    public bool IsPase { get; } = isPase;
    public ulong LocalNodeId { get; } = localNodeId;
    public ulong PeerNodeId { get; } = peerNodeId;
    public byte? FabricIndex { get; set; }
    public MatterRequestContext Context => _context ??= new MatterRequestContext(IsPase, FabricIndex, IsPase ? null : PeerNodeId, AttestationChallenge);
    private MatterRequestContext? _context;

    public uint NextCounter() => (uint)Interlocked.Increment(ref _counter);

    public bool IsDuplicate(uint counter)
    {
        lock (_seen)
        {
            if (!_seen.Add(counter)) return true;
            _order.Enqueue(counter);
            if (_order.Count > 256) _seen.Remove(_order.Dequeue());
            return false;
        }
    }
}

/// <summary>A message exchange: request/response pairs share an exchange id.</summary>
internal sealed class Exchange(ushort id, bool localInitiator, SecureSession? session, EndPoint peer, ulong? ephemeralNodeId)
{
    public ushort Id { get; } = id;
    public bool LocalInitiator { get; } = localInitiator;
    public SecureSession? Session { get; } = session;
    public EndPoint Peer { get; set; } = peer;
    public ulong? EphemeralNodeId { get; set; } = ephemeralNodeId;
    public ulong? PeerEphemeralNodeId { get; set; }
    public Channel<MatterMessage> Inbox { get; } = Channel.CreateUnbounded<MatterMessage>();
    public uint? PendingAck { get; set; }
    public (uint Counter, TaskCompletionSource Acked)? Outstanding { get; set; }
    public readonly Lock Gate = new();
    private readonly HashSet<uint> _received = [];

    /// <summary>Unsecured messages have no session-wide counter check: duplicates are caught per exchange.</summary>
    public bool IsDuplicate(uint counter)
    {
        lock (_received)
        {
            if (!_received.Add(counter)) return true;
            if (_received.Count > 64) _received.Clear();
            return false;
        }
    }
}

/// <summary>
/// Message transport for a Matter node: unsecured and secure sessions over one datagram transport, exchanges, message
/// counters with duplicate detection, and MRP (piggybacked or standalone acknowledgements, retransmissions with backoff).
/// </summary>
internal sealed class MatterTransport : IAsyncDisposable
{
    private readonly IDatagramTransport _transport;
    private readonly MatterReliability _mrp;
    private readonly ILogger _logger;
    private readonly Func<Exchange, MatterMessage, Task> _onUnsolicited;
    private readonly Action<ReadOnlyMemory<byte>, FrameDirection, string>? _tap;
    private readonly ConcurrentDictionary<ushort, SecureSession> _sessions = new();
    private readonly ConcurrentDictionary<(string Session, ushort Id, bool LocalInitiator), Exchange> _exchanges = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private int _unsecuredCounter = Random.Shared.Next(1, 1 << 28);
    private int _exchangeId = Random.Shared.Next(0, 0xFFFF);
    private int _sessionId = Random.Shared.Next(1, 0xFFFE);

    public MatterTransport(IDatagramTransport transport, MatterReliability mrp, ILogger logger, Func<Exchange, MatterMessage, Task> onUnsolicited, Action<ReadOnlyMemory<byte>, FrameDirection, string>? tap)
    {
        (_transport, _mrp, _logger, _onUnsolicited, _tap) = (transport, mrp, logger, onUnsolicited, tap);
        _loop = Task.Run(ReceiveLoopAsync);
    }

    public MatterReliability Reliability => _mrp;

    public EndPoint LocalEndPoint => _transport.LocalEndPoint;

    public ushort AllocateSessionId()
    {
        while (true)
        {
            var id = (ushort)((Interlocked.Increment(ref _sessionId) % 0xFFFE) + 1);
            if (!_sessions.ContainsKey(id)) return id;
        }
    }

    public void AddSession(SecureSession s) => _sessions[s.LocalId] = s;

    public void RemoveSession(ushort localId) => _sessions.TryRemove(localId, out _);

    public IEnumerable<SecureSession> Sessions => _sessions.Values;

    private static string Key(SecureSession? s, EndPoint peer) => s is null ? "u:" + peer : "s:" + s.LocalId;

    public Exchange NewExchange(SecureSession? session, EndPoint peer)
    {
        var id = (ushort)Interlocked.Increment(ref _exchangeId);
        var ex = new Exchange(id, true, session, peer, session is null ? (ulong)Random.Shared.NextInt64() | 0xFFFF_FFFE_0000_0000UL : null);
        _exchanges[(Key(session, peer), id, true)] = ex;
        return ex;
    }

    public void Close(Exchange ex) => _exchanges.TryRemove((Key(ex.Session, ex.Peer), ex.Id, ex.LocalInitiator), out _);

    public async Task SendAsync(Exchange ex, ushort protocol, byte opcode, byte[] payload, bool reliable = true, CancellationToken ct = default)
    {
        uint? ack;
        lock (ex.Gate)
        {
            ack = ex.PendingAck;
            ex.PendingAck = null;
        }

        var s = ex.Session;
        var message = new MatterMessage
        {
            SessionId = s?.PeerId ?? 0,
            Counter = s?.NextCounter() ?? (uint)Interlocked.Increment(ref _unsecuredCounter),
            SourceNodeId = s is null && ex.LocalInitiator ? ex.EphemeralNodeId : null,
            DestinationNodeId = s is null && !ex.LocalInitiator ? ex.PeerEphemeralNodeId : null,
            ExchangeId = ex.Id,
            Initiator = ex.LocalInitiator,
            Reliable = reliable,
            AckCounter = ack,
            ProtocolId = protocol,
            Opcode = opcode,
            Payload = payload,
        };
        var raw = s is null ? message.Encode() : message.EncodeSecured(s.SendKey, s.IsPase ? 0 : s.LocalNodeId);
        _tap?.Invoke(raw, FrameDirection.Outbound, Summary(message));
        if (!reliable)
        {
            await _transport.SendAsync(raw, ex.Peer, ct).ConfigureAwait(false);
            return;
        }

        var acked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (ex.Gate) ex.Outstanding = (message.Counter, acked);
        var interval = _mrp.Interval;
        for (var attempt = 0; attempt <= _mrp.MaxRetransmissions; attempt++)
        {
            await _transport.SendAsync(raw, ex.Peer, ct).ConfigureAwait(false);
            var done = await Task.WhenAny(acked.Task, Task.Delay(interval, ct)).ConfigureAwait(false);
            if (done == acked.Task) return;
            interval = TimeSpan.FromMilliseconds(interval.TotalMilliseconds * 1.6);
            _logger.LogDebug("MRP: retransmitting counter {Counter} on exchange {Exchange}", message.Counter, ex.Id);
        }

        throw new IoTComTimeoutException($"Matter peer {ex.Peer} did not acknowledge message {message.Counter} after {_mrp.MaxRetransmissions} retransmissions.");
    }

    public async Task<MatterMessage> ReceiveAsync(Exchange ex, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_mrp.ResponseTimeout);
        try
        {
            return await ex.Inbox.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IoTComTimeoutException($"No response from {ex.Peer} on exchange {ex.Id} within {_mrp.ResponseTimeout.TotalSeconds:0.#} s.");
        }
    }

    private static string Summary(MatterMessage m) => m.ProtocolId switch
    {
        MatterProtocol.SecureChannel => $"{(SecureChannelOpcode)m.Opcode} ex {m.ExchangeId}",
        MatterProtocol.InteractionModel => $"{(InteractionOpcode)m.Opcode} ex {m.ExchangeId}",
        _ => $"protocol 0x{m.ProtocolId:X4} op 0x{m.Opcode:X2}",
    };

    private async Task ReceiveLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            Datagram d;
            try
            {
                d = await _transport.ReceiveAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is ChannelClosedException or ObjectDisposedException or System.Net.Sockets.SocketException)
            {
                if (_cts.IsCancellationRequested) break;
                continue;
            }

            try
            {
                await HandleAsync(d).ConfigureAwait(false);
            }
            catch (ProtocolException ex)
            {
                _logger.LogDebug("Matter: dropped a message from {Peer}: {Message}", d.Remote, ex.Message);
            }
        }
    }

    private async Task HandleAsync(Datagram d)
    {
        var raw = d.Data.Span;
        var sessionId = MatterMessage.PeekSessionId(raw);
        SecureSession? session = null;
        MatterMessage m;
        if (sessionId == 0) m = MatterMessage.Decode(raw);
        else
        {
            if (!_sessions.TryGetValue(sessionId, out session)) throw new ProtocolException($"Unknown session {sessionId}.");
            m = MatterMessage.DecodeSecured(raw, session.ReceiveKey, session.IsPase ? 0 : session.PeerNodeId);
            if (session.IsDuplicate(m.Counter))
            {
                if (m.Reliable) await SendStandaloneAckAsync(session, d.Remote, m).ConfigureAwait(false);
                return;
            }

            session.Peer = d.Remote;
        }

        _tap?.Invoke(d.Data, FrameDirection.Inbound, Summary(m));
        var key = (Key(session, d.Remote), m.ExchangeId, !m.Initiator);
        var isNew = false;
        if (!_exchanges.TryGetValue(key, out var ex))
        {
            if (!m.Initiator) return;   // a response to an exchange we no longer track
            if (m.ProtocolId == MatterProtocol.SecureChannel && m.Opcode == (byte)SecureChannelOpcode.StandaloneAck) return;
            ex = new Exchange(m.ExchangeId, false, session, d.Remote, null) { PeerEphemeralNodeId = m.SourceNodeId };
            _exchanges[key] = ex;
            isNew = true;
        }

        ex.Peer = d.Remote;
        if (session is null && !isNew && ex.IsDuplicate(m.Counter))
        {
            if (m.Reliable)
            {
                lock (ex.Gate) ex.PendingAck = m.Counter;
                _ = FlushAckAsync(ex);
            }

            return;
        }

        if (session is null && isNew) ex.IsDuplicate(m.Counter);
        if (m.AckCounter is { } acked)
        {
            lock (ex.Gate)
            {
                if (ex.Outstanding is { } o && o.Counter == acked)
                {
                    ex.Outstanding = null;
                    o.Acked.TrySetResult();
                }
            }
        }

        if (m.ProtocolId == MatterProtocol.SecureChannel && m.Opcode == (byte)SecureChannelOpcode.StandaloneAck) return;
        if (m.Reliable)
        {
            lock (ex.Gate) ex.PendingAck = m.Counter;
            _ = FlushAckAsync(ex);
        }

        if (isNew) _ = Task.Run(() => DispatchAsync(ex, m));
        else ex.Inbox.Writer.TryWrite(m);
    }

    private async Task DispatchAsync(Exchange ex, MatterMessage m)
    {
        try
        {
            await _onUnsolicited(ex, m).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IoTComException or ProtocolException)
        {
            _logger.LogDebug(e, "Matter: handling {Opcode} from {Peer} failed", m.Opcode, ex.Peer);
        }
        finally
        {
            _ = CloseLaterAsync(ex);
        }
    }

    private async Task CloseLaterAsync(Exchange ex)
    {
        await Task.Delay(_mrp.Interval * 4).ConfigureAwait(false);   // keep the exchange for retransmitted requests
        Close(ex);
    }

    private async Task FlushAckAsync(Exchange ex)
    {
        await Task.Delay(_mrp.AckDelay).ConfigureAwait(false);
        uint? ack;
        lock (ex.Gate)
        {
            ack = ex.PendingAck;
            ex.PendingAck = null;
        }

        if (ack is not { } counter) return;
        var s = ex.Session;
        var message = new MatterMessage
        {
            SessionId = s?.PeerId ?? 0,
            Counter = s?.NextCounter() ?? (uint)Interlocked.Increment(ref _unsecuredCounter),
            SourceNodeId = s is null && ex.LocalInitiator ? ex.EphemeralNodeId : null,
            DestinationNodeId = s is null && !ex.LocalInitiator ? ex.PeerEphemeralNodeId : null,
            ExchangeId = ex.Id,
            Initiator = ex.LocalInitiator,
            AckCounter = counter,
            ProtocolId = MatterProtocol.SecureChannel,
            Opcode = (byte)SecureChannelOpcode.StandaloneAck,
        };
        var raw = s is null ? message.Encode() : message.EncodeSecured(s.SendKey, s.IsPase ? 0 : s.LocalNodeId);
        try
        {
            await _transport.SendAsync(raw, ex.Peer).ConfigureAwait(false);
        }
        catch (Exception e) when (e is ObjectDisposedException or TransportException or System.Net.Sockets.SocketException)
        {
        }
    }

    private async Task SendStandaloneAckAsync(SecureSession s, EndPoint peer, MatterMessage m)
    {
        var message = new MatterMessage
        {
            SessionId = s.PeerId, Counter = s.NextCounter(), ExchangeId = m.ExchangeId, Initiator = !m.Initiator, AckCounter = m.Counter,
            ProtocolId = MatterProtocol.SecureChannel, Opcode = (byte)SecureChannelOpcode.StandaloneAck,
        };
        try
        {
            await _transport.SendAsync(message.EncodeSecured(s.SendKey, s.IsPase ? 0 : s.LocalNodeId), peer).ConfigureAwait(false);
        }
        catch (Exception e) when (e is ObjectDisposedException or TransportException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        await _transport.DisposeAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _cts.Dispose();
    }
}
