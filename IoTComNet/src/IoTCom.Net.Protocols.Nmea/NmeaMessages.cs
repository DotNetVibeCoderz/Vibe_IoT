using System.Globalization;

namespace IoTCom.Net.Protocols.Nmea;

/// <summary>GPS fix quality (GGA field 6).</summary>
public enum GpsFixQuality
{
    /// <summary>No fix.</summary>
    Invalid = 0,
    /// <summary>Autonomous GPS fix.</summary>
    Gps = 1,
    /// <summary>Differential GPS.</summary>
    Dgps = 2,
    /// <summary>PPS fix.</summary>
    Pps = 3,
    /// <summary>RTK fixed integer.</summary>
    RtkFixed = 4,
    /// <summary>RTK float.</summary>
    RtkFloat = 5,
    /// <summary>Dead reckoning.</summary>
    Estimated = 6,
    /// <summary>Manual input.</summary>
    Manual = 7,
    /// <summary>Simulation.</summary>
    Simulation = 8,
}

/// <summary>Base of typed NMEA messages.</summary>
/// <param name="Sentence">The underlying raw sentence.</param>
public abstract record NmeaMessage(NmeaSentence Sentence);

/// <summary>A sentence without a typed decoder.</summary>
public sealed record UnknownNmeaMessage(NmeaSentence Sentence) : NmeaMessage(Sentence);

/// <summary>GGA — fix data.</summary>
public sealed record GgaMessage(NmeaSentence Sentence, TimeOnly? Time, double? Latitude, double? Longitude, GpsFixQuality FixQuality,
    int Satellites, double? Hdop, double? AltitudeMeters, double? GeoidSeparationMeters) : NmeaMessage(Sentence);

/// <summary>RMC — recommended minimum data.</summary>
public sealed record RmcMessage(NmeaSentence Sentence, DateTimeOffset? Timestamp, bool Active, double? Latitude, double? Longitude,
    double? SpeedKnots, double? CourseDegrees, double? MagneticVariation) : NmeaMessage(Sentence)
{
    /// <summary>Speed over ground in km/h.</summary>
    public double? SpeedKmh => SpeedKnots * 1.852;
}

/// <summary>Satellite in view (GSV).</summary>
/// <param name="Prn">Satellite id.</param>
/// <param name="Elevation">Elevation in degrees.</param>
/// <param name="Azimuth">Azimuth in degrees.</param>
/// <param name="Snr">Signal-to-noise ratio dB-Hz (null when not tracking).</param>
public readonly record struct SatelliteInfo(int Prn, int? Elevation, int? Azimuth, int? Snr);

/// <summary>GSV — satellites in view (one of several messages).</summary>
public sealed record GsvMessage(NmeaSentence Sentence, int TotalMessages, int MessageNumber, int SatellitesInView, IReadOnlyList<SatelliteInfo> Satellites) : NmeaMessage(Sentence);

/// <summary>GSA — DOP and active satellites.</summary>
public sealed record GsaMessage(NmeaSentence Sentence, bool AutoMode, int FixType, IReadOnlyList<int> ActivePrns, double? Pdop, double? Hdop, double? Vdop) : NmeaMessage(Sentence);

/// <summary>VTG — track and ground speed.</summary>
public sealed record VtgMessage(NmeaSentence Sentence, double? TrueCourse, double? MagneticCourse, double? SpeedKnots, double? SpeedKmh) : NmeaMessage(Sentence);

/// <summary>GLL — geographic position.</summary>
public sealed record GllMessage(NmeaSentence Sentence, double? Latitude, double? Longitude, TimeOnly? Time, bool Active) : NmeaMessage(Sentence);

/// <summary>ZDA — UTC date and time.</summary>
public sealed record ZdaMessage(NmeaSentence Sentence, DateTimeOffset? Timestamp) : NmeaMessage(Sentence);

