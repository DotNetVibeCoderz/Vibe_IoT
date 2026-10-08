using System.Globalization;
using Google.Protobuf;

namespace IoTCom.Net.Protocols.Sparkplug;

/// <summary>Sparkplug B metric data types (Sparkplug 3.0).</summary>
public enum SparkplugDataType : uint
{
    /// <summary>Unknown.</summary>
    Unknown = 0,
    /// <summary>Signed 8-bit.</summary>
    Int8 = 1,
    /// <summary>Signed 16-bit.</summary>
    Int16 = 2,
    /// <summary>Signed 32-bit.</summary>
    Int32 = 3,
    /// <summary>Signed 64-bit.</summary>
    Int64 = 4,
    /// <summary>Unsigned 8-bit.</summary>
    UInt8 = 5,
    /// <summary>Unsigned 16-bit.</summary>
    UInt16 = 6,
    /// <summary>Unsigned 32-bit.</summary>
    UInt32 = 7,
    /// <summary>Unsigned 64-bit.</summary>
    UInt64 = 8,
    /// <summary>32-bit float.</summary>
    Float = 9,
    /// <summary>64-bit float.</summary>
    Double = 10,
    /// <summary>Boolean.</summary>
    Boolean = 11,
    /// <summary>UTF-8 string.</summary>
    String = 12,
    /// <summary>Milliseconds since the Unix epoch.</summary>
    DateTime = 13,
    /// <summary>Text.</summary>
    Text = 14,
    /// <summary>UUID as text.</summary>
    Uuid = 15,
    /// <summary>Data set (not decoded).</summary>
    DataSet = 16,
    /// <summary>Bytes.</summary>
    Bytes = 17,
    /// <summary>File.</summary>
    File = 18,
    /// <summary>Template (not decoded).</summary>
    Template = 19,
}

/// <summary>A metric: name and/or alias, type, value and flags.</summary>
public sealed record SparkplugMetric
{
    /// <summary>Name (required in births; data messages may send only the alias).</summary>
    public string? Name { get; init; }

    /// <summary>Alias assigned in the birth.</summary>
    public ulong? Alias { get; init; }

    /// <summary>Timestamp (ms since the epoch).</summary>
    public ulong? Timestamp { get; init; }

    /// <summary>Data type.</summary>
    public SparkplugDataType DataType { get; init; }

    /// <summary>Historical value (not the current state).</summary>
    public bool IsHistorical { get; init; }

    /// <summary>Transient value (do not store).</summary>
    public bool IsTransient { get; init; }

    /// <summary>The value is null.</summary>
    public bool IsNull { get; init; }

    /// <summary>The value: long, ulong, float, double, bool, string, byte[] or <see cref="DateTimeOffset"/>.</summary>
    public object? Value { get; init; }

    /// <summary>Creates a metric with a typed value.</summary>
    public static SparkplugMetric Of(string name, SparkplugDataType type, object? value, ulong? alias = null) =>
        new() { Name = name, Alias = alias, DataType = type, Value = value, IsNull = value is null };

    /// <summary>The value as a double (numbers and booleans).</summary>
    public double? AsDouble() => Value switch
    {
        long l => l,
        ulong u => u,
        float f => f,
        double d => d,
        bool b => b ? 1 : 0,
        _ => null,
    };

    /// <inheritdoc />
    public override string ToString() =>
        $"{Name ?? $"#{Alias}"} = {(IsNull ? "null" : Value switch { byte[] b => Convert.ToHexString(b), DateTimeOffset t => t.ToString("O", CultureInfo.InvariantCulture), IFormattable f => f.ToString(null, CultureInfo.InvariantCulture), var v => v?.ToString() })} ({DataType})";
}

/// <summary>A Sparkplug B payload: timestamp, metrics, sequence number, uuid and body.</summary>
public sealed record SparkplugPayload
{
    /// <summary>Timestamp (ms since the epoch).</summary>
    public ulong? Timestamp { get; init; }

    /// <summary>Metrics.</summary>
    public IReadOnlyList<SparkplugMetric> Metrics { get; init; } = [];

    /// <summary>Sequence number (0–255), absent in NDEATH.</summary>
    public ulong? Seq { get; init; }

    /// <summary>UUID describing the body.</summary>
    public string? Uuid { get; init; }

    /// <summary>Opaque body.</summary>
    public byte[]? Body { get; init; }

