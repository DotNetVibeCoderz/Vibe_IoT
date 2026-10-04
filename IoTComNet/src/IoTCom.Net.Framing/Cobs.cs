using System.Buffers;

namespace IoTCom.Net.Framing;

/// <summary>
/// Consistent Overhead Byte Stuffing. Encoded data contains no zero bytes, so <c>0x00</c> delimits frames.
/// Overhead is at most one byte per 254 bytes of payload.
/// </summary>
public sealed class Cobs(int maxFrameLength = 64 * 1024) : IFrameEncoder, IFrameDecoder
{
    /// <summary>Frame delimiter.</summary>
    public const byte Delimiter = 0x00;

    /// <summary>Maximum decoded frame length.</summary>
    public int MaxFrameLength { get; } = maxFrameLength;

    /// <summary>Worst-case encoded length (without delimiter).</summary>
    public static int MaxEncodedLength(int payloadLength) => payloadLength + payloadLength / 254 + 1;

    /// <summary>COBS-encodes <paramref name="payload"/> (no trailing delimiter). Returns bytes written.</summary>
    public static int Encode(ReadOnlySpan<byte> payload, Span<byte> destination)
    {
        var codeIndex = 0;
        var o = 1;
        byte code = 1;
        for (var i = 0; i < payload.Length; i++)
        {
            var b = payload[i];
            if (b == 0)
            {
                destination[codeIndex] = code;
                code = 1;
                codeIndex = o++;
            }
            else
            {
                destination[o++] = b;
                if (++code == 0xFF)
                {
                    destination[codeIndex] = code;
                    code = 1;
                    if (i == payload.Length - 1) return o; // a full block that ends the payload needs no extra code byte
                    codeIndex = o++;
                }
            }
        }
        destination[codeIndex] = code;
        return o;
    }

    /// <summary>Decodes a COBS block (without delimiter). Returns bytes written or -1 when malformed.</summary>
    public static int Decode(ReadOnlySpan<byte> encoded, Span<byte> destination)
    {
        var i = 0;
        var o = 0;
        while (i < encoded.Length)
        {
            var code = encoded[i++];
            if (code == 0) return -1;
            var n = code - 1;
            if (i + n > encoded.Length) return -1;
            for (var k = 0; k < n; k++)
            {
                var b = encoded[i++];
                if (b == 0) return -1;
                destination[o++] = b;
            }
            if (code != 0xFF && i < encoded.Length) destination[o++] = 0;
        }
        return o;
    }

    /// <inheritdoc />
    public void Encode(ReadOnlySpan<byte> payload, IBufferWriter<byte> output)
    {
        var span = output.GetSpan(MaxEncodedLength(payload.Length) + 1);
        var n = Encode(payload, span);
        span[n++] = Delimiter;
        output.Advance(n);
    }

    /// <inheritdoc />
    public FrameDecodeStatus TryDecode(ref ReadOnlySequence<byte> buffer, IBufferWriter<byte> payload)
    {
        while (true)
        {
            var reader = new SequenceReader<byte>(buffer);
            if (!reader.TryReadTo(out ReadOnlySequence<byte> body, Delimiter, advancePastDelimiter: true))
            {
                if (buffer.Length > MaxEncodedLength(MaxFrameLength))
                {
                    buffer = buffer.Slice(buffer.End);
                    return FrameDecodeStatus.Invalid;
                }
                return FrameDecodeStatus.NeedMoreData;
            }
            buffer = buffer.Slice(reader.Position);
            if (body.IsEmpty) continue;
            if (body.Length > MaxEncodedLength(MaxFrameLength)) return FrameDecodeStatus.Invalid;

            var len = (int)body.Length;
            byte[]? rented = null;
            var src = body.IsSingleSegment ? body.FirstSpan : (rented = ArrayPool<byte>.Shared.Rent(len)).AsSpan(0, len);
            try
            {
                if (rented is not null) body.CopyTo(rented);
                var dst = payload.GetSpan(len);
                var n = Decode(src, dst);
                if (n < 0) return FrameDecodeStatus.Invalid;
                payload.Advance(n);
                return FrameDecodeStatus.Frame;
            }
            finally
            {
                if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }
}
