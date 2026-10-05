using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace IoTCom.Net.Protocols.Coap;

/// <summary>Message type (RFC 7252 §4.3).</summary>
public enum CoapType : byte
{
    /// <summary>Confirmable: retransmitted until acknowledged.</summary>
    Confirmable = 0,
    /// <summary>Non-confirmable: fire and forget.</summary>
    NonConfirmable = 1,
    /// <summary>Acknowledgement.</summary>
    Acknowledgement = 2,
    /// <summary>Reset: "I cannot process this message".</summary>
    Reset = 3,
}

/// <summary>A CoAP code: method (0.xx), success (2.xx), client error (4.xx) or server error (5.xx).</summary>
/// <param name="Value">Raw byte: class &lt;&lt; 5 | detail.</param>
public readonly record struct CoapCode(byte Value)
{
    /// <summary>0.00 empty message.</summary>
    public static readonly CoapCode Empty = new(0x00);
    /// <summary>0.01 GET.</summary>
    public static readonly CoapCode Get = new(0x01);
    /// <summary>0.02 POST.</summary>
    public static readonly CoapCode Post = new(0x02);
    /// <summary>0.03 PUT.</summary>
    public static readonly CoapCode Put = new(0x03);
    /// <summary>0.04 DELETE.</summary>
    public static readonly CoapCode Delete = new(0x04);
    /// <summary>2.01 Created.</summary>
    public static readonly CoapCode Created = new(0x41);
    /// <summary>2.02 Deleted.</summary>
    public static readonly CoapCode Deleted = new(0x42);
    /// <summary>2.03 Valid.</summary>
    public static readonly CoapCode Valid = new(0x43);
    /// <summary>2.04 Changed.</summary>
    public static readonly CoapCode Changed = new(0x44);
    /// <summary>2.05 Content.</summary>
    public static readonly CoapCode Content = new(0x45);
    /// <summary>2.31 Continue (Block1).</summary>
    public static readonly CoapCode Continue = new(0x5F);
    /// <summary>4.00 Bad Request.</summary>
    public static readonly CoapCode BadRequest = new(0x80);
    /// <summary>4.01 Unauthorized.</summary>
    public static readonly CoapCode Unauthorized = new(0x81);
    /// <summary>4.02 Bad Option.</summary>
    public static readonly CoapCode BadOption = new(0x82);
    /// <summary>4.04 Not Found.</summary>
    public static readonly CoapCode NotFound = new(0x84);
    /// <summary>4.05 Method Not Allowed.</summary>
    public static readonly CoapCode MethodNotAllowed = new(0x85);
    /// <summary>4.06 Not Acceptable.</summary>
    public static readonly CoapCode NotAcceptable = new(0x86);
    /// <summary>4.08 Request Entity Incomplete (Block1).</summary>
    public static readonly CoapCode RequestEntityIncomplete = new(0x88);
    /// <summary>4.13 Request Entity Too Large.</summary>
    public static readonly CoapCode RequestEntityTooLarge = new(0x8D);
    /// <summary>4.15 Unsupported Content-Format.</summary>
    public static readonly CoapCode UnsupportedContentFormat = new(0x8F);
    /// <summary>5.00 Internal Server Error.</summary>
    public static readonly CoapCode InternalServerError = new(0xA0);
    /// <summary>5.01 Not Implemented.</summary>
    public static readonly CoapCode NotImplemented = new(0xA1);
    /// <summary>5.03 Service Unavailable.</summary>
    public static readonly CoapCode ServiceUnavailable = new(0xA3);

    /// <summary>Class (0 request, 2 success, 4 client error, 5 server error).</summary>
    public int Class => Value >> 5;

    /// <summary>Detail.</summary>
    public int Detail => Value & 0x1F;

    /// <summary>True for 0.01–0.31.</summary>
    public bool IsRequest => Class == 0 && Value != 0;

    /// <summary>True for 2.xx.</summary>
    public bool IsSuccess => Class == 2;

    /// <summary>Creates a code from class and detail.</summary>
    public static CoapCode From(int @class, int detail) => new((byte)((@class << 5) | detail));

    /// <summary>"2.05 Content" style text.</summary>
    public override string ToString()
    {
        var name = Value switch
        {
            0x00 => "Empty", 0x01 => "GET", 0x02 => "POST", 0x03 => "PUT", 0x04 => "DELETE", 0x05 => "FETCH",
            0x41 => "Created", 0x42 => "Deleted", 0x43 => "Valid", 0x44 => "Changed", 0x45 => "Content", 0x5F => "Continue",
            0x80 => "Bad Request", 0x81 => "Unauthorized", 0x82 => "Bad Option", 0x83 => "Forbidden", 0x84 => "Not Found",
            0x85 => "Method Not Allowed", 0x86 => "Not Acceptable", 0x88 => "Request Entity Incomplete", 0x8C => "Precondition Failed",
            0x8D => "Request Entity Too Large", 0x8F => "Unsupported Content-Format",
            0xA0 => "Internal Server Error", 0xA1 => "Not Implemented", 0xA2 => "Bad Gateway", 0xA3 => "Service Unavailable",
            0xA4 => "Gateway Timeout", 0xA5 => "Proxying Not Supported",
            _ => "",
        };
        var code = IsRequest || Value == 0 ? "" : $"{Class}.{Detail:00}";
        return code.Length == 0 ? name : name.Length == 0 ? code : $"{code} {name}";
    }
}

