using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace IoTCom.Net.Protocols.MBus;

/// <summary>What a record value means (function field of the DIF).</summary>
public enum MBusFunction
{
    /// <summary>Instantaneous value.</summary>
    Instantaneous = 0,
    /// <summary>Maximum value.</summary>
    Maximum = 1,
    /// <summary>Minimum value.</summary>
    Minimum = 2,
    /// <summary>Value during an error state.</summary>
    Error = 3,
}

/// <summary>One data record: DIF/DIFE, VIF/VIFE and the value.</summary>
public sealed record MBusRecord
{
    /// <summary>DIF.</summary>
    public byte Dif { get; init; }

    /// <summary>DIFEs.</summary>
    public byte[] Dife { get; init; } = [];

    /// <summary>VIF (0 when absent).</summary>
    public byte Vif { get; init; }

    /// <summary>VIFEs (including the extension-table byte after 0xFB/0xFD).</summary>
    public byte[] Vife { get; init; } = [];

    /// <summary>Raw data bytes.</summary>
    public byte[] Raw { get; init; } = [];

    /// <summary>Function.</summary>
    public MBusFunction Function => (MBusFunction)((Dif >> 4) & 3);

    /// <summary>Storage number (0 = current, 1… = historic).</summary>
    public long StorageNumber { get; init; }

    /// <summary>Tariff.</summary>
    public int Tariff { get; init; }

    /// <summary>Sub-unit.</summary>
    public int SubUnit { get; init; }

    /// <summary>Quantity, e.g. "Energy", "Volume", "Flow temperature".</summary>
    public string Quantity { get; init; } = "";

    /// <summary>Unit symbol.</summary>
    public string Unit { get; init; } = "";

    /// <summary>Numeric value (already multiplied by the VIF's power of ten), if numeric.</summary>
    public double? Value { get; init; }

    /// <summary>Date or date-time value (VIF time point).</summary>
    public DateTime? Time { get; init; }

    /// <summary>Text value (plain text VIF, variable-length strings).</summary>
    public string? Text { get; init; }

    /// <summary>Offset of the DIF inside the user data (for the frame lane).</summary>
    public int Offset { get; init; }

    /// <summary>Total length of the record in bytes.</summary>
    public int Length { get; init; }

