using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using IoTCom.Net.Transports;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Protocols.Knx;

/// <summary>Options for <see cref="KnxTunnelClient"/>.</summary>
public sealed class KnxTunnelClientOptions : IDatagramBuilder<KnxTunnelClientOptions>
{
    /// <summary>Local socket (default: any UDP port).</summary>
    public DatagramTransportFactory? TransportFactory { get; set; }

    /// <summary>The KNX IP interface's control endpoint.</summary>
    public EndPoint? Gateway { get; set; }

    /// <summary>Refuses group writes (default true). Reads and monitoring stay allowed.</summary>
    public bool ReadOnly { get; set; } = true;

    /// <summary>Ask the interface to answer to the packet's source (NAT mode, HPAI 0.0.0.0:0; default true).</summary>
    public bool NatMode { get; set; } = true;

    /// <summary>Heartbeat interval (CONNECTIONSTATE_REQUEST; default 60 s, the specification's value).</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Wait for TUNNELING_ACK (default 1 s, one retry).</summary>
    public TimeSpan AckTimeout { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Wait for L_Data.con after the ack (default 3 s).</summary>
    public TimeSpan ConfirmTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Wait for connect, heartbeat and disconnect responses (default 10 s).</summary>
    public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Default timeout of <see cref="KnxTunnelClient.ReadAsync"/> (default 3 s).</summary>
    public TimeSpan ReadTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Endpoint name.</summary>
    public string? Name { get; set; }

    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }

    /// <inheritdoc />
    public KnxTunnelClientOptions UseDatagramTransport(DatagramTransportFactory factory)
    {
        TransportFactory = factory;
        return this;
    }

    /// <summary>Sets the interface address.</summary>
    public KnxTunnelClientOptions UseGateway(string host, int port = KnxIp.Port)
    {
        Gateway = IPAddress.TryParse(host, out var ip) ? new IPEndPoint(ip, port) : new DnsEndPoint(host, port);
        return this;
    }

    /// <summary>Sets the interface address.</summary>
    public KnxTunnelClientOptions UseGateway(IPEndPoint endpoint)
    {
        Gateway = endpoint;
        return this;
    }

    /// <summary>Allows group writes.</summary>
    public KnxTunnelClientOptions AllowWrites()
    {
        ReadOnly = false;
        return this;
    }
}

/// <summary>
/// KNXnet/IP tunnelling client (KNX Standard 3/8/4): connects to a KNX IP interface or router, keeps the connection
/// alive with heartbeats, sends group telegrams with sequence counters, acknowledgement retry and L_Data.con, and
/// reports every group telegram on the line. Writes are refused until <see cref="KnxTunnelClientOptions.AllowWrites"/>.
/// </summary>
public sealed class KnxTunnelClient : EndpointBase, IClientEndpoint, IKnxGroupClient
{
    private readonly KnxTunnelClientOptions _options;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly ConcurrentDictionary<byte, TaskCompletionSource<byte>> _acks = new();
    private readonly ConcurrentDictionary<ushort, List<TaskCompletionSource<KnxGroupValue>>> _reads = new();
    private IDatagramTransport? _transport;
    private EndPoint? _gateway;
    private CancellationTokenSource? _cts;
    private Task? _receive, _heartbeat;
    private TaskCompletionSource<byte[]>? _pendingResponse;
    private KnxServiceType _pendingService;
    private TaskCompletionSource<KnxCemiFrame>? _pendingConfirm;
    private byte _channel, _sendSeq, _receiveSeq;

    private KnxTunnelClient(KnxTunnelClientOptions options) : base("knxip", options.Logger)
    {
        _options = options;
        Name = options.Name;
    }

    /// <summary>Creates a client.</summary>
    public static KnxTunnelClient Create(Action<KnxTunnelClientOptions> configure)
    {
        var o = new KnxTunnelClientOptions();
        configure(o);
        if (o.Gateway is null) throw new ArgumentException("Set the interface address with UseGateway(host).");
        return new KnxTunnelClient(o);
    }

    /// <inheritdoc />
    public bool ReadOnly => _options.ReadOnly;

    /// <summary>Individual address the interface assigned to this connection.</summary>
    public KnxIndividualAddress Address { get; private set; }

    /// <summary>Communication channel id.</summary>
    public byte Channel => _channel;

    /// <inheritdoc />
    public event Action<KnxGroupEvent>? GroupValueReceived;

    /// <summary>Raised when the interface closes the connection or stops answering heartbeats.</summary>
    public event Action<string>? ConnectionLost;

    private IPEndPoint? Hpai => _options.NatMode ? null : _transport?.LocalEndPoint as IPEndPoint;

