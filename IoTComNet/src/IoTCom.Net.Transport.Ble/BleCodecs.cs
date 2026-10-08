using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace IoTCom.Net.Transport.Ble;

/// <summary>Bluetooth SIG UUIDs: short-form expansion on the base UUID and names of common services and characteristics.</summary>
public static class BleUuid
{
    private const string BaseSuffix = "-0000-1000-8000-00805f9b34fb";

    private static readonly Dictionary<ushort, string> Names = new()
    {
        [0x1800] = "Generic Access", [0x1801] = "Generic Attribute", [0x180A] = "Device Information", [0x180D] = "Heart Rate",
        [0x180F] = "Battery", [0x181A] = "Environmental Sensing", [0x1816] = "Cycling Speed and Cadence", [0x1809] = "Health Thermometer",
        [0x2A00] = "Device Name", [0x2A01] = "Appearance", [0x2A19] = "Battery Level", [0x2A24] = "Model Number String",
        [0x2A25] = "Serial Number String", [0x2A26] = "Firmware Revision String", [0x2A29] = "Manufacturer Name String",
        [0x2A37] = "Heart Rate Measurement", [0x2A38] = "Body Sensor Location", [0x2A1C] = "Temperature Measurement",
        [0x2A6D] = "Pressure", [0x2A6E] = "Temperature", [0x2A6F] = "Humidity", [0x2A5B] = "CSC Measurement",
        [0xFEAA] = "Eddystone",
    };

