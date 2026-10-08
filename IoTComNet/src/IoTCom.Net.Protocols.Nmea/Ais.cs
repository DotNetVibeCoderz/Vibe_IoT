using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace IoTCom.Net.Protocols.Nmea;

/// <summary>Navigational status (AIS message types 1–3).</summary>
public enum AisNavigationStatus
{
    /// <summary>Under way using engine.</summary>
    UnderWayUsingEngine = 0,
    /// <summary>At anchor.</summary>
    AtAnchor = 1,
    /// <summary>Not under command.</summary>
    NotUnderCommand = 2,
    /// <summary>Restricted manoeuvrability.</summary>
    RestrictedManoeuvrability = 3,
    /// <summary>Constrained by draught.</summary>
    ConstrainedByDraught = 4,
    /// <summary>Moored.</summary>
    Moored = 5,
    /// <summary>Aground.</summary>
    Aground = 6,
    /// <summary>Engaged in fishing.</summary>
    Fishing = 7,
    /// <summary>Under way sailing.</summary>
    UnderWaySailing = 8,
    /// <summary>AIS-SART active.</summary>
    AisSart = 14,
    /// <summary>Not defined.</summary>
    NotDefined = 15,
}

/// <summary>A decoded AIS message.</summary>
/// <param name="Type">Message type (1–27).</param>
/// <param name="Repeat">Repeat indicator.</param>
/// <param name="Mmsi">Maritime Mobile Service Identity.</param>
public abstract record AisMessage(int Type, int Repeat, uint Mmsi);

/// <summary>Position report (types 1, 2, 3 class A; 18, 19 class B; 27 long range).</summary>
public sealed record AisPositionReport(int Type, int Repeat, uint Mmsi) : AisMessage(Type, Repeat, Mmsi)
{
    /// <summary>Navigational status (class A).</summary>
    public AisNavigationStatus Status { get; init; } = AisNavigationStatus.NotDefined;
    /// <summary>Rate of turn in °/min (null when not available).</summary>
    public double? RateOfTurn { get; init; }
    /// <summary>Speed over ground in knots (null when not available).</summary>
    public double? SpeedOverGround { get; init; }
    /// <summary>Position accuracy better than 10 m.</summary>
    public bool HighAccuracy { get; init; }
    /// <summary>Longitude in degrees (null when not available).</summary>
    public double? Longitude { get; init; }
    /// <summary>Latitude in degrees (null when not available).</summary>
    public double? Latitude { get; init; }
    /// <summary>Course over ground in degrees (null when not available).</summary>
    public double? CourseOverGround { get; init; }
    /// <summary>True heading in degrees (null when not available).</summary>
    public int? Heading { get; init; }
    /// <summary>UTC second of the report (60+ = not available).</summary>
    public int Second { get; init; } = 60;
    /// <summary>Class B: vessel name (type 19).</summary>
    public string? Name { get; init; }
    /// <summary>Class B: ship type (type 19).</summary>
    public int? ShipType { get; init; }
}

/// <summary>Base station report (type 4): UTC time and position of a shore station.</summary>
public sealed record AisBaseStationReport(int Repeat, uint Mmsi, DateTime? Utc, double? Longitude, double? Latitude) : AisMessage(4, Repeat, Mmsi);

/// <summary>Static and voyage data (type 5) or class B static data (type 24).</summary>
public sealed record AisStaticData(int Type, int Repeat, uint Mmsi) : AisMessage(Type, Repeat, Mmsi)
{
    /// <summary>IMO number (type 5).</summary>
    public uint? Imo { get; init; }
    /// <summary>Call sign.</summary>
    public string? CallSign { get; init; }
    /// <summary>Vessel name.</summary>
    public string? Name { get; init; }
    /// <summary>Ship and cargo type.</summary>
    public int? ShipType { get; init; }
    /// <summary>Length in metres (bow + stern).</summary>
    public int? Length { get; init; }
    /// <summary>Beam in metres (port + starboard).</summary>
    public int? Beam { get; init; }
    /// <summary>ETA (month, day, hour, minute; year unknown).</summary>
    public string? Eta { get; init; }
    /// <summary>Draught in metres.</summary>
    public double? Draught { get; init; }
    /// <summary>Destination.</summary>
    public string? Destination { get; init; }
    /// <summary>Type 24 part number (0 = A, 1 = B).</summary>
    public int? Part { get; init; }
}