    /// <summary>Formatted value with unit.</summary>
    public string FormattedValue => Time is { } t ? t.ToString(t.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
        : Text ?? (Value is { } v ? $"{v.ToString("0.######", CultureInfo.InvariantCulture)}{(Unit.Length > 0 ? " " + Unit : "")}" : Convert.ToHexString(Raw));

    /// <inheritdoc />
    public override string ToString()
    {
        var tags = new List<string>();
        if (Function != MBusFunction.Instantaneous) tags.Add(Function.ToString().ToLowerInvariant());
        if (StorageNumber > 0) tags.Add($"storage {StorageNumber}");
        if (Tariff > 0) tags.Add($"tariff {Tariff}");
        if (SubUnit > 0) tags.Add($"subunit {SubUnit}");
        return $"{Quantity}{(tags.Count > 0 ? $" ({string.Join(", ", tags)})" : "")}: {FormattedValue}";
    }
}

/// <summary>
/// A variable data structure response (CI 0x72): identification number, manufacturer, version, medium, access
/// number, status, and the data records (EN 13757-3).
/// </summary>
public sealed record MBusTelegram
{
    /// <summary>Primary address of the slave that answered.</summary>
    public byte Address { get; init; }

    /// <summary>Identification number (8 BCD digits).</summary>
    public uint Id { get; init; }

    /// <summary>Manufacturer code (FLAG association, 3 letters).</summary>
    public string Manufacturer { get; init; } = "";

    /// <summary>Version (generation).</summary>
    public byte Version { get; init; }

    /// <summary>Medium (device type).</summary>
    public byte Medium { get; init; }

    /// <summary>Access number (increments per read).</summary>
    public byte AccessNumber { get; init; }

    /// <summary>Status byte (bit 0 application busy, 1 application error, 2 power low, 3 permanent error, 4 temporary error).</summary>
    public byte Status { get; init; }

    /// <summary>Records.</summary>
    public IReadOnlyList<MBusRecord> Records { get; init; } = [];

    /// <summary>Manufacturer-specific data after DIF 0x0F/0x1F.</summary>
    public byte[] ManufacturerData { get; init; } = [];

    /// <summary>More records follow in another telegram (DIF 0x1F).</summary>
    public bool MoreRecordsFollow { get; init; }

    /// <summary>Medium name.</summary>
    public string MediumName => MBusMedium.Name(Medium);

    /// <summary>The secondary address (ID, manufacturer, version, medium) as written on the label.</summary>
    public string SecondaryAddress => $"{Id:X8}-{Manufacturer}-{Version:X2}-{Medium:X2}";

    /// <summary>Parses the user data of a CI 0x72 response.</summary>
    public static bool TryParse(ReadOnlySpan<byte> userData, out MBusTelegram? telegram, out string? error, byte address = 0)
    {
        (telegram, error) = (null, null);
        if (userData.Length < 12)
        {
            error = "A variable data structure has a 12-byte header.";
            return false;
        }

        try
        {
            var (records, manufacturerData, more) = ParseRecords(userData[12..], 12);
            telegram = new MBusTelegram
            {
                Address = address,
                Id = BinaryPrimitives.ReadUInt32LittleEndian(userData),
                Manufacturer = ManufacturerCode(BinaryPrimitives.ReadUInt16LittleEndian(userData[4..])),
                Version = userData[6],
                Medium = userData[7],
                AccessNumber = userData[8],
                Status = userData[9],
                Records = records,
                ManufacturerData = manufacturerData,
                MoreRecordsFollow = more,
            };
            return true;
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Parses a long frame carrying CI 0x72.</summary>
    public static MBusTelegram FromFrame(MBusFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Ci != MBusCi.VariableLong) throw new FormatException($"CI 0x{frame.Ci:X2} is not a variable data response (0x72).");
        return TryParse(frame.UserData, out var t, out var e, frame.Address) ? t! : throw new FormatException(e);
    }

    /// <summary>The 3-letter manufacturer code of a 16-bit FLAG id.</summary>
    public static string ManufacturerCode(ushort m) =>
        new([(char)(((m >> 10) & 0x1F) + 64), (char)(((m >> 5) & 0x1F) + 64), (char)((m & 0x1F) + 64)]);

    /// <summary>The 16-bit FLAG id of a 3-letter code.</summary>
    public static ushort ManufacturerId(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        if (code.Length != 3 || !code.All(c => c is >= 'A' and <= 'Z')) throw new ArgumentException("A manufacturer code is three letters A–Z.", nameof(code));
        return (ushort)(((code[0] - 64) << 10) | ((code[1] - 64) << 5) | (code[2] - 64));
    }

    /// <summary>Parses data records (stops at 0x0F/0x1F manufacturer data; skips 0x2F fillers).</summary>
    public static (IReadOnlyList<MBusRecord> Records, byte[] ManufacturerData, bool More) ParseRecords(ReadOnlySpan<byte> d, int baseOffset = 0)
    {
        var records = new List<MBusRecord>();
        var pos = 0;
        while (pos < d.Length)
        {
            var start = pos;
            var dif = d[pos++];
            if (dif == 0x2F) continue;
            if ((dif & 0x0F) == 0x0F) return (records, d[pos..].ToArray(), dif == 0x1F);
            var dife = new List<byte>();
            var last = dif;
            while ((last & 0x80) != 0)
            {
                if (pos >= d.Length) throw new FormatException("Truncated DIFE.");
                last = d[pos++];
                dife.Add(last);
                if (dife.Count > 10) throw new FormatException("More than 10 DIFEs.");
            }

            long storage = (dif >> 6) & 1;
            int tariff = 0, subunit = 0;
            for (var i = 0; i < dife.Count; i++)
            {
                storage |= (long)(dife[i] & 0x0F) << (1 + (4 * i));
                tariff |= ((dife[i] >> 4) & 3) << (2 * i);
                subunit |= ((dife[i] >> 6) & 1) << i;
            }

            if (pos >= d.Length) throw new FormatException("Record without a VIF.");
            var vif = d[pos++];
            var vife = new List<byte>();
            string? plainUnit = null;
            last = vif;
            while ((last & 0x80) != 0)
            {
                if (pos >= d.Length) throw new FormatException("Truncated VIFE.");
                last = d[pos++];
                vife.Add(last);
                if (vife.Count > 10) throw new FormatException("More than 10 VIFEs.");
            }

            if ((vif & 0x7F) == 0x7C)
            {
                if (pos >= d.Length) throw new FormatException("Truncated plain-text unit.");
                var n = d[pos++];
                if (pos + n > d.Length) throw new FormatException("Truncated plain-text unit.");
                var chars = d.Slice(pos, n).ToArray();
                Array.Reverse(chars);
                plainUnit = Encoding.ASCII.GetString(chars);
                pos += n;
            }

            var size = (dif & 0x0F) switch
            {
                0 or 8 => 0,
                1 => 1, 2 => 2, 3 => 3, 4 => 4, 5 => 4, 6 => 6, 7 => 8,
                9 => 1, 0xA => 2, 0xB => 3, 0xC => 4, 0xE => 6,
                0xD => -1,
                _ => throw new FormatException($"Unsupported data field 0x{dif & 0x0F:X}."),
            };
            var textValue = false;
            if (size < 0)
            {
                if (pos >= d.Length) throw new FormatException("Truncated LVAR.");
                var lvar = d[pos++];
                if (lvar <= 0xBF) (size, textValue) = (lvar, true);
                else if (lvar <= 0xCF) size = lvar - 0xC0;
                else if (lvar <= 0xDF) size = lvar - 0xD0;
                else throw new FormatException($"Unsupported LVAR 0x{lvar:X2}.");
            }

            if (pos + size > d.Length) throw new FormatException("Truncated record value.");
            var raw = d.Slice(pos, size).ToArray();
            pos += size;
            var (quantity, unit, exponent, kind) = MBusVif.Describe(vif, [.. vife]);
            if (plainUnit is not null) (quantity, unit) = ("Value", plainUnit);
            double? value = null;
            DateTime? time = null;
            string? text = null;
            var field = dif & 0x0F;
            if (textValue)
            {
                var chars = raw.ToArray();
                Array.Reverse(chars);
                text = Encoding.ASCII.GetString(chars);
            }
            else if (kind == VifKind.Date && field == 2)
            {
                time = TypeG(raw);
            }
            else if (kind == VifKind.DateTime && field == 4)
            {
                time = TypeF(raw);
            }
            else if (field is 9 or 0xA or 0xB or 0xC or 0xE)
            {
                value = Bcd(raw) * Math.Pow(10, exponent);
            }
            else if (field == 5)
            {
                value = BinaryPrimitives.ReadSingleLittleEndian(raw) * Math.Pow(10, exponent);
            }
            else if (size > 0)
            {
                value = SignedLittleEndian(raw) * Math.Pow(10, exponent);
                if (kind == VifKind.Identifier) (value, text) = (null, Bcd(raw).ToString(CultureInfo.InvariantCulture));
            }

            records.Add(new MBusRecord
            {
                Dif = dif, Dife = [.. dife], Vif = vif, Vife = [.. vife], Raw = raw, StorageNumber = storage, Tariff = tariff, SubUnit = subunit,
                Quantity = quantity, Unit = unit, Value = value, Time = time, Text = text, Offset = baseOffset + start, Length = pos - start,
            });
        }

        return (records, [], false);
    }

    private static long SignedLittleEndian(ReadOnlySpan<byte> b)
    {
        long v = 0;
        for (var i = b.Length - 1; i >= 0; i--) v = (v << 8) | b[i];
        var bits = 8 * b.Length;
        if (bits < 64 && (v & (1L << (bits - 1))) != 0) v -= 1L << bits;
        return v;
    }

    private static long Bcd(ReadOnlySpan<byte> b)
    {
        long v = 0;
        var negative = false;
        for (var i = b.Length - 1; i >= 0; i--)
        {
            var hi = b[i] >> 4;
            var lo = b[i] & 0xF;
            if (i == b.Length - 1 && hi == 0xF)
            {
                negative = true;
                hi = 0;
            }

            v = (v * 100) + (Math.Min(hi, 9) * 10) + Math.Min(lo, 9);
        }

        return negative ? -v : v;
    }

    /// <summary>Type G date (2 bytes).</summary>
    public static DateTime? TypeG(ReadOnlySpan<byte> b)
    {
        if (b.Length < 2) return null;
        var day = b[0] & 0x1F;
        var month = b[1] & 0x0F;
        var year = 2000 + (((b[1] & 0xF0) >> 1) | ((b[0] & 0xE0) >> 5));
        return day is >= 1 and <= 31 && month is >= 1 and <= 12 && day <= DateTime.DaysInMonth(year, month) ? new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Unspecified) : null;
    }

    /// <summary>Type F date and time (4 bytes).</summary>
    public static DateTime? TypeF(ReadOnlySpan<byte> b)
    {
        if (b.Length < 4 || (b[0] & 0x80) != 0) return null;
        var minute = b[0] & 0x3F;
        var hour = b[1] & 0x1F;
        var day = b[2] & 0x1F;
        var month = b[3] & 0x0F;
        var year = 2000 + (((b[3] & 0xF0) >> 1) | ((b[2] & 0xE0) >> 5));
        return minute < 60 && hour < 24 && day is >= 1 and <= 31 && month is >= 1 and <= 12 && day <= DateTime.DaysInMonth(year, month)
            ? new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified) : null;
    }

    /// <summary>Encodes a type G date.</summary>
    public static byte[] EncodeTypeG(DateTime d)
    {
        var y = d.Year - 2000;
        return [(byte)((d.Day & 0x1F) | ((y & 0x07) << 5)), (byte)((d.Month & 0x0F) | ((y & 0x78) << 1))];
    }

    /// <summary>Encodes a type F date-time.</summary>
    public static byte[] EncodeTypeF(DateTime d)
    {
        var y = d.Year - 2000;
        return [(byte)d.Minute, (byte)d.Hour, (byte)((d.Day & 0x1F) | ((y & 0x07) << 5)), (byte)((d.Month & 0x0F) | ((y & 0x78) << 1))];
    }

    /// <inheritdoc />
    public override string ToString() => $"{SecondaryAddress} {MediumName}, {Records.Count} records, access {AccessNumber}";
}

internal enum VifKind
{
    Numeric,
    Date,
    DateTime,
    Identifier,
}

/// <summary>Value information field tables (EN 13757-3, primary VIF and the 0xFD extension).</summary>
public static class MBusVif
{
    /// <summary>Quantity, unit and power of ten of a VIF.</summary>
    public static (string Quantity, string Unit, int Exponent) Lookup(byte vif, ReadOnlySpan<byte> vife)
    {
        var (q, u, e, _) = Describe(vif, vife.ToArray());
        return (q, u, e);
    }

