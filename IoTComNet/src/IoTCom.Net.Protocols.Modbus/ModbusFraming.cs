using System.Buffers;
using System.Buffers.Binary;
using IoTCom.Net.Framing;

namespace IoTCom.Net.Protocols.Modbus;

/// <summary>A decoded application data unit: addressing + PDU + the raw wire bytes.</summary>
/// <param name="TransactionId">MBAP transaction id (0 for RTU/ASCII).</param>
/// <param name="UnitId">Unit / slave id.</param>
/// <param name="Pdu">Function code + data.</param>
/// <param name="Raw">Complete frame as seen on the wire (for the traffic tap).</param>
public readonly record struct ModbusAdu(ushort TransactionId, byte UnitId, byte[] Pdu, byte[] Raw);

/// <summary>Encodes and decodes Modbus ADUs for one framing variant. Stateless and thread-safe.</summary>
public abstract class ModbusFraming
{
    /// <summary>Modbus TCP (MBAP).</summary>
    public static ModbusFraming Tcp { get; } = new ModbusTcpFraming();
    /// <summary>Modbus RTU (CRC-16).</summary>
    public static ModbusFraming Rtu { get; } = new ModbusRtuFraming();
    /// <summary>Modbus ASCII (LRC).</summary>
    public static ModbusFraming Ascii { get; } = new ModbusAsciiFraming();