/// <summary>Aid to navigation report (type 21): buoys, lights, beacons.</summary>
public sealed record AisAidToNavigation(int Repeat, uint Mmsi, int AidType, string Name, double? Longitude, double? Latitude, bool Virtual) : AisMessage(21, Repeat, Mmsi);

/// <summary>A message type this decoder does not interpret (payload kept as bits).</summary>
public sealed record AisUnknownMessage(int Type, int Repeat, uint Mmsi, int BitLength) : AisMessage(Type, Repeat, Mmsi);

/// <summary>AIS helpers: ship type names, MMSI classification.</summary>
public static class Ais
{
    /// <summary>Ship type name (ITU-R M.1371 table 53).</summary>
    public static string ShipTypeName(int? type) => type switch
    {
        null or 0 => "Not available",
        >= 20 and <= 29 => "Wing in ground",
        30 => "Fishing",
        31 or 32 => "Towing",
        33 => "Dredging",
        34 => "Diving",
        35 => "Military",
        36 => "Sailing",
        37 => "Pleasure craft",
        >= 40 and <= 49 => "High-speed craft",
        50 => "Pilot vessel",
        51 => "Search and rescue",
        52 => "Tug",
        53 => "Port tender",
        55 => "Law enforcement",
        58 => "Medical transport",
        >= 60 and <= 69 => "Passenger",
        >= 70 and <= 79 => "Cargo",
        >= 80 and <= 89 => "Tanker",
        _ => "Other",
    };

    /// <summary>Kind of station by MMSI pattern.</summary>
    public static string StationKind(uint mmsi) => mmsi switch
    {
        >= 2_000_000 and < 8_000_000 when mmsi % 1000 == 0 => "Ship",
        < 1_000_000 => "Group / coast",
        >= 970_000_000 and < 980_000_000 => "SART / MOB / EPIRB",
        >= 990_000_000 and < 1_000_000_000 => "Aid to navigation",
        >= 111_000_000 and < 112_000_000 => "SAR aircraft",
        _ => mmsi / 10_000_000 == 0 ? "Coast station" : "Ship",
    };
}

/// <summary>
/// Decodes AIS messages carried in <c>!AIVDM</c>/<c>!AIVDO</c> sentences: reassembles multi-sentence messages, removes
/// the 6-bit armouring and decodes the common message types (1–5, 18, 19, 21, 24, 27). Feed sentences in arrival order.
/// </summary>
public sealed class AisDecoder
{
    private readonly Dictionary<string, (int Total, string?[] Parts, int Fill, DateTime Started)> _pending = [];

    /// <summary>Messages dropped because a fragment was missing or the payload was malformed.</summary>
    public long Dropped { get; private set; }

