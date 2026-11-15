using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using IoTCom.Net.Transports;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Protocols.Knx;

/// <summary>Options for <see cref="KnxRouter"/>.</summary>
public sealed class KnxRouterOptions : IDatagramBuilder<KnxRouterOptions>
{
    /// <summary>Multicast socket (default UDP 224.0.23.12:3671).</summary>
    public DatagramTransportFactory? TransportFactory { get; set; }

    /// <summary>Individual address this participant sends with (default 1.1.249).</summary>
    public KnxIndividualAddress Address { get; set; } = new(1, 1, 249);

    /// <summary>Refuses group writes (default true).</summary>
    public bool ReadOnly { get; set; } = true;

    /// <summary>Default read timeout (default 3 s).</summary>
    public TimeSpan ReadTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Endpoint name.</summary>
    public string? Name { get; set; }

    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }

    /// <inheritdoc />
    public KnxRouterOptions UseDatagramTransport(DatagramTransportFactory factory)
    {
        TransportFactory = factory;
        return this;
    }

    /// <summary>Allows group writes.</summary>
    public KnxRouterOptions AllowWrites()
    {
        ReadOnly = false;
        return this;
    }
}

/// <summary>
/// KNXnet/IP routing participant (KNX Standard 3/8/5): sends and receives ROUTING_INDICATIONs on the multicast group,
/// so it sees every telegram the KNX IP routers forward. Writes are refused until <see cref="KnxRouterOptions.AllowWrites"/>.
/// </summary>
public sealed class KnxRouter : EndpointBase, IClientEndpoint, IKnxGroupClient
{
    private static readonly IPEndPoint Group = new(KnxIp.RoutingMulticast, KnxIp.Port);
    private readonly KnxRouterOptions _options;
    private readonly ConcurrentDictionary<ushort, List<TaskCompletionSource<KnxGroupValue>>> _reads = new();
    private IDatagramTransport? _transport;
    private CancellationTokenSource? _cts;
    private Task? _receive;

    private KnxRouter(KnxRouterOptions options) : base("knxip", options.Logger)
    {
        _options = options;
        Name = options.Name;
    }

    /// <summary>Creates a routing participant.</summary>
    public static KnxRouter Create(Action<KnxRouterOptions>? configure = null)
    {
        var o = new KnxRouterOptions();
        configure?.Invoke(o);
        return new KnxRouter(o);
    }

    /// <inheritdoc />
    public bool ReadOnly => _options.ReadOnly;

    /// <summary>Individual address used as the source of sent telegrams.</summary>
    public KnxIndividualAddress Address => _options.Address;

    /// <inheritdoc />
    public event Action<KnxGroupEvent>? GroupValueReceived;

