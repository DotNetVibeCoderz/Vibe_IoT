using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace IoTCom.Net.Protocols.Knx;

/// <summary>
/// The value carried by a group telegram: either up to 6 bits packed into the APCI byte (<see cref="IsShort"/>, used by
/// DPT 1, 2 and 3) or the bytes that follow it.
/// </summary>
public readonly record struct KnxGroupValue(byte[] Bytes, bool IsShort)
{
    /// <summary>A short (6-bit) value.</summary>
    public static KnxGroupValue Short(int bits) => new([(byte)(bits & 0x3F)], true);

    /// <summary>A value of one or more bytes after the APCI.</summary>
    public static KnxGroupValue Of(params byte[] bytes) => new(bytes, false);

    /// <summary>An empty value (GroupValueRead).</summary>
    public static KnxGroupValue Empty { get; } = new([], false);

    /// <inheritdoc />
    public override string ToString() => IsShort ? $"0x{Bytes[0]:X2} (6-bit)" : Convert.ToHexString(Bytes);

    /// <inheritdoc />
    public bool Equals(KnxGroupValue other) => IsShort == other.IsShort && Bytes.AsSpan().SequenceEqual(other.Bytes);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(IsShort, Bytes.Length, Bytes.Length > 0 ? Bytes[0] : 0);
}

/// <summary>A KNX datapoint type: id, name and unit.</summary>
/// <param name="Id">Id such as <c>9.001</c>.</param>
/// <param name="Name">Name such as <c>Temperature</c>.</param>
/// <param name="Unit">Unit such as <c>°C</c> (empty when none).</param>
public sealed record KnxDatapointType(string Id, string Name, string Unit)
{
    /// <summary>Main number (the part before the dot).</summary>
    public int Main => int.Parse(Id.Split('.')[0], CultureInfo.InvariantCulture);
}

/// <summary>
/// Encoding and decoding of KNX datapoint types (KNX Standard 3/7/2): 1.x booleans, 2.x controlled booleans, 3.007/3.008
/// dimming and blind steps, 5.x 8-bit unsigned (5.001 percent, 5.003 angle), 6.x 8-bit signed, 7.x/8.x 16-bit, 9.x
/// 2-byte KNX float, 10.001 time of day, 11.001 date, 12.x/13.x 32-bit, 14.x IEEE float, 16.x strings, 17.001 and
/// 18.001 scenes, 20.102 HVAC mode, 232.600 RGB.
/// </summary>
public static class KnxDpt
{
    private static readonly Dictionary<string, KnxDatapointType> Known = new KnxDatapointType[]
    {
        new("1.001", "Switch", ""), new("1.002", "Boolean", ""), new("1.003", "Enable", ""), new("1.008", "Up/Down", ""), new("1.009", "Open/Close", ""),
        new("1.010", "Start/Stop", ""), new("1.018", "Occupancy", ""), new("1.019", "Window/Door", ""), new("2.001", "Switch control", ""),
        new("3.007", "Dimming control", ""), new("3.008", "Blind control", ""),
        new("5.001", "Percent", "%"), new("5.003", "Angle", "°"), new("5.004", "Percent (0–255)", "%"), new("5.010", "Counter pulses", ""),
        new("6.001", "Percent (signed)", "%"), new("6.010", "Counter pulses (signed)", ""),
        new("7.001", "Pulses", ""), new("7.013", "Brightness", "lux"), new("8.001", "Pulses (signed)", ""),
        new("9.001", "Temperature", "°C"), new("9.002", "Temperature difference", "K"), new("9.004", "Illuminance", "lux"), new("9.005", "Wind speed", "m/s"),
        new("9.006", "Pressure", "Pa"), new("9.007", "Humidity", "%"), new("9.008", "Air quality", "ppm"), new("9.020", "Voltage", "mV"), new("9.021", "Current", "mA"),
        new("10.001", "Time of day", ""), new("11.001", "Date", ""), new("12.001", "Counter", ""), new("13.001", "Counter (signed)", ""),
        new("13.010", "Active energy", "Wh"), new("13.013", "Active energy", "kWh"),
        new("14.019", "Electric current", "A"), new("14.027", "Electric potential", "V"), new("14.056", "Power", "W"), new("14.068", "Temperature", "°C"),
        new("16.000", "String (ASCII)", ""), new("16.001", "String (ISO 8859-1)", ""), new("17.001", "Scene number", ""), new("18.001", "Scene control", ""),
        new("20.102", "HVAC mode", ""), new("232.600", "RGB", ""),
    }.ToDictionary(t => t.Id, StringComparer.Ordinal);