    /// <summary>Feeds one sentence; returns a message when it completes one, otherwise null.</summary>
    public AisMessage? Feed(NmeaSentence sentence)
    {
        ArgumentNullException.ThrowIfNull(sentence);
        if (sentence.Type is not ("VDM" or "VDO") || sentence.Fields.Count < 6) return null;
        if (!int.TryParse(sentence[0], NumberStyles.None, CultureInfo.InvariantCulture, out var total) || total is < 1 or > 9
            || !int.TryParse(sentence[1], NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1 || number > total)
        {
            Dropped++;
            return null;
        }

        int.TryParse(sentence[5], NumberStyles.None, CultureInfo.InvariantCulture, out var fill);
        if (total == 1) return Decode(sentence[4], fill);

        var key = $"{sentence[2]}|{sentence[3]}|{total}";
        if (number == 1 || !_pending.TryGetValue(key, out var entry))
        {
            if (number != 1)
            {
                Dropped++;
                return null;
            }

            entry = (total, new string?[total], 0, DateTime.UtcNow);
        }

        entry.Parts[number - 1] = sentence[4];
        if (number == total) entry.Fill = fill;
        _pending[key] = entry;
        if (entry.Parts.Any(p => p is null)) return null;
        _pending.Remove(key);
        return Decode(string.Concat(entry.Parts), entry.Fill);
    }

    /// <summary>Feeds one line of text (non-AIS lines return null).</summary>
    public AisMessage? Feed(string line) => NmeaSentence.TryParse(line, out var s) ? Feed(s!) : null;

    /// <summary>Decodes one armoured payload; returns null (and counts a drop) when it is malformed.</summary>
    public AisMessage? Decode(string payload, int fillBits = 0)
    {
        if (!AisBits.TryDearmour(payload, fillBits, out var bits) || bits.Length < 38)
        {
            Dropped++;
            return null;
        }

        try
        {
            return AisBits.DecodeMessage(bits);
        }
        catch (ArgumentOutOfRangeException)
        {
            Dropped++;
            return null;
        }
    }
}

/// <summary>Bit-level AIS codec: armouring, fields, 6-bit text, and encoders for the simulator.</summary>
public static class AisBits
{
    /// <summary>Converts an armoured payload into bits.</summary>
    public static bool TryDearmour(string payload, int fillBits, out bool[] bits)
    {
        bits = [];
        if (payload is null || fillBits is < 0 or > 5) return false;
        var list = new bool[(payload.Length * 6) - fillBits < 0 ? 0 : (payload.Length * 6) - fillBits];
        var pos = 0;
        foreach (var ch in payload)
        {
            int v = ch;
            if (v is < 48 or > 119 or (> 87 and < 96)) return false;
            v -= 48;
            if (v > 40) v -= 8;
            for (var b = 5; b >= 0; b--)
            {
                if (pos < list.Length) list[pos] = ((v >> b) & 1) != 0;
                pos++;
            }
        }

        bits = list;
        return true;
    }

    /// <summary>Armours bits into a payload; returns it with the fill-bit count.</summary>
    public static (string Payload, int FillBits) Armour(IReadOnlyList<bool> bits)
    {
        ArgumentNullException.ThrowIfNull(bits);
        var fill = (6 - (bits.Count % 6)) % 6;
        var sb = new StringBuilder((bits.Count + 5) / 6);
        for (var i = 0; i < bits.Count + fill; i += 6)
        {
            var v = 0;
            for (var b = 0; b < 6; b++) v = (v << 1) | (i + b < bits.Count && bits[i + b] ? 1 : 0);
            sb.Append((char)(v < 40 ? v + 48 : v + 56));
        }

        return (sb.ToString(), fill);
    }

    internal static uint U(bool[] bits, int start, int length)
    {
        if (start + length > bits.Length) throw new ArgumentOutOfRangeException(nameof(length), "AIS field past the end of the payload.");
        uint v = 0;
        for (var i = 0; i < length; i++) v = (v << 1) | (bits[start + i] ? 1u : 0u);
        return v;
    }

    internal static int S(bool[] bits, int start, int length)
    {
        var v = (int)U(bits, start, length);
        return bits[start] ? v - (1 << length) : v;
    }

    internal static string Text(bool[] bits, int start, int length)
    {
        var sb = new StringBuilder();
        for (var i = 0; i + 6 <= length && start + i + 6 <= bits.Length; i += 6)
        {
            var c = (int)U(bits, start + i, 6);
            sb.Append((char)(c < 32 ? c + 64 : c));
        }

        return sb.ToString().TrimEnd('@', ' ');
    }

    private static double? Coord(bool[] bits, int start, int length, double scale, int unavailable)
    {
        var raw = S(bits, start, length);
        return raw == unavailable ? null : Math.Round(raw / scale, 6);
    }

