using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using IoTCom.Net.Framing;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Protocols.Hl7;

/// <summary>
/// Minimal Lower Layer Protocol framing: <c>&lt;VT&gt; message &lt;FS&gt;&lt;CR&gt;</c> (0x0B … 0x1C 0x0D).
/// Bytes outside a block are ignored, so the decoder resynchronises after noise.
/// </summary>
public sealed class MllpFraming(int maxMessageLength = 1024 * 1024) : IFrameEncoder, IFrameDecoder
{
    /// <summary>Start block (VT).</summary>
    public const byte StartBlock = 0x0B;
    /// <summary>End block (FS).</summary>
    public const byte EndBlock = 0x1C;
    /// <summary>Trailing carriage return.</summary>
    public const byte CarriageReturn = 0x0D;

    /// <summary>Maximum accepted message size.</summary>
    public int MaxMessageLength { get; } = maxMessageLength;

    /// <inheritdoc />
    public void Encode(ReadOnlySpan<byte> payload, IBufferWriter<byte> output)
    {
        var span = output.GetSpan(payload.Length + 3);
        span[0] = StartBlock;
        payload.CopyTo(span[1..]);
        span[payload.Length + 1] = EndBlock;
        span[payload.Length + 2] = CarriageReturn;
        output.Advance(payload.Length + 3);
    }

    /// <inheritdoc />
    public FrameDecodeStatus TryDecode(ref ReadOnlySequence<byte> buffer, IBufferWriter<byte> payload)
    {
        var reader = new SequenceReader<byte>(buffer);
        if (!reader.TryAdvanceTo(StartBlock, advancePastDelimiter: false))
        {
            buffer = buffer.Slice(buffer.End); // noise before any start block
            return FrameDecodeStatus.NeedMoreData;
        }
        var blockStart = reader.Position;       // at the VT byte
        reader.Advance(1);
        if (!reader.TryReadTo(out ReadOnlySequence<byte> body, EndBlock, advancePastDelimiter: true))
        {
            buffer = buffer.Slice(blockStart);  // drop leading noise, keep the partial block
            if (buffer.Length > MaxMessageLength + 1)
            {
                buffer = buffer.Slice(1);
                return FrameDecodeStatus.Invalid;
            }
            return FrameDecodeStatus.NeedMoreData;
        }
        if (reader.Remaining == 0)              // need the trailing CR to be sure the block ended
        {
            buffer = buffer.Slice(blockStart);
            return FrameDecodeStatus.NeedMoreData;
        }
        if (reader.TryPeek(out var cr) && cr == CarriageReturn) reader.Advance(1);
        buffer = buffer.Slice(reader.Position);
        if (body.Length > MaxMessageLength) return FrameDecodeStatus.Invalid;
        foreach (var seg in body) payload.Write(seg.Span);
        return FrameDecodeStatus.Frame;
    }
}

/// <summary>Raised when the server receives a message.</summary>
public sealed class Hl7MessageReceivedEventArgs(Hl7Message message, string peer) : EventArgs
{
    /// <summary>The message.</summary>
    public Hl7Message Message { get; } = message;
    /// <summary>Remote address.</summary>
    public string Peer { get; } = peer;
    /// <summary>Set to override the automatic ACK (e.g. <c>message.CreateAck("AE", "Unknown patient")</c>).</summary>
    public Hl7Message? Ack { get; set; }
}

/// <summary>Options for <see cref="Hl7MllpServer"/>.</summary>
public sealed class Hl7MllpServerOptions : IListenerBuilder<Hl7MllpServerOptions>
{
    /// <summary>Listener (TCP, in-memory).</summary>
    public TransportListenerFactory? ListenerFactory { get; set; }
    /// <summary>Single transport (serial line).</summary>
    public TransportFactory? TransportFactory { get; set; }
    /// <summary>Send an ACK for every message (default true).</summary>
    public bool AutoAcknowledge { get; set; } = true;
    /// <summary>Text encoding (HL7 default is ASCII/Latin-1; many systems use UTF-8).</summary>
    public Encoding Encoding { get; set; } = Encoding.UTF8;
    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }
    /// <summary>Endpoint name.</summary>
    public string? Name { get; set; }

    /// <inheritdoc />
    public Hl7MllpServerOptions UseListener(TransportListenerFactory factory) { ListenerFactory = factory; TransportFactory = null; return this; }
    /// <inheritdoc />
    public Hl7MllpServerOptions UseTransport(TransportFactory factory) { TransportFactory = factory; ListenerFactory = null; return this; }
    /// <summary>Disables automatic ACKs (the application replies itself).</summary>
    public Hl7MllpServerOptions WithoutAutoAck() { AutoAcknowledge = false; return this; }
    /// <summary>Sets the logger.</summary>
    public Hl7MllpServerOptions WithLogger(ILogger logger) { Logger = logger; return this; }
}

