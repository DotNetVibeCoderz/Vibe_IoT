using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;

namespace IoTCom.Net.Framing;

/// <summary>Result of a single decode attempt.</summary>
public enum FrameDecodeStatus
{
    /// <summary>More bytes are needed.</summary>
    NeedMoreData = 0,
    /// <summary>A complete, valid frame was written to the payload writer.</summary>
    Frame = 1,
    /// <summary>A complete but invalid frame (bad escape, bad checksum, too long) was dropped.</summary>
    Invalid = 2,
}

/// <summary>
/// Streaming frame decoder. Works directly on <see cref="ReadOnlySequence{T}"/> from a <see cref="PipeReader"/>,
/// so frames split across reads are handled without copying the input.
/// </summary>
public interface IFrameDecoder
{
    /// <summary>
    /// Tries to extract one frame from <paramref name="buffer"/>. On <see cref="FrameDecodeStatus.Frame"/> or
    /// <see cref="FrameDecodeStatus.Invalid"/>, <paramref name="buffer"/> is advanced past the consumed bytes and
    /// (for valid frames) the decoded payload is written to <paramref name="payload"/>.
    /// </summary>
    FrameDecodeStatus TryDecode(ref ReadOnlySequence<byte> buffer, IBufferWriter<byte> payload);
}

/// <summary>Encodes payloads into wire frames.</summary>
public interface IFrameEncoder
{
    /// <summary>Writes the framed form of <paramref name="payload"/> to <paramref name="output"/>.</summary>
    void Encode(ReadOnlySpan<byte> payload, IBufferWriter<byte> output);
}

/// <summary>Helpers that connect decoders to pipes.</summary>
public static class FramingPipeExtensions
{
    /// <summary>
    /// Reads frames from <paramref name="reader"/> until completion or cancellation.
    /// Each yielded buffer is a fresh array owned by the caller. Invalid frames are skipped.
    /// </summary>
    public static async IAsyncEnumerable<byte[]> ReadFramesAsync(this PipeReader reader, IFrameDecoder decoder, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(decoder);
        var payload = new ArrayBufferWriter<byte>(256);
        while (true)
        {
            var result = await reader.ReadAsync(ct).ConfigureAwait(false);
            var buffer = result.Buffer;
            var frames = new List<byte[]>();
            while (true)
            {
                payload.ResetWrittenCount();
                var status = decoder.TryDecode(ref buffer, payload);
                if (status == FrameDecodeStatus.NeedMoreData) break;
                if (status == FrameDecodeStatus.Frame) frames.Add(payload.WrittenSpan.ToArray());
            }
            reader.AdvanceTo(buffer.Start, buffer.End);
            foreach (var f in frames) yield return f;
            if (result.IsCompleted || result.IsCanceled) yield break;
        }
    }

    /// <summary>Encodes and writes one frame, then flushes.</summary>
    public static ValueTask<FlushResult> WriteFrameAsync(this PipeWriter writer, IFrameEncoder encoder, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(encoder);
        encoder.Encode(payload.Span, writer);
        return writer.FlushAsync(ct);
    }
}
