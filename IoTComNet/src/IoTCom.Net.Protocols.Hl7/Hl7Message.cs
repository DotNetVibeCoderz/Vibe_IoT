using System.Globalization;
using System.Text;

namespace IoTCom.Net.Protocols.Hl7;

/// <summary>HL7 v2 encoding characters (from MSH-1 and MSH-2).</summary>
public sealed record Hl7Delimiters(char Field = '|', char Component = '^', char Repetition = '~', char Escape = '\\', char SubComponent = '&')
{
    /// <summary>The standard delimiters <c>|^~\&amp;</c>.</summary>
    public static Hl7Delimiters Default { get; } = new();

    /// <summary>MSH-2 value.</summary>
    public string EncodingCharacters => $"{Component}{Repetition}{Escape}{SubComponent}";
}

/// <summary>One segment (e.g. <c>PID</c>). Fields are kept raw (still escaped); accessors unescape.</summary>
public sealed class Hl7Segment
{
    private readonly List<string> _fields;
    private readonly Hl7Delimiters _d;

    internal Hl7Segment(string name, List<string> fields, Hl7Delimiters delimiters)
    {
        Name = name;
        _fields = fields;
        _d = delimiters;
    }

    /// <summary>Segment id, e.g. <c>OBX</c>.</summary>
    public string Name { get; }

    /// <summary>Number of fields (excluding the segment name).</summary>
    public int FieldCount => _fields.Count;

    /// <summary>
    /// Value at <paramref name="field"/> (1-based, as in the HL7 standard: PID-5 is <c>Get(5)</c>), optional component,
    /// sub-component and repetition (1-based). Returns an empty string when absent. For MSH, field numbers follow the
    /// standard (MSH-1 is the field separator, MSH-9 the message type).
    /// </summary>
    public string Get(int field, int component = 0, int subComponent = 0, int repetition = 1)
    {
        var raw = Raw(field);
        if (raw.Length == 0) return string.Empty;
        if (Name == "MSH" && field <= 2) return raw;
        var rep = Nth(raw, _d.Repetition, repetition);
        if (component > 0) rep = Nth(rep, _d.Component, component);
        if (subComponent > 0) rep = Nth(rep, _d.SubComponent, subComponent);
        return Hl7Escaping.Unescape(rep, _d);
    }

    /// <summary>Number of repetitions of a field.</summary>
    public int Repetitions(int field)
    {
        var raw = Raw(field);
        return raw.Length == 0 ? 0 : raw.Split(_d.Repetition).Length;
    }

    /// <summary>The raw (escaped) field text.</summary>
    public string Raw(int field)
    {
        if (Name == "MSH")
        {
            if (field == 1) return _d.Field.ToString();
            field -= 1; // MSH-1 is the separator itself, so MSH-2 is stored at index 0
        }
        return field >= 1 && field <= _fields.Count ? _fields[field - 1] : string.Empty;
    }

    /// <summary>Sets a raw field value (already escaped).</summary>
    public void SetRaw(int field, string value)
    {
        var index = Name == "MSH" ? field - 1 : field;
        while (_fields.Count < index) _fields.Add(string.Empty);
        _fields[index - 1] = value;
    }

    internal string Encode() => _fields.Count == 0 ? Name : Name + _d.Field + string.Join(_d.Field, _fields);

    /// <inheritdoc />
    public override string ToString() => Encode();

    private static string Nth(string value, char separator, int index)
    {
        if (index <= 1)
        {
            var end = value.IndexOf(separator, StringComparison.Ordinal);
            return end < 0 ? value : value[..end];
        }
        var parts = value.Split(separator);
        return index <= parts.Length ? parts[index - 1] : string.Empty;
    }
}

/// <summary>
/// An HL7 v2 message in ER7 (pipe) encoding. Parsing is tolerant: segment terminators may be CR, LF or CRLF,
/// and missing trailing fields read as empty strings.
/// </summary>
/// <example>
/// <code>
/// var msg = Hl7Message.Parse(text);
/// string type = msg.MessageType;          // "ORU^R01"
/// string mrn  = msg["PID.3.1"];           // patient id
/// foreach (var obs in msg.GetObservations()) Console.WriteLine($"{obs.Code.Text} = {obs.Value} {obs.Units}");
/// </code>
/// </example>
public sealed class Hl7Message
{
    private readonly List<Hl7Segment> _segments;

    private Hl7Message(List<Hl7Segment> segments, Hl7Delimiters delimiters)
    {
        _segments = segments;
        Delimiters = delimiters;
    }