    internal static AisMessage DecodeMessage(bool[] bits)
    {
        var type = (int)U(bits, 0, 6);
        var repeat = (int)U(bits, 6, 2);
        var mmsi = U(bits, 8, 30);
        switch (type)
        {
            case 1 or 2 or 3:
            {
                var rot = S(bits, 42, 8);
                var sog = U(bits, 50, 10);
                var cog = U(bits, 116, 12);
                var heading = U(bits, 128, 9);
                return new AisPositionReport(type, repeat, mmsi)
                {
                    Status = (AisNavigationStatus)U(bits, 38, 4),
                    RateOfTurn = rot == -128 ? null : Math.Round(Math.Sign(rot) * Math.Pow(rot / 4.733, 2), 1),
                    SpeedOverGround = sog == 1023 ? null : sog / 10.0,
                    HighAccuracy = U(bits, 60, 1) == 1,
                    Longitude = Coord(bits, 61, 28, 600_000, 108_600_000),
                    Latitude = Coord(bits, 89, 27, 600_000, 54_600_000),
                    CourseOverGround = cog == 3600 ? null : cog / 10.0,
                    Heading = heading == 511 ? null : (int)heading,
                    Second = (int)U(bits, 137, 6),
                };
            }

            case 4:
            {
                var y = (int)U(bits, 38, 14);
                var mo = (int)U(bits, 52, 4);
                var d = (int)U(bits, 56, 5);
                var h = (int)U(bits, 61, 5);
                var mi = (int)U(bits, 66, 6);
                var s = (int)U(bits, 72, 6);
                DateTime? utc = y is > 0 and < 9999 && mo is >= 1 and <= 12 && d is >= 1 and <= 31 && h < 24 && mi < 60 && s < 60 && d <= DateTime.DaysInMonth(y, mo)
                    ? new DateTime(y, mo, d, h, mi, s, DateTimeKind.Utc) : null;
                return new AisBaseStationReport(repeat, mmsi, utc, Coord(bits, 79, 28, 600_000, 108_600_000), Coord(bits, 107, 27, 600_000, 54_600_000));
            }

            case 5:
            {
                var imo = U(bits, 40, 30);
                var month = U(bits, 274, 4);
                var day = U(bits, 278, 5);
                var hour = U(bits, 283, 5);
                var minute = U(bits, 288, 6);
                return new AisStaticData(5, repeat, mmsi)
                {
                    Imo = imo == 0 ? null : imo,
                    CallSign = Text(bits, 70, 42),
                    Name = Text(bits, 112, 120),
                    ShipType = (int)U(bits, 232, 8),
                    Length = (int)(U(bits, 240, 9) + U(bits, 249, 9)),
                    Beam = (int)(U(bits, 258, 6) + U(bits, 264, 6)),
                    Eta = month == 0 ? null : $"{month:00}-{day:00} {hour:00}:{minute:00}",
                    Draught = U(bits, 294, 8) / 10.0,
                    Destination = Text(bits, 302, 120),
                };
            }

            case 18 or 19:
            {
                var sog = U(bits, 46, 10);
                var cog = U(bits, 112, 12);
                var heading = U(bits, 124, 9);
                return new AisPositionReport(type, repeat, mmsi)
                {
                    SpeedOverGround = sog == 1023 ? null : sog / 10.0,
                    HighAccuracy = U(bits, 56, 1) == 1,
                    Longitude = Coord(bits, 57, 28, 600_000, 108_600_000),
                    Latitude = Coord(bits, 85, 27, 600_000, 54_600_000),
                    CourseOverGround = cog == 3600 ? null : cog / 10.0,
                    Heading = heading == 511 ? null : (int)heading,
                    Second = (int)U(bits, 133, 6),
                    Name = type == 19 ? Text(bits, 143, 120) : null,
                    ShipType = type == 19 ? (int)U(bits, 263, 8) : null,
                };
            }

            case 21:
                return new AisAidToNavigation(repeat, mmsi, (int)U(bits, 38, 5), Text(bits, 43, 120),
                    Coord(bits, 164, 28, 600_000, 108_600_000), Coord(bits, 192, 27, 600_000, 54_600_000), U(bits, 269, 1) == 1);

            case 24:
            {
                var part = (int)U(bits, 38, 2);
                return part == 0
                    ? new AisStaticData(24, repeat, mmsi) { Part = 0, Name = Text(bits, 40, 120) }
                    : new AisStaticData(24, repeat, mmsi)
                    {
                        Part = 1,
                        ShipType = (int)U(bits, 40, 8),
                        CallSign = Text(bits, 90, 42),
                        Length = (int)(U(bits, 132, 9) + U(bits, 141, 9)),
                        Beam = (int)(U(bits, 150, 6) + U(bits, 156, 6)),
                    };
            }

            case 27:
            {
                var sog = U(bits, 79, 6);
                var cog = U(bits, 85, 9);
                return new AisPositionReport(27, repeat, mmsi)
                {
                    HighAccuracy = U(bits, 38, 1) == 1,
                    Status = (AisNavigationStatus)U(bits, 40, 4),
                    Longitude = Coord(bits, 44, 18, 600, 181 * 600),
                    Latitude = Coord(bits, 62, 17, 600, 91 * 600),
                    SpeedOverGround = sog == 63 ? null : sog,
                    CourseOverGround = cog == 511 ? null : cog,
                };
            }

            default:
                return new AisUnknownMessage(type, repeat, mmsi, bits.Length);
        }
    }