    /// <summary>Milliseconds since the epoch for <paramref name="time"/>.</summary>
    public static ulong Millis(DateTimeOffset time) => (ulong)time.ToUnixTimeMilliseconds();

    /// <summary>Encodes the payload (Protobuf, Sparkplug B field numbers).</summary>
    public byte[] Encode()
    {
        using var ms = new MemoryStream();
        var o = new CodedOutputStream(ms);
        if (Timestamp is { } ts)
        {
            o.WriteTag(1, WireFormat.WireType.Varint);
            o.WriteUInt64(ts);
        }

        foreach (var m in Metrics)
        {
            o.WriteTag(2, WireFormat.WireType.LengthDelimited);
            o.WriteBytes(ByteString.CopyFrom(EncodeMetric(m)));
        }

        if (Seq is { } seq)
        {
            o.WriteTag(3, WireFormat.WireType.Varint);
            o.WriteUInt64(seq);
        }

        if (Uuid is { } uuid)
        {
            o.WriteTag(4, WireFormat.WireType.LengthDelimited);
            o.WriteString(uuid);
        }

        if (Body is { } body)
        {
            o.WriteTag(5, WireFormat.WireType.LengthDelimited);
            o.WriteBytes(ByteString.CopyFrom(body));
        }

        o.Flush();
        return ms.ToArray();
    }

    private static byte[] EncodeMetric(SparkplugMetric m)
    {
        using var ms = new MemoryStream();
        var o = new CodedOutputStream(ms);
        if (m.Name is { } name)
        {
            o.WriteTag(1, WireFormat.WireType.LengthDelimited);
            o.WriteString(name);
        }

        if (m.Alias is { } alias)
        {
            o.WriteTag(2, WireFormat.WireType.Varint);
            o.WriteUInt64(alias);
        }

        if (m.Timestamp is { } ts)
        {
            o.WriteTag(3, WireFormat.WireType.Varint);
            o.WriteUInt64(ts);
        }

        o.WriteTag(4, WireFormat.WireType.Varint);
        o.WriteUInt32((uint)m.DataType);
        if (m.IsHistorical)
        {
            o.WriteTag(5, WireFormat.WireType.Varint);
            o.WriteBool(true);
        }

        if (m.IsTransient)
        {
            o.WriteTag(6, WireFormat.WireType.Varint);
            o.WriteBool(true);
        }

        if (m.IsNull || m.Value is null)
        {
            o.WriteTag(7, WireFormat.WireType.Varint);
            o.WriteBool(true);
        }
        else
        {
            WriteValue(o, m.DataType, m.Value);
        }

        o.Flush();
        return ms.ToArray();
    }

