using System.Globalization;
using System.Text;

namespace IoTCom.Net.Protocols.Nmea;

/// <summary>A raw NMEA 0183 sentence: <c>$TTSSS,f1,f2,...*CS</c>.</summary>
public sealed class NmeaSentence
{
    private NmeaSentence(string raw, char start, string talker, string type, string[] fields, bool hasChecksum, bool checksumValid)
    {
        Raw = raw;
        StartChar = start;
        Talker = talker;
        Type = type;
        Fields = fields;
        HasChecksum = hasChecksum;
        ChecksumValid = checksumValid;
    }

    /// <summary>Original text (without CR/LF).</summary>
    public string Raw { get; }
    /// <summary><c>$</c> for parametric sentences, <c>!</c> for encapsulated (AIS).</summary>
    public char StartChar { get; }
    /// <summary>Talker id, e.g. <c>GP</c>, <c>GN</c>, <c>GL</c>, <c>AI</c>. Empty for proprietary sentences.</summary>
    public string Talker { get; }
    /// <summary>Sentence type, e.g. <c>GGA</c>. Proprietary sentences keep the full address (<c>PUBX</c>).</summary>
    public string Type { get; }
    /// <summary>Data fields after the address.</summary>
    public IReadOnlyList<string> Fields { get; }
    /// <summary>True when a <c>*hh</c> checksum was present.</summary>
    public bool HasChecksum { get; }
    /// <summary>True when the checksum matches (or none was present).</summary>
    public bool ChecksumValid { get; }

    /// <summary>Field <paramref name="index"/> or empty string.</summary>
    public string this[int index] => index < Fields.Count ? Fields[index] : string.Empty;

    /// <summary>Parses a sentence. Returns false for malformed input or a wrong checksum (when <paramref name="requireValidChecksum"/>).</summary>
    public static bool TryParse(string line, out NmeaSentence? sentence, bool requireValidChecksum = true)
    {
        sentence = null;
        if (string.IsNullOrEmpty(line)) return false;
        line = line.TrimEnd('\r', '\n');
        if (line.Length < 6 || (line[0] != '$' && line[0] != '!')) return false;

        var star = line.LastIndexOf('*');
        var hasChecksum = star > 0 && star == line.Length - 3;
        var body = hasChecksum ? line[1..star] : line[1..];
        var valid = true;
        if (hasChecksum)
        {
            if (!byte.TryParse(line.AsSpan(star + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var expected)) return false;
            valid = Checksum(body) == expected;
            if (!valid && requireValidChecksum) return false;
        }

        var parts = body.Split(',');
        var address = parts[0];
        if (address.Length < 3) return false;
        string talker, type;
        if (address[0] == 'P') { talker = string.Empty; type = address; }
        else if (address.Length == 5) { talker = address[..2]; type = address[2..]; }
        else { talker = address[..^3]; type = address[^3..]; }

        sentence = new NmeaSentence(line, line[0], talker, type, parts[1..], hasChecksum, valid);
        return true;
    }

    /// <summary>Parses or throws <see cref="ProtocolException"/>.</summary>
    public static NmeaSentence Parse(string line) =>
        TryParse(line, out var s) ? s! : throw new ProtocolException($"Invalid NMEA sentence: {line}");

    /// <summary>XOR checksum of the characters between <c>$</c> and <c>*</c>.</summary>
    public static byte Checksum(ReadOnlySpan<char> body)
    {
        byte cs = 0;
        foreach (var c in body) cs ^= (byte)c;
        return cs;
    }

    /// <summary>Builds a sentence with checksum, e.g. <c>Build("GP", "GGA", ...)</c>.</summary>
    public static string Build(string talker, string type, params string[] fields)
    {
        var sb = new StringBuilder(82);
        sb.Append(talker).Append(type);
        foreach (var f in fields) sb.Append(',').Append(f);
        var body = sb.ToString();
        return string.Create(CultureInfo.InvariantCulture, $"${body}*{Checksum(body):X2}");
    }

    /// <inheritdoc />
    public override string ToString() => Raw;
}
