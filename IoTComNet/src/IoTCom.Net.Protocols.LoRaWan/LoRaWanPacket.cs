using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace IoTCom.Net.Protocols.LoRaWan;

/// <summary>
/// A LoRaWAN PHYPayload decoded structurally (no keys needed): MHDR, the Join-Request fields or the data frame header
/// (DevAddr, FCtrl, FCnt, FOpts, FPort), the still-encrypted FRMPayload and the MIC. Use <see cref="VerifyMic"/> and
/// <see cref="DecryptPayload"/> with session keys, or <see cref="LoRaWanJoinAccept.TryDecrypt"/> for a Join-Accept.
/// </summary>
public sealed class LoRaWanPacket
{
    private LoRaWanPacket(byte[] raw) => Raw = raw;

    /// <summary>The complete PHYPayload.</summary>
    public byte[] Raw { get; }

    /// <summary>Message type.</summary>
    public LoRaWanMType MType { get; private init; }

    /// <summary>Major version (0 = LoRaWAN R1).</summary>
    public byte Major { get; private init; }

    /// <summary>MIC as transmitted (the last 4 bytes, little-endian).</summary>
    public uint Mic { get; private init; }

    /// <summary>Join-Request: JoinEUI (AppEUI in 1.0).</summary>
    public Eui64 JoinEui { get; private init; }

    /// <summary>Join-Request: DevEUI.</summary>
    public Eui64 DevEui { get; private init; }

    /// <summary>Join-Request: DevNonce.</summary>
    public ushort DevNonce { get; private init; }

    /// <summary>Data frame: device address.</summary>
    public DevAddr DevAddr { get; private init; }

    /// <summary>Data frame: FCtrl.</summary>
    public LoRaWanFCtrl FCtrl { get; private init; }

    /// <summary>Data frame: the 16 least significant bits of the frame counter.</summary>
    public ushort FCnt { get; private init; }

    /// <summary>Data frame: MAC commands piggybacked in the header (clear text in 1.0.x).</summary>
    public ReadOnlyMemory<byte> FOpts { get; private init; }

    /// <summary>Data frame: port (null when there is no FRMPayload). 0 carries MAC commands.</summary>
    public byte? FPort { get; private init; }

    /// <summary>Data frame: FRMPayload as transmitted (encrypted). For a Join-Accept: the encrypted body.</summary>
    public ReadOnlyMemory<byte> FrmPayload { get; private init; }

    /// <summary>Data frame (up or down, confirmed or not).</summary>
    public bool IsData => MType is >= LoRaWanMType.UnconfirmedDataUp and <= LoRaWanMType.ConfirmedDataDown;

    /// <summary>Sent by a device (Join-Request, Rejoin-Request, data up).</summary>
    public bool IsUplink => MType is LoRaWanMType.JoinRequest or LoRaWanMType.UnconfirmedDataUp or LoRaWanMType.ConfirmedDataUp or LoRaWanMType.RejoinRequest;

    /// <summary>Confirmed data frame.</summary>
    public bool IsConfirmed => MType is LoRaWanMType.ConfirmedDataUp or LoRaWanMType.ConfirmedDataDown;