    /// <summary>Returns the framing for <paramref name="mode"/>.</summary>
    public static ModbusFraming For(ModbusFramingMode mode) => mode switch
    {
        ModbusFramingMode.Tcp => Tcp,
        ModbusFramingMode.Rtu => Rtu,
        ModbusFramingMode.Ascii => Ascii,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    /// <summary>Framing mode.</summary>
    public abstract ModbusFramingMode Mode { get; }

    /// <summary>Protocol name used in logs and taps.</summary>
    public string ProtocolName => Mode switch { ModbusFramingMode.Tcp => "modbus-tcp", ModbusFramingMode.Rtu => "modbus-rtu", _ => "modbus-ascii" };

    /// <summary>True when frames carry transaction ids (allows pipelining).</summary>
    public bool HasTransactionIds => Mode == ModbusFramingMode.Tcp;

    /// <summary>Writes a complete ADU.</summary>
    public abstract void Write(IBufferWriter<byte> output, ushort transactionId, byte unitId, ReadOnlySpan<byte> pdu);

    /// <summary>Encodes an ADU into a new array.</summary>
    public byte[] Encode(ushort transactionId, byte unitId, ReadOnlySpan<byte> pdu)
    {
        var w = new ArrayBufferWriter<byte>(pdu.Length + 16);
        Write(w, transactionId, unitId, pdu);
        return w.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Tries to read one ADU. <paramref name="expectRequest"/> selects request or response length rules (needed by RTU,
    /// whose frames carry no length field). On <see cref="FrameDecodeStatus.Invalid"/> the buffer is advanced to resynchronise.
    /// </summary>
    public abstract FrameDecodeStatus TryRead(ref ReadOnlySequence<byte> buffer, bool expectRequest, out ModbusAdu adu);

    /// <summary>Decodes a single complete frame from a byte array (CLI / inspector helper).</summary>
    public bool TryDecode(ReadOnlySpan<byte> frame, bool expectRequest, out ModbusAdu adu)
    {
        var seq = new ReadOnlySequence<byte>(frame.ToArray());
        return TryRead(ref seq, expectRequest, out adu) == FrameDecodeStatus.Frame;
    }
}

internal sealed class ModbusTcpFraming : ModbusFraming
{
    public override ModbusFramingMode Mode => ModbusFramingMode.Tcp;

    public override void Write(IBufferWriter<byte> output, ushort transactionId, byte unitId, ReadOnlySpan<byte> pdu)
    {
        var span = output.GetSpan(7 + pdu.Length);
        BinaryPrimitives.WriteUInt16BigEndian(span, transactionId);
        BinaryPrimitives.WriteUInt16BigEndian(span[2..], 0); // protocol id
        BinaryPrimitives.WriteUInt16BigEndian(span[4..], (ushort)(pdu.Length + 1));
        span[6] = unitId;
        pdu.CopyTo(span[7..]);
        output.Advance(7 + pdu.Length);
    }

    public override FrameDecodeStatus TryRead(ref ReadOnlySequence<byte> buffer, bool expectRequest, out ModbusAdu adu)
    {
        adu = default;
        if (buffer.Length < 7) return FrameDecodeStatus.NeedMoreData;
        Span<byte> header = stackalloc byte[7];
        buffer.Slice(0, 7).CopyTo(header);
        var tid = BinaryPrimitives.ReadUInt16BigEndian(header);
        var pid = BinaryPrimitives.ReadUInt16BigEndian(header[2..]);
        var len = BinaryPrimitives.ReadUInt16BigEndian(header[4..]);
        if (pid != 0 || len < 2 || len > ModbusLimits.MaxPduLength + 1)
        {
            buffer = buffer.Slice(1); // garbage: slide by one byte to resync
            return FrameDecodeStatus.Invalid;
        }
        var total = 6 + len;
        if (buffer.Length < total) return FrameDecodeStatus.NeedMoreData;
        var raw = buffer.Slice(0, total).ToArray();
        buffer = buffer.Slice(total);
        adu = new ModbusAdu(tid, raw[6], raw[7..], raw);
        return FrameDecodeStatus.Frame;
    }
}

internal sealed class ModbusRtuFraming : ModbusFraming
{
    private const int NeedMore = -1;
    private const int Unknown = -2;

    public override ModbusFramingMode Mode => ModbusFramingMode.Rtu;

    public override void Write(IBufferWriter<byte> output, ushort transactionId, byte unitId, ReadOnlySpan<byte> pdu)
    {
        var span = output.GetSpan(3 + pdu.Length);
        span[0] = unitId;
        pdu.CopyTo(span[1..]);
        var crc = Crc16.Modbus(span[..(1 + pdu.Length)]);
        span[1 + pdu.Length] = (byte)crc;
        span[2 + pdu.Length] = (byte)(crc >> 8);
        output.Advance(3 + pdu.Length);
    }

    public override FrameDecodeStatus TryRead(ref ReadOnlySequence<byte> buffer, bool expectRequest, out ModbusAdu adu)
    {
        adu = default;
        if (buffer.Length < 4) return FrameDecodeStatus.NeedMoreData;
        var peekLen = (int)Math.Min(buffer.Length, 260);
        Span<byte> head = stackalloc byte[peekLen];
        buffer.Slice(0, peekLen).CopyTo(head);

        var len = expectRequest ? RequestLength(head) : ResponseLength(head);
        if (len == NeedMore) return FrameDecodeStatus.NeedMoreData;
        if (len == Unknown || len > 256)
        {
            buffer = buffer.Slice(1);
            return FrameDecodeStatus.Invalid;
        }
        if (buffer.Length < len) return FrameDecodeStatus.NeedMoreData;

        var frame = head[..len];
        var crc = (ushort)(frame[len - 2] | (frame[len - 1] << 8));
        if (Crc16.Modbus(frame[..(len - 2)]) != crc)
        {
            buffer = buffer.Slice(1); // CRC mismatch: resync one byte at a time
            return FrameDecodeStatus.Invalid;
        }
        var raw = frame.ToArray();
        buffer = buffer.Slice(len);
        adu = new ModbusAdu(0, raw[0], raw[1..(len - 2)], raw);
        return FrameDecodeStatus.Frame;
    }

    /// <summary>Total RTU response length (unit .. CRC) or NeedMore/Unknown.</summary>
    internal static int ResponseLength(ReadOnlySpan<byte> f)
    {
        if (f.Length < 3) return NeedMore;
        var fc = f[1];
        if ((fc & 0x80) != 0) return 5;
        switch (fc)
        {
            case 0x01 or 0x02 or 0x03 or 0x04 or 0x17: return 3 + f[2] + 2;
            case 0x05 or 0x06 or 0x0F or 0x10: return 8;
            case 0x16: return 10;
            case 0x2B:
                // unit fc mei code conformity more next count {id len value}* crc
                if (f.Length < 8) return NeedMore;
                var count = f[7];
                var p = 8;
                for (var i = 0; i < count; i++)
                {
                    if (f.Length < p + 2) return NeedMore;
                    p += 2 + f[p + 1];
                }
                return p + 2;
            default: return Unknown;
        }
    }

    /// <summary>Total RTU request length (unit .. CRC) or NeedMore/Unknown.</summary>
    internal static int RequestLength(ReadOnlySpan<byte> f)
    {
        if (f.Length < 2) return NeedMore;
        switch (f[1])
        {
            case 0x01 or 0x02 or 0x03 or 0x04 or 0x05 or 0x06: return 8;
            case 0x0F or 0x10: return f.Length < 7 ? NeedMore : 7 + f[6] + 2;
            case 0x16: return 10;
            case 0x17: return f.Length < 11 ? NeedMore : 11 + f[10] + 2;
            case 0x2B: return 7;
            default: return Unknown;
        }
    }
}

internal sealed class ModbusAsciiFraming : ModbusFraming
{
    public override ModbusFramingMode Mode => ModbusFramingMode.Ascii;

    public override void Write(IBufferWriter<byte> output, ushort transactionId, byte unitId, ReadOnlySpan<byte> pdu)
    {
        var binLen = pdu.Length + 2;
        var span = output.GetSpan(1 + binLen * 2 + 2);
        span[0] = (byte)':';
        var o = 1;
        Hex(span, ref o, unitId);
        foreach (var b in pdu) Hex(span, ref o, b);
        byte sum = unitId;
        foreach (var b in pdu) sum += b;
        Hex(span, ref o, (byte)-sum);
        span[o++] = (byte)'\r';
        span[o++] = (byte)'\n';
        output.Advance(o);

        static void Hex(Span<byte> s, ref int o, byte b)
        {
            const string digits = "0123456789ABCDEF";
            s[o++] = (byte)digits[b >> 4];
            s[o++] = (byte)digits[b & 0xF];
        }
    }

    public override FrameDecodeStatus TryRead(ref ReadOnlySequence<byte> buffer, bool expectRequest, out ModbusAdu adu)
    {
        adu = default;
        var reader = new SequenceReader<byte>(buffer);
        if (!reader.TryAdvanceTo((byte)':', advancePastDelimiter: false))
        {
            buffer = buffer.Slice(buffer.End);
            return FrameDecodeStatus.NeedMoreData;
        }
        buffer = buffer.Slice(reader.Position);
        reader = new SequenceReader<byte>(buffer);
        if (!reader.TryReadTo(out ReadOnlySequence<byte> line, (byte)'\n', advancePastDelimiter: true))
        {
            if (buffer.Length > 1 + 256 * 2 + 2)
            {
                buffer = buffer.Slice(1);
                return FrameDecodeStatus.Invalid;
            }
            return FrameDecodeStatus.NeedMoreData;
        }
        var raw = buffer.Slice(0, reader.Position).ToArray();
        buffer = buffer.Slice(reader.Position);
        var text = line.ToArray().AsSpan(1); // skip ':'
        if (text.Length > 0 && text[^1] == '\r') text = text[..^1];
        if (text.Length < 6 || text.Length % 2 != 0) return FrameDecodeStatus.Invalid;
        var bin = new byte[text.Length / 2];
        for (var i = 0; i < bin.Length; i++)
        {
            var hi = FromHex(text[i * 2]);
            var lo = FromHex(text[i * 2 + 1]);
            if (hi < 0 || lo < 0) return FrameDecodeStatus.Invalid;
            bin[i] = (byte)(hi << 4 | lo);
        }
        if (Lrc.Compute(bin.AsSpan(0, bin.Length - 1)) != bin[^1]) return FrameDecodeStatus.Invalid;
        adu = new ModbusAdu(0, bin[0], bin[1..^1], raw);
        return FrameDecodeStatus.Frame;

        static int FromHex(byte c) => c switch
        {
            >= (byte)'0' and <= (byte)'9' => c - '0',
            >= (byte)'A' and <= (byte)'F' => c - 'A' + 10,
            >= (byte)'a' and <= (byte)'f' => c - 'a' + 10,
            _ => -1,
        };
    }
}
