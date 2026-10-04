using System.Buffers;
using System.Formats.Cbor;
using System.Text.Json;

namespace IoTCom.Net.Serialization.SenML;

/// <summary>A SenML record as it appears on the wire (RFC 8428 §4). Base fields apply to following records.</summary>
public sealed record SenMLRecord
{
    /// <summary>bn — base name.</summary>
    public string? BaseName { get; init; }
    /// <summary>bt — base time (seconds).</summary>
    public double? BaseTime { get; init; }
    /// <summary>bu — base unit.</summary>
    public string? BaseUnit { get; init; }
    /// <summary>bv — base value.</summary>
    public double? BaseValue { get; init; }
    /// <summary>bs — base sum.</summary>
    public double? BaseSum { get; init; }
    /// <summary>bver — base version.</summary>
    public int? BaseVersion { get; init; }
    /// <summary>n — name.</summary>
    public string? Name { get; init; }
    /// <summary>u — unit (SenML units registry, e.g. <c>Cel</c>, <c>%RH</c>, <c>W</c>).</summary>
    public string? Unit { get; init; }
    /// <summary>v — numeric value.</summary>
    public double? Value { get; init; }
    /// <summary>vs — string value.</summary>
    public string? StringValue { get; init; }
    /// <summary>vb — boolean value.</summary>
    public bool? BoolValue { get; init; }
    /// <summary>vd — data value (base64url in JSON, byte string in CBOR).</summary>
    public byte[]? DataValue { get; init; }
    /// <summary>s — sum.</summary>
    public double? Sum { get; init; }
    /// <summary>t — time (absolute when ≥ 2^28, else relative to now).</summary>
    public double? Time { get; init; }
    /// <summary>ut — update time (max seconds before the next value).</summary>
    public double? UpdateTime { get; init; }
}

/// <summary>A record after applying base fields and converting time (RFC 8428 §4.6).</summary>
public sealed record SenMLResolvedRecord(string Name, string? Unit, double? Value, string? StringValue, bool? BoolValue, byte[]? DataValue, double? Sum, DateTimeOffset Time, double? UpdateTime);

/// <summary>SenML encoders, decoders and resolution.</summary>
public static class SenMLCodec
{
    /// <summary>JSON media type.</summary>
    public const string JsonContentType = "application/senml+json";
    /// <summary>CBOR media type.</summary>
    public const string CborContentType = "application/senml+cbor";

    private const double RelativeTimeLimit = 268_435_456; // 2^28

