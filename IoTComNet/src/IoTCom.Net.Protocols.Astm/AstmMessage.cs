using System.Globalization;
using System.Text;

namespace IoTCom.Net.Protocols.Astm;

/// <summary>ASTM E1394 / CLSI LIS2-A2 delimiters, declared in the header record (<c>H|\^&amp;</c>).</summary>
/// <param name="Field">Field delimiter.</param>
/// <param name="Repeat">Repeat delimiter.</param>
/// <param name="Component">Component delimiter.</param>
/// <param name="Escape">Escape character.</param>
public sealed record AstmDelimiters(char Field = '|', char Repeat = '\\', char Component = '^', char Escape = '&')
{
    /// <summary>The usual delimiters.</summary>
    public static AstmDelimiters Default { get; } = new();
}

/// <summary>One record: a type letter and fields (field 1 is the type; components are split on demand).</summary>
public sealed class AstmRecord
{
    private readonly List<string> _fields;

    /// <summary>Creates a record from raw fields (index 0 = type).</summary>
    public AstmRecord(IEnumerable<string> fields, AstmDelimiters delimiters)
    {
        ArgumentNullException.ThrowIfNull(fields);
        _fields = [.. fields];
        Delimiters = delimiters ?? AstmDelimiters.Default;
        if (_fields.Count == 0 || _fields[0].Length == 0) throw new FormatException("An ASTM record starts with its type letter.");
    }

    /// <summary>Delimiters.</summary>
    public AstmDelimiters Delimiters { get; }

    /// <summary>Record type: H, P, O, R, C, Q, M, S or L.</summary>
    public char Type => _fields[0][0];

    /// <summary>Number of fields including the type.</summary>
    public int Count => _fields.Count;

    /// <summary>Field <paramref name="index"/> (1-based as in the standard: 1 = type, 2 = sequence, …) or "".</summary>
    public string this[int index] => index >= 1 && index <= _fields.Count ? _fields[index - 1] : "";

    /// <summary>Component <paramref name="component"/> (1-based) of field <paramref name="index"/>.</summary>
    public string Component(int index, int component)
    {
        var parts = this[index].Split(Delimiters.Component);
        return component >= 1 && component <= parts.Length ? parts[component - 1] : "";
    }

    /// <inheritdoc />
    public override string ToString() => string.Join(Delimiters.Field, _fields);
}

/// <summary>A result record interpreted (R).</summary>
/// <param name="Sequence">Sequence number.</param>
/// <param name="TestCode">Analyte code from the universal test ID (fourth component, e.g. "GLU").</param>
/// <param name="Value">Measurement as text.</param>
/// <param name="Units">Units.</param>
/// <param name="ReferenceRange">Reference range ("70 to 99").</param>
/// <param name="Flag">Abnormal flag: N, L, H, LL, HH, &lt;, &gt;, A.</param>
/// <param name="Status">Result status: F final, C correction, P preliminary, X cannot be done.</param>
/// <param name="Completed">Date/time the test was completed.</param>
public sealed record AstmResult(int Sequence, string TestCode, string Value, string Units, string ReferenceRange, string Flag, string Status, DateTime? Completed)
{
    /// <summary>Numeric value, when the result is a number.</summary>
    public double? Number => double.TryParse(Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
}

/// <summary>
/// An ASTM E1394 / LIS2-A2 message: header (H), patients (P), orders (O), results (R), comments (C), queries (Q) and
/// the terminator (L), with the delimiters from the header. Parses and builds the record layer; the transport layer
/// (E1381 frames) is handled by <see cref="AstmLink"/>.
/// </summary>
public sealed class AstmMessage
{
    /// <summary>Creates a message from records.</summary>
    public AstmMessage(IReadOnlyList<AstmRecord> records)
    {
        Records = records ?? throw new ArgumentNullException(nameof(records));
        if (records.Count == 0 || records[0].Type != 'H') throw new FormatException("An ASTM message starts with a header record (H).");
    }

    /// <summary>Records in order.</summary>
    public IReadOnlyList<AstmRecord> Records { get; }

    /// <summary>The header.</summary>
    public AstmRecord Header => Records[0];

    /// <summary>Sender name (H field 5, first component).</summary>
    public string Sender => Header.Component(5, 1);

    /// <summary>Records of one type.</summary>
    public IEnumerable<AstmRecord> OfType(char type) => Records.Where(r => r.Type == type);

    /// <summary>Patient id (P field 3, practice assigned).</summary>
    public string? PatientId => OfType('P').FirstOrDefault() is { } p ? p[3] is { Length: > 0 } id ? id : p[4] : null;

