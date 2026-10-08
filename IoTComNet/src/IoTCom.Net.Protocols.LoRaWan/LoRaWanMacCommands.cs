using System.Buffers.Binary;
using System.Globalization;

namespace IoTCom.Net.Protocols.LoRaWan;

/// <summary>MAC command identifiers (LoRaWAN 1.0.4, Class A).</summary>
public static class LoRaWanCid
{
    /// <summary>LinkCheckReq (up) / LinkCheckAns (down).</summary>
    public const byte LinkCheck = 0x02;
    /// <summary>LinkADRReq (down) / LinkADRAns (up).</summary>
    public const byte LinkAdr = 0x03;
    /// <summary>DutyCycleReq / DutyCycleAns.</summary>
    public const byte DutyCycle = 0x04;
    /// <summary>RXParamSetupReq / RXParamSetupAns.</summary>
    public const byte RxParamSetup = 0x05;
    /// <summary>DevStatusReq (down) / DevStatusAns (up).</summary>
    public const byte DevStatus = 0x06;
    /// <summary>NewChannelReq / NewChannelAns.</summary>
    public const byte NewChannel = 0x07;
    /// <summary>RXTimingSetupReq / RXTimingSetupAns.</summary>
    public const byte RxTimingSetup = 0x08;
    /// <summary>TxParamSetupReq / TxParamSetupAns.</summary>
    public const byte TxParamSetup = 0x09;
    /// <summary>DlChannelReq / DlChannelAns.</summary>
    public const byte DlChannel = 0x0A;
    /// <summary>DeviceTimeReq (up) / DeviceTimeAns (down).</summary>
    public const byte DeviceTime = 0x0D;
}

/// <summary>One MAC command.</summary>
/// <param name="Cid">Command identifier.</param>
/// <param name="Uplink">Sent by the device.</param>
/// <param name="Payload">Command payload (without the CID).</param>
public sealed record LoRaWanMacCommand(byte Cid, bool Uplink, byte[] Payload)
{
    /// <summary>Name, e.g. <c>LinkCheckReq</c>.</summary>
    public string Name => LoRaWanMacCommands.Name(Cid, Uplink);

    /// <summary>Encoded size (CID + payload).</summary>
    public int Size => 1 + Payload.Length;

    /// <summary>LinkCheckReq (device asks for link margin and gateway count).</summary>
    public static LoRaWanMacCommand LinkCheckReq() => new(LoRaWanCid.LinkCheck, true, []);

    /// <summary>LinkCheckAns: demodulation margin in dB above the demodulation floor, and gateway count.</summary>
    public static LoRaWanMacCommand LinkCheckAns(int marginDb, int gatewayCount) =>
        new(LoRaWanCid.LinkCheck, false, [(byte)Math.Clamp(marginDb, 0, 254), (byte)Math.Clamp(gatewayCount, 0, 255)]);

    /// <summary>DevStatusReq (network asks for battery and margin).</summary>
    public static LoRaWanMacCommand DevStatusReq() => new(LoRaWanCid.DevStatus, false, []);

    /// <summary>DevStatusAns: battery (0 external power, 1..254 level, 255 unknown) and SNR margin (−32..31 dB).</summary>
    public static LoRaWanMacCommand DevStatusAns(byte battery, int marginDb) =>
        new(LoRaWanCid.DevStatus, true, [battery, (byte)(Math.Clamp(marginDb, -32, 31) & 0x3F)]);

    /// <summary>DeviceTimeReq.</summary>
    public static LoRaWanMacCommand DeviceTimeReq() => new(LoRaWanCid.DeviceTime, true, []);