    /// <summary>Encodes a pack as SenML JSON.</summary>
    public static byte[] ToJson(IEnumerable<SenMLRecord> pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartArray();
            foreach (var r in pack)
            {
                w.WriteStartObject();
                if (r.BaseName is not null) w.WriteString("bn", r.BaseName);
                if (r.BaseTime is { } bt) w.WriteNumber("bt", bt);
                if (r.BaseUnit is not null) w.WriteString("bu", r.BaseUnit);
                if (r.BaseValue is { } bv) w.WriteNumber("bv", bv);
                if (r.BaseSum is { } bs) w.WriteNumber("bs", bs);
                if (r.BaseVersion is { } ver) w.WriteNumber("bver", ver);
                if (r.Name is not null) w.WriteString("n", r.Name);
                if (r.Unit is not null) w.WriteString("u", r.Unit);
                if (r.Value is { } v) w.WriteNumber("v", v);
                if (r.StringValue is not null) w.WriteString("vs", r.StringValue);
                if (r.BoolValue is { } vb) w.WriteBoolean("vb", vb);
                if (r.DataValue is not null) w.WriteString("vd", Base64Url(r.DataValue));
                if (r.Sum is { } s) w.WriteNumber("s", s);
                if (r.Time is { } t) w.WriteNumber("t", t);
                if (r.UpdateTime is { } ut) w.WriteNumber("ut", ut);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Decodes SenML JSON.</summary>
    /// <exception cref="ProtocolException">Malformed pack.</exception>
    public static IReadOnlyList<SenMLRecord> ParseJson(ReadOnlySpan<byte> json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json.ToArray());
            if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new ProtocolException("SenML JSON must be an array.");
            var list = new List<SenMLRecord>();
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) throw new ProtocolException("SenML records must be objects.");
                list.Add(new SenMLRecord
                {
                    BaseName = Str(e, "bn"), BaseTime = Num(e, "bt"), BaseUnit = Str(e, "bu"), BaseValue = Num(e, "bv"), BaseSum = Num(e, "bs"),
                    BaseVersion = e.TryGetProperty("bver", out var ver) ? ver.GetInt32() : null,
                    Name = Str(e, "n"), Unit = Str(e, "u"), Value = Num(e, "v"), StringValue = Str(e, "vs"),
                    BoolValue = e.TryGetProperty("vb", out var vb) ? vb.GetBoolean() : null,
                    DataValue = Str(e, "vd") is { } vd ? FromBase64Url(vd) : null,
                    Sum = Num(e, "s"), Time = Num(e, "t"), UpdateTime = Num(e, "ut"),
                });
            }
            return list;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw new ProtocolException($"Invalid SenML JSON: {ex.Message}", ex);
        }

        static string? Str(JsonElement e, string p) => e.TryGetProperty(p, out var v) ? v.GetString() : null;
        static double? Num(JsonElement e, string p) => e.TryGetProperty(p, out var v) ? v.GetDouble() : null;
    }

    /// <summary>Encodes a pack as SenML CBOR (integer labels, RFC 8428 §6).</summary>
    public static byte[] ToCbor(IEnumerable<SenMLRecord> pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        var records = pack.ToList();
        var w = new CborWriter(CborConformanceMode.Lax);
        w.WriteStartArray(records.Count);
        foreach (var r in records)
        {
            var fields = new List<Action>();
            void Add(int label, Action write) => fields.Add(() => { w.WriteInt32(label); write(); });
            if (r.BaseVersion is { } ver) Add(-1, () => w.WriteInt32(ver));
            if (r.BaseName is not null) Add(-2, () => w.WriteTextString(r.BaseName));
            if (r.BaseTime is { } bt) Add(-3, () => WriteNumber(w, bt));
            if (r.BaseUnit is not null) Add(-4, () => w.WriteTextString(r.BaseUnit));
            if (r.BaseValue is { } bv) Add(-5, () => WriteNumber(w, bv));
            if (r.BaseSum is { } bs) Add(-6, () => WriteNumber(w, bs));
            if (r.Name is not null) Add(0, () => w.WriteTextString(r.Name));
            if (r.Unit is not null) Add(1, () => w.WriteTextString(r.Unit));
            if (r.Value is { } v) Add(2, () => WriteNumber(w, v));
            if (r.StringValue is not null) Add(3, () => w.WriteTextString(r.StringValue));
            if (r.BoolValue is { } vb) Add(4, () => w.WriteBoolean(vb));
            if (r.Sum is { } s) Add(5, () => WriteNumber(w, s));
            if (r.Time is { } t) Add(6, () => WriteNumber(w, t));
            if (r.UpdateTime is { } ut) Add(7, () => WriteNumber(w, ut));
            if (r.DataValue is not null) Add(8, () => w.WriteByteString(r.DataValue));
            w.WriteStartMap(fields.Count);
            foreach (var f in fields) f();
            w.WriteEndMap();
        }
        w.WriteEndArray();
        return w.Encode();
    }

    /// <summary>Decodes SenML CBOR.</summary>
    /// <exception cref="ProtocolException">Malformed pack.</exception>
    public static IReadOnlyList<SenMLRecord> ParseCbor(ReadOnlyMemory<byte> cbor)
    {
        try
        {
            var r = new CborReader(cbor, CborConformanceMode.Lax);
            var count = r.ReadStartArray();
            var list = new List<SenMLRecord>();
            while (r.PeekState() != CborReaderState.EndArray)
            {
                var rec = new SenMLRecord();
                r.ReadStartMap();
                while (r.PeekState() != CborReaderState.EndMap)
                {
                    var label = r.ReadInt32();
                    rec = label switch
                    {
                        -1 => rec with { BaseVersion = r.ReadInt32() },
                        -2 => rec with { BaseName = r.ReadTextString() },
                        -3 => rec with { BaseTime = ReadNumber(r) },
                        -4 => rec with { BaseUnit = r.ReadTextString() },
                        -5 => rec with { BaseValue = ReadNumber(r) },
                        -6 => rec with { BaseSum = ReadNumber(r) },
                        0 => rec with { Name = r.ReadTextString() },
                        1 => rec with { Unit = r.ReadTextString() },
                        2 => rec with { Value = ReadNumber(r) },
                        3 => rec with { StringValue = r.ReadTextString() },
                        4 => rec with { BoolValue = r.ReadBoolean() },
                        5 => rec with { Sum = ReadNumber(r) },
                        6 => rec with { Time = ReadNumber(r) },
                        7 => rec with { UpdateTime = ReadNumber(r) },
                        8 => rec with { DataValue = r.ReadByteString() },
                        _ => Skip(r, rec),
                    };
                }
                r.ReadEndMap();
                list.Add(rec);
            }
            r.ReadEndArray();
            _ = count;
            return list;
        }
        catch (Exception ex) when (ex is CborContentException or InvalidOperationException or OverflowException)
        {
            throw new ProtocolException($"Invalid SenML CBOR: {ex.Message}", ex);
        }

        static SenMLRecord Skip(CborReader r, SenMLRecord rec)
        {
            r.SkipValue();
            return rec;
        }
    }

    /// <summary>Resolves base fields and times into self-contained records (RFC 8428 §4.6).</summary>
    public static IReadOnlyList<SenMLResolvedRecord> Resolve(IEnumerable<SenMLRecord> pack, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(pack);
        var nowSeconds = (now ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds() / 1000.0;
        string bn = string.Empty, bu = string.Empty;
        double bt = 0, bv = 0, bs = 0;
        var result = new List<SenMLResolvedRecord>();
        foreach (var r in pack)
        {
            if (r.BaseName is not null) bn = r.BaseName;
            if (r.BaseUnit is not null) bu = r.BaseUnit;
            if (r.BaseTime is { } t0) bt = t0;
            if (r.BaseValue is { } v0) bv = v0;
            if (r.BaseSum is { } s0) bs = s0;
            var hasValue = r.Value.HasValue || r.StringValue is not null || r.BoolValue.HasValue || r.DataValue is not null || r.Sum.HasValue;
            if (!hasValue && r.Name is null) continue; // pure base record
            var name = bn + (r.Name ?? string.Empty);
            var time = bt + (r.Time ?? 0);
            if (time < RelativeTimeLimit) time += nowSeconds;
            result.Add(new SenMLResolvedRecord(
                name,
                r.Unit ?? (bu.Length > 0 ? bu : null),
                r.Value.HasValue ? bv + r.Value.Value : null,
                r.StringValue, r.BoolValue, r.DataValue,
                r.Sum.HasValue ? bs + r.Sum.Value : null,
                DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(time * 1000)),
                r.UpdateTime));
        }
        return result;
    }

    private static void WriteNumber(CborWriter w, double value)
    {
        if (value == Math.Floor(value) && Math.Abs(value) < long.MaxValue) w.WriteInt64((long)value);
        else w.WriteDouble(value);
    }

    private static double ReadNumber(CborReader r) => r.PeekState() switch
    {
        CborReaderState.UnsignedInteger or CborReaderState.NegativeInteger => r.ReadInt64(),
        CborReaderState.HalfPrecisionFloat => (double)r.ReadHalf(),
        CborReaderState.SinglePrecisionFloat => r.ReadSingle(),
        _ => r.ReadDouble(),
    };

    private static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string s)
    {
        var b64 = s.Replace('-', '+').Replace('_', '/');
        b64 += (b64.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Convert.FromBase64String(b64);
    }
}