    // ---- encoders (simulator, tests) ----------------------------------------------------------------------

    private sealed class Writer
    {
        public readonly List<bool> Bits = [];

        public Writer U(long value, int length)
        {
            for (var i = length - 1; i >= 0; i--) Bits.Add(((value >> i) & 1) != 0);
            return this;
        }

        public Writer Text(string? text, int chars)
        {
            var t = (text ?? "").ToUpperInvariant().PadRight(chars, '@');
            for (var i = 0; i < chars; i++)
            {
                var c = t[i];
                var v = c is >= '@' and <= '_' ? c - 64 : c is >= ' ' and <= '?' ? c : 0;
                U(v, 6);
            }

            return this;
        }
    }

    private static long Lon(double? v) => v is { } d ? (long)Math.Round(d * 600_000) & 0xFFFFFFF : 108_600_000;

    private static long Lat(double? v) => v is { } d ? (long)Math.Round(d * 600_000) & 0x7FFFFFF : 54_600_000;

    /// <summary>Encodes a class A (types 1–3) or class B (type 18) position report into payload bits.</summary>
    public static IReadOnlyList<bool> EncodePosition(AisPositionReport r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var w = new Writer();
        var sog = r.SpeedOverGround is { } s ? (long)Math.Round(s * 10) : 1023;
        var cog = r.CourseOverGround is { } c ? (long)Math.Round(c * 10) : 3600;
        if (r.Type is 1 or 2 or 3)
        {
            var rot = r.RateOfTurn is { } t ? (long)Math.Round(Math.Sign(t) * 4.733 * Math.Sqrt(Math.Abs(t))) & 0xFF : 0x80;
            w.U(r.Type, 6).U(r.Repeat, 2).U(r.Mmsi, 30).U((int)r.Status, 4).U(rot, 8).U(sog, 10).U(r.HighAccuracy ? 1 : 0, 1)
                .U(Lon(r.Longitude), 28).U(Lat(r.Latitude), 27).U(cog, 12).U(r.Heading ?? 511, 9).U(r.Second, 6).U(0, 2).U(0, 3).U(0, 1).U(0, 19);
        }
        else if (r.Type == 18)
        {
            w.U(18, 6).U(r.Repeat, 2).U(r.Mmsi, 30).U(0, 8).U(sog, 10).U(r.HighAccuracy ? 1 : 0, 1).U(Lon(r.Longitude), 28).U(Lat(r.Latitude), 27)
                .U(cog, 12).U(r.Heading ?? 511, 9).U(r.Second, 6).U(0, 2).U(1, 1).U(0, 1).U(1, 1).U(1, 1).U(1, 1).U(0, 1).U(0, 1).U(0, 20);
        }
        else
        {
            throw new ArgumentException($"Encoding type {r.Type} is not supported.", nameof(r));
        }

        return w.Bits;
    }