    /// <summary>DeviceTimeAns: GPS epoch seconds and 1/256 s fractions.</summary>
    public static LoRaWanMacCommand DeviceTimeAns(DateTimeOffset time)
    {
        var gps = time - LoRaWanMacCommands.GpsEpoch + TimeSpan.FromSeconds(LoRaWanMacCommands.GpsLeapSeconds);
        var payload = new byte[5];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)Math.Floor(gps.TotalSeconds));
        payload[4] = (byte)((gps.TotalSeconds - Math.Floor(gps.TotalSeconds)) * 256);
        return new LoRaWanMacCommand(LoRaWanCid.DeviceTime, false, payload);
    }

    /// <summary>LinkADRReq: data rate, TX power index, channel mask and redundancy (ChMaskCntl, NbTrans).</summary>
    public static LoRaWanMacCommand LinkAdrReq(int dataRate, int txPower, ushort channelMask, int chMaskCntl = 0, int nbTrans = 1)
    {
        var payload = new byte[4];
        payload[0] = (byte)(((dataRate & 0x0F) << 4) | (txPower & 0x0F));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(1), channelMask);
        payload[3] = (byte)(((chMaskCntl & 0x07) << 4) | (nbTrans & 0x0F));
        return new LoRaWanMacCommand(LoRaWanCid.LinkAdr, false, payload);
    }

    /// <summary>Human-readable payload, e.g. <c>margin 12 dB, 2 gateways</c>.</summary>
    public string Describe()
    {
        var p = Payload;
        var ic = CultureInfo.InvariantCulture;
        return (Cid, Uplink, p.Length) switch
        {
            (LoRaWanCid.LinkCheck, false, 2) => string.Format(ic, "margin {0} dB, {1} gateway(s)", p[0], p[1]),
            (LoRaWanCid.DevStatus, true, 2) => string.Format(ic, "battery {0}, margin {1} dB", p[0] switch { 0 => "external", 255 => "unknown", var b => $"{b * 100 / 254}%" }, (sbyte)(p[1] << 2) >> 2),
            (LoRaWanCid.LinkAdr, false, 4) => string.Format(ic, "DR{0} power {1} mask {2:X4} NbTrans {3}", p[0] >> 4, p[0] & 0x0F, BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(1)), p[3] & 0x0F),
            (LoRaWanCid.LinkAdr, true, 1) => string.Format(ic, "power {0}, DR {1}, mask {2}", Ok(p[0], 2), Ok(p[0], 1), Ok(p[0], 0)),
            (LoRaWanCid.DutyCycle, false, 1) => string.Format(ic, "max duty cycle 1/{0}", 1 << (p[0] & 0x0F)),
            (LoRaWanCid.RxTimingSetup, false, 1) => string.Format(ic, "RX1 delay {0} s", Math.Max(1, p[0] & 0x0F)),
            (LoRaWanCid.DeviceTime, false, 5) => (LoRaWanMacCommands.GpsEpoch + TimeSpan.FromSeconds(BinaryPrimitives.ReadUInt32LittleEndian(p) - LoRaWanMacCommands.GpsLeapSeconds + (p[4] / 256.0))).ToString("u", ic),
            _ => p.Length == 0 ? "" : Convert.ToHexString(p),
        };

        static string Ok(byte status, int bit) => (status & (1 << bit)) != 0 ? "ok" : "rejected";
    }

    /// <inheritdoc />
    public override string ToString() => Describe() is { Length: > 0 } d ? $"{Name}({d})" : Name;
}

/// <summary>Parses and encodes MAC command sequences (FOpts or FRMPayload on FPort 0).</summary>
public static class LoRaWanMacCommands
{
    internal static readonly DateTimeOffset GpsEpoch = new(1980, 1, 6, 0, 0, 0, TimeSpan.Zero);
    internal const int GpsLeapSeconds = 18;

    // Payload lengths per CID: [uplink, downlink]; -1 = unknown in that direction.
    private static readonly Dictionary<byte, (int Up, int Down, string Name)> Table = new()
    {
        [LoRaWanCid.LinkCheck] = (0, 2, "LinkCheck"),
        [LoRaWanCid.LinkAdr] = (1, 4, "LinkADR"),
        [LoRaWanCid.DutyCycle] = (0, 1, "DutyCycle"),
        [LoRaWanCid.RxParamSetup] = (1, 4, "RXParamSetup"),
        [LoRaWanCid.DevStatus] = (2, 0, "DevStatus"),
        [LoRaWanCid.NewChannel] = (1, 5, "NewChannel"),
        [LoRaWanCid.RxTimingSetup] = (0, 1, "RXTimingSetup"),
        [LoRaWanCid.TxParamSetup] = (0, 1, "TxParamSetup"),
        [LoRaWanCid.DlChannel] = (1, 4, "DlChannel"),
        [LoRaWanCid.DeviceTime] = (0, 5, "DeviceTime"),
    };

    private static readonly HashSet<byte> DeviceInitiated = [LoRaWanCid.LinkCheck, LoRaWanCid.DeviceTime];

    /// <summary>Command name for a direction (<c>LinkCheckReq</c> uplink, <c>LinkCheckAns</c> downlink).</summary>
    public static string Name(byte cid, bool uplink)
    {
        if (!Table.TryGetValue(cid, out var e)) return $"CID 0x{cid:X2}";
        var request = DeviceInitiated.Contains(cid) ? uplink : !uplink;
        return e.Name + (request ? "Req" : "Ans");
    }

    /// <summary>
    /// Parses a sequence of commands. Parsing stops at an unknown CID (its length is unknown) and the rest is returned
    /// as one opaque command.
    /// </summary>
    public static IReadOnlyList<LoRaWanMacCommand> Parse(ReadOnlySpan<byte> data, bool uplink)
    {
        var list = new List<LoRaWanMacCommand>();
        var i = 0;
        while (i < data.Length)
        {
            var cid = data[i];
            var length = Table.TryGetValue(cid, out var e) ? (uplink ? e.Up : e.Down) : -1;
            if (length < 0 || i + 1 + length > data.Length)
            {
                list.Add(new LoRaWanMacCommand(cid, uplink, data[(i + 1)..].ToArray()));
                break;
            }

            list.Add(new LoRaWanMacCommand(cid, uplink, data.Slice(i + 1, length).ToArray()));
            i += 1 + length;
        }

        return list;
    }

    /// <summary>Encodes commands back to back.</summary>
    public static byte[] Encode(IEnumerable<LoRaWanMacCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        var buffer = new List<byte>();
        foreach (var c in commands)
        {
            buffer.Add(c.Cid);
            buffer.AddRange(c.Payload);
        }

        return [.. buffer];
    }

    /// <summary>One-line summary of a command sequence.</summary>
    public static string Summarize(ReadOnlySpan<byte> data, bool uplink) => string.Join(", ", Parse(data, uplink));
}
