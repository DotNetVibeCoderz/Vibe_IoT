using System.Diagnostics;
using System.Net;
using IoTCom.Net.Transports;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Protocols.LoRaWan;

/// <summary>Packet forwarder options.</summary>
public sealed class SemtechPacketForwarderOptions : IDatagramBuilder<SemtechPacketForwarderOptions>
{
    /// <summary>Gateway EUI.</summary>
    public Eui64 GatewayEui { get; set; } = new(0xAA555A0000000001);

    /// <summary>Network server address.</summary>
    public EndPoint Server { get; set; } = new IPEndPoint(IPAddress.Loopback, 1700);

    /// <summary>Binds the local transport (default: UDP on an ephemeral port).</summary>
    public DatagramTransportFactory? TransportFactory { get; set; }

    /// <summary>PULL_DATA keep-alive interval (opens the downlink path through NATs).</summary>
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Status report interval (0 disables).</summary>
    public TimeSpan StatusInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Gateway position for status reports.</summary>
    public (double Latitude, double Longitude, int Altitude)? Position { get; set; }

    /// <summary>Friendly name.</summary>
    public string? Name { get; set; }

    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }

    /// <inheritdoc />
    public SemtechPacketForwarderOptions UseDatagramTransport(DatagramTransportFactory factory)
    {
        TransportFactory = factory;
        return this;
    }
}

/// <summary>
/// The gateway side of the Semtech UDP protocol: forwards received radio packets (PUSH_DATA), keeps the downlink
/// path open (PULL_DATA), reports status, and hands downlinks (PULL_RESP) to the radio through
/// <see cref="TransmitRequested"/>, confirming each with TX_ACK. Bridge it to a concentrator, or use it as the virtual
/// gateway of <see cref="LoRaWanSimulator"/>.
/// </summary>
public sealed class SemtechPacketForwarder : EndpointBase, IClientEndpoint
{
    private readonly SemtechPacketForwarderOptions _options;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private IDatagramTransport? _transport;
    private CancellationTokenSource? _cts;
    private Task? _loops;
    private ushort _token = (ushort)Random.Shared.Next(ushort.MaxValue);
    private int _rxReceived, _rxForwarded, _pushSent, _pushAcked, _downlinks, _txEmitted;

    private SemtechPacketForwarder(SemtechPacketForwarderOptions options) : base("semtech-udp", options.Logger)
    {
        _options = options;
        Name = options.Name ?? options.GatewayEui.ToString();
    }

    /// <summary>Creates a forwarder (call <see cref="ConnectAsync"/>).</summary>
    public static SemtechPacketForwarder Create(Action<SemtechPacketForwarderOptions>? configure = null)
    {
        var o = new SemtechPacketForwarderOptions();
        configure?.Invoke(o);
        return new SemtechPacketForwarder(o);
    }

    /// <summary>
    /// Raised for each downlink the server asks for. Return normally to report success (TX_ACK NONE); throw
    /// <see cref="InvalidOperationException"/> with a Semtech error code (TOO_LATE, TX_FREQ, ...) to report failure.
    /// </summary>
    public event Func<SemtechTxPacket, ValueTask>? TransmitRequested;

    /// <summary>Gateway EUI.</summary>
    public Eui64 GatewayEui => _options.GatewayEui;

    /// <summary>Concentrator time in microseconds (wraps at 2^32), the reference for <c>tmst</c>.</summary>
    public uint Timestamp => unchecked((uint)(_clock.Elapsed.Ticks / 10));

    /// <summary>The server acknowledged the last PULL_DATA.</summary>
    public bool DownlinkPathOpen { get; private set; }

    /// <summary>Percentage of PUSH_DATA acknowledged.</summary>
    public double AckRatio => _pushSent == 0 ? 100 : 100.0 * _pushAcked / _pushSent;