/// <summary>Option numbers (RFC 7252 §5.10, RFC 7641, RFC 7959).</summary>
public static class CoapOptionNumber
{
    /// <summary>1 If-Match.</summary>
    public const ushort IfMatch = 1;
    /// <summary>3 Uri-Host.</summary>
    public const ushort UriHost = 3;
    /// <summary>4 ETag.</summary>
    public const ushort ETag = 4;
    /// <summary>5 If-None-Match.</summary>
    public const ushort IfNoneMatch = 5;
    /// <summary>6 Observe.</summary>
    public const ushort Observe = 6;
    /// <summary>7 Uri-Port.</summary>
    public const ushort UriPort = 7;
    /// <summary>8 Location-Path.</summary>
    public const ushort LocationPath = 8;
    /// <summary>11 Uri-Path.</summary>
    public const ushort UriPath = 11;
    /// <summary>12 Content-Format.</summary>
    public const ushort ContentFormat = 12;
    /// <summary>14 Max-Age.</summary>
    public const ushort MaxAge = 14;
    /// <summary>15 Uri-Query.</summary>
    public const ushort UriQuery = 15;
    /// <summary>17 Accept.</summary>
    public const ushort Accept = 17;
    /// <summary>20 Location-Query.</summary>
    public const ushort LocationQuery = 20;
    /// <summary>23 Block2.</summary>
    public const ushort Block2 = 23;
    /// <summary>27 Block1.</summary>
    public const ushort Block1 = 27;
    /// <summary>28 Size2.</summary>
    public const ushort Size2 = 28;
    /// <summary>35 Proxy-Uri.</summary>
    public const ushort ProxyUri = 35;
    /// <summary>39 Proxy-Scheme.</summary>
    public const ushort ProxyScheme = 39;
    /// <summary>60 Size1.</summary>
    public const ushort Size1 = 60;

    /// <summary>Critical options (odd numbers) must be understood or the message rejected.</summary>
    public static bool IsCritical(ushort number) => (number & 1) != 0;