/// <summary>Decodes raw sentences into typed messages.</summary>
public static class NmeaParser
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Parses a line into a typed message, or returns null for invalid input.</summary>
    public static NmeaMessage? Parse(string line) => NmeaSentence.TryParse(line, out var s) ? Decode(s!) : null;

    /// <summary>Decodes an already parsed sentence.</summary>
    public static NmeaMessage Decode(NmeaSentence s)
    {
        try
        {
            return s.Type switch
            {
                "GGA" => new GgaMessage(s, Time(s[0]), Coordinate(s[1], s[2]), Coordinate(s[3], s[4]),
                    (GpsFixQuality)(Int(s[5]) ?? 0), Int(s[6]) ?? 0, Double(s[7]), Double(s[8]), Double(s[10])),
                "RMC" => new RmcMessage(s, DateTimeUtc(s[8], s[0]), s[1] == "A", Coordinate(s[2], s[3]), Coordinate(s[4], s[5]),
                    Double(s[6]), Double(s[7]), Double(s[9]) is { } mv ? (s[10] == "W" ? -mv : mv) : null),
                "GSV" => DecodeGsv(s),
                "GSA" => new GsaMessage(s, s[0] == "A", Int(s[1]) ?? 1,
                    Enumerable.Range(2, 12).Select(i => Int(s[i])).Where(p => p.HasValue).Select(p => p!.Value).ToArray(),
                    Double(s[14]), Double(s[15]), Double(s[16])),
                "VTG" => new VtgMessage(s, Double(s[0]), Double(s[2]), Double(s[4]), Double(s[6])),
                "GLL" => new GllMessage(s, Coordinate(s[0], s[1]), Coordinate(s[2], s[3]), Time(s[4]), s[5] == "A"),
                "ZDA" => new ZdaMessage(s, Int(s[1]) is { } d && Int(s[2]) is { } m && Int(s[3]) is { } y && Time(s[0]) is { } t
                    ? new DateTimeOffset(new DateOnly(y, m, d), t, TimeSpan.Zero) : null),
                _ => new UnknownNmeaMessage(s),
            };
        }
        catch (ArgumentException)
        {
            return new UnknownNmeaMessage(s);
        }
    }

    private static GsvMessage DecodeGsv(NmeaSentence s)
    {
        var sats = new List<SatelliteInfo>(4);
        for (var i = 3; i + 3 < s.Fields.Count + 1 && i < 19; i += 4)
        {
            if (Int(s[i]) is { } prn) sats.Add(new SatelliteInfo(prn, Int(s[i + 1]), Int(s[i + 2]), Int(s[i + 3])));
        }
        return new GsvMessage(s, Int(s[0]) ?? 1, Int(s[1]) ?? 1, Int(s[2]) ?? 0, sats);
    }

    /// <summary>Converts NMEA <c>ddmm.mmmm</c> + hemisphere into signed decimal degrees.</summary>
    public static double? Coordinate(string value, string hemisphere)
    {
        if (string.IsNullOrEmpty(value) || !double.TryParse(value, NumberStyles.Float, Inv, out var raw)) return null;
        var degrees = Math.Floor(raw / 100);
        var result = degrees + (raw - degrees * 100) / 60;
        return hemisphere is "S" or "W" ? -result : result;
    }

    /// <summary>Formats signed decimal degrees as NMEA <c>(d)ddmm.mmmmm</c> and hemisphere.</summary>
    public static (string Value, string Hemisphere) FormatCoordinate(double degrees, bool isLatitude)
    {
        var hemi = isLatitude ? (degrees < 0 ? "S" : "N") : (degrees < 0 ? "W" : "E");
        var abs = Math.Abs(degrees);
        var d = Math.Floor(abs);
        var minutes = (abs - d) * 60;
        var value = isLatitude
            ? string.Create(Inv, $"{d:00}{minutes:00.00000}")
            : string.Create(Inv, $"{d:000}{minutes:00.00000}");
        return (value, hemi);
    }

    private static TimeOnly? Time(string v)
    {
        if (v.Length < 6) return null;
        if (!int.TryParse(v.AsSpan(0, 2), Inv, out var h) || !int.TryParse(v.AsSpan(2, 2), Inv, out var m)) return null;
        if (!double.TryParse(v.AsSpan(4), NumberStyles.Float, Inv, out var sec)) return null;
        return new TimeOnly(h, m).Add(TimeSpan.FromSeconds(sec));
    }

    private static DateTimeOffset? DateTimeUtc(string date, string time)
    {
        if (date.Length != 6 || Time(time) is not { } t) return null;
        if (!int.TryParse(date.AsSpan(0, 2), Inv, out var d) || !int.TryParse(date.AsSpan(2, 2), Inv, out var mo) || !int.TryParse(date.AsSpan(4, 2), Inv, out var y)) return null;
        return new DateTimeOffset(new DateOnly(y >= 80 ? 1900 + y : 2000 + y, mo, d), t, TimeSpan.Zero); // NMEA two-digit year pivot
    }

    private static int? Int(string v) => int.TryParse(v, NumberStyles.Integer, Inv, out var r) ? r : null;

    private static double? Double(string v) => double.TryParse(v, NumberStyles.Float, Inv, out var r) ? r : null;
}