    /// <summary>Encodes static and voyage data (type 5, 424 bits).</summary>
    public static IReadOnlyList<bool> EncodeStatic(AisStaticData s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var length = s.Length ?? 0;
        var beam = s.Beam ?? 0;
        var w = new Writer().U(5, 6).U(s.Repeat, 2).U(s.Mmsi, 30).U(0, 2).U(s.Imo ?? 0, 30).Text(s.CallSign, 7).Text(s.Name, 20).U(s.ShipType ?? 0, 8)
            .U(length / 2, 9).U(length - (length / 2), 9).U(beam / 2, 6).U(beam - (beam / 2), 6).U(1, 4);
        var eta = s.Eta is { Length: 11 } e ? e : "00-00 24:60";
        w.U(int.Parse(eta[..2], CultureInfo.InvariantCulture), 4).U(int.Parse(eta[3..5], CultureInfo.InvariantCulture), 5)
            .U(int.Parse(eta[6..8], CultureInfo.InvariantCulture), 5).U(int.Parse(eta[9..11], CultureInfo.InvariantCulture), 6)
            .U((long)Math.Round((s.Draught ?? 0) * 10), 8).Text(s.Destination, 20).U(0, 1).U(0, 1);
        return w.Bits;
    }

    /// <summary>Wraps payload bits into one or more <c>!AIVDM</c> sentences (at most 60 characters each).</summary>
    public static IReadOnlyList<string> ToSentences(IReadOnlyList<bool> bits, char channel = 'A', int sequentialId = 1, string talker = "AI")
    {
        var (payload, fill) = Armour(bits);
        var chunks = Enumerable.Range(0, (payload.Length + 59) / 60).Select(i => payload.Substring(i * 60, Math.Min(60, payload.Length - (i * 60)))).ToList();
        var sentences = new List<string>(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            var last = i == chunks.Count - 1;
            var seq = chunks.Count > 1 ? sequentialId.ToString(CultureInfo.InvariantCulture) : "";
            var body = $"{talker}VDM,{chunks.Count},{i + 1},{seq},{channel},{chunks[i]},{(last ? fill : 0)}";
            sentences.Add($"!{body}*{NmeaSentence.Checksum(body):X2}");
        }

        return sentences;
    }
}

/// <summary>A vessel as tracked from AIS traffic.</summary>
public sealed class AisVessel(uint mmsi)
{
    /// <summary>MMSI.</summary>
    public uint Mmsi { get; } = mmsi;
    /// <summary>Name (type 5, 19 or 24A).</summary>
    public string? Name { get; internal set; }
    /// <summary>Call sign.</summary>
    public string? CallSign { get; internal set; }
    /// <summary>Ship type.</summary>
    public int? ShipType { get; internal set; }
    /// <summary>Destination.</summary>
    public string? Destination { get; internal set; }
    /// <summary>Length (m).</summary>
    public int? Length { get; internal set; }
    /// <summary>Latitude.</summary>
    public double? Latitude { get; internal set; }
    /// <summary>Longitude.</summary>
    public double? Longitude { get; internal set; }
    /// <summary>Speed over ground (kn).</summary>
    public double? Speed { get; internal set; }
    /// <summary>Course over ground (°).</summary>
    public double? Course { get; internal set; }
    /// <summary>Heading (°).</summary>
    public int? Heading { get; internal set; }
    /// <summary>Navigational status.</summary>
    public AisNavigationStatus Status { get; internal set; } = AisNavigationStatus.NotDefined;
    /// <summary>Class B (types 18, 19, 24).</summary>
    public bool ClassB { get; internal set; }
    /// <summary>Last message time.</summary>
    public DateTimeOffset LastSeen { get; internal set; }
    /// <summary>Messages received.</summary>
    public int Messages { get; internal set; }

