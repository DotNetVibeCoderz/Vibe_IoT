using System.Globalization;
using IoTCom.Net.Framing;

namespace IoTCom.Net.Protocols.Dlms;

/// <summary>
/// An HDLC address (IEC 62056-46): one byte for a client SAP; one, two or four bytes for a server (upper = logical
/// device, lower = physical device). Each byte carries 7 bits; the last byte has its low bit set.
/// </summary>
/// <param name="Upper">Client SAP, or the server's upper (logical) address.</param>
/// <param name="Lower">The server's lower (physical) address, if any.</param>
/// <param name="Size">Encoded size: 1, 2 or 4 bytes (0 picks the smallest that fits).</param>
public readonly record struct HdlcAddress(ushort Upper, ushort? Lower = null, int Size = 0)
{
    /// <summary>Encoded size in bytes.</summary>
    public int EncodedSize => Size != 0 ? Size : Lower is null ? (Upper < 0x80 ? 1 : 4) : (Upper < 0x80 && Lower < 0x80 ? 2 : 4);

    /// <summary>Writes the address.</summary>
    public void Write(List<byte> buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        switch (EncodedSize)
        {
            case 1:
                buffer.Add((byte)((Upper << 1) | 1));
                break;
            case 2:
                buffer.Add((byte)(Upper << 1));
                buffer.Add((byte)(((Lower ?? 0) << 1) | 1));
                break;
            default:
                var lower = Lower ?? 0;
                buffer.Add((byte)((Upper >> 7) << 1));
                buffer.Add((byte)((Upper & 0x7F) << 1));
                buffer.Add((byte)((lower >> 7) << 1));
                buffer.Add((byte)(((lower & 0x7F) << 1) | 1));
                break;
        }
    }

    /// <summary>Reads an address at <paramref name="pos"/> (server addresses may be 1, 2 or 4 bytes).</summary>
    public static HdlcAddress Read(ReadOnlySpan<byte> data, ref int pos)
    {
        var start = pos;
        while (pos < data.Length && (data[pos] & 1) == 0) pos++;
        if (pos >= data.Length) throw new FormatException("HDLC address has no terminating byte.");
        pos++;
        var b = data[start..pos];
        return b.Length switch
        {
            1 => new HdlcAddress((ushort)(b[0] >> 1), null, 1),
            2 => new HdlcAddress((ushort)(b[0] >> 1), (ushort)(b[1] >> 1), 2),
            4 => new HdlcAddress((ushort)(((b[0] >> 1) << 7) | (b[1] >> 1)), (ushort)(((b[2] >> 1) << 7) | (b[3] >> 1)), 4),
            _ => throw new FormatException($"HDLC address of {b.Length} bytes (1, 2 or 4 expected)."),
        };
    }

    /// <inheritdoc />
    public override string ToString() => Lower is { } l ? $"{Upper}/{l}" : Upper.ToString(CultureInfo.InvariantCulture);
}

/// <summary>HDLC control field values and helpers.</summary>
public static class HdlcControl
{
    /// <summary>Set normal response mode (with P).</summary>
    public const byte Snrm = 0x93;
    /// <summary>Disconnect (with P).</summary>
    public const byte Disc = 0x53;
    /// <summary>Unnumbered acknowledge (with F).</summary>
    public const byte Ua = 0x73;
    /// <summary>Disconnected mode (with F).</summary>
    public const byte Dm = 0x1F;
    /// <summary>Frame reject (with F).</summary>
    public const byte Frmr = 0x97;
    /// <summary>Unnumbered information (with P).</summary>
    public const byte Ui = 0x13;

    /// <summary>An information frame.</summary>
    public static byte I(int sendSequence, int receiveSequence, bool poll = true) =>
        (byte)(((receiveSequence & 7) << 5) | (poll ? 0x10 : 0) | ((sendSequence & 7) << 1));

