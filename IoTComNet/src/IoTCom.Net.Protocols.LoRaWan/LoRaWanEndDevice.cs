namespace IoTCom.Net.Protocols.LoRaWan;

/// <summary>What a device got from a downlink.</summary>
/// <param name="JoinAccepted">The downlink was the Join-Accept that activated the device.</param>
/// <param name="FPort">Application port (null when there was no FRMPayload).</param>
/// <param name="Payload">Decrypted application payload.</param>
/// <param name="Ack">The network acknowledged the last confirmed uplink.</param>
/// <param name="FPending">The network has more data pending.</param>
/// <param name="Confirmed">The downlink is confirmed (the next uplink carries ACK).</param>
/// <param name="MacCommands">MAC commands received.</param>
public sealed record LoRaWanDownlink(bool JoinAccepted, byte? FPort, byte[] Payload, bool Ack, bool FPending, bool Confirmed, IReadOnlyList<LoRaWanMacCommand> MacCommands);

/// <summary>
/// The MAC layer of a Class A end device, without I/O: builds Join-Requests and uplinks, processes downlinks
/// (Join-Accept, ACK, MAC commands, application data) and keeps the session. Drives the simulator and is usable on its
/// own to test a network server or to emulate a device from a host.
/// </summary>
/// <example>
/// <code>
/// var device = new LoRaWanEndDevice(Eui64.Parse("70B3D57ED0000001"), Eui64.Parse("0000000000000000"), appKey);
/// var join = device.CreateJoinRequest();          // transmit, then feed what arrives in RX1/RX2:
/// device.HandleDownlink(joinAcceptPhy);
/// var uplink = device.CreateUplink(1, [0x01, 0x67, 0x00, 0xE1]);
/// </code>
/// </example>
public sealed class LoRaWanEndDevice
{
    private readonly byte[]? _appKey;
    private readonly List<LoRaWanMacCommand> _pendingMac = [];
    private bool _ackNext;
    private bool _fCntDownSeen;

    /// <summary>Creates an over-the-air-activated (OTAA) device.</summary>
    public LoRaWanEndDevice(Eui64 devEui, Eui64 joinEui, byte[] appKey)
    {
        ArgumentNullException.ThrowIfNull(appKey);
        if (appKey.Length != 16) throw new ArgumentException("The AppKey has 16 bytes.", nameof(appKey));
        DevEui = devEui;
        JoinEui = joinEui;
        _appKey = appKey;
    }

    private LoRaWanEndDevice(Eui64 devEui, DevAddr devAddr, LoRaWanSessionKeys keys)
    {
        DevEui = devEui;
        DevAddr = devAddr;
        SessionKeys = keys;
    }

    /// <summary>Creates an activation-by-personalisation (ABP) device: already in session.</summary>
    public static LoRaWanEndDevice Abp(Eui64 devEui, DevAddr devAddr, LoRaWanSessionKeys keys) => new(devEui, devAddr, keys);

    /// <summary>Device EUI.</summary>
    public Eui64 DevEui { get; }

    /// <summary>Join EUI (OTAA).</summary>
    public Eui64 JoinEui { get; }

    /// <summary>In session (joined or ABP).</summary>
    public bool IsActivated => SessionKeys is not null;

    /// <summary>Device address (after activation).</summary>
    public DevAddr DevAddr { get; private set; }

    /// <summary>Session keys (after activation).</summary>
    public LoRaWanSessionKeys? SessionKeys { get; private set; }

    /// <summary>Next uplink frame counter.</summary>
    public uint FCntUp { get; private set; }

    /// <summary>Last downlink frame counter received.</summary>
    public uint FCntDown { get; private set; }

    /// <summary>DevNonce of the last Join-Request (a counter, as in LoRaWAN 1.0.4).</summary>
    public ushort DevNonce { get; private set; }

    /// <summary>Uplink data rate index (changed by LinkADRReq).</summary>
    public int DataRate { get; set; } = 5;

    /// <summary>TX power index (changed by LinkADRReq).</summary>
    public int TxPowerIndex { get; private set; }

