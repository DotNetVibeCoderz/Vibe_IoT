using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using IoTCom.Net.Transports;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Protocols.LoRaWan;

/// <summary>A device the network server knows: OTAA (AppKey) or ABP (DevAddr and session keys).</summary>
public sealed record LoRaWanDeviceRegistration
{
    /// <summary>Device EUI.</summary>
    public required Eui64 DevEui { get; init; }

    /// <summary>Join EUI the device must present (null accepts any).</summary>
    public Eui64? JoinEui { get; init; }

    /// <summary>Root key for OTAA.</summary>
    public byte[]? AppKey { get; init; }

    /// <summary>ABP device address.</summary>
    public DevAddr? AbpDevAddr { get; init; }

    /// <summary>ABP session keys.</summary>
    public LoRaWanSessionKeys? AbpKeys { get; init; }

    /// <summary>Friendly name.</summary>
    public string? Name { get; init; }

    /// <summary>An OTAA registration.</summary>
    public static LoRaWanDeviceRegistration Otaa(Eui64 devEui, byte[] appKey, string? name = null, Eui64? joinEui = null) =>
        new() { DevEui = devEui, AppKey = appKey, Name = name, JoinEui = joinEui };

    /// <summary>An ABP registration.</summary>
    public static LoRaWanDeviceRegistration Abp(Eui64 devEui, DevAddr devAddr, LoRaWanSessionKeys keys, string? name = null) =>
        new() { DevEui = devEui, AbpDevAddr = devAddr, AbpKeys = keys, Name = name };
}

/// <summary>Reception metadata from one gateway.</summary>
/// <param name="GatewayEui">Gateway.</param>
/// <param name="Rssi">RSSI in dBm.</param>
/// <param name="Snr">SNR in dB.</param>
/// <param name="Tmst">Concentrator timestamp.</param>
public sealed record LoRaWanRxInfo(Eui64 GatewayEui, double Rssi, double Snr, uint Tmst);

/// <summary>A device as the network server sees it.</summary>
public sealed class LoRaWanDeviceSession
{
    internal LoRaWanDeviceSession(LoRaWanDeviceRegistration registration) => Registration = registration;

    internal readonly object Gate = new();
    internal readonly HashSet<ushort> UsedNonces = [];
    internal readonly Queue<(byte FPort, byte[] Payload, bool Confirmed)> Downlinks = new();
    internal readonly List<LoRaWanMacCommand> MacToSend = [];
    internal bool FCntUpSeen;
    internal LoRaWanSessionKeys? Keys;

    /// <summary>The registration.</summary>
    public LoRaWanDeviceRegistration Registration { get; }

    /// <summary>Device EUI.</summary>
    public Eui64 DevEui => Registration.DevEui;

    /// <summary>Name, or the DevEUI.</summary>
    public string Name => Registration.Name ?? Registration.DevEui.ToString();

    /// <summary>In session.</summary>
    public bool IsActivated => Keys is not null;

    /// <summary>Current device address.</summary>
    public DevAddr DevAddr { get; internal set; }

    /// <summary>Last uplink counter accepted.</summary>
    public uint FCntUp { get; internal set; }

    /// <summary>Next downlink counter.</summary>
    public uint FCntDown { get; internal set; }

    /// <summary>Uplinks accepted.</summary>
    public long UplinkCount { get; internal set; }

    /// <summary>Downlinks sent.</summary>
    public long DownlinkCount { get; internal set; }

    /// <summary>Joins accepted.</summary>
    public int JoinCount { get; internal set; }

    /// <summary>Last uplink time.</summary>
    public DateTimeOffset? LastSeen { get; internal set; }

    /// <summary>Best RSSI of the last uplink.</summary>
    public double? LastRssi { get; internal set; }

    /// <summary>Best SNR of the last uplink.</summary>
    public double? LastSnr { get; internal set; }

    /// <summary>Data rate of the last uplink.</summary>
    public string? LastDataRate { get; internal set; }

    /// <summary>Battery from the last DevStatusAns (0 external, 1..254, 255 unknown).</summary>
    public byte? Battery { get; internal set; }

    /// <summary>SNR margin from the last DevStatusAns.</summary>
    public int? DeviceMargin { get; internal set; }