    /// <summary>Expands a 16/32-bit short form ("2a37", "0x180D") or parses a 128-bit UUID.</summary>
    public static Guid Parse(string uuid)
    {
        ArgumentNullException.ThrowIfNull(uuid);
        var t = uuid.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
        if (t.Length is > 0 and <= 8 && uint.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var shortForm)) return FromShort(shortForm);
        return Guid.TryParse(t, out var g) ? g : throw new FormatException($"'{uuid}' is not a UUID (16-bit like 2a37, or 128-bit).");
    }

    /// <summary>The 128-bit UUID of a 16/32-bit SIG value.</summary>
    public static Guid FromShort(uint value) => Guid.Parse(value.ToString("x8", CultureInfo.InvariantCulture) + BaseSuffix);

    /// <summary>The 16-bit value when <paramref name="uuid"/> is on the SIG base and fits 16 bits.</summary>
    public static ushort? ToShort(Guid uuid)
    {
        var s = uuid.ToString();
        return s.EndsWith(BaseSuffix, StringComparison.OrdinalIgnoreCase) && s.StartsWith("0000", StringComparison.Ordinal)
            ? ushort.Parse(s.AsSpan(4, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>A readable name ("Heart Rate Measurement") or the short/long form.</summary>
    public static string Name(Guid uuid) => ToShort(uuid) is { } s ? Names.TryGetValue(s, out var n) ? n : $"0x{s:X4}" : uuid.ToString();

    /// <summary>Compact text: "2A37" for SIG UUIDs, the full form otherwise.</summary>
    public static string Short(Guid uuid) => ToShort(uuid) is { } s ? s.ToString("X4", CultureInfo.InvariantCulture) : uuid.ToString();
}

/// <summary>An advertisement as received by a scan.</summary>
public sealed record BleAdvertisement
{
    /// <summary>Platform id used to connect (address on Windows/Linux, a UUID on macOS).</summary>
    public required string Id { get; init; }

    /// <summary>Bluetooth address, when the platform exposes it.</summary>
    public string? Address { get; init; }

    /// <summary>Local name.</summary>
    public string? Name { get; init; }

    /// <summary>Received signal strength (dBm).</summary>
    public int? Rssi { get; init; }

    /// <summary>Advertised TX power (dBm).</summary>
    public int? TxPower { get; init; }

    /// <summary>Advertised service UUIDs.</summary>
    public IReadOnlyList<Guid> Services { get; init; } = [];

    /// <summary>Manufacturer specific data by company id (0x004C Apple, 0x0059 Nordic, …).</summary>
    public IReadOnlyDictionary<ushort, byte[]> ManufacturerData { get; init; } = new Dictionary<ushort, byte[]>();

    /// <summary>Service data by UUID.</summary>
    public IReadOnlyDictionary<Guid, byte[]> ServiceData { get; init; } = new Dictionary<Guid, byte[]>();

    /// <summary>An iBeacon frame, if this is one.</summary>
    public IBeacon? IBeacon => ManufacturerData.TryGetValue(0x004C, out var d) ? Ble.IBeacon.TryParse(d) : null;

    /// <summary>An Eddystone frame, if this is one.</summary>
    public EddystoneFrame? Eddystone => ServiceData.TryGetValue(BleUuid.FromShort(0xFEAA), out var d) ? EddystoneFrame.TryParse(d) : null;

    /// <summary>
    /// Estimated distance in metres (free-space path loss, exponent 2) from RSSI and the 1 m reference: the iBeacon's
    /// measured power, or the advertised TX power (at 0 m) minus 41 dB.
    /// </summary>
    public double? EstimatedDistance => Rssi is { } r && (IBeacon?.MeasuredPower ?? TxPower - 41) is { } atOneMetre ? Math.Round(Math.Pow(10, (atOneMetre - r) / 20.0), 2) : null;

    /// <inheritdoc />
    public override string ToString() => $"{Name ?? "(no name)"} {Id} {Rssi?.ToString(CultureInfo.InvariantCulture) ?? "?"} dBm {string.Join(",", Services.Select(BleUuid.Short))}";
}

/// <summary>Advertising data (AD structures) as transmitted over the air: length, type, data.</summary>
public static class AdvertisingData
{
    /// <summary>Decodes AD structures into an advertisement (flags and unknown types are skipped).</summary>
    public static BleAdvertisement Parse(string id, ReadOnlySpan<byte> data, int? rssi = null)
    {
        string? name = null;
        int? tx = null;
        var services = new List<Guid>();
        var manufacturer = new Dictionary<ushort, byte[]>();
        var serviceData = new Dictionary<Guid, byte[]>();
        var pos = 0;
        while (pos < data.Length)
        {
            int len = data[pos];
            if (len == 0) break;
            if (pos + 1 + len > data.Length) throw new FormatException($"AD structure at {pos} claims {len} bytes, {data.Length - pos - 1} remain.");
            var type = data[pos + 1];
            var value = data.Slice(pos + 2, len - 1);
            switch (type)
            {
                case 0x02 or 0x03:
                    for (var i = 0; i + 1 < value.Length; i += 2) services.Add(BleUuid.FromShort(BinaryPrimitives.ReadUInt16LittleEndian(value[i..])));
                    break;
                case 0x06 or 0x07:
                    for (var i = 0; i + 15 < value.Length; i += 16) services.Add(FromLittleEndian(value.Slice(i, 16)));
                    break;
                case 0x08 or 0x09:
                    name = Encoding.UTF8.GetString(value);
                    break;
                case 0x0A when value.Length == 1:
                    tx = unchecked((sbyte)value[0]);
                    break;
                case 0x16 when value.Length >= 2:
                    serviceData[BleUuid.FromShort(BinaryPrimitives.ReadUInt16LittleEndian(value))] = value[2..].ToArray();
                    break;
                case 0xFF when value.Length >= 2:
                    manufacturer[BinaryPrimitives.ReadUInt16LittleEndian(value)] = value[2..].ToArray();
                    break;
            }

            pos += 1 + len;
        }

        return new BleAdvertisement { Id = id, Name = name, Rssi = rssi, TxPower = tx, Services = services, ManufacturerData = manufacturer, ServiceData = serviceData };
    }

    /// <summary>Encodes an advertisement as AD structures (flags, name, 16-bit services, TX power, service and manufacturer data).</summary>
    public static byte[] Encode(BleAdvertisement ad)
    {
        ArgumentNullException.ThrowIfNull(ad);
        var b = new List<byte>();
        void Add(byte type, ReadOnlySpan<byte> value)
        {
            b.Add((byte)(value.Length + 1));
            b.Add(type);
            b.AddRange(value.ToArray());
        }

        Add(0x01, [0x06]);   // LE General Discoverable, BR/EDR not supported
        var shorts = ad.Services.Select(BleUuid.ToShort).OfType<ushort>().ToList();
        if (shorts.Count > 0) Add(0x03, [.. shorts.SelectMany(s => new[] { (byte)s, (byte)(s >> 8) })]);
        if (ad.TxPower is { } tx) Add(0x0A, [unchecked((byte)(sbyte)tx)]);
        foreach (var (uuid, data) in ad.ServiceData)
            if (BleUuid.ToShort(uuid) is { } s) Add(0x16, [(byte)s, (byte)(s >> 8), .. data]);
        foreach (var (company, data) in ad.ManufacturerData) Add(0xFF, [(byte)company, (byte)(company >> 8), .. data]);
        if (ad.Name is { } name) Add(0x09, Encoding.UTF8.GetBytes(name));
        return [.. b];
    }

    private static Guid FromLittleEndian(ReadOnlySpan<byte> le)
    {
        Span<byte> be = stackalloc byte[16];
        for (var i = 0; i < 16; i++) be[i] = le[15 - i];
        return new Guid(be, bigEndian: true);
    }

    /// <summary>Frame-lane fields: one length/type header and one value per AD structure.</summary>
    public static IReadOnlyList<FrameField> Describe(ReadOnlySpan<byte> data)
    {
        var fields = new List<FrameField>();
        var pos = 0;
        while (pos < data.Length && data[pos] != 0)
        {
            int len = data[pos];
            if (pos + 1 + len > data.Length)
            {
                fields.Add(new FrameField("Invalid", pos, data.Length - pos, FrameFieldKind.Error, "length past the end"));
                break;
            }

            var type = data[pos + 1];
            fields.Add(new FrameField("Len", pos, 1, FrameFieldKind.Length, len.ToString(CultureInfo.InvariantCulture)));
            fields.Add(new FrameField("Type", pos + 1, 1, FrameFieldKind.Function, type switch
            {
                0x01 => "Flags", 0x02 or 0x03 => "16-bit services", 0x06 or 0x07 => "128-bit services", 0x08 or 0x09 => "Name", 0x0A => "TX power",
                0x16 => "Service data", 0xFF => "Manufacturer data", _ => $"0x{type:X2}",
            }));
            if (len > 1) fields.Add(new FrameField("Value", pos + 2, len - 1, FrameFieldKind.Data, Convert.ToHexString(data.Slice(pos + 2, len - 1))));
            pos += 1 + len;
        }

        return fields;
    }
}

/// <summary>An Apple iBeacon frame (manufacturer data of company 0x004C, type 0x02, length 0x15).</summary>
/// <param name="ProximityUuid">Proximity UUID.</param>
/// <param name="Major">Major.</param>
/// <param name="Minor">Minor.</param>
/// <param name="MeasuredPower">Calibrated RSSI at 1 m (dBm).</param>
public sealed record IBeacon(Guid ProximityUuid, ushort Major, ushort Minor, int MeasuredPower)
{
    /// <summary>Parses manufacturer data (after the company id).</summary>
    public static IBeacon? TryParse(ReadOnlySpan<byte> data) =>
        data.Length >= 23 && data[0] == 0x02 && data[1] == 0x15
            ? new IBeacon(new Guid(data.Slice(2, 16), bigEndian: true), BinaryPrimitives.ReadUInt16BigEndian(data[18..]), BinaryPrimitives.ReadUInt16BigEndian(data[20..]), unchecked((sbyte)data[22]))
            : null;

    /// <summary>Manufacturer data (without the company id).</summary>
    public byte[] Encode()
    {
        var b = new byte[23];
        b[0] = 0x02;
        b[1] = 0x15;
        ProximityUuid.TryWriteBytes(b.AsSpan(2, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(18), Major);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(20), Minor);
        b[22] = unchecked((byte)(sbyte)MeasuredPower);
        return b;
    }
}

/// <summary>An Eddystone frame (service data of 0xFEAA): UID, URL or TLM.</summary>
/// <param name="Kind">UID, URL or TLM.</param>
/// <param name="Description">Decoded content (namespace/instance, URL, or telemetry).</param>
/// <param name="TxPower">Calibrated TX power at 0 m (UID/URL).</param>
public sealed record EddystoneFrame(string Kind, string Description, int? TxPower)
{
    private static readonly string[] Schemes = ["http://www.", "https://www.", "http://", "https://"];
    private static readonly string[] Expansions = [".com/", ".org/", ".edu/", ".net/", ".info/", ".biz/", ".gov/", ".com", ".org", ".edu", ".net", ".info", ".biz", ".gov"];

    /// <summary>Parses service data (after the UUID).</summary>
    public static EddystoneFrame? TryParse(ReadOnlySpan<byte> d)
    {
        if (d.Length < 2) return null;
        switch (d[0])
        {
            case 0x00 when d.Length >= 18:
                return new EddystoneFrame("UID", $"{Convert.ToHexString(d.Slice(2, 10))}/{Convert.ToHexString(d.Slice(12, 6))}", unchecked((sbyte)d[1]));
            case 0x10 when d.Length >= 3 && d[2] < Schemes.Length:
                var url = new StringBuilder(Schemes[d[2]]);
                foreach (var c in d[3..]) url.Append(c < Expansions.Length ? Expansions[c] : ((char)c).ToString());
                return new EddystoneFrame("URL", url.ToString(), unchecked((sbyte)d[1]));
            case 0x20 when d.Length >= 14:
                var volts = BinaryPrimitives.ReadUInt16BigEndian(d[2..]);
                var temp = BinaryPrimitives.ReadInt16BigEndian(d[4..]) / 256.0;
                var count = BinaryPrimitives.ReadUInt32BigEndian(d[6..]);
                return new EddystoneFrame("TLM", string.Create(CultureInfo.InvariantCulture, $"{volts} mV, {temp:0.0} °C, {count} adv"), null);
            default:
                return null;
        }
    }
}

/// <summary>Decoders for standard GATT characteristic values.</summary>
public static class GattValue
{
    /// <summary>Heart Rate Measurement (0x2A37).</summary>
    /// <param name="BeatsPerMinute">Heart rate.</param>
    /// <param name="SensorContact">Contact detected (null when not supported).</param>
    /// <param name="EnergyExpended">kJ, when present.</param>
    /// <param name="RrIntervals">RR intervals in seconds.</param>
    public sealed record HeartRate(int BeatsPerMinute, bool? SensorContact, int? EnergyExpended, IReadOnlyList<double> RrIntervals);

    /// <summary>Decodes a Heart Rate Measurement.</summary>
    public static HeartRate ParseHeartRate(ReadOnlySpan<byte> v)
    {
        if (v.Length < 2) throw new FormatException("Heart Rate Measurement needs at least 2 bytes.");
        var flags = v[0];
        var pos = 1;
        int bpm;
        if ((flags & 0x01) != 0)
        {
            bpm = BinaryPrimitives.ReadUInt16LittleEndian(v[pos..]);
            pos += 2;
        }
        else
        {
            bpm = v[pos++];
        }

        bool? contact = (flags & 0x04) != 0 ? (flags & 0x02) != 0 : null;
        int? energy = null;
        if ((flags & 0x08) != 0 && pos + 1 < v.Length)
        {
            energy = BinaryPrimitives.ReadUInt16LittleEndian(v[pos..]);
            pos += 2;
        }

        var rr = new List<double>();
        if ((flags & 0x10) != 0)
            for (; pos + 1 < v.Length; pos += 2) rr.Add(Math.Round(BinaryPrimitives.ReadUInt16LittleEndian(v[pos..]) / 1024.0, 3, MidpointRounding.AwayFromZero));
        return new HeartRate(bpm, contact, energy, rr);
    }

    /// <summary>Encodes a Heart Rate Measurement (8-bit value, contact supported, optional RR intervals).</summary>
    public static byte[] EncodeHeartRate(int bpm, bool contact, IReadOnlyList<double>? rr = null)
    {
        var b = new List<byte> { (byte)(0x04 | (contact ? 0x02 : 0) | (rr is { Count: > 0 } ? 0x10 : 0) | (bpm > 255 ? 0x01 : 0)) };
        if (bpm > 255) b.AddRange([(byte)bpm, (byte)(bpm >> 8)]);
        else b.Add((byte)bpm);
        foreach (var r in rr ?? [])
        {
            var raw = (ushort)Math.Round(r * 1024);
            b.AddRange([(byte)raw, (byte)(raw >> 8)]);
        }

        return [.. b];
    }

    /// <summary>A readable rendering of a value for well-known characteristics; hex otherwise.</summary>
    public static string Describe(Guid characteristic, ReadOnlySpan<byte> v)
    {
        try
        {
            return BleUuid.ToShort(characteristic) switch
            {
                0x2A19 when v.Length >= 1 => $"{v[0]} %",
                0x2A37 => ParseHeartRate(v) is var h ? $"{h.BeatsPerMinute} bpm{(h.RrIntervals.Count > 0 ? $", RR {string.Join("/", h.RrIntervals.Select(r => r.ToString("0.000", CultureInfo.InvariantCulture)))} s" : "")}" : "",
                0x2A6E when v.Length >= 2 => (BinaryPrimitives.ReadInt16LittleEndian(v) / 100.0).ToString("0.00 °C", CultureInfo.InvariantCulture),
                0x2A6F when v.Length >= 2 => (BinaryPrimitives.ReadUInt16LittleEndian(v) / 100.0).ToString("0.00 '%'", CultureInfo.InvariantCulture),
                0x2A6D when v.Length >= 4 => (BinaryPrimitives.ReadUInt32LittleEndian(v) / 1000.0).ToString("0.0 hPa", CultureInfo.InvariantCulture),
                0x2A00 or 0x2A24 or 0x2A25 or 0x2A26 or 0x2A29 => Encoding.UTF8.GetString(v),
                0x2A38 when v.Length >= 1 => v[0] switch { 0 => "other", 1 => "chest", 2 => "wrist", 3 => "finger", 4 => "hand", 5 => "ear lobe", 6 => "foot", _ => v[0].ToString(CultureInfo.InvariantCulture) },
                _ => Convert.ToHexString(v),
            };
        }
        catch (FormatException)
        {
            return Convert.ToHexString(v);
        }
    }
}
