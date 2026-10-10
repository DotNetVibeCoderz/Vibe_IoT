using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using IoTCom.Net.Serialization.SenML;

namespace IoTCom.Net.Protocols.Lwm2m;

/// <summary>LwM2M content formats (CoAP Content-Format numbers).</summary>
public static class Lwm2mFormat
{
    /// <summary>text/plain: a single resource.</summary>
    public const ushort Text = 0;
    /// <summary>application/link-format: registration and discover.</summary>
    public const ushort Link = 40;
    /// <summary>application/octet-stream: a single opaque resource.</summary>
    public const ushort Opaque = 42;
    /// <summary>application/senml+json (LwM2M 1.1).</summary>
    public const ushort SenMLJson = 110;
    /// <summary>application/senml+cbor (LwM2M 1.1).</summary>
    public const ushort SenMLCbor = 112;
    /// <summary>application/vnd.oma.lwm2m+tlv (LwM2M 1.0 and 1.1).</summary>
    public const ushort Tlv = 11542;

    /// <summary>A short name.</summary>
    public static string Name(ushort format) => format switch
    {
        Text => "text", Link => "link-format", Opaque => "opaque", SenMLJson => "SenML JSON", SenMLCbor => "SenML CBOR", Tlv => "TLV",
        _ => format.ToString(CultureInfo.InvariantCulture),
    };
}

/// <summary>
/// Encodes and decodes LwM2M payloads. Decoding needs the resource types, which come from <see cref="Lwm2mRegistry"/>;
/// unknown resources decode as opaque bytes (TLV) or as text (plain text).
/// </summary>
public static class Lwm2mContent
{
    // ---- TLV ----------------------------------------------------------------------------------------------------

    private enum TlvKind : byte
    {
        ObjectInstance = 0,
        ResourceInstance = 1,
        MultipleResource = 2,
        Resource = 3,
    }

    /// <summary>Encodes values below <paramref name="request"/> as TLV (object instances, resources, resource instances as needed).</summary>
    public static byte[] EncodeTlv(Lwm2mPath request, IEnumerable<Lwm2mValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var list = values.Where(v => request.Contains(v.Path)).ToList();
        var o = new List<byte>();
        switch (request.Depth)
        {
            case 1:
                foreach (var instance in list.GroupBy(v => v.Path.InstanceId!.Value).OrderBy(g => g.Key))
                    WriteTlv(o, TlvKind.ObjectInstance, instance.Key, Resources(instance));
                break;
            case 2:
                o.AddRange(Resources(list));
                break;
            case 3:
                o.AddRange(Resources(list));
                break;
            default:
                foreach (var v in list) WriteTlv(o, TlvKind.ResourceInstance, v.Path.ResourceInstanceId!.Value, Bytes(v.Value));
                break;
        }

        return [.. o];
    }

    private static byte[] Resources(IEnumerable<Lwm2mValue> values)
    {
        var o = new List<byte>();
        foreach (var resource in values.GroupBy(v => v.Path.ResourceId!.Value).OrderBy(g => g.Key))
        {
            var items = resource.ToList();
            if (items.Count == 1 && items[0].Path.ResourceInstanceId is null)
            {
                WriteTlv(o, TlvKind.Resource, resource.Key, Bytes(items[0].Value));
                continue;
            }

            var inner = new List<byte>();
            foreach (var item in items.OrderBy(i => i.Path.ResourceInstanceId)) WriteTlv(inner, TlvKind.ResourceInstance, item.Path.ResourceInstanceId ?? 0, Bytes(item.Value));
            WriteTlv(o, TlvKind.MultipleResource, resource.Key, [.. inner]);
        }

        return [.. o];
    }

