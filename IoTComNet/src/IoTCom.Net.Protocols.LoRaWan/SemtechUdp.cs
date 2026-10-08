using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace IoTCom.Net.Protocols.LoRaWan;

/// <summary>Semtech UDP packet-forwarder message identifiers (protocol version 2).</summary>
public enum SemtechPacketType : byte
{
    /// <summary>Gateway → server: received packets and/or status.</summary>
    PushData = 0x00,
    /// <summary>Server → gateway: PUSH_DATA received.</summary>
    PushAck = 0x01,
    /// <summary>Gateway → server: keep-alive that opens the downlink path.</summary>
    PullData = 0x02,
    /// <summary>Server → gateway: a packet to transmit.</summary>
    PullResp = 0x03,
    /// <summary>Server → gateway: PULL_DATA received.</summary>
    PullAck = 0x04,
    /// <summary>Gateway → server: result of a PULL_RESP.</summary>
    TxAck = 0x05,
}

/// <summary>A packet received by a gateway (<c>rxpk</c>).</summary>
public sealed record SemtechRxPacket
{
    /// <summary>The PHYPayload.</summary>
    public required byte[] Data { get; init; }

    /// <summary>Concentrator timestamp in microseconds (wraps at 2^32); downlinks are scheduled relative to it.</summary>
    public uint Tmst { get; init; }

    /// <summary>UTC reception time, if the gateway has one.</summary>
    public DateTimeOffset? Time { get; init; }

    /// <summary>Frequency in MHz.</summary>
    public double Frequency { get; init; }

    /// <summary>Concentrator IF channel.</summary>
    public int Channel { get; init; }

    /// <summary>RF chain.</summary>
    public int RfChain { get; init; }

    /// <summary>CRC status: 1 ok, -1 bad, 0 none.</summary>
    public int CrcStatus { get; init; } = 1;

    /// <summary>Data rate, e.g. <c>SF7BW125</c>.</summary>
    public string DataRate { get; init; } = "SF7BW125";

    /// <summary>Coding rate, e.g. <c>4/5</c>.</summary>
    public string CodingRate { get; init; } = "4/5";

    /// <summary>RSSI in dBm.</summary>
    public double Rssi { get; init; }

    /// <summary>SNR in dB.</summary>
    public double Snr { get; init; }
}

/// <summary>A packet the server asks a gateway to transmit (<c>txpk</c>).</summary>
public sealed record SemtechTxPacket
{
    /// <summary>The PHYPayload.</summary>
    public required byte[] Data { get; init; }

    /// <summary>Send immediately, ignoring <see cref="Tmst"/>.</summary>
    public bool Immediate { get; init; }

    /// <summary>Concentrator timestamp to transmit at (microseconds).</summary>
    public uint Tmst { get; init; }

    /// <summary>Frequency in MHz.</summary>
    public double Frequency { get; init; }

    /// <summary>RF chain.</summary>
    public int RfChain { get; init; }

    /// <summary>Transmit power in dBm.</summary>
    public int Power { get; init; } = 14;

    /// <summary>Data rate, e.g. <c>SF7BW125</c>.</summary>
    public string DataRate { get; init; } = "SF7BW125";

    /// <summary>Coding rate.</summary>
    public string CodingRate { get; init; } = "4/5";

    /// <summary>Inverted polarity (true for LoRaWAN downlinks).</summary>
    public bool InvertPolarity { get; init; } = true;

    /// <summary>Disable the payload CRC (true for LoRaWAN downlinks).</summary>
    public bool NoCrc { get; init; } = true;
}

/// <summary>Gateway statistics (<c>stat</c>).</summary>
public sealed record SemtechGatewayStatus
{
    /// <summary>Gateway time.</summary>
    public DateTimeOffset Time { get; init; }

    /// <summary>Latitude.</summary>
    public double? Latitude { get; init; }

    /// <summary>Longitude.</summary>
    public double? Longitude { get; init; }