    /// <summary>Encoding characters.</summary>
    public Hl7Delimiters Delimiters { get; }

    /// <summary>All segments in order.</summary>
    public IReadOnlyList<Hl7Segment> Segments => _segments;

    /// <summary>The MSH segment.</summary>
    public Hl7Segment Header => _segments[0];

    /// <summary>MSH-9, e.g. <c>ORU^R01</c> (without the message structure component).</summary>
    public string MessageType => Join(Header.Get(9, 1), Header.Get(9, 2));

    /// <summary>MSH-10 message control id.</summary>
    public string ControlId => Header.Get(10);

    /// <summary>MSH-12 version, e.g. <c>2.5.1</c>.</summary>
    public string Version => Header.Get(12);

    /// <summary>MSH-3 / MSH-4 sending application and facility.</summary>
    public (string Application, string Facility) Sender => (Header.Get(3, 1), Header.Get(4, 1));

    /// <summary>MSH-5 / MSH-6 receiving application and facility.</summary>
    public (string Application, string Facility) Receiver => (Header.Get(5, 1), Header.Get(6, 1));

    /// <summary>MSH-7 date/time of the message.</summary>
    public DateTimeOffset? Timestamp => Hl7Time.Parse(Header.Get(7));

    /// <summary>
    /// Terser-style access: <c>"PID.5.1"</c>, <c>"OBX(2).5"</c> (second OBX), <c>"PID.3.1"</c>, <c>"MSH.9.2"</c>.
    /// </summary>
    public string this[string path] => Get(path);

    /// <summary>Terser-style access (see indexer).</summary>
    public string Get(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var parts = path.Split('.');
        var segPart = parts[0];
        var occurrence = 1;
        var paren = segPart.IndexOf('(', StringComparison.Ordinal);
        if (paren > 0)
        {
            occurrence = int.Parse(segPart.AsSpan(paren + 1, segPart.Length - paren - 2), CultureInfo.InvariantCulture);
            segPart = segPart[..paren];
        }
        var segment = _segments.Where(s => s.Name == segPart).Skip(occurrence - 1).FirstOrDefault();
        if (segment is null || parts.Length < 2) return string.Empty;
        int At(int i) => parts.Length > i ? int.Parse(parts[i], CultureInfo.InvariantCulture) : 0;
        return segment.Get(At(1), At(2), At(3));
    }

    /// <summary>Segments with the given id.</summary>
    public IEnumerable<Hl7Segment> GetSegments(string name) => _segments.Where(s => s.Name == name);

    /// <summary>Parses ER7 text.</summary>
    /// <exception cref="ProtocolException">The text does not start with a valid MSH segment.</exception>
    public static Hl7Message Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text.Replace("\r\n", "\r", StringComparison.Ordinal).Replace('\n', '\r')
            .Split('\r', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0 || !lines[0].StartsWith("MSH", StringComparison.Ordinal) || lines[0].Length < 8)
            throw new ProtocolException("HL7 message must start with an MSH segment.");

        var h = lines[0];
        var enc = h[4..8];
        var d = new Hl7Delimiters(h[3], enc[0], enc[1], enc[2], enc.Length > 3 && h.Length > 7 ? enc[3] : '&');
        var segments = new List<Hl7Segment>(lines.Length);
        foreach (var line in lines)
        {
            var trimmed = line.TrimEnd('\x1C', '\x0B');
            if (trimmed.Length < 3) continue;
            var fields = trimmed.Split(d.Field).ToList();
            var name = fields[0];
            fields.RemoveAt(0);
            segments.Add(new Hl7Segment(name, fields, d));
        }
        return new Hl7Message(segments, d);
    }

    /// <summary>Parses, returning false instead of throwing.</summary>
    public static bool TryParse(string text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Hl7Message? message)
    {
        try
        {
            message = Parse(text);
            return true;
        }
        catch (Exception ex) when (ex is ProtocolException or ArgumentException or FormatException)
        {
            message = null;
            return false;
        }
    }

    /// <summary>Encodes to ER7 with CR segment terminators.</summary>
    public string Encode() => string.Join('\r', _segments.Select(s => s.ToString())) + "\r";

    /// <inheritdoc />
    public override string ToString() => Encode();

    /// <summary>Builds an acknowledgement (ACK) for this message.</summary>
    /// <param name="code"><c>AA</c> accept, <c>AE</c> error, <c>AR</c> reject (or CA/CE/CR in enhanced mode).</param>
    /// <param name="text">Optional MSA-3 text.</param>
    public Hl7Message CreateAck(string code = "AA", string? text = null)
    {
        var (app, fac) = Receiver;
        var (toApp, toFac) = Sender;
        return new Hl7MessageBuilder(Delimiters)
            .Header(app, fac, toApp, toFac, Join("ACK", Header.Get(9, 2)), version: Version.Length > 0 ? Version : "2.5.1")
            .Segment("MSA", code, ControlId, text ?? string.Empty)
            .Build();
    }

    internal static Hl7Message FromSegments(List<Hl7Segment> segments, Hl7Delimiters d) => new(segments, d);

    private static string Join(string a, string b) => b.Length > 0 ? $"{a}^{b}" : a;
}