    /// <summary>Option name.</summary>
    public static string Name(ushort number) => number switch
    {
        IfMatch => "If-Match", UriHost => "Uri-Host", ETag => "ETag", IfNoneMatch => "If-None-Match", Observe => "Observe",
        UriPort => "Uri-Port", LocationPath => "Location-Path", UriPath => "Uri-Path", ContentFormat => "Content-Format",
        MaxAge => "Max-Age", UriQuery => "Uri-Query", Accept => "Accept", LocationQuery => "Location-Query",
        Block2 => "Block2", Block1 => "Block1", Size2 => "Size2", ProxyUri => "Proxy-Uri", ProxyScheme => "Proxy-Scheme", Size1 => "Size1",
        _ => $"Option {number}",
    };
}

/// <summary>Registered content formats (IANA CoAP Content-Formats).</summary>
public static class CoapContentFormat
{
    /// <summary>0 text/plain; charset=utf-8.</summary>
    public const ushort TextPlain = 0;
    /// <summary>40 application/link-format.</summary>
    public const ushort LinkFormat = 40;
    /// <summary>41 application/xml.</summary>
    public const ushort Xml = 41;
    /// <summary>42 application/octet-stream.</summary>
    public const ushort OctetStream = 42;
    /// <summary>50 application/json.</summary>
    public const ushort Json = 50;
    /// <summary>60 application/cbor.</summary>
    public const ushort Cbor = 60;
    /// <summary>110 application/senml+json.</summary>
    public const ushort SenMLJson = 110;
    /// <summary>112 application/senml+cbor.</summary>
    public const ushort SenMLCbor = 112;

    /// <summary>Media type name.</summary>
    public static string Name(ushort format) => format switch
    {
        TextPlain => "text/plain", LinkFormat => "application/link-format", Xml => "application/xml",
        OctetStream => "application/octet-stream", Json => "application/json", Cbor => "application/cbor",
        SenMLJson => "application/senml+json", SenMLCbor => "application/senml+cbor",
        _ => format.ToString(CultureInfo.InvariantCulture),
    };
}

/// <summary>One option instance.</summary>
/// <param name="Number">Option number.</param>
/// <param name="Value">Raw value.</param>
public readonly record struct CoapOption(ushort Number, ReadOnlyMemory<byte> Value)
{
    /// <summary>Value as an unsigned integer (≤ 4 bytes).</summary>
    public uint UInt
    {
        get
        {
            uint v = 0;
            foreach (var b in Value.Span) v = (v << 8) | b;
            return v;
        }
    }

    /// <summary>Value as UTF-8 text.</summary>
    public string Text => Encoding.UTF8.GetString(Value.Span);

    /// <summary>Minimal big-endian encoding of an unsigned option value (0 → empty).</summary>
    public static byte[] EncodeUInt(uint value)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buf, value);
        var skip = 0;
        while (skip < 4 && buf[skip] == 0) skip++;
        return buf[skip..].ToArray();
    }
}

/// <summary>A Block1/Block2 option value (RFC 7959 §2.2).</summary>
/// <param name="Number">Block number.</param>
/// <param name="More">More blocks follow.</param>
/// <param name="SizeExponent">SZX 0–6: block size = 2^(SZX + 4) (16–1024 bytes).</param>
public readonly record struct CoapBlock(uint Number, bool More, int SizeExponent)
{
    /// <summary>Block size in bytes.</summary>
    public int Size => 16 << SizeExponent;

    /// <summary>Offset of this block in the full body.</summary>
    public long Offset => (long)Number * Size;

    /// <summary>SZX for a block size (16–1024, power of two).</summary>
    public static int ExponentFor(int size) => size switch
    {
        16 => 0, 32 => 1, 64 => 2, 128 => 3, 256 => 4, 512 => 5, 1024 => 6,
        _ => throw new ArgumentOutOfRangeException(nameof(size), "Block size must be 16, 32, 64, 128, 256, 512 or 1024."),
    };

    /// <summary>Decodes an option value.</summary>
    public static CoapBlock? Decode(ReadOnlySpan<byte> value)
    {
        if (value.Length > 3) return null;
        uint raw = 0;
        foreach (var b in value) raw = (raw << 8) | b;
        var szx = (int)(raw & 7);
        return szx == 7 ? null : new CoapBlock(raw >> 4, (raw & 8) != 0, szx);
    }

    /// <summary>Encodes the option value.</summary>
    public byte[] Encode() => CoapOption.EncodeUInt((Number << 4) | (More ? 8u : 0u) | (uint)SizeExponent);

    /// <inheritdoc />
    public override string ToString() => $"{Number}{(More ? "+" : "")}/{Size}";
}