    /// <summary>Altitude in metres.</summary>
    public int? Altitude { get; init; }

    /// <summary>Radio packets received.</summary>
    public int RxReceived { get; init; }

    /// <summary>Radio packets received with a valid CRC.</summary>
    public int RxOk { get; init; }

    /// <summary>Radio packets forwarded.</summary>
    public int RxForwarded { get; init; }

    /// <summary>Percentage of upstream datagrams acknowledged.</summary>
    public double AckRatio { get; init; }

    /// <summary>Downlink datagrams received.</summary>
    public int DownlinkReceived { get; init; }

    /// <summary>Packets emitted.</summary>
    public int TxEmitted { get; init; }
}

/// <summary>A Semtech UDP datagram: header (version, token, type, gateway EUI) and JSON body.</summary>
public sealed record SemtechPacket
{
    /// <summary>Protocol version (2).</summary>
    public byte Version { get; init; } = 2;

    /// <summary>Random token echoed by the acknowledgement.</summary>
    public ushort Token { get; init; }

    /// <summary>Message type.</summary>
    public SemtechPacketType Type { get; init; }

    /// <summary>Gateway EUI (PUSH_DATA, PULL_DATA, TX_ACK).</summary>
    public Eui64? GatewayEui { get; init; }

    /// <summary>Received packets (PUSH_DATA).</summary>
    public IReadOnlyList<SemtechRxPacket> RxPackets { get; init; } = [];

    /// <summary>Gateway status (PUSH_DATA).</summary>
    public SemtechGatewayStatus? Status { get; init; }

    /// <summary>Packet to transmit (PULL_RESP).</summary>
    public SemtechTxPacket? TxPacket { get; init; }

    /// <summary>TX_ACK error (<c>NONE</c> when transmitted).</summary>
    public string? TxError { get; init; }

    /// <summary>The JSON body as received (empty for acknowledgements).</summary>
    public string Json { get; init; } = "";

    private static bool HasEui(SemtechPacketType t) => t is SemtechPacketType.PushData or SemtechPacketType.PullData or SemtechPacketType.TxAck;