    /// <inheritdoc />
    public override string ToString() =>
        $"{Mmsi} {Name ?? "?"} {Latitude?.ToString("0.0000", CultureInfo.InvariantCulture)},{Longitude?.ToString("0.0000", CultureInfo.InvariantCulture)} {Speed?.ToString("0.0", CultureInfo.InvariantCulture)} kn";
}

/// <summary>Maintains a vessel table from decoded AIS messages (thread-safe).</summary>
public sealed class AisTracker
{
    private readonly ConcurrentDictionary<uint, AisVessel> _vessels = new();

    /// <summary>Raised after a vessel changed.</summary>
    public event Action<AisVessel>? VesselUpdated;

    /// <summary>All vessels.</summary>
    public IReadOnlyCollection<AisVessel> Vessels => [.. _vessels.Values];

    /// <summary>Applies a message.</summary>
    public void Apply(AisMessage message, DateTimeOffset? time = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message is AisBaseStationReport or AisAidToNavigation or AisUnknownMessage) return;
        var v = _vessels.GetOrAdd(message.Mmsi, m => new AisVessel(m));
        lock (v)
        {
            v.Messages++;
            v.LastSeen = time ?? DateTimeOffset.UtcNow;
            switch (message)
            {
                case AisPositionReport p:
                    (v.Latitude, v.Longitude) = (p.Latitude ?? v.Latitude, p.Longitude ?? v.Longitude);
                    (v.Speed, v.Course, v.Heading) = (p.SpeedOverGround, p.CourseOverGround, p.Heading);
                    if (p.Type is 1 or 2 or 3) v.Status = p.Status;
                    if (p.Type is 18 or 19) v.ClassB = true;
                    if (p.Name is { Length: > 0 } n) v.Name = n;
                    if (p.ShipType is { } st) v.ShipType = st;
                    break;
                case AisStaticData s:
                    if (s.Name is { Length: > 0 } name) v.Name = name;
                    if (s.CallSign is { Length: > 0 } cs) v.CallSign = cs;
                    if (s.ShipType is { } t) v.ShipType = t;
                    if (s.Destination is { Length: > 0 } d) v.Destination = d;
                    if (s.Length is > 0) v.Length = s.Length;
                    if (s.Type == 24) v.ClassB = true;
                    break;
            }
        }

        VesselUpdated?.Invoke(v);
    }
}

/// <summary>
/// Simulated harbour traffic for demos and tests: vessels around the Tanjung Priok approaches (Jakarta Bay) that
/// emit class A/B position reports and static data as <c>!AIVDM</c> sentences.
/// </summary>
public sealed class AisSimulator
{
    private readonly Random _random;
    private readonly List<SimVessel> _vessels = [];
    private int _sequence;
    private int _tick;

    private sealed class SimVessel
    {
        public required AisStaticData Static;
        public double Lat, Lon, Speed, Course;
        public bool ClassB;
        public AisNavigationStatus Status;
    }

    /// <summary>Creates the fleet.</summary>
    public AisSimulator(int seed = 9)
    {
        _random = new Random(seed);
        void Add(uint mmsi, string name, string call, int type, int length, int beam, string dest, double draught, double lat, double lon, double speed, double course, bool classB = false, AisNavigationStatus status = AisNavigationStatus.UnderWayUsingEngine)
            => _vessels.Add(new SimVessel
            {
                Static = new AisStaticData(5, 0, mmsi) { Imo = classB ? null : 9_000_000 + (mmsi % 900_000), CallSign = call, Name = name, ShipType = type, Length = length, Beam = beam, Destination = dest, Draught = draught, Eta = "10-12 06:00" },
                Lat = lat, Lon = lon, Speed = speed, Course = course, ClassB = classB, Status = status,
            });
        Add(525_005_123, "KM SINAR JAKARTA", "PKZA", 70, 182, 28, "TANJUNG PRIOK", 9.8, -5.98, 106.86, 11.5, 175);
        Add(525_012_456, "PERTAMINA GAS 2", "PMLQ", 84, 230, 36, "BALONGAN", 10.6, -6.03, 106.95, 9.2, 95);
        Add(525_019_789, "KMP NUSA BAHARI", "YCTB", 60, 96, 18, "PULAU SERIBU", 3.4, -6.05, 106.80, 14.0, 330);
        Add(563_041_200, "MAERSK SELETAR", "9V8641", 71, 300, 45, "IDTPP", 13.1, -5.90, 106.98, 13.8, 205);
        Add(525_100_321, "TB PRIOK 7", "PNRA", 52, 32, 10, "TANJUNG PRIOK", 4.0, -6.08, 106.89, 6.5, 10);
        Add(525_200_654, "KM BINTANG LAUT", "", 30, 24, 6, "MUARA ANGKE", 2.2, -6.06, 106.76, 7.0, 290, classB: true, status: AisNavigationStatus.Fishing);
        Add(525_300_987, "MV ANCHOR STAR", "PLQB", 70, 150, 23, "TANJUNG PRIOK", 8.2, -6.02, 106.90, 0.1, 0, status: AisNavigationStatus.AtAnchor);
    }

