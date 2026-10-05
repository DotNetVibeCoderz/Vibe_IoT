using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace IoTCom.Net.Protocols.Mavlink;

/// <summary>Protocol version of a frame.</summary>
public enum MavlinkVersion : byte
{
    /// <summary>MAVLink 1 (0xFE, 8-bit message ids).</summary>
    V1 = 1,
    /// <summary>MAVLink 2 (0xFD, 24-bit ids, payload truncation, optional signing).</summary>
    V2 = 2,
}

/// <summary>MAVLink 2 signing (secret key, link id and the 48-bit timestamp in 10 µs units since 2015-01-01).</summary>
public sealed class MavlinkSigning
{
    private static readonly DateTimeOffset Epoch = new(2015, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly byte[] _key;
    private ulong _last;

    /// <summary>Creates signing with a 32-byte secret key.</summary>
    public MavlinkSigning(ReadOnlySpan<byte> key, byte linkId = 0)
    {
        if (key.Length != 32) throw new ArgumentException("MAVLink signing keys are 32 bytes (e.g. SHA-256 of a passphrase).", nameof(key));
        _key = key.ToArray();
        LinkId = linkId;
    }

    /// <summary>Derives the key from a passphrase as MAVProxy and Mission Planner do (SHA-256 of the UTF-8 text).</summary>
    public static MavlinkSigning FromPassphrase(string passphrase, byte linkId = 0) => new(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(passphrase)), linkId);

    /// <summary>Link id written into signed frames.</summary>
    public byte LinkId { get; }

    /// <summary>Accept unsigned frames from peers (default false once signing is on).</summary>
    public bool AcceptUnsigned { get; set; }

    /// <summary>Next strictly increasing timestamp (10 µs units since 2015-01-01).</summary>
    public ulong NextTimestamp()
    {
        var now = (ulong)((DateTimeOffset.UtcNow - Epoch).Ticks / 100);
        while (true)
        {
            var last = Interlocked.Read(ref _last);
            var next = Math.Max(now, last + 1);
            if (Interlocked.CompareExchange(ref _last, next, last) == last) return next;
        }
    }

    /// <summary>The 6-byte signature of <paramref name="frame"/> (header to CRC) with link id and timestamp.</summary>
    public void Sign(ReadOnlySpan<byte> frame, byte linkId, ulong timestamp, Span<byte> signature)
    {
        var input = new byte[32 + frame.Length + 7];
        _key.CopyTo(input, 0);
        frame.CopyTo(input.AsSpan(32));
        input[32 + frame.Length] = linkId;
        WriteUInt48(input.AsSpan(33 + frame.Length), timestamp);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);
        hash[..6].CopyTo(signature);
    }

    internal static void WriteUInt48(Span<byte> d, ulong v)
    {
        for (var i = 0; i < 6; i++) d[i] = (byte)(v >> (8 * i));
    }

    internal static ulong ReadUInt48(ReadOnlySpan<byte> d)
    {
        ulong v = 0;
        for (var i = 5; i >= 0; i--) v = (v << 8) | d[i];
        return v;
    }
}

/// <summary>A received (or encoded) frame.</summary>
/// <param name="Version">Protocol version.</param>
/// <param name="Sequence">Sequence number (per sender, wraps at 255).</param>
/// <param name="SystemId">Sender system id.</param>
/// <param name="ComponentId">Sender component id.</param>
/// <param name="MessageId">Message id.</param>
/// <param name="Payload">Payload as received (MAVLink 2 may be truncated).</param>
/// <param name="Message">Decoded message, or null when the dialect does not know the id.</param>
/// <param name="Signed">True when the frame carried a (verified, if a key is configured) signature.</param>
public sealed record MavlinkPacket(MavlinkVersion Version, byte Sequence, byte SystemId, byte ComponentId, uint MessageId,
    ReadOnlyMemory<byte> Payload, IMavlinkMessage? Message, bool Signed)
{
    /// <summary>Receive time.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>The complete frame bytes.</summary>
    public ReadOnlyMemory<byte> Frame { get; init; }

    /// <inheritdoc />
    public override string ToString() => $"#{Sequence} {SystemId}/{ComponentId} {Message?.ToString() ?? $"msg {MessageId}"}";
}

/// <summary>Sans-I/O MAVLink framing (no sockets, no clocks): encode messages, checksum helpers.</summary>
public static class MavlinkCodec
{
    /// <summary>MAVLink 1 start byte.</summary>
    public const byte StxV1 = 0xFE;
    /// <summary>MAVLink 2 start byte.</summary>
    public const byte StxV2 = 0xFD;
    /// <summary>MAVLink 2 incompatibility flag: signed frame.</summary>
    public const byte IncompatSigned = 0x01;
    /// <summary>Largest frame: 10 header + 255 payload + 2 CRC + 13 signature.</summary>
    public const int MaxFrameLength = 280;