/// <summary>
/// HL7 receiver (interface engine / LIS / dashboard side): accepts MLLP connections, parses each message,
/// acknowledges it (AA, or AE when it cannot be parsed) and delivers it as an event and an async stream.
/// </summary>
public sealed class Hl7MllpServer : EndpointBase, IServerEndpoint, ISubscriber<Hl7Message>
{
    private readonly Hl7MllpServerOptions _options;
    private readonly ConcurrentDictionary<int, ITransport> _peers = new();
    private readonly List<Channel<Hl7Message>> _subscribers = [];
    private readonly Lock _gate = new();
    private ITransportListener? _listener;
    private CancellationTokenSource? _cts;
    private int _ids;
    private long _received;

    private Hl7MllpServer(Hl7MllpServerOptions options) : base("hl7-mllp", options.Logger)
    {
        _options = options;
        Name = options.Name;
    }

    /// <summary>Creates a server.</summary>
    public static Hl7MllpServer Create(Action<Hl7MllpServerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var o = new Hl7MllpServerOptions();
        configure(o);
        if (o.ListenerFactory is null && o.TransportFactory is null) throw new ArgumentException("A listener (UseTcp/ListenInMemory) or transport is required.", nameof(configure));
        return new Hl7MllpServer(o);
    }

    /// <summary>Raised for every parsed message (on the I/O thread), before the ACK is sent.</summary>
    public event EventHandler<Hl7MessageReceivedEventArgs>? MessageReceived;

    /// <summary>Messages received.</summary>
    public long MessagesReceived => Interlocked.Read(ref _received);

    /// <summary>Connected peers.</summary>
    public int ConnectionCount => _peers.Count;

    /// <summary>Local address after start.</summary>
    public string? LocalAddress => _listener?.LocalAddress;