    private static void WriteTlv(List<byte> o, TlvKind kind, ushort id, ReadOnlySpan<byte> value)
    {
        var len = value.Length;
        var lengthType = len < 8 ? 0 : len <= 0xFF ? 1 : len <= 0xFFFF ? 2 : 3;
        o.Add((byte)(((byte)kind << 6) | (id > 0xFF ? 0x20 : 0) | (lengthType << 3) | (lengthType == 0 ? len : 0)));
        if (id > 0xFF) o.Add((byte)(id >> 8));
        o.Add((byte)id);
        for (var i = lengthType - 1; i >= 0; i--) o.Add((byte)(len >> (8 * i)));
        o.AddRange(value.ToArray());
    }

    /// <summary>The TLV (and opaque) representation of one value.</summary>
    public static byte[] Bytes(object value)
    {
        switch (value)
        {
            case string s:
                return Encoding.UTF8.GetBytes(s);
            case bool b:
                return [(byte)(b ? 1 : 0)];
            case byte[] raw:
                return raw;
            case DateTimeOffset t:
                return Integer(t.ToUnixTimeSeconds());
            case Lwm2mObjectLink link:
                return [(byte)(link.ObjectId >> 8), (byte)link.ObjectId, (byte)(link.InstanceId >> 8), (byte)link.InstanceId];
            case double or float:
                var d = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if ((double)(float)d == d || double.IsNaN(d))
                {
                    var f = new byte[4];
                    BinaryPrimitives.WriteSingleBigEndian(f, (float)d);
                    return f;
                }

                var e = new byte[8];
                BinaryPrimitives.WriteDoubleBigEndian(e, d);
                return e;
            case ulong or uint or ushort or byte:
                var u = Convert.ToUInt64(value, CultureInfo.InvariantCulture);
                var size = u <= byte.MaxValue ? 1 : u <= ushort.MaxValue ? 2 : u <= uint.MaxValue ? 4 : 8;
                var ub = new byte[size];
                for (var i = 0; i < size; i++) ub[size - 1 - i] = (byte)(u >> (8 * i));
                return ub;
            default:
                return Integer(Convert.ToInt64(value, CultureInfo.InvariantCulture));
        }
    }

    private static byte[] Integer(long v)
    {
        var size = v is >= sbyte.MinValue and <= sbyte.MaxValue ? 1 : v is >= short.MinValue and <= short.MaxValue ? 2 : v is >= int.MinValue and <= int.MaxValue ? 4 : 8;
        var b = new byte[size];
        for (var i = 0; i < size; i++) b[size - 1 - i] = (byte)(v >> (8 * i));
        return b;
    }

    /// <summary>Decodes a TLV value with a known type.</summary>
    /// <exception cref="ProtocolException">Wrong length for the type.</exception>
    public static object FromBytes(ReadOnlySpan<byte> b, Lwm2mType type)
    {
        return type switch
        {
            Lwm2mType.String => Encoding.UTF8.GetString(b),
            Lwm2mType.Integer => Signed(b),
            Lwm2mType.UnsignedInteger => Unsigned(b),
            Lwm2mType.Float => b.Length switch
            {
                4 => (double)BinaryPrimitives.ReadSingleBigEndian(b),
                8 => BinaryPrimitives.ReadDoubleBigEndian(b),
                _ => throw new ProtocolException($"An LwM2M float is 4 or 8 bytes, not {b.Length}."),
            },
            Lwm2mType.Boolean => b.Length == 1 && b[0] <= 1 ? b[0] == 1 : throw new ProtocolException("An LwM2M boolean is one byte, 0 or 1."),
            Lwm2mType.Time => DateTimeOffset.FromUnixTimeSeconds(Signed(b)),
            Lwm2mType.ObjectLink => b.Length == 4 ? new Lwm2mObjectLink(BinaryPrimitives.ReadUInt16BigEndian(b), BinaryPrimitives.ReadUInt16BigEndian(b[2..])) : throw new ProtocolException("An LwM2M object link is 4 bytes."),
            _ => b.ToArray(),
        };
    }

    private static long Signed(ReadOnlySpan<byte> b) => b.Length switch
    {
        1 => (sbyte)b[0],
        2 => BinaryPrimitives.ReadInt16BigEndian(b),
        4 => BinaryPrimitives.ReadInt32BigEndian(b),
        8 => BinaryPrimitives.ReadInt64BigEndian(b),
        _ => throw new ProtocolException($"An LwM2M integer is 1, 2, 4 or 8 bytes, not {b.Length}."),
    };