    /// <summary>Decodes a PHYPayload; never throws for malformed input.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> phy, out LoRaWanPacket? packet, out string? error)
    {
        packet = null;
        error = null;
        if (phy.Length < 5)
        {
            error = "A PHYPayload has at least MHDR and a 4-byte MIC.";
            return false;
        }

        var mhdr = phy[0];
        var mtype = (LoRaWanMType)(mhdr >> 5);
        var major = (byte)(mhdr & 0x03);
        var mic = BinaryPrimitives.ReadUInt32LittleEndian(phy[^4..]);
        var raw = phy.ToArray();
        switch (mtype)
        {
            case LoRaWanMType.JoinRequest:
                if (phy.Length != 23)
                {
                    error = $"A Join-Request is 23 bytes, got {phy.Length}.";
                    return false;
                }

                packet = new LoRaWanPacket(raw)
                {
                    MType = mtype, Major = major, Mic = mic,
                    JoinEui = Eui64.ReadLittleEndian(phy[1..]),
                    DevEui = Eui64.ReadLittleEndian(phy[9..]),
                    DevNonce = BinaryPrimitives.ReadUInt16LittleEndian(phy[17..]),
                };
                return true;

            case LoRaWanMType.JoinAccept:
                if (phy.Length is not (17 or 33))
                {
                    error = $"A Join-Accept is 17 or 33 bytes, got {phy.Length}.";
                    return false;
                }

                packet = new LoRaWanPacket(raw) { MType = mtype, Major = major, Mic = mic, FrmPayload = raw.AsMemory(1) };
                return true;

            case LoRaWanMType.UnconfirmedDataUp or LoRaWanMType.UnconfirmedDataDown or LoRaWanMType.ConfirmedDataUp or LoRaWanMType.ConfirmedDataDown:
            {
                // MHDR(1) DevAddr(4) FCtrl(1) FCnt(2) FOpts(0..15) [FPort(1) FRMPayload] MIC(4)
                if (phy.Length < 12)
                {
                    error = $"A data frame has at least 12 bytes, got {phy.Length}.";
                    return false;
                }

                var uplink = mtype is LoRaWanMType.UnconfirmedDataUp or LoRaWanMType.ConfirmedDataUp;
                var fctrl = new LoRaWanFCtrl(phy[5], uplink);
                var fhdrEnd = 8 + fctrl.FOptsLength;
                var micAt = phy.Length - 4;
                if (fhdrEnd > micAt)
                {
                    error = $"FOptsLen={fctrl.FOptsLength} runs past the end of the frame.";
                    return false;
                }

                byte? fport = null;
                ReadOnlyMemory<byte> frm = default;
                if (fhdrEnd < micAt)
                {
                    fport = phy[fhdrEnd];
                    frm = raw.AsMemory(fhdrEnd + 1, micAt - fhdrEnd - 1);
                    if (fport == 0 && fctrl.FOptsLength > 0)
                    {
                        error = "MAC commands cannot be both in FOpts and on FPort 0.";
                        return false;
                    }
                }

                packet = new LoRaWanPacket(raw)
                {
                    MType = mtype, Major = major, Mic = mic,
                    DevAddr = DevAddr.ReadLittleEndian(phy[1..]),
                    FCtrl = fctrl,
                    FCnt = BinaryPrimitives.ReadUInt16LittleEndian(phy[6..]),
                    FOpts = raw.AsMemory(8, fctrl.FOptsLength),
                    FPort = fport,
                    FrmPayload = frm,
                };
                return true;
            }

            default:
                packet = new LoRaWanPacket(raw) { MType = mtype, Major = major, Mic = mic, FrmPayload = raw.AsMemory(1, raw.Length - 5) };
                return true;
        }
    }

    /// <summary>Decodes a PHYPayload or throws <see cref="FormatException"/>.</summary>
    public static LoRaWanPacket Decode(ReadOnlySpan<byte> phy) =>
        TryDecode(phy, out var p, out var error) ? p! : throw new FormatException(error);

    /// <summary>
    /// Verifies the MIC. Join-Request: <paramref name="key"/> is the AppKey. Data frame: the NwkSKey, with the full
    /// 32-bit counter (<paramref name="fullFCnt"/>, default: the 16 transmitted bits).
    /// </summary>
    public bool VerifyMic(ReadOnlySpan<byte> key, uint? fullFCnt = null)
    {
        var body = Raw.AsSpan(0, Raw.Length - 4);
        return MType switch
        {
            LoRaWanMType.JoinRequest => LoRaWanCrypto.ComputeJoinMic(key, body) == Mic,
            _ when IsData => LoRaWanCrypto.ComputeDataMic(key, IsUplink, DevAddr, fullFCnt ?? FCnt, body) == Mic,
            _ => false,
        };
    }