    /// <inheritdoc />
    public async ValueTask StartAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_cts is not null) return;
        SetState(EndpointState.Connecting);
        var cts = _cts = new CancellationTokenSource();
        var token = cts.Token;
        if (_options.ListenerFactory is not null)
        {
            var listener = _listener = _options.ListenerFactory();
            await listener.StartAsync(ct).ConfigureAwait(false);
            _ = Task.Run(() => AcceptLoopAsync(listener, token), CancellationToken.None);
        }
        else
        {
            var t = _options.TransportFactory!();
            await t.OpenAsync(ct).ConfigureAwait(false);
            _ = Task.Run(() => ServeAsync(t, token), CancellationToken.None);
        }
        SetState(EndpointState.Listening);
    }

    /// <inheritdoc />
    public async ValueTask StopAsync(CancellationToken ct = default)
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts is null) return;
        SetState(EndpointState.Stopping);
        await cts.CancelAsync().ConfigureAwait(false);
        if (_listener is not null) await _listener.DisposeAsync().ConfigureAwait(false);
        foreach (var p in _peers.Values) await p.DisposeAsync().ConfigureAwait(false);
        _peers.Clear();
        lock (_gate) foreach (var s in _subscribers) s.Writer.TryComplete();
        cts.Dispose();
        SetState(EndpointState.Disconnected);
    }

    /// <summary>Streams received messages whose type matches <paramref name="filter"/> (<c>"ORU^R01"</c>, <c>"ADT"</c>, or <c>"*"</c>).</summary>
    public async IAsyncEnumerable<Message<Hl7Message>> SubscribeAsync(string filter, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var m in ReceiveAsync(ct).ConfigureAwait(false))
            if (filter is "*" or "#" || m.MessageType == filter || m.MessageType.StartsWith(filter + "^", StringComparison.Ordinal))
                yield return new Message<Hl7Message>(m.MessageType, m, DateTimeOffset.UtcNow);
    }

    /// <summary>Streams every received message.</summary>
    public async IAsyncEnumerable<Hl7Message> ReceiveAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        var ch = Channel.CreateBounded<Hl7Message>(new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.DropOldest });
        lock (_gate) _subscribers.Add(ch);
        try
        {
            await foreach (var m in ch.Reader.ReadAllAsync(ct).ConfigureAwait(false)) yield return m;
        }
        finally
        {
            lock (_gate) _subscribers.Remove(ch);
        }
    }

    private async Task AcceptLoopAsync(ITransportListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            ITransport peer;
            try { peer = await listener.AcceptAsync(ct).ConfigureAwait(false); }
            catch (Exception) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "MLLP accept failed");
                break;
            }
            _ = Task.Run(() => ServeAsync(peer, ct), CancellationToken.None);
        }
    }

    private async Task ServeAsync(ITransport peer, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _ids);
        _peers[id] = peer;
        var name = peer.Info.RemoteAddress ?? $"peer-{id}";
        var framing = new MllpFraming();
        try
        {
            await foreach (var frame in peer.Pipe.Input.ReadFramesAsync(framing, ct).ConfigureAwait(false))
            {
                var text = _options.Encoding.GetString(frame);
                Hl7Message? ack;
                if (Hl7Message.TryParse(text, out var message))
                {
                    Tap(FrameDirection.Inbound, frame, () => $"{message!.MessageType} {message.ControlId}");
                    Interlocked.Increment(ref _received);
                    var args = new Hl7MessageReceivedEventArgs(message!, name);
                    MessageReceived?.Invoke(this, args);
                    Channel<Hl7Message>[] subs;
                    lock (_gate) subs = [.. _subscribers];
                    foreach (var s in subs) s.Writer.TryWrite(message!);
                    ack = args.Ack ?? (_options.AutoAcknowledge ? message!.CreateAck("AA") : null);
                }
                else
                {
                    Tap(FrameDirection.Inbound, frame, () => "invalid HL7");
                    IoTComDiagnostics.Errors.Add(1, new KeyValuePair<string, object?>("protocol", Protocol));
                    ack = _options.AutoAcknowledge
                        ? new Hl7MessageBuilder().Header("IOTCOM", "", "", "", "ACK").Segment("MSA", "AR", "", "Message could not be parsed").Build()
                        : null;
                }
                if (ack is null) continue;
                var bytes = _options.Encoding.GetBytes(ack.Encode());
                Tap(FrameDirection.Outbound, bytes, () => $"ACK {ack.AckCode()} {ack.Get("MSA.2")}");
                await peer.Pipe.Output.WriteFrameAsync(framing, bytes, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "MLLP peer {Peer} error", name);
        }
        finally
        {
            _peers.TryRemove(id, out _);
            await peer.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore() => await StopAsync().ConfigureAwait(false);
}

/// <summary>Options for <see cref="Hl7MllpClient"/>.</summary>
public sealed class Hl7MllpClientOptions : ITransportBuilder<Hl7MllpClientOptions>
{
    /// <summary>Transport.</summary>
    public TransportFactory? TransportFactory { get; set; }
    /// <summary>Time to wait for the ACK (default 5 s).</summary>
    public TimeSpan AckTimeout { get; set; } = TimeSpan.FromSeconds(5);
    /// <summary>Text encoding.</summary>
    public Encoding Encoding { get; set; } = Encoding.UTF8;
    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }

    /// <inheritdoc />
    public Hl7MllpClientOptions UseTransport(TransportFactory factory) { TransportFactory = factory; return this; }
    /// <summary>Sets the ACK timeout.</summary>
    public Hl7MllpClientOptions WithAckTimeout(TimeSpan timeout) { AckTimeout = timeout; return this; }
}

/// <summary>
/// HL7 sender (device / modality / analyzer side): sends messages over MLLP and waits for the matching ACK
/// (MSA-2 = MSH-10). Messages are sent one at a time, as MLLP requires.
/// </summary>
public sealed class Hl7MllpClient : EndpointBase, IClientEndpoint, IPublisher<Hl7Message>
{
    private readonly Hl7MllpClientOptions _options;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly MllpFraming _framing = new();
    private ITransport? _transport;