/// <summary>Fluent builder for a SenML pack sharing a base name and base time.</summary>
/// <example>
/// <code>
/// var pack = new SenMLPackBuilder("urn:dev:mac:0024befffe804ff1/")
///     .At(DateTimeOffset.UtcNow)
///     .Add("temperature", 23.5, "Cel")
///     .Add("humidity", 61, "%RH")
///     .Build();
/// byte[] json = SenMLCodec.ToJson(pack);
/// </code>
/// </example>
public sealed class SenMLPackBuilder(string? baseName = null)
{
    private readonly List<SenMLRecord> _records = [];
    private double? _baseTime;

    /// <summary>Sets the base time.</summary>
    public SenMLPackBuilder At(DateTimeOffset time)
    {
        _baseTime = time.ToUnixTimeMilliseconds() / 1000.0;
        return this;
    }

    /// <summary>Adds a numeric measurement.</summary>
    public SenMLPackBuilder Add(string name, double value, string? unit = null, double? relativeTime = null)
    {
        _records.Add(new SenMLRecord { Name = name, Value = value, Unit = unit, Time = relativeTime });
        return this;
    }

    /// <summary>Adds a string value.</summary>
    public SenMLPackBuilder Add(string name, string value)
    {
        _records.Add(new SenMLRecord { Name = name, StringValue = value });
        return this;
    }

    /// <summary>Adds a boolean value.</summary>
    public SenMLPackBuilder Add(string name, bool value)
    {
        _records.Add(new SenMLRecord { Name = name, BoolValue = value });
        return this;
    }

    /// <summary>Builds the pack (base fields go on the first record).</summary>
    public IReadOnlyList<SenMLRecord> Build()
    {
        if (_records.Count == 0) return [new SenMLRecord { BaseName = baseName, BaseTime = _baseTime }];
        var list = _records.ToList();
        list[0] = list[0] with { BaseName = baseName, BaseTime = _baseTime };
        return list;
    }
}
