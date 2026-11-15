using System.Buffers.Binary;
using System.Globalization;

namespace IoTCom.Net.Protocols.Matter;

/// <summary>Protocol ids (vendor 0 = the Matter standard).</summary>
public static class MatterProtocol
{
    /// <summary>Secure Channel.</summary>
    public const ushort SecureChannel = 0x0000;
    /// <summary>Interaction Model.</summary>
    public const ushort InteractionModel = 0x0001;
    /// <summary>BDX.</summary>
    public const ushort Bdx = 0x0002;
    /// <summary>User Directed Commissioning.</summary>
    public const ushort Udc = 0x0003;
}

/// <summary>Secure Channel opcodes.</summary>
public enum SecureChannelOpcode : byte
{
    /// <summary>Message counter sync request.</summary>
    MsgCounterSyncReq = 0x00,
    /// <summary>Message counter sync response.</summary>
    MsgCounterSyncRsp = 0x01,
    /// <summary>MRP standalone acknowledgement.</summary>
    StandaloneAck = 0x10,
    /// <summary>PASE: PBKDF parameter request.</summary>
    PbkdfParamRequest = 0x20,
    /// <summary>PASE: PBKDF parameter response.</summary>
    PbkdfParamResponse = 0x21,
    /// <summary>PASE: Pake1.</summary>
    Pake1 = 0x22,
    /// <summary>PASE: Pake2.</summary>
    Pake2 = 0x23,
    /// <summary>PASE: Pake3.</summary>
    Pake3 = 0x24,
    /// <summary>CASE: Sigma1.</summary>
    Sigma1 = 0x30,
    /// <summary>CASE: Sigma2.</summary>
    Sigma2 = 0x31,
    /// <summary>CASE: Sigma3.</summary>
    Sigma3 = 0x32,
    /// <summary>CASE: Sigma2 resume.</summary>
    Sigma2Resume = 0x33,
    /// <summary>Status report.</summary>
    StatusReport = 0x40,
}

/// <summary>Interaction Model opcodes.</summary>
public enum InteractionOpcode : byte
{
    /// <summary>Status response.</summary>
    StatusResponse = 0x01,
    /// <summary>Read request.</summary>
    ReadRequest = 0x02,
    /// <summary>Subscribe request.</summary>
    SubscribeRequest = 0x03,
    /// <summary>Subscribe response.</summary>
    SubscribeResponse = 0x04,
    /// <summary>Report data.</summary>
    ReportData = 0x05,
    /// <summary>Write request.</summary>
    WriteRequest = 0x06,
    /// <summary>Write response.</summary>
    WriteResponse = 0x07,
    /// <summary>Invoke request.</summary>
    InvokeRequest = 0x08,
    /// <summary>Invoke response.</summary>
    InvokeResponse = 0x09,
    /// <summary>Timed request.</summary>
    TimedRequest = 0x0A,
}

/// <summary>
/// A Matter message (core spec §4.4): the message header (flags, session id, security flags, counter, optional source
/// and destination node ids) and the protocol header with the application payload. Secured messages carry the protocol
/// header and payload encrypted with AES-CCM; the nonce is the security flags, the counter and the source node id.
/// </summary>
public sealed record MatterMessage
{
    /// <summary>Session id (0 = unsecured session).</summary>
    public ushort SessionId { get; init; }

    /// <summary>Message counter.</summary>
    public uint Counter { get; init; }

    /// <summary>Source node id (present when set).</summary>
    public ulong? SourceNodeId { get; init; }

    /// <summary>Destination node id (present when set).</summary>
    public ulong? DestinationNodeId { get; init; }

    /// <summary>Destination group id (present when set; excludes <see cref="DestinationNodeId"/>).</summary>
    public ushort? DestinationGroupId { get; init; }

    /// <summary>Group session (security flags type 1).</summary>
    public bool IsGroup { get; init; }

    /// <summary>Exchange id.</summary>
    public ushort ExchangeId { get; init; }

    /// <summary>True when sent by the exchange initiator.</summary>
    public bool Initiator { get; init; }

    /// <summary>Asks the receiver to acknowledge (MRP).</summary>
    public bool Reliable { get; init; }

