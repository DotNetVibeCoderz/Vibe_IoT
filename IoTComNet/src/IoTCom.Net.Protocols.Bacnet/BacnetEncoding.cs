using System.Buffers.Binary;
using System.Text;

namespace IoTCom.Net.Protocols.Bacnet;

/// <summary>Writes BACnet tagged data (ASHRAE 135 clause 20.2).</summary>
public sealed class BacnetWriter
{
    private readonly List<byte> _b = new(64);

    /// <summary>Bytes written.</summary>
    public int Length => _b.Count;

    /// <summary>The encoded bytes.</summary>
    public byte[] ToArray() => [.. _b];

    /// <summary>Appends raw bytes.</summary>
    public BacnetWriter Raw(params byte[] bytes)
    {
        _b.AddRange(bytes);
        return this;
    }

    private void Tag(int number, bool context, int lengthValueType)
    {
        var first = (byte)((context ? 0x08 : 0) | Math.Min(lengthValueType, 5));
        if (number <= 14) _b.Add((byte)((number << 4) | first));
        else
        {
            _b.Add((byte)(0xF0 | first));
            _b.Add((byte)number);
        }

        if (lengthValueType < 5) return;
        if (lengthValueType <= 253) _b.Add((byte)lengthValueType);
        else if (lengthValueType <= 0xFFFF)
        {
            _b.Add(254);
            _b.Add((byte)(lengthValueType >> 8));
            _b.Add((byte)lengthValueType);
        }
        else
        {
            _b.Add(255);
            for (var s = 24; s >= 0; s -= 8) _b.Add((byte)(lengthValueType >> s));
        }
    }

    private void Content(int number, bool context, byte[] content)
    {
        Tag(number, context, content.Length);
        _b.AddRange(content);
    }

    /// <summary>Minimal big-endian unsigned bytes (at least one).</summary>
    public static byte[] UnsignedBytes(ulong v)
    {
        var n = 1;
        while (n < 8 && v >> (8 * n) != 0) n++;
        var b = new byte[n];
        for (var i = 0; i < n; i++) b[n - 1 - i] = (byte)(v >> (8 * i));
        return b;
    }

    /// <summary>Minimal two's-complement signed bytes.</summary>
    public static byte[] SignedBytes(long v)
    {
        var n = 1;
        while (n < 8 && (v < -(1L << ((8 * n) - 1)) || v >= 1L << ((8 * n) - 1))) n++;
        var b = new byte[n];
        for (var i = 0; i < n; i++) b[n - 1 - i] = (byte)(v >> (8 * i));
        return b;
    }

    /// <summary>Writes an application-tagged value.</summary>
    public BacnetWriter Application(BacnetValue value)
    {
        var tag = (int)value.Tag;
        switch (value.Tag)
        {
            case BacnetApplicationTag.Null:
                Tag(tag, false, 0);
                break;
            case BacnetApplicationTag.Boolean:
                Tag(tag, false, value.Value is true ? 1 : 0);
                break;
            default:
                Content(tag, false, Encode(value));
                break;
        }

        return this;
    }

