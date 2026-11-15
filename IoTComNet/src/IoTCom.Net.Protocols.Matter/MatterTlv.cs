using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace IoTCom.Net.Protocols.Matter;

/// <summary>TLV element types (Matter core specification, appendix A).</summary>
public enum TlvType : byte
{
    /// <summary>Signed integer.</summary>
    SignedInt,
    /// <summary>Unsigned integer.</summary>
    UnsignedInt,
    /// <summary>Boolean.</summary>
    Bool,
    /// <summary>IEEE 754 single.</summary>
    Float,
    /// <summary>IEEE 754 double.</summary>
    Double,
    /// <summary>UTF-8 string.</summary>
    Utf8,
    /// <summary>Octet string.</summary>
    Bytes,
    /// <summary>Null.</summary>
    Null,
    /// <summary>Structure (members have unique tags).</summary>
    Structure,
    /// <summary>Array (members are anonymous).</summary>
    Array,
    /// <summary>List (ordered, tags allowed).</summary>
    List,
}

/// <summary>Tag forms.</summary>
public enum TlvTagKind : byte
{
    /// <summary>No tag.</summary>
    Anonymous,
    /// <summary>Context-specific (0–255).</summary>
    Context,
    /// <summary>Common profile.</summary>
    CommonProfile,
    /// <summary>Implicit profile.</summary>
    ImplicitProfile,
    /// <summary>Fully qualified (vendor, profile, tag).</summary>
    FullyQualified,
}

/// <summary>A TLV tag.</summary>
/// <param name="Kind">Form.</param>
/// <param name="Number">Tag number.</param>
/// <param name="Vendor">Vendor id (fully qualified).</param>
/// <param name="Profile">Profile number (fully qualified).</param>
public readonly record struct TlvTag(TlvTagKind Kind, uint Number = 0, ushort Vendor = 0, ushort Profile = 0)
{
    /// <summary>No tag.</summary>
    public static TlvTag Anonymous => default;

    /// <summary>A context tag.</summary>
    public static TlvTag Context(byte number) => new(TlvTagKind.Context, number);

    /// <inheritdoc />
    public override string ToString() => Kind switch
    {
        TlvTagKind.Anonymous => "",
        TlvTagKind.Context => Number.ToString(CultureInfo.InvariantCulture),
        TlvTagKind.CommonProfile => $"common:{Number}",
        TlvTagKind.ImplicitProfile => $"implicit:{Number}",
        _ => $"{Vendor:X4}:{Profile:X4}:{Number}",
    };
}

/// <summary>A TLV element: a scalar, or a container with members.</summary>
public sealed class TlvElement
{
    private readonly List<TlvElement>? _children;

    private TlvElement(TlvTag tag, TlvType type, object? value, List<TlvElement>? children)
    {
        Tag = tag;
        Type = type;
        Value = value;
        _children = children;
    }

    /// <summary>Tag.</summary>
    public TlvTag Tag { get; }

    /// <summary>Type.</summary>
    public TlvType Type { get; }

    /// <summary>Scalar value: long, ulong, bool, float, double, string, byte[] or null.</summary>
    public object? Value { get; }

    /// <summary>Members of a container (empty for scalars).</summary>
    public IReadOnlyList<TlvElement> Children => _children ?? (IReadOnlyList<TlvElement>)[];

    /// <summary>True for structures, arrays and lists.</summary>
    public bool IsContainer => Type is TlvType.Structure or TlvType.Array or TlvType.List;

    /// <summary>The member with context tag <paramref name="tag"/>, or null.</summary>
    public TlvElement? this[byte tag] => Children.FirstOrDefault(c => c.Tag.Kind == TlvTagKind.Context && c.Tag.Number == tag);

    /// <summary>Required member with context tag <paramref name="tag"/>.</summary>
    public TlvElement Required(byte tag) => this[tag] ?? throw new ProtocolException($"TLV container {Tag} has no member {tag}.");

    /// <summary>Unsigned value.</summary>
    public ulong AsUInt() => Value switch
    {
        ulong u => u,
        long l when l >= 0 => (ulong)l,
        _ => throw new ProtocolException($"TLV element {Tag} is {Type}, not an unsigned integer."),
    };

    /// <summary>Signed value.</summary>
    public long AsInt() => Value switch
    {
        long l => l,
        ulong u when u <= long.MaxValue => (long)u,
        _ => throw new ProtocolException($"TLV element {Tag} is {Type}, not an integer."),
    };