    /// <inheritdoc />
    public ValueTask ConnectAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_transport is not null) return ValueTask.CompletedTask;
        _transport = (_options.TransportFactory ?? (() => UdpDatagramTransport.Multicast(KnxIp.RoutingMulticast, KnxIp.Port)))();
        if (_transport is InMemoryDatagramTransport m) m.JoinMulticastGroup(KnxIp.RoutingMulticast);
        _cts = new CancellationTokenSource();
        _receive = Task.Run(() => ReceiveLoopAsync(_cts.Token), CancellationToken.None);
        SetState(EndpointState.Connected);
        return ValueTask.CompletedTask;
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Datagram d;
            try
            {
                d = await _transport!.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ObjectDisposedException or ChannelClosedException)
            {
                if (ct.IsCancellationRequested) break;
                continue;
            }

            try
            {
                var (service, body) = KnxIp.Parse(d.Data);
                if (service != KnxServiceType.RoutingIndication) continue;
                var cemi = KnxCemiFrame.Decode(body.Span);
                if (cemi.Source == _options.Address) continue;   // our own telegram, looped back
                Tap(FrameDirection.Inbound, d.Data.Span, () => KnxIp.Describe(d.Data));
                if (!cemi.IsGroup) continue;
                if (cemi.Apci == KnxApci.GroupValueResponse && _reads.TryRemove(cemi.Destination, out var waiters))
                    foreach (var w in waiters) w.TrySetResult(cemi.Value);
                GroupValueReceived?.Invoke(new KnxGroupEvent(cemi.Source, cemi.Group, cemi.Apci, cemi.Value, DateTimeOffset.UtcNow));
            }
            catch (ProtocolException ex)
            {
                Logger.LogDebug(ex, "KNX routing: bad frame from {Remote}", d.Remote);
            }
        }
    }

    private async Task SendAsync(KnxApci apci, KnxGroupAddress address, KnxGroupValue value, CancellationToken ct)
    {
        if (_transport is null) throw new InvalidOperationException("Not connected. Call ConnectAsync first.");
        var frame = KnxIp.RoutingIndication(KnxCemiFrame.GroupTelegram(KnxMessageCode.DataIndication, _options.Address, address, apci, value).Encode());
        Tap(FrameDirection.Outbound, frame, () => KnxIp.Describe(frame));
        await _transport.SendAsync(frame, Group, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task WriteAsync(KnxGroupAddress address, KnxGroupValue value, CancellationToken ct = default)
    {
        if (_options.ReadOnly) throw new ReadOnlyModeException($"Writing {address} changes devices on the KNX installation; this router is read-only until AllowWrites().");
        return SendAsync(KnxApci.GroupValueWrite, address, value, ct);
    }

    /// <inheritdoc />
    public async Task<KnxGroupValue> ReadAsync(KnxGroupAddress address, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<KnxGroupValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        var list = _reads.GetOrAdd(address.Raw, _ => []);
        lock (list) list.Add(tcs);
        try
        {
            await SendAsync(KnxApci.GroupValueRead, address, KnxGroupValue.Empty, ct).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(timeout ?? _options.ReadTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new IoTComTimeoutException($"No device answered the read of {address}.");
        }
        finally
        {
            lock (list) list.Remove(tcs);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisconnectAsync(CancellationToken ct = default)
    {
        if (_transport is null) return;
        await (_cts?.CancelAsync() ?? Task.CompletedTask).ConfigureAwait(false);
        await _transport.DisposeAsync().ConfigureAwait(false);
        if (_receive is not null) await _receive.ConfigureAwait(false);
        _transport = null;
        _cts?.Dispose();
        _cts = null;
        SetState(EndpointState.Disconnected);
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore() => await DisconnectAsync().ConfigureAwait(false);
}

/// <summary>Finds KNX IP interfaces and routers with SEARCH_REQUEST (KNX Standard 3/8/2).</summary>
public static class KnxDiscovery
{
    /// <summary>
    /// Multicasts a search to 224.0.23.12:3671 and collects the answers until <paramref name="timeout"/> (default 3 s).
    /// </summary>
    /// <param name="transport">Socket to search from (default: a UDP socket on any port).</param>
    /// <param name="timeout">How long to collect answers.</param>
    /// <param name="ct">Cancellation.</param>
    public static async Task<IReadOnlyList<KnxDeviceInfo>> SearchAsync(IDatagramTransport? transport = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var own = transport is null;
        transport ??= new UdpDatagramTransport(new IPEndPoint(IPAddress.Any, 0));
        var found = new Dictionary<string, KnxDeviceInfo>(StringComparer.Ordinal);
        try
        {
            await transport.SendAsync(KnxIp.SearchRequest(null), new IPEndPoint(KnxIp.RoutingMulticast, KnxIp.Port), ct).ConfigureAwait(false);
            using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
            window.CancelAfter(timeout ?? TimeSpan.FromSeconds(3));
            while (true)
            {
                Datagram d;
                try
                {
                    d = await transport.ReceiveAsync(window.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    var (service, body) = KnxIp.Parse(d.Data);
                    if (service != KnxServiceType.SearchResponse) continue;
                    var control = KnxIp.ReadHpai(body.Span);
                    if (control.Address.Equals(IPAddress.Any) && d.Remote is IPEndPoint remote) control = new IPEndPoint(remote.Address, control.Port);
                    var info = KnxIp.ReadDibs(body.Span[8..], control);
                    found[Convert.ToHexString(info.SerialNumber) + control] = info;
                }
                catch (ProtocolException)
                {
                }
            }
        }
        finally
        {
            if (own) await transport.DisposeAsync().ConfigureAwait(false);
        }

        return [.. found.Values];
    }
}
