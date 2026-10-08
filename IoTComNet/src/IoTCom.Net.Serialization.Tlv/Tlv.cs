using System.Buffers.Binary;
using System.Globalization;

namespace IoTCom.Net.Serialization.Tlv;

/// <summary>One TLV item; constructed BER items carry children.</summary>
/// <param name="Tag">Tag.</param>
/// <param name="Value">Value bytes (for constructed BER items, the encoded children).</param>
/// <param name="Children">Children of a constructed BER item.</param>
/// <param name="Offset">Offset of the tag in the decoded buffer.</param>
/// <param name="HeaderLength">Bytes of tag and length.</param>
public sealed record TlvItem(uint Tag, byte[] Value, IReadOnlyList<TlvItem> Children, int Offset = 0, int HeaderLength = 0)
{
    /// <summary>Creates a primitive item.</summary>
    public static TlvItem Primitive(uint tag, ReadOnlySpan<byte> value) => new(tag, value.ToArray(), []);

    /// <summary>Creates a constructed BER item.</summary>
    public static TlvItem Constructed(uint tag, params TlvItem[] children) => new(tag, [], children);

    /// <summary>The first child (or descendant) with <paramref name="tag"/>.</summary>
    public TlvItem? Find(uint tag) => Children.FirstOrDefault(c => c.Tag == tag) ?? Children.Select(c => c.Find(tag)).FirstOrDefault(c => c is not null);

    /// <inheritdoc />
    public override string ToString() => Children.Count > 0
        ? $"{Tag:X} [{string.Join(", ", Children)}]"
        : $"{Tag:X}={Convert.ToHexString(Value)}";
}

/// <summary>A fixed TLV layout: tag size, length size (1, 2 or 4 bytes) and byte order.</summary>
/// <param name="TagSize">Bytes of the tag.</param>
/// <param name="LengthSize">Bytes of the length.</param>
/// <param name="BigEndian">Network byte order (default) or little-endian.</param>
public sealed record TlvFormat(int TagSize = 1, int LengthSize = 1, bool BigEndian = true)
{
    /// <summary>1-byte tag, 1-byte length.</summary>
    public static TlvFormat Simple { get; } = new();

    /// <summary>2-byte tag, 2-byte length, big-endian.</summary>
    public static TlvFormat Short16 { get; } = new(2, 2);

    /// <summary>Encodes items one after another.</summary>
    public byte[] Encode(IEnumerable<TlvItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var buffer = new List<byte>();
        foreach (var item in items)
        {
            Write(buffer, item.Tag, TagSize);
            Write(buffer, (uint)item.Value.Length, LengthSize);
            buffer.AddRange(item.Value);
        }

        return [.. buffer];
    }

    /// <summary>Decodes items; throws <see cref="FormatException"/> when a length runs past the end.</summary>
    public IReadOnlyList<TlvItem> Decode(ReadOnlySpan<byte> data)
    {
        var items = new List<TlvItem>();
        var pos = 0;
        while (pos < data.Length)
        {
            if (pos + TagSize + LengthSize > data.Length) throw new FormatException($"Truncated TLV header at offset {pos}.");
            var tag = Read(data.Slice(pos, TagSize));
            var length = Read(data.Slice(pos + TagSize, LengthSize));
            var start = pos + TagSize + LengthSize;
            if (length > (uint)(data.Length - start)) throw new FormatException($"TLV {tag:X} claims {length} bytes, {data.Length - start} remain.");
            items.Add(new TlvItem(tag, data.Slice(start, (int)length).ToArray(), [], pos, TagSize + LengthSize));
            pos = start + (int)length;
        }

        return items;
    }

    private uint Read(ReadOnlySpan<byte> b) => b.Length switch
    {
        1 => b[0],
        2 => BigEndian ? BinaryPrimitives.ReadUInt16BigEndian(b) : BinaryPrimitives.ReadUInt16LittleEndian(b),
        4 => BigEndian ? BinaryPrimitives.ReadUInt32BigEndian(b) : BinaryPrimitives.ReadUInt32LittleEndian(b),
        _ => throw new InvalidOperationException("TLV fields are 1, 2 or 4 bytes."),
    };

    private void Write(List<byte> buffer, uint value, int size)
    {
        Span<byte> b = stackalloc byte[4];
        switch (size)
        {
            case 1:
                if (value > 0xFF) throw new ArgumentOutOfRangeException(nameof(value), $"{value} does not fit one byte.");
                buffer.Add((byte)value);
                return;
            case 2:
                if (value > 0xFFFF) throw new ArgumentOutOfRangeException(nameof(value), $"{value} does not fit two bytes.");
                if (BigEndian) BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)value);
                else BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)value);
                buffer.AddRange(b[..2]);
                return;
            case 4:
                if (BigEndian) BinaryPrimitives.WriteUInt32BigEndian(b, value);
                else BinaryPrimitives.WriteUInt32LittleEndian(b, value);
                buffer.AddRange(b[..4]);
                return;
            default:
                throw new InvalidOperationException("TLV fields are 1, 2 or 4 bytes.");
        }
    }
}