    internal static (string Quantity, string Unit, int Exponent, VifKind Kind) Describe(byte vif, byte[] vife)
    {
        var v = vif & 0x7F;
        if (vif == 0xFD && vife.Length > 0) return Fd(vife[0] & 0x7F);
        if (vif == 0xFB && vife.Length > 0) return ("Extension FB", "", 0, VifKind.Numeric);
        int n3 = v & 7, n2 = v & 3;
        (string, string, int, VifKind) N(string q, string u, int e) => (q, u, e, VifKind.Numeric);
        string TimeUnit(int nn) => nn switch { 0 => "s", 1 => "min", 2 => "h", _ => "d" };
        return v switch
        {
            <= 0x07 => N("Energy", "Wh", n3 - 3),
            <= 0x0F => N("Energy", "J", n3),
            <= 0x17 => N("Volume", "m³", n3 - 6),
            <= 0x1F => N("Mass", "kg", n3 - 3),
            <= 0x23 => N("On time", TimeUnit(n2), 0),
            <= 0x27 => N("Operating time", TimeUnit(n2), 0),
            <= 0x2F => N("Power", "W", n3 - 3),
            <= 0x37 => N("Power", "J/h", n3),
            <= 0x3F => N("Volume flow", "m³/h", n3 - 6),
            <= 0x47 => N("Volume flow", "m³/min", n3 - 7),
            <= 0x4F => N("Volume flow", "m³/s", n3 - 9),
            <= 0x57 => N("Mass flow", "kg/h", n3 - 3),
            <= 0x5B => N("Flow temperature", "°C", n2 - 3),
            <= 0x5F => N("Return temperature", "°C", n2 - 3),
            <= 0x63 => N("Temperature difference", "K", n2 - 3),
            <= 0x67 => N("External temperature", "°C", n2 - 3),
            <= 0x6B => N("Pressure", "bar", n2 - 3),
            0x6C => ("Date", "", 0, VifKind.Date),
            0x6D => ("Date and time", "", 0, VifKind.DateTime),
            0x6E => N("Heat cost allocation", "HCA", 0),
            <= 0x73 => N("Averaging duration", TimeUnit(n2), 0),
            <= 0x77 => N("Actuality duration", TimeUnit(n2), 0),
            0x78 => ("Fabrication number", "", 0, VifKind.Identifier),
            0x79 => ("Enhanced identification", "", 0, VifKind.Identifier),
            0x7A => N("Bus address", "", 0),
            0x7E => N("Any VIF", "", 0),
            0x7F => N("Manufacturer specific", "", 0),
            _ => N($"VIF 0x{vif:X2}", "", 0),
        };
    }