    /// <summary>Decrypts FRMPayload with the session keys (NwkSKey on FPort 0, AppSKey otherwise).</summary>
    public byte[] DecryptPayload(LoRaWanSessionKeys keys, uint? fullFCnt = null)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (FPort is not { } port) return [];
        return LoRaWanCrypto.CryptPayload(keys.PayloadKey(port), IsUplink, DevAddr, fullFCnt ?? FCnt, FrmPayload.Span);
    }

    /// <summary>
    /// Reconstructs the full 32-bit counter from the 16 transmitted bits and the last counter seen
    /// (the smallest value ≥ <paramref name="last"/> + 1 whose low 16 bits match).
    /// </summary>
    public static uint ReconstructFCnt(uint last, ushort received, bool first = false)
    {
        if (first) return received;
        var candidate = (last & 0xFFFF0000u) | received;
        if (candidate <= last) candidate += 0x10000;
        return candidate;
    }

    /// <summary>Encodes a Join-Request with its MIC.</summary>
    public static byte[] EncodeJoinRequest(Eui64 joinEui, Eui64 devEui, ushort devNonce, ReadOnlySpan<byte> appKey)
    {
        var buffer = new byte[23];
        buffer[0] = (byte)LoRaWanMType.JoinRequest << 5;
        joinEui.WriteLittleEndian(buffer.AsSpan(1));
        devEui.WriteLittleEndian(buffer.AsSpan(9));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(17), devNonce);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(19), LoRaWanCrypto.ComputeJoinMic(appKey, buffer.AsSpan(0, 19)));
        return buffer;
    }

    /// <summary>
    /// Encodes a data frame: encrypts <paramref name="payload"/> (on <paramref name="fport"/>; FPort 0 means MAC
    /// commands) and appends the MIC computed with the full counter <paramref name="fCnt"/>.
    /// </summary>
    public static byte[] EncodeData(LoRaWanMType mtype, DevAddr devAddr, LoRaWanFCtrl fctrl, uint fCnt, ReadOnlySpan<byte> fOpts,
        byte? fport, ReadOnlySpan<byte> payload, LoRaWanSessionKeys keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (mtype is < LoRaWanMType.UnconfirmedDataUp or > LoRaWanMType.ConfirmedDataDown)
            throw new ArgumentOutOfRangeException(nameof(mtype), "Not a data message type.");
        if (fOpts.Length > 15) throw new ArgumentException("FOpts holds at most 15 bytes.", nameof(fOpts));
        if (fport is null && !payload.IsEmpty) throw new ArgumentException("A payload needs an FPort.", nameof(fport));
        if (fport == 0 && !fOpts.IsEmpty) throw new ArgumentException("MAC commands cannot be both in FOpts and on FPort 0.", nameof(fOpts));
        var uplink = mtype is LoRaWanMType.UnconfirmedDataUp or LoRaWanMType.ConfirmedDataUp;
        fctrl = new LoRaWanFCtrl((byte)((fctrl.Value & 0xF0) | fOpts.Length), uplink);

        var length = 8 + fOpts.Length + (fport is null ? 0 : 1 + payload.Length) + 4;
        var buffer = new byte[length];
        buffer[0] = (byte)((byte)mtype << 5);
        devAddr.WriteLittleEndian(buffer.AsSpan(1));
        buffer[5] = fctrl.Value;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(6), (ushort)fCnt);
        fOpts.CopyTo(buffer.AsSpan(8));
        var pos = 8 + fOpts.Length;
        if (fport is { } port)
        {
            buffer[pos++] = port;
            LoRaWanCrypto.CryptPayload(keys.PayloadKey(port), uplink, devAddr, fCnt, payload).CopyTo(buffer, pos);
            pos += payload.Length;
        }

        var mic = LoRaWanCrypto.ComputeDataMic(keys.NwkSKey, uplink, devAddr, fCnt, buffer.AsSpan(0, pos));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos), mic);
        return buffer;
    }

    /// <inheritdoc />
    public override string ToString() => MType switch
    {
        LoRaWanMType.JoinRequest => $"Join-Request DevEUI={DevEui} JoinEUI={JoinEui} DevNonce={DevNonce}",
        LoRaWanMType.JoinAccept => $"Join-Accept ({Raw.Length} bytes, encrypted)",
        _ when IsData => new StringBuilder()
            .Append(IsConfirmed ? "Confirmed" : "Unconfirmed").Append(IsUplink ? " up " : " down ")
            .Append("DevAddr=").Append(DevAddr).Append(" FCnt=").Append(FCnt.ToString(CultureInfo.InvariantCulture))
            .Append(FCtrl.Value == 0 ? "" : " " + FCtrl)
            .Append(FPort is { } p ? $" FPort={p} {FrmPayload.Length} B" : "")
            .ToString(),
        _ => $"{MType} ({Raw.Length} bytes)",
    };
}

