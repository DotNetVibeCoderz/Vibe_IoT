using System.Buffers.Binary;
using System.Globalization;
using System.Text;

#pragma warning disable CS1591 // CIP names follow the ODVA specification.

namespace IoTCom.Net.Protocols.EtherNetIp;

/// <summary>CIP service codes (common services and the Logix tag services).</summary>
public enum CipService : byte
{
    GetAttributesAll = 0x01, SetAttributesAll = 0x02, GetAttributeList = 0x03, Reset = 0x05, MultipleServicePacket = 0x0A,
    GetAttributeSingle = 0x0E, SetAttributeSingle = 0x10, ForwardClose = 0x4E, ReadTag = 0x4C, WriteTag = 0x4D,
    UnconnectedSend = 0x52, ForwardOpen = 0x54,
}

/// <summary>CIP general status codes.</summary>
public enum CipStatus : byte
{
    Success = 0x00, ConnectionFailure = 0x01, ResourceUnavailable = 0x02, InvalidParameterValue = 0x03, PathSegmentError = 0x04,
    PathDestinationUnknown = 0x05, PartialTransfer = 0x06, ServiceNotSupported = 0x08, InvalidAttributeValue = 0x09,
    AlreadyInRequestedState = 0x0B, ObjectStateConflict = 0x0C, AttributeNotSettable = 0x0E, PrivilegeViolation = 0x0F,
    DeviceStateConflict = 0x10, ReplyDataTooLarge = 0x11, NotEnoughData = 0x13, AttributeNotSupported = 0x14, TooMuchData = 0x15,
    ObjectDoesNotExist = 0x16, EmbeddedServiceError = 0x1E, InvalidParameter = 0x20, PathSizeInvalid = 0x26, GeneralError = 0xFF,
}

/// <summary>Elementary CIP data types used by Logix tags.</summary>
public enum CipDataType : ushort
{
    Bool = 0xC1, Sint = 0xC2, Int = 0xC3, Dint = 0xC4, Lint = 0xC5, Usint = 0xC6, Uint = 0xC7, Udint = 0xC8, Real = 0xCA, Lreal = 0xCB,
}

/// <summary>Well-known CIP classes.</summary>
public static class CipClass
{
    public const ushort Identity = 0x01;
    public const ushort MessageRouter = 0x02;
    public const ushort Assembly = 0x04;
    public const ushort ConnectionManager = 0x06;
    public const ushort TcpIpInterface = 0xF5;
    public const ushort EthernetLink = 0xF6;
}

/// <summary>A CIP error answer.</summary>
public sealed class CipException : ProtocolException
{
    /// <summary>Creates the exception.</summary>
    public CipException(CipStatus status, ushort[]? additional = null, string? message = null)
        : base(message ?? $"CIP error 0x{(byte)status:X2} {status}" + (additional is { Length: > 0 } a ? $" (additional 0x{a[0]:X4})" : "")) => (Status, Additional) = (status, additional ?? []);

    /// <summary>Creates the exception.</summary>
    public CipException() : this(CipStatus.GeneralError) { }

    /// <summary>Creates the exception.</summary>
    public CipException(string message) : this(CipStatus.GeneralError, null, message) { }

    /// <summary>Creates the exception.</summary>
    public CipException(string message, Exception? inner) : base(message, inner) => Status = CipStatus.GeneralError;

    /// <summary>General status.</summary>
    public CipStatus Status { get; }

    /// <summary>Additional status words.</summary>
    public IReadOnlyList<ushort> Additional { get; } = [];
}

/// <summary>A CIP path (EPATH): logical class/instance/attribute segments, or a symbolic tag name with optional element indexes.</summary>
public sealed record CipPath
{
    /// <summary>Class id (logical paths).</summary>
    public ushort? Class { get; init; }

    /// <summary>Instance id.</summary>
    public uint? Instance { get; init; }

    /// <summary>Attribute id.</summary>
    public ushort? Attribute { get; init; }

    /// <summary>Symbolic segments, e.g. "Program:Main", "Motor" (a tag "Program:Main.Motor" has two).</summary>
    public IReadOnlyList<string> Symbols { get; init; } = [];

    /// <summary>Element index following the last symbol (arrays), e.g. Counts[3].</summary>
    public uint? Element { get; init; }