/// <summary>
/// A CoAP message (RFC 7252). Sans-I/O: <see cref="Encode()"/> and <see cref="TryDecode"/> are pure and match the Rust
/// codec (<c>iotcom-coap</c>) byte for byte — both run <c>/conformance/coap.json</c>.
/// </summary>
public sealed class CoapMessage
{
    private readonly List<CoapOption> _options = [];

    /// <summary>Type.</summary>
    public CoapType Type { get; set; } = CoapType.Confirmable;

    /// <summary>Code.</summary>
    public CoapCode Code { get; set; }

    /// <summary>Message ID (deduplication and ACK matching).</summary>
    public ushort MessageId { get; set; }

    /// <summary>Token (0–8 bytes, request/response matching).</summary>
    public ReadOnlyMemory<byte> Token { get; set; }

    /// <summary>Payload.</summary>
    public ReadOnlyMemory<byte> Payload { get; set; }

    /// <summary>Options in ascending number order (repeatable options keep insertion order).</summary>
    public IReadOnlyList<CoapOption> Options => _options;

    /// <summary>Adds an option, keeping numbers sorted.</summary>
    public CoapMessage AddOption(ushort number, ReadOnlyMemory<byte> value)
    {
        var i = _options.Count;
        while (i > 0 && _options[i - 1].Number > number) i--;
        _options.Insert(i, new CoapOption(number, value));
        return this;
    }

    /// <summary>Adds an unsigned-integer option.</summary>
    public CoapMessage AddOption(ushort number, uint value) => AddOption(number, CoapOption.EncodeUInt(value));

    /// <summary>Adds a string option.</summary>
    public CoapMessage AddOption(ushort number, string value) => AddOption(number, Encoding.UTF8.GetBytes(value));

    /// <summary>Replaces all instances of an option (null removes it).</summary>
    public CoapMessage SetOption(ushort number, ReadOnlyMemory<byte>? value)
    {
        _options.RemoveAll(o => o.Number == number);
        if (value is { } v) AddOption(number, v);
        return this;
    }

    // byte[] → ReadOnlyMemory<byte>? turns null into an *empty* value; property setters use this to mean "remove".
    private CoapMessage SetBytes(ushort number, byte[]? value) =>
        value is null ? SetOption(number, (ReadOnlyMemory<byte>?)null) : SetOption(number, value.AsMemory());

    /// <summary>First instance of an option, or null.</summary>
    public CoapOption? GetOption(ushort number)
    {
        foreach (var o in _options)
            if (o.Number == number) return o;
        return null;
    }

    /// <summary>All instances of an option.</summary>
    public IEnumerable<CoapOption> GetOptions(ushort number) => _options.Where(o => o.Number == number);