    /// <summary>Receive ready.</summary>
    public static byte Rr(int receiveSequence, bool poll = true) => (byte)(((receiveSequence & 7) << 5) | (poll ? 0x10 : 0) | 0x01);

    /// <summary>Receive not ready.</summary>
    public static byte Rnr(int receiveSequence, bool poll = true) => (byte)(((receiveSequence & 7) << 5) | (poll ? 0x10 : 0) | 0x05);

    /// <summary>An I-frame.</summary>
    public static bool IsI(byte control) => (control & 0x01) == 0;

    /// <summary>A receive-ready supervisory frame.</summary>
    public static bool IsRr(byte control) => (control & 0x0F) == 0x01;

    /// <summary>N(S) of an I-frame.</summary>
    public static int SendSequence(byte control) => (control >> 1) & 7;

    /// <summary>N(R) of an I or S frame.</summary>
    public static int ReceiveSequence(byte control) => (control >> 5) & 7;

    /// <summary>Human-readable name, e.g. <c>I(S=1,R=0)</c>.</summary>
    public static string Describe(byte control)
    {
        if (IsI(control)) return $"I(S={SendSequence(control)},R={ReceiveSequence(control)})";
        if ((control & 0x03) == 0x01)
            return (control & 0x0F) switch
            {
                0x01 => $"RR(R={ReceiveSequence(control)})",
                0x05 => $"RNR(R={ReceiveSequence(control)})",
                _ => $"S 0x{control:X2}",
            };
        return (control & 0xEF) switch
        {
            0x83 => "SNRM",
            0x43 => "DISC",
            0x63 => "UA",
            0x0F => "DM",
            0x87 => "FRMR",
            0x03 => "UI",
            _ => $"U 0x{control:X2}",
        };
    }
}

/// <summary>Negotiated HDLC parameters (carried in SNRM and UA).</summary>
/// <param name="MaxInfoTransmit">Maximum information field length the sender transmits.</param>
/// <param name="MaxInfoReceive">Maximum information field length the sender accepts.</param>
/// <param name="WindowTransmit">Transmit window.</param>
/// <param name="WindowReceive">Receive window.</param>
public sealed record HdlcParameters(int MaxInfoTransmit = 128, int MaxInfoReceive = 128, int WindowTransmit = 1, int WindowReceive = 1)
{
    /// <summary>Encodes the parameter negotiation field (81 80 …).</summary>
    public byte[] Encode()
    {
        var p = new List<byte> { 0x81, 0x80, 0 };
        void Add(byte id, long value, int size)
        {
            p.Add(id);
            p.Add((byte)size);
            for (var i = size - 1; i >= 0; i--) p.Add((byte)(value >> (8 * i)));
        }

        Add(0x05, MaxInfoTransmit, MaxInfoTransmit > 0xFF ? 2 : 1);
        Add(0x06, MaxInfoReceive, MaxInfoReceive > 0xFF ? 2 : 1);
        Add(0x07, WindowTransmit, 4);
        Add(0x08, WindowReceive, 4);
        p[2] = (byte)(p.Count - 3);
        return [.. p];
    }

    /// <summary>Parses a negotiation field; missing parameters keep their defaults.</summary>
    public static HdlcParameters Parse(ReadOnlySpan<byte> info)
    {
        var result = new HdlcParameters();
        if (info.Length < 3 || info[0] != 0x81 || info[1] != 0x80) return result;
        var end = Math.Min(info.Length, 3 + info[2]);
        var pos = 3;
        while (pos + 2 <= end)
        {
            var id = info[pos];
            var len = info[pos + 1];
            if (pos + 2 + len > end || len > 4) break;
            long v = 0;
            for (var i = 0; i < len; i++) v = (v << 8) | info[pos + 2 + i];
            result = id switch
            {
                0x05 => result with { MaxInfoTransmit = (int)v },
                0x06 => result with { MaxInfoReceive = (int)v },
                0x07 => result with { WindowTransmit = (int)v },
                0x08 => result with { WindowReceive = (int)v },
                _ => result,
            };
            pos += 2 + len;
        }

        return result;
    }
}

