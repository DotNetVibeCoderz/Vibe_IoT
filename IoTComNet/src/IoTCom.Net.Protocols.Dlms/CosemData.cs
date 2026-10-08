using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace IoTCom.Net.Protocols.Dlms;

/// <summary>A-XDR data type tags (IEC 62056-6-2 "Data" CHOICE).</summary>
public enum CosemDataType : byte
{
    /// <summary>null-data.</summary>
    Null = 0,
    /// <summary>array (SEQUENCE OF Data, all of one type).</summary>
    Array = 1,
    /// <summary>structure.</summary>
    Structure = 2,
    /// <summary>boolean.</summary>
    Boolean = 3,
    /// <summary>bit-string.</summary>
    BitString = 4,
    /// <summary>double-long (int32).</summary>
    Int32 = 5,
    /// <summary>double-long-unsigned (uint32).</summary>
    UInt32 = 6,
    /// <summary>octet-string.</summary>
    OctetString = 9,
    /// <summary>visible-string (ASCII).</summary>
    VisibleString = 10,
    /// <summary>utf8-string.</summary>
    Utf8String = 12,
    /// <summary>bcd (one byte).</summary>
    Bcd = 13,
    /// <summary>integer (int8).</summary>
    Int8 = 15,
    /// <summary>long (int16).</summary>
    Int16 = 16,
    /// <summary>unsigned (uint8).</summary>
    UInt8 = 17,
    /// <summary>long-unsigned (uint16).</summary>
    UInt16 = 18,
    /// <summary>long64.</summary>
    Int64 = 20,
    /// <summary>long64-unsigned.</summary>
    UInt64 = 21,
    /// <summary>enum (uint8).</summary>
    Enum = 22,
    /// <summary>float32.</summary>
    Float32 = 23,
    /// <summary>float64.</summary>
    Float64 = 24,
    /// <summary>date-time (12 bytes).</summary>
    DateTime = 25,
    /// <summary>date (5 bytes).</summary>
    Date = 26,
    /// <summary>time (4 bytes).</summary>
    Time = 27,
}

/// <summary>
/// A COSEM data value: the A-XDR tagged union used for attribute values, method parameters and profile buffers.
/// Immutable; build values with the static factories and read them with the <c>As…</c> accessors.
/// </summary>
public sealed class CosemData : IEquatable<CosemData>
{
    private readonly long _integer;
    private readonly double _real;
    private readonly byte[]? _bytes;
    private readonly CosemData[]? _items;

    private CosemData(CosemDataType type, long integer = 0, double real = 0, byte[]? bytes = null, CosemData[]? items = null, int bitLength = 0)
    {
        Type = type;
        _integer = integer;
        _real = real;
        _bytes = bytes;
        _items = items;
        BitLength = bitLength;
    }

    /// <summary>The type tag.</summary>
    public CosemDataType Type { get; }

    /// <summary>Number of bits of a bit-string.</summary>
    public int BitLength { get; }

    /// <summary>The null value.</summary>
    public static CosemData Null { get; } = new(CosemDataType.Null);

    /// <summary>A boolean.</summary>
    public static CosemData Boolean(bool value) => new(CosemDataType.Boolean, value ? 1 : 0);

    /// <summary>An integer of the given type (Int8 … UInt64, Enum, Bcd).</summary>
    public static CosemData Integer(CosemDataType type, long value)
    {
        var (min, max) = type switch
        {
            CosemDataType.Int8 => (sbyte.MinValue, sbyte.MaxValue),
            CosemDataType.UInt8 or CosemDataType.Enum or CosemDataType.Bcd => (0, byte.MaxValue),
            CosemDataType.Int16 => (short.MinValue, short.MaxValue),
            CosemDataType.UInt16 => (0, ushort.MaxValue),
            CosemDataType.Int32 => (int.MinValue, int.MaxValue),
            CosemDataType.UInt32 => (0, uint.MaxValue),
            CosemDataType.Int64 or CosemDataType.UInt64 => (long.MinValue, long.MaxValue),
            _ => throw new ArgumentException($"{type} is not an integer type.", nameof(type)),
        };
        if (value < min || value > max) throw new ArgumentOutOfRangeException(nameof(value), $"{value} does not fit {type}.");
        return new CosemData(type, value);
    }

    /// <summary>An int8 (<c>integer</c>).</summary>
    public static CosemData Int8(sbyte value) => new(CosemDataType.Int8, value);

    /// <summary>An int16 (<c>long</c>).</summary>
    public static CosemData Int16(short value) => new(CosemDataType.Int16, value);