    /// <summary>A logical path.</summary>
    public static CipPath Logical(ushort cls, uint instance, ushort? attribute = null) => new() { Class = cls, Instance = instance, Attribute = attribute };

    /// <summary>A symbolic tag path ("Line.Speed" or "Counts[3]").</summary>
    public static CipPath Tag(string name)
    {
        uint? element = null;
        var text = name.Trim();
        if (text.EndsWith(']'))
        {
            var open = text.LastIndexOf('[');
            if (open < 0) throw new FormatException($"'{name}' has an unbalanced index.");
            element = uint.Parse(text[(open + 1)..^1], NumberStyles.None, CultureInfo.InvariantCulture);
            text = text[..open];
        }

        var parts = text.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(p => p.Length > 255)) throw new FormatException($"'{name}' is not a tag name.");
        return new CipPath { Symbols = parts, Element = element };
    }

    /// <summary>The tag name written back with dots and an index.</summary>
    public string TagName => string.Join(".", Symbols) + (Element is { } e ? $"[{e}]" : "");

    /// <summary>Encodes the path (padded EPATH, even length).</summary>
    public byte[] Encode()
    {
        var b = new List<byte>();
        foreach (var s in Symbols)
        {
            var bytes = Encoding.ASCII.GetBytes(s);
            b.Add(0x91);
            b.Add((byte)bytes.Length);
            b.AddRange(bytes);
            if (bytes.Length % 2 == 1) b.Add(0);
        }

        if (Element is { } el) Logical(b, 0x28, el);
        if (Class is { } c) Logical(b, 0x20, c);
        if (Instance is { } i) Logical(b, 0x24, i);
        if (Attribute is { } a) Logical(b, 0x30, a);
        return [.. b];
    }

    private static void Logical(List<byte> b, byte type, uint value)
    {
        if (value <= 0xFF)
        {
            b.Add(type);
            b.Add((byte)value);
        }
        else if (value <= 0xFFFF)
        {
            b.Add((byte)(type | 1));
            b.Add(0);
            b.Add((byte)value);
            b.Add((byte)(value >> 8));
        }
        else
        {
            b.Add((byte)(type | 2));
            b.Add(0);
            b.Add((byte)value);
            b.Add((byte)(value >> 8));
            b.Add((byte)(value >> 16));
            b.Add((byte)(value >> 24));
        }
    }

    /// <summary>Decodes a padded EPATH.</summary>
    public static CipPath Decode(ReadOnlySpan<byte> b)
    {
        ushort? cls = null, attr = null;
        uint? inst = null, element = null;
        var symbols = new List<string>();
        var o = 0;
        while (o < b.Length)
        {
            var seg = b[o];
            if (seg == 0x91)
            {
                if (o + 2 > b.Length) throw new ProtocolException("Symbolic segment truncated.");
                var len = b[o + 1];
                if (o + 2 + len > b.Length) throw new ProtocolException("Symbolic segment longer than the path.");
                symbols.Add(Encoding.ASCII.GetString(b.Slice(o + 2, len)));
                o += 2 + len + (len % 2);
                continue;
            }

            var kind = seg & 0xFC;
            var format = seg & 0x03;
            uint value;
            switch (format)
            {
                case 0:
                    if (o + 2 > b.Length) throw new ProtocolException("Logical segment truncated.");
                    value = b[o + 1];
                    o += 2;
                    break;
                case 1:
                    if (o + 4 > b.Length) throw new ProtocolException("Logical segment truncated.");
                    value = BinaryPrimitives.ReadUInt16LittleEndian(b[(o + 2)..]);
                    o += 4;
                    break;
                case 2:
                    if (o + 6 > b.Length) throw new ProtocolException("Logical segment truncated.");
                    value = BinaryPrimitives.ReadUInt32LittleEndian(b[(o + 2)..]);
                    o += 6;
                    break;
                default:
                    throw new ProtocolException($"Logical format {format} is reserved.");
            }

            switch (kind)
            {
                case 0x20: cls = (ushort)value; break;
                case 0x24: inst = value; break;
                case 0x28: element = value; break;
                case 0x30: attr = (ushort)value; break;
                default: throw new ProtocolException($"Path segment 0x{seg:X2} is not supported.");
            }
        }

        return new CipPath { Class = cls, Instance = inst, Attribute = attr, Symbols = symbols, Element = element };
    }

    /// <inheritdoc />
    public override string ToString() => Symbols.Count > 0 ? TagName
        : $"class 0x{Class ?? 0:X2}" + (Instance is { } i ? $"/instance {i}" : "") + (Attribute is { } a ? $"/attribute {a}" : "");
}