/// <summary>Fluent builder producing well-formed, escaped HL7 messages.</summary>
public sealed class Hl7MessageBuilder(Hl7Delimiters? delimiters = null)
{
    private readonly Hl7Delimiters _d = delimiters ?? Hl7Delimiters.Default;
    private readonly List<Hl7Segment> _segments = [];

    /// <summary>Adds the MSH segment.</summary>
    /// <param name="sendingApp">MSH-3.</param>
    /// <param name="sendingFacility">MSH-4.</param>
    /// <param name="receivingApp">MSH-5.</param>
    /// <param name="receivingFacility">MSH-6.</param>
    /// <param name="messageType">MSH-9, e.g. <c>ORU^R01</c> (components separated by <c>^</c>).</param>
    /// <param name="controlId">MSH-10 (generated when null).</param>
    /// <param name="processingId">MSH-11 (<c>P</c> production, <c>T</c> training, <c>D</c> debug).</param>
    /// <param name="version">MSH-12.</param>
    /// <param name="timestamp">MSH-7 (now when null).</param>
    public Hl7MessageBuilder Header(string sendingApp, string sendingFacility, string receivingApp, string receivingFacility,
        string messageType, string? controlId = null, string processingId = "P", string version = "2.5.1", DateTimeOffset? timestamp = null)
    {
        var fields = new List<string>
        {
            _d.EncodingCharacters,
            Hl7Escaping.Escape(sendingApp, _d), Hl7Escaping.Escape(sendingFacility, _d),
            Hl7Escaping.Escape(receivingApp, _d), Hl7Escaping.Escape(receivingFacility, _d),
            Hl7Time.Format(timestamp ?? DateTimeOffset.Now), string.Empty,
            messageType.Replace('^', _d.Component),
            controlId ?? NewControlId(), processingId, version,
        };
        _segments.Insert(0, new Hl7Segment("MSH", fields, _d));
        return this;
    }

    /// <summary>Adds a segment. Each value is one field; use <c>^</c> inside a value for components (they are kept),
    /// all other delimiter characters are escaped.</summary>
    public Hl7MessageBuilder Segment(string name, params string?[] fields)
    {
        _segments.Add(new Hl7Segment(name, fields.Select(f => EscapeKeepingComponents(f ?? string.Empty)).ToList(), _d));
        return this;
    }

    /// <summary>Adds a PID segment for a patient.</summary>
    public Hl7MessageBuilder Patient(Hl7Patient p) => Segment("PID", "1", null, $"{p.Id}^^^{p.AssigningAuthority}^MR", null,
        $"{p.FamilyName}^{p.GivenName}", null, p.BirthDate?.ToString("yyyyMMdd", CultureInfo.InvariantCulture), p.Sex);

    /// <summary>Adds an OBX numeric observation.</summary>
    public Hl7MessageBuilder Observation(int setId, Hl7Observation o) => Segment("OBX",
        setId.ToString(CultureInfo.InvariantCulture), o.ValueType,
        $"{o.Code.Identifier}^{o.Code.Text}^{o.Code.System}", null, o.Value, o.Units, o.ReferenceRange,
        o.AbnormalFlag, null, null, o.Status, null, null, o.Timestamp is { } t ? Hl7Time.Format(t) : null);

    /// <summary>Builds the message.</summary>
    /// <exception cref="InvalidOperationException">No MSH segment was added.</exception>
    public Hl7Message Build()
    {
        if (_segments.Count == 0 || _segments[0].Name != "MSH") throw new InvalidOperationException("Call Header(...) first.");
        return Hl7Message.FromSegments(_segments.ToList(), _d);
    }

    private string EscapeKeepingComponents(string value) =>
        string.Join(_d.Component, value.Split('^').Select(part => Hl7Escaping.Escape(part, _d)));