/// <summary>The clear-text content of a Join-Accept (LoRaWAN 1.0.x).</summary>
/// <param name="JoinNonce">JoinNonce (AppNonce in 1.0), 24 bits.</param>
/// <param name="NetId">Network identifier, 24 bits.</param>
/// <param name="DevAddr">Assigned device address.</param>
/// <param name="Rx1DrOffset">RX1 data rate offset (DLSettings bits 6..4).</param>
/// <param name="Rx2DataRate">RX2 data rate (DLSettings bits 3..0).</param>
/// <param name="RxDelay">RX1 delay in seconds (0 means 1).</param>
/// <param name="CfList">Optional 16-byte channel frequency list.</param>
public sealed record LoRaWanJoinAccept(uint JoinNonce, uint NetId, DevAddr DevAddr, byte Rx1DrOffset = 0, byte Rx2DataRate = 0, byte RxDelay = 1, byte[]? CfList = null)
{
    /// <summary>RX1 delay as a time span.</summary>
    public TimeSpan Rx1Delay => TimeSpan.FromSeconds(Math.Max((byte)1, (byte)(RxDelay & 0x0F)));

    /// <summary>Encodes and encrypts the Join-Accept with <paramref name="appKey"/> (network side).</summary>
    public byte[] Encode(ReadOnlySpan<byte> appKey)
    {
        if (CfList is { Length: not 16 }) throw new InvalidOperationException("A CFList has 16 bytes.");
        var plain = new byte[1 + 12 + (CfList?.Length ?? 0) + 4];
        plain[0] = (byte)LoRaWanMType.JoinAccept << 5;
        LoRaWanCrypto.WriteUInt24(plain.AsSpan(1), JoinNonce);
        LoRaWanCrypto.WriteUInt24(plain.AsSpan(4), NetId);
        DevAddr.WriteLittleEndian(plain.AsSpan(7));
        plain[11] = (byte)(((Rx1DrOffset & 0x07) << 4) | (Rx2DataRate & 0x0F));
        plain[12] = RxDelay;
        CfList?.CopyTo(plain, 13);
        var micAt = plain.Length - 4;
        BinaryPrimitives.WriteUInt32LittleEndian(plain.AsSpan(micAt), LoRaWanCrypto.ComputeJoinMic(appKey, plain.AsSpan(0, micAt)));
        var result = new byte[plain.Length];
        result[0] = plain[0];
        LoRaWanCrypto.EncryptJoinAccept(appKey, plain.AsSpan(1)).CopyTo(result, 1);
        return result;
    }

    /// <summary>Decrypts a Join-Accept PHYPayload and checks its MIC (device side).</summary>
    public static bool TryDecrypt(ReadOnlySpan<byte> phy, ReadOnlySpan<byte> appKey, out LoRaWanJoinAccept? accept, out string? error)
    {
        accept = null;
        if (phy.Length is not (17 or 33) || (LoRaWanMType)(phy[0] >> 5) != LoRaWanMType.JoinAccept)
        {
            error = "Not a Join-Accept.";
            return false;
        }

        var plain = new byte[phy.Length];
        plain[0] = phy[0];
        LoRaWanCrypto.DecryptJoinAccept(appKey, phy[1..]).CopyTo(plain, 1);
        var micAt = plain.Length - 4;
        if (LoRaWanCrypto.ComputeJoinMic(appKey, plain.AsSpan(0, micAt)) != BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(micAt)))
        {
            error = "Join-Accept MIC mismatch (wrong AppKey?).";
            return false;
        }