    /// <summary>Datapoint types with a name and unit.</summary>
    public static IReadOnlyCollection<KnxDatapointType> Types => Known.Values;

    /// <summary>Looks a type up (null when unknown; the main number still decides the encoding).</summary>
    public static KnxDatapointType? Find(string id) => Known.TryGetValue(id, out var t) ? t : null;

    private static int MainOf(string dpt) =>
        int.TryParse(dpt.Split('.')[0], NumberStyles.None, CultureInfo.InvariantCulture, out var m) ? m : throw new FormatException($"'{dpt}' is not a datapoint type id (main.sub).");

    /// <summary>Encodes a value: bool, numbers, <see cref="TimeOnly"/>, <see cref="DateOnly"/>, strings, (bool control, int step) for 3.x, (byte r, byte g, byte b) for 232.600.</summary>
    public static KnxGroupValue Encode(string dpt, object value)
    {
        double D() => Convert.ToDouble(value, CultureInfo.InvariantCulture);
        long L() => Convert.ToInt64(value, CultureInfo.InvariantCulture);
        switch (MainOf(dpt))
        {
            case 1:
                return KnxGroupValue.Short(Convert.ToBoolean(value, CultureInfo.InvariantCulture) ? 1 : 0);
            case 2:
                var (c2, v2) = ((bool, bool))value;
                return KnxGroupValue.Short((c2 ? 2 : 0) | (v2 ? 1 : 0));
            case 3:
                var (c3, step) = ((bool, int))value;
                if (step is < 0 or > 7) throw new ArgumentOutOfRangeException(nameof(value), "the step code is 0–7");
                return KnxGroupValue.Short((c3 ? 8 : 0) | step);
            case 5:
                var raw = dpt switch
                {
                    "5.001" => Math.Round(D() * 255 / 100, MidpointRounding.AwayFromZero),
                    "5.003" => Math.Round(D() * 255 / 360, MidpointRounding.AwayFromZero),
                    _ => D(),
                };
                return KnxGroupValue.Of(checked((byte)Clamp(raw, 0, 255, dpt)));
            case 6:
                return KnxGroupValue.Of(unchecked((byte)checked((sbyte)Clamp(L(), -128, 127, dpt))));
            case 7:
                var u16 = new byte[2];
                BinaryPrimitives.WriteUInt16BigEndian(u16, (ushort)Clamp(L(), 0, 65535, dpt));
                return KnxGroupValue.Of(u16);
            case 8:
                var i16 = new byte[2];
                BinaryPrimitives.WriteInt16BigEndian(i16, (short)Clamp(L(), -32768, 32767, dpt));
                return KnxGroupValue.Of(i16);
            case 9:
                return KnxGroupValue.Of(EncodeFloat16(D()));
            case 10:
                var t = (TimeOnly)value;
                return KnxGroupValue.Of((byte)t.Hour, (byte)t.Minute, (byte)t.Second);   // day of week 0 = no day
            case 11:
                var d = (DateOnly)value;
                if (d.Year is < 1990 or > 2089) throw new ArgumentOutOfRangeException(nameof(value), "DPT 11.001 covers 1990–2089");
                return KnxGroupValue.Of((byte)d.Day, (byte)d.Month, (byte)(d.Year % 100));
            case 12:
                var u32 = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(u32, (uint)Clamp(L(), 0, uint.MaxValue, dpt));
                return KnxGroupValue.Of(u32);
            case 13:
                var i32 = new byte[4];
                BinaryPrimitives.WriteInt32BigEndian(i32, (int)Clamp(L(), int.MinValue, int.MaxValue, dpt));
                return KnxGroupValue.Of(i32);
            case 14:
                var f = new byte[4];
                BinaryPrimitives.WriteSingleBigEndian(f, (float)D());
                return KnxGroupValue.Of(f);
            case 16:
                var s = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
                if (s.Length > 14) throw new ArgumentOutOfRangeException(nameof(value), "DPT 16 strings are at most 14 characters");
                var bytes = new byte[14];
                Encoding.Latin1.GetBytes(s, bytes);
                return KnxGroupValue.Of(bytes);
            case 17:
                return KnxGroupValue.Of((byte)Clamp(L(), 0, 63, dpt));
            case 18:
                var (learn, scene) = ((bool, int))value;
                return KnxGroupValue.Of((byte)((learn ? 0x80 : 0) | (int)Clamp(scene, 0, 63, dpt)));
            case 20:
                return KnxGroupValue.Of((byte)Clamp(L(), 0, 255, dpt));
            case 232:
                var (r, g, b) = ((byte, byte, byte))value;
                return KnxGroupValue.Of(r, g, b);
            default:
                throw new NotSupportedException($"DPT {dpt} is not supported.");
        }
    }