    private static string NewControlId() => DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture) + Random.Shared.Next(1000, 9999).ToString(CultureInfo.InvariantCulture);
}

/// <summary>HL7 escape sequences (\F\ \S\ \T\ \R\ \E\ \Xhh..\ and .br).</summary>
public static class Hl7Escaping
{
    /// <summary>Escapes delimiter characters.</summary>
    public static string Escape(string value, Hl7Delimiters d)
    {
        if (string.IsNullOrEmpty(value) || value.IndexOfAny([d.Field, d.Component, d.Repetition, d.Escape, d.SubComponent, '\r', '\n']) < 0) return value;
        var sb = new StringBuilder(value.Length + 8);
        foreach (var c in value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'))
        {
            if (c == d.Escape) sb.Append(d.Escape).Append('E').Append(d.Escape);
            else if (c == d.Field) sb.Append(d.Escape).Append('F').Append(d.Escape);
            else if (c == d.Component) sb.Append(d.Escape).Append('S').Append(d.Escape);
            else if (c == d.SubComponent) sb.Append(d.Escape).Append('T').Append(d.Escape);
            else if (c == d.Repetition) sb.Append(d.Escape).Append('R').Append(d.Escape);
            else if (c == '\n') sb.Append(d.Escape).Append(".br").Append(d.Escape);
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Resolves escape sequences.</summary>
    public static string Unescape(string value, Hl7Delimiters d)
    {
        if (value.IndexOf(d.Escape, StringComparison.Ordinal) < 0) return value;
        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            var end = c == d.Escape ? value.IndexOf(d.Escape, i + 1) : -1;
            if (end < 0)
            {
                sb.Append(c);
                continue;
            }
            var code = value[(i + 1)..end];
            switch (code)
            {
                case "F": sb.Append(d.Field); break;
                case "S": sb.Append(d.Component); break;
                case "T": sb.Append(d.SubComponent); break;
                case "R": sb.Append(d.Repetition); break;
                case "E": sb.Append(d.Escape); break;
                case ".br": sb.Append('\n'); break;
                default:
                    if (code.Length > 1 && code[0] == 'X')
                    {
                        try { sb.Append(Encoding.Latin1.GetString(Convert.FromHexString(code[1..]))); }
                        catch (FormatException) { sb.Append(value, i, end - i + 1); }
                    }
                    else sb.Append(value, i, end - i + 1); // unknown escape: keep verbatim
                    break;
            }
            i = end;
        }
        return sb.ToString();
    }
}

/// <summary>HL7 DTM/TS date-time helpers (<c>YYYY[MM[DD[HH[MM[SS[.S+]]]]]][+/-ZZZZ]</c>).</summary>
public static class Hl7Time
{
    /// <summary>Formats with seconds and offset.</summary>
    public static string Format(DateTimeOffset t) => t.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + t.ToString("zzz", CultureInfo.InvariantCulture).Replace(":", "", StringComparison.Ordinal);

    /// <summary>Parses any HL7 precision; returns null when empty or invalid.</summary>
    public static DateTimeOffset? Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var offset = TimeSpan.Zero;
        var hasOffset = false;
        var sign = value.IndexOfAny(['+', '-'], 4);
        if (sign > 0 && value.Length - sign == 5)
        {
            var h = int.Parse(value.AsSpan(sign + 1, 2), CultureInfo.InvariantCulture);
            var m = int.Parse(value.AsSpan(sign + 3, 2), CultureInfo.InvariantCulture);
            offset = new TimeSpan(h, m, 0) * (value[sign] == '-' ? -1 : 1);
            hasOffset = true;
            value = value[..sign];
        }
        var dot = value.IndexOf('.', StringComparison.Ordinal);
        var fraction = 0.0;
        if (dot > 0)
        {
            double.TryParse("0" + value[dot..], NumberStyles.Float, CultureInfo.InvariantCulture, out fraction);
            value = value[..dot];
        }
        int Part(int start, int len, int def) => value.Length >= start + len && int.TryParse(value.AsSpan(start, len), NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : def;
        if (value.Length < 4) return null;
        try
        {
            var dt = new DateTime(Part(0, 4, 1), Part(4, 2, 1), Part(6, 2, 1), Part(8, 2, 0), Part(10, 2, 0), Part(12, 2, 0)).AddSeconds(fraction);
            return new DateTimeOffset(dt, hasOffset ? offset : TimeZoneInfo.Local.GetUtcOffset(dt));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