    /// <summary>Boolean value.</summary>
    public bool AsBool() => Value as bool? ?? throw new ProtocolException($"TLV element {Tag} is {Type}, not a boolean.");

    /// <summary>String value.</summary>
    public string AsString() => Value as string ?? throw new ProtocolException($"TLV element {Tag} is {Type}, not a string.");

    /// <summary>Octet string value.</summary>
    public byte[] AsBytes() => Value as byte[] ?? throw new ProtocolException($"TLV element {Tag} is {Type}, not an octet string.");

    /// <summary>Unsigned integer.</summary>
    public static TlvElement UInt(TlvTag tag, ulong value) => new(tag, TlvType.UnsignedInt, value, null);

    /// <summary>Signed integer.</summary>
    public static TlvElement Int(TlvTag tag, long value) => new(tag, TlvType.SignedInt, value, null);

    /// <summary>Boolean.</summary>
    public static TlvElement Bool(TlvTag tag, bool value) => new(tag, TlvType.Bool, value, null);

    /// <summary>Single.</summary>
    public static TlvElement Float(TlvTag tag, float value) => new(tag, TlvType.Float, value, null);

    /// <summary>Double.</summary>
    public static TlvElement Double(TlvTag tag, double value) => new(tag, TlvType.Double, value, null);

    /// <summary>UTF-8 string.</summary>
    public static TlvElement String(TlvTag tag, string value) => new(tag, TlvType.Utf8, value, null);

    /// <summary>Octet string.</summary>
    public static TlvElement Bytes(TlvTag tag, byte[] value) => new(tag, TlvType.Bytes, value, null);

    /// <summary>Null.</summary>
    public static TlvElement Null(TlvTag tag) => new(tag, TlvType.Null, null, null);

    /// <summary>Structure.</summary>
    public static TlvElement Structure(TlvTag tag, params TlvElement[] members) => new(tag, TlvType.Structure, null, [.. members]);

    /// <summary>Array.</summary>
    public static TlvElement Array(TlvTag tag, params TlvElement[] members) => new(tag, TlvType.Array, null, [.. members]);

    /// <summary>List.</summary>
    public static TlvElement List(TlvTag tag, params TlvElement[] members) => new(tag, TlvType.List, null, [.. members]);

    /// <summary>The same element with another tag.</summary>
    public TlvElement WithTag(TlvTag tag) => new(tag, Type, Value, _children);

    internal static TlvElement Container(TlvTag tag, TlvType type, List<TlvElement> members) => new(tag, type, null, members);

    internal static TlvElement Scalar(TlvTag tag, TlvType type, object? value) => new(tag, type, value, null);

    /// <inheritdoc />
    public override string ToString() => Tlv.Describe(this);
}

/// <summary>Matter TLV encoding: smallest integer and length forms on write, all forms on read.</summary>
public static class Tlv
{
    private const byte EndOfContainer = 0x18;

    /// <summary>Encodes one element (and its members).</summary>
    public static byte[] Encode(TlvElement element)
    {
        var b = new List<byte>();
        Write(b, element);
        return [.. b];
    }

    private static void Write(List<byte> b, TlvElement e)
    {
        var (tagControl, tagBytes) = TagBytes(e.Tag);
        void Head(byte type)
        {
            b.Add((byte)((tagControl << 5) | type));
            b.AddRange(tagBytes);
        }

        void Le(ulong v, int size)
        {
            for (var i = 0; i < size; i++) b.Add((byte)(v >> (8 * i)));
        }

        switch (e.Type)
        {
            case TlvType.SignedInt:
                var s = (long)e.Value!;
                var ssize = s is >= sbyte.MinValue and <= sbyte.MaxValue ? 0 : s is >= short.MinValue and <= short.MaxValue ? 1 : s is >= int.MinValue and <= int.MaxValue ? 2 : 3;
                Head((byte)ssize);
                Le((ulong)s, 1 << ssize);
                break;
            case TlvType.UnsignedInt:
                var u = (ulong)e.Value!;
                var usize = u <= byte.MaxValue ? 0 : u <= ushort.MaxValue ? 1 : u <= uint.MaxValue ? 2 : 3;
                Head((byte)(4 + usize));
                Le(u, 1 << usize);
                break;
            case TlvType.Bool:
                Head((bool)e.Value! ? (byte)0x09 : (byte)0x08);
                break;
            case TlvType.Float:
                Head(0x0A);
                Le(BitConverter.SingleToUInt32Bits((float)e.Value!), 4);
                break;
            case TlvType.Double:
                Head(0x0B);
                Le(BitConverter.DoubleToUInt64Bits((double)e.Value!), 8);
                break;
            case TlvType.Utf8 or TlvType.Bytes:
                var data = e.Type == TlvType.Utf8 ? Encoding.UTF8.GetBytes((string)e.Value!) : (byte[])e.Value!;
                var lsize = data.Length <= byte.MaxValue ? 0 : data.Length <= ushort.MaxValue ? 1 : 2;
                Head((byte)((e.Type == TlvType.Utf8 ? 0x0C : 0x10) + lsize));
                Le((ulong)data.Length, 1 << lsize);
                b.AddRange(data);
                break;
            case TlvType.Null:
                Head(0x14);
                break;
            default:
                Head(e.Type switch { TlvType.Structure => 0x15, TlvType.Array => 0x16, _ => 0x17 });
                foreach (var c in e.Children) Write(b, c);
                b.Add(EndOfContainer);
                break;
        }
    }