    /// <summary>Patient name "family^given" (P field 6).</summary>
    public string? PatientName => OfType('P').FirstOrDefault()?[6];

    /// <summary>Specimen id (O field 3).</summary>
    public string? SpecimenId => OfType('O').FirstOrDefault()?[3];

    /// <summary>Results.</summary>
    public IReadOnlyList<AstmResult> Results => [.. OfType('R').Select(r => new AstmResult(
        int.TryParse(r[2], CultureInfo.InvariantCulture, out var seq) ? seq : 0,
        r.Component(3, 4) is { Length: > 0 } code ? code : r[3].Trim(r.Delimiters.Component),
        r[4], r[5], r[6], r[7], r[9], AstmTime.Parse(r[13])))];

    /// <summary>Parses records separated by CR (CR/LF tolerated).</summary>
    public static AstmMessage Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text.Split('\r', '\n').Where(l => l.Length > 0).ToList();
        if (lines.Count == 0 || lines[0].Length < 5 || lines[0][0] != 'H') throw new FormatException("An ASTM message starts with 'H' and its delimiters, e.g. H|\\^&.");
        var d = new AstmDelimiters(lines[0][1], lines[0][2], lines[0][3], lines[0][4]);
        var records = new List<AstmRecord>(lines.Count);
        foreach (var line in lines)
        {
            if (line.Length < 1 || !char.IsLetter(line[0])) throw new FormatException($"Not an ASTM record: '{line}'.");
            if (line[0] == 'H')
            {
                // H|\^&|… : the delimiter definition counts as field 2.
                var rest = line.Length > 5 ? line[6..].Split(d.Field) : [];
                records.Add(new AstmRecord(["H", line[2..5], .. rest], d));
            }
            else
            {
                records.Add(new AstmRecord(line.Split(d.Field), d));
            }
        }

        return new AstmMessage(records);
    }

    /// <summary>Tries to parse.</summary>
    public static bool TryParse(string text, out AstmMessage? message)
    {
        try
        {
            message = Parse(text);
            return true;
        }
        catch (FormatException)
        {
            message = null;
            return false;
        }
    }

    /// <summary>Encodes the records separated by CR.</summary>
    public string Encode() => string.Concat(Records.Select(r => r.ToString() + "\r"));

    /// <inheritdoc />
    public override string ToString() => $"ASTM from {Sender}: {Records.Count} records, {Results.Count} results";
}

/// <summary>Builds ASTM messages.</summary>
public sealed class AstmMessageBuilder(AstmDelimiters? delimiters = null)
{
    private readonly AstmDelimiters _d = delimiters ?? AstmDelimiters.Default;
    private readonly List<AstmRecord> _records = [];
    private int _patients, _orders, _results, _comments;

    private AstmMessageBuilder Add(params string?[] fields)
    {
        _records.Add(new AstmRecord(fields.Select(f => f ?? ""), _d));
        return this;
    }

    /// <summary>Header: sender "name^version", processing id (P production, T training), version.</summary>
    public AstmMessageBuilder Header(string sender, DateTime? time = null, string processingId = "P") =>
        Add("H", $"{_d.Repeat}{_d.Component}{_d.Escape}", null, null, sender, null, null, null, null, null, null, processingId, "LIS2-A2", AstmTime.Format(time ?? DateTime.Now));

    /// <summary>Patient: id, "family^given", birth date, sex.</summary>
    public AstmMessageBuilder Patient(string id, string name, DateTime? birthDate = null, string? sex = null)
    {
        _orders = _results = 0;
        return Add("P", (++_patients).ToString(CultureInfo.InvariantCulture), id, null, null, name, null, birthDate?.ToString("yyyyMMdd", CultureInfo.InvariantCulture), sex);
    }

    /// <summary>Order: specimen id, tests ("^^^GLU\^^^CREA"), priority, collection time.</summary>
    public AstmMessageBuilder Order(string specimenId, IEnumerable<string> tests, string priority = "R", DateTime? collected = null)
    {
        _results = 0;
        var testField = string.Join(_d.Repeat, tests.Select(t => $"{_d.Component}{_d.Component}{_d.Component}{t}"));
        return Add("O", (++_orders).ToString(CultureInfo.InvariantCulture), specimenId, null, testField, priority, null, AstmTime.Format(collected ?? DateTime.Now));
    }

    /// <summary>Result.</summary>
    public AstmMessageBuilder Result(string test, string value, string units, string range, string flag = "N", string status = "F", DateTime? completed = null) =>
        Add("R", (++_results).ToString(CultureInfo.InvariantCulture), $"{_d.Component}{_d.Component}{_d.Component}{test}", value, units, range, flag, null, status, null, null, null, AstmTime.Format(completed ?? DateTime.Now));

