using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Text;

namespace IoTCom.Net.Protocols.Ntp;

/// <summary>
/// A 64-bit NTP timestamp: seconds since 1900-01-01 UTC (32 bits) and a binary fraction (32 bits). Conversion uses the
/// RFC 4330 era rule: a seconds value with the top bit clear belongs to era 1 (from 2036-02-07 06:28:16 UTC), so
/// timestamps map onto 1968–2104.
/// </summary>
/// <param name="Raw">The 64 bits as sent on the wire.</param>
public readonly record struct NtpTimestamp(ulong Raw)
{
    private static readonly DateTime Era0 = new(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Era1 = new(2036, 2, 7, 6, 28, 16, DateTimeKind.Utc);

    /// <summary>Zero (meaning "not set").</summary>
    public static NtpTimestamp Zero => default;

    /// <summary>Whole seconds.</summary>
    public uint Seconds => (uint)(Raw >> 32);

    /// <summary>Fraction of a second in units of 2⁻³² s.</summary>
    public uint Fraction => (uint)Raw;

    /// <summary>True for the zero timestamp.</summary>
    public bool IsZero => Raw == 0;

    /// <summary>Converts a UTC time (1968–2104).</summary>
    /// <exception cref="ArgumentOutOfRangeException">Outside the representable range.</exception>
    public static NtpTimestamp FromDateTime(DateTime utc)
    {
        var t = utc.Kind == DateTimeKind.Local ? utc.ToUniversalTime() : utc;
        var baseTime = t >= Era1 ? Era1 : Era0;
        var ticks = (t - baseTime).Ticks;
        if (ticks < 0 || (baseTime == Era0 && t < new DateTime(1968, 1, 20, 3, 14, 8, DateTimeKind.Utc)))
            throw new ArgumentOutOfRangeException(nameof(utc), "NTP timestamps cover 1968–2104.");
        var seconds = (ulong)(ticks / TimeSpan.TicksPerSecond);
        if (seconds > uint.MaxValue || (baseTime == Era1 && seconds >= 0x8000_0000))
            throw new ArgumentOutOfRangeException(nameof(utc), "NTP timestamps cover 1968–2104.");
        var fraction = (((ulong)(ticks % TimeSpan.TicksPerSecond) << 32) + ((ulong)TimeSpan.TicksPerSecond / 2)) / (ulong)TimeSpan.TicksPerSecond;   // nearest
        return new NtpTimestamp((seconds << 32) | fraction);
    }

    /// <summary>Converts to UTC (100 ns resolution).</summary>
    public DateTime ToDateTime()
    {
        var baseTime = (Seconds & 0x8000_0000) == 0 ? Era1 : Era0;
        var fractionTicks = (long)((((ulong)Fraction * (ulong)TimeSpan.TicksPerSecond) + (1UL << 31)) >> 32);   // nearest 100 ns
        return baseTime.AddTicks((Seconds * TimeSpan.TicksPerSecond) + fractionTicks);
    }

    /// <summary>
    /// <c>a − b</c> in seconds using on-wire arithmetic (the 64-bit difference interpreted as signed), which stays correct
    /// across the era boundary as long as the two times are within 68 years of each other.
    /// </summary>
    public static double Difference(NtpTimestamp a, NtpTimestamp b) => (long)(a.Raw - b.Raw) / 4294967296.0;

    /// <inheritdoc />
    public override string ToString() => IsZero ? "0" : ToDateTime().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
}

/// <summary>Association modes.</summary>
public enum NtpMode : byte
{
    /// <summary>Reserved (0).</summary>
    Reserved = 0,
    /// <summary>Symmetric active (1).</summary>
    SymmetricActive = 1,
    /// <summary>Symmetric passive (2).</summary>
    SymmetricPassive = 2,
    /// <summary>Client (3).</summary>
    Client = 3,
    /// <summary>Server (4).</summary>
    Server = 4,
    /// <summary>Broadcast (5).</summary>
    Broadcast = 5,
    /// <summary>NTP control message (6).</summary>
    Control = 6,
    /// <summary>Private (7).</summary>
    Private = 7,
}

/// <summary>Leap indicator.</summary>
public enum NtpLeap : byte
{
    /// <summary>No warning.</summary>
    None = 0,
    /// <summary>Last minute of the day has 61 seconds.</summary>
    AddSecond = 1,
    /// <summary>Last minute of the day has 59 seconds.</summary>
    DeleteSecond = 2,
    /// <summary>Clock not synchronised (alarm).</summary>
    Unsynchronised = 3,
}

/// <summary>
/// An NTP packet (RFC 5905 §7.3): the 48-octet header plus anything that follows (extension fields, MAC), kept as
/// <see cref="Trailer"/>. Root delay and dispersion are in seconds (NTP short format, 16.16).
/// </summary>
public sealed record NtpPacket
{
    /// <summary>Header length.</summary>
    public const int HeaderLength = 48;

    /// <summary>Default UDP port.</summary>
    public const int DefaultPort = 123;

    /// <summary>Leap indicator.</summary>
    public NtpLeap Leap { get; init; }

    /// <summary>Version (3 or 4 in practice).</summary>
    public byte Version { get; init; } = 4;

    /// <summary>Mode.</summary>
    public NtpMode Mode { get; init; }

    /// <summary>Stratum: 0 = kiss-o'-death / unspecified, 1 = primary reference, 2–15 = secondary, 16 = unsynchronised.</summary>
    public byte Stratum { get; init; }

    /// <summary>Poll interval exponent (log₂ seconds).</summary>
    public sbyte Poll { get; init; }

    /// <summary>Precision exponent (log₂ seconds).</summary>
    public sbyte Precision { get; init; }

    /// <summary>Round-trip delay to the reference clock, seconds.</summary>
    public double RootDelay { get; init; }

    /// <summary>Dispersion to the reference clock, seconds.</summary>
    public double RootDispersion { get; init; }

    /// <summary>Reference identifier (four ASCII characters for stratum 0–1, an IPv4 address or hash above).</summary>
    public uint ReferenceId { get; init; }

    /// <summary>Time the system clock was last set.</summary>
    public NtpTimestamp Reference { get; init; }

    /// <summary>T1 echoed by the server (client transmit time).</summary>
    public NtpTimestamp Originate { get; init; }

    /// <summary>T2: time the request arrived at the server.</summary>
    public NtpTimestamp Receive { get; init; }

    /// <summary>T3 (server) or T1 (client): time the packet left.</summary>
    public NtpTimestamp Transmit { get; init; }

    /// <summary>Extension fields and MAC (not interpreted).</summary>
    public ReadOnlyMemory<byte> Trailer { get; init; }

    /// <summary>A kiss-o'-death packet (stratum 0 from a server).</summary>
    public bool IsKissOfDeath => Stratum == 0 && Mode is NtpMode.Server or NtpMode.Broadcast or NtpMode.SymmetricPassive;

    /// <summary>The reference identifier as text: ASCII for stratum 0–1 ("GPS", "PPS", or a kiss code such as "RATE"), dotted IPv4 above.</summary>
    public string ReferenceIdText => Stratum <= 1 ? Ascii(ReferenceId) : Dotted(ReferenceId);

    private static string Dotted(uint id) => string.Create(CultureInfo.InvariantCulture, $"{id >> 24}.{(id >> 16) & 0xFF}.{(id >> 8) & 0xFF}.{id & 0xFF}");

    /// <summary>Encodes four ASCII characters (padded with zeros) as a reference identifier.</summary>
    public static uint ReferenceCode(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        if (code.Length > 4 || code.Any(c => c > 0x7E)) throw new ArgumentException("A reference code is up to four ASCII characters.", nameof(code));
        Span<byte> b = stackalloc byte[4];
        Encoding.ASCII.GetBytes(code, b);
        return BinaryPrimitives.ReadUInt32BigEndian(b);
    }

    /// <summary>The reference identifier of an IPv4 server address (stratum 2 and above).</summary>
    public static uint ReferenceAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return BinaryPrimitives.ReadUInt32BigEndian(address.MapToIPv4().GetAddressBytes());
    }

    private static string Ascii(uint id)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, id);
        var n = b.IndexOf((byte)0) is var z and >= 0 ? z : 4;
        foreach (var c in b[..n])
            if (c is < 0x20 or > 0x7E) return $"0x{id:X8}";
        return Encoding.ASCII.GetString(b[..n]);
    }

    /// <summary>Encodes the packet.</summary>
    public byte[] Encode()
    {
        var b = new byte[HeaderLength + Trailer.Length];
        b[0] = (byte)(((byte)Leap << 6) | ((Version & 7) << 3) | ((byte)Mode & 7));
        b[1] = Stratum;
        b[2] = (byte)Poll;
        b[3] = (byte)Precision;
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), Short(RootDelay));
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8), Short(RootDispersion));
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(12), ReferenceId);
        BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(16), Reference.Raw);
        BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(24), Originate.Raw);
        BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(32), Receive.Raw);
        BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(40), Transmit.Raw);
        Trailer.Span.CopyTo(b.AsSpan(HeaderLength));
        return b;
    }

    private static uint Short(double seconds) => (uint)Math.Clamp(Math.Round(seconds * 65536), 0, uint.MaxValue);

    /// <summary>Parses a packet.</summary>
    /// <exception cref="ProtocolException">Shorter than 48 octets.</exception>
    public static NtpPacket Parse(ReadOnlySpan<byte> d)
    {
        if (d.Length < HeaderLength) throw new ProtocolException($"NTP packet of {d.Length} octets; the header alone is {HeaderLength}.");
        return new NtpPacket
        {
            Leap = (NtpLeap)(d[0] >> 6),
            Version = (byte)((d[0] >> 3) & 7),
            Mode = (NtpMode)(d[0] & 7),
            Stratum = d[1],
            Poll = (sbyte)d[2],
            Precision = (sbyte)d[3],
            RootDelay = BinaryPrimitives.ReadUInt32BigEndian(d[4..]) / 65536.0,
            RootDispersion = BinaryPrimitives.ReadUInt32BigEndian(d[8..]) / 65536.0,
            ReferenceId = BinaryPrimitives.ReadUInt32BigEndian(d[12..]),
            Reference = new NtpTimestamp(BinaryPrimitives.ReadUInt64BigEndian(d[16..])),
            Originate = new NtpTimestamp(BinaryPrimitives.ReadUInt64BigEndian(d[24..])),
            Receive = new NtpTimestamp(BinaryPrimitives.ReadUInt64BigEndian(d[32..])),
            Transmit = new NtpTimestamp(BinaryPrimitives.ReadUInt64BigEndian(d[40..])),
            Trailer = d[HeaderLength..].ToArray(),
        };
    }

    /// <summary>The frame lane.</summary>
    public static IReadOnlyList<FrameField> Describe(ReadOnlySpan<byte> d)
    {
        if (d.Length < HeaderLength) return [new FrameField("Bytes", 0, d.Length, FrameFieldKind.Error, "shorter than the 48-octet header")];
        var p = Parse(d);
        var f = new List<FrameField>
        {
            new("LI/VN/Mode", 0, 1, FrameFieldKind.Function, $"leap {(byte)p.Leap}, v{p.Version}, {p.Mode}"),
            new("Stratum", 1, 1, p.IsKissOfDeath ? FrameFieldKind.Error : FrameFieldKind.Header, p.IsKissOfDeath ? $"0 (kiss-o'-death {p.ReferenceIdText})" : p.Stratum.ToString(CultureInfo.InvariantCulture)),
            new("Poll", 2, 1, FrameFieldKind.Header, string.Create(CultureInfo.InvariantCulture, $"2^{p.Poll} s")),
            new("Precision", 3, 1, FrameFieldKind.Header, string.Create(CultureInfo.InvariantCulture, $"2^{p.Precision} s")),
            new("Root delay", 4, 4, FrameFieldKind.Data, string.Create(CultureInfo.InvariantCulture, $"{p.RootDelay * 1000:0.###} ms")),
            new("Root disp.", 8, 4, FrameFieldKind.Data, string.Create(CultureInfo.InvariantCulture, $"{p.RootDispersion * 1000:0.###} ms")),
            new("Ref ID", 12, 4, FrameFieldKind.Address, p.ReferenceIdText),
            new("Reference", 16, 8, FrameFieldKind.Data, p.Reference.ToString()),
            new("Originate", 24, 8, FrameFieldKind.Data, p.Originate.ToString()),
            new("Receive", 32, 8, FrameFieldKind.Data, p.Receive.ToString()),
            new("Transmit", 40, 8, FrameFieldKind.Data, p.Transmit.ToString()),
        };
        if (d.Length > HeaderLength) f.Add(new("Extensions/MAC", HeaderLength, d.Length - HeaderLength, FrameFieldKind.Checksum));
        return f;
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"NTPv{Version} {Mode} stratum {Stratum} ref {ReferenceIdText}{(Leap != NtpLeap.None ? $" leap {Leap}" : "")} transmit {Transmit}";
}