    private static (byte Control, byte[] Bytes) TagBytes(TlvTag t) => t.Kind switch
    {
        TlvTagKind.Anonymous => (0, []),
        TlvTagKind.Context => t.Number <= byte.MaxValue ? ((byte)1, [(byte)t.Number]) : throw new ArgumentException("Context tags are 0–255."),
        TlvTagKind.CommonProfile => t.Number <= ushort.MaxValue ? ((byte)2, BitConverter.GetBytes((ushort)t.Number)) : ((byte)3, BitConverter.GetBytes(t.Number)),
        TlvTagKind.ImplicitProfile => t.Number <= ushort.MaxValue ? ((byte)4, BitConverter.GetBytes((ushort)t.Number)) : ((byte)5, BitConverter.GetBytes(t.Number)),
        _ => t.Number <= ushort.MaxValue
            ? ((byte)6, [.. BitConverter.GetBytes(t.Vendor), .. BitConverter.GetBytes(t.Profile), .. BitConverter.GetBytes((ushort)t.Number)])
            : ((byte)7, [.. BitConverter.GetBytes(t.Vendor), .. BitConverter.GetBytes(t.Profile), .. BitConverter.GetBytes(t.Number)]),
    };

    /// <summary>Decodes exactly one element spanning the whole input.</summary>
    /// <exception cref="ProtocolException">Malformed TLV, trailing bytes, nesting deeper than 32.</exception>
    public static TlvElement Decode(ReadOnlySpan<byte> data)
    {
        var at = 0;
        var e = Read(data, ref at, 0) ?? throw new ProtocolException("End-of-container outside a container.");
        if (at != data.Length) throw new ProtocolException($"{data.Length - at} bytes follow the TLV element.");
        return e;
    }

    /// <summary>Decodes one element starting at <paramref name="at"/>; returns the element and advances.</summary>
    public static TlvElement DecodeOne(ReadOnlySpan<byte> data, ref int at) => Read(data, ref at, 0) ?? throw new ProtocolException("End-of-container outside a container.");

    private static ulong Le(ReadOnlySpan<byte> d, ref int at, int size)
    {
        if (d.Length - at < size) throw new ProtocolException($"TLV truncated: {size} bytes needed at offset {at}.");
        ulong v = 0;
        for (var i = 0; i < size; i++) v |= (ulong)d[at + i] << (8 * i);
        at += size;
        return v;
    }

