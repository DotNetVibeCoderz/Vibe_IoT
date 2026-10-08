using System.Globalization;

namespace IoTCom.Net.Protocols.LoRaWan;

/// <summary>A LoRa data rate: spreading factor and bandwidth.</summary>
/// <param name="SpreadingFactor">SF7..SF12.</param>
/// <param name="BandwidthKHz">125, 250 or 500 kHz.</param>
/// <param name="MaxPayload">Maximum application payload N (no FOpts) at this data rate.</param>
public readonly record struct LoRaDataRate(int SpreadingFactor, int BandwidthKHz, int MaxPayload)
{
    /// <summary>Semtech UDP notation, e.g. <c>SF7BW125</c>.</summary>
    public string Datr => $"SF{SpreadingFactor}BW{BandwidthKHz}";

    /// <summary>Parses <c>SF7BW125</c>.</summary>
    public static (int Sf, int Bw) ParseDatr(string datr)
    {
        ArgumentNullException.ThrowIfNull(datr);
        var bw = datr.IndexOf("BW", StringComparison.OrdinalIgnoreCase);
        if (!datr.StartsWith("SF", StringComparison.OrdinalIgnoreCase) || bw < 0) throw new FormatException($"Not a LoRa data rate: '{datr}'.");
        return (int.Parse(datr.AsSpan(2, bw - 2), CultureInfo.InvariantCulture), int.Parse(datr.AsSpan(bw + 2), CultureInfo.InvariantCulture));
    }

    /// <inheritdoc />
    public override string ToString() => Datr;
}

/// <summary>
/// Regional parameters (RP002-1.0.x) used by the simulator and the light network server: data rates, default uplink
/// channels, RX1 and RX2 rules and transmit power. EU868, US915 (sub-band 2) and AS923-2 (Indonesia, Vietnam) are built in.
/// </summary>
public sealed class LoRaRegion
{
    private LoRaRegion(string name, LoRaDataRate[] dataRates, double[] uplinkChannels, double rx2Frequency, int rx2DataRate, int defaultTxPowerDbm, int uplinkMaxDr)
    {
        Name = name;
        DataRates = dataRates;
        UplinkChannels = uplinkChannels;
        Rx2Frequency = rx2Frequency;
        Rx2DataRate = rx2DataRate;
        DefaultTxPowerDbm = defaultTxPowerDbm;
        UplinkMaxDataRate = uplinkMaxDr;
    }

    /// <summary>Region name.</summary>
    public string Name { get; }

    /// <summary>Data rates by index (DR0, DR1, ...).</summary>
    public IReadOnlyList<LoRaDataRate> DataRates { get; }

    /// <summary>Default uplink channels in MHz.</summary>
    public IReadOnlyList<double> UplinkChannels { get; }

    /// <summary>RX2 frequency in MHz.</summary>
    public double Rx2Frequency { get; }

    /// <summary>RX2 data rate index.</summary>
    public int Rx2DataRate { get; }

    /// <summary>Default downlink transmit power (dBm EIRP).</summary>
    public int DefaultTxPowerDbm { get; }

    /// <summary>Highest LoRa uplink data rate index.</summary>
    public int UplinkMaxDataRate { get; }

    /// <summary>Europe 863–870 MHz.</summary>
    public static LoRaRegion EU868 { get; } = new("EU868",
        [new(12, 125, 51), new(11, 125, 51), new(10, 125, 51), new(9, 125, 115), new(8, 125, 222), new(7, 125, 222), new(7, 250, 222)],
        [868.1, 868.3, 868.5], 869.525, 0, 14, 5);

    /// <summary>United States 902–928 MHz, sub-band 2 (channels 8–15, as used by most public networks).</summary>
    public static LoRaRegion US915 { get; } = new("US915",
        [new(10, 125, 11), new(9, 125, 53), new(8, 125, 125), new(7, 125, 242), new(8, 500, 242), default, default, default,
         new(12, 500, 53), new(11, 500, 129), new(10, 500, 242), new(9, 500, 242), new(8, 500, 242), new(7, 500, 242)],
        [903.9, 904.1, 904.3, 904.5, 904.7, 904.9, 905.1, 905.3], 923.3, 8, 20, 3);