    /// <inheritdoc />
    public async ValueTask ConnectAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_transport is not null) return;
        SetState(EndpointState.Connecting);
        _transport = (_options.TransportFactory ?? (() => new UdpDatagramTransport(new IPEndPoint(IPAddress.Any, 0))))();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loops = Task.WhenAll(Task.Run(() => ReceiveLoopAsync(_transport, token), CancellationToken.None), Task.Run(() => KeepAliveLoopAsync(token), CancellationToken.None));
        await SendPullDataAsync(ct).ConfigureAwait(false);
        SetState(EndpointState.Connected);
    }

    /// <inheritdoc />
    public async ValueTask DisconnectAsync(CancellationToken ct = default)
    {
        if (_transport is null) return;
        SetState(EndpointState.Stopping);
        await _cts!.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loops!.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        await _transport.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
        (_transport, _cts, _loops) = (null, null, null);
        DownlinkPathOpen = false;
        SetState(EndpointState.Disconnected);
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore() => await DisconnectAsync().ConfigureAwait(false);

    /// <summary>Forwards a packet the radio received (PUSH_DATA). <c>Tmst</c> defaults to <see cref="Timestamp"/>.</summary>
    public async ValueTask ForwardAsync(SemtechRxPacket rx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rx);
        var transport = _transport ?? throw new InvalidOperationException("Connect the forwarder first.");
        Interlocked.Increment(ref _rxReceived);
        if (rx.CrcStatus == 1) Interlocked.Increment(ref _rxForwarded);
        if (rx.Tmst == 0) rx = rx with { Tmst = Timestamp };
        if (rx.Time is null) rx = rx with { Time = DateTimeOffset.UtcNow };
        var packet = new SemtechPacket { Token = NextToken(), Type = SemtechPacketType.PushData, GatewayEui = _options.GatewayEui, RxPackets = [rx] };
        Tap(FrameDirection.Outbound, rx.Data, () => $"rxpk {rx.DataRate} {rx.Frequency:0.0##} MHz RSSI {rx.Rssi:0} SNR {rx.Snr:0.0}: {Describe(rx.Data)}");
        Interlocked.Increment(ref _pushSent);
        await transport.SendAsync(packet.Encode(), _options.Server, ct).ConfigureAwait(false);
    }

    /// <summary>Sends a status report now.</summary>
    public async ValueTask SendStatusAsync(CancellationToken ct = default)
    {
        var transport = _transport ?? throw new InvalidOperationException("Connect the forwarder first.");
        var status = new SemtechGatewayStatus
        {
            Time = DateTimeOffset.UtcNow,
            Latitude = _options.Position?.Latitude,
            Longitude = _options.Position?.Longitude,
            Altitude = _options.Position?.Altitude,
            RxReceived = _rxReceived,
            RxOk = _rxForwarded,
            RxForwarded = _rxForwarded,
            AckRatio = AckRatio,
            DownlinkReceived = _downlinks,
            TxEmitted = _txEmitted,
        };
        Interlocked.Increment(ref _pushSent);
        var packet = new SemtechPacket { Token = NextToken(), Type = SemtechPacketType.PushData, GatewayEui = _options.GatewayEui, Status = status };
        await transport.SendAsync(packet.Encode(), _options.Server, ct).ConfigureAwait(false);
    }

    private ushort NextToken() => unchecked(++_token);

    private async ValueTask SendPullDataAsync(CancellationToken ct)
    {
        var packet = new SemtechPacket { Token = NextToken(), Type = SemtechPacketType.PullData, GatewayEui = _options.GatewayEui };
        await _transport!.SendAsync(packet.Encode(), _options.Server, ct).ConfigureAwait(false);
    }

    private async Task KeepAliveLoopAsync(CancellationToken ct)
    {
        var nextStatus = _options.StatusInterval > TimeSpan.Zero ? _clock.Elapsed + TimeSpan.FromSeconds(1) : TimeSpan.MaxValue;
        while (!ct.IsCancellationRequested)
        {
            var wait = _options.KeepAliveInterval;
            if (nextStatus - _clock.Elapsed < wait) wait = nextStatus - _clock.Elapsed;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
            try
            {
                if (_clock.Elapsed >= nextStatus)
                {
                    await SendStatusAsync(ct).ConfigureAwait(false);
                    nextStatus = _clock.Elapsed + _options.StatusInterval;
                }
                else
                {
                    await SendPullDataAsync(ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ObjectDisposedException)
            {
                if (ct.IsCancellationRequested) return;
            }
        }
    }

    private async Task ReceiveLoopAsync(IDatagramTransport transport, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Datagram datagram;
            try
            {
                datagram = await transport.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ObjectDisposedException)
            {
                if (ct.IsCancellationRequested) return;
                continue;
            }

            if (!SemtechPacket.TryDecode(datagram.Data.Span, out var packet, out _)) continue;
            switch (packet!.Type)
            {
                case SemtechPacketType.PushAck:
                    Interlocked.Increment(ref _pushAcked);
                    break;
                case SemtechPacketType.PullAck:
                    DownlinkPathOpen = true;
                    break;
                case SemtechPacketType.PullResp when packet.TxPacket is { } tx:
                    Interlocked.Increment(ref _downlinks);
                    Tap(FrameDirection.Inbound, tx.Data, () => $"txpk {tx.DataRate} {tx.Frequency:0.0##} MHz {(tx.Immediate ? "now" : $"tmst {tx.Tmst}")}: {Describe(tx.Data)}");
                    _ = TransmitAsync(transport, packet.Token, tx, ct);
                    break;
            }
        }
    }

    private async Task TransmitAsync(IDatagramTransport transport, ushort token, SemtechTxPacket tx, CancellationToken ct)
    {
        var error = "NONE";
        try
        {
            if (TransmitRequested is { } handler) await handler(tx).ConfigureAwait(false);
            Interlocked.Increment(ref _txEmitted);
        }
        catch (InvalidOperationException ex)
        {
            error = ex.Message;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            var ack = new SemtechPacket { Token = token, Type = SemtechPacketType.TxAck, GatewayEui = _options.GatewayEui, TxError = error };
            await transport.SendAsync(ack.Encode(), _options.Server, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or System.Net.Sockets.SocketException)
        {
        }
    }

    private static string Describe(byte[] phy) => LoRaWanPacket.TryDecode(phy, out var p, out var e) ? p!.ToString() : e!;
}