    /// <summary>RX1 delay announced by the Join-Accept or RXTimingSetupReq.</summary>
    public TimeSpan Rx1Delay { get; private set; } = TimeSpan.FromSeconds(1);

    /// <summary>Battery level reported in DevStatusAns (0 external power, 1..254, 255 unknown).</summary>
    public byte Battery { get; set; } = 255;

    /// <summary>SNR of the last downlink, reported as the margin in DevStatusAns.</summary>
    public int LastDownlinkSnr { get; set; } = 10;

    /// <summary>Result of the last LinkCheckReq: margin (dB) and gateway count.</summary>
    public (int MarginDb, int Gateways)? LastLinkCheck { get; private set; }

    /// <summary>Network time from the last DeviceTimeAns.</summary>
    public DateTimeOffset? NetworkTime { get; private set; }

    /// <summary>A Join-Request was sent and no Join-Accept arrived yet.</summary>
    public bool JoinPending { get; private set; }

    /// <summary>Asks the network for link quality in the next uplink.</summary>
    public void RequestLinkCheck() => Queue(LoRaWanMacCommand.LinkCheckReq());

    /// <summary>Asks the network for the time in the next uplink.</summary>
    public void RequestDeviceTime() => Queue(LoRaWanMacCommand.DeviceTimeReq());

    /// <summary>Builds the next Join-Request (increments DevNonce).</summary>
    public byte[] CreateJoinRequest()
    {
        if (_appKey is null) throw new InvalidOperationException("An ABP device does not join.");
        DevNonce++;
        JoinPending = true;
        return LoRaWanPacket.EncodeJoinRequest(JoinEui, DevEui, DevNonce, _appKey);
    }

    /// <summary>
    /// Builds the next uplink. Pending MAC answers and requests go into FOpts; the ACK bit is set when the last downlink
    /// was confirmed. Use FPort 1..223 for application data.
    /// </summary>
    public byte[] CreateUplink(byte fport, ReadOnlySpan<byte> payload, bool confirmed = false, bool adr = true)
    {
        var keys = SessionKeys ?? throw new InvalidOperationException("The device has not joined yet.");
        if (fport == 0) throw new ArgumentOutOfRangeException(nameof(fport), "FPort 0 is reserved for MAC commands.");
        var fopts = TakeFOpts();
        var fctrl = LoRaWanFCtrl.Create(true, adr: adr, ack: _ackNext);
        var phy = LoRaWanPacket.EncodeData(confirmed ? LoRaWanMType.ConfirmedDataUp : LoRaWanMType.UnconfirmedDataUp,
            DevAddr, fctrl, FCntUp, fopts, fport, payload, keys);
        _ackNext = false;
        FCntUp++;
        return phy;
    }

    /// <summary>Builds an uplink that carries only MAC commands (FPort 0), e.g. answers the network is waiting for.</summary>
    public byte[] CreateMacUplink()
    {
        var keys = SessionKeys ?? throw new InvalidOperationException("The device has not joined yet.");
        var commands = LoRaWanMacCommands.Encode(_pendingMac);
        _pendingMac.Clear();
        var phy = LoRaWanPacket.EncodeData(LoRaWanMType.UnconfirmedDataUp, DevAddr, LoRaWanFCtrl.Create(true, ack: _ackNext), FCntUp, [], 0, commands, keys);
        _ackNext = false;
        FCntUp++;
        return phy;
    }

    /// <summary>MAC commands waiting for the next uplink.</summary>
    public int PendingMacCommands => _pendingMac.Count;

