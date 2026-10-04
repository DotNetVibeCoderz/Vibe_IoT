namespace IoTCom.Net.Protocols.Nmea;

/// <summary>A consolidated GNSS fix built from GGA/RMC/GSA/GSV/VTG messages.</summary>
public sealed record GnssFix
{
    /// <summary>UTC time of the last position.</summary>
    public DateTimeOffset? Timestamp { get; init; }
    /// <summary>Latitude in decimal degrees (south negative).</summary>
    public double? Latitude { get; init; }
    /// <summary>Longitude in decimal degrees (west negative).</summary>
    public double? Longitude { get; init; }
    /// <summary>Altitude above mean sea level, metres.</summary>
    public double? AltitudeMeters { get; init; }
    /// <summary>Speed over ground, km/h.</summary>
    public double? SpeedKmh { get; init; }
    /// <summary>Course over ground, degrees true.</summary>
    public double? CourseDegrees { get; init; }
    /// <summary>Fix quality.</summary>
    public GpsFixQuality Quality { get; init; }
    /// <summary>Satellites used in the solution.</summary>
    public int SatellitesUsed { get; init; }
    /// <summary>Horizontal dilution of precision.</summary>
    public double? Hdop { get; init; }
    /// <summary>Position dilution of precision.</summary>
    public double? Pdop { get; init; }
    /// <summary>Satellites in view (from GSV).</summary>
    public IReadOnlyList<SatelliteInfo> SatellitesInView { get; init; } = [];

    /// <summary>True when a valid position is available.</summary>
    public bool HasFix => Quality != GpsFixQuality.Invalid && Latitude.HasValue && Longitude.HasValue;
}

/// <summary>Aggregates NMEA messages into a <see cref="GnssFix"/>. Thread-safe.</summary>
public sealed class GnssState
{
    private readonly Lock _gate = new();
    private GnssFix _fix = new();
    private readonly Dictionary<string, List<SatelliteInfo>> _gsvBuild = [];

    /// <summary>Latest consolidated fix.</summary>
    public GnssFix Current { get { lock (_gate) return _fix; } }

    /// <summary>Raised when the fix changes.</summary>
    public event Action<GnssFix>? Updated;

    /// <summary>Applies a message. Returns true when the state changed.</summary>
    public bool Apply(NmeaMessage message)
    {
        GnssFix next;
        lock (_gate)
        {
            var f = _fix;
            next = message switch
            {
                GgaMessage g => f with
                {
                    Latitude = g.Latitude ?? f.Latitude,
                    Longitude = g.Longitude ?? f.Longitude,
                    AltitudeMeters = g.AltitudeMeters ?? f.AltitudeMeters,
                    Quality = g.FixQuality,
                    SatellitesUsed = g.Satellites,
                    Hdop = g.Hdop ?? f.Hdop,
                },
                RmcMessage r => f with
                {
                    Timestamp = r.Timestamp ?? f.Timestamp,
                    Latitude = r.Latitude ?? f.Latitude,
                    Longitude = r.Longitude ?? f.Longitude,
                    SpeedKmh = r.SpeedKmh ?? f.SpeedKmh,
                    CourseDegrees = r.CourseDegrees ?? f.CourseDegrees,
                },
                GsaMessage a => f with { Pdop = a.Pdop ?? f.Pdop, Hdop = a.Hdop ?? f.Hdop },
                VtgMessage v => f with { SpeedKmh = v.SpeedKmh ?? f.SpeedKmh, CourseDegrees = v.TrueCourse ?? f.CourseDegrees },
                GsvMessage s => ApplyGsv(f, s),
                _ => f,
            };
            if (ReferenceEquals(next, f) || next == f) return false;
            _fix = next;
        }
        Updated?.Invoke(next);
        return true;
    }

    private GnssFix ApplyGsv(GnssFix f, GsvMessage s)
    {
        var key = s.Sentence.Talker;
        if (s.MessageNumber == 1 || !_gsvBuild.TryGetValue(key, out var list)) _gsvBuild[key] = list = [];
        list.AddRange(s.Satellites);
        if (s.MessageNumber < s.TotalMessages) return f;
        // Merge satellites from all talkers (GP, GL, GA, GB) seen in the latest complete cycles.
        var all = _gsvBuild.Values.SelectMany(v => v).GroupBy(x => x.Prn).Select(g => g.Last()).OrderBy(x => x.Prn).ToArray();
        return f with { SatellitesInView = all };
    }
}
