using System.Buffers;

namespace IoTCom.Net.Framing;

/// <summary>SLIP (RFC 1055) byte stuffing. <c>END=0xC0</c>, <c>ESC=0xDB</c>.</summary>
public sealed class Slip : IFrameEncoder, IFrameDecoder
{
    /// <summary>Frame delimiter.</summary>
    public const byte End = 0xC0;
    /// <summary>Escape byte.</summary>
    public const byte Esc = 0xDB;
    /// <summary>Escaped END.</summary>
    public const byte EscEnd = 0xDC;
    /// <summary>Escaped ESC.</summary>
    public const byte EscEsc = 0xDD;

    /// <summary>Creates a SLIP codec.</summary>
    /// <param name="leadingEnd">Emit an END before each frame to flush line noise (recommended by RFC 1055).</param>
    /// <param name="maxFrameLength">Frames whose decoded size exceeds this are dropped.</param>
    public Slip(bool leadingEnd = true, int maxFrameLength = 64 * 1024)
    {
        LeadingEnd = leadingEnd;
        MaxFrameLength = maxFrameLength;
    }

    /// <summary>Emit an END before each frame.</summary>
    public bool LeadingEnd { get; }

    /// <summary>Maximum decoded frame length.</summary>
    public int MaxFrameLength { get; }

    /// <summary>Worst-case encoded length for <paramref name="payloadLength"/> bytes.</summary>
    public static int MaxEncodedLength(int payloadLength) => payloadLength * 2 + 2;

    /// <summary>Encodes into <paramref name="destination"/>; returns the number of bytes written.</summary>
    public static int Encode(ReadOnlySpan<byte> payload, Span<byte> destination, bool leadingEnd = true)
    {
        var o = 0;
        if (leadingEnd) destination[o++] = End;
        foreach (var b in payload)
        {
            switch (b)
            {
                case End: destination[o++] = Esc; destination[o++] = EscEnd; break;
                case Esc: destination[o++] = Esc; destination[o++] = EscEsc; break;
                default: destination[o++] = b; break;
            }
        }
        destination[o++] = End;
        return o;
    }

    /// <summary>Decodes one complete frame body (without END delimiters). Returns -1 on an invalid escape.</summary>
    public static int Decode(ReadOnlySpan<byte> encoded, Span<byte> destination)
    {
        var o = 0;
        for (var i = 0; i < encoded.Length; i++)
        {
            var b = encoded[i];
            if (b == End) continue;
            if (b == Esc)
            {
                if (++i >= encoded.Length) return -1;
                switch (encoded[i])
                {
                    case EscEnd: b = End; break;
                    case EscEsc: b = Esc; break;
                    default: return -1;
                }
            }
            destination[o++] = b;
        }
        return o;
    }

    /// <inheritdoc />
    public void Encode(ReadOnlySpan<byte> payload, IBufferWriter<byte> output)
    {
        var span = output.GetSpan(MaxEncodedLength(payload.Length));
        output.Advance(Encode(payload, span, LeadingEnd));
    }

    /// <inheritdoc />
    public FrameDecodeStatus TryDecode(ref ReadOnlySequence<byte> buffer, IBufferWriter<byte> payload)
    {
        while (true)
        {
            var reader = new SequenceReader<byte>(buffer);
            if (!reader.TryReadTo(out ReadOnlySequence<byte> body, End, advancePastDelimiter: true))
            {
                if (buffer.Length > MaxEncodedLength(MaxFrameLength))
                {
                    buffer = buffer.Slice(buffer.End); // runaway garbage without delimiter
                    return FrameDecodeStatus.Invalid;
                }
                return FrameDecodeStatus.NeedMoreData;
            }
            buffer = buffer.Slice(reader.Position);
            if (body.IsEmpty) continue; // back-to-back END bytes

            if (body.Length > MaxEncodedLength(MaxFrameLength)) return FrameDecodeStatus.Invalid;
            var escaped = false;
            var written = 0;
            foreach (var segment in body)
            {
                var src = segment.Span;
                var dst = payload.GetSpan(src.Length);
                var o = 0;
                foreach (var b in src)
                {
                    if (escaped)
                    {
                        escaped = false;
                        if (b == EscEnd) dst[o++] = End;
                        else if (b == EscEsc) dst[o++] = Esc;
                        else { payload.Advance(o); return FrameDecodeStatus.Invalid; }
                    }
                    else if (b == Esc) escaped = true;
                    else dst[o++] = b;
                }
                payload.Advance(o);
                written += o;
            }
            if (escaped || written > MaxFrameLength) return FrameDecodeStatus.Invalid;
            return FrameDecodeStatus.Frame;
        }
    }
}