/// <summary>
/// BER-TLV (ISO/IEC 8825-1 as used by EMV and ISO 7816): tags of one or more bytes (low five bits 0x1F = more bytes
/// follow), short and long-form lengths, constructed tags (bit 6) decoded recursively.
/// </summary>
public static class BerTlv
{
    /// <summary>True when <paramref name="tag"/> is constructed (bit 6 of its first byte).</summary>
    public static bool IsConstructed(uint tag)
    {
        var first = tag;
        while (first > 0xFF) first >>= 8;
        return (first & 0x20) != 0;
    }

    /// <summary>Decodes items (constructed ones recursively); throws <see cref="FormatException"/> on malformed input.</summary>
    public static IReadOnlyList<TlvItem> Decode(ReadOnlySpan<byte> data, int baseOffset = 0, int depth = 0)
    {
        if (depth > 16) throw new FormatException("BER-TLV nested deeper than 16 levels.");
        var items = new List<TlvItem>();
        var pos = 0;
        while (pos < data.Length)
        {
            // Padding bytes 00 and FF may appear between items (EMV).
            if (data[pos] is 0x00 or 0xFF)
            {
                pos++;
                continue;
            }

            var start = pos;
            uint tag = data[pos++];
            if ((tag & 0x1F) == 0x1F)
            {
                do
                {
                    if (pos >= data.Length) throw new FormatException("Truncated BER-TLV tag.");
                    if (tag > 0xFFFFFF) throw new FormatException("BER-TLV tag longer than four bytes.");
                    tag = (tag << 8) | data[pos];
                }
                while ((data[pos++] & 0x80) != 0);
            }

            if (pos >= data.Length) throw new FormatException("Missing BER-TLV length.");
            int length = data[pos++];
            if (length > 0x80)
            {
                var n = length & 0x7F;
                if (n > 3 || pos + n > data.Length) throw new FormatException("Invalid BER-TLV long-form length.");
                length = 0;
                for (var i = 0; i < n; i++) length = (length << 8) | data[pos++];
            }
            else if (length == 0x80)
            {
                throw new FormatException("Indefinite BER lengths are not supported.");
            }

            if (length > data.Length - pos) throw new FormatException($"BER-TLV {tag:X} claims {length} bytes, {data.Length - pos} remain.");
            var value = data.Slice(pos, length);
            var header = pos - start;
            items.Add(IsConstructed(tag)
                ? new TlvItem(tag, value.ToArray(), Decode(value, baseOffset + pos, depth + 1), baseOffset + start, header)
                : new TlvItem(tag, value.ToArray(), [], baseOffset + start, header));
            pos += length;
        }

        return items;
    }

    /// <summary>Encodes items (constructed ones from their children).</summary>
    public static byte[] Encode(IEnumerable<TlvItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var buffer = new List<byte>();
        foreach (var item in items)
        {
            var value = item.Children.Count > 0 ? Encode(item.Children) : item.Value;
            var tagBytes = new List<byte>();
            for (var t = item.Tag; t > 0 || tagBytes.Count == 0; t >>= 8) tagBytes.Insert(0, (byte)t);
            buffer.AddRange(tagBytes);
            if (value.Length < 0x80) buffer.Add((byte)value.Length);
            else if (value.Length <= 0xFF) buffer.AddRange([0x81, (byte)value.Length]);
            else if (value.Length <= 0xFFFF) buffer.AddRange([0x82, (byte)(value.Length >> 8), (byte)value.Length]);
            else buffer.AddRange([0x83, (byte)(value.Length >> 16), (byte)(value.Length >> 8), (byte)value.Length]);
            buffer.AddRange(value);
        }

        return [.. buffer];
    }

    /// <summary>Frame-lane fields: one tag/length header and one value per primitive item.</summary>
    public static IReadOnlyList<FrameField> Describe(ReadOnlySpan<byte> data)
    {
        IReadOnlyList<TlvItem> items;
        try
        {
            items = Decode(data);
        }
        catch (FormatException ex)
        {
            return [new FrameField("Invalid", 0, data.Length, FrameFieldKind.Error, ex.Message)];
        }

        var fields = new List<FrameField>();
        void Walk(IEnumerable<TlvItem> list)
        {
            foreach (var i in list)
            {
                var tag = i.Tag.ToString("X", CultureInfo.InvariantCulture);
                fields.Add(new FrameField(tag, i.Offset, i.HeaderLength, IsConstructed(i.Tag) ? FrameFieldKind.Function : FrameFieldKind.Address, $"{i.Value.Length} B"));
                if (i.Children.Count > 0) Walk(i.Children);
                else if (i.Value.Length > 0) fields.Add(new FrameField("Value", i.Offset + i.HeaderLength, i.Value.Length, FrameFieldKind.Data, Convert.ToHexString(i.Value)));
            }
        }

        Walk(items);
        return fields;
    }
}