    /// <inheritdoc />
    public async ValueTask ConnectAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (State == EndpointState.Connected) return;
        SetState(EndpointState.Connecting);
        try
        {
            _gateway = _options.Gateway is DnsEndPoint dns
                ? new IPEndPoint((await Dns.GetHostAddressesAsync(dns.Host, System.Net.Sockets.AddressFamily.InterNetwork, ct).ConfigureAwait(false))[0], dns.Port)
                : _options.Gateway;
            _transport = (_options.TransportFactory ?? (() => new UdpDatagramTransport(new IPEndPoint(IPAddress.Any, 0))))();
            _cts = new CancellationTokenSource();
            _receive = Task.Run(() => ReceiveLoopAsync(_cts.Token), CancellationToken.None);
            var response = await RequestAsync(KnxIp.ConnectRequest(Hpai, Hpai), KnxServiceType.ConnectResponse, ct).ConfigureAwait(false);
            var (channel, status, _, assigned) = KnxIp.ReadConnectResponse(response);
            if (status != KnxIpStatus.NoError) throw new DeviceException($"The KNX interface refused the connection: {KnxIpStatus.Name(status)}.", status);
            (_channel, Address, _sendSeq, _receiveSeq) = (channel, assigned, 0, 0);
            _heartbeat = Task.Run(() => HeartbeatAsync(_cts.Token), CancellationToken.None);
            SetState(EndpointState.Connected);
        }
        catch (Exception ex)
        {
            await TeardownAsync().ConfigureAwait(false);
            SetState(EndpointState.Faulted, ex);
            throw;
        }
    }

    private async Task<byte[]> RequestAsync(byte[] frame, KnxServiceType expected, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        (_pendingResponse, _pendingService) = (tcs, expected);
        await SendRawAsync(frame, ct).ConfigureAwait(false);
        try
        {
            return await tcs.Task.WaitAsync(_options.ResponseTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new IoTComTimeoutException($"The KNX interface at {_gateway} did not answer {KnxIp.Name(expected - 1)} within {_options.ResponseTimeout.TotalSeconds:0} s.");
        }
        finally
        {
            _pendingResponse = null;
        }
    }

    private async Task SendRawAsync(byte[] frame, CancellationToken ct)
    {
        Tap(FrameDirection.Outbound, frame, () => KnxIp.Describe(frame));
        await _transport!.SendAsync(frame, _gateway!, ct).ConfigureAwait(false);
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
                await HandleAsync(d.Data, ct).ConfigureAwait(false);
            }
            catch (ProtocolException ex)
            {
                Logger.LogDebug(ex, "KNX tunnel: bad frame");
            }
        }
    }

    private async Task HandleAsync(ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        var (service, body) = KnxIp.Parse(frame);
        switch (service)
        {
            case KnxServiceType.ConnectResponse or KnxServiceType.ConnectionStateResponse or KnxServiceType.DisconnectResponse:
                if (_pendingResponse is { } p && _pendingService == service) p.TrySetResult(body.ToArray());
                break;
            case KnxServiceType.DisconnectRequest when body.Span[0] == _channel:
                await SendRawAsync(KnxIp.DisconnectResponse(_channel, KnxIpStatus.NoError), ct).ConfigureAwait(false);
                Lost("the interface closed the connection");
                break;
            case KnxServiceType.TunnelingAck:
            {
                var (channel, seq, status, _) = KnxIp.ReadConnectionHeader(body);
                if (channel == _channel && _acks.TryRemove(seq, out var tcs)) tcs.TrySetResult(status);
                break;
            }

            case KnxServiceType.TunnelingRequest:
            {
                var (channel, seq, _, rest) = KnxIp.ReadConnectionHeader(body);
                if (channel != _channel) break;
                if (seq == (byte)(_receiveSeq - 1))
                {
                    await SendRawAsync(KnxIp.TunnelingAck(channel, seq, KnxIpStatus.NoError), ct).ConfigureAwait(false);   // repetition
                    break;
                }

                if (seq != _receiveSeq) break;
                _receiveSeq++;
                await SendRawAsync(KnxIp.TunnelingAck(channel, seq, KnxIpStatus.NoError), ct).ConfigureAwait(false);
                Dispatch(KnxCemiFrame.Decode(rest.Span));
                break;
            }
        }
    }

    private void Dispatch(KnxCemiFrame cemi)
    {
        if (cemi.MessageCode == KnxMessageCode.DataConfirmation)
        {
            _pendingConfirm?.TrySetResult(cemi);
            return;
        }

        if (!cemi.IsGroup) return;
        if (cemi.Apci == KnxApci.GroupValueResponse && _reads.TryRemove(cemi.Destination, out var waiters))
        {
            foreach (var w in waiters) w.TrySetResult(cemi.Value);
        }

        GroupValueReceived?.Invoke(new KnxGroupEvent(cemi.Source, cemi.Group, cemi.Apci, cemi.Value, DateTimeOffset.UtcNow));
    }

    private async Task HeartbeatAsync(CancellationToken ct)
    {
        var failures = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_options.HeartbeatInterval, ct).ConfigureAwait(false);
                try
                {
                    await _sendGate.WaitAsync(ct).ConfigureAwait(false);
                    byte[] response;
                    try
                    {
                        response = await RequestAsync(KnxIp.ConnectionStateRequest(_channel, Hpai), KnxServiceType.ConnectionStateResponse, ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        _sendGate.Release();
                    }

                    if (response[1] != KnxIpStatus.NoError)
                    {
                        Lost($"heartbeat answered {KnxIpStatus.Name(response[1])}");
                        return;
                    }

                    failures = 0;
                }
                catch (IoTComTimeoutException)
                {
                    if (++failures >= 3)
                    {
                        Lost("three heartbeats unanswered");
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Lost(string reason)
    {
        if (State != EndpointState.Connected) return;
        Logger.LogWarning("KNX tunnel to {Gateway} lost: {Reason}", _gateway, reason);
        SetState(EndpointState.Disconnected);
        _ = Task.Run(TeardownAsync);
        ConnectionLost?.Invoke(reason);
    }

    /// <summary>Sends a cEMI L_Data.req and waits for its ack and confirmation.</summary>
    private async Task SendTelegramAsync(KnxCemiFrame frame, CancellationToken ct)
    {
        if (State != EndpointState.Connected) throw new InvalidOperationException("Not connected. Call ConnectAsync first.");
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cemi = frame.Encode();
            var confirm = new TaskCompletionSource<KnxCemiFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingConfirm = confirm;
            var acked = false;
            for (var attempt = 0; attempt < 2 && !acked; attempt++)
            {
                var ack = new TaskCompletionSource<byte>(TaskCreationOptions.RunContinuationsAsynchronously);
                _acks[_sendSeq] = ack;
                await SendRawAsync(KnxIp.TunnelingRequest(_channel, _sendSeq, cemi), ct).ConfigureAwait(false);
                try
                {
                    var status = await ack.Task.WaitAsync(_options.AckTimeout, ct).ConfigureAwait(false);
                    if (status != KnxIpStatus.NoError) throw new DeviceException($"The KNX interface rejected the telegram: {KnxIpStatus.Name(status)}.", status);
                    acked = true;
                }
                catch (TimeoutException)
                {
                    _acks.TryRemove(_sendSeq, out _);
                }
            }

            _sendSeq++;
            if (!acked)
            {
                Lost("no TUNNELING_ACK after a retry");
                throw new IoTComTimeoutException("The KNX interface did not acknowledge the telegram; the connection was closed.");
            }

            try
            {
                var con = await confirm.Task.WaitAsync(_options.ConfirmTimeout, ct).ConfigureAwait(false);
                if (con.ConfirmError) throw new DeviceException($"The KNX interface could not send the telegram to {frame.DestinationText} on the bus (negative L_Data.con).");
            }
            catch (TimeoutException)
            {
                throw new IoTComTimeoutException($"No L_Data.con for the telegram to {frame.DestinationText}.");
            }
        }
        finally
        {
            _pendingConfirm = null;
            _sendGate.Release();
        }
    }

    /// <inheritdoc />
    public Task WriteAsync(KnxGroupAddress address, KnxGroupValue value, CancellationToken ct = default)
    {
        if (_options.ReadOnly) throw new ReadOnlyModeException($"Writing {address} changes devices on the KNX line; this client is read-only until AllowWrites().");
        return SendTelegramAsync(KnxCemiFrame.GroupTelegram(KnxMessageCode.DataRequest, default, address, KnxApci.GroupValueWrite, value), ct);
    }

    /// <inheritdoc />
    public async Task<KnxGroupValue> ReadAsync(KnxGroupAddress address, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<KnxGroupValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        var list = _reads.GetOrAdd(address.Raw, _ => []);
        lock (list) list.Add(tcs);
        try
        {
            await SendTelegramAsync(KnxCemiFrame.GroupTelegram(KnxMessageCode.DataRequest, default, address, KnxApci.GroupValueRead, KnxGroupValue.Empty), ct).ConfigureAwait(false);
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
        if (State == EndpointState.Connected)
        {
            SetState(EndpointState.Stopping);
            try
            {
                await RequestAsync(KnxIp.DisconnectRequest(_channel, Hpai), KnxServiceType.DisconnectResponse, ct).ConfigureAwait(false);
            }
            catch (IoTComException)
            {
            }
        }

        await TeardownAsync().ConfigureAwait(false);
        SetState(EndpointState.Disconnected);
    }

    private async Task TeardownAsync()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts is null) return;
        await cts.CancelAsync().ConfigureAwait(false);
        if (_transport is not null) await _transport.DisposeAsync().ConfigureAwait(false);
        if (_receive is not null && !_receive.IsCompleted && Task.CurrentId != _receive.Id) await _receive.ConfigureAwait(false);
        if (_heartbeat is not null && Task.CurrentId != _heartbeat.Id) await _heartbeat.ConfigureAwait(false);
        cts.Dispose();
        _transport = null;
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _sendGate.Dispose();
    }
}
