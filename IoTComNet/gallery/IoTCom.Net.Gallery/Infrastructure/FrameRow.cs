using System.Globalization;
using Avalonia.Media;
using IoTCom.Net.Protocols.Modbus;

namespace IoTCom.Net.Gallery.Infrastructure;

/// <summary>A field of the frame lane: a run of byte tiles in the field's colour.</summary>
public sealed record FieldTiles(string Name, string? Value, IBrush Background, IBrush Foreground, IReadOnlyList<string> Bytes)
{
    public string Tooltip => Value is null ? Name : $"{Name}: {Value}";
}

/// <summary>One captured frame shown in the Traffic tab and status bar.</summary>
public sealed record FrameRow(string Time, string Direction, bool Outbound, string Protocol, string Summary, IReadOnlyList<FieldTiles> Fields, string HexDump)
{
    public IBrush DirectionBrush => Outbound ? Palette.Amber : Palette.Blue;

    public static FrameRow From(TrafficFrame f)
    {
        var data = f.Data.Span;
        var outbound = f.Direction == FrameDirection.Outbound;
        var fields = f.Protocol switch
        {
            "modbus-tcp" or "modbus-tcp-native" => ModbusAnatomy.Describe(data, ModbusFramingMode.Tcp, outbound),
            "modbus-rtu" or "modbus-rtu-native" => ModbusAnatomy.Describe(data, ModbusFramingMode.Rtu, outbound),
            "artnet" => ArtNetFields(data),
            "nmea0183" => NmeaFields(data),
            "can" or "can-slcan" => CanFields(data),
            "coap" => Protocols.Coap.CoapAnatomy.Describe(data),
            "mavlink" => Protocols.Mavlink.MavlinkAnatomy.Describe(data, Protocols.Mavlink.Common.CommonDialect.Instance),
            "uds" or "uds-ecu" or "obd2" => Protocols.Uds.UdsAnatomy.Describe(data),
            "lorawan" or "semtech-udp" => Protocols.LoRaWan.LoRaWanAnatomy.Describe(data),
            "dlms" => Protocols.Dlms.DlmsAnatomy.Describe(data),
            "mbus" => Protocols.MBus.MBusAnatomy.Describe(data),
            "iec104" => Protocols.Iec104.Iec104Apdu.Describe(data),
            "ntp" => Protocols.Ntp.NtpPacket.Describe(data),
            "ndef" => Protocols.Nfc.NdefMessage.Describe(data),
            _ => [new FrameField("Payload", 0, data.Length, FrameFieldKind.Data)],
        };
        return new FrameRow(
            f.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
            outbound ? "TX" : "RX", outbound, f.Protocol, f.Summary ?? f.Protocol,
            Tiles(data, fields, ascii: f.Protocol == "nmea0183"),
            IoTCom.Net.HexDump.Format(data));
    }

    /// <summary>SocketCAN layout used by the CAN traffic tap: identifier, length, flags, reserved, data.</summary>
    private static List<FrameField> CanFields(ReadOnlySpan<byte> d)
    {
        if (d.Length < 8) return [new FrameField("Payload", 0, d.Length, FrameFieldKind.Data)];
        var raw = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(d);
        var id = (raw & 0x8000_0000) != 0 ? $"{raw & 0x1FFF_FFFF:X8}" : $"{raw & 0x7FF:X3}";
        return
        [
            new FrameField("ID", 0, 4, FrameFieldKind.Address, id),
            new FrameField("Len", 4, 1, FrameFieldKind.Length, d[4].ToString(CultureInfo.InvariantCulture)),
            new FrameField("Flags", 5, 3, FrameFieldKind.Header, (d[5] & 4) != 0 ? "CAN FD" : "classic"),
            new FrameField("Data", 8, d.Length - 8, FrameFieldKind.Data),
        ];
    }