    /// <summary>The content octets of a value (no tag).</summary>
    public static byte[] Encode(BacnetValue value)
    {
        switch (value.Tag)
        {
            case BacnetApplicationTag.Null:
                return [];
            case BacnetApplicationTag.Boolean:
                return [(byte)(value.Value is true ? 1 : 0)];
            case BacnetApplicationTag.UnsignedInteger or BacnetApplicationTag.Enumerated:
                return UnsignedBytes(Convert.ToUInt64(value.Value, System.Globalization.CultureInfo.InvariantCulture));
            case BacnetApplicationTag.SignedInteger:
                return SignedBytes(Convert.ToInt64(value.Value, System.Globalization.CultureInfo.InvariantCulture));
            case BacnetApplicationTag.Real:
                var f = new byte[4];
                BinaryPrimitives.WriteSingleBigEndian(f, Convert.ToSingle(value.Value, System.Globalization.CultureInfo.InvariantCulture));
                return f;
            case BacnetApplicationTag.Double:
                var d = new byte[8];
                BinaryPrimitives.WriteDoubleBigEndian(d, Convert.ToDouble(value.Value, System.Globalization.CultureInfo.InvariantCulture));
                return d;
            case BacnetApplicationTag.OctetString:
                return (byte[])value.Value!;
            case BacnetApplicationTag.CharacterString:
                return [0, .. Encoding.UTF8.GetBytes((string)value.Value!)];   // character set 0 = ISO 10646 (UTF-8)
            case BacnetApplicationTag.BitString:
                var bits = ((BacnetBitString)value.Value!).Bits;
                var bytes = new byte[1 + ((bits.Length + 7) / 8)];
                bytes[0] = (byte)((8 - (bits.Length % 8)) % 8);
                for (var i = 0; i < bits.Length; i++)
                    if (bits[i]) bytes[1 + (i / 8)] |= (byte)(0x80 >> (i % 8));
                return bytes;
            case BacnetApplicationTag.Date:
                var date = (BacnetDate)value.Value!;
                return [(byte)(date.Year == 255 ? 255 : date.Year - 1900), (byte)date.Month, (byte)date.Day, (byte)date.DayOfWeek];
            case BacnetApplicationTag.Time:
                var t = (BacnetTime)value.Value!;
                return [(byte)t.Hour, (byte)t.Minute, (byte)t.Second, (byte)t.Hundredths];
            case BacnetApplicationTag.ObjectIdentifier:
                var o = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(o, ((BacnetObjectId)value.Value!).Raw);
                return o;
            default:
                throw new ArgumentOutOfRangeException(nameof(value), $"Unknown application tag {value.Tag}.");
        }
    }

    /// <summary>Context-tagged unsigned integer.</summary>
    public BacnetWriter ContextUnsigned(int tag, ulong v)
    {
        Content(tag, true, UnsignedBytes(v));
        return this;
    }

    /// <summary>Context-tagged enumerated.</summary>
    public BacnetWriter ContextEnumerated(int tag, uint v) => ContextUnsigned(tag, v);

    /// <summary>Context-tagged boolean.</summary>
    public BacnetWriter ContextBoolean(int tag, bool v)
    {
        Content(tag, true, [(byte)(v ? 1 : 0)]);
        return this;
    }

    /// <summary>Context-tagged object identifier.</summary>
    public BacnetWriter ContextObjectId(int tag, BacnetObjectId id)
    {
        Content(tag, true, Encode(BacnetValue.Object(id)));
        return this;
    }

    /// <summary>Context-tagged character string.</summary>
    public BacnetWriter ContextText(int tag, string text)
    {
        Content(tag, true, Encode(BacnetValue.Text(text)));
        return this;
    }

    /// <summary>Opening tag.</summary>
    public BacnetWriter Open(int tag)
    {
        _b.Add((byte)((tag << 4) | 0x0E));
        return this;
    }

    /// <summary>Closing tag.</summary>
    public BacnetWriter Close(int tag)
    {
        _b.Add((byte)((tag << 4) | 0x0F));
        return this;
    }
}