    /// <summary>Application downlinks waiting for the device's next uplink.</summary>
    public int PendingDownlinks
    {
        get
        {
            lock (Gate) return Downlinks.Count;
        }
    }
}

/// <summary>A gateway connected to the server.</summary>
public sealed class LoRaWanGatewaySession
{
    internal LoRaWanGatewaySession(Eui64 eui) => Eui = eui;

    /// <summary>Gateway EUI.</summary>
    public Eui64 Eui { get; }

    /// <summary>Address of the last PULL_DATA (downlinks go there).</summary>
    public EndPoint? PullEndPoint { get; internal set; }

    /// <summary>Address of the last PUSH_DATA.</summary>
    public EndPoint? PushEndPoint { get; internal set; }

    /// <summary>Last datagram time.</summary>
    public DateTimeOffset LastSeen { get; internal set; }

    /// <summary>Last status report.</summary>
    public SemtechGatewayStatus? Status { get; internal set; }

    /// <summary>Radio packets forwarded to the server.</summary>
    public long RxPackets { get; internal set; }

    /// <summary>Downlinks sent through this gateway.</summary>
    public long TxPackets { get; internal set; }

    /// <summary>Last TX_ACK error other than NONE.</summary>
    public string? LastTxError { get; internal set; }
}

/// <summary>An application uplink, decrypted and deduplicated across gateways.</summary>
public sealed record LoRaWanUplink
{
    /// <summary>The device.</summary>
    public required LoRaWanDeviceSession Device { get; init; }

    /// <summary>Full frame counter.</summary>
    public uint FCnt { get; init; }

    /// <summary>Application port (null: MAC-only frame).</summary>
    public byte? FPort { get; init; }

    /// <summary>Decrypted application payload.</summary>
    public byte[] Payload { get; init; } = [];

    /// <summary>Confirmed uplink (the server answered with ACK).</summary>
    public bool Confirmed { get; init; }

    /// <summary>The device acknowledged the last confirmed downlink.</summary>
    public bool Ack { get; init; }

    /// <summary>MAC commands from the device.</summary>
    public IReadOnlyList<LoRaWanMacCommand> MacCommands { get; init; } = [];

    /// <summary>Data rate.</summary>
    public string DataRate { get; init; } = "";

    /// <summary>Frequency in MHz.</summary>
    public double Frequency { get; init; }

    /// <summary>Time on air.</summary>
    public TimeSpan Airtime { get; init; }

    /// <summary>Every gateway that heard the frame, best first.</summary>
    public IReadOnlyList<LoRaWanRxInfo> Gateways { get; init; } = [];

    /// <summary>The PHYPayload.</summary>
    public byte[] Phy { get; init; } = [];

    /// <summary>Server reception time.</summary>
    public DateTimeOffset Time { get; init; }
}

/// <summary>Light network server options.</summary>
public sealed class LoRaWanNetworkServerOptions : IDatagramBuilder<LoRaWanNetworkServerOptions>
{
    /// <summary>Binds the transport (default: UDP <see cref="Port"/> on all interfaces).</summary>
    public DatagramTransportFactory? TransportFactory { get; set; }

    /// <summary>UDP port of the Semtech packet-forwarder protocol.</summary>
    public int Port { get; set; } = 1700;

    /// <summary>Regional parameters.</summary>
    public LoRaRegion Region { get; set; } = LoRaRegion.EU868;

    /// <summary>Network identifier (24 bits). Type-0 NetIDs put their 6 low bits into the DevAddr prefix.</summary>
    public uint NetId { get; set; }

    /// <summary>RX1 delay in seconds sent in Join-Accepts (1..15).</summary>
    public byte RxDelay { get; set; } = 1;

    /// <summary>Delay of the Join-Accept after the Join-Request (JOIN_ACCEPT_DELAY1 = 5 s).</summary>
    public TimeSpan JoinAcceptDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long copies of the same uplink from several gateways are collected.</summary>
    public TimeSpan DeduplicationWindow { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Accept a frame counter lower than expected (devices that reset without rejoining). Default false.</summary>
    public bool RelaxFCnt { get; set; }

    /// <summary>Friendly name.</summary>
    public string? Name { get; set; }

    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }

