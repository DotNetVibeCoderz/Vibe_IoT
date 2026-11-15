using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using IoTCom.Net.Transports;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Protocols.Knx;

/// <summary>Options for <see cref="KnxIpGateway"/>.</summary>
public sealed class KnxIpGatewayOptions : IDatagramBuilder<KnxIpGatewayOptions>
{
    /// <summary>Socket factory (<c>UseUdp(3671)</c> or <c>UseInMemory(net, endpoint)</c>).</summary>
    public DatagramTransportFactory? TransportFactory { get; set; }

    /// <summary>The KNX line behind the gateway.</summary>
    public KnxBus? Bus { get; set; }

    /// <summary>Individual address of the interface (default 1.1.0).</summary>
    public KnxIndividualAddress Address { get; set; } = new(1, 1, 0);

    /// <summary>First individual address handed to tunnelling connections (default 1.1.250).</summary>
    public KnxIndividualAddress FirstTunnelAddress { get; set; } = new(1, 1, 250);

    /// <summary>Simultaneous tunnelling connections (default 4).</summary>
    public int MaxTunnels { get; set; } = 4;

    /// <summary>Also act as a KNXnet/IP router: bus telegrams go out as routing indications and routing indications reach the bus (default true).</summary>
    public bool Routing { get; set; } = true;

    /// <summary>Friendly name in search and description responses.</summary>
    public string FriendlyName { get; set; } = "IoTCom KNX IP Router";

    /// <summary>KNX serial number (6 bytes).</summary>
    public byte[] SerialNumber { get; set; } = [0x00, 0xFA, 0x10, 0x00, 0x04, 0x17];

    /// <summary>MAC address shown in descriptions.</summary>
    public byte[] Mac { get; set; } = [0x02, 0x10, 0x17, 0x00, 0x04, 0x17];

    /// <summary>A tunnel without a heartbeat for this long is dropped (default 120 s, the specification's value).</summary>
    public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>How long the gateway waits for a client's TUNNELING_ACK (default 1 s, one retry).</summary>
    public TimeSpan AckTimeout { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Endpoint name.</summary>
    public string? Name { get; set; }

    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }

    /// <inheritdoc />
    public KnxIpGatewayOptions UseDatagramTransport(DatagramTransportFactory factory)
    {
        TransportFactory = factory;
        return this;
    }
}

/// <summary>A tunnelling connection as the gateway sees it.</summary>
public sealed class KnxTunnel
{
    internal KnxTunnel(byte channel, KnxIndividualAddress address, EndPoint control, EndPoint data) => (Channel, Address, Control, Data) = (channel, address, control, data);

    /// <summary>Communication channel id.</summary>
    public byte Channel { get; }

    /// <summary>Individual address given to the client.</summary>
    public KnxIndividualAddress Address { get; }

    /// <summary>Client control endpoint.</summary>
    public EndPoint Control { get; }

    /// <summary>Client data endpoint.</summary>
    public EndPoint Data { get; }

    /// <summary>Last heartbeat or request.</summary>
    public DateTimeOffset LastSeen { get; internal set; } = DateTimeOffset.UtcNow;

    internal byte NextReceive;
    internal byte NextSend;
    internal readonly Channel<byte[]> Outbox = System.Threading.Channels.Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    internal readonly ConcurrentDictionary<byte, TaskCompletionSource<byte>> Acks = new();
    internal Task? Worker;

    /// <inheritdoc />
    public override string ToString() => $"channel {Channel}, {Address}, {Data}";
}

/// <summary>
/// A KNX IP interface/router in software: answers search and description requests, accepts tunnelling connections
/// (assigning individual addresses, heartbeat supervision, sequence counters, acknowledgements with retry), confirms
/// L_Data.req with L_Data.con, forwards bus telegrams to every tunnel and, with routing on, to the multicast group.
/// Put a <see cref="KnxBuildingSimulator"/> on its <see cref="KnxBus"/> for a complete installation without hardware.
/// </summary>
public sealed class KnxIpGateway : EndpointBase, IServerEndpoint
{
    private readonly KnxIpGatewayOptions _options;
    private readonly ConcurrentDictionary<byte, KnxTunnel> _tunnels = new();
    private readonly KnxBus _bus;
    private readonly bool _ownsBus;
    private IDatagramTransport? _transport;
    private CancellationTokenSource? _cts;
    private Task? _receive, _supervise;

    private KnxIpGateway(KnxIpGatewayOptions options) : base("knxip", options.Logger)
    {
        _options = options;
        Name = options.Name ?? options.FriendlyName;
        _ownsBus = options.Bus is null;
        _bus = options.Bus ?? new KnxBus();
    }

