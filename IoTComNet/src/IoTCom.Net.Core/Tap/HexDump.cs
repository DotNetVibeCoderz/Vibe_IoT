using System.Text;

namespace IoTCom.Net;

/// <summary>Hex formatting helpers used by the CLI, Gallery and logs.</summary>
public static class HexDump
{
    /// <summary>Formats bytes as <c>"01 03 00 00 00 0A"</c>.</summary>
    public static string ToHex(ReadOnlySpan<byte> data, char separator = ' ')
    {
        if (data.IsEmpty) return string.Empty;
        var sb = new StringBuilder(data.Length * 3);
        for (var i = 0; i < data.Length; i++)
        {
            if (i > 0 && separator != '\0') sb.Append(separator);
            sb.Append(data[i].ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>Parses hex text. Accepts spaces, dashes, colons, commas and <c>0x</c> prefixes.</summary>
    /// <exception cref="FormatException">Odd number of digits or invalid characters.</exception>
    public static byte[] Parse(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        var clean = new StringBuilder(hex.Length);
        for (var i = 0; i < hex.Length; i++)
        {
            var c = hex[i];
            if (c == '0' && i + 1 < hex.Length && (hex[i + 1] == 'x' || hex[i + 1] == 'X')) { i++; continue; }
            if (char.IsWhiteSpace(c) || c is '-' or ':' or ',') continue;
            clean.Append(c);
        }
        return Convert.FromHexString(clean.ToString());
    }

    /// <summary>Classic 16-bytes-per-line dump with offsets and ASCII column.</summary>
    public static string Format(ReadOnlySpan<byte> data, int bytesPerLine = 16)
    {
        var sb = new StringBuilder();
        for (var offset = 0; offset < data.Length; offset += bytesPerLine)
        {
            var line = data.Slice(offset, Math.Min(bytesPerLine, data.Length - offset));
            sb.Append(offset.ToString("X4", System.Globalization.CultureInfo.InvariantCulture)).Append("  ");
            for (var i = 0; i < bytesPerLine; i++)
            {
                if (i < line.Length) sb.Append(line[i].ToString("X2", System.Globalization.CultureInfo.InvariantCulture)).Append(' ');
                else sb.Append("   ");
                if (i == 7) sb.Append(' ');
            }
            sb.Append(' ');
            foreach (var b in line) sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