    private static ulong Unsigned(ReadOnlySpan<byte> b)
    {
        if (b.Length is not (1 or 2 or 4 or 8)) throw new ProtocolException($"An LwM2M unsigned integer is 1, 2, 4 or 8 bytes, not {b.Length}.");
        var v = 0UL;
        foreach (var x in b) v = (v << 8) | x;
        return v;
    }

    private static Lwm2mType TypeAt(ushort objectId, ushort resourceId) => Lwm2mRegistry.Find(objectId, resourceId)?.Type ?? Lwm2mType.Opaque;

    /// <summary>Decodes TLV relative to the path the payload belongs to.</summary>
    /// <exception cref="ProtocolException">Malformed TLV.</exception>
    public static IReadOnlyList<Lwm2mValue> DecodeTlv(Lwm2mPath target, ReadOnlySpan<byte> payload)
    {
        var values = new List<Lwm2mValue>();
        DecodeLevel(target, payload, values);
        return values;
    }

    private static void DecodeLevel(Lwm2mPath at, ReadOnlySpan<byte> d, List<Lwm2mValue> values)
    {
        var p = 0;
        while (p < d.Length)
        {
            var h = d[p++];
            var kind = (TlvKind)(h >> 6);
            var idLength = (h & 0x20) != 0 ? 2 : 1;
            var lengthType = (h >> 3) & 3;
            if (p + idLength + lengthType > d.Length) throw new ProtocolException("Truncated TLV header.");
            var id = idLength == 2 ? (ushort)((d[p] << 8) | d[p + 1]) : d[p];
            p += idLength;
            var len = lengthType == 0 ? h & 7 : 0;
            for (var i = 0; i < lengthType; i++) len = (len << 8) | d[p++];
            if (len > d.Length - p) throw new ProtocolException("TLV value runs past the payload.");
            var value = d.Slice(p, len);
            p += len;
            switch (kind)
            {
                case TlvKind.ObjectInstance:
                    if (at.Depth != 1) throw new ProtocolException("An object instance TLV inside an instance or resource.");
                    DecodeLevel(new Lwm2mPath(at.ObjectId, id), value, values);
                    break;
                case TlvKind.MultipleResource:
                    if (at.Depth == 3 && at.ResourceId != id) throw new ProtocolException("TLV resource id differs from the request path.");
                    DecodeLevel(new Lwm2mPath(at.ObjectId, at.InstanceId ?? 0, id), value, values);
                    break;
                case TlvKind.Resource:
                    if (at.Depth > 3) throw new ProtocolException("A resource TLV below a resource.");
                    if (at.Depth == 3 && at.ResourceId != id) throw new ProtocolException("TLV resource id differs from the request path.");
                    var path = new Lwm2mPath(at.ObjectId, at.InstanceId ?? 0, id);
                    values.Add(new Lwm2mValue(path, FromBytes(value, TypeAt(path.ObjectId, id))));
                    break;
                default:
                    if (at.ResourceId is null) throw new ProtocolException("A resource instance TLV outside a resource.");
                    values.Add(new Lwm2mValue(at with { ResourceInstanceId = id }, FromBytes(value, TypeAt(at.ObjectId, at.ResourceId.Value))));
                    break;
            }
        }
    }

    /// <summary>The frame lane of a TLV payload (one header and value range per TLV, nested ones flattened).</summary>
    public static IReadOnlyList<FrameField> DescribeTlv(ReadOnlySpan<byte> d)
    {
        var fields = new List<FrameField>();
        try
        {
            Walk(d, 0, fields);
        }
        catch (ProtocolException ex)
        {
            return [new FrameField("TLV", 0, d.Length, FrameFieldKind.Error, ex.Message)];
        }

        return fields;
    }

