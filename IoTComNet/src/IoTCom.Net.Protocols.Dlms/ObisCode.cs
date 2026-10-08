using System.Globalization;

namespace IoTCom.Net.Protocols.Dlms;

/// <summary>
/// An OBIS code (IEC 62056-6-1): the six-group logical name of a COSEM object, e.g. <c>1.0.1.8.0.255</c>
/// (active energy import, total). Accepts the dotted form, the <c>A-B:C.D.E*F</c> form and the short <c>C.D.E</c>
/// form (electricity, A = 1, B = 0, F = 255).
/// </summary>
/// <param name="A">Medium (0 abstract, 1 electricity, 6 heat, 7 gas, 8 water).</param>
/// <param name="B">Channel.</param>
/// <param name="C">Quantity.</param>
/// <param name="D">Processing (instantaneous, cumulative, ...).</param>
/// <param name="E">Tariff or classification.</param>
/// <param name="F">Historical value (255 = current).</param>
public readonly record struct ObisCode(byte A, byte B, byte C, byte D, byte E, byte F)
{
    /// <summary>Parses an OBIS code.</summary>
    public static ObisCode Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return TryParse(text, out var code) ? code : throw new FormatException($"Not an OBIS code: '{text}' (expected e.g. 1.0.1.8.0.255 or 1-0:1.8.0*255).");
    }

    /// <summary>Tries to parse an OBIS code.</summary>
    public static bool TryParse(string? text, out ObisCode code)
    {
        code = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Trim().Split(['.', '-', ':', '*', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 3) parts = ["1", "0", .. parts, "255"];
        else if (parts.Length == 5) parts = [.. parts, "255"];
        if (parts.Length != 6) return false;
        var b = new byte[6];
        for (var i = 0; i < 6; i++)
            if (!byte.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out b[i])) return false;
        code = new ObisCode(b[0], b[1], b[2], b[3], b[4], b[5]);
        return true;
    }

    /// <summary>Reads the 6-byte logical name.</summary>
    public static ObisCode FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 6) throw new FormatException($"A logical name has 6 bytes, got {bytes.Length}.");
        return new ObisCode(bytes[0], bytes[1], bytes[2], bytes[3], bytes[4], bytes[5]);
    }

    /// <summary>The 6-byte logical name.</summary>
    public byte[] ToBytes() => [A, B, C, D, E, F];

    /// <summary>The <c>A-B:C.D.E*F</c> form used on meter displays and in the Blue Book.</summary>
    public string ToDisplayString() => $"{A}-{B}:{C}.{D}.{E}*{F}";

    /// <summary>Description from the built-in catalog, or null.</summary>
    public string? Description => ObisCatalog.Describe(this);

    /// <inheritdoc />
    public override string ToString() => $"{A}.{B}.{C}.{D}.{E}.{F}";
}

/// <summary>Names of common OBIS codes (electricity metering, abstract objects).</summary>
public static class ObisCatalog
{
    private static readonly Dictionary<ObisCode, string> Names = new()
    {
        [ObisCode.Parse("0.0.1.0.0.255")] = "Clock",
        [ObisCode.Parse("0.0.40.0.0.255")] = "Current association",
        [ObisCode.Parse("0.0.42.0.0.255")] = "COSEM logical device name",
        [ObisCode.Parse("0.0.96.1.0.255")] = "Meter serial number",
        [ObisCode.Parse("0.0.96.1.1.255")] = "Manufacturer",
        [ObisCode.Parse("1.0.0.2.0.255")] = "Firmware version",
        [ObisCode.Parse("0.0.96.3.10.255")] = "Disconnect control",
        [ObisCode.Parse("0.0.96.14.0.255")] = "Current tariff",
        [ObisCode.Parse("0.0.96.7.21.255")] = "Number of power failures",
        [ObisCode.Parse("1.0.1.8.0.255")] = "Active energy import (+A), total",
        [ObisCode.Parse("1.0.1.8.1.255")] = "Active energy import (+A), tariff 1",
        [ObisCode.Parse("1.0.1.8.2.255")] = "Active energy import (+A), tariff 2",
        [ObisCode.Parse("1.0.2.8.0.255")] = "Active energy export (−A), total",
        [ObisCode.Parse("1.0.3.8.0.255")] = "Reactive energy import (+R), total",
        [ObisCode.Parse("1.0.4.8.0.255")] = "Reactive energy export (−R), total",
        [ObisCode.Parse("1.0.1.7.0.255")] = "Active power import (+P)",
        [ObisCode.Parse("1.0.2.7.0.255")] = "Active power export (−P)",
        [ObisCode.Parse("1.0.1.6.0.255")] = "Maximum demand, active import",
        [ObisCode.Parse("1.0.32.7.0.255")] = "Voltage L1",
        [ObisCode.Parse("1.0.52.7.0.255")] = "Voltage L2",
        [ObisCode.Parse("1.0.72.7.0.255")] = "Voltage L3",
        [ObisCode.Parse("1.0.31.7.0.255")] = "Current L1",
        [ObisCode.Parse("1.0.51.7.0.255")] = "Current L2",
        [ObisCode.Parse("1.0.71.7.0.255")] = "Current L3",
        [ObisCode.Parse("1.0.21.7.0.255")] = "Active power L1",
        [ObisCode.Parse("1.0.41.7.0.255")] = "Active power L2",
        [ObisCode.Parse("1.0.61.7.0.255")] = "Active power L3",
        [ObisCode.Parse("1.0.13.7.0.255")] = "Power factor",
        [ObisCode.Parse("1.0.14.7.0.255")] = "Supply frequency",
        [ObisCode.Parse("1.0.99.1.0.255")] = "Load profile 1 (15 min)",
        [ObisCode.Parse("1.0.99.2.0.255")] = "Load profile 2 (daily)",
        [ObisCode.Parse("0.0.99.98.0.255")] = "Standard event log",
    };