    /// <summary>CRC-16/MCRF4XX (X.25) accumulate.</summary>
    public static ushort Accumulate(ushort crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            unchecked
            {
                var t = (byte)(b ^ (byte)crc);
                t ^= (byte)(t << 4);
                crc = (ushort)((crc >> 8) ^ (t << 8) ^ (t << 3) ^ (t >> 4));
            }
        }
        return crc;
    }

    /// <summary>Frame checksum: X.25 over the header after STX and the payload, then CRC_EXTRA.</summary>
    public static ushort Checksum(ReadOnlySpan<byte> headerAfterStx, ReadOnlySpan<byte> payload, byte crcExtra) =>
        Accumulate(Accumulate(Accumulate(0xFFFF, headerAfterStx), payload), [crcExtra]);

    /// <summary>Encodes a message into <paramref name="destination"/> (≥ <see cref="MaxFrameLength"/>) and returns the frame length.</summary>
    public static int Encode(Span<byte> destination, IMavlinkMessage message, byte sequence, byte systemId, byte componentId,
        MavlinkVersion version = MavlinkVersion.V2, MavlinkSigning? signing = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        Span<byte> payload = stackalloc byte[256];
        var length = message.Serialize(payload);
        if (version == MavlinkVersion.V1)
        {
            if (message.MessageId > 255) throw new ArgumentException($"{message.Name} (id {message.MessageId}) needs MAVLink 2.", nameof(version));
            destination[0] = StxV1;
            destination[1] = (byte)length;
            destination[2] = sequence;
            destination[3] = systemId;
            destination[4] = componentId;
            destination[5] = (byte)message.MessageId;
            payload[..length].CopyTo(destination[6..]);
            var crc1 = Checksum(destination[1..6], payload[..length], message.CrcExtra);
            BinaryPrimitives.WriteUInt16LittleEndian(destination[(6 + length)..], crc1);
            return 8 + length;
        }

        // MAVLink 2 drops trailing zero bytes but always keeps at least one.
        while (length > 1 && payload[length - 1] == 0) length--;
        destination[0] = StxV2;
        destination[1] = (byte)length;
        destination[2] = signing is null ? (byte)0 : IncompatSigned;
        destination[3] = 0;
        destination[4] = sequence;
        destination[5] = systemId;
        destination[6] = componentId;
        destination[7] = (byte)message.MessageId;
        destination[8] = (byte)(message.MessageId >> 8);
        destination[9] = (byte)(message.MessageId >> 16);
        payload[..length].CopyTo(destination[10..]);
        var crc = Checksum(destination[1..10], payload[..length], message.CrcExtra);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[(10 + length)..], crc);
        var n = 12 + length;
        if (signing is null) return n;
        var ts = signing.NextTimestamp();
        destination[n] = signing.LinkId;
        MavlinkSigning.WriteUInt48(destination[(n + 1)..], ts);
        signing.Sign(destination[..n], signing.LinkId, ts, destination[(n + 7)..]);
        return n + 13;
    }

    /// <summary>Encodes to a new array.</summary>
    public static byte[] Encode(IMavlinkMessage message, byte sequence, byte systemId, byte componentId,
        MavlinkVersion version = MavlinkVersion.V2, MavlinkSigning? signing = null)
    {
        Span<byte> buf = stackalloc byte[MaxFrameLength];
        return buf[..Encode(buf, message, sequence, systemId, componentId, version, signing)].ToArray();
    }
}

/// <summary>
/// Streaming MAVLink parser (sans-I/O): feed bytes in any chunking, read frames. Garbage, CRC failures and unknown
/// message ids are skipped one byte at a time so the parser re-synchronises on the next start byte.
/// </summary>
public sealed class MavlinkParser(IMavlinkDialect dialect, MavlinkSigning? signing = null)
{
    private readonly ArrayBufferWriter<byte> _buffer = new(1024);
    private int _start;

    /// <summary>The dialect used for CRC_EXTRA and decoding.</summary>
    public IMavlinkDialect Dialect { get; } = dialect;

    /// <summary>Frames with a wrong checksum.</summary>
    public long CrcErrors { get; private set; }

    /// <summary>Frames whose message id the dialect does not know (cannot be verified).</summary>
    public long UnknownMessages { get; private set; }

    /// <summary>Signed frames with a bad signature, or unsigned frames rejected because signing is required.</summary>
    public long SignatureErrors { get; private set; }

    /// <summary>Bytes skipped while searching for a start byte.</summary>
    public long BytesDiscarded { get; private set; }

    /// <summary>Appends received bytes.</summary>
    public void Feed(ReadOnlySpan<byte> data)
    {
        if (_start > 0 && _start == _buffer.WrittenCount)
        {
            _buffer.ResetWrittenCount();
            _start = 0;
        }
        else if (_start > 4096)
        {
            var rest = _buffer.WrittenSpan[_start..].ToArray();
            _buffer.ResetWrittenCount();
            _buffer.Write(rest);
            _start = 0;
        }
        _buffer.Write(data);
    }