    /// <summary>An int32 (<c>double-long</c>).</summary>
    public static CosemData Int32(int value) => new(CosemDataType.Int32, value);

    /// <summary>An int64 (<c>long64</c>).</summary>
    public static CosemData Int64(long value) => new(CosemDataType.Int64, value);

    /// <summary>A uint8 (<c>unsigned</c>).</summary>
    public static CosemData UInt8(byte value) => new(CosemDataType.UInt8, value);

    /// <summary>A uint16 (<c>long-unsigned</c>).</summary>
    public static CosemData UInt16(ushort value) => new(CosemDataType.UInt16, value);

    /// <summary>A uint32 (<c>double-long-unsigned</c>).</summary>
    public static CosemData UInt32(uint value) => new(CosemDataType.UInt32, value);

    /// <summary>A uint64 (<c>long64-unsigned</c>), stored as its bit pattern.</summary>
    public static CosemData UInt64(ulong value) => new(CosemDataType.UInt64, unchecked((long)value));

    /// <summary>An enum.</summary>
    public static CosemData Enum(byte value) => new(CosemDataType.Enum, value);

    /// <summary>A float32.</summary>
    public static CosemData Float32(float value) => new(CosemDataType.Float32, real: value);

    /// <summary>A float64.</summary>
    public static CosemData Float64(double value) => new(CosemDataType.Float64, real: value);

    /// <summary>An octet-string.</summary>
    public static CosemData OctetString(ReadOnlySpan<byte> value) => new(CosemDataType.OctetString, bytes: value.ToArray());

    /// <summary>A visible-string.</summary>
    public static CosemData VisibleString(string value) => new(CosemDataType.VisibleString, bytes: Encoding.ASCII.GetBytes(value ?? ""));

    /// <summary>A utf8-string.</summary>
    public static CosemData Utf8String(string value) => new(CosemDataType.Utf8String, bytes: Encoding.UTF8.GetBytes(value ?? ""));

    /// <summary>A bit-string of <paramref name="bitLength"/> bits (most significant bit first).</summary>
    public static CosemData BitString(ReadOnlySpan<byte> bits, int bitLength)
    {
        if (bitLength < 0 || (bitLength + 7) / 8 != bits.Length) throw new ArgumentException("The byte count must match the bit length.", nameof(bitLength));
        return new CosemData(CosemDataType.BitString, bytes: bits.ToArray(), bitLength: bitLength);
    }

    /// <summary>A date-time as an octet-string (the usual encoding of Clock.time and profile timestamps).</summary>
    public static CosemData DateTime(DateTimeOffset time) => OctetString(CosemDateTime.Encode(time));

    /// <summary>A date-time with the dedicated tag 25.</summary>
    public static CosemData TaggedDateTime(DateTimeOffset time) => new(CosemDataType.DateTime, bytes: CosemDateTime.Encode(time));

    /// <summary>An array (items should share one type).</summary>
    public static CosemData Array(params CosemData[] items) => new(CosemDataType.Array, items: items ?? []);

    /// <summary>An array.</summary>
    public static CosemData Array(IEnumerable<CosemData> items) => new(CosemDataType.Array, items: [.. items]);

    /// <summary>A structure.</summary>
    public static CosemData Structure(params CosemData[] items) => new(CosemDataType.Structure, items: items ?? []);

    /// <summary>Items of an array or structure.</summary>
    public IReadOnlyList<CosemData> Items => _items ?? [];

    /// <summary>Item <paramref name="index"/> of an array or structure.</summary>
    public CosemData this[int index] => Items[index];

    /// <summary>True for integer-like types (including enum, bcd and boolean).</summary>
    public bool IsInteger => Type is CosemDataType.Boolean or CosemDataType.Int8 or CosemDataType.Int16 or CosemDataType.Int32 or CosemDataType.Int64
        or CosemDataType.UInt8 or CosemDataType.UInt16 or CosemDataType.UInt32 or CosemDataType.UInt64 or CosemDataType.Enum or CosemDataType.Bcd;

    /// <summary>The value as a 64-bit integer.</summary>
    public long AsInt64() => IsInteger ? _integer : Type is CosemDataType.Float32 or CosemDataType.Float64 ? (long)_real : throw Mismatch("an integer");

    /// <summary>The value as a double (integers and floats).</summary>
    public double AsDouble() => Type switch
    {
        CosemDataType.Float32 or CosemDataType.Float64 => _real,
        CosemDataType.UInt64 => unchecked((ulong)_integer),
        _ when IsInteger => _integer,
        _ => throw Mismatch("a number"),
    };

