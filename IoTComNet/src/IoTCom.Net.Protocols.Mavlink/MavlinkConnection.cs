using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using IoTCom.Net.Protocols.Mavlink.Common;
using IoTCom.Net.Transports;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Protocols.Mavlink;

/// <summary>MAVLink connection options.</summary>
public sealed class MavlinkConnectionOptions : ITransportBuilder<MavlinkConnectionOptions>, IDatagramBuilder<MavlinkConnectionOptions>
{
    /// <summary>Our system id (255 is the usual ground station id; vehicles use 1…).</summary>
    public byte SystemId { get; set; } = 255;

    /// <summary>Our component id (190 = MAV_COMP_ID_MISSIONPLANNER; 1 = autopilot).</summary>
    public byte ComponentId { get; set; } = 190;

    /// <summary>Version used for sending (receiving accepts both).</summary>
    public MavlinkVersion Version { get; set; } = MavlinkVersion.V2;

    /// <summary>Dialect for CRC_EXTRA and decoding (default: common).</summary>
    public IMavlinkDialect Dialect { get; set; } = CommonDialect.Instance;

    /// <summary>MAVLink 2 signing (sign outgoing, verify incoming).</summary>
    public MavlinkSigning? Signing { get; set; }

    /// <summary>Stream transport (TCP, serial, in-memory).</summary>
    public TransportFactory? TransportFactory { get; set; }

    /// <summary>Datagram transport (UDP, in-memory network).</summary>
    public DatagramTransportFactory? DatagramFactory { get; set; }

    /// <summary>Datagram destination; when null, replies go to the last peer we heard from (ground-station style).</summary>
    public EndPoint? Remote { get; set; }

    /// <summary>Send a HEARTBEAT every second (with <see cref="HeartbeatType"/>).</summary>
    public bool SendHeartbeats { get; set; } = true;

    /// <summary>MAV_TYPE in our heartbeats (GCS by default).</summary>
    public MavType HeartbeatType { get; set; } = MavType.Gcs;

    /// <summary>Friendly name.</summary>
    public string? Name { get; set; }

    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }

    /// <inheritdoc />
    public MavlinkConnectionOptions UseTransport(TransportFactory factory)
    {
        (TransportFactory, DatagramFactory) = (factory, null);
        return this;
    }

    /// <inheritdoc />
    public MavlinkConnectionOptions UseDatagramTransport(DatagramTransportFactory factory)
    {
        (DatagramFactory, TransportFactory) = (factory, null);
        return this;
    }

    /// <summary>Sends datagrams to <paramref name="remote"/> (e.g. a vehicle simulator sending to a GCS on 14550).</summary>
    public MavlinkConnectionOptions SendTo(EndPoint remote)
    {
        Remote = remote;
        return this;
    }
}

/// <summary>Link statistics.</summary>
public sealed class MavlinkLinkStatistics
{
    internal long Received, Sent, Lost;

    /// <summary>Valid frames received.</summary>
    public long PacketsReceived => Interlocked.Read(ref Received);

    /// <summary>Frames sent.</summary>
    public long PacketsSent => Interlocked.Read(ref Sent);

    /// <summary>Frames missing according to sequence-number gaps.</summary>
    public long PacketsLost => Interlocked.Read(ref Lost);

    /// <summary>Loss ratio (0–1).</summary>
    public double LossRatio => PacketsReceived + PacketsLost == 0 ? 0 : (double)PacketsLost / (PacketsReceived + PacketsLost);
}

/// <summary>A system/component we heard a heartbeat from.</summary>
/// <param name="SystemId">System id.</param>
/// <param name="ComponentId">Component id.</param>
/// <param name="Heartbeat">Last heartbeat.</param>
/// <param name="LastSeen">When.</param>
public sealed record MavlinkPeer(byte SystemId, byte ComponentId, Heartbeat Heartbeat, DateTimeOffset LastSeen);

/// <summary>
/// A MAVLink link over a stream (TCP, serial) or datagrams (UDP): sends messages with our system/component ids and a
/// running sequence, parses and decodes everything received, tracks peers and packet loss, and sends heartbeats.
/// </summary>
/// <example>
/// <code>
/// await using var link = MavlinkConnection.Create(o => o.UseUdp(14550));          // listen like a ground station
/// await link.ConnectAsync();
/// await foreach (var p in link.ReadAllAsync())
///     if (p.Message is Attitude a) Console.WriteLine($"roll {a.Roll:0.00} rad");
/// </code>
/// </example>
public sealed class MavlinkConnection : EndpointBase, IClientEndpoint, IPublisher<IMavlinkMessage>, ISubscriber<MavlinkPacket>
{
    private readonly MavlinkConnectionOptions _options;
    private readonly ConcurrentDictionary<Channel<MavlinkPacket>, byte> _readers = new();
    private readonly ConcurrentDictionary<(byte, byte), byte> _lastSeq = new();
    private readonly ConcurrentDictionary<(byte, byte), MavlinkPeer> _peers = new();
    private readonly SemaphoreSlim _send = new(1, 1);
    private ITransport? _stream;
    private IDatagramTransport? _datagrams;
    private EndPoint? _lastPeer;
    private CancellationTokenSource? _cts;
    private Task[] _loops = [];
    private int _sequence = -1;