/// <summary>A CIP Message Router request (sans-I/O).</summary>
public sealed record CipRequest(CipService Service, CipPath Path, byte[] Data)
{
    /// <summary>Encodes service, path size (words), path and data.</summary>
    public byte[] Encode()
    {
        var path = Path.Encode();
        return [(byte)Service, (byte)(path.Length / 2), .. path, .. Data];
    }

    /// <summary>Decodes a request.</summary>
    public static CipRequest Decode(ReadOnlySpan<byte> b)
    {
        if (b.Length < 2) throw new ProtocolException("CIP request shorter than its service and path size.");
        var size = b[1] * 2;
        if (b.Length < 2 + size) throw new ProtocolException($"CIP path of {size} bytes is longer than the request.");
        return new CipRequest((CipService)b[0], CipPath.Decode(b.Slice(2, size)), b[(2 + size)..].ToArray());
    }

    /// <inheritdoc />
    public override string ToString() => $"{(Enum.IsDefined(Service) ? Service.ToString() : $"service 0x{(byte)Service:X2}")} {Path}";
}

/// <summary>A CIP Message Router response (sans-I/O).</summary>
public sealed record CipResponse(CipService Service, CipStatus Status, ushort[] Additional, byte[] Data)
{
    /// <summary>A success response.</summary>
    public static CipResponse Ok(CipService service, byte[]? data = null) => new(service, CipStatus.Success, [], data ?? []);

    /// <summary>An error response.</summary>
    public static CipResponse Error(CipService service, CipStatus status, params ushort[] additional) => new(service, status, additional, []);

    /// <summary>Encodes reply service, reserved, status, additional status and data.</summary>
    public byte[] Encode()
    {
        var b = new byte[4 + (Additional.Length * 2) + Data.Length];
        b[0] = (byte)((byte)Service | 0x80);
        b[2] = (byte)Status;
        b[3] = (byte)Additional.Length;
        for (var i = 0; i < Additional.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4 + (i * 2)), Additional[i]);
        Data.CopyTo(b, 4 + (Additional.Length * 2));
        return b;
    }

    /// <summary>Decodes a response.</summary>
    public static CipResponse Decode(ReadOnlySpan<byte> b)
    {
        if (b.Length < 4) throw new ProtocolException("CIP response shorter than 4 bytes.");
        if ((b[0] & 0x80) == 0) throw new ProtocolException($"0x{b[0]:X2} is not a reply service code.");
        if (b[1] != 0) throw new ProtocolException("The reserved byte of a CIP reply must be zero.");
        var words = b[3];
        if (b.Length < 4 + (words * 2)) throw new ProtocolException("CIP additional status truncated.");
        var add = new ushort[words];
        for (var i = 0; i < words; i++) add[i] = BinaryPrimitives.ReadUInt16LittleEndian(b[(4 + (i * 2))..]);
        return new CipResponse((CipService)(b[0] & 0x7F), (CipStatus)b[2], add, b[(4 + (words * 2))..].ToArray());
    }

    /// <summary>Throws <see cref="CipException"/> unless the status is success (or partial transfer).</summary>
    public CipResponse EnsureSuccess() => Status is CipStatus.Success or CipStatus.PartialTransfer ? this : throw new CipException(Status, Additional);

    /// <inheritdoc />
    public override string ToString() => $"{(Enum.IsDefined(Service) ? Service.ToString() : $"service 0x{(byte)Service:X2}")} reply {Status}" + (Data.Length > 0 ? $" [{Data.Length} B]" : "");
}