    private static double Clamp(double v, double min, double max, string dpt) =>
        v < min || v > max ? throw new ArgumentOutOfRangeException(nameof(v), $"{v} is outside DPT {dpt} ({min}–{max})") : v;

    /// <summary>Decodes a value (types as accepted by <see cref="Encode"/>).</summary>
    public static object Decode(string dpt, KnxGroupValue value)
    {
        var b = value.Bytes;
        void Need(int n)
        {
            if (b.Length != n || value.IsShort != (n == 0)) throw new ProtocolException($"DPT {dpt} needs {(n == 0 ? "a 6-bit value" : $"{n} byte(s)")}, got {value}.");
        }

        byte Bits()
        {
            if (!value.IsShort && b.Length != 1) throw new ProtocolException($"DPT {dpt} needs a 6-bit value, got {value}.");
            return b[0];
        }

        switch (MainOf(dpt))
        {
            case 1:
                return (Bits() & 1) != 0;
            case 2:
                var v2 = Bits();
                return ((v2 & 2) != 0, (v2 & 1) != 0);
            case 3:
                var v3 = Bits();
                return ((v3 & 8) != 0, v3 & 7);
            case 5:
                Need(1);
                return dpt switch
                {
                    "5.001" => Math.Round(b[0] * 100.0 / 255, 1),
                    "5.003" => Math.Round(b[0] * 360.0 / 255, 1),
                    _ => (double)b[0],
                };
            case 6:
                Need(1);
                return (double)(sbyte)b[0];
            case 7:
                Need(2);
                return (double)BinaryPrimitives.ReadUInt16BigEndian(b);
            case 8:
                Need(2);
                return (double)BinaryPrimitives.ReadInt16BigEndian(b);
            case 9:
                Need(2);
                return DecodeFloat16(b);
            case 10:
                Need(3);
                return new TimeOnly(b[0] & 0x1F, b[1] & 0x3F, b[2] & 0x3F);
            case 11:
                Need(3);
                var yy = b[2] & 0x7F;
                return new DateOnly(yy >= 90 ? 1900 + yy : 2000 + yy, b[1] & 0x0F, b[0] & 0x1F);
            case 12:
                Need(4);
                return (double)BinaryPrimitives.ReadUInt32BigEndian(b);
            case 13:
                Need(4);
                return (double)BinaryPrimitives.ReadInt32BigEndian(b);
            case 14:
                Need(4);
                return (double)BinaryPrimitives.ReadSingleBigEndian(b);
            case 16:
                Need(14);
                return Encoding.Latin1.GetString(b).TrimEnd('\0');
            case 17:
                Need(1);
                return (double)(b[0] & 0x3F);
            case 18:
                Need(1);
                return ((b[0] & 0x80) != 0, b[0] & 0x3F);
            case 20:
                Need(1);
                return (double)b[0];
            case 232:
                Need(3);
                return (b[0], b[1], b[2]);
            default:
                throw new NotSupportedException($"DPT {dpt} is not supported.");
        }
    }

