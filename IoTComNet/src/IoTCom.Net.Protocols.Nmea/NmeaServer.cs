using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Protocols.Nmea;

/// <summary>Options for <see cref="NmeaServer"/>.</summary>
public sealed class NmeaServerOptions : IListenerBuilder<NmeaServerOptions>
{
    /// <summary>Listener (TCP, in-memory).</summary>
    public TransportListenerFactory? ListenerFactory { get; set; }
    /// <summary>Single output transport (serial port).</summary>
    public TransportFactory? TransportFactory { get; set; }
    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }

    /// <inheritdoc />
    public NmeaServerOptions UseListener(TransportListenerFactory factory) { ListenerFactory = factory; return this; }
    /// <inheritdoc />
    public NmeaServerOptions UseTransport(TransportFactory factory) { TransportFactory = factory; return this; }
}

/// <summary>
/// NMEA publisher: broadcasts sentences to every connected client (NMEA-over-TCP multiplexer, like a chart plotter
/// gateway) or to a serial port. Pair it with <see cref="NmeaSimulator"/> to emulate a GPS receiver.
/// </summary>
public sealed class NmeaServer : EndpointBase, IServerEndpoint, IPublisher<string>
{
    private readonly NmeaServerOptions _options;
    private readonly ConcurrentDictionary<int, ITransport> _clients = new();
    private ITransportListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private int _ids;

    private NmeaServer(NmeaServerOptions options) : base("nmea0183", options.Logger) => _options = options;

    /// <summary>Creates a server.</summary>
    public static NmeaServer Create(Action<NmeaServerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var o = new NmeaServerOptions();
        configure(o);
        if (o.ListenerFactory is null && o.TransportFactory is null) throw new ArgumentException("A listener or transport is required.", nameof(configure));
        return new NmeaServer(o);
    }

    /// <summary>Connected clients.</summary>
    public int ClientCount => _clients.Count;

    /// <summary>Local address (after start).</summary>
    public string? LocalAddress => _listener?.LocalAddress;

    /// <inheritdoc />
    public async ValueTask StartAsync(CancellationToken ct = default)
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        if (_options.ListenerFactory is not null)
        {
            _listener = _options.ListenerFactory();
            await _listener.StartAsync(ct).ConfigureAwait(false);
            _ = Task.Run(() => AcceptLoopAsync(_listener, _cts.Token), CancellationToken.None);
        }
        else
        {
            var t = _options.TransportFactory!();
            await t.OpenAsync(ct).ConfigureAwait(false);
            _clients[Interlocked.Increment(ref _ids)] = t;
        }
        SetState(EndpointState.Listening);
    }

    /// <inheritdoc />
    public async ValueTask StopAsync(CancellationToken ct = default)
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts is null) return;
        await cts.CancelAsync().ConfigureAwait(false);
        if (_listener is not null) await _listener.DisposeAsync().ConfigureAwait(false);
        foreach (var c in _clients.Values) await c.DisposeAsync().ConfigureAwait(false);
        _clients.Clear();
        cts.Dispose();
        SetState(EndpointState.Disconnected);
    }

    /// <summary>Broadcasts one sentence (CR/LF appended). <paramref name="topic"/> is ignored (NMEA has no topics).</summary>
    public ValueTask PublishAsync(string topic, string message, PublishOptions? options = null, CancellationToken ct = default) => BroadcastAsync(message, ct);

    /// <summary>Broadcasts sentences to every client; clients that fail are dropped.</summary>
    public async ValueTask BroadcastAsync(string sentence, CancellationToken ct = default)
    {
        var bytes = Encoding.ASCII.GetBytes(sentence.EndsWith("\r\n", StringComparison.Ordinal) ? sentence : sentence + "\r\n");
        Tap(FrameDirection.Outbound, bytes, () => sentence);
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var (id, client) in _clients)
            {
                try
                {
                    await client.Pipe.Output.WriteAsync(bytes, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (_clients.TryRemove(id, out var dead)) await dead.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task AcceptLoopAsync(ITransportListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptAsync(ct).ConfigureAwait(false);
                var id = Interlocked.Increment(ref _ids);
                _clients[id] = client;
                Logger.LogDebug("NMEA client {Id} connected from {Peer}", id.ToString(CultureInfo.InvariantCulture), client.Info.RemoteAddress);
            }
            catch (Exception) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "NMEA accept failed");
                break;
            }
        }
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore()
    {
        await StopAsync().ConfigureAwait(false);
        _sendLock.Dispose();
    }
}