    internal static List<FieldTiles> Tiles(ReadOnlySpan<byte> data, IReadOnlyList<FrameField> fields, bool ascii)
    {
        const int maxBytes = 72;
        var list = new List<FieldTiles>();
        var cursor = 0;
        void Add(string name, string? value, FrameFieldKind kind, ReadOnlySpan<byte> bytes)
        {
            if (bytes.IsEmpty) return;
            var shown = new List<string>(bytes.Length);
            foreach (var b in bytes) shown.Add(ascii ? (b is >= 0x20 and < 0x7F ? ((char)b).ToString() : "·") : b.ToString("X2", CultureInfo.InvariantCulture));
            var (bg, fg) = Palette.ForKind(kind);
            list.Add(new FieldTiles(name, value, bg, fg, shown));
        }
        foreach (var f in fields)
        {
            if (f.Offset >= maxBytes) break;
            if (f.Offset > cursor) Add("", null, FrameFieldKind.Header, data[cursor..Math.Min(f.Offset, data.Length)]);
            var end = Math.Min(Math.Min(f.Offset + f.Length, data.Length), maxBytes);
            Add(f.Name, f.Value, f.Kind, data[Math.Min(f.Offset, end)..end]);
            cursor = f.Offset + f.Length;
        }
        if (cursor < Math.Min(data.Length, maxBytes)) Add("", null, FrameFieldKind.Data, data[cursor..Math.Min(data.Length, maxBytes)]);
        return list;
    }

    private static List<FrameField> ArtNetFields(ReadOnlySpan<byte> d)
    {
        if (d.Length < 18 || Protocols.Dmx.ArtNetPacket.GetOpCode(d) != Protocols.Dmx.ArtNetOpCode.Dmx)
            return [new FrameField("Art-Net", 0, Math.Min(d.Length, 14), FrameFieldKind.Header), new FrameField("Body", 14, Math.Max(0, d.Length - 14), FrameFieldKind.Data)];
        return
        [
            new FrameField("ID", 0, 8, FrameFieldKind.Header, "Art-Net"),
            new FrameField("OpCode", 8, 2, FrameFieldKind.Function, "ArtDmx"),
            new FrameField("Version", 10, 2, FrameFieldKind.Header, "14"),
            new FrameField("Sequence", 12, 1, FrameFieldKind.Length, d[12].ToString(CultureInfo.InvariantCulture)),
            new FrameField("Physical", 13, 1, FrameFieldKind.Header),
            new FrameField("Universe", 14, 2, FrameFieldKind.Address, (d[14] | (d[15] << 8)).ToString(CultureInfo.InvariantCulture)),
            new FrameField("Length", 16, 2, FrameFieldKind.Length, ((d[16] << 8) | d[17]).ToString(CultureInfo.InvariantCulture)),
            new FrameField("DMX", 18, d.Length - 18, FrameFieldKind.Data),
        ];
    }

    private static List<FrameField> NmeaFields(ReadOnlySpan<byte> d)
    {
        var star = d.LastIndexOf((byte)'*');
        var comma = d.IndexOf((byte)',');
        if (d.Length < 7 || comma < 0) return [new FrameField("Sentence", 0, d.Length, FrameFieldKind.Data)];
        var list = new List<FrameField>
        {
            new("Start", 0, 1, FrameFieldKind.Delimiter),
            new("Talker", 1, 2, FrameFieldKind.Address),
            new("Type", 3, comma - 3, FrameFieldKind.Function),
        };
        var dataEnd = star > comma ? star : d.Length;
        list.Add(new FrameField("Fields", comma, dataEnd - comma, FrameFieldKind.Data));
        if (star > comma) list.Add(new FrameField("Checksum", star, d.Length - star, FrameFieldKind.Checksum));
        return list;
    }
}

/// <summary>Signal-lamp palette shared by the lane, status bar and demos.</summary>
public static class Palette
{
    public static readonly IBrush Amber = new SolidColorBrush(Color.Parse("#F2A900"));
    public static readonly IBrush Blue = new SolidColorBrush(Color.Parse("#2F6FD6"));
    public static readonly IBrush Green = new SolidColorBrush(Color.Parse("#2E9E5B"));
    public static readonly IBrush Red = new SolidColorBrush(Color.Parse("#D23B2F"));
    public static readonly IBrush Violet = new SolidColorBrush(Color.Parse("#7A5BB5"));
    public static readonly IBrush Grey = new SolidColorBrush(Color.Parse("#8A9098"));
    public static readonly IBrush Data = new SolidColorBrush(Color.Parse("#C9CBC4"));
    public static readonly IBrush Ink = new SolidColorBrush(Color.Parse("#2B3036"));
    public static readonly IBrush White = Brushes.White;

    public static (IBrush Background, IBrush Foreground) ForKind(FrameFieldKind kind) => kind switch
    {
        FrameFieldKind.Address => (Blue, White),
        FrameFieldKind.Function => (Amber, Ink),
        FrameFieldKind.Length => (Violet, White),
        FrameFieldKind.Checksum => (Green, White),
        FrameFieldKind.Error => (Red, White),
        FrameFieldKind.Header or FrameFieldKind.Delimiter => (Grey, White),
        _ => (Data, Ink),
    };
}
