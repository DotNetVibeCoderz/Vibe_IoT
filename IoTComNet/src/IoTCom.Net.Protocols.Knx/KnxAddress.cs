using System.Globalization;

namespace IoTCom.Net.Protocols.Knx;

/// <summary>A KNX individual (physical) address, area.line.device (4.4.8 bits), e.g. <c>1.1.5</c>.</summary>
public readonly record struct KnxIndividualAddress(ushort Raw) : ISpanFormattable
{
    /// <summary>Creates an address from its parts.</summary>
    public KnxIndividualAddress(int area, int line, int device)
        : this(area is < 0 or > 15 || line is < 0 or > 15 || device is < 0 or > 255
            ? throw new ArgumentOutOfRangeException(nameof(area), "area and line are 0–15, device 0–255")
            : (ushort)((area << 12) | (line << 8) | device))
    {
    }

    /// <summary>Area (0–15).</summary>
    public int Area => Raw >> 12;

    /// <summary>Line (0–15).</summary>
    public int Line => (Raw >> 8) & 0x0F;

    /// <summary>Device (0–255).</summary>
    public int Device => Raw & 0xFF;

    /// <summary>Parses <c>a.l.d</c>.</summary>
    public static KnxIndividualAddress Parse(string text)
    {
        var p = text.Trim().Split('.');
        if (p.Length != 3 || !int.TryParse(p[0], NumberStyles.None, CultureInfo.InvariantCulture, out var a)
            || !int.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out var l) || !int.TryParse(p[2], NumberStyles.None, CultureInfo.InvariantCulture, out var d)
            || a > 15 || l > 15 || d > 255)
            throw new FormatException($"'{text}' is not a KNX individual address (area.line.device, 0–15.0–15.0–255).");
        return new KnxIndividualAddress(a, l, d);
    }

    /// <inheritdoc />
    public override string ToString() => $"{Area}.{Line}.{Device}";

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) => ToString();

    /// <inheritdoc />
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) =>
        destination.TryWrite(CultureInfo.InvariantCulture, $"{Area}.{Line}.{Device}", out charsWritten);
}

/// <summary>A KNX group address, shown in the three-level style main/middle/sub (5.3.8 bits), e.g. <c>1/2/3</c>.</summary>
public readonly record struct KnxGroupAddress(ushort Raw) : ISpanFormattable
{
    /// <summary>Creates a three-level address.</summary>
    public KnxGroupAddress(int main, int middle, int sub)
        : this(main is < 0 or > 31 || middle is < 0 or > 7 || sub is < 0 or > 255
            ? throw new ArgumentOutOfRangeException(nameof(main), "main is 0–31, middle 0–7, sub 0–255")
            : (ushort)((main << 11) | (middle << 8) | sub))
    {
    }

    /// <summary>Main group (0–31).</summary>
    public int Main => Raw >> 11;

    /// <summary>Middle group (0–7).</summary>
    public int Middle => (Raw >> 8) & 0x07;

    /// <summary>Sub group (0–255).</summary>
    public int Sub => Raw & 0xFF;

    /// <summary>Parses <c>main/middle/sub</c>, two-level <c>main/sub</c> (5.11 bits) or a raw number.</summary>
    public static KnxGroupAddress Parse(string text)
    {
        var p = text.Trim().Split('/');
        int[] n;
        try
        {
            n = [.. p.Select(x => int.Parse(x, NumberStyles.None, CultureInfo.InvariantCulture))];
        }
        catch (FormatException)
        {
            throw new FormatException($"'{text}' is not a KNX group address (main/middle/sub).");
        }

        return n.Length switch
        {
            3 when n[0] <= 31 && n[1] <= 7 && n[2] <= 255 => new KnxGroupAddress(n[0], n[1], n[2]),
            2 when n[0] <= 31 && n[1] <= 2047 => new KnxGroupAddress((ushort)((n[0] << 11) | n[1])),
            1 when n[0] <= 0xFFFF => new KnxGroupAddress((ushort)n[0]),
            _ => throw new FormatException($"'{text}' is not a KNX group address (main/middle/sub, 0–31/0–7/0–255)."),
        };
    }

    /// <summary>Parses, or returns false.</summary>
    public static bool TryParse(string text, out KnxGroupAddress address)
    {
        try
        {
            address = Parse(text);
            return true;
        }
        catch (FormatException)
        {
            address = default;
            return false;
        }
    }

    /// <inheritdoc />
    public override string ToString() => $"{Main}/{Middle}/{Sub}";

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) => ToString();

    /// <inheritdoc />
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) =>
        destination.TryWrite(CultureInfo.InvariantCulture, $"{Main}/{Middle}/{Sub}", out charsWritten);
}