    /// <inheritdoc />
    public LoRaWanNetworkServerOptions UseDatagramTransport(DatagramTransportFactory factory)
    {
        TransportFactory = factory;
        return this;
    }
}

/// <summary>
/// A light LoRaWAN 1.0.x network server for labs, gateways on a desk and simulations: Semtech UDP packet-forwarder
/// protocol towards gateways, OTAA joins and ABP, MIC and frame counter checks, deduplication across gateways, payload
/// decryption, Class A downlinks in RX1 (ACKs, queued data, MAC answers) and LinkCheck, DeviceTime and DevStatus.
/// </summary>
/// <example>
/// <code>
/// await using var server = LoRaWanNetworkServer.Create(o => o.Region = LoRaRegion.AS923Group2);
/// server.AddDevice(LoRaWanDeviceRegistration.Otaa(Eui64.Parse("70B3D57ED0000001"), LoRaWanKeys.Parse("...")));
/// server.UplinkReceived += (_, up) => Console.WriteLine($"{up.Device.Name}: {Convert.ToHexString(up.Payload)}");
/// await server.StartAsync();
/// </code>
/// </example>
public sealed class LoRaWanNetworkServer : EndpointBase, IServerEndpoint
{
    private readonly LoRaWanNetworkServerOptions _options;
    private readonly ConcurrentDictionary<Eui64, LoRaWanDeviceSession> _devices = new();
    private readonly ConcurrentDictionary<Eui64, LoRaWanGatewaySession> _gateways = new();
    private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private IDatagramTransport? _transport;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private uint _nextAddr;
    private uint _joinNonce;
    private ushort _token;

    private sealed class Pending(SemtechRxPacket first, LoRaWanPacket packet)
    {
        public readonly SemtechRxPacket First = first;
        public readonly LoRaWanPacket Packet = packet;
        public readonly List<(LoRaWanGatewaySession Gateway, SemtechRxPacket Rx)> Receptions = [];
    }

    private LoRaWanNetworkServer(LoRaWanNetworkServerOptions options) : base("lorawan", options.Logger)
    {
        _options = options;
        Name = options.Name;
        _nextAddr = (uint)Random.Shared.Next(0x1000, 0x8000);
        _joinNonce = (uint)Random.Shared.Next(1, 0x1000);
    }

    /// <summary>Creates a server (call <see cref="StartAsync"/>).</summary>
    public static LoRaWanNetworkServer Create(Action<LoRaWanNetworkServerOptions>? configure = null)
    {
        var o = new LoRaWanNetworkServerOptions();
        configure?.Invoke(o);
        return new LoRaWanNetworkServer(o);
    }

    /// <summary>Raised for every accepted uplink (after deduplication).</summary>
    public event EventHandler<LoRaWanUplink>? UplinkReceived;

    /// <summary>Raised when a device joins.</summary>
    public event EventHandler<LoRaWanDeviceSession>? DeviceJoined;

    /// <summary>Raised for frames the server drops, with the reason (unknown device, MIC, replay, ...).</summary>
    public event EventHandler<string>? FrameRejected;

    /// <summary>Raised when a gateway connects or reports status.</summary>
    public event EventHandler<LoRaWanGatewaySession>? GatewayUpdated;

    /// <summary>Raised when a downlink is handed to a gateway.</summary>
    public event EventHandler<(LoRaWanDeviceSession Device, SemtechTxPacket Tx)>? DownlinkSent;

    /// <summary>Local address after start.</summary>
    public EndPoint? LocalEndPoint => _transport?.LocalEndPoint;

    /// <summary>The options.</summary>
    public LoRaWanNetworkServerOptions Options => _options;

    /// <summary>Registered devices.</summary>
    public IReadOnlyCollection<LoRaWanDeviceSession> Devices => [.. _devices.Values];

    /// <summary>Gateways seen.</summary>
    public IReadOnlyCollection<LoRaWanGatewaySession> Gateways => [.. _gateways.Values];