    /// <summary>The value as a boolean.</summary>
    public bool AsBoolean() => Type == CosemDataType.Boolean ? _integer != 0 : throw Mismatch("a boolean");

    /// <summary>The raw bytes of an octet-string, string, bit-string or date-time.</summary>
    public byte[] AsBytes() => _bytes is { } b ? (byte[])b.Clone() : throw Mismatch("bytes");

    /// <summary>The text of a visible/utf8 string (or an ASCII octet-string).</summary>
    public string AsString() => Type switch
    {
        CosemDataType.VisibleString or CosemDataType.OctetString => Encoding.ASCII.GetString(_bytes!),
        CosemDataType.Utf8String => Encoding.UTF8.GetString(_bytes!),
        _ => throw Mismatch("a string"),
    };

    /// <summary>The date-time of a 12-byte octet-string or date-time value.</summary>
    public DateTimeOffset? AsDateTime() => _bytes is { Length: 12 } b && Type is CosemDataType.OctetString or CosemDataType.DateTime ? CosemDateTime.Decode(b) : null;

    /// <summary>The logical name of a 6-byte octet-string.</summary>
    public ObisCode AsObis() => Type == CosemDataType.OctetString && _bytes is { Length: 6 } b ? ObisCode.FromBytes(b) : throw Mismatch("a logical name");

    private InvalidCastException Mismatch(string what) => new($"COSEM {Type} is not {what}.");

    // ---- A-XDR ---------------------------------------------------------------------------------------------

    /// <summary>Encodes the value (tag + content).</summary>
    public byte[] Encode()
    {
        var buffer = new List<byte>(16);
        Write(buffer);
        return [.. buffer];
    }

    /// <summary>Appends the encoding to <paramref name="buffer"/>.</summary>
    public void Write(List<byte> buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        buffer.Add((byte)Type);
        Span<byte> tmp = stackalloc byte[8];
        switch (Type)
        {
            case CosemDataType.Null:
                break;
            case CosemDataType.Array or CosemDataType.Structure:
                AxdrLength.Write(buffer, Items.Count);
                foreach (var item in Items) item.Write(buffer);
                break;
            case CosemDataType.Boolean:
                buffer.Add(_integer != 0 ? (byte)0xFF : (byte)0x00);
                break;
            case CosemDataType.BitString:
                AxdrLength.Write(buffer, BitLength);
                buffer.AddRange(_bytes!);
                break;
            case CosemDataType.OctetString or CosemDataType.VisibleString or CosemDataType.Utf8String:
                AxdrLength.Write(buffer, _bytes!.Length);
                buffer.AddRange(_bytes);
                break;
            case CosemDataType.DateTime or CosemDataType.Date or CosemDataType.Time:
                buffer.AddRange(_bytes!);
                break;
            case CosemDataType.Int8 or CosemDataType.UInt8 or CosemDataType.Enum or CosemDataType.Bcd:
                buffer.Add(unchecked((byte)_integer));
                break;
            case CosemDataType.Int16 or CosemDataType.UInt16:
                BinaryPrimitives.WriteUInt16BigEndian(tmp, unchecked((ushort)_integer));
                buffer.AddRange(tmp[..2]);
                break;
            case CosemDataType.Int32 or CosemDataType.UInt32:
                BinaryPrimitives.WriteUInt32BigEndian(tmp, unchecked((uint)_integer));
                buffer.AddRange(tmp[..4]);
                break;
            case CosemDataType.Int64 or CosemDataType.UInt64:
                BinaryPrimitives.WriteInt64BigEndian(tmp, _integer);
                buffer.AddRange(tmp[..8]);
                break;
            case CosemDataType.Float32:
                BinaryPrimitives.WriteSingleBigEndian(tmp, (float)_real);
                buffer.AddRange(tmp[..4]);
                break;
            case CosemDataType.Float64:
                BinaryPrimitives.WriteDoubleBigEndian(tmp, _real);
                buffer.AddRange(tmp[..8]);
                break;
            default:
                throw new InvalidOperationException($"Cannot encode {Type}.");
        }
    }