    private static void Walk(ReadOnlySpan<byte> d, int offset, List<FrameField> fields)
    {
        var p = 0;
        while (p < d.Length)
        {
            var start = p;
            var h = d[p++];
            var kind = (TlvKind)(h >> 6);
            var idLength = (h & 0x20) != 0 ? 2 : 1;
            var lengthType = (h >> 3) & 3;
            if (p + idLength + lengthType > d.Length) throw new ProtocolException("Truncated TLV header.");
            var id = idLength == 2 ? (d[p] << 8) | d[p + 1] : d[p];
            p += idLength;
            var len = lengthType == 0 ? h & 7 : 0;
            for (var i = 0; i < lengthType; i++) len = (len << 8) | d[p++];
            if (len > d.Length - p) throw new ProtocolException("TLV value runs past the payload.");
            var label = kind switch { TlvKind.ObjectInstance => "instance", TlvKind.MultipleResource => "multi-resource", TlvKind.Resource => "resource", _ => "res. instance" };
            fields.Add(new FrameField($"{label} {id}", offset + start, p - start, kind is TlvKind.Resource or TlvKind.ResourceInstance ? FrameFieldKind.Address : FrameFieldKind.Header, string.Create(CultureInfo.InvariantCulture, $"{len} bytes")));
            if (kind is TlvKind.ObjectInstance or TlvKind.MultipleResource) Walk(d.Slice(p, len), offset + p, fields);
            else if (len > 0) fields.Add(new FrameField("Value", offset + p, len, FrameFieldKind.Data));
            p += len;
        }
    }

    // ---- Plain text and opaque ------------------------------------------------------------------------------------