    private static (string, string, int, VifKind) Fd(int e) => e switch
    {
        0x0A => ("Manufacturer", "", 0, VifKind.Numeric),
        0x0C => ("Version", "", 0, VifKind.Numeric),
        0x0E => ("Firmware version", "", 0, VifKind.Numeric),
        0x0F => ("Software version", "", 0, VifKind.Numeric),
        0x17 => ("Error flags", "", 0, VifKind.Numeric),
        >= 0x40 and <= 0x4F => ("Voltage", "V", (e & 0x0F) - 9, VifKind.Numeric),
        >= 0x50 and <= 0x5F => ("Current", "A", (e & 0x0F) - 12, VifKind.Numeric),
        0x74 => ("Remaining battery", "d", 0, VifKind.Numeric),
        _ => ($"VIF FD 0x{e:X2}", "", 0, VifKind.Numeric),
    };
}

/// <summary>Medium (device type) codes.</summary>
public static class MBusMedium
{
    /// <summary>Electricity.</summary>
    public const byte Electricity = 0x02;
    /// <summary>Gas.</summary>
    public const byte Gas = 0x03;
    /// <summary>Heat (outlet).</summary>
    public const byte Heat = 0x04;
    /// <summary>Warm water.</summary>
    public const byte WarmWater = 0x06;
    /// <summary>Water.</summary>
    public const byte Water = 0x07;

    /// <summary>Name of a medium code.</summary>
    public static string Name(byte medium) => medium switch
    {
        0x00 => "Other", 0x01 => "Oil", 0x02 => "Electricity", 0x03 => "Gas", 0x04 => "Heat (outlet)", 0x05 => "Steam",
        0x06 => "Warm water", 0x07 => "Water", 0x08 => "Heat cost allocator", 0x09 => "Compressed air", 0x0A => "Cooling (outlet)",
        0x0B => "Cooling (inlet)", 0x0C => "Heat (inlet)", 0x0D => "Heat / cooling", 0x0E => "Bus / system", 0x0F => "Unknown",
        0x15 => "Hot water", 0x16 => "Cold water", 0x17 => "Dual water", 0x18 => "Pressure", 0x19 => "A/D converter",
        _ => $"Medium 0x{medium:X2}",
    };
}