    private MavlinkConnection(MavlinkConnectionOptions options) : base("mavlink", options.Logger)
    {
        _options = options;
        Name = options.Name;
        Parser = new MavlinkParser(options.Dialect, options.Signing);
    }

    /// <summary>Creates a connection (call <see cref="ConnectAsync"/>).</summary>
    public static MavlinkConnection Create(Action<MavlinkConnectionOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var o = new MavlinkConnectionOptions();
        configure(o);
        if (o.TransportFactory is null && o.DatagramFactory is null) throw new ArgumentException("Choose a transport: UseUdp, UseTcp, UseSerial or UseInMemory.", nameof(configure));
        return new MavlinkConnection(o);
    }

    /// <summary>Options.</summary>
    public MavlinkConnectionOptions Options => _options;

    /// <summary>The parser (CRC errors, unknown messages, signature failures).</summary>
    public MavlinkParser Parser { get; }

    /// <summary>Link statistics.</summary>
    public MavlinkLinkStatistics Statistics { get; } = new();

    /// <summary>Systems/components we heard heartbeats from.</summary>
    public IReadOnlyCollection<MavlinkPeer> Peers => [.. _peers.Values];

    /// <summary>Local datagram address (UDP), if any.</summary>
    public EndPoint? LocalEndPoint => _datagrams?.LocalEndPoint;

    /// <summary>Raised for every valid packet (on the receive thread).</summary>
    public event Action<MavlinkPacket>? PacketReceived;