/// <summary>Reads BACnet tagged data.</summary>
public ref struct BacnetReader
{
    private readonly ReadOnlySpan<byte> _b;
    private int _pos;

    /// <summary>Starts reading.</summary>
    public BacnetReader(ReadOnlySpan<byte> data) => _b = data;

    /// <summary>Current offset.</summary>
    public readonly int Position => _pos;

    /// <summary>True when everything has been read.</summary>
    public readonly bool End => _pos >= _b.Length;

    /// <summary>The header of the next tag.</summary>
    public readonly record struct TagHeader(int Number, bool Context, bool Opening, bool Closing, int Length, int HeaderSize);

    /// <summary>Reads the next tag header without consuming it.</summary>
    public readonly TagHeader Peek()
    {
        if (_pos >= _b.Length) throw new ProtocolException("BACnet data ended where a tag was expected.");
        var first = _b[_pos];
        var size = 1;
        var number = first >> 4;
        if (number == 15)
        {
            if (_pos + 1 >= _b.Length) throw new ProtocolException("BACnet extended tag number is truncated.");
            number = _b[_pos + 1];
            size = 2;
        }

        var context = (first & 0x08) != 0;
        var lvt = first & 0x07;
        if (context && lvt == 6) return new TagHeader(number, true, true, false, 0, size);
        if (context && lvt == 7) return new TagHeader(number, true, false, true, 0, size);
        var length = lvt;
        if (lvt == 5)
        {
            if (_pos + size >= _b.Length) throw new ProtocolException("BACnet tag length is truncated.");
            length = _b[_pos + size];
            size++;
            if (length == 254)
            {
                if (_pos + size + 2 > _b.Length) throw new ProtocolException("BACnet tag length is truncated.");
                length = BinaryPrimitives.ReadUInt16BigEndian(_b[(_pos + size)..]);
                size += 2;
            }
            else if (length == 255)
            {
                if (_pos + size + 4 > _b.Length) throw new ProtocolException("BACnet tag length is truncated.");
                length = (int)BinaryPrimitives.ReadUInt32BigEndian(_b[(_pos + size)..]);
                size += 4;
            }
        }

        if (!context && number == (int)BacnetApplicationTag.Boolean) return new TagHeader(number, false, false, false, 0, size);   // the value is in the LVT bits
        if (_pos + size + length > _b.Length) throw new ProtocolException($"BACnet tag {number} declares {length} byte(s) but only {_b.Length - _pos - size} remain.");
        return new TagHeader(number, context, false, false, length, size);
    }

    /// <summary>True when the next tag is the given context tag (not opening/closing).</summary>
    public readonly bool IsContext(int tag) => !End && Peek() is { Context: true, Opening: false, Closing: false } h && h.Number == tag;

    /// <summary>True when the next tag opens the given context tag.</summary>
    public readonly bool IsOpening(int tag) => !End && Peek() is { Opening: true } h && h.Number == tag;

    /// <summary>True when the next tag closes the given context tag.</summary>
    public readonly bool IsClosing(int tag) => !End && Peek() is { Closing: true } h && h.Number == tag;

    private ReadOnlySpan<byte> TakeContent(TagHeader h)
    {
        var content = _b.Slice(_pos + h.HeaderSize, h.Length);
        _pos += h.HeaderSize + h.Length;
        return content;
    }

    /// <summary>Consumes an opening tag.</summary>
    public void Open(int tag)
    {
        if (!IsOpening(tag)) throw new ProtocolException($"Expected opening tag {tag}.");
        _pos += Peek().HeaderSize;
    }

    /// <summary>Consumes a closing tag.</summary>
    public void Close(int tag)
    {
        if (!IsClosing(tag)) throw new ProtocolException($"Expected closing tag {tag}.");
        _pos += Peek().HeaderSize;
    }

    /// <summary>Reads a context-tagged unsigned integer.</summary>
    public ulong ContextUnsigned(int tag)
    {
        var h = Peek();
        if (!h.Context || h.Opening || h.Closing || h.Number != tag) throw new ProtocolException($"Expected context tag {tag}, found {(h.Context ? "context" : "application")} tag {h.Number}.");
        return Unsigned(TakeContent(h));
    }

    /// <summary>Reads a context-tagged boolean.</summary>
    public bool ContextBoolean(int tag) => ContextUnsigned(tag) != 0;

    /// <summary>Reads a context-tagged object identifier.</summary>
    public BacnetObjectId ContextObjectId(int tag)
    {
        var h = Peek();
        if (!h.Context || h.Number != tag || h.Length != 4) throw new ProtocolException($"Expected a context-{tag} object identifier.");
        return BacnetObjectId.FromRaw(BinaryPrimitives.ReadUInt32BigEndian(TakeContent(h)));
    }

    /// <summary>Reads a context-tagged character string.</summary>
    public string ContextText(int tag)
    {
        var h = Peek();
        if (!h.Context || h.Number != tag) throw new ProtocolException($"Expected a context-{tag} character string.");
        return Text(TakeContent(h));
    }

    /// <summary>Reads an application-tagged value.</summary>
    public BacnetValue Application()
    {
        var h = Peek();
        if (h.Context) throw new ProtocolException($"Expected an application tag, found context tag {h.Number}.");
        var tag = (BacnetApplicationTag)h.Number;
        if (tag == BacnetApplicationTag.Boolean)
        {
            var lvt = _b[_pos] & 0x07;
            _pos += h.HeaderSize;
            return BacnetValue.Boolean(lvt != 0);
        }

        var c = TakeContent(h);
        return tag switch
        {
            BacnetApplicationTag.Null => BacnetValue.Null,
            BacnetApplicationTag.UnsignedInteger => BacnetValue.Unsigned(checked((uint)Unsigned(c))),
            BacnetApplicationTag.Enumerated => BacnetValue.Enumerated(checked((uint)Unsigned(c))),
            BacnetApplicationTag.SignedInteger => BacnetValue.Signed(checked((int)Signed(c))),
            BacnetApplicationTag.Real => c.Length == 4 ? BacnetValue.Real(BinaryPrimitives.ReadSingleBigEndian(c)) : throw new ProtocolException("REAL must be 4 bytes."),
            BacnetApplicationTag.Double => c.Length == 8 ? new BacnetValue(tag, BinaryPrimitives.ReadDoubleBigEndian(c)) : throw new ProtocolException("DOUBLE must be 8 bytes."),
            BacnetApplicationTag.OctetString => new BacnetValue(tag, c.ToArray()),
            BacnetApplicationTag.CharacterString => BacnetValue.Text(Text(c)),
            BacnetApplicationTag.BitString => BacnetValue.Bits(BitString(c)),
            BacnetApplicationTag.Date => c.Length == 4 ? new BacnetValue(tag, new BacnetDate(c[0] == 255 ? 255 : 1900 + c[0], c[1], c[2], c[3])) : throw new ProtocolException("Date must be 4 bytes."),
            BacnetApplicationTag.Time => c.Length == 4 ? new BacnetValue(tag, new BacnetTime(c[0], c[1], c[2], c[3])) : throw new ProtocolException("Time must be 4 bytes."),
            BacnetApplicationTag.ObjectIdentifier => c.Length == 4 ? BacnetValue.Object(BacnetObjectId.FromRaw(BinaryPrimitives.ReadUInt32BigEndian(c))) : throw new ProtocolException("Object identifier must be 4 bytes."),
            _ => throw new ProtocolException($"Unknown application tag {h.Number}."),
        };
    }

    /// <summary>Skips one tagged element, including everything between an opening and its closing tag.</summary>
    public void Skip()
    {
        var h = Peek();
        if (h.Opening)
        {
            _pos += h.HeaderSize;
            while (!IsClosing(h.Number)) Skip();
            _pos += Peek().HeaderSize;
            return;
        }

        if (!h.Context && h.Number == (int)BacnetApplicationTag.Boolean)
        {
            _pos += h.HeaderSize;
            return;
        }

        _pos += h.HeaderSize + h.Length;
    }

    /// <summary>The remaining bytes.</summary>
    public readonly ReadOnlySpan<byte> Rest => _b[_pos..];

    private static ulong Unsigned(ReadOnlySpan<byte> c)
    {
        if (c.Length is 0 or > 8) throw new ProtocolException($"An unsigned integer of {c.Length} byte(s) is invalid.");
        ulong v = 0;
        foreach (var x in c) v = (v << 8) | x;
        return v;
    }

    private static long Signed(ReadOnlySpan<byte> c)
    {
        if (c.Length is 0 or > 8) throw new ProtocolException($"A signed integer of {c.Length} byte(s) is invalid.");
        long v = (sbyte)c[0];
        for (var i = 1; i < c.Length; i++) v = (v << 8) | c[i];
        return v;
    }

    private static string Text(ReadOnlySpan<byte> c)
    {
        if (c.Length == 0) throw new ProtocolException("A character string needs its character-set byte.");
        return c[0] switch
        {
            0 => Encoding.UTF8.GetString(c[1..]),
            5 => Encoding.Latin1.GetString(c[1..]),
            4 => Encoding.BigEndianUnicode.GetString(c[1..]),
            _ => throw new ProtocolException($"Character set {c[0]} is not supported."),
        };
    }

    private static BacnetBitString BitString(ReadOnlySpan<byte> c)
    {
        if (c.Length == 0 || c[0] > 7) throw new ProtocolException("Invalid bit string.");
        var count = ((c.Length - 1) * 8) - c[0];
        var bits = new bool[Math.Max(0, count)];
        for (var i = 0; i < bits.Length; i++) bits[i] = (c[1 + (i / 8)] & (0x80 >> (i % 8))) != 0;
        return new BacnetBitString(bits);
    }
}