    /// <summary>Uri-Path as "/a/b" (empty segments are kept).</summary>
    public string UriPath
    {
        get => "/" + string.Join('/', GetOptions(CoapOptionNumber.UriPath).Select(o => o.Text));
        set
        {
            SetOption(CoapOptionNumber.UriPath, null);
            foreach (var segment in (value ?? "").Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries))
                AddOption(CoapOptionNumber.UriPath, Uri.UnescapeDataString(segment));
        }
    }

    /// <summary>Uri-Query values ("key=value").</summary>
    public IReadOnlyList<string> UriQuery => GetOptions(CoapOptionNumber.UriQuery).Select(o => o.Text).ToList();

    /// <summary>Content-Format, or null.</summary>
    public ushort? ContentFormat
    {
        get => GetOption(CoapOptionNumber.ContentFormat) is { } o ? (ushort)o.UInt : null;
        set => SetBytes(CoapOptionNumber.ContentFormat, value is { } v ? CoapOption.EncodeUInt(v) : null);
    }

    /// <summary>Accept, or null.</summary>
    public ushort? Accept
    {
        get => GetOption(CoapOptionNumber.Accept) is { } o ? (ushort)o.UInt : null;
        set => SetBytes(CoapOptionNumber.Accept, value is { } v ? CoapOption.EncodeUInt(v) : null);
    }

    /// <summary>Observe (0 register / 1 deregister in requests; sequence number in notifications), or null.</summary>
    public uint? Observe
    {
        get => GetOption(CoapOptionNumber.Observe) is { } o ? o.UInt : null;
        set => SetBytes(CoapOptionNumber.Observe, value is { } v ? CoapOption.EncodeUInt(v & 0xFFFFFF) : null);
    }

    /// <summary>Max-Age in seconds (default 60 when absent).</summary>
    public uint? MaxAge
    {
        get => GetOption(CoapOptionNumber.MaxAge) is { } o ? o.UInt : null;
        set => SetBytes(CoapOptionNumber.MaxAge, value is { } v ? CoapOption.EncodeUInt(v) : null);
    }

    /// <summary>Block2 (response body blocks), or null.</summary>
    public CoapBlock? Block2
    {
        get => GetOption(CoapOptionNumber.Block2) is { } o ? CoapBlock.Decode(o.Value.Span) : null;
        set => SetBytes(CoapOptionNumber.Block2, value?.Encode());
    }

    /// <summary>Block1 (request body blocks), or null.</summary>
    public CoapBlock? Block1
    {
        get => GetOption(CoapOptionNumber.Block1) is { } o ? CoapBlock.Decode(o.Value.Span) : null;
        set => SetBytes(CoapOptionNumber.Block1, value?.Encode());
    }

    /// <summary>Payload as UTF-8 text.</summary>
    public string PayloadText => Encoding.UTF8.GetString(Payload.Span);

    /// <summary>Encoded size in bytes.</summary>
    public int EncodedLength
    {
        get
        {
            var n = 4 + Token.Length;
            var last = 0;
            foreach (var o in _options)
            {
                n += 1 + ExtLength(o.Number - last) + ExtLength(o.Value.Length) + o.Value.Length;
                last = o.Number;
            }
            return n + (Payload.IsEmpty ? 0 : 1 + Payload.Length);
        }
    }

    private static int ExtLength(int v) => v < 13 ? 0 : v < 269 ? 1 : 2;

    /// <summary>Encodes to a new array.</summary>
    public byte[] Encode()
    {
        var buf = new byte[EncodedLength];
        Encode(buf);
        return buf;
    }

    /// <summary>Encodes into <paramref name="destination"/>; returns the bytes written.</summary>
    public int Encode(Span<byte> destination)
    {
        if (Token.Length > 8) throw new InvalidOperationException("CoAP tokens are at most 8 bytes.");
        var d = destination;
        d[0] = (byte)(0x40 | ((int)Type << 4) | Token.Length);
        d[1] = Code.Value;
        BinaryPrimitives.WriteUInt16BigEndian(d[2..], MessageId);
        Token.Span.CopyTo(d[4..]);
        var pos = 4 + Token.Length;
        var last = 0;
        foreach (var o in _options)
        {
            var delta = o.Number - last;
            var len = o.Value.Length;
            if (len > 65_804) throw new InvalidOperationException($"Option {o.Number} value too long.");
            var head = pos++;
            d[head] = (byte)((Nibble(delta) << 4) | Nibble(len));
            pos += WriteExt(d[pos..], delta);
            pos += WriteExt(d[pos..], len);
            o.Value.Span.CopyTo(d[pos..]);
            pos += len;
            last = o.Number;
        }
        if (!Payload.IsEmpty)
        {
            d[pos++] = 0xFF;
            Payload.Span.CopyTo(d[pos..]);
            pos += Payload.Length;
        }
        return pos;
    }

    private static int Nibble(int v) => v < 13 ? v : v < 269 ? 13 : 14;

    private static int WriteExt(Span<byte> d, int v)
    {
        if (v < 13) return 0;
        if (v < 269)
        {
            d[0] = (byte)(v - 13);
            return 1;
        }
        BinaryPrimitives.WriteUInt16BigEndian(d, (ushort)(v - 269));
        return 2;
    }

    /// <summary>Decodes a datagram.</summary>
    /// <exception cref="ProtocolException">The datagram is not a valid CoAP message.</exception>
    public static CoapMessage Decode(ReadOnlySpan<byte> data) =>
        TryDecode(data, out var m, out var error) ? m : throw new ProtocolException("Invalid CoAP message: " + error);

    /// <summary>Decodes a datagram, validating RFC 7252 §3 (version, token length, reserved nibbles, truncation, empty messages).</summary>
    public static bool TryDecode(ReadOnlySpan<byte> data, [NotNullWhen(true)] out CoapMessage? message, out string? error)
    {
        message = null;
        error = null;
        if (data.Length < 4) return Fail("message shorter than 4 bytes", out error);
        if (data[0] >> 6 != 1) return Fail("unsupported CoAP version", out error);
        var tkl = data[0] & 0x0F;
        if (tkl > 8) return Fail("token length 9-15 is reserved", out error);
        if (data.Length < 4 + tkl) return Fail("message truncated", out error);
        var m = new CoapMessage
        {
            Type = (CoapType)((data[0] >> 4) & 3),
            Code = new CoapCode(data[1]),
            MessageId = BinaryPrimitives.ReadUInt16BigEndian(data[2..]),
            Token = data.Slice(4, tkl).ToArray(),
        };
        var pos = 4 + tkl;
        var number = 0;
        while (pos < data.Length)
        {
            var b = data[pos++];
            if (b == 0xFF)
            {
                if (pos == data.Length) return Fail("payload marker without payload", out error);
                m.Payload = data[pos..].ToArray();
                break;
            }
            if (!ReadExt(b >> 4, data, ref pos, out var delta) || !ReadExt(b & 0x0F, data, ref pos, out var len))
                return Fail(b >> 4 == 15 || (b & 0x0F) == 15 ? "reserved option nibble 15" : "message truncated", out error);
            number += delta;
            if (number > ushort.MaxValue) return Fail("option number above 65535", out error);
            if (pos + len > data.Length) return Fail("message truncated", out error);
            m._options.Add(new CoapOption((ushort)number, data.Slice(pos, len).ToArray()));
            pos += len;
        }
        if (m.Code.Value == 0 && (tkl > 0 || m._options.Count > 0 || !m.Payload.IsEmpty))
            return Fail("empty message must have no token, options or payload", out error);
        message = m;
        return true;
    }

    private static bool ReadExt(int nibble, ReadOnlySpan<byte> data, ref int pos, out int value)
    {
        value = nibble;
        switch (nibble)
        {
            case < 13:
                return true;
            case 13:
                if (pos >= data.Length) return false;
                value = data[pos++] + 13;
                return true;
            case 14:
                if (pos + 2 > data.Length) return false;
                value = BinaryPrimitives.ReadUInt16BigEndian(data[pos..]) + 269;
                pos += 2;
                return true;
            default:
                return false;
        }
    }

    private static bool Fail(string reason, out string? error)
    {
        error = reason;
        return false;
    }

    /// <summary>Creates an empty ACK or RST for <paramref name="messageId"/>.</summary>
    public static CoapMessage Empty(CoapType type, ushort messageId) => new() { Type = type, Code = CoapCode.Empty, MessageId = messageId };

    /// <summary>Short description, e.g. "CON GET /temp mid=0x7D34 tok=A1".</summary>
    public override string ToString()
    {
        var t = Type switch { CoapType.Confirmable => "CON", CoapType.NonConfirmable => "NON", CoapType.Acknowledgement => "ACK", _ => "RST" };
        var sb = new StringBuilder().Append(t).Append(' ').Append(Code);
        if (Code.IsRequest) sb.Append(' ').Append(UriPath);
        sb.Append(CultureInfo.InvariantCulture, $" mid=0x{MessageId:X4}");
        if (!Token.IsEmpty) sb.Append(" tok=").Append(Convert.ToHexString(Token.Span));
        if (Observe is { } obs) sb.Append(CultureInfo.InvariantCulture, $" obs={obs}");
        if (Block2 is { } b2) sb.Append(" b2=").Append(b2);
        if (Block1 is { } b1) sb.Append(" b1=").Append(b1);
        if (!Payload.IsEmpty) sb.Append(CultureInfo.InvariantCulture, $" {Payload.Length}B");
        return sb.ToString();
    }
}