    /// <summary>All catalogued codes.</summary>
    public static IReadOnlyDictionary<ObisCode, string> All => Names;

    /// <summary>The name of <paramref name="code"/>, or null.</summary>
    public static string? Describe(ObisCode code) => Names.GetValueOrDefault(code);
}

/// <summary>COSEM interface classes used by this library (IEC 62056-6-2, the Blue Book).</summary>
public static class CosemClass
{
    /// <summary>Data (value).</summary>
    public const ushort Data = 1;
    /// <summary>Register (value, scaler_unit).</summary>
    public const ushort Register = 3;
    /// <summary>Extended register.</summary>
    public const ushort ExtendedRegister = 4;
    /// <summary>Demand register.</summary>
    public const ushort DemandRegister = 5;
    /// <summary>Profile generic (buffer, capture objects).</summary>
    public const ushort ProfileGeneric = 7;
    /// <summary>Clock.</summary>
    public const ushort Clock = 8;
    /// <summary>Association LN (object list, authentication).</summary>
    public const ushort AssociationLn = 15;
    /// <summary>Disconnect control.</summary>
    public const ushort DisconnectControl = 70;

    /// <summary>Class name.</summary>
    public static string Name(ushort classId) => classId switch
    {
        Data => "Data",
        Register => "Register",
        ExtendedRegister => "Extended register",
        DemandRegister => "Demand register",
        ProfileGeneric => "Profile generic",
        Clock => "Clock",
        AssociationLn => "Association LN",
        DisconnectControl => "Disconnect control",
        _ => $"class {classId}",
    };
}

/// <summary>Physical units (DLMS unit enumeration, IEC 62056-6-2).</summary>
public static class CosemUnit
{
    private static readonly Dictionary<byte, string> Symbols = new()
    {
        [1] = "a", [2] = "mo", [3] = "wk", [4] = "d", [5] = "h", [6] = "min", [7] = "s", [8] = "°", [9] = "°C",
        [10] = "currency", [11] = "m", [12] = "m/s", [13] = "m³", [14] = "m³", [15] = "m³/h", [16] = "m³/h", [17] = "m³/d",
        [18] = "m³/d", [19] = "l", [20] = "kg", [21] = "N", [22] = "Nm", [23] = "Pa", [24] = "bar", [25] = "J", [26] = "J/h",
        [27] = "W", [28] = "VA", [29] = "var", [30] = "Wh", [31] = "VAh", [32] = "varh", [33] = "A", [34] = "C", [35] = "V",
        [36] = "V/m", [37] = "F", [38] = "Ω", [39] = "Ωm²/m", [40] = "Wb", [41] = "T", [42] = "A/m", [43] = "H", [44] = "Hz",
        [45] = "1/(Wh)", [46] = "1/(varh)", [47] = "1/(VAh)", [48] = "V²h", [49] = "A²h", [50] = "kg/s", [51] = "S", [52] = "K",
        [56] = "%", [57] = "Ah", [60] = "Wh/m³", [61] = "J/m³", [70] = "dBm", [71] = "dBµV", [72] = "dB", [254] = "", [255] = "",
    };

    /// <summary>Watt.</summary>
    public const byte Watt = 27;
    /// <summary>Volt-ampere.</summary>
    public const byte VoltAmpere = 28;
    /// <summary>Var.</summary>
    public const byte Var = 29;
    /// <summary>Watt-hour.</summary>
    public const byte WattHour = 30;
    /// <summary>Var-hour.</summary>
    public const byte VarHour = 32;
    /// <summary>Ampere.</summary>
    public const byte Ampere = 33;
    /// <summary>Volt.</summary>
    public const byte Volt = 35;
    /// <summary>Hertz.</summary>
    public const byte Hertz = 44;
    /// <summary>No unit (count).</summary>
    public const byte Count = 255;

    /// <summary>The symbol of a unit code.</summary>
    public static string Symbol(byte unit) => Symbols.TryGetValue(unit, out var s) ? s : $"unit {unit}";
}