        accept = new LoRaWanJoinAccept(
            LoRaWanCrypto.ReadUInt24(plain.AsSpan(1)),
            LoRaWanCrypto.ReadUInt24(plain.AsSpan(4)),
            DevAddr.ReadLittleEndian(plain.AsSpan(7)),
            (byte)((plain[11] >> 4) & 0x07),
            (byte)(plain[11] & 0x0F),
            plain[12],
            plain.Length == 33 ? plain.AsSpan(13, 16).ToArray() : null);
        error = null;
        return true;
    }

    /// <summary>The session keys this Join-Accept establishes for <paramref name="devNonce"/>.</summary>
    public LoRaWanSessionKeys DeriveSessionKeys(ReadOnlySpan<byte> appKey, ushort devNonce) =>
        LoRaWanCrypto.DeriveSessionKeys(appKey, JoinNonce, NetId, devNonce);
}

/// <summary>Frame-lane description of LoRaWAN PHYPayloads (shared colours with the other protocols).</summary>
public static class LoRaWanAnatomy
{
    /// <summary>Describes MHDR, the header fields, FOpts (with MAC command names), FPort, FRMPayload and MIC.</summary>
    public static IReadOnlyList<FrameField> Describe(ReadOnlySpan<byte> phy)
    {
        if (!LoRaWanPacket.TryDecode(phy, out var p, out var error))
            return [new FrameField("Invalid", 0, phy.Length, FrameFieldKind.Error, error)];
        var fields = new List<FrameField> { new("MHDR", 0, 1, FrameFieldKind.Function, MTypeName(p!.MType)) };
        switch (p.MType)
        {
            case LoRaWanMType.JoinRequest:
                fields.Add(new FrameField("JoinEUI", 1, 8, FrameFieldKind.Address, p.JoinEui.ToString()));
                fields.Add(new FrameField("DevEUI", 9, 8, FrameFieldKind.Address, p.DevEui.ToString()));
                fields.Add(new FrameField("DevNonce", 17, 2, FrameFieldKind.Header, p.DevNonce.ToString(CultureInfo.InvariantCulture)));
                break;
            case LoRaWanMType.JoinAccept:
                fields.Add(new FrameField("Encrypted", 1, phy.Length - 5, FrameFieldKind.Data, "AppKey needed"));
                break;
            case var _ when p.IsData:
                fields.Add(new FrameField("DevAddr", 1, 4, FrameFieldKind.Address, p.DevAddr.ToString()));
                fields.Add(new FrameField("FCtrl", 5, 1, FrameFieldKind.Header, p.FCtrl.ToString()));
                fields.Add(new FrameField("FCnt", 6, 2, FrameFieldKind.Header, p.FCnt.ToString(CultureInfo.InvariantCulture)));
                if (p.FOpts.Length > 0)
                    fields.Add(new FrameField("FOpts", 8, p.FOpts.Length, FrameFieldKind.Function, LoRaWanMacCommands.Summarize(p.FOpts.Span, p.IsUplink)));
                if (p.FPort is { } port)
                {
                    var at = 8 + p.FOpts.Length;
                    fields.Add(new FrameField("FPort", at, 1, FrameFieldKind.Length, port.ToString(CultureInfo.InvariantCulture)));
                    if (p.FrmPayload.Length > 0)
                        fields.Add(new FrameField("FRMPayload", at + 1, p.FrmPayload.Length, FrameFieldKind.Data, port == 0 ? "MAC commands (NwkSKey)" : "encrypted (AppSKey)"));
                }

                break;
            default:
                if (phy.Length > 5) fields.Add(new FrameField("Payload", 1, phy.Length - 5, FrameFieldKind.Data));
                break;
        }

        fields.Add(new FrameField("MIC", phy.Length - 4, 4, FrameFieldKind.Checksum, Convert.ToHexString(phy[^4..])));
        return fields;
    }

    /// <summary>Short display name of a message type.</summary>
    public static string MTypeName(LoRaWanMType mtype) => mtype switch
    {
        LoRaWanMType.JoinRequest => "Join-Request",
        LoRaWanMType.JoinAccept => "Join-Accept",
        LoRaWanMType.UnconfirmedDataUp => "Unconfirmed Up",
        LoRaWanMType.UnconfirmedDataDown => "Unconfirmed Down",
        LoRaWanMType.ConfirmedDataUp => "Confirmed Up",
        LoRaWanMType.ConfirmedDataDown => "Confirmed Down",
        LoRaWanMType.RejoinRequest => "Rejoin-Request",
        _ => "Proprietary",
    };
}