/// <summary>Frame-lane description of CoAP datagrams.</summary>
public static class CoapAnatomy
{
    /// <summary>Describes header, token, options and payload.</summary>
    public static IReadOnlyList<FrameField> Describe(ReadOnlySpan<byte> data)
    {
        var fields = new List<FrameField>();
        if (!CoapMessage.TryDecode(data, out var m, out var error))
            return [new FrameField("Invalid", 0, data.Length, FrameFieldKind.Error, error)];
        var type = m.Type switch { CoapType.Confirmable => "CON", CoapType.NonConfirmable => "NON", CoapType.Acknowledgement => "ACK", _ => "RST" };
        fields.Add(new FrameField("Ver/T/TKL", 0, 1, FrameFieldKind.Header, type));
        fields.Add(new FrameField("Code", 1, 1, m.Code.Class >= 4 ? FrameFieldKind.Error : FrameFieldKind.Function, m.Code.ToString()));
        fields.Add(new FrameField("MID", 2, 2, FrameFieldKind.Header, $"0x{m.MessageId:X4}"));
        if (!m.Token.IsEmpty) fields.Add(new FrameField("Token", 4, m.Token.Length, FrameFieldKind.Address, Convert.ToHexString(m.Token.Span)));
        var pos = 4 + m.Token.Length;
        var last = 0;
        foreach (var o in m.Options)
        {
            var len = 1 + Ext(o.Number - last) + Ext(o.Value.Length) + o.Value.Length;
            var value = o.Number is CoapOptionNumber.UriPath or CoapOptionNumber.UriQuery or CoapOptionNumber.UriHost or CoapOptionNumber.LocationPath
                ? o.Text
                : o.Number is CoapOptionNumber.Block1 or CoapOptionNumber.Block2 ? CoapBlock.Decode(o.Value.Span)?.ToString()
                : o.Value.Length <= 4 ? o.UInt.ToString(CultureInfo.InvariantCulture) : null;
            fields.Add(new FrameField(CoapOptionNumber.Name(o.Number), pos, len, o.Number is CoapOptionNumber.UriPath or CoapOptionNumber.UriQuery ? FrameFieldKind.Address : FrameFieldKind.Length, value));
            pos += len;
            last = o.Number;
        }
        if (!m.Payload.IsEmpty)
        {
            fields.Add(new FrameField("0xFF", pos, 1, FrameFieldKind.Delimiter));
            fields.Add(new FrameField("Payload", pos + 1, m.Payload.Length, FrameFieldKind.Data));
        }
        return fields;
    }

    private static int Ext(int v) => v < 13 ? 0 : v < 269 ? 1 : 2;
}