    /// <summary>Comment.</summary>
    public AstmMessageBuilder Comment(string text, string source = "I") => Add("C", (++_comments).ToString(CultureInfo.InvariantCulture), source, text, "G");

    /// <summary>Terminator and build ("N" normal end).</summary>
    public AstmMessage Build(string termination = "N")
    {
        Add("L", "1", termination);
        return new AstmMessage([.. _records]);
    }
}

/// <summary>ASTM timestamps (YYYYMMDDHHMMSS).</summary>
public static class AstmTime
{
    /// <summary>Formats a time.</summary>
    public static string Format(DateTime t) => t.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

    /// <summary>Parses 8, 12 or 14 digits; null for empty or invalid.</summary>
    public static DateTime? Parse(string value) =>
        DateTime.TryParseExact(value, ["yyyyMMddHHmmss", "yyyyMMddHHmm", "yyyyMMdd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;
}

/// <summary>
/// The ASTM E1381 / LIS1-A low-level protocol, without I/O: frames <c>STX FN text ETB|ETX C1 C2 CR LF</c> (frame
/// number 1–7, 0, 1 …; checksum = sum of FN…ETB/ETX modulo 256 in two hex digits), at most 240 characters per frame.
/// </summary>
public static class AstmLink
{
    /// <summary>Enquiry: the sender asks to send.</summary>
    public const byte Enq = 0x05;
    /// <summary>Acknowledge.</summary>
    public const byte Ack = 0x06;
    /// <summary>Negative acknowledge.</summary>
    public const byte Nak = 0x15;
    /// <summary>End of transmission.</summary>
    public const byte Eot = 0x04;
    /// <summary>Start of frame.</summary>
    public const byte Stx = 0x02;
    /// <summary>End of an intermediate frame.</summary>
    public const byte Etb = 0x17;
    /// <summary>End of the last frame of a record.</summary>
    public const byte Etx = 0x03;
    /// <summary>Maximum text per frame.</summary>
    public const int MaxText = 240;

    /// <summary>Splits a message into frames: one record per frame, long records split with ETB.</summary>
    public static IReadOnlyList<byte[]> Frames(AstmMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var frames = new List<byte[]>();
        var fn = 1;
        foreach (var record in message.Records)
        {
            var text = record + "\r";
            for (var i = 0; i < text.Length; i += MaxText)
            {
                var chunk = text.Substring(i, Math.Min(MaxText, text.Length - i));
                frames.Add(Frame(fn, chunk, last: i + MaxText >= text.Length));
                fn = (fn + 1) % 8;
            }
        }

        return frames;
    }

    /// <summary>Builds one frame.</summary>
    public static byte[] Frame(int frameNumber, string text, bool last)
    {
        ArgumentNullException.ThrowIfNull(text);
        var body = new List<byte> { (byte)('0' + (frameNumber % 8)) };
        body.AddRange(Encoding.Latin1.GetBytes(text));
        body.Add(last ? Etx : Etb);
        var sum = body.Aggregate(0, (a, b) => (a + b) & 0xFF);
        return [Stx, .. body, .. Encoding.ASCII.GetBytes(sum.ToString("X2", CultureInfo.InvariantCulture)), 0x0D, 0x0A];
    }

    /// <summary>Checks a frame (from STX to LF) and returns its frame number, text and whether it ends a record.</summary>
    public static bool TryParseFrame(ReadOnlySpan<byte> frame, out int frameNumber, out string text, out bool last, out string? error)
    {
        (frameNumber, text, last, error) = (0, "", false, null);
        if (frame.Length < 7 || frame[0] != Stx || frame[^2] != 0x0D || frame[^1] != 0x0A)
        {
            error = "Not a complete ASTM frame (STX … CR LF).";
            return false;
        }

        var end = frame[^5];
        if (end is not (Etx or Etb) || frame[1] is < (byte)'0' or > (byte)'7')
        {
            error = "Bad frame number or terminator.";
            return false;
        }

        var sum = 0;
        foreach (var b in frame[1..^4]) sum = (sum + b) & 0xFF;
        var expected = Encoding.ASCII.GetString(frame[^4..^2]);
        if (!string.Equals(sum.ToString("X2", CultureInfo.InvariantCulture), expected, StringComparison.OrdinalIgnoreCase))
        {
            error = $"Checksum {expected}, expected {sum:X2}.";
            return false;
        }

        frameNumber = frame[1] - '0';
        text = Encoding.Latin1.GetString(frame[2..^5]);
        last = end == Etx;
        return true;
    }
}