    private static void WriteValue(CodedOutputStream o, SparkplugDataType type, object value)
    {
        switch (type)
        {
            case SparkplugDataType.Int8 or SparkplugDataType.Int16 or SparkplugDataType.Int32:
                o.WriteTag(10, WireFormat.WireType.Varint);
                o.WriteUInt32(unchecked((uint)Convert.ToInt32(value, CultureInfo.InvariantCulture)));
                break;
            case SparkplugDataType.UInt8 or SparkplugDataType.UInt16 or SparkplugDataType.UInt32:
                o.WriteTag(10, WireFormat.WireType.Varint);
                o.WriteUInt32(Convert.ToUInt32(value, CultureInfo.InvariantCulture));
                break;
            case SparkplugDataType.Int64:
                o.WriteTag(11, WireFormat.WireType.Varint);
                o.WriteUInt64(unchecked((ulong)Convert.ToInt64(value, CultureInfo.InvariantCulture)));
                break;
            case SparkplugDataType.UInt64:
                o.WriteTag(11, WireFormat.WireType.Varint);
                o.WriteUInt64(Convert.ToUInt64(value, CultureInfo.InvariantCulture));
                break;
            case SparkplugDataType.DateTime:
                o.WriteTag(11, WireFormat.WireType.Varint);
                o.WriteUInt64(value is DateTimeOffset t ? Millis(t) : Convert.ToUInt64(value, CultureInfo.InvariantCulture));
                break;
            case SparkplugDataType.Float:
                o.WriteTag(12, WireFormat.WireType.Fixed32);
                o.WriteFloat(Convert.ToSingle(value, CultureInfo.InvariantCulture));
                break;
            case SparkplugDataType.Double:
                o.WriteTag(13, WireFormat.WireType.Fixed64);
                o.WriteDouble(Convert.ToDouble(value, CultureInfo.InvariantCulture));
                break;
            case SparkplugDataType.Boolean:
                o.WriteTag(14, WireFormat.WireType.Varint);
                o.WriteBool(Convert.ToBoolean(value, CultureInfo.InvariantCulture));
                break;
            case SparkplugDataType.String or SparkplugDataType.Text or SparkplugDataType.Uuid:
                o.WriteTag(15, WireFormat.WireType.LengthDelimited);
                o.WriteString(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
                break;
            case SparkplugDataType.Bytes or SparkplugDataType.File:
                o.WriteTag(16, WireFormat.WireType.LengthDelimited);
                o.WriteBytes(ByteString.CopyFrom((byte[])value));
                break;
            default:
                throw new NotSupportedException($"Encoding {type} metrics is not supported.");
        }
    }

    /// <summary>Decodes a payload; unknown fields (data sets, templates, properties) are skipped.</summary>
    public static SparkplugPayload Decode(ReadOnlySpan<byte> data)
    {
        try
        {
            var input = new CodedInputStream(data.ToArray());
            ulong? timestamp = null, seq = null;
            string? uuid = null;
            byte[]? body = null;
            var metrics = new List<SparkplugMetric>();
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                switch (WireFormat.GetTagFieldNumber(tag))
                {
                    case 1:
                        timestamp = input.ReadUInt64();
                        break;
                    case 2:
                        metrics.Add(DecodeMetric(input.ReadBytes().ToByteArray()));
                        break;
                    case 3:
                        seq = input.ReadUInt64();
                        break;
                    case 4:
                        uuid = input.ReadString();
                        break;
                    case 5:
                        body = input.ReadBytes().ToByteArray();
                        break;
                    default:
                        input.SkipLastField();
                        break;
                }
            }

            return new SparkplugPayload { Timestamp = timestamp, Metrics = metrics, Seq = seq, Uuid = uuid, Body = body };
        }
        catch (InvalidProtocolBufferException ex)
        {
            throw new ProtocolException($"Invalid Sparkplug payload: {ex.Message}", ex);
        }
    }

    private static SparkplugMetric DecodeMetric(byte[] bytes)
    {
        var input = new CodedInputStream(bytes);
        string? name = null;
        ulong? alias = null, timestamp = null;
        var type = SparkplugDataType.Unknown;
        bool historical = false, transient = false, isNull = false;
        uint? intValue = null;
        ulong? longValue = null;
        object? other = null;
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 1: name = input.ReadString(); break;
                case 2: alias = input.ReadUInt64(); break;
                case 3: timestamp = input.ReadUInt64(); break;
                case 4: type = (SparkplugDataType)input.ReadUInt32(); break;
                case 5: historical = input.ReadBool(); break;
                case 6: transient = input.ReadBool(); break;
                case 7: isNull = input.ReadBool(); break;
                case 10: intValue = input.ReadUInt32(); break;
                case 11: longValue = input.ReadUInt64(); break;
                case 12: other = input.ReadFloat(); break;
                case 13: other = input.ReadDouble(); break;
                case 14: other = input.ReadBool(); break;
                case 15: other = input.ReadString(); break;
                case 16: other = input.ReadBytes().ToByteArray(); break;
                default: input.SkipLastField(); break;
            }
        }

        object? value = isNull ? null : type switch
        {
            SparkplugDataType.Int8 => intValue is { } i ? (long)unchecked((sbyte)i) : null,
            SparkplugDataType.Int16 => intValue is { } i ? (long)unchecked((short)i) : null,
            SparkplugDataType.Int32 => intValue is { } i ? (long)unchecked((int)i) : null,
            SparkplugDataType.UInt8 or SparkplugDataType.UInt16 or SparkplugDataType.UInt32 => intValue is { } u ? (ulong)u : null,
            SparkplugDataType.Int64 => longValue is { } l ? unchecked((long)l) : null,
            SparkplugDataType.UInt64 => longValue,
            SparkplugDataType.DateTime => longValue is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds((long)ms) : null,
            _ => other,
        };
        return new SparkplugMetric { Name = name, Alias = alias, Timestamp = timestamp, DataType = type, IsHistorical = historical, IsTransient = transient, IsNull = isNull, Value = value };
    }

    /// <summary>Frame-lane fields: timestamp, one field per metric (decoded), sequence number, uuid and body.</summary>
    public static IReadOnlyList<FrameField> Describe(ReadOnlySpan<byte> data)
    {
        var fields = new List<FrameField>();
        var input = new CodedInputStream(data.ToArray());
        var start = 0;
        try
        {
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                var number = WireFormat.GetTagFieldNumber(tag);
                switch (number)
                {
                    case 1:
                        var ts = input.ReadUInt64();
                        fields.Add(new FrameField("Timestamp", start, (int)input.Position - start, FrameFieldKind.Header, DateTimeOffset.FromUnixTimeMilliseconds((long)ts).ToString("O", CultureInfo.InvariantCulture)));
                        break;
                    case 2:
                        var metric = DecodeMetric(input.ReadBytes().ToByteArray());
                        fields.Add(new FrameField("Metric", start, (int)input.Position - start, FrameFieldKind.Data, metric.ToString()));
                        break;
                    case 3:
                        var seq = input.ReadUInt64();
                        fields.Add(new FrameField("Seq", start, (int)input.Position - start, FrameFieldKind.Address, seq.ToString(CultureInfo.InvariantCulture)));
                        break;
                    default:
                        input.SkipLastField();
                        fields.Add(new FrameField(number switch { 4 => "UUID", 5 => "Body", _ => $"#{number}" }, start, (int)input.Position - start, FrameFieldKind.Header));
                        break;
                }

                start = (int)input.Position;
            }
        }
        catch (InvalidProtocolBufferException ex)
        {
            fields.Add(new FrameField("Invalid", start, data.Length - start, FrameFieldKind.Error, ex.Message));
        }

        return fields;
    }

    /// <inheritdoc />
    public override string ToString() => $"seq {Seq?.ToString(CultureInfo.InvariantCulture) ?? "-"}: {string.Join(", ", Metrics)}";
}