    private Hl7MllpClient(Hl7MllpClientOptions options) : base("hl7-mllp", options.Logger) => _options = options;

    /// <summary>Creates a client.</summary>
    public static Hl7MllpClient Create(Action<Hl7MllpClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var o = new Hl7MllpClientOptions();
        configure(o);
        if (o.TransportFactory is null) throw new ArgumentException("A transport is required.", nameof(configure));
        return new Hl7MllpClient(o);
    }

    /// <inheritdoc />
    public async ValueTask ConnectAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_transport is not null) return;
        SetState(EndpointState.Connecting);
        var t = _options.TransportFactory!();
        try
        {
            await t.OpenAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await t.DisposeAsync().ConfigureAwait(false);
            SetState(EndpointState.Disconnected, ex);
            throw;
        }
        _transport = t;
        SetState(EndpointState.Connected);
    }

    /// <inheritdoc />
    public async ValueTask DisconnectAsync(CancellationToken ct = default)
    {
        var t = Interlocked.Exchange(ref _transport, null);
        if (t is null) return;
        await t.DisposeAsync().ConfigureAwait(false);
        SetState(EndpointState.Disconnected);
    }

    /// <summary>Sends <paramref name="message"/> and returns the ACK.</summary>
    /// <exception cref="IoTComTimeoutException">No ACK in time.</exception>
    /// <exception cref="DeviceException">The receiver answered AE/AR (when <paramref name="throwOnNegativeAck"/>).</exception>
    public async Task<Hl7Message> SendAsync(Hl7Message message, bool throwOnNegativeAck = true, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();
        if (_transport is null) await ConnectAsync(ct).ConfigureAwait(false);
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var t = _transport ?? throw new TransportException("Not connected.");
            var bytes = _options.Encoding.GetBytes(message.Encode());
            Tap(FrameDirection.Outbound, bytes, () => $"{message.MessageType} {message.ControlId}");
            await t.Pipe.Output.WriteFrameAsync(_framing, bytes, ct).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_options.AckTimeout);
            try
            {
                await foreach (var frame in ReadOneAsync(t, timeout.Token).ConfigureAwait(false))
                {
                    var text = _options.Encoding.GetString(frame);
                    if (!Hl7Message.TryParse(text, out var ack)) throw new ProtocolException("Received an invalid HL7 acknowledgement.");
                    Tap(FrameDirection.Inbound, frame, () => $"ACK {ack!.AckCode()} {ack.Get("MSA.2")}");
                    if (ack!.Get("MSA.2") is { Length: > 0 } ackId && ackId != message.ControlId)
                    {
                        Logger.LogDebug("Ignoring ACK for {AckId}, waiting for {ControlId}", ackId, message.ControlId);
                        continue;
                    }
                    if (throwOnNegativeAck && !ack.IsPositiveAck())
                        throw new DeviceException($"HL7 receiver answered {ack.AckCode()}: {ack.Get("MSA.3")}");
                    return ack;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new IoTComTimeoutException($"No HL7 ACK for {message.ControlId} within {_options.AckTimeout.TotalSeconds:0.#} s.");
            }
            throw new TransportException("Connection closed before the ACK arrived.");
        }
        catch (Exception ex) when (ex is TransportException or IOException)
        {
            await DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Publishes a message (topic ignored; MLLP has no topics) and waits for a positive ACK.</summary>
    public async ValueTask PublishAsync(string topic, Hl7Message message, PublishOptions? options = null, CancellationToken ct = default)
        => await SendAsync(message, true, ct).ConfigureAwait(false);

    private async IAsyncEnumerable<byte[]> ReadOneAsync(ITransport t, [EnumeratorCancellation] CancellationToken ct)
    {
        var reader = t.Pipe.Input;
        var payload = new ArrayBufferWriter<byte>();
        while (true)
        {
            var result = await reader.ReadAsync(ct).ConfigureAwait(false);
            var buffer = result.Buffer;
            payload.ResetWrittenCount();
            var status = _framing.TryDecode(ref buffer, payload);
            reader.AdvanceTo(buffer.Start, buffer.End);
            if (status == FrameDecodeStatus.Frame) yield return payload.WrittenSpan.ToArray();
            else if (result.IsCompleted) yield break;
        }
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _sendLock.Dispose();
    }
}