    /// <summary>Decodes one value at the start of <paramref name="data"/>; never throws for malformed input.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> data, out CosemData? value, out int consumed, out string? error)
    {
        value = null;
        consumed = 0;
        error = null;
        try
        {
            var pos = 0;
            value = Read(data, ref pos, 0);
            consumed = pos;
            return true;
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Decodes one value that must span <paramref name="data"/> exactly.</summary>
    public static CosemData Decode(ReadOnlySpan<byte> data)
    {
        if (!TryDecode(data, out var v, out var n, out var e)) throw new FormatException(e);
        if (n != data.Length) throw new FormatException($"{data.Length - n} trailing byte(s) after the COSEM value.");
        return v!;
    }

    internal static CosemData Read(ReadOnlySpan<byte> data, ref int pos, int depth)
    {
        if (depth > 32) throw new FormatException("COSEM data nested deeper than 32 levels.");
        if (pos >= data.Length) throw new FormatException("Truncated COSEM data (missing tag).");
        var type = (CosemDataType)data[pos++];
        ReadOnlySpan<byte> Take(ref int p, int n, ReadOnlySpan<byte> d)
        {
            if (n < 0 || p + n > d.Length) throw new FormatException($"Truncated COSEM {type}: {n} byte(s) needed.");
            var s = d.Slice(p, n);
            p += n;
            return s;
        }

        switch (type)
        {
            case CosemDataType.Null:
                return Null;
            case CosemDataType.Array or CosemDataType.Structure:
            {
                var count = AxdrLength.Read(data, ref pos);
                if (count > data.Length - pos) throw new FormatException($"COSEM {type} claims {count} items but only {data.Length - pos} bytes remain.");
                var items = new CosemData[count];
                for (var i = 0; i < count; i++) items[i] = Read(data, ref pos, depth + 1);
                return new CosemData(type, items: items);
            }
            case CosemDataType.Boolean:
                return Boolean(Take(ref pos, 1, data)[0] != 0);
            case CosemDataType.BitString:
            {
                var bits = AxdrLength.Read(data, ref pos);
                return new CosemData(type, bytes: Take(ref pos, (bits + 7) / 8, data).ToArray(), bitLength: bits);
            }
            case CosemDataType.OctetString or CosemDataType.VisibleString or CosemDataType.Utf8String:
            {
                var n = AxdrLength.Read(data, ref pos);
                return new CosemData(type, bytes: Take(ref pos, n, data).ToArray());
            }
            case CosemDataType.DateTime:
                return new CosemData(type, bytes: Take(ref pos, 12, data).ToArray());
            case CosemDataType.Date:
                return new CosemData(type, bytes: Take(ref pos, 5, data).ToArray());
            case CosemDataType.Time:
                return new CosemData(type, bytes: Take(ref pos, 4, data).ToArray());
            case CosemDataType.Int8:
                return new CosemData(type, (sbyte)Take(ref pos, 1, data)[0]);
            case CosemDataType.UInt8 or CosemDataType.Enum or CosemDataType.Bcd:
                return new CosemData(type, Take(ref pos, 1, data)[0]);
            case CosemDataType.Int16:
                return new CosemData(type, BinaryPrimitives.ReadInt16BigEndian(Take(ref pos, 2, data)));
            case CosemDataType.UInt16:
                return new CosemData(type, BinaryPrimitives.ReadUInt16BigEndian(Take(ref pos, 2, data)));
            case CosemDataType.Int32:
                return new CosemData(type, BinaryPrimitives.ReadInt32BigEndian(Take(ref pos, 4, data)));
            case CosemDataType.UInt32:
                return new CosemData(type, BinaryPrimitives.ReadUInt32BigEndian(Take(ref pos, 4, data)));
            case CosemDataType.Int64 or CosemDataType.UInt64:
                return new CosemData(type, BinaryPrimitives.ReadInt64BigEndian(Take(ref pos, 8, data)));
            case CosemDataType.Float32:
                return new CosemData(type, real: BinaryPrimitives.ReadSingleBigEndian(Take(ref pos, 4, data)));
            case CosemDataType.Float64:
                return new CosemData(type, real: BinaryPrimitives.ReadDoubleBigEndian(Take(ref pos, 8, data)));
            default:
                throw new FormatException($"Unsupported COSEM data tag {(byte)type}.");
        }
    }

    /// <inheritdoc />
    public bool Equals(CosemData? other)
    {
        if (other is null || other.Type != Type || other.BitLength != BitLength || other._integer != _integer) return false;
        if (BitConverter.DoubleToInt64Bits(other._real) != BitConverter.DoubleToInt64Bits(_real)) return false;
        if (!(_bytes ?? []).AsSpan().SequenceEqual(other._bytes ?? [])) return false;
        return Items.Count == other.Items.Count && Items.Zip(other.Items).All(p => p.First.Equals(p.Second));
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is CosemData d && Equals(d);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Type, _integer, _real, _bytes?.Length ?? 0, Items.Count);

    /// <inheritdoc />
    public override string ToString() => Type switch
    {
        CosemDataType.Null => "null",
        CosemDataType.Array => $"[{string.Join(", ", Items)}]",
        CosemDataType.Structure => $"{{{string.Join(", ", Items)}}}",
        CosemDataType.Boolean => _integer != 0 ? "true" : "false",
        CosemDataType.UInt64 => unchecked((ulong)_integer).ToString(CultureInfo.InvariantCulture),
        CosemDataType.Float32 or CosemDataType.Float64 => _real.ToString("G7", CultureInfo.InvariantCulture),
        CosemDataType.VisibleString or CosemDataType.Utf8String => $"\"{AsString()}\"",
        CosemDataType.OctetString when _bytes!.Length == 12 && AsDateTime() is { } t => t.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
        CosemDataType.OctetString when _bytes!.Length == 6 => ObisCode.FromBytes(_bytes).ToString(),
        CosemDataType.OctetString when _bytes!.Length > 0 && _bytes.All(b => b is >= 0x20 and < 0x7F) => $"\"{Encoding.ASCII.GetString(_bytes)}\"",
        CosemDataType.OctetString or CosemDataType.BitString or CosemDataType.Date or CosemDataType.Time => Convert.ToHexString(_bytes!),
        CosemDataType.DateTime => AsDateTime()?.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture) ?? Convert.ToHexString(_bytes!),
        _ => _integer.ToString(CultureInfo.InvariantCulture),
    };
}

/// <summary>A-XDR length/count encoding: one byte below 128, otherwise 0x80 | n followed by n big-endian bytes.</summary>
public static class AxdrLength
{
    /// <summary>Appends a length.</summary>
    public static void Write(List<byte> buffer, int length)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (length < 0x80) buffer.Add((byte)length);
        else if (length <= 0xFF) buffer.AddRange([0x81, (byte)length]);
        else if (length <= 0xFFFF) buffer.AddRange([0x82, (byte)(length >> 8), (byte)length]);
        else buffer.AddRange([0x84, (byte)(length >> 24), (byte)(length >> 16), (byte)(length >> 8), (byte)length]);
    }