    /// <summary>Message counter this message acknowledges.</summary>
    public uint? AckCounter { get; init; }

    /// <summary>Protocol id.</summary>
    public ushort ProtocolId { get; init; }

    /// <summary>Protocol vendor id (when not 0).</summary>
    public ushort ProtocolVendorId { get; init; }

    /// <summary>Opcode.</summary>
    public byte Opcode { get; init; }

    /// <summary>Application payload (usually TLV).</summary>
    public byte[] Payload { get; init; } = [];

    /// <summary>The message header bytes (also the AAD for encryption).</summary>
    public byte[] EncodeHeader()
    {
        var b = new List<byte>();
        var flags = (byte)((SourceNodeId is not null ? 0x04 : 0) | (DestinationNodeId is not null ? 0x01 : DestinationGroupId is not null ? 0x02 : 0));
        b.Add(flags);
        b.AddRange(BitConverter.GetBytes(SessionId));
        b.Add(SecurityFlags);
        b.AddRange(BitConverter.GetBytes(Counter));
        if (SourceNodeId is { } s) b.AddRange(BitConverter.GetBytes(s));
        if (DestinationNodeId is { } d) b.AddRange(BitConverter.GetBytes(d));
        else if (DestinationGroupId is { } g) b.AddRange(BitConverter.GetBytes(g));
        return [.. b];
    }

    private byte SecurityFlags => (byte)(IsGroup ? 0x01 : 0x00);

    /// <summary>The protocol header and payload (the part that is encrypted).</summary>
    public byte[] EncodeProtocol()
    {
        var b = new List<byte>();
        var exFlags = (byte)((Initiator ? 0x01 : 0) | (AckCounter is not null ? 0x02 : 0) | (Reliable ? 0x04 : 0) | (ProtocolVendorId != 0 ? 0x10 : 0));
        b.Add(exFlags);
        b.Add(Opcode);
        b.AddRange(BitConverter.GetBytes(ExchangeId));
        if (ProtocolVendorId != 0) b.AddRange(BitConverter.GetBytes(ProtocolVendorId));
        b.AddRange(BitConverter.GetBytes(ProtocolId));
        if (AckCounter is { } a) b.AddRange(BitConverter.GetBytes(a));
        b.AddRange(Payload);
        return [.. b];
    }

    /// <summary>Encodes an unsecured message (session 0).</summary>
    public byte[] Encode() => [.. EncodeHeader(), .. EncodeProtocol()];

    /// <summary>The 13-byte AES-CCM nonce: security flags, counter, source node id (0 when absent).</summary>
    public byte[] Nonce(ulong sourceNodeId)
    {
        var n = new byte[13];
        n[0] = SecurityFlags;
        BinaryPrimitives.WriteUInt32LittleEndian(n.AsSpan(1), Counter);
        BinaryPrimitives.WriteUInt64LittleEndian(n.AsSpan(5), sourceNodeId);
        return n;
    }

    /// <summary>Encodes and encrypts with <paramref name="key"/>; the nonce uses <paramref name="nonceNodeId"/> (the sender's node id, 0 for PASE).</summary>
    public byte[] EncodeSecured(byte[] key, ulong nonceNodeId)
    {
        var header = EncodeHeader();
        return [.. header, .. MatterCrypto.Encrypt(key, Nonce(nonceNodeId), EncodeProtocol(), header)];
    }

    /// <summary>Reads the session id of a raw message without decoding it.</summary>
    public static ushort PeekSessionId(ReadOnlySpan<byte> raw) => raw.Length >= 3 ? BinaryPrimitives.ReadUInt16LittleEndian(raw[1..]) : throw new ProtocolException("Message shorter than its header.");

    /// <summary>Decodes the message header; returns the header length.</summary>
    public static (MatterMessage Header, int Length) DecodeHeader(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < 8) throw new ProtocolException("Message shorter than its header.");
        var flags = raw[0];
        if (flags >> 4 != 0) throw new ProtocolException($"Unsupported message version {flags >> 4}.");
        var dsiz = flags & 0x03;
        if (dsiz == 3) throw new ProtocolException("Reserved destination size.");
        var security = raw[3];
        if ((security & 0x80) != 0) throw new ProtocolException("Privacy-obfuscated messages are not supported.");
        if ((security & 0x20) != 0) throw new ProtocolException("Message extensions are not supported.");
        var at = 8;
        ulong? source = null, destination = null;
        ushort? group = null;
        if ((flags & 0x04) != 0)
        {
            if (raw.Length < at + 8) throw new ProtocolException("Source node id truncated.");
            source = BinaryPrimitives.ReadUInt64LittleEndian(raw[at..]);
            at += 8;
        }

