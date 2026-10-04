using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using IoTCom.Net.Framing;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Protocols.Nmea;

/// <summary>Options for <see cref="NmeaReader"/>.</summary>
public sealed class NmeaReaderOptions : ITransportBuilder<NmeaReaderOptions>
{
    /// <summary>Transport (GPS receiver on serial, NMEA-over-TCP multiplexer, in-memory simulator...).</summary>
    public TransportFactory? TransportFactory { get; set; }
    /// <summary>Drop sentences with a wrong checksum (default true).</summary>
    public bool RequireValidChecksum { get; set; } = true;
    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }
    /// <summary>Endpoint name.</summary>
    public string? Name { get; set; }

    /// <inheritdoc />
    public NmeaReaderOptions UseTransport(TransportFactory factory) { TransportFactory = factory; return this; }
    /// <summary>Accepts sentences with a bad checksum (they are still flagged).</summary>
    public NmeaReaderOptions AcceptInvalidChecksums() { RequireValidChecksum = false; return this; }
    /// <summary>Sets the logger.</summary>
    public NmeaReaderOptions WithLogger(ILogger logger) { Logger = logger; return this; }
}

/// <summary>
/// Consumer of an NMEA 0183 stream. Exposes typed messages as an async stream (subscriber role), raises
/// <see cref="MessageReceived"/>, and maintains a consolidated <see cref="Gnss"/> fix.
/// </summary>
/// <example>
/// <code>
/// await using var gps = NmeaReader.Create(o => o.UseSerial("COM4", 9600));
/// await gps.ConnectAsync();
/// await foreach (var msg in gps.ReadAllAsync()) Console.WriteLine(msg);
/// </code>
/// </example>
public sealed class NmeaReader : EndpointBase, IClientEndpoint, ISubscriber<NmeaMessage>
{
    private readonly NmeaReaderOptions _options;
    private readonly List<Channel<NmeaMessage>> _subscribers = [];
    private readonly Lock _gate = new();
    private ITransport? _transport;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    private NmeaReader(NmeaReaderOptions options) : base("nmea0183", options.Logger)
    {
        _options = options;
        Name = options.Name;
    }

    /// <summary>Creates a reader.</summary>
    public static NmeaReader Create(Action<NmeaReaderOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var o = new NmeaReaderOptions();
        configure(o);
        if (o.TransportFactory is null) throw new ArgumentException("A transport is required.", nameof(configure));
        return new NmeaReader(o);
    }

    /// <summary>Consolidated GNSS state.</summary>
    public GnssState Gnss { get; } = new();

    /// <summary>Raised for every decoded message (on the reader thread).</summary>
    public event Action<NmeaMessage>? MessageReceived;

    /// <summary>Number of sentences dropped (bad checksum / malformed).</summary>
    public long DroppedSentences { get; private set; }

    /// <inheritdoc />
    public async ValueTask ConnectAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_transport is not null) return;
        SetState(EndpointState.Connecting);
        var transport = _options.TransportFactory!();
        try
        {
            await transport.OpenAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            SetState(EndpointState.Disconnected, ex);
            throw;
        }
        _transport = transport;
        _cts = new CancellationTokenSource();
        var token = _cts.Token; // capture now: DisconnectAsync may clear _cts before the loop task starts
        _loop = Task.Run(() => ReadLoopAsync(transport, token), CancellationToken.None);
        SetState(EndpointState.Connected);
    }

    /// <inheritdoc />
    public async ValueTask DisconnectAsync(CancellationToken ct = default)
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts is null) return;
        SetState(EndpointState.Stopping);
        await cts.CancelAsync().ConfigureAwait(false);
        if (_transport is not null) await _transport.DisposeAsync().ConfigureAwait(false);
        _transport = null;
        if (_loop is not null) await _loop.ConfigureAwait(false);
        cts.Dispose();
        SetState(EndpointState.Disconnected);
    }

    /// <summary>Streams every message until cancelled.</summary>
    public IAsyncEnumerable<NmeaMessage> ReadAllAsync(CancellationToken ct = default) => ReadFilteredAsync(null, ct);

    /// <summary>Streams messages of type <typeparamref name="T"/> (e.g. <see cref="GgaMessage"/>).</summary>
    public async IAsyncEnumerable<T> ReadAsync<T>([EnumeratorCancellation] CancellationToken ct = default) where T : NmeaMessage
    {
        await foreach (var m in ReadFilteredAsync(null, ct).ConfigureAwait(false))
            if (m is T t) yield return t;
    }

    /// <summary>
    /// Subscribes with a filter on the sentence type (<c>"GGA"</c>), address (<c>"GPRMC"</c>) or <c>"*"</c>/<c>"#"</c> for all.
    /// </summary>
    public async IAsyncEnumerable<Message<NmeaMessage>> SubscribeAsync(string filter, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var m in ReadFilteredAsync(filter, ct).ConfigureAwait(false))
            yield return new Message<NmeaMessage>(m.Sentence.Talker + m.Sentence.Type, m, DateTimeOffset.UtcNow);
    }

    private async IAsyncEnumerable<NmeaMessage> ReadFilteredAsync(string? filter, [EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateBounded<NmeaMessage>(new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        lock (_gate) _subscribers.Add(channel);
        try
        {
            await foreach (var m in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (filter is null or "*" or "#" || m.Sentence.Type == filter || m.Sentence.Talker + m.Sentence.Type == filter)
                    yield return m;
            }
        }
        finally
        {
            lock (_gate) _subscribers.Remove(channel);
        }
    }

    /// <summary>Processes one line (also usable without a transport, e.g. for log replay).</summary>
    public NmeaMessage? Process(string line)
    {
        if (!NmeaSentence.TryParse(line, out var sentence, _options.RequireValidChecksum))
        {
            DroppedSentences++;
            return null;
        }
        var message = NmeaParser.Decode(sentence!);
        Gnss.Apply(message);
        MessageReceived?.Invoke(message);
        Channel<NmeaMessage>[] subs;
        lock (_gate) subs = [.. _subscribers];
        foreach (var s in subs) s.Writer.TryWrite(message);
        return message;
    }

    private async Task ReadLoopAsync(ITransport transport, CancellationToken ct)
    {
        try
        {
            await foreach (var frame in transport.Pipe.Input.ReadFramesAsync(new LineFraming(), ct).ConfigureAwait(false))
            {
                Tap(FrameDirection.Inbound, frame, () => Encoding.ASCII.GetString(frame));
                Process(Encoding.ASCII.GetString(frame));
            }
            if (!ct.IsCancellationRequested) SetState(EndpointState.Disconnected);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "NMEA stream failed");
            SetState(EndpointState.Faulted, ex);
        }
        finally
        {
            Channel<NmeaMessage>[] subs;
            lock (_gate) subs = [.. _subscribers];
            if (!ct.IsCancellationRequested) foreach (var s in subs) s.Writer.TryComplete();
        }
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore() => await DisconnectAsync().ConfigureAwait(false);
}