    /// <summary>
    /// Processes a received PHYPayload. Returns null when the frame is not for this device (other DevAddr, MIC
    /// mismatch, replayed counter, or an unexpected Join-Accept).
    /// </summary>
    public LoRaWanDownlink? HandleDownlink(ReadOnlySpan<byte> phy)
    {
        if (!LoRaWanPacket.TryDecode(phy, out var packet, out _) || packet!.IsUplink) return null;
        if (packet.MType == LoRaWanMType.JoinAccept)
        {
            if (!JoinPending || _appKey is null || !LoRaWanJoinAccept.TryDecrypt(phy, _appKey, out var accept, out _)) return null;
            SessionKeys = accept!.DeriveSessionKeys(_appKey, DevNonce);
            DevAddr = accept.DevAddr;
            Rx1Delay = accept.Rx1Delay;
            FCntUp = 0;
            FCntDown = 0;
            _fCntDownSeen = false;
            _ackNext = false;
            _pendingMac.Clear();
            JoinPending = false;
            return new LoRaWanDownlink(true, null, [], false, false, false, []);
        }

        if (!packet.IsData || SessionKeys is null || packet.DevAddr != DevAddr) return null;
        var full = LoRaWanPacket.ReconstructFCnt(FCntDown, packet.FCnt, first: !_fCntDownSeen);
        if (!packet.VerifyMic(SessionKeys.NwkSKey, full)) return null;
        if (_fCntDownSeen && full <= FCntDown) return null;
        FCntDown = full;
        _fCntDownSeen = true;

        var payload = packet.DecryptPayload(SessionKeys, full);
        var macBytes = packet.FPort == 0 ? payload : packet.FOpts.ToArray();
        var commands = LoRaWanMacCommands.Parse(macBytes, uplink: false);
        foreach (var c in commands) Apply(c);
        if (packet.IsConfirmed) _ackNext = true;
        return new LoRaWanDownlink(false, packet.FPort, packet.FPort is > 0 ? payload : [], packet.FCtrl.Ack, packet.FCtrl.FPending, packet.IsConfirmed, commands);
    }

    private void Apply(LoRaWanMacCommand c)
    {
        var p = c.Payload;
        switch (c.Cid)
        {
            case LoRaWanCid.LinkCheck when p.Length == 2:
                LastLinkCheck = (p[0], p[1]);
                break;
            case LoRaWanCid.DeviceTime when p.Length == 5:
                NetworkTime = LoRaWanMacCommands.GpsEpoch + TimeSpan.FromSeconds(BitConverter.ToUInt32(p) - LoRaWanMacCommands.GpsLeapSeconds + (p[4] / 256.0));
                break;
            case LoRaWanCid.DevStatus:
                Queue(LoRaWanMacCommand.DevStatusAns(Battery, LastDownlinkSnr));
                break;
            case LoRaWanCid.LinkAdr when p.Length == 4:
                if ((p[0] >> 4) != 0x0F) DataRate = p[0] >> 4;
                if ((p[0] & 0x0F) != 0x0F) TxPowerIndex = p[0] & 0x0F;
                Queue(new LoRaWanMacCommand(LoRaWanCid.LinkAdr, true, [0x07]));
                break;
            case LoRaWanCid.RxTimingSetup when p.Length == 1:
                Rx1Delay = TimeSpan.FromSeconds(Math.Max(1, p[0] & 0x0F));
                Queue(new LoRaWanMacCommand(LoRaWanCid.RxTimingSetup, true, []));
                break;
            case LoRaWanCid.DutyCycle:
                Queue(new LoRaWanMacCommand(LoRaWanCid.DutyCycle, true, []));
                break;
            case LoRaWanCid.RxParamSetup:
                Queue(new LoRaWanMacCommand(LoRaWanCid.RxParamSetup, true, [0x07]));
                break;
            case LoRaWanCid.NewChannel:
                Queue(new LoRaWanMacCommand(LoRaWanCid.NewChannel, true, [0x03]));
                break;
            case LoRaWanCid.DlChannel:
                Queue(new LoRaWanMacCommand(LoRaWanCid.DlChannel, true, [0x03]));
                break;
            case LoRaWanCid.TxParamSetup:
                Queue(new LoRaWanMacCommand(LoRaWanCid.TxParamSetup, true, []));
                break;
        }
    }

    private void Queue(LoRaWanMacCommand command)
    {
        _pendingMac.RemoveAll(c => c.Cid == command.Cid);
        _pendingMac.Add(command);
    }

    private byte[] TakeFOpts()
    {
        var taken = new List<LoRaWanMacCommand>();
        var size = 0;
        foreach (var c in _pendingMac)
        {
            if (size + c.Size > 15) break;
            taken.Add(c);
            size += c.Size;
        }

        _pendingMac.RemoveRange(0, taken.Count);
        return LoRaWanMacCommands.Encode(taken);
    }
}