    private static TlvElement? Read(ReadOnlySpan<byte> d, ref int at, int depth)
    {
        if (depth > 32) throw new ProtocolException("TLV nesting deeper than 32 levels.");
        if (at >= d.Length) throw new ProtocolException("TLV truncated: element expected.");
        var control = d[at++];
        var type = control & 0x1F;
        if (type == EndOfContainer)
        {
            if (control != EndOfContainer) throw new ProtocolException("End-of-container must not carry a tag.");
            return null;
        }

        if (type > 0x18) throw new ProtocolException($"Reserved TLV element type 0x{type:X2}.");
        var tag = (control >> 5) switch
        {
            0 => TlvTag.Anonymous,
            1 => TlvTag.Context((byte)Le(d, ref at, 1)),
            2 => new TlvTag(TlvTagKind.CommonProfile, (uint)Le(d, ref at, 2)),
            3 => new TlvTag(TlvTagKind.CommonProfile, (uint)Le(d, ref at, 4)),
            4 => new TlvTag(TlvTagKind.ImplicitProfile, (uint)Le(d, ref at, 2)),
            5 => new TlvTag(TlvTagKind.ImplicitProfile, (uint)Le(d, ref at, 4)),
            6 => FullyQualified(d, ref at, 2),
            _ => FullyQualified(d, ref at, 4),
        };
        switch (type)
        {
            case <= 0x03:
                var size = 1 << type;
                var raw = Le(d, ref at, size);
                var shift = 64 - (8 * size);
                return TlvElement.Scalar(tag, TlvType.SignedInt, (long)(raw << shift) >> shift);
            case <= 0x07:
                return TlvElement.Scalar(tag, TlvType.UnsignedInt, Le(d, ref at, 1 << (type - 4)));
            case 0x08 or 0x09:
                return TlvElement.Scalar(tag, TlvType.Bool, type == 0x09);
            case 0x0A:
                return TlvElement.Scalar(tag, TlvType.Float, BitConverter.UInt32BitsToSingle((uint)Le(d, ref at, 4)));
            case 0x0B:
                return TlvElement.Scalar(tag, TlvType.Double, BitConverter.UInt64BitsToDouble(Le(d, ref at, 8)));
            case <= 0x13:
                var utf8 = type <= 0x0F;
                var length = Le(d, ref at, 1 << ((type - (utf8 ? 0x0C : 0x10)) & 3));
                if (length > (ulong)(d.Length - at)) throw new ProtocolException($"TLV string announces {length} bytes; {d.Length - at} present.");
                var bytes = d.Slice(at, (int)length).ToArray();
                at += (int)length;
                if (!utf8) return TlvElement.Scalar(tag, TlvType.Bytes, bytes);
                try
                {
                    return TlvElement.Scalar(tag, TlvType.Utf8, new UTF8Encoding(false, true).GetString(bytes));
                }
                catch (DecoderFallbackException)
                {
                    throw new ProtocolException("TLV UTF-8 string is not valid UTF-8.");
                }

            case 0x14:
                return TlvElement.Scalar(tag, TlvType.Null, null);
            default:
                var kind = type switch { 0x15 => TlvType.Structure, 0x16 => TlvType.Array, _ => TlvType.List };
                var members = new List<TlvElement>();
                while (true)
                {
                    var m = Read(d, ref at, depth + 1);
                    if (m is null) break;
                    if (kind == TlvType.Array && m.Tag.Kind != TlvTagKind.Anonymous) throw new ProtocolException("TLV array members must be anonymous.");
                    if (kind == TlvType.Structure && m.Tag.Kind == TlvTagKind.Anonymous) throw new ProtocolException("TLV structure members must be tagged.");
                    members.Add(m);
                }

                return TlvElement.Container(tag, kind, members);
        }
    }

    private static TlvTag FullyQualified(ReadOnlySpan<byte> d, ref int at, int size)
    {
        var vendor = (ushort)Le(d, ref at, 2);
        var profile = (ushort)Le(d, ref at, 2);
        return new TlvTag(TlvTagKind.FullyQualified, (uint)Le(d, ref at, size), vendor, profile);
    }

    /// <summary>Normalised description shared with the Rust twin, e.g. <c>{0=1,1="on",2=[true,null]}</c>.</summary>
    public static string Describe(TlvElement e)
    {
        var prefix = e.Tag.Kind == TlvTagKind.Anonymous ? "" : e.Tag + "=";
        return prefix + e.Type switch
        {
            TlvType.SignedInt => ((long)e.Value!).ToString(CultureInfo.InvariantCulture),
            TlvType.UnsignedInt => ((ulong)e.Value!).ToString(CultureInfo.InvariantCulture) + "u",
            TlvType.Bool => (bool)e.Value! ? "true" : "false",
            TlvType.Float => ((float)e.Value!).ToString("R", CultureInfo.InvariantCulture) + "f",
            TlvType.Double => ((double)e.Value!).ToString("R", CultureInfo.InvariantCulture) + "d",
            TlvType.Utf8 => "\"" + (string)e.Value! + "\"",
            TlvType.Bytes => "h'" + Convert.ToHexString((byte[])e.Value!) + "'",
            TlvType.Null => "null",
            TlvType.Structure => "{" + string.Join(',', e.Children.Select(Describe)) + "}",
            TlvType.Array => "[" + string.Join(',', e.Children.Select(Describe)) + "]",
            _ => "(" + string.Join(',', e.Children.Select(Describe)) + ")",
        };
    }
}