    /// <summary>Plain-text form of one value (booleans as 1/0, times as seconds, opaque as base64).</summary>
    public static string Text(object value) => value switch
    {
        bool b => b ? "1" : "0",
        DateTimeOffset t => t.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
        byte[] raw => Convert.ToBase64String(raw),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    /// <summary>Parses a plain-text value of a known type.</summary>
    /// <exception cref="ProtocolException">The text does not fit the type.</exception>
    public static object FromText(string text, Lwm2mType type)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            return type switch
            {
                Lwm2mType.Integer => long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture),
                Lwm2mType.UnsignedInteger => ulong.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture),
                Lwm2mType.Float => double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture),
                Lwm2mType.Boolean => text switch { "1" or "true" => true, "0" or "false" => false, _ => throw new FormatException() },
                Lwm2mType.Time => DateTimeOffset.FromUnixTimeSeconds(long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture)),
                Lwm2mType.ObjectLink => text.Split(':') is [var o, var i] ? new Lwm2mObjectLink(ushort.Parse(o, CultureInfo.InvariantCulture), ushort.Parse(i, CultureInfo.InvariantCulture)) : throw new FormatException(),
                Lwm2mType.Opaque => Convert.FromBase64String(text),
                _ => text,
            };
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            throw new ProtocolException($"\"{text}\" is not a valid {type}.", ex);
        }
    }

    // ---- SenML ----------------------------------------------------------------------------------------------------

    /// <summary>SenML records for values below <paramref name="request"/> (base name = the request path).</summary>
    public static IReadOnlyList<SenMLRecord> ToSenML(Lwm2mPath request, IEnumerable<Lwm2mValue> values)
    {
        var baseName = request.ToString() + "/";
        var records = new List<SenMLRecord>();
        foreach (var v in values.Where(v => request.Contains(v.Path)).OrderBy(v => v.Path.ToString(), StringComparer.Ordinal))
        {
            var name = v.Path.ToString()[(request.Depth == v.Path.Depth ? v.Path.ToString().Length : baseName.Length)..];
            var r = new SenMLRecord { BaseName = records.Count == 0 ? (request.Depth == v.Path.Depth ? v.Path.ToString() : baseName) : null, Name = name.Length == 0 ? null : name };
            records.Add(v.Value switch
            {
                string s => r with { StringValue = s },
                bool b => r with { BoolValue = b },
                byte[] raw => r with { DataValue = raw },
                DateTimeOffset t => r with { Value = t.ToUnixTimeSeconds() },
                Lwm2mObjectLink link => r with { StringValue = link.ToString() },
                _ => r with { Value = Convert.ToDouble(v.Value, CultureInfo.InvariantCulture) },
            });
        }

        return records;
    }

    /// <summary>Values from SenML records (names resolved against base names, types from the registry).</summary>
    /// <exception cref="ProtocolException">A name is not an LwM2M resource path.</exception>
    public static IReadOnlyList<Lwm2mValue> FromSenML(IEnumerable<SenMLRecord> records)
    {
        var values = new List<Lwm2mValue>();
        var baseName = "";
        foreach (var r in records)
        {
            if (r.BaseName is not null) baseName = r.BaseName;
            var full = baseName + (r.Name ?? "");
            if (!Lwm2mPath.TryParse(full, out var path) || path.Depth < 3) throw new ProtocolException($"SenML name \"{full}\" is not an LwM2M resource path.");
            var type = Lwm2mRegistry.Find(path.ObjectId, path.ResourceId!.Value)?.Type;
            object raw = r.StringValue is { } s ? s : r.BoolValue is { } b ? b : r.DataValue is { } data ? data : r.Value is { } n ? n
                : throw new ProtocolException($"SenML record \"{full}\" has no value.");
            values.Add(new Lwm2mValue(path, type is null or Lwm2mType.None ? raw : raw is string text && type is not Lwm2mType.String ? FromText(text, type.Value) : Lwm2mValue.Normalise(raw, type.Value)));
        }

        return values;
    }

    // ---- Dispatch -------------------------------------------------------------------------------------------------

    /// <summary>Encodes values in a content format.</summary>
    /// <exception cref="ArgumentException">Text or opaque for more than one value, or an unsupported format.</exception>
    public static byte[] Encode(ushort format, Lwm2mPath request, IReadOnlyList<Lwm2mValue> values) => format switch
    {
        Lwm2mFormat.Tlv => EncodeTlv(request, values),
        Lwm2mFormat.SenMLJson => SenMLCodec.ToJson(ToSenML(request, values)),
        Lwm2mFormat.SenMLCbor => SenMLCodec.ToCbor(ToSenML(request, values)),
        Lwm2mFormat.Text when values.Count == 1 => Encoding.UTF8.GetBytes(Text(values[0].Value)),
        Lwm2mFormat.Opaque when values.Count == 1 && values[0].Value is byte[] raw => raw,
        _ => throw new ArgumentException($"Content format {Lwm2mFormat.Name(format)} cannot carry {values.Count} value(s) of this type."),
    };

    /// <summary>Decodes a payload in a content format for the path it belongs to.</summary>
    /// <exception cref="ProtocolException">Malformed payload or unsupported format.</exception>
    public static IReadOnlyList<Lwm2mValue> Decode(ushort format, Lwm2mPath target, ReadOnlyMemory<byte> payload)
    {
        try
        {
            return format switch
            {
                Lwm2mFormat.Tlv => DecodeTlv(target, payload.Span),
                Lwm2mFormat.SenMLJson => FromSenML(SenMLCodec.ParseJson(payload.Span)),
                Lwm2mFormat.SenMLCbor => FromSenML(SenMLCodec.ParseCbor(payload)),
                Lwm2mFormat.Text when target.Depth >= 3 => [new Lwm2mValue(target, FromText(Encoding.UTF8.GetString(payload.Span), Lwm2mRegistry.Find(target.ObjectId, target.ResourceId!.Value)?.Type ?? Lwm2mType.String))],
                Lwm2mFormat.Opaque when target.Depth >= 3 => [new Lwm2mValue(target, payload.ToArray())],
                _ => throw new ProtocolException($"Content format {Lwm2mFormat.Name(format)} is not supported for {target}."),
            };
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or System.Formats.Cbor.CborContentException or InvalidOperationException or FormatException)
        {
            throw new ProtocolException($"Malformed {Lwm2mFormat.Name(format)} payload: {ex.Message}", ex);
        }
    }
}
