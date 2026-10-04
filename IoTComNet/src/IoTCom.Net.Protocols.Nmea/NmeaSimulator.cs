using System.Globalization;

namespace IoTCom.Net.Protocols.Nmea;

/// <summary>
/// Generates realistic NMEA epochs (GGA, RMC, GSA, GSV, VTG) for a vehicle moving on a circular track.
/// Deterministic for a given start time and seed — ideal for tests, notebooks and the Gallery.
/// </summary>
public sealed class NmeaSimulator
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly int[] SatellitePrns = [2, 5, 7, 9, 13, 15, 18, 20, 24, 29];
    private readonly Random _rng;
    private readonly (int Prn, double Elevation, double Azimuth, double Drift)[] _satellites;

    /// <summary>Creates a simulator centred on <paramref name="latitude"/>/<paramref name="longitude"/>.</summary>
    /// <param name="latitude">Centre latitude (default: Bandung, Indonesia).</param>
    /// <param name="longitude">Centre longitude.</param>
    /// <param name="radiusMeters">Track radius.</param>
    /// <param name="speedKmh">Ground speed.</param>
    /// <param name="seed">Random seed.</param>
    public NmeaSimulator(double latitude = -6.9147, double longitude = 107.6098, double radiusMeters = 400, double speedKmh = 36, int seed = 7)
    {
        CenterLatitude = latitude;
        CenterLongitude = longitude;
        RadiusMeters = radiusMeters;
        SpeedKmh = speedKmh;
        _rng = new Random(seed);
        _satellites = Enumerable.Range(0, 10)
            .Select(i => (Prn: SatellitePrns[i], Elevation: 15 + _rng.NextDouble() * 70, Azimuth: _rng.NextDouble() * 360, Drift: (_rng.NextDouble() - 0.5) * 0.02))
            .ToArray();
    }

    /// <summary>Track centre latitude.</summary>
    public double CenterLatitude { get; }
    /// <summary>Track centre longitude.</summary>
    public double CenterLongitude { get; }
    /// <summary>Track radius in metres.</summary>
    public double RadiusMeters { get; }
    /// <summary>Speed in km/h.</summary>
    public double SpeedKmh { get; set; }
    /// <summary>Talker id (default <c>GP</c>).</summary>
    public string Talker { get; init; } = "GP";
    /// <summary>Altitude above MSL in metres.</summary>
    public double AltitudeMeters { get; init; } = 768;

    /// <summary>Position on the track after <paramref name="elapsed"/>.</summary>
    public (double Latitude, double Longitude, double Course) PositionAt(TimeSpan elapsed)
    {
        var circumference = 2 * Math.PI * RadiusMeters;
        var distance = SpeedKmh / 3.6 * elapsed.TotalSeconds;
        var angle = distance / circumference * 2 * Math.PI;
        var north = RadiusMeters * Math.Cos(angle);
        var east = RadiusMeters * Math.Sin(angle);
        var lat = CenterLatitude + north / 111_320.0;
        var lon = CenterLongitude + east / (111_320.0 * Math.Cos(CenterLatitude * Math.PI / 180));
        var course = (angle * 180 / Math.PI + 90) % 360;
        return (lat, lon, course);
    }

    /// <summary>Generates one epoch of sentences for UTC time <paramref name="utc"/>, <paramref name="elapsed"/> after the start.</summary>
    public IReadOnlyList<string> GenerateEpoch(DateTimeOffset utc, TimeSpan elapsed)
    {
        var (lat, lon, course) = PositionAt(elapsed);
        var (latV, latH) = NmeaParser.FormatCoordinate(lat, isLatitude: true);
        var (lonV, lonH) = NmeaParser.FormatCoordinate(lon, isLatitude: false);
        var time = utc.ToString("HHmmss.ff", Inv);
        var date = utc.ToString("ddMMyy", Inv);
        var knots = SpeedKmh / 1.852;
        var hdop = 0.8 + _rng.NextDouble() * 0.4;
        var alt = AltitudeMeters + Math.Sin(elapsed.TotalSeconds / 20) * 2;

        var sats = _satellites.Select(s => (s.Prn,
            Elevation: (int)Math.Clamp(s.Elevation + s.Drift * elapsed.TotalSeconds, 5, 89),
            Azimuth: (int)((s.Azimuth + elapsed.TotalSeconds * 0.01) % 360),
            Snr: 30 + (int)(s.Elevation / 6) + _rng.Next(-2, 3))).ToArray();
        var used = sats.Where(s => s.Elevation > 10).Take(12).ToArray();

        var list = new List<string>
        {
            NmeaSentence.Build(Talker, "GGA", time, latV, latH, lonV, lonH, "1", used.Length.ToString("00", Inv), F(hdop, 1), F(alt, 1), "M", "-0.3", "M", "", ""),
            NmeaSentence.Build(Talker, "RMC", time, "A", latV, latH, lonV, lonH, F(knots, 2), F(course, 1), date, "", "", "A"),
            NmeaSentence.Build(Talker, "VTG", F(course, 1), "T", "", "M", F(knots, 2), "N", F(SpeedKmh, 2), "K", "A"),
        };
        var gsa = new List<string> { "A", "3" };
        gsa.AddRange(Enumerable.Range(0, 12).Select(i => i < used.Length ? used[i].Prn.ToString("00", Inv) : ""));
        gsa.AddRange([F(hdop * 1.6, 1), F(hdop, 1), F(hdop * 1.3, 1)]);
        list.Add(NmeaSentence.Build(Talker, "GSA", [.. gsa]));

        var total = (sats.Length + 3) / 4;
        for (var m = 0; m < total; m++)
        {
            var fields = new List<string> { total.ToString(Inv), (m + 1).ToString(Inv), sats.Length.ToString("00", Inv) };
            foreach (var s in sats.Skip(m * 4).Take(4))
                fields.AddRange([s.Prn.ToString("00", Inv), s.Elevation.ToString("00", Inv), s.Azimuth.ToString("000", Inv), s.Snr.ToString("00", Inv)]);
            list.Add(NmeaSentence.Build(Talker, "GSV", [.. fields]));
        }
        return list;

        static string F(double v, int decimals) => v.ToString("F" + decimals.ToString(Inv), Inv);
    }

    /// <summary>Publishes one epoch per <paramref name="interval"/> to <paramref name="server"/> until cancelled.</summary>
    public async Task RunAsync(NmeaServer server, TimeSpan? interval = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        var start = DateTimeOffset.UtcNow;
        using var timer = new PeriodicTimer(interval ?? TimeSpan.FromSeconds(1));
        try
        {
            do
            {
                var now = DateTimeOffset.UtcNow;
                foreach (var s in GenerateEpoch(now, now - start)) await server.BroadcastAsync(s, ct).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
    }
}