    /// <summary>Formats a value with its unit, e.g. <c>21.5 °C</c> or <c>On</c>.</summary>
    public static string Format(string dpt, KnxGroupValue value)
    {
        object v;
        try
        {
            v = Decode(dpt, value);
        }
        catch (Exception ex) when (ex is ProtocolException or NotSupportedException or ArgumentException)
        {
            return value.ToString();
        }

        var unit = Find(dpt)?.Unit is { Length: > 0 } u ? " " + u : "";
        return v switch
        {
            bool on => dpt switch
            {
                "1.001" => on ? "On" : "Off",
                "1.008" => on ? "Down" : "Up",
                "1.009" => on ? "Close" : "Open",
                "1.010" => on ? "Start" : "Stop",
                "1.018" => on ? "Occupied" : "Not occupied",
                "1.019" => on ? "Open" : "Closed",
                _ => on ? "True" : "False",
            },
            double d when dpt == "20.102" => d switch { 0 => "Auto", 1 => "Comfort", 2 => "Standby", 3 => "Economy", 4 => "Building protection", _ => d.ToString(CultureInfo.InvariantCulture) },
            double d => d.ToString("0.##", CultureInfo.InvariantCulture) + unit,
            ValueTuple<bool, int> t when MainOf(dpt) == 3 => t.Item2 == 0 ? "Stop" : $"{(t.Item1 ? (dpt == "3.008" ? "Down" : "Increase") : (dpt == "3.008" ? "Up" : "Decrease"))} {100 / (1 << (t.Item2 - 1))} %",
            TimeOnly t => t.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ValueTuple<byte, byte, byte> rgb => $"#{rgb.Item1:X2}{rgb.Item2:X2}{rgb.Item3:X2}",
            _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
        };
    }

    /// <summary>KNX 2-byte float (DPT 9): value = 0.01 × M × 2^E, M 12-bit two's complement, E 0–15; 0x7FFF is reserved for invalid data.</summary>
    public static byte[] EncodeFloat16(double value)
    {
        if (double.IsNaN(value) || value < -671088.64 || value > 670433.28) throw new ArgumentOutOfRangeException(nameof(value), "DPT 9 covers -671088.64 to 670433.28 (0x7FFF is reserved for invalid data)");
        // The smallest exponent whose rounded mantissa fits 12 bits keeps the most precision.
        long m = 0;
        var e = 0;
        for (; e <= 15; e++)
        {
            m = (long)Math.Round(value * 100 / (1 << e), MidpointRounding.AwayFromZero);
            if (m is >= -2048 and <= 2047) break;
        }

        if (e > 15) throw new ArgumentOutOfRangeException(nameof(value), "DPT 9 overflow");
        var raw = (ushort)((m < 0 ? 0x8000 : 0) | (e << 11) | (int)(m & 0x7FF));
        return [(byte)(raw >> 8), (byte)raw];
    }

    /// <summary>Decodes a KNX 2-byte float; 0x7FFF (invalid data) decodes to NaN.</summary>
    public static double DecodeFloat16(ReadOnlySpan<byte> b)
    {
        var raw = (b[0] << 8) | b[1];
        if (raw == 0x7FFF) return double.NaN;
        var e = (raw >> 11) & 0x0F;
        var m = raw & 0x7FF;
        if ((raw & 0x8000) != 0) m -= 2048;
        return Math.Round(0.01 * m * (1 << e), 2);
    }
}