        if (dsiz == 1)
        {
            if (raw.Length < at + 8) throw new ProtocolException("Destination node id truncated.");
            destination = BinaryPrimitives.ReadUInt64LittleEndian(raw[at..]);
            at += 8;
        }
        else if (dsiz == 2)
        {
            if (raw.Length < at + 2) throw new ProtocolException("Destination group id truncated.");
            group = BinaryPrimitives.ReadUInt16LittleEndian(raw[at..]);
            at += 2;
        }

        return (new MatterMessage
        {
            SessionId = BinaryPrimitives.ReadUInt16LittleEndian(raw[1..]),
            IsGroup = (security & 0x03) == 1,
            Counter = BinaryPrimitives.ReadUInt32LittleEndian(raw[4..]),
            SourceNodeId = source,
            DestinationNodeId = destination,
            DestinationGroupId = group,
        }, at);
    }

    /// <summary>Decodes the protocol header and payload into a copy of <paramref name="header"/>.</summary>
    public static MatterMessage DecodeProtocol(MatterMessage header, ReadOnlySpan<byte> p)
    {
        if (p.Length < 6) throw new ProtocolException("Protocol header truncated.");
        var exFlags = p[0];
        var at = 4;
        ushort vendor = 0;
        if ((exFlags & 0x10) != 0)
        {
            if (p.Length < at + 2) throw new ProtocolException("Protocol vendor id truncated.");
            vendor = BinaryPrimitives.ReadUInt16LittleEndian(p[at..]);
            at += 2;
        }

        if (p.Length < at + 2) throw new ProtocolException("Protocol id truncated.");
        var protocol = BinaryPrimitives.ReadUInt16LittleEndian(p[at..]);
        at += 2;
        uint? ack = null;
        if ((exFlags & 0x02) != 0)
        {
            if (p.Length < at + 4) throw new ProtocolException("Acknowledged counter truncated.");
            ack = BinaryPrimitives.ReadUInt32LittleEndian(p[at..]);
            at += 4;
        }

        if ((exFlags & 0x08) != 0) throw new ProtocolException("Secured extensions are not supported.");
        return header with
        {
            Initiator = (exFlags & 0x01) != 0,
            Reliable = (exFlags & 0x04) != 0,
            Opcode = p[1],
            ExchangeId = BinaryPrimitives.ReadUInt16LittleEndian(p[2..]),
            ProtocolVendorId = vendor,
            ProtocolId = protocol,
            AckCounter = ack,
            Payload = p[at..].ToArray(),
        };
    }

    /// <summary>Decodes an unsecured message.</summary>
    public static MatterMessage Decode(ReadOnlySpan<byte> raw)
    {
        var (header, length) = DecodeHeader(raw);
        return DecodeProtocol(header, raw[length..]);
    }

    /// <summary>Decrypts and decodes a secured message.</summary>
    /// <exception cref="ProtocolException">The MIC does not verify.</exception>
    public static MatterMessage DecodeSecured(ReadOnlySpan<byte> raw, byte[] key, ulong nonceNodeId)
    {
        var (header, length) = DecodeHeader(raw);
        var plain = MatterCrypto.Decrypt(key, header.Nonce(header.SourceNodeId ?? nonceNodeId), raw[length..].ToArray(), raw[..length].ToArray());
        return DecodeProtocol(header, plain);
    }

    /// <summary>Normalised description shared with the Rust twin.</summary>
    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"session={SessionId} counter={Counter} src={(SourceNodeId is { } s ? s.ToString("X16", CultureInfo.InvariantCulture) : "-")} dst={(DestinationNodeId is { } d ? d.ToString("X16", CultureInfo.InvariantCulture) : DestinationGroupId is { } g ? "g" + g.ToString("X4", CultureInfo.InvariantCulture) : "-")} " +
        $"exchange={ExchangeId} {(Initiator ? "I" : "")}{(Reliable ? "R" : "")}{(AckCounter is not null ? "A" : "")} ack={(AckCounter is { } a ? a.ToString(CultureInfo.InvariantCulture) : "-")} protocol={ProtocolVendorId:X4}:{ProtocolId:X4} opcode=0x{Opcode:X2} payload={Convert.ToHexString(Payload)}");

    /// <summary>Frame anatomy of an unsecured message, or of the header of a secured one.</summary>
    public static IReadOnlyList<FrameField> DescribeFields(ReadOnlySpan<byte> raw)
    {
        var fields = new List<FrameField>();
        try
        {
            var (h, length) = DecodeHeader(raw);
            fields.Add(new FrameField("Flags", 0, 1, FrameFieldKind.Header));
            fields.Add(new FrameField("Session", 1, 2, FrameFieldKind.Address, h.SessionId == 0 ? "unsecured" : h.SessionId.ToString(CultureInfo.InvariantCulture)));
            fields.Add(new FrameField("Security", 3, 1, FrameFieldKind.Header));
            fields.Add(new FrameField("Counter", 4, 4, FrameFieldKind.Length, h.Counter.ToString(CultureInfo.InvariantCulture)));
            if (length > 8) fields.Add(new FrameField("Node ids", 8, length - 8, FrameFieldKind.Address));
            if (h.SessionId != 0)
            {
                fields.Add(new FrameField("Encrypted", length, raw.Length - length - MatterCrypto.MicLength, FrameFieldKind.Data));
                fields.Add(new FrameField("MIC", raw.Length - MatterCrypto.MicLength, MatterCrypto.MicLength, FrameFieldKind.Checksum));
                return fields;
            }

            var m = DecodeProtocol(h, raw[length..]);
            fields.Add(new FrameField("Exchange", length, 4, FrameFieldKind.Function, $"{m.ExchangeId} op 0x{m.Opcode:X2}"));
            fields.Add(new FrameField("Protocol", length + 4, raw.Length - length - 4 - m.Payload.Length, FrameFieldKind.Header, $"0x{m.ProtocolId:X4}"));
            if (m.Payload.Length > 0) fields.Add(new FrameField("Payload", raw.Length - m.Payload.Length, m.Payload.Length, FrameFieldKind.Data));
        }
        catch (ProtocolException ex)
        {
            fields.Add(new FrameField("Invalid", 0, raw.Length, FrameFieldKind.Error, ex.Message));
        }

        return fields;
    }
}