/// <summary>Sparkplug B message types.</summary>
public enum SparkplugMessageType
{
    /// <summary>Edge node birth.</summary>
    NBirth,
    /// <summary>Edge node death.</summary>
    NDeath,
    /// <summary>Device birth.</summary>
    DBirth,
    /// <summary>Device death.</summary>
    DDeath,
    /// <summary>Edge node data.</summary>
    NData,
    /// <summary>Device data.</summary>
    DData,
    /// <summary>Command to an edge node.</summary>
    NCmd,
    /// <summary>Command to a device.</summary>
    DCmd,
    /// <summary>Host application state.</summary>
    State,
}

/// <summary>A Sparkplug topic: <c>spBv1.0/{group}/{type}/{edge node}[/{device}]</c> or <c>spBv1.0/STATE/{host}</c>.</summary>
/// <param name="Type">Message type.</param>
/// <param name="Group">Group id.</param>
/// <param name="EdgeNode">Edge node id (host id for STATE).</param>
/// <param name="Device">Device id, if any.</param>
public sealed record SparkplugTopic(SparkplugMessageType Type, string Group, string EdgeNode, string? Device = null)
{
    /// <summary>The namespace prefix.</summary>
    public const string Namespace = "spBv1.0";

    /// <summary>Parses a topic; returns false for topics outside the namespace.</summary>
    public static bool TryParse(string topic, out SparkplugTopic? parsed)
    {
        parsed = null;
        var p = topic?.Split('/') ?? [];
        if (p.Length < 3 || p[0] != Namespace) return false;
        if (p[1] == "STATE" && p.Length == 3)
        {
            parsed = new SparkplugTopic(SparkplugMessageType.State, "", p[2]);
            return true;
        }

        if (p.Length is < 4 or > 5) return false;
        SparkplugMessageType? type = p[2] switch
        {
            "NBIRTH" => SparkplugMessageType.NBirth, "NDEATH" => SparkplugMessageType.NDeath, "DBIRTH" => SparkplugMessageType.DBirth,
            "DDEATH" => SparkplugMessageType.DDeath, "NDATA" => SparkplugMessageType.NData, "DDATA" => SparkplugMessageType.DData,
            "NCMD" => SparkplugMessageType.NCmd, "DCMD" => SparkplugMessageType.DCmd, _ => null,
        };
        if (type is null) return false;
        parsed = new SparkplugTopic(type.Value, p[1], p[3], p.Length == 5 ? p[4] : null);
        return true;
    }

    /// <inheritdoc />
    public override string ToString() => Type == SparkplugMessageType.State
        ? $"{Namespace}/STATE/{EdgeNode}"
        : $"{Namespace}/{Group}/{Type.ToString().ToUpperInvariant()}/{EdgeNode}{(Device is null ? "" : "/" + Device)}";
}