    /// <summary>Returns the next valid frame, or false when more bytes are needed.</summary>
    public bool TryRead(out MavlinkPacket packet)
    {
        packet = null!;
        while (true)
        {
            var span = _buffer.WrittenSpan[_start..];
            var stx = span.IndexOfAny(MavlinkCodec.StxV1, MavlinkCodec.StxV2);
            if (stx < 0)
            {
                BytesDiscarded += span.Length;
                _start += span.Length;
                return false;
            }
            BytesDiscarded += stx;
            _start += stx;
            span = span[stx..];
            var v2 = span[0] == MavlinkCodec.StxV2;
            var header = v2 ? 10 : 6;
            if (span.Length < header) return false;
            var len = span[1];
            var signed = v2 && (span[2] & MavlinkCodec.IncompatSigned) != 0;
            if (v2 && (span[2] & ~MavlinkCodec.IncompatSigned) != 0)
            {
                Skip();
                continue; // unknown incompatibility flags: must not be processed
            }
            var total = header + len + 2 + (signed ? 13 : 0);
            if (span.Length < total) return false;
            var frame = span[..total];
            var id = v2 ? (uint)(frame[7] | frame[8] << 8 | frame[9] << 16) : frame[5];
            if (!Dialect.TryGetInfo(id, out var info))
            {
                UnknownMessages++;
                Skip();
                continue;
            }
            var crc = MavlinkCodec.Checksum(frame[1..header], frame.Slice(header, len), info.CrcExtra);
            if (crc != BinaryPrimitives.ReadUInt16LittleEndian(frame[(header + len)..]))
            {
                CrcErrors++;
                Skip();
                continue;
            }
            if (signing is not null)
            {
                var ok = signed ? VerifySignature(frame, header + len + 2) : signing.AcceptUnsigned;
                if (!ok)
                {
                    SignatureErrors++;
                    _start += total;
                    continue;
                }
            }
            var payload = frame.Slice(header, len).ToArray();
            var message = Dialect.Deserialize(id, payload);
            packet = new MavlinkPacket(v2 ? MavlinkVersion.V2 : MavlinkVersion.V1, frame[v2 ? 4 : 2], frame[v2 ? 5 : 3], frame[v2 ? 6 : 4], id, payload, message, signed)
            {
                Frame = frame.ToArray(),
            };
            _start += total;
            return true;
        }
    }

    private bool VerifySignature(ReadOnlySpan<byte> frame, int signedLength)
    {
        var linkId = frame[signedLength];
        var ts = MavlinkSigning.ReadUInt48(frame[(signedLength + 1)..]);
        Span<byte> expected = stackalloc byte[6];
        signing!.Sign(frame[..signedLength], linkId, ts, expected);
        return expected.SequenceEqual(frame.Slice(signedLength + 7, 6));
    }

    private void Skip() => _start++;
}

/// <summary>Frame-lane description of MAVLink frames.</summary>
public static class MavlinkAnatomy
{
    /// <summary>Describes a MAVLink 1 or 2 frame.</summary>
    public static IReadOnlyList<FrameField> Describe(ReadOnlySpan<byte> f, IMavlinkDialect? dialect = null)
    {
        if (f.Length < 8 || f[0] is not (MavlinkCodec.StxV1 or MavlinkCodec.StxV2)) return [new FrameField("Data", 0, f.Length, FrameFieldKind.Data)];
        var v2 = f[0] == MavlinkCodec.StxV2;
        var header = v2 ? 10 : 6;
        var len = f[1];
        var id = v2 ? (uint)(f[7] | f[8] << 8 | f[9] << 16) : f[5];
        var name = dialect is not null && dialect.TryGetInfo(id, out var info) ? info.Name : id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var fields = new List<FrameField>
        {
            new("STX", 0, 1, FrameFieldKind.Header, v2 ? "v2" : "v1"),
            new("Len", 1, 1, FrameFieldKind.Length, len.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        };
        if (v2) fields.Add(new FrameField("Flags", 2, 2, FrameFieldKind.Header, (f[2] & 1) != 0 ? "signed" : null));
        fields.Add(new FrameField("Seq", v2 ? 4 : 2, 1, FrameFieldKind.Header, f[v2 ? 4 : 2].ToString(System.Globalization.CultureInfo.InvariantCulture)));
        fields.Add(new FrameField("Sys/Comp", v2 ? 5 : 3, 2, FrameFieldKind.Address, $"{f[v2 ? 5 : 3]}/{f[v2 ? 6 : 4]}"));
        fields.Add(new FrameField("MsgId", v2 ? 7 : 5, v2 ? 3 : 1, FrameFieldKind.Function, name));
        if (len > 0 && f.Length >= header + len) fields.Add(new FrameField("Payload", header, len, FrameFieldKind.Data));
        if (f.Length >= header + len + 2) fields.Add(new FrameField("CRC", header + len, 2, FrameFieldKind.Checksum));
        if (v2 && (f[2] & 1) != 0 && f.Length >= header + len + 15) fields.Add(new FrameField("Signature", header + len + 2, 13, FrameFieldKind.Checksum));
        return fields;
    }
}