    /// <summary>Advances the fleet by <paramref name="dt"/> and returns the sentences transmitted in that step.</summary>
    public IReadOnlyList<string> Step(TimeSpan dt)
    {
        var lines = new List<string>();
        _tick++;
        foreach (var v in _vessels)
        {
            var hours = dt.TotalHours;
            v.Course = (v.Course + ((_random.NextDouble() - 0.5) * 2)) % 360;
            v.Lat += v.Speed * hours / 60 * Math.Cos(v.Course * Math.PI / 180);
            v.Lon += v.Speed * hours / 60 * Math.Sin(v.Course * Math.PI / 180) / Math.Cos(v.Lat * Math.PI / 180);
            var report = new AisPositionReport(v.ClassB ? 18 : 1, 0, v.Static.Mmsi)
            {
                Status = v.Status,
                RateOfTurn = v.ClassB ? null : 0,
                SpeedOverGround = Math.Round(v.Speed + ((_random.NextDouble() - 0.5) * 0.4), 1),
                HighAccuracy = true,
                Latitude = v.Lat,
                Longitude = v.Lon,
                CourseOverGround = Math.Round((v.Course + 360) % 360, 1),
                Heading = (int)Math.Round((v.Course + 360) % 360) % 360,
                Second = DateTime.UtcNow.Second,
            };
            lines.AddRange(AisBits.ToSentences(AisBits.EncodePosition(report), _tick % 2 == 0 ? 'A' : 'B'));
            if (!v.ClassB && _tick % 6 == 1)
                lines.AddRange(AisBits.ToSentences(AisBits.EncodeStatic(v.Static), 'A', (_sequence++ % 9) + 1));
            if (v.ClassB && _tick % 6 == 1)
            {
                var w = new List<bool>();
                void U(long value, int length)
                {
                    for (var i = length - 1; i >= 0; i--) w.Add(((value >> i) & 1) != 0);
                }

                U(24, 6); U(0, 2); U(v.Static.Mmsi, 30); U(0, 2);
                foreach (var c in (v.Static.Name ?? "").PadRight(20, '@')[..20]) U(c is >= '@' and <= '_' ? c - 64 : c, 6);
                lines.AddRange(AisBits.ToSentences(w, 'B'));

                // Part B: ship type, vendor id, call sign, dimensions.
                w.Clear();
                U(24, 6); U(0, 2); U(v.Static.Mmsi, 30); U(1, 2); U(v.Static.ShipType ?? 0, 8);
                foreach (var c in "IOTCOM@") U(c - 64, 6);
                U(0, 4); U(0, 20);
                foreach (var c in (v.Static.CallSign is { Length: > 0 } cs ? cs : "@").PadRight(7, '@')[..7]) U(c is >= '@' and <= '_' ? c - 64 : c, 6);
                var length = v.Static.Length ?? 0;
                var beam = v.Static.Beam ?? 0;
                U(length / 2, 9); U(length - (length / 2), 9); U(beam / 2, 6); U(beam - (beam / 2), 6); U(0, 6);
                lines.AddRange(AisBits.ToSentences(w, 'B'));
            }
        }

        return lines;
    }
}