/// <summary>
/// An HDLC frame of format type 3 (IEC 62056-46): flag, format (segmentation bit, 11-bit length), destination and
/// source addresses, control, HCS, information, FCS, flag. The checksums are CRC-16/X-25.
/// </summary>
/// <param name="Destination">Destination address.</param>
/// <param name="Source">Source address.</param>
/// <param name="Control">Control field.</param>
/// <param name="Information">Information field (LLC header + APDU segment, or negotiation parameters).</param>
/// <param name="Segmented">More segments follow (format bit S).</param>
public sealed record HdlcFrame(HdlcAddress Destination, HdlcAddress Source, byte Control, byte[] Information, bool Segmented = false)
{
    /// <summary>The frame flag.</summary>
    public const byte Flag = 0x7E;

    /// <summary>LLC header of a request (client → server).</summary>
    public static ReadOnlySpan<byte> LlcRequest => [0xE6, 0xE6, 0x00];

    /// <summary>LLC header of a response (server → client).</summary>
    public static ReadOnlySpan<byte> LlcResponse => [0xE6, 0xE7, 0x00];

    /// <summary>Encodes the frame including both flags.</summary>
    public byte[] Encode()
    {
        var body = new List<byte>(Information.Length + 16) { 0, 0 };
        Destination.Write(body);
        Source.Write(body);
        body.Add(Control);
        var headerEnd = body.Count;
        var length = headerEnd + (Information.Length > 0 ? 2 + Information.Length : 0) + 2;
        if (length > 0x7FF) throw new InvalidOperationException($"HDLC frame of {length} bytes exceeds the 11-bit length.");
        body[0] = (byte)(0xA0 | (Segmented ? 0x08 : 0) | (length >> 8));
        body[1] = (byte)length;
        if (Information.Length > 0)
        {
            var hcs = (ushort)CrcCatalog.Crc16X25.Compute(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(body));
            body.Add((byte)hcs);
            body.Add((byte)(hcs >> 8));
            body.AddRange(Information);
        }

        var fcs = (ushort)CrcCatalog.Crc16X25.Compute(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(body));
        body.Add((byte)fcs);
        body.Add((byte)(fcs >> 8));
        return [Flag, .. body, Flag];
    }

    /// <summary>Outcome of <see cref="TryRead"/>.</summary>
    public enum ReadStatus
    {
        /// <summary>A frame was decoded.</summary>
        Frame,
        /// <summary>More bytes are needed.</summary>
        NeedMore,
        /// <summary>Bytes were skipped (noise, bad checksum); call again.</summary>
        Skipped,
    }

    /// <summary>
    /// Reads the next frame from <paramref name="data"/>. <paramref name="consumed"/> bytes can be discarded
    /// (also for <see cref="ReadStatus.Skipped"/>, with the reason in <paramref name="error"/>).
    /// </summary>
    public static ReadStatus TryRead(ReadOnlySpan<byte> data, out HdlcFrame? frame, out int consumed, out string? error)
    {
        frame = null;
        error = null;
        consumed = 0;
        var start = data.IndexOf(Flag);
        if (start < 0)
        {
            consumed = data.Length;
            return data.Length == 0 ? ReadStatus.NeedMore : ReadStatus.Skipped;
        }

        // Skip extra flags (opening flag shared with the previous frame's closing flag is allowed).
        while (start + 1 < data.Length && data[start + 1] == Flag) start++;
        if (start + 3 > data.Length)
        {
            consumed = start;
            return start > 0 ? ReadStatus.Skipped : ReadStatus.NeedMore;
        }

        if ((data[start + 1] & 0xF0) != 0xA0)
        {
            consumed = start + 1;
            error = $"Not an HDLC type-3 format byte: 0x{data[start + 1]:X2}.";
            return ReadStatus.Skipped;
        }

        var length = ((data[start + 1] & 0x07) << 8) | data[start + 2];
        if (start + 1 + length + 1 > data.Length)
        {
            consumed = start;
            return start > 0 ? ReadStatus.Skipped : ReadStatus.NeedMore;
        }

        var body = data.Slice(start + 1, length);
        if (data[start + 1 + length] != Flag)
        {
            consumed = start + 1;
            error = "HDLC frame not closed by a flag at the announced length.";
            return ReadStatus.Skipped;
        }

        consumed = start + 1 + length + 1;
        try
        {
            frame = Parse(body);
            return ReadStatus.Frame;
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return ReadStatus.Skipped;
        }
    }