    /// <summary>AS923-2 (920–923 MHz; Indonesia, Vietnam), dwell time limit off.</summary>
    public static LoRaRegion AS923Group2 { get; } = new("AS923-2",
        [new(12, 125, 51), new(11, 125, 51), new(10, 125, 51), new(9, 125, 115), new(8, 125, 222), new(7, 125, 222), new(7, 250, 222)],
        [921.4, 921.6], 921.4, 2, 16, 5);

    /// <summary>All built-in regions.</summary>
    public static IReadOnlyList<LoRaRegion> All { get; } = [EU868, US915, AS923Group2];

    /// <summary>Finds a region by name (case-insensitive; <c>AS923</c> means AS923-2).</summary>
    public static LoRaRegion Get(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Equals("AS923", StringComparison.OrdinalIgnoreCase)) return AS923Group2;
        return All.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Unknown region '{name}'. Known: {string.Join(", ", All.Select(r => r.Name))}.", nameof(name));
    }

    /// <summary>The data rate index of a <c>SF7BW125</c> string (uplink table first).</summary>
    public int DataRateIndex(string datr)
    {
        var (sf, bw) = LoRaDataRate.ParseDatr(datr);
        for (var i = 0; i < DataRates.Count; i++)
            if (DataRates[i].SpreadingFactor == sf && DataRates[i].BandwidthKHz == bw) return i;
        throw new ArgumentException($"{datr} is not a data rate of {Name}.", nameof(datr));
    }

    /// <summary>RX1 frequency for an uplink on <paramref name="uplinkMHz"/>.</summary>
    public double Rx1Frequency(double uplinkMHz)
    {
        if (this != US915) return uplinkMHz;
        var channel = (int)Math.Round((uplinkMHz - 902.3) / 0.2);
        return Math.Round(923.3 + (channel % 8 * 0.6), 1);
    }

    /// <summary>RX1 data rate for an uplink data rate and the RX1 offset.</summary>
    public int Rx1DataRate(int uplinkDr, int rx1DrOffset = 0) =>
        this == US915 ? Math.Clamp(10 + uplinkDr - rx1DrOffset, 8, 13) : Math.Max(0, uplinkDr - rx1DrOffset);

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>LoRa time on air (Semtech AN1200.13), used by the simulator, the dashboard and duty-cycle accounting.</summary>
public static class LoRaAirtime
{
    /// <summary>Time on air of a PHYPayload of <paramref name="payloadBytes"/> bytes.</summary>
    /// <param name="payloadBytes">PHYPayload length (13 bytes of LoRaWAN overhead plus the application payload).</param>
    /// <param name="spreadingFactor">SF7..SF12.</param>
    /// <param name="bandwidthKHz">Bandwidth.</param>
    /// <param name="codingRate">1 for 4/5 … 4 for 4/8.</param>
    /// <param name="preambleSymbols">Preamble length (8 for LoRaWAN).</param>
    /// <param name="explicitHeader">Explicit header (LoRaWAN: yes).</param>
    /// <param name="crc">Payload CRC (LoRaWAN: uplink yes, downlink no).</param>
    public static TimeSpan Compute(int payloadBytes, int spreadingFactor, int bandwidthKHz = 125, int codingRate = 1, int preambleSymbols = 8, bool explicitHeader = true, bool crc = true)
    {
        var tsym = Math.Pow(2, spreadingFactor) / (bandwidthKHz * 1000.0);
        var lowDataRateOptimize = tsym > 0.016 ? 1 : 0;
        var numerator = (8.0 * payloadBytes) - (4 * spreadingFactor) + 28 + (crc ? 16 : 0) - (explicitHeader ? 0 : 20);
        var symbols = 8 + Math.Max(Math.Ceiling(numerator / (4.0 * (spreadingFactor - (2 * lowDataRateOptimize)))) * (codingRate + 4), 0);
        var seconds = ((preambleSymbols + 4.25) * tsym) + (symbols * tsym);
        return TimeSpan.FromTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond));
    }

    /// <summary>Time on air for a Semtech <c>datr</c> string (<c>SF9BW125</c>).</summary>
    public static TimeSpan Compute(int payloadBytes, string datr, bool crc = true)
    {
        var (sf, bw) = LoRaDataRate.ParseDatr(datr);
        return Compute(payloadBytes, sf, bw, crc: crc);
    }
}
