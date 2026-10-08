using System.Globalization;

namespace IoTCom.Net.Protocols.LoRaWan;

/// <summary>One Cayenne LPP value.</summary>
/// <param name="Channel">Data channel.</param>
/// <param name="Type">LPP type (IPSO object id − 3200).</param>
/// <param name="Values">The value(s): one number, or x/y/z, or latitude/longitude/altitude.</param>
public sealed record CayenneLppValue(byte Channel, byte Type, double[] Values)
{
    /// <summary>Type name (<c>temperature</c>, <c>humidity</c>, ...).</summary>
    public string Name => CayenneLpp.TypeName(Type);

    /// <summary>Unit.</summary>
    public string Unit => CayenneLpp.Unit(Type);

    /// <summary>The first value.</summary>
    public double Value => Values[0];

    /// <inheritdoc />
    public override string ToString() =>
        $"ch{Channel} {Name} {string.Join("/", Values.Select(v => v.ToString("0.####", CultureInfo.InvariantCulture)))}{(Unit.Length > 0 ? " " + Unit : "")}";
}

/// <summary>
/// Cayenne Low Power Payload: the compact sensor format most LoRaWAN devices and consoles understand
/// (channel, type, big-endian value).
/// </summary>
public sealed class CayenneLpp
{
    private readonly List<byte> _buffer = [];

    /// <summary>Digital input (1 byte).</summary>
    public const byte DigitalInput = 0;
    /// <summary>Digital output (1 byte).</summary>
    public const byte DigitalOutput = 1;
    /// <summary>Analog input (2 bytes, 0.01 signed).</summary>
    public const byte AnalogInput = 2;
    /// <summary>Analog output (2 bytes, 0.01 signed).</summary>
    public const byte AnalogOutput = 3;
    /// <summary>Illuminance (2 bytes, 1 lux unsigned).</summary>
    public const byte Luminosity = 101;
    /// <summary>Presence (1 byte).</summary>
    public const byte Presence = 102;
    /// <summary>Temperature (2 bytes, 0.1 °C signed).</summary>
    public const byte Temperature = 103;
    /// <summary>Relative humidity (1 byte, 0.5 % unsigned).</summary>
    public const byte RelativeHumidity = 104;
    /// <summary>Accelerometer (3 × 2 bytes, 0.001 G signed).</summary>
    public const byte Accelerometer = 113;
    /// <summary>Barometer (2 bytes, 0.1 hPa unsigned).</summary>
    public const byte Barometer = 115;
    /// <summary>Gyrometer (3 × 2 bytes, 0.01 °/s signed).</summary>
    public const byte Gyrometer = 134;
    /// <summary>GPS (3 × 3 bytes: 0.0001° latitude, longitude; 0.01 m altitude).</summary>
    public const byte Gps = 136;

    /// <summary>Adds a temperature in °C.</summary>
    public CayenneLpp AddTemperature(byte channel, double celsius) => Add(channel, Temperature, 2, celsius * 10);

    /// <summary>Adds relative humidity in %.</summary>
    public CayenneLpp AddHumidity(byte channel, double percent) => Add(channel, RelativeHumidity, 1, percent * 2);

    /// <summary>Adds an analog value (e.g. battery volts).</summary>
    public CayenneLpp AddAnalogInput(byte channel, double value) => Add(channel, AnalogInput, 2, value * 100);

    /// <summary>Adds a digital input.</summary>
    public CayenneLpp AddDigitalInput(byte channel, bool value) => Add(channel, DigitalInput, 1, value ? 1 : 0);

    /// <summary>Adds illuminance in lux.</summary>
    public CayenneLpp AddLuminosity(byte channel, double lux) => Add(channel, Luminosity, 2, lux);

    /// <summary>Adds barometric pressure in hPa.</summary>
    public CayenneLpp AddBarometer(byte channel, double hPa) => Add(channel, Barometer, 2, hPa * 10);

    /// <summary>Adds a GPS position.</summary>
    public CayenneLpp AddGps(byte channel, double latitude, double longitude, double altitudeMetres)
    {
        _buffer.Add(channel);
        _buffer.Add(Gps);
        Write(3, latitude * 10000);
        Write(3, longitude * 10000);
        Write(3, altitudeMetres * 100);
        return this;
    }

    /// <summary>The encoded payload.</summary>
    public byte[] ToArray() => [.. _buffer];

    private CayenneLpp Add(byte channel, byte type, int size, double scaled)
    {
        _buffer.Add(channel);
        _buffer.Add(type);
        Write(size, scaled);
        return this;
    }

    private void Write(int size, double scaled)
    {
        var v = (long)Math.Round(scaled);
        for (var i = size - 1; i >= 0; i--) _buffer.Add((byte)(v >> (8 * i)));
    }

    /// <summary>Decodes a payload; stops at the first unknown type.</summary>
    public static IReadOnlyList<CayenneLppValue> Decode(ReadOnlySpan<byte> data)
    {
        var values = new List<CayenneLppValue>();
        var i = 0;
        while (i + 2 <= data.Length)
        {
            var channel = data[i];
            var type = data[i + 1];
            var (count, size, scale, signed) = Layout(type);
            if (count == 0 || i + 2 + (count * size) > data.Length) break;
            var v = new double[count];
            for (var k = 0; k < count; k++) v[k] = Read(data.Slice(i + 2 + (k * size), size), signed) / (type == Gps && k == 2 ? 100 : scale);
            values.Add(new CayenneLppValue(channel, type, v));
            i += 2 + (count * size);
        }

        return values;
    }

    /// <summary>True when the whole payload decodes as Cayenne LPP.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> data, out IReadOnlyList<CayenneLppValue> values)
    {
        values = Decode(data);
        var consumed = values.Sum(v => 2 + (Layout(v.Type).Count * Layout(v.Type).Size));
        return values.Count > 0 && consumed == data.Length;
    }

    private static (int Count, int Size, double Scale, bool Signed) Layout(byte type) => type switch
    {
        DigitalInput or DigitalOutput or Presence => (1, 1, 1, false),
        AnalogInput or AnalogOutput => (1, 2, 100, true),
        Luminosity => (1, 2, 1, false),
        Temperature => (1, 2, 10, true),
        RelativeHumidity => (1, 1, 2, false),
        Accelerometer => (3, 2, 1000, true),
        Barometer => (1, 2, 10, false),
        Gyrometer => (3, 2, 100, true),
        Gps => (3, 3, 10000, true),
        _ => (0, 0, 1, false),
    };

    private static double Read(ReadOnlySpan<byte> bytes, bool signed)
    {
        long v = 0;
        foreach (var b in bytes) v = (v << 8) | b;
        if (signed && (bytes[0] & 0x80) != 0) v -= 1L << (8 * bytes.Length);
        return v;
    }

    internal static string TypeName(byte type) => type switch
    {
        DigitalInput => "digital in",
        DigitalOutput => "digital out",
        AnalogInput => "analog in",
        AnalogOutput => "analog out",
        Luminosity => "illuminance",
        Presence => "presence",
        Temperature => "temperature",
        RelativeHumidity => "humidity",
        Accelerometer => "accelerometer",
        Barometer => "barometer",
        Gyrometer => "gyrometer",
        Gps => "gps",
        _ => $"type {type}",
    };

    internal static string Unit(byte type) => type switch
    {
        Luminosity => "lx",
        Temperature => "°C",
        RelativeHumidity => "%",
        Accelerometer => "G",
        Barometer => "hPa",
        Gyrometer => "°/s",
        _ => "",
    };
}