    /// <summary>Decodes a frame body (format field through FCS, without flags).</summary>
    public static HdlcFrame Parse(ReadOnlySpan<byte> body)
    {
        if (body.Length < 7) throw new FormatException("HDLC frame too short.");
        var fcs = (ushort)CrcCatalog.Crc16X25.Compute(body[..^2]);
        if (body[^2] != (byte)fcs || body[^1] != (byte)(fcs >> 8)) throw new FormatException("HDLC FCS mismatch.");
        var pos = 2;
        var dest = HdlcAddress.Read(body, ref pos);
        var src = HdlcAddress.Read(body, ref pos);
        if (pos >= body.Length - 2) throw new FormatException("HDLC frame has no control field.");
        var control = body[pos++];
        byte[] info = [];
        if (body.Length - 2 > pos)
        {
            if (pos + 2 > body.Length - 2) throw new FormatException("HDLC frame truncated in the HCS.");
            var hcs = (ushort)CrcCatalog.Crc16X25.Compute(body[..pos]);
            if (body[pos] != (byte)hcs || body[pos + 1] != (byte)(hcs >> 8)) throw new FormatException("HDLC HCS mismatch.");
            pos += 2;
            info = body[pos..^2].ToArray();
        }

        return new HdlcFrame(dest, src, control, info, (body[0] & 0x08) != 0);
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"HDLC {HdlcControl.Describe(Control)} {Source}→{Destination}{(Segmented ? " segmented" : "")}{(Information.Length > 0 ? $" {Information.Length} B" : "")}";
}

/// <summary>The DLMS/COSEM TCP-UDP wrapper (IEC 62056-47): version 1, source and destination wPort, length, APDU.</summary>
public static class DlmsWrapper
{
    /// <summary>Default TCP port for DLMS/COSEM.</summary>
    public const int Port = 4059;

    /// <summary>Encodes a wrapper PDU.</summary>
    public static byte[] Encode(ushort source, ushort destination, ReadOnlySpan<byte> apdu)
    {
        if (apdu.Length > ushort.MaxValue) throw new ArgumentException("APDU too long for the wrapper.", nameof(apdu));
        var b = new byte[8 + apdu.Length];
        b[1] = 1;
        b[2] = (byte)(source >> 8);
        b[3] = (byte)source;
        b[4] = (byte)(destination >> 8);
        b[5] = (byte)destination;
        b[6] = (byte)(apdu.Length >> 8);
        b[7] = (byte)apdu.Length;
        apdu.CopyTo(b.AsSpan(8));
        return b;
    }

    /// <summary>Reads one wrapper PDU; returns false when more bytes are needed. Throws on a wrong version.</summary>
    public static bool TryRead(ReadOnlySpan<byte> data, out ushort source, out ushort destination, out byte[] apdu, out int consumed)
    {
        (source, destination, apdu, consumed) = (0, 0, [], 0);
        if (data.Length < 8) return false;
        if (data[0] != 0 || data[1] != 1) throw new FormatException($"DLMS wrapper version {data[0] << 8 | data[1]} (1 expected).");
        var length = (data[6] << 8) | data[7];
        if (data.Length < 8 + length) return false;
        source = (ushort)((data[2] << 8) | data[3]);
        destination = (ushort)((data[4] << 8) | data[5]);
        apdu = data.Slice(8, length).ToArray();
        consumed = 8 + length;
        return true;
    }
}