    /// <summary>Creates a gateway.</summary>
    public static KnxIpGateway Create(Action<KnxIpGatewayOptions> configure)
    {
        var o = new KnxIpGatewayOptions();
        configure(o);
        return new KnxIpGateway(o);
    }

    /// <summary>The KNX line.</summary>
    public KnxBus Bus => _bus;

    /// <summary>Open tunnelling connections.</summary>
    public IReadOnlyCollection<KnxTunnel> Tunnels => [.. _tunnels.Values];

    /// <summary>Raised when a client connects.</summary>
    public event Action<KnxTunnel>? TunnelOpened;

    /// <summary>Raised when a tunnel closes (with the reason).</summary>
    public event Action<KnxTunnel, string>? TunnelClosed;

    /// <summary>The bound endpoint (after start).</summary>
    public EndPoint? LocalEndPoint => _transport?.LocalEndPoint;

    private KnxDeviceInfo Device => new()
    {
        Address = _options.Address, SerialNumber = _options.SerialNumber, Mac = _options.Mac, FriendlyName = _options.FriendlyName,
        Services = _options.Routing ? [(2, 1), (3, 1), (4, 1), (5, 1)] : [(2, 1), (3, 1), (4, 1)],
    };

    /// <inheritdoc />
    public async ValueTask StartAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_transport is not null) return;
        SetState(EndpointState.Connecting);
        _transport = (_options.TransportFactory ?? (() => new UdpDatagramTransport(new IPEndPoint(IPAddress.Any, KnxIp.Port), reuseAddress: true)))();
        try
        {
            switch (_transport)
            {
                case InMemoryDatagramTransport m: m.JoinMulticastGroup(KnxIp.RoutingMulticast); break;
                case UdpDatagramTransport u: u.JoinMulticastGroup(KnxIp.RoutingMulticast); break;
            }
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or InvalidOperationException)
        {
            Logger.LogWarning(ex, "KNX gateway could not join {Group}; discovery and routing need multicast", KnxIp.RoutingMulticast);
        }

        _cts = new CancellationTokenSource();
        _bus.Telegram += OnBusTelegram;
        _receive = Task.Run(() => ReceiveLoopAsync(_cts.Token), CancellationToken.None);
        _supervise = Task.Run(() => SuperviseAsync(_cts.Token), CancellationToken.None);
        await Task.CompletedTask.ConfigureAwait(false);
        SetState(EndpointState.Listening);
    }

    private IPEndPoint ControlEndpoint => _transport?.LocalEndPoint as IPEndPoint ?? new IPEndPoint(IPAddress.Loopback, KnxIp.Port);

    private async Task SendAsync(byte[] frame, EndPoint to, CancellationToken ct)
    {
        Tap(FrameDirection.Outbound, frame, () => KnxIp.Describe(frame));
        await _transport!.SendAsync(frame, to, ct).ConfigureAwait(false);
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

            Tap(FrameDirection.Inbound, d.Data.Span, () => KnxIp.Describe(d.Data));
            try
            {
                await HandleAsync(d, ct).ConfigureAwait(false);
            }
            catch (ProtocolException ex)
            {
                Logger.LogDebug(ex, "KNX gateway: bad frame from {Remote}", d.Remote);
            }
        }
    }

    private async Task HandleAsync(Datagram d, CancellationToken ct)
    {
        var (service, body) = KnxIp.Parse(d.Data);
        var b = body.Span;
        switch (service)
        {
            case KnxServiceType.SearchRequest:
                await SendAsync(KnxIp.SearchResponse(ControlEndpoint, Device), KnxIp.Route(KnxIp.ReadHpai(b), d.Remote), ct).ConfigureAwait(false);
                break;
            case KnxServiceType.DescriptionRequest:
                await SendAsync(KnxIp.DescriptionResponse(Device), KnxIp.Route(KnxIp.ReadHpai(b), d.Remote), ct).ConfigureAwait(false);
                break;
            case KnxServiceType.ConnectRequest:
                await ConnectAsync(d, body, ct).ConfigureAwait(false);
                break;
            case KnxServiceType.ConnectionStateRequest:
            {
                var known = _tunnels.TryGetValue(b[0], out var t);
                if (known) t!.LastSeen = DateTimeOffset.UtcNow;
                await SendAsync(KnxIp.ConnectionStateResponse(b[0], known ? KnxIpStatus.NoError : KnxIpStatus.ConnectionId), KnxIp.Route(KnxIp.ReadHpai(b[2..]), d.Remote), ct).ConfigureAwait(false);
                break;
            }

            case KnxServiceType.DisconnectRequest:
            {
                var known = _tunnels.TryRemove(b[0], out var t);
                await SendAsync(KnxIp.DisconnectResponse(b[0], known ? KnxIpStatus.NoError : KnxIpStatus.ConnectionId), KnxIp.Route(KnxIp.ReadHpai(b[2..]), d.Remote), ct).ConfigureAwait(false);
                if (known) Close(t!, "disconnected by the client");
                break;
            }

            case KnxServiceType.TunnelingRequest:
                await TunnelRequestAsync(d, body, ct).ConfigureAwait(false);
                break;
            case KnxServiceType.TunnelingAck:
            {
                var (channel, seq, status, _) = KnxIp.ReadConnectionHeader(body);
                if (_tunnels.TryGetValue(channel, out var t) && t.Acks.TryRemove(seq, out var tcs)) tcs.TrySetResult(status);
                break;
            }

            case KnxServiceType.RoutingIndication when _options.Routing:
                if (_recentRouting.TryRemove(Convert.ToHexString(b), out var sentAt) && Environment.TickCount64 - sentAt < 2000) break;   // our own, looped back
                var cemi = KnxCemiFrame.Decode(b);
                _bus.Send(cemi, RoutingOrigin);
                break;
        }
    }

    private static readonly object RoutingOrigin = new();
    private readonly ConcurrentDictionary<string, long> _recentRouting = new();

    private async Task ConnectAsync(Datagram d, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        var (control, data, status) = ParseConnect(d, body.Span);
        if (status != KnxIpStatus.NoError)
        {
            await SendAsync(KnxIp.ConnectResponse(0, status, null, default), control, ct).ConfigureAwait(false);
            return;
        }

        byte channel = 1;
        while (_tunnels.ContainsKey(channel)) channel++;
        var used = _tunnels.Values.Select(t => t.Address.Raw).ToHashSet();
        var address = _options.FirstTunnelAddress.Raw;
        while (used.Contains(address)) address++;
        var tunnel = new KnxTunnel(channel, new KnxIndividualAddress(address), control, data);
        _tunnels[channel] = tunnel;
        tunnel.Worker = Task.Run(() => TunnelWorkerAsync(tunnel, _cts!.Token), CancellationToken.None);
        await SendAsync(KnxIp.ConnectResponse(channel, KnxIpStatus.NoError, ControlEndpoint, tunnel.Address), control, ct).ConfigureAwait(false);
        Logger.LogInformation("KNX tunnel {Channel} opened for {Data} as {Address}", channel, data, tunnel.Address);
        TunnelOpened?.Invoke(tunnel);
    }

    private (EndPoint Control, EndPoint Data, byte Status) ParseConnect(Datagram d, ReadOnlySpan<byte> b)
    {
        var control = KnxIp.Route(KnxIp.ReadHpai(b), d.Remote);
        var data = KnxIp.Route(KnxIp.ReadHpai(b[8..]), d.Remote);
        var cri = b[16..];
        byte status = KnxIpStatus.NoError;
        if (cri.Length < 2 || cri[1] != 0x04) status = KnxIpStatus.ConnectionType;
        else if (cri.Length < 4 || cri[2] != 0x02) status = KnxIpStatus.TunnellingLayer;
        else if (_tunnels.Count >= _options.MaxTunnels) status = KnxIpStatus.NoMoreConnections;
        return (control, data, status);
    }

    private async Task TunnelRequestAsync(Datagram d, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        var (channel, seq, _, rest) = KnxIp.ReadConnectionHeader(body);
        if (!_tunnels.TryGetValue(channel, out var t)) return;
        t.LastSeen = DateTimeOffset.UtcNow;
        if (seq == (byte)(t.NextReceive - 1))
        {
            await SendAsync(KnxIp.TunnelingAck(channel, seq, KnxIpStatus.NoError), t.Data, ct).ConfigureAwait(false);   // a repetition: ack again, do not act twice
            return;
        }

        if (seq != t.NextReceive) return;   // out of sequence: discard without ack (the client repeats)
        t.NextReceive++;
        await SendAsync(KnxIp.TunnelingAck(channel, seq, KnxIpStatus.NoError), t.Data, ct).ConfigureAwait(false);
        var cemi = KnxCemiFrame.Decode(rest.Span);
        if (cemi.MessageCode != KnxMessageCode.DataRequest) return;
        var onBus = cemi with { Source = cemi.Source.Raw == 0 ? t.Address : cemi.Source };
        t.Outbox.Writer.TryWrite(KnxIp.TunnelingRequest(channel, 0, (onBus with { MessageCode = KnxMessageCode.DataConfirmation }).Encode()));
        _bus.Send(onBus, t);
    }

    private void OnBusTelegram(KnxCemiFrame frame, object? origin)
    {
        var cemi = frame.Encode();
        foreach (var t in _tunnels.Values)
        {
            if (ReferenceEquals(origin, t)) continue;   // the sender got its L_Data.con
            t.Outbox.Writer.TryWrite(KnxIp.TunnelingRequest(t.Channel, 0, cemi));
        }

        if (_options.Routing && !ReferenceEquals(origin, RoutingOrigin) && _transport is not null && _cts is { IsCancellationRequested: false } cts)
        {
            var frameBytes = KnxIp.RoutingIndication(cemi);
            if (_recentRouting.Count > 256) _recentRouting.Clear();
            _recentRouting[Convert.ToHexString(cemi)] = Environment.TickCount64;
            _ = SendAsync(frameBytes, new IPEndPoint(KnxIp.RoutingMulticast, KnxIp.Port), cts.Token).ContinueWith(static _ => { }, TaskScheduler.Default);
        }
    }

    /// <summary>Sends queued TUNNELING_REQUESTs to one client in order, each waiting for its ack (one retry).</summary>
    private async Task TunnelWorkerAsync(KnxTunnel t, CancellationToken ct)
    {
        try
        {
            await foreach (var template in t.Outbox.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                var seq = t.NextSend;
                var frame = (byte[])template.Clone();
                frame[8] = seq;                               // header (6) + structure length + channel → sequence at offset 8
                var acked = false;
                for (var attempt = 0; attempt < 2 && !acked; attempt++)
                {
                    var tcs = new TaskCompletionSource<byte>(TaskCreationOptions.RunContinuationsAsynchronously);
                    t.Acks[seq] = tcs;
                    await SendAsync(frame, t.Data, ct).ConfigureAwait(false);
                    try
                    {
                        await tcs.Task.WaitAsync(_options.AckTimeout, ct).ConfigureAwait(false);
                        acked = true;
                    }
                    catch (TimeoutException)
                    {
                        t.Acks.TryRemove(seq, out _);
                    }
                }

                t.NextSend++;
                if (!acked && _tunnels.TryRemove(t.Channel, out _))
                {
                    await SendAsync(KnxIp.DisconnectRequest(t.Channel, ControlEndpoint), t.Control, ct).ConfigureAwait(false);
                    Close(t, "no TUNNELING_ACK after a retry");
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ObjectDisposedException or ChannelClosedException)
        {
        }
    }

    private async Task SuperviseAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(5, _options.ConnectionTimeout.TotalSeconds / 4)), ct).ConfigureAwait(false);
                foreach (var t in _tunnels.Values)
                {
                    if (DateTimeOffset.UtcNow - t.LastSeen > _options.ConnectionTimeout && _tunnels.TryRemove(t.Channel, out _))
                        Close(t, "no heartbeat");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Close(KnxTunnel t, string reason)
    {
        t.Outbox.Writer.TryComplete();
        Logger.LogInformation("KNX tunnel {Channel} closed: {Reason}", t.Channel, reason);
        TunnelClosed?.Invoke(t, reason);
    }

    /// <inheritdoc />
    public async ValueTask StopAsync(CancellationToken ct = default)
    {
        if (_transport is null) return;
        SetState(EndpointState.Stopping);
        _bus.Telegram -= OnBusTelegram;
        foreach (var t in _tunnels.Values)
        {
            try
            {
                await SendAsync(KnxIp.DisconnectRequest(t.Channel, ControlEndpoint), t.Control, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ObjectDisposedException)
            {
            }

            Close(t, "gateway stopping");
        }

        _tunnels.Clear();
        await (_cts?.CancelAsync() ?? Task.CompletedTask).ConfigureAwait(false);
        await _transport.DisposeAsync().ConfigureAwait(false);
        if (_receive is not null) await _receive.ConfigureAwait(false);
        if (_supervise is not null) await _supervise.ConfigureAwait(false);
        _transport = null;
        _cts?.Dispose();
        _cts = null;
        if (_ownsBus) await _bus.DisposeAsync().ConfigureAwait(false);
        SetState(EndpointState.Disconnected);
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore() => await StopAsync().ConfigureAwait(false);
}
