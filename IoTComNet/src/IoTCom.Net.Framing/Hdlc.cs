using System.Buffers;

namespace IoTCom.Net.Framing;

/// <summary>
/// Asynchronous HDLC-like framing (RFC 1662 style, as used by PPP, many modems and bootloaders):
/// <c>0x7E</c> flags, <c>0x7D</c> escape with XOR <c>0x20</c>, and an FCS-16 (CRC-16/IBM-SDLC) trailer
/// appended little-endian. Address/control fields are left to the caller (they are part of the payload).
/// </summary>
public sealed class Hdlc(bool appendFcs = true, int maxFrameLength = 16 * 1024) : IFrameEncoder, IFrameDecoder
{
    /// <summary>Flag sequence.</summary>
    public const byte Flag = 0x7E;
    /// <summary>Control escape.</summary>
    public const byte Escape = 0x7D;
    /// <summary>Escape XOR mask.</summary>
    public const byte EscapeXor = 0x20;

    /// <summary>Append/verify the FCS-16 trailer.</summary>
    public bool AppendFcs { get; } = appendFcs;

    /// <summary>Maximum decoded frame length (payload + FCS).</summary>
    public int MaxFrameLength { get; } = maxFrameLength;

    /// <inheritdoc />
    public void Encode(ReadOnlySpan<byte> payload, IBufferWriter<byte> output)
    {
        var span = output.GetSpan((payload.Length + 2) * 2 + 2);
        var o = 0;
        span[o++] = Flag;
        foreach (var b in payload) Put(span, ref o, b);
        if (AppendFcs)
        {
            var fcs = (ushort)CrcCatalog.Crc16X25.Compute(payload);
            Put(span, ref o, (byte)fcs);
            Put(span, ref o, (byte)(fcs >> 8));
        }
        span[o++] = Flag;
        output.Advance(o);

        static void Put(Span<byte> s, ref int o, byte b)
        {
            if (b is Flag or Escape || b < 0x20)
            {
                s[o++] = Escape;
                s[o++] = (byte)(b ^ EscapeXor);
            }
            else s[o++] = b;
        }
    }

    /// <inheritdoc />
    public FrameDecodeStatus TryDecode(ref ReadOnlySequence<byte> buffer, IBufferWriter<byte> payload)
    {
        while (true)
        {
            var reader = new SequenceReader<byte>(buffer);
            if (!reader.TryReadTo(out ReadOnlySequence<byte> body, Flag, advancePastDelimiter: true))
            {
                if (buffer.Length > (MaxFrameLength + 2) * 2)
                {
                    buffer = buffer.Slice(buffer.End);
                    return FrameDecodeStatus.Invalid;
                }
                return FrameDecodeStatus.NeedMoreData;
            }
            buffer = buffer.Slice(reader.Position);
            if (body.IsEmpty) continue; // shared/opening flag

            var max = (int)Math.Min(body.Length, MaxFrameLength + 2);
            var tmp = ArrayPool<byte>.Shared.Rent(max);
            try
            {
                var n = 0;
                var escaped = false;
                foreach (var segment in body)
                {
                    foreach (var b in segment.Span)
                    {
                        if (b == Escape) { escaped = true; continue; }
                        if (n >= max) return FrameDecodeStatus.Invalid;
                        tmp[n++] = escaped ? (byte)(b ^ EscapeXor) : b;
                        escaped = false;
                    }
                }
                if (escaped) return FrameDecodeStatus.Invalid;
                if (AppendFcs)
                {
                    if (n < 2) return FrameDecodeStatus.Invalid;
                    var data = tmp.AsSpan(0, n - 2);
                    var fcs = (ushort)(tmp[n - 2] | (tmp[n - 1] << 8));
                    if ((ushort)CrcCatalog.Crc16X25.Compute(data) != fcs) return FrameDecodeStatus.Invalid;
                    payload.Write(data);
                }
                else payload.Write(tmp.AsSpan(0, n));
                return FrameDecodeStatus.Frame;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(tmp);
            }
        }
    }
}

/// <summary>Line-oriented framing for text protocols (NMEA 0183, AT commands, ASTM): splits on CR/LF.</summary>
public sealed class LineFraming(int maxLineLength = 4096) : IFrameEncoder, IFrameDecoder
{
    /// <summary>Maximum accepted line length.</summary>
    public int MaxLineLength { get; } = maxLineLength;

    /// <summary>Line terminator appended by <see cref="Encode"/>.</summary>
    public ReadOnlyMemory<byte> Terminator { get; init; } = "\r\n"u8.ToArray();

    /// <inheritdoc />
    public void Encode(ReadOnlySpan<byte> payload, IBufferWriter<byte> output)
    {
        output.Write(payload);
        output.Write(Terminator.Span);
    }

    /// <inheritdoc />
    public FrameDecodeStatus TryDecode(ref ReadOnlySequence<byte> buffer, IBufferWriter<byte> payload)
    {
        while (true)
        {
            var reader = new SequenceReader<byte>(buffer);
            if (!reader.TryReadTo(out ReadOnlySequence<byte> line, (byte)'\n', advancePastDelimiter: true))
            {
                if (buffer.Length > MaxLineLength)
                {
                    buffer = buffer.Slice(buffer.End);
                    return FrameDecodeStatus.Invalid;
                }
                return FrameDecodeStatus.NeedMoreData;
            }
            buffer = buffer.Slice(reader.Position);
            if (line.Length > 0 && line.Slice(line.Length - 1).FirstSpan[0] == (byte)'\r') line = line.Slice(0, line.Length - 1);
            if (line.IsEmpty) continue;
            if (line.Length > MaxLineLength) return FrameDecodeStatus.Invalid;
            foreach (var seg in line) payload.Write(seg.Span);
            return FrameDecodeStatus.Frame;
        }
    }
}