    /// <summary>Registers (or replaces) a device.</summary>
    public LoRaWanDeviceSession AddDevice(LoRaWanDeviceRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (registration.AppKey is null && (registration.AbpDevAddr is null || registration.AbpKeys is null))
            throw new ArgumentException("A device needs an AppKey (OTAA) or a DevAddr and session keys (ABP).", nameof(registration));
        var session = new LoRaWanDeviceSession(registration);
        if (registration.AbpKeys is { } keys)
        {
            session.Keys = keys;
            session.DevAddr = registration.AbpDevAddr!.Value;
        }

        _devices[registration.DevEui] = session;
        return session;
    }

    /// <summary>Finds a device.</summary>
    public LoRaWanDeviceSession? GetDevice(Eui64 devEui) => _devices.GetValueOrDefault(devEui);

    /// <summary>Queues an application downlink, sent in RX1 after the device's next uplink.</summary>
    public void EnqueueDownlink(Eui64 devEui, byte fport, byte[] payload, bool confirmed = false)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (fport is 0 or > 223) throw new ArgumentOutOfRangeException(nameof(fport), "Application ports are 1..223.");
        var device = _devices.GetValueOrDefault(devEui) ?? throw new KeyNotFoundException($"Unknown device {devEui}.");
        lock (device.Gate) device.Downlinks.Enqueue((fport, payload, confirmed));
    }

    /// <summary>Queues a MAC command for the device's next downlink (e.g. <see cref="LoRaWanMacCommand.DevStatusReq"/>).</summary>
    public void EnqueueMacCommand(Eui64 devEui, LoRaWanMacCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var device = _devices.GetValueOrDefault(devEui) ?? throw new KeyNotFoundException($"Unknown device {devEui}.");
        lock (device.Gate) device.MacToSend.Add(command);
    }

    /// <inheritdoc />
    public ValueTask StartAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_transport is not null) return ValueTask.CompletedTask;
        _transport = (_options.TransportFactory ?? (() => new UdpDatagramTransport(new IPEndPoint(IPAddress.Any, _options.Port))))();
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => ReceiveLoopAsync(_transport, _cts.Token), CancellationToken.None);
        SetState(EndpointState.Listening);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask StopAsync(CancellationToken ct = default)
    {
        if (_transport is null) return;
        SetState(EndpointState.Stopping);
        await _cts!.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop!.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        await _transport.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
        (_transport, _cts, _loop) = (null, null, null);
        SetState(EndpointState.Disconnected);
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore() => await StopAsync().ConfigureAwait(false);

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

            try
            {
                await HandleDatagramAsync(transport, datagram, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogWarning(ex, "LoRaWAN: datagram from {Remote} failed", datagram.Remote);
            }
        }
    }

    private async Task HandleDatagramAsync(IDatagramTransport transport, Datagram datagram, CancellationToken ct)
    {
        if (!SemtechPacket.TryDecode(datagram.Data.Span, out var packet, out var error))
        {
            Reject($"Semtech UDP from {datagram.Remote}: {error}");
            return;
        }

        var gateway = packet!.GatewayEui is { } eui ? _gateways.GetOrAdd(eui, e => new LoRaWanGatewaySession(e)) : null;
        if (gateway is not null) gateway.LastSeen = DateTimeOffset.UtcNow;
        switch (packet.Type)
        {
            case SemtechPacketType.PullData:
                var isNew = gateway!.PullEndPoint is null;
                gateway.PullEndPoint = datagram.Remote;
                await transport.SendAsync(packet.Acknowledgement().Encode(), datagram.Remote, ct).ConfigureAwait(false);
                if (isNew) GatewayUpdated?.Invoke(this, gateway);
                break;

            case SemtechPacketType.PushData:
                gateway!.PushEndPoint = datagram.Remote;
                await transport.SendAsync(packet.Acknowledgement().Encode(), datagram.Remote, ct).ConfigureAwait(false);
                if (packet.Status is { } status)
                {
                    gateway.Status = status;
                    GatewayUpdated?.Invoke(this, gateway);
                }

                foreach (var rx in packet.RxPackets) Receive(gateway, rx);
                break;

            case SemtechPacketType.TxAck:
                if (packet.TxError is { } txError && txError != "NONE")
                {
                    gateway!.LastTxError = txError;
                    Reject($"Gateway {gateway.Eui} could not transmit: {txError}");
                }

                break;
        }
    }

    private void Receive(LoRaWanGatewaySession gateway, SemtechRxPacket rx)
    {
        gateway.RxPackets++;
        if (rx.CrcStatus != 1) return;
        if (!LoRaWanPacket.TryDecode(rx.Data, out var packet, out var error))
        {
            Reject($"Malformed LoRaWAN frame via {gateway.Eui}: {error}");
            return;
        }

        if (!packet!.IsUplink) return;
        var key = Convert.ToHexString(rx.Data);
        var pending = _pending.GetOrAdd(key, _ => new Pending(rx, packet));
        bool first;
        lock (pending)
        {
            first = pending.Receptions.Count == 0;
            pending.Receptions.Add((gateway, rx));
        }

        if (!first) return;
        Tap(FrameDirection.Inbound, rx.Data, () => $"{packet} via {gateway.Eui} {rx.DataRate} RSSI {rx.Rssi:0} SNR {rx.Snr:0.0}");
        _ = ProcessAfterWindowAsync(key, pending);
    }

    private async Task ProcessAfterWindowAsync(string key, Pending pending)
    {
        try
        {
            await Task.Delay(_options.DeduplicationWindow, _cts?.Token ?? CancellationToken.None).ConfigureAwait(false);
            _pending.TryRemove(key, out _);
            List<(LoRaWanGatewaySession Gateway, SemtechRxPacket Rx)> receptions;
            lock (pending) receptions = [.. pending.Receptions.OrderByDescending(r => r.Rx.Snr).ThenByDescending(r => r.Rx.Rssi)];
            if (pending.Packet.MType == LoRaWanMType.JoinRequest) await HandleJoinAsync(pending.Packet, receptions).ConfigureAwait(false);
            else if (pending.Packet.IsData) await HandleDataAsync(pending.Packet, receptions).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "LoRaWAN: processing an uplink failed");
        }
    }

    private async Task HandleJoinAsync(LoRaWanPacket packet, List<(LoRaWanGatewaySession Gateway, SemtechRxPacket Rx)> receptions)
    {
        if (!_devices.TryGetValue(packet.DevEui, out var device) || device.Registration.AppKey is not { } appKey)
        {
            Reject($"Join-Request from unknown device {packet.DevEui}");
            return;
        }

        if (device.Registration.JoinEui is { } joinEui && joinEui != packet.JoinEui)
        {
            Reject($"Join-Request from {device.Name}: JoinEUI {packet.JoinEui} does not match");
            return;
        }

        if (!packet.VerifyMic(appKey))
        {
            Reject($"Join-Request from {device.Name}: MIC mismatch (wrong AppKey)");
            return;
        }

        LoRaWanJoinAccept accept;
        lock (device.Gate)
        {
            if (!device.UsedNonces.Add(packet.DevNonce))
            {
                Reject($"Join-Request from {device.Name}: DevNonce {packet.DevNonce} already used (replay)");
                return;
            }

            var nwkId = _options.NetId & 0x7F;
            var addr = new DevAddr((nwkId << 25) | (Interlocked.Increment(ref _nextAddr) & 0x01FF_FFFF));
            accept = new LoRaWanJoinAccept(Interlocked.Increment(ref _joinNonce) & 0xFFFFFF, _options.NetId & 0xFFFFFF, addr,
                0, (byte)_options.Region.Rx2DataRate, _options.RxDelay);
            device.Keys = accept.DeriveSessionKeys(appKey, packet.DevNonce);
            device.DevAddr = addr;
            device.FCntUp = 0;
            device.FCntUpSeen = false;
            device.FCntDown = 0;
            device.JoinCount++;
            device.MacToSend.Clear();
        }

        var (gateway, rx) = receptions[0];
        device.LastSeen = DateTimeOffset.UtcNow;
        device.LastRssi = rx.Rssi;
        device.LastSnr = rx.Snr;
        device.LastDataRate = rx.DataRate;
        await SendDownlinkAsync(device, gateway, rx, accept.Encode(appKey), _options.JoinAcceptDelay).ConfigureAwait(false);
        DeviceJoined?.Invoke(this, device);
    }

    private async Task HandleDataAsync(LoRaWanPacket packet, List<(LoRaWanGatewaySession Gateway, SemtechRxPacket Rx)> receptions)
    {
        LoRaWanDeviceSession? device = null;
        uint full = 0;
        foreach (var candidate in _devices.Values)
        {
            if (candidate.Keys is null || candidate.DevAddr != packet.DevAddr) continue;
            // Try the expected counter first, then the same 16 bits in the current epoch (a replay or retransmission,
            // rejected below with a precise reason), then the raw 16 bits (devices that reset, with RelaxFCnt).
            var expected = LoRaWanPacket.ReconstructFCnt(candidate.FCntUp, packet.FCnt, first: !candidate.FCntUpSeen);
            uint[] tries = [expected, (candidate.FCntUp & 0xFFFF0000u) | packet.FCnt, packet.FCnt];
            foreach (var f in tries.Distinct())
            {
                if (!packet.VerifyMic(candidate.Keys.NwkSKey, f)) continue;
                device = candidate;
                full = f;
                break;
            }

            if (device is not null) break;
        }

        if (device is null)
        {
            Reject($"Uplink from DevAddr {packet.DevAddr}: no device with a matching MIC");
            return;
        }

        byte[] payload;
        IReadOnlyList<LoRaWanMacCommand> commands;
        var replies = new List<LoRaWanMacCommand>();
        var (gateway, rx) = receptions[0];
        lock (device.Gate)
        {
            if (device.FCntUpSeen && full <= device.FCntUp && !_options.RelaxFCnt)
            {
                Reject($"Uplink from {device.Name}: FCnt {full} not above {device.FCntUp} (replay or retransmission)");
                return;
            }

            device.FCntUp = full;
            device.FCntUpSeen = true;
            device.UplinkCount++;
            device.LastSeen = DateTimeOffset.UtcNow;
            device.LastRssi = rx.Rssi;
            device.LastSnr = rx.Snr;
            device.LastDataRate = rx.DataRate;
            var decrypted = packet.DecryptPayload(device.Keys!, full);
            payload = packet.FPort is > 0 ? decrypted : [];
            commands = LoRaWanMacCommands.Parse(packet.FPort == 0 ? decrypted : packet.FOpts.Span, uplink: true);
            foreach (var c in commands)
            {
                switch (c.Cid)
                {
                    case LoRaWanCid.LinkCheck:
                        var sf = LoRaDataRate.ParseDatr(rx.DataRate).Sf;
                        replies.Add(LoRaWanMacCommand.LinkCheckAns((int)Math.Round(rx.Snr - DemodulationFloor(sf)), receptions.Count));
                        break;
                    case LoRaWanCid.DeviceTime:
                        replies.Add(LoRaWanMacCommand.DeviceTimeAns(DateTimeOffset.UtcNow));
                        break;
                    case LoRaWanCid.DevStatus when c.Payload.Length == 2:
                        device.Battery = c.Payload[0];
                        device.DeviceMargin = (sbyte)(c.Payload[1] << 2) >> 2;
                        break;
                }
            }

            replies.AddRange(device.MacToSend);
            device.MacToSend.Clear();
        }

        var uplink = new LoRaWanUplink
        {
            Device = device,
            FCnt = full,
            FPort = packet.FPort,
            Payload = payload,
            Confirmed = packet.IsConfirmed,
            Ack = packet.FCtrl.Ack,
            MacCommands = commands,
            DataRate = rx.DataRate,
            Frequency = rx.Frequency,
            Airtime = LoRaAirtime.Compute(packet.Raw.Length, rx.DataRate),
            Gateways = [.. receptions.Select(r => new LoRaWanRxInfo(r.Gateway.Eui, r.Rx.Rssi, r.Rx.Snr, r.Rx.Tmst))],
            Phy = packet.Raw,
            Time = DateTimeOffset.UtcNow,
        };

        await RespondAsync(device, packet, gateway, rx, replies).ConfigureAwait(false);
        UplinkReceived?.Invoke(this, uplink);
    }

    private async Task RespondAsync(LoRaWanDeviceSession device, LoRaWanPacket uplink, LoRaWanGatewaySession gateway, SemtechRxPacket rx, List<LoRaWanMacCommand> mac)
    {
        (byte FPort, byte[] Payload, bool Confirmed)? data = null;
        bool more;
        lock (device.Gate)
        {
            if (device.Downlinks.Count > 0) data = device.Downlinks.Dequeue();
            more = device.Downlinks.Count > 0;
        }

        if (!uplink.IsConfirmed && mac.Count == 0 && data is null) return;
        var fopts = LoRaWanMacCommands.Encode(mac);
        byte? fport = data?.FPort;
        var body = data?.Payload ?? [];
        if (fopts.Length > 15)
        {
            // Too many MAC commands for FOpts: send them on FPort 0 and keep the application data queued.
            if (data is { } d) lock (device.Gate) PushFront(device, d);
            (fport, body, fopts, data, more) = ((byte)0, fopts, [], null, true);
        }

        var mtype = data?.Confirmed == true ? LoRaWanMType.ConfirmedDataDown : LoRaWanMType.UnconfirmedDataDown;
        uint fcnt;
        lock (device.Gate) fcnt = device.FCntDown++;
        var fctrl = LoRaWanFCtrl.Create(false, adr: uplink.FCtrl.Adr, ack: uplink.IsConfirmed, fPendingOrClassB: more);
        var phy = LoRaWanPacket.EncodeData(mtype, device.DevAddr, fctrl, fcnt, fopts, fport, body, device.Keys!);
        await SendDownlinkAsync(device, gateway, rx, phy, TimeSpan.FromSeconds(Math.Max((byte)1, _options.RxDelay))).ConfigureAwait(false);
    }

    private static void PushFront(LoRaWanDeviceSession device, (byte, byte[], bool) item)
    {
        var rest = device.Downlinks.ToArray();
        device.Downlinks.Clear();
        device.Downlinks.Enqueue(item);
        foreach (var r in rest) device.Downlinks.Enqueue(r);
    }

    private async Task SendDownlinkAsync(LoRaWanDeviceSession device, LoRaWanGatewaySession gateway, SemtechRxPacket rx, byte[] phy, TimeSpan delay)
    {
        var region = _options.Region;
        string datr;
        try
        {
            var dr = region.Rx1DataRate(region.DataRateIndex(rx.DataRate));
            datr = region.DataRates[dr].Datr;
        }
        catch (ArgumentException)
        {
            datr = rx.DataRate;
        }

        var tx = new SemtechTxPacket
        {
            Data = phy,
            Tmst = unchecked(rx.Tmst + (uint)(delay.Ticks / 10)),
            Frequency = region.Rx1Frequency(rx.Frequency),
            Power = region.DefaultTxPowerDbm,
            DataRate = datr,
            CodingRate = rx.CodingRate,
        };
        if (gateway.PullEndPoint is not { } target || _transport is not { } transport)
        {
            Reject($"No downlink path to gateway {gateway.Eui} (no PULL_DATA yet)");
            return;
        }

        var packet = new SemtechPacket { Token = unchecked(++_token), Type = SemtechPacketType.PullResp, TxPacket = tx };
        Tap(FrameDirection.Outbound, phy, () => $"{LoRaWanPacket.Decode(phy)} via {gateway.Eui} {datr} {tx.Frequency.ToString("0.0##", CultureInfo.InvariantCulture)} MHz");
        await transport.SendAsync(packet.Encode(), target).ConfigureAwait(false);
        gateway.TxPackets++;
        lock (device.Gate) device.DownlinkCount++;
        DownlinkSent?.Invoke(this, (device, tx));
    }

    private void Reject(string reason)
    {
        Logger.LogInformation("LoRaWAN: {Reason}", reason);
        FrameRejected?.Invoke(this, reason);
    }

    /// <summary>Minimum SNR the LoRa demodulator needs at a spreading factor (dB).</summary>
    public static double DemodulationFloor(int spreadingFactor) => spreadingFactor switch
    {
        <= 7 => -7.5,
        8 => -10,
        9 => -12.5,
        10 => -15,
        11 => -17.5,
        _ => -20,
    };
}