    /// <summary>Encodes the datagram.</summary>
    public byte[] Encode()
    {
        var json = Type switch
        {
            SemtechPacketType.PushData => PushJson(),
            SemtechPacketType.PullResp => TxJson(),
            SemtechPacketType.TxAck when TxError is not null => Encoding.UTF8.GetBytes($"{{\"txpk_ack\":{{\"error\":\"{TxError}\"}}}}"),
            _ => [],
        };
        var header = 4 + (HasEui(Type) ? 8 : 0);
        var buffer = new byte[header + json.Length];
        buffer[0] = Version;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(1), Token);
        buffer[3] = (byte)Type;
        if (HasEui(Type)) (GatewayEui ?? default).WriteBigEndian(buffer.AsSpan(4));
        json.CopyTo(buffer, header);
        return buffer;
    }

    /// <summary>Decodes a datagram; never throws for malformed input.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> data, out SemtechPacket? packet, out string? error)
    {
        packet = null;
        error = null;
        if (data.Length < 4)
        {
            error = "A Semtech UDP datagram has at least 4 bytes.";
            return false;
        }

        if (data[0] is not (1 or 2))
        {
            error = $"Unsupported protocol version {data[0]}.";
            return false;
        }

        var type = (SemtechPacketType)data[3];
        if (type > SemtechPacketType.TxAck)
        {
            error = $"Unknown message type 0x{data[3]:X2}.";
            return false;
        }

        var header = 4 + (HasEui(type) ? 8 : 0);
        if (data.Length < header)
        {
            error = $"{type} needs a gateway EUI.";
            return false;
        }

        var json = data[header..];
        var p = new SemtechPacket
        {
            Version = data[0],
            Token = BinaryPrimitives.ReadUInt16BigEndian(data[1..]),
            Type = type,
            GatewayEui = HasEui(type) ? Eui64.ReadBigEndian(data[4..]) : null,
            Json = Encoding.UTF8.GetString(json),
        };
        if (json.IsEmpty || json.IndexOfAnyExcept((byte)0) < 0)
        {
            packet = p;
            return true;
        }

        try
        {
            using var doc = JsonDocument.Parse(json.ToArray());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new JsonException("The body is not a JSON object.");
            var rx = new List<SemtechRxPacket>();
            if (root.TryGetProperty("rxpk", out var rxpk) && rxpk.ValueKind == JsonValueKind.Array)
                foreach (var e in rxpk.EnumerateArray()) rx.Add(ReadRx(e));
            packet = p with
            {
                RxPackets = rx,
                Status = root.TryGetProperty("stat", out var stat) && stat.ValueKind == JsonValueKind.Object ? ReadStat(stat) : null,
                TxPacket = root.TryGetProperty("txpk", out var txpk) && txpk.ValueKind == JsonValueKind.Object ? ReadTx(txpk) : null,
                TxError = root.TryGetProperty("txpk_ack", out var ack) && ack.ValueKind == JsonValueKind.Object && ack.TryGetProperty("error", out var err) ? err.GetString() : null,
            };
            return true;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            error = $"Invalid JSON body: {ex.Message}";
            return false;
        }
    }

    /// <summary>The acknowledgement a server sends for this PUSH_DATA or PULL_DATA.</summary>
    public SemtechPacket Acknowledgement() => new()
    {
        Version = Version,
        Token = Token,
        Type = Type switch
        {
            SemtechPacketType.PushData => SemtechPacketType.PushAck,
            SemtechPacketType.PullData => SemtechPacketType.PullAck,
            _ => throw new InvalidOperationException($"{Type} is not acknowledged."),
        },
    };

    /// <inheritdoc />
    public override string ToString()
    {
        var sb = new StringBuilder(Type switch
        {
            SemtechPacketType.PushData => "PUSH_DATA",
            SemtechPacketType.PushAck => "PUSH_ACK",
            SemtechPacketType.PullData => "PULL_DATA",
            SemtechPacketType.PullResp => "PULL_RESP",
            SemtechPacketType.PullAck => "PULL_ACK",
            _ => "TX_ACK",
        });
        sb.Append(CultureInfo.InvariantCulture, $" token={Token:X4}");
        if (GatewayEui is { } eui) sb.Append(" gw=").Append(eui);
        foreach (var rx in RxPackets) sb.Append(CultureInfo.InvariantCulture, $" rxpk[{rx.Frequency:0.0##} MHz {rx.DataRate} RSSI {rx.Rssi:0} SNR {rx.Snr:0.0} {rx.Data.Length} B]");
        if (Status is not null) sb.Append(" stat");
        if (TxPacket is { } tx) sb.Append(CultureInfo.InvariantCulture, $" txpk[{tx.Frequency:0.0##} MHz {tx.DataRate} {(tx.Immediate ? "now" : $"tmst {tx.Tmst}")} {tx.Data.Length} B]");
        if (TxError is not null) sb.Append(" error=").Append(TxError);
        return sb.ToString();
    }

    private byte[] PushJson()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            if (RxPackets.Count > 0)
            {
                w.WriteStartArray("rxpk");
                foreach (var rx in RxPackets)
                {
                    w.WriteStartObject();
                    if (rx.Time is { } t) w.WriteString("time", t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture));
                    w.WriteNumber("tmst", rx.Tmst);
                    w.WriteNumber("chan", rx.Channel);
                    w.WriteNumber("rfch", rx.RfChain);
                    w.WriteNumber("freq", Math.Round(rx.Frequency, 6));
                    w.WriteNumber("stat", rx.CrcStatus);
                    w.WriteString("modu", "LORA");
                    w.WriteString("datr", rx.DataRate);
                    w.WriteString("codr", rx.CodingRate);
                    w.WriteNumber("rssi", Math.Round(rx.Rssi));
                    w.WriteNumber("lsnr", Math.Round(rx.Snr, 1));
                    w.WriteNumber("size", rx.Data.Length);
                    w.WriteString("data", Convert.ToBase64String(rx.Data));
                    w.WriteEndObject();
                }

                w.WriteEndArray();
            }

            if (Status is { } s)
            {
                w.WriteStartObject("stat");
                w.WriteString("time", s.Time.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'GMT'", CultureInfo.InvariantCulture));
                if (s.Latitude is { } lat) w.WriteNumber("lati", lat);
                if (s.Longitude is { } lon) w.WriteNumber("long", lon);
                if (s.Altitude is { } alt) w.WriteNumber("alti", alt);
                w.WriteNumber("rxnb", s.RxReceived);
                w.WriteNumber("rxok", s.RxOk);
                w.WriteNumber("rxfw", s.RxForwarded);
                w.WriteNumber("ackr", Math.Round(s.AckRatio, 1));
                w.WriteNumber("dwnb", s.DownlinkReceived);
                w.WriteNumber("txnb", s.TxEmitted);
                w.WriteEndObject();
            }

            w.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private byte[] TxJson()
    {
        var tx = TxPacket ?? throw new InvalidOperationException("PULL_RESP needs a TxPacket.");
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteStartObject("txpk");
            if (tx.Immediate) w.WriteBoolean("imme", true);
            else w.WriteNumber("tmst", tx.Tmst);
            w.WriteNumber("freq", Math.Round(tx.Frequency, 6));
            w.WriteNumber("rfch", tx.RfChain);
            w.WriteNumber("powe", tx.Power);
            w.WriteString("modu", "LORA");
            w.WriteString("datr", tx.DataRate);
            w.WriteString("codr", tx.CodingRate);
            w.WriteBoolean("ipol", tx.InvertPolarity);
            if (tx.NoCrc) w.WriteBoolean("ncrc", true);
            w.WriteNumber("size", tx.Data.Length);
            w.WriteString("data", Convert.ToBase64String(tx.Data));
            w.WriteEndObject();
            w.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    // Semtech's forwarder writes base64 without padding (bin_to_b64_nopad); accept both forms.
    private static byte[] FromBase64(string? text)
    {
        var s = (text ?? "").Trim();
        return Convert.FromBase64String((s.Length % 4) switch { 2 => s + "==", 3 => s + "=", _ => s });
    }

    private static SemtechRxPacket ReadRx(JsonElement e) => new()
    {
        Data = FromBase64(e.GetProperty("data").GetString()),
        Tmst = e.TryGetProperty("tmst", out var tmst) ? tmst.GetUInt32() : 0,
        Time = e.TryGetProperty("time", out var time) && DateTimeOffset.TryParse(time.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null,
        Frequency = e.TryGetProperty("freq", out var freq) ? freq.GetDouble() : 0,
        Channel = e.TryGetProperty("chan", out var chan) ? chan.GetInt32() : 0,
        RfChain = e.TryGetProperty("rfch", out var rfch) ? rfch.GetInt32() : 0,
        CrcStatus = e.TryGetProperty("stat", out var stat) ? stat.GetInt32() : 1,
        DataRate = e.TryGetProperty("datr", out var datr) && datr.ValueKind == JsonValueKind.String ? datr.GetString()! : "SF7BW125",
        CodingRate = e.TryGetProperty("codr", out var codr) ? codr.GetString() ?? "4/5" : "4/5",
        Rssi = e.TryGetProperty("rssi", out var rssi) ? rssi.GetDouble() : 0,
        Snr = e.TryGetProperty("lsnr", out var lsnr) ? lsnr.GetDouble() : 0,
    };

    private static SemtechTxPacket ReadTx(JsonElement e) => new()
    {
        Data = FromBase64(e.GetProperty("data").GetString()),
        Immediate = e.TryGetProperty("imme", out var imme) && imme.ValueKind == JsonValueKind.True,
        Tmst = e.TryGetProperty("tmst", out var tmst) ? tmst.GetUInt32() : 0,
        Frequency = e.TryGetProperty("freq", out var freq) ? freq.GetDouble() : 0,
        RfChain = e.TryGetProperty("rfch", out var rfch) ? rfch.GetInt32() : 0,
        Power = e.TryGetProperty("powe", out var powe) ? powe.GetInt32() : 14,
        DataRate = e.TryGetProperty("datr", out var datr) && datr.ValueKind == JsonValueKind.String ? datr.GetString()! : "SF7BW125",
        CodingRate = e.TryGetProperty("codr", out var codr) ? codr.GetString() ?? "4/5" : "4/5",
        InvertPolarity = !e.TryGetProperty("ipol", out var ipol) || ipol.ValueKind == JsonValueKind.True,
        NoCrc = e.TryGetProperty("ncrc", out var ncrc) && ncrc.ValueKind == JsonValueKind.True,
    };

    private static SemtechGatewayStatus ReadStat(JsonElement e) => new()
    {
        Time = e.TryGetProperty("time", out var time) && DateTimeOffset.TryParse(time.GetString()?.Replace(" GMT", "Z", StringComparison.Ordinal), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : default,
        Latitude = e.TryGetProperty("lati", out var lat) ? lat.GetDouble() : null,
        Longitude = e.TryGetProperty("long", out var lon) ? lon.GetDouble() : null,
        Altitude = e.TryGetProperty("alti", out var alt) ? (int)alt.GetDouble() : null,
        RxReceived = e.TryGetProperty("rxnb", out var rxnb) ? rxnb.GetInt32() : 0,
        RxOk = e.TryGetProperty("rxok", out var rxok) ? rxok.GetInt32() : 0,
        RxForwarded = e.TryGetProperty("rxfw", out var rxfw) ? rxfw.GetInt32() : 0,
        AckRatio = e.TryGetProperty("ackr", out var ackr) ? ackr.GetDouble() : 0,
        DownlinkReceived = e.TryGetProperty("dwnb", out var dwnb) ? dwnb.GetInt32() : 0,
        TxEmitted = e.TryGetProperty("txnb", out var txnb) ? txnb.GetInt32() : 0,
    };
}

/// <summary>Frame-lane description of Semtech UDP datagrams.</summary>
public static class SemtechAnatomy
{
    /// <summary>Describes version, token, type, gateway EUI and the JSON body.</summary>
    public static IReadOnlyList<FrameField> Describe(ReadOnlySpan<byte> data)
    {
        if (!SemtechPacket.TryDecode(data, out var p, out var error))
            return [new FrameField("Invalid", 0, data.Length, FrameFieldKind.Error, error)];
        var fields = new List<FrameField>
        {
            new("Version", 0, 1, FrameFieldKind.Header, p!.Version.ToString(CultureInfo.InvariantCulture)),
            new("Token", 1, 2, FrameFieldKind.Header, p.Token.ToString("X4", CultureInfo.InvariantCulture)),
            new("Type", 3, 1, FrameFieldKind.Function, p.ToString().Split(' ')[0]),
        };
        var at = 4;
        if (p.GatewayEui is { } eui)
        {
            fields.Add(new FrameField("Gateway EUI", 4, 8, FrameFieldKind.Address, eui.ToString()));
            at = 12;
        }

        if (data.Length > at)
        {
            var what = p.RxPackets.Count > 0 ? $"{p.RxPackets.Count} rxpk" + (p.Status is null ? "" : " + stat")
                : p.Status is not null ? "stat" : p.TxPacket is not null ? "txpk" : p.TxError ?? "JSON";
            fields.Add(new FrameField("JSON", at, data.Length - at, FrameFieldKind.Data, what));
        }

        return fields;
    }
}