/// <summary>A typed Logix tag value: data type, element count and raw little-endian bytes.</summary>
public sealed record CipTagValue(CipDataType Type, byte[] Data)
{
    /// <summary>Bytes per element.</summary>
    public static int SizeOf(CipDataType t) => t switch
    {
        CipDataType.Bool or CipDataType.Sint or CipDataType.Usint => 1,
        CipDataType.Int or CipDataType.Uint => 2,
        CipDataType.Dint or CipDataType.Udint or CipDataType.Real => 4,
        CipDataType.Lint or CipDataType.Lreal => 8,
        _ => throw new NotSupportedException($"Data type 0x{(ushort)t:X4} is not an elementary type."),
    };

    /// <summary>Number of elements.</summary>
    public int Count => Data.Length / SizeOf(Type);

    /// <summary>Element <paramref name="i"/> as a number (BOOL 0/1).</summary>
    public double this[int i]
    {
        get
        {
            var s = Data.AsSpan(i * SizeOf(Type));
            return Type switch
            {
                CipDataType.Bool => s[0] != 0 ? 1 : 0,
                CipDataType.Sint => (sbyte)s[0],
                CipDataType.Usint => s[0],
                CipDataType.Int => BinaryPrimitives.ReadInt16LittleEndian(s),
                CipDataType.Uint => BinaryPrimitives.ReadUInt16LittleEndian(s),
                CipDataType.Dint => BinaryPrimitives.ReadInt32LittleEndian(s),
                CipDataType.Udint => BinaryPrimitives.ReadUInt32LittleEndian(s),
                CipDataType.Real => BinaryPrimitives.ReadSingleLittleEndian(s),
                CipDataType.Lint => BinaryPrimitives.ReadInt64LittleEndian(s),
                CipDataType.Lreal => BinaryPrimitives.ReadDoubleLittleEndian(s),
                _ => double.NaN,
            };
        }
    }

    /// <summary>The first element.</summary>
    public double Value => this[0];

    /// <summary>Encodes values of a type.</summary>
    public static CipTagValue Of(CipDataType type, params double[] values)
    {
        var size = SizeOf(type);
        var b = new byte[size * values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            var s = b.AsSpan(i * size);
            var v = values[i];
            switch (type)
            {
                case CipDataType.Bool: s[0] = (byte)(v != 0 ? 0xFF : 0); break;
                case CipDataType.Sint: s[0] = unchecked((byte)checked((sbyte)v)); break;
                case CipDataType.Usint: s[0] = checked((byte)v); break;
                case CipDataType.Int: BinaryPrimitives.WriteInt16LittleEndian(s, checked((short)v)); break;
                case CipDataType.Uint: BinaryPrimitives.WriteUInt16LittleEndian(s, checked((ushort)v)); break;
                case CipDataType.Dint: BinaryPrimitives.WriteInt32LittleEndian(s, checked((int)v)); break;
                case CipDataType.Udint: BinaryPrimitives.WriteUInt32LittleEndian(s, checked((uint)v)); break;
                case CipDataType.Real: BinaryPrimitives.WriteSingleLittleEndian(s, (float)v); break;
                case CipDataType.Lint: BinaryPrimitives.WriteInt64LittleEndian(s, checked((long)v)); break;
                case CipDataType.Lreal: BinaryPrimitives.WriteDoubleLittleEndian(s, v); break;
            }
        }

        return new CipTagValue(type, b);
    }

    /// <summary>Parses a type name (BOOL, DINT, REAL…).</summary>
    public static CipDataType ParseType(string name) => Enum.TryParse<CipDataType>(name, true, out var t) && Enum.IsDefined(t)
        ? t : throw new FormatException($"'{name}' is not a CIP elementary type (BOOL, SINT, INT, DINT, LINT, USINT, UINT, UDINT, REAL, LREAL).");

    /// <inheritdoc />
    public bool Equals(CipTagValue? other) => other is not null && Type == other.Type && Data.AsSpan().SequenceEqual(other.Data);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Type, Data.Length);

    /// <inheritdoc />
    public override string ToString()
    {
        var values = Enumerable.Range(0, Count).Select(i => Type == CipDataType.Bool ? (this[i] != 0 ? "TRUE" : "FALSE") : this[i].ToString("0.###", CultureInfo.InvariantCulture));
        return $"{Type.ToString().ToUpperInvariant()} " + (Count == 1 ? values.First() : "[" + string.Join(", ", values) + "]");
    }
}