    /// <inheritdoc />
    public async ValueTask ConnectAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_cts is not null) return;
        SetState(EndpointState.Connecting);
        try
        {
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            if (_options.DatagramFactory is { } df)
            {
                _datagrams = df();
                _loops = [Task.Run(() => DatagramLoopAsync(_datagrams, token), CancellationToken.None)];
            }
            else
            {
                _stream = _options.TransportFactory!();
                await _stream.OpenAsync(ct).ConfigureAwait(false);
                _loops = [Task.Run(() => StreamLoopAsync(_stream, token), CancellationToken.None)];
            }
            if (_options.SendHeartbeats) _loops = [.. _loops, Task.Run(() => HeartbeatLoopAsync(token), CancellationToken.None)];
            SetState(EndpointState.Connected);
        }
        catch (Exception ex)
        {
            SetState(EndpointState.Faulted, ex);
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisconnectAsync(CancellationToken ct = default)
    {
        if (_cts is null) return;
        SetState(EndpointState.Stopping);
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_datagrams is not null) await _datagrams.DisposeAsync().ConfigureAwait(false);
        if (_stream is not null) await _stream.DisposeAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_loops).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        foreach (var r in _readers.Keys) r.Writer.TryComplete();
        _cts.Dispose();
        (_cts, _datagrams, _stream, _loops) = (null, null, null, []);
        SetState(EndpointState.Disconnected);
    }

    /// <summary>Sends a message with our ids and the next sequence number.</summary>
    public async Task SendAsync(IMavlinkMessage message, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_cts is null) throw new InvalidOperationException("MAVLink connection is not open. Call ConnectAsync first.");
        // Sequence assignment and transmission are atomic, so frames leave in sequence order even when several
        // tasks send at once (otherwise the receiver would count reordering as packet loss).
        await _send.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var to = _datagrams is null ? null : _options.Remote ?? _lastPeer;
            if (_datagrams is not null && to is null) return; // nobody to talk to yet (a GCS answers once a vehicle has spoken)
            var seq = (byte)++_sequence;
            var frame = MavlinkCodec.Encode(message, seq, _options.SystemId, _options.ComponentId, _options.Version, _options.Signing);
            Tap(FrameDirection.Outbound, frame, message.ToString);
            Interlocked.Increment(ref Statistics.Sent);
            if (_datagrams is { } d) await d.SendAsync(frame, to!, ct).ConfigureAwait(false);
            else await _stream!.Pipe.Output.WriteAsync(frame, ct).ConfigureAwait(false);
        }
        finally
        {
            _send.Release();
        }
    }

    /// <summary>Streams every received packet (each call gets its own buffered view).</summary>
    public async IAsyncEnumerable<MavlinkPacket> ReadAllAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = Channel.CreateBounded<MavlinkPacket>(new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        _readers[channel] = 0;
        try
        {
            await foreach (var p in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false)) yield return p;
        }
        finally
        {
            _readers.TryRemove(channel, out _);
        }
    }

    /// <summary>Waits for the next message of type <typeparamref name="T"/> matching <paramref name="predicate"/>.</summary>
    public async Task<(T Message, MavlinkPacket Packet)?> WaitForAsync<T>(Func<T, MavlinkPacket, bool>? predicate, TimeSpan timeout, CancellationToken ct = default)
        where T : class, IMavlinkMessage
    {
        var tcs = new TaskCompletionSource<(T, MavlinkPacket)>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(MavlinkPacket p)
        {
            if (p.Message is T m && (predicate is null || predicate(m, p))) tcs.TrySetResult((m, p));
        }
        PacketReceived += Handler;
        try
        {
            return await tcs.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            PacketReceived -= Handler;
        }
    }

    /// <inheritdoc />
    public ValueTask PublishAsync(string topic, IMavlinkMessage message, PublishOptions? options = null, CancellationToken ct = default) =>
        new(SendAsync(message, ct));

    /// <summary>Subscribes to messages by MAVLink name (<c>ATTITUDE</c>, <c>*</c> for all).</summary>
    public async IAsyncEnumerable<Message<MavlinkPacket>> SubscribeAsync(string filter, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var p in ReadAllAsync(ct).ConfigureAwait(false))
        {
            var name = p.Message?.Name ?? p.MessageId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (filter is "*" or "#" || string.Equals(filter, name, StringComparison.OrdinalIgnoreCase))
                yield return new Message<MavlinkPacket>(name, p, p.Timestamp);
        }
    }

    private async Task StreamLoopAsync(ITransport transport, CancellationToken ct)
    {
        var input = transport.Pipe.Input;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await input.ReadAsync(ct).ConfigureAwait(false);
                foreach (var segment in result.Buffer) Process(segment.Span, null);
                input.AdvanceTo(result.Buffer.End);
                if (result.IsCompleted) break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "MAVLink stream closed");
            SetState(EndpointState.Faulted, ex);
        }
    }

    private async Task DatagramLoopAsync(IDatagramTransport transport, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var d = await transport.ReceiveAsync(ct).ConfigureAwait(false);
                Process(d.Data.Span, d.Remote);
            }
        }
        catch (OperationCanceledException) { }
        catch (ChannelClosedException) { }
        catch (ObjectDisposedException) { }
    }

    private void Process(ReadOnlySpan<byte> data, EndPoint? from)
    {
        lock (Parser)
        {
            Parser.Feed(data);
            while (Parser.TryRead(out var p))
            {
                if (from is not null) _lastPeer = from;
                Deliver(p);
            }
        }
    }

    private void Deliver(MavlinkPacket p)
    {
        Interlocked.Increment(ref Statistics.Received);
        var key = (p.SystemId, p.ComponentId);
        if (_lastSeq.TryGetValue(key, out var last))
        {
            var gap = (byte)(p.Sequence - last - 1);
            if (gap is > 0 and < 128) Interlocked.Add(ref Statistics.Lost, gap);
        }
        _lastSeq[key] = p.Sequence;
        if (p.Message is Heartbeat hb) _peers[key] = new MavlinkPeer(p.SystemId, p.ComponentId, hb, p.Timestamp);
        Tap(FrameDirection.Inbound, p.Frame.Span, p.ToString);
        foreach (var r in _readers.Keys) r.Writer.TryWrite(p);
        try
        {
            PacketReceived?.Invoke(p);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "MAVLink PacketReceived handler failed");
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            do
            {
                if (HeartbeatOverride is { } custom) await SendAsync(custom(), ct).ConfigureAwait(false);
                else
                    await SendAsync(new Heartbeat
                    {
                        Type = _options.HeartbeatType,
                        Autopilot = MavAutopilot.Invalid,
                        SystemStatus = MavState.Active,
                    }, ct).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IoTComException or System.Net.Sockets.SocketException or IOException)
        {
            Logger.LogDebug(ex, "MAVLink heartbeat failed");
        }
    }

    /// <summary>Supplies the heartbeat to send each second (vehicles report their state here).</summary>
    public Func<Heartbeat>? HeartbeatOverride { get; set; }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _send.Dispose();
    }
}