    /// <summary>Reads a length at <paramref name="pos"/>.</summary>
    public static int Read(ReadOnlySpan<byte> data, ref int pos)
    {
        if (pos >= data.Length) throw new FormatException("Truncated A-XDR length.");
        var first = data[pos++];
        if (first < 0x80) return first;
        var n = first & 0x7F;
        if (n is 0 or > 4 || pos + n > data.Length) throw new FormatException("Invalid A-XDR length.");
        long value = 0;
        for (var i = 0; i < n; i++) value = (value << 8) | data[pos++];
        if (value > int.MaxValue) throw new FormatException("A-XDR length too large.");
        return (int)value;
    }
}

/// <summary>COSEM date-time (12 bytes): year, month, day, weekday, hour, minute, second, hundredths, deviation, status.</summary>
public static class CosemDateTime
{
    /// <summary>Encodes a time with its UTC offset as the deviation.</summary>
    public static byte[] Encode(DateTimeOffset time)
    {
        var b = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)time.Year);
        b[2] = (byte)time.Month;
        b[3] = (byte)time.Day;
        b[4] = time.DayOfWeek == DayOfWeek.Sunday ? (byte)7 : (byte)time.DayOfWeek;
        b[5] = (byte)time.Hour;
        b[6] = (byte)time.Minute;
        b[7] = (byte)time.Second;
        b[8] = (byte)(time.Millisecond / 10);
        // Deviation: minutes from local time to UTC (UTC = local + deviation), so UTC+07:00 is -420.
        BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(9), (short)-time.Offset.TotalMinutes);
        b[11] = 0x00;
        return b;
    }

    /// <summary>Decodes a date-time; unspecified fields (0xFF) become 0/1 and an unspecified deviation means UTC.</summary>
    public static DateTimeOffset? Decode(ReadOnlySpan<byte> b)
    {
        if (b.Length != 12) return null;
        var year = BinaryPrimitives.ReadUInt16BigEndian(b);
        if (year == 0xFFFF || b[2] is 0 or > 12 and < 0xFD || b[3] is 0 or > 31 and < 0xFD) return null;
        int Field(byte v, int fallback) => v == 0xFF ? fallback : v;
        var deviation = BinaryPrimitives.ReadInt16BigEndian(b[9..]);
        var offset = deviation == unchecked((short)0x8000) ? TimeSpan.Zero : TimeSpan.FromMinutes(-deviation);
        try
        {
            return new DateTimeOffset(year, Field(b[2], 1), Field(b[3], 1), Field(b[5], 0), Field(b[6], 0), Field(b[7], 0), Field(b[8], 0) * 10, offset);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