/// <summary>A Secure Channel status report (raw, not TLV): general code, protocol id, protocol code.</summary>
/// <param name="GeneralCode">0 success, 1 failure, 2 bad precondition, 4 busy, 0x10 session not found…</param>
/// <param name="ProtocolId">Protocol (vendor in the high 16 bits).</param>
/// <param name="ProtocolCode">Protocol-specific code (Secure Channel: 0 success, 1 no shared root, 2 invalid parameter, 3 close session, 4 busy).</param>
public readonly record struct StatusReport(ushort GeneralCode, uint ProtocolId, ushort ProtocolCode)
{
    /// <summary>Session establishment success.</summary>
    public static StatusReport Success => new(0, MatterProtocol.SecureChannel, 0);

    /// <summary>Encodes.</summary>
    public byte[] Encode() => [.. BitConverter.GetBytes(GeneralCode), .. BitConverter.GetBytes(ProtocolId), .. BitConverter.GetBytes(ProtocolCode)];

    /// <summary>Decodes.</summary>
    public static StatusReport Decode(ReadOnlySpan<byte> b) => b.Length < 8
        ? throw new ProtocolException("Status report shorter than 8 bytes.")
        : new(BinaryPrimitives.ReadUInt16LittleEndian(b), BinaryPrimitives.ReadUInt32LittleEndian(b[2..]), BinaryPrimitives.ReadUInt16LittleEndian(b[6..]));

    /// <summary>True for success.</summary>
    public bool IsSuccess => GeneralCode == 0;
}
