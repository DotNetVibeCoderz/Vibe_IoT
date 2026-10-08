using System.Buffers.Binary;
using System.Globalization;
using IoTCom.Net.Transport.Can;

namespace IoTCom.Net.Protocols.J1939;

/// <summary>A 29-bit J1939 identifier: priority, parameter group number, destination and source address.</summary>
/// <param name="Priority">0 (highest) – 7.</param>
/// <param name="Pgn">Parameter group number (18 bits; PDU1 PGNs have a zero low byte).</param>
/// <param name="Destination">Destination address (0xFF = global; PDU2 PGNs are always global).</param>
/// <param name="Source">Source address.</param>
public readonly record struct J1939Id(byte Priority, uint Pgn, byte Destination, byte Source)
{
    /// <summary>Global (broadcast) address.</summary>
    public const byte Global = 0xFF;

    /// <summary>Null address (used by a node that could not claim one).</summary>
    public const byte Null = 0xFE;

    /// <summary>PDU format byte.</summary>
    public byte PduFormat => (byte)(Pgn >> 8);

    /// <summary>PDU1 (destination specific) when the PDU format is below 240.</summary>
    public bool IsPdu1 => PduFormat < 240;

    /// <summary>The 29-bit CAN identifier.</summary>
    public uint ToCanId()
    {
        var ps = IsPdu1 ? Destination : (byte)Pgn;
        return ((uint)(Priority & 7) << 26) | ((Pgn & 0x3FF00) << 8) | ((uint)ps << 8) | Source;
    }

    /// <summary>Parses a 29-bit CAN identifier.</summary>
    public static J1939Id FromCanId(uint id)
    {
        var priority = (byte)((id >> 26) & 7);
        var dpEdpPf = (id >> 16) & 0x3FF;           // EDP, DP, PF
        var pf = (byte)(dpEdpPf & 0xFF);
        var ps = (byte)(id >> 8);
        var source = (byte)id;
        return pf < 240
            ? new J1939Id(priority, dpEdpPf << 8, ps, source)
            : new J1939Id(priority, (dpEdpPf << 8) | ps, Global, source);
    }

    /// <summary>A CAN frame with this identifier.</summary>
    public CanFrame Frame(ReadOnlyMemory<byte> data) => new(ToCanId(), data, CanFrameFlags.Extended);

    /// <inheritdoc />
    public override string ToString() => $"PGN {Pgn} (0x{Pgn:X5}) {Source:X2}→{(Destination == Global ? "all" : Destination.ToString("X2", CultureInfo.InvariantCulture))} P{Priority}";
}

/// <summary>Well-known parameter group numbers.</summary>
public static class Pgn
{
    /// <summary>Request (PDU1, 3 data bytes = requested PGN).</summary>
    public const uint Request = 0xEA00;
    /// <summary>Address claimed / cannot claim (8-byte NAME).</summary>
    public const uint AddressClaimed = 0xEE00;
    /// <summary>Transport protocol connection management (TP.CM).</summary>
    public const uint TpConnection = 0xEC00;
    /// <summary>Transport protocol data transfer (TP.DT).</summary>
    public const uint TpData = 0xEB00;
    /// <summary>Acknowledgement.</summary>
    public const uint Acknowledgement = 0xE800;
    /// <summary>Electronic engine controller 1 (engine speed, torque).</summary>
    public const uint Eec1 = 0xF004;
    /// <summary>Electronic engine controller 2 (accelerator pedal, load).</summary>
    public const uint Eec2 = 0xF003;
    /// <summary>Cruise control / vehicle speed 1.</summary>
    public const uint Ccvs1 = 0xFEF1;
    /// <summary>Engine temperature 1.</summary>
    public const uint Et1 = 0xFEEE;
    /// <summary>Engine fluid level / pressure 1.</summary>
    public const uint EflP1 = 0xFEEF;
    /// <summary>Fuel economy (liquid).</summary>
    public const uint Lfe1 = 0xFEF2;
    /// <summary>Vehicle electrical power 1.</summary>
    public const uint Vep1 = 0xFEF7;
    /// <summary>Engine hours, revolutions.</summary>
    public const uint Hours = 0xFEE5;
    /// <summary>Vehicle identification (VIN, ASCII, '*' terminated).</summary>
    public const uint VehicleIdentification = 0xFEEC;
    /// <summary>Component identification (make*model*serial*unit*).</summary>
    public const uint ComponentIdentification = 0xFEEB;
    /// <summary>Active diagnostic trouble codes (DM1).</summary>
    public const uint Dm1 = 0xFECA;
    /// <summary>Previously active diagnostic trouble codes (DM2).</summary>
    public const uint Dm2 = 0xFECB;

    /// <summary>Acronym of a well-known PGN.</summary>
    public static string Name(uint pgn) => pgn switch
    {
        Request => "Request", AddressClaimed => "Address claimed", TpConnection => "TP.CM", TpData => "TP.DT", Acknowledgement => "ACK",
        Eec1 => "EEC1", Eec2 => "EEC2", Ccvs1 => "CCVS1", Et1 => "ET1", EflP1 => "EFL/P1", Lfe1 => "LFE1", Vep1 => "VEP1", Hours => "HOURS",
        VehicleIdentification => "VI", ComponentIdentification => "CI", Dm1 => "DM1", Dm2 => "DM2",
        _ => string.Create(CultureInfo.InvariantCulture, $"PGN {pgn}"),
    };
}

/// <summary>A J1939 NAME (64 bits) as sent in the address-claimed message.</summary>
/// <param name="IdentityNumber">21 bits.</param>
/// <param name="ManufacturerCode">11 bits.</param>
/// <param name="EcuInstance">3 bits.</param>
/// <param name="FunctionInstance">5 bits.</param>
/// <param name="Function">8 bits (0 = engine, 3 = transmission, 11 = brakes…).</param>
/// <param name="VehicleSystem">7 bits.</param>
/// <param name="VehicleSystemInstance">4 bits.</param>
/// <param name="IndustryGroup">3 bits (1 = on-highway, 2 = agriculture, 3 = construction, 4 = marine, 5 = industrial).</param>
/// <param name="ArbitraryAddressCapable">The node can pick another address after losing a claim.</param>
public sealed record J1939Name(uint IdentityNumber, ushort ManufacturerCode, byte EcuInstance, byte FunctionInstance, byte Function, byte VehicleSystem, byte VehicleSystemInstance, byte IndustryGroup, bool ArbitraryAddressCapable)
{
    /// <summary>The 64-bit value (lower NAME value wins address conflicts).</summary>
    public ulong Value =>
        (IdentityNumber & 0x1F_FFFFUL) | ((ulong)(ManufacturerCode & 0x7FF) << 21) | ((ulong)(EcuInstance & 7) << 32) | ((ulong)(FunctionInstance & 0x1F) << 35)
        | ((ulong)Function << 40) | ((ulong)(VehicleSystem & 0x7F) << 49) | ((ulong)(VehicleSystemInstance & 0xF) << 56) | ((ulong)(IndustryGroup & 7) << 60)
        | (ArbitraryAddressCapable ? 1UL << 63 : 0);

    /// <summary>The 8 data bytes (little-endian).</summary>
    public byte[] Encode()
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(b, Value);
        return b;
    }

    /// <summary>Decodes 8 data bytes.</summary>
    public static J1939Name Parse(ReadOnlySpan<byte> d)
    {
        if (d.Length < 8) throw new FormatException("A J1939 NAME has 8 bytes.");
        var v = BinaryPrimitives.ReadUInt64LittleEndian(d);
        return new J1939Name((uint)(v & 0x1F_FFFF), (ushort)((v >> 21) & 0x7FF), (byte)((v >> 32) & 7), (byte)((v >> 35) & 0x1F), (byte)(v >> 40),
            (byte)((v >> 49) & 0x7F), (byte)((v >> 56) & 0xF), (byte)((v >> 60) & 7), (v >> 63) != 0);
    }
}

/// <summary>Transport protocol connection-management messages (TP.CM).</summary>
public enum TpControl : byte
{
    /// <summary>Request to send.</summary>
    RequestToSend = 16,
    /// <summary>Clear to send.</summary>
    ClearToSend = 17,
    /// <summary>End of message acknowledgement.</summary>
    EndOfMessageAck = 19,
    /// <summary>Broadcast announce message.</summary>
    Broadcast = 32,
    /// <summary>Connection abort.</summary>
    Abort = 255,
}

/// <summary>A decoded TP.CM message.</summary>
/// <param name="Control">Control byte.</param>
/// <param name="Size">Message size (RTS, BAM, EndOfMsgAck).</param>
/// <param name="Packets">Number of packets (RTS, BAM, EndOfMsgAck) or packets to send (CTS).</param>
/// <param name="NextPacket">Next packet number (CTS).</param>
/// <param name="MaxPerCts">Maximum packets per CTS (RTS).</param>
/// <param name="AbortReason">Abort reason.</param>
/// <param name="Pgn">PGN of the packeted message.</param>
public sealed record TpConnectionMessage(TpControl Control, ushort Size, byte Packets, byte NextPacket, byte MaxPerCts, byte AbortReason, uint Pgn)
{
    /// <summary>Decodes 8 data bytes.</summary>
    public static TpConnectionMessage Parse(ReadOnlySpan<byte> d)
    {
        if (d.Length < 8) throw new FormatException("TP.CM has 8 bytes.");
        var pgn = d[5] | ((uint)d[6] << 8) | ((uint)d[7] << 16);
        var control = (TpControl)d[0];
        return control switch
        {
            TpControl.RequestToSend => new(control, BinaryPrimitives.ReadUInt16LittleEndian(d[1..]), d[3], 0, d[4], 0, pgn),
            TpControl.Broadcast or TpControl.EndOfMessageAck => new(control, BinaryPrimitives.ReadUInt16LittleEndian(d[1..]), d[3], 0, 0, 0, pgn),
            TpControl.ClearToSend => new(control, 0, d[1], d[2], 0, 0, pgn),
            TpControl.Abort => new(control, 0, 0, 0, 0, d[1], pgn),
            _ => throw new FormatException($"Unknown TP.CM control byte {d[0]}."),
        };
    }

    /// <summary>Encodes 8 data bytes (reserved bytes 0xFF).</summary>
    public byte[] Encode()
    {
        var b = new byte[] { (byte)Control, 0xFF, 0xFF, 0xFF, 0xFF, (byte)Pgn, (byte)(Pgn >> 8), (byte)(Pgn >> 16) };
        switch (Control)
        {
            case TpControl.RequestToSend:
                BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(1), Size);
                b[3] = Packets;
                b[4] = MaxPerCts;
                break;
            case TpControl.Broadcast:
                BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(1), Size);
                b[3] = Packets;
                break;
            case TpControl.EndOfMessageAck:
                BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(1), Size);
                b[3] = Packets;
                break;
            case TpControl.ClearToSend:
                b[1] = Packets;
                b[2] = NextPacket;
                break;
            case TpControl.Abort:
                b[1] = AbortReason;
                break;
        }

        return b;
    }

    /// <summary>Packets needed for <paramref name="size"/> bytes (7 per TP.DT).</summary>
    public static byte PacketsFor(int size) => (byte)((size + 6) / 7);
}

/// <summary>One diagnostic trouble code (SPN, FMI, occurrence count).</summary>
/// <param name="Spn">Suspect parameter number (19 bits).</param>
/// <param name="Fmi">Failure mode identifier (0–31).</param>
/// <param name="OccurrenceCount">Occurrence count (0–126; 127 = not available).</param>
public sealed record J1939Dtc(uint Spn, byte Fmi, byte OccurrenceCount)
{
    /// <summary>Decodes 4 bytes (SPN conversion method 0).</summary>
    public static J1939Dtc Parse(ReadOnlySpan<byte> d) =>
        new((uint)(d[0] | (d[1] << 8) | ((d[2] >> 5) << 16)), (byte)(d[2] & 0x1F), (byte)(d[3] & 0x7F));

    /// <summary>Encodes 4 bytes.</summary>
    public byte[] Encode() => [(byte)Spn, (byte)(Spn >> 8), (byte)(((Spn >> 16) << 5) | (Fmi & 0x1Fu)), (byte)(OccurrenceCount & 0x7F)];

    /// <summary>A readable failure mode.</summary>
    public string FailureMode => Fmi switch
    {
        0 => "above normal (most severe)", 1 => "below normal (most severe)", 2 => "erratic", 3 => "voltage above normal", 4 => "voltage below normal",
        5 => "current below normal", 6 => "current above normal", 7 => "mechanical system not responding", 8 => "abnormal frequency",
        9 => "abnormal update rate", 10 => "abnormal rate of change", 11 => "root cause not known", 12 => "bad device", 13 => "out of calibration",
        14 => "special instructions", 15 => "above normal (least severe)", 16 => "above normal (moderate)", 17 => "below normal (least severe)",
        18 => "below normal (moderate)", 19 => "received network data in error", 31 => "condition exists", _ => $"FMI {Fmi}",
    };

    /// <inheritdoc />
    public override string ToString() => $"SPN {Spn} FMI {Fmi} ({FailureMode}) ×{OccurrenceCount} {J1939Spn.Name(Spn)}";
}

/// <summary>A DM1 / DM2 message: lamp status and trouble codes.</summary>
/// <param name="MalfunctionLamp">MIL status (0 off, 1 on).</param>
/// <param name="RedStopLamp">Red stop lamp.</param>
/// <param name="AmberWarningLamp">Amber warning lamp.</param>
/// <param name="ProtectLamp">Protect lamp.</param>
/// <param name="Dtcs">Trouble codes (empty when none is active).</param>
public sealed record J1939Dm1(byte MalfunctionLamp, byte RedStopLamp, byte AmberWarningLamp, byte ProtectLamp, IReadOnlyList<J1939Dtc> Dtcs)
{
    /// <summary>Decodes a DM1 payload (8 bytes or more through the transport protocol).</summary>
    public static J1939Dm1 Parse(ReadOnlySpan<byte> d)
    {
        if (d.Length < 6) throw new FormatException("DM1 has at least 6 bytes.");
        var lamps = d[0];
        var dtcs = new List<J1939Dtc>();
        for (var i = 2; i + 3 < d.Length; i += 4)
        {
            var dtc = J1939Dtc.Parse(d.Slice(i, 4));
            if (dtc.Spn != 0 || dtc.Fmi != 0) dtcs.Add(dtc);
        }

        return new J1939Dm1((byte)((lamps >> 6) & 3), (byte)((lamps >> 4) & 3), (byte)((lamps >> 2) & 3), (byte)(lamps & 3), dtcs);
    }

    /// <summary>Encodes the payload (8 bytes when one or no DTC, otherwise 2 + 4n bytes for the transport protocol).</summary>
    public byte[] Encode()
    {
        var b = new List<byte> { (byte)((MalfunctionLamp << 6) | (RedStopLamp << 4) | (AmberWarningLamp << 2) | ProtectLamp), 0xFF };
        if (Dtcs.Count == 0) b.AddRange([0, 0, 0, 0, 0xFF, 0xFF]);
        foreach (var d in Dtcs) b.AddRange(d.Encode());
        if (Dtcs.Count == 1) b.AddRange([0xFF, 0xFF]);
        return [.. b];
    }
}

/// <summary>Suspect parameters (SPNs) of the common PGNs, with SAE J1939-71 scaling.</summary>
public static class J1939Spn
{
    /// <summary>A decoded parameter.</summary>
    /// <param name="Spn">SPN.</param>
    /// <param name="Name">Name.</param>
    /// <param name="Value">Scaled value, or null when the raw value means "not available" or "error".</param>
    /// <param name="Unit">Unit.</param>
    public sealed record Reading(uint Spn, string Name, double? Value, string Unit)
    {
        /// <inheritdoc />
        public override string ToString() => $"{Name} = {(Value is { } v ? (v.ToString("0.###", CultureInfo.InvariantCulture) + " " + Unit).TrimEnd() : "n/a")}";
    }

    private sealed record Definition(uint Spn, string Name, int Byte, int Length, double Scale, double Offset, string Unit);

    private static readonly Dictionary<uint, Definition[]> Definitions = new()
    {
        [Pgn.Eec1] =
        [
            new(899, "Engine torque mode", 0, 0, 1, 0, ""),
            new(512, "Driver's demand engine torque", 1, 1, 1, -125, "%"),
            new(513, "Actual engine torque", 2, 1, 1, -125, "%"),
            new(190, "Engine speed", 3, 2, 0.125, 0, "rpm"),
            new(1483, "Source address of controlling device", 5, 1, 1, 0, ""),
        ],
        [Pgn.Eec2] = [new(91, "Accelerator pedal position", 1, 1, 0.4, 0, "%"), new(92, "Engine load at current speed", 2, 1, 1, 0, "%")],
        [Pgn.Ccvs1] = [new(84, "Wheel-based vehicle speed", 1, 2, 1 / 256.0, 0, "km/h")],
        [Pgn.Et1] =
        [
            new(110, "Engine coolant temperature", 0, 1, 1, -40, "°C"),
            new(174, "Fuel temperature", 1, 1, 1, -40, "°C"),
            new(175, "Engine oil temperature", 2, 2, 0.03125, -273, "°C"),
        ],
        [Pgn.EflP1] = [new(94, "Fuel delivery pressure", 0, 1, 4, 0, "kPa"), new(98, "Engine oil level", 2, 1, 0.4, 0, "%"), new(100, "Engine oil pressure", 3, 1, 4, 0, "kPa")],
        [Pgn.Lfe1] = [new(183, "Engine fuel rate", 0, 2, 0.05, 0, "l/h"), new(51, "Throttle position", 6, 1, 0.4, 0, "%")],
        [Pgn.Vep1] = [new(167, "Charging system potential", 2, 2, 0.05, 0, "V"), new(168, "Battery potential", 4, 2, 0.05, 0, "V")],
        [Pgn.Hours] = [new(247, "Engine total hours of operation", 0, 4, 0.05, 0, "h"), new(249, "Engine total revolutions", 4, 4, 1000, 0, "r")],
    };

    /// <summary>Name of an SPN (known ones only).</summary>
    public static string Name(uint spn) => Definitions.Values.SelectMany(d => d).FirstOrDefault(d => d.Spn == spn)?.Name ?? spn switch
    {
        190 => "Engine speed", 100 => "Engine oil pressure", 110 => "Engine coolant temperature", 168 => "Battery potential", 3226 => "Aftertreatment NOx",
        _ => "",
    };

    /// <summary>True when values of <paramref name="pgn"/> can be decoded.</summary>
    public static bool Knows(uint pgn) => Definitions.ContainsKey(pgn);

    /// <summary>Decodes the SPNs of a PGN; raw values in the "not available" / "error" ranges give null.</summary>
    public static IReadOnlyList<Reading> Decode(uint pgn, ReadOnlySpan<byte> data)
    {
        if (!Definitions.TryGetValue(pgn, out var defs)) return [];
        var values = new List<Reading>(defs.Length);
        foreach (var d in defs)
        {
            if (d.Length == 0)
            {
                values.Add(new Reading(d.Spn, d.Name, data.Length > 0 ? data[0] & 0x0F : null, d.Unit));
                continue;
            }

            if (d.Byte + d.Length > data.Length)
            {
                values.Add(new Reading(d.Spn, d.Name, null, d.Unit));
                continue;
            }

            var raw = d.Length switch
            {
                1 => data[d.Byte],
                2 => BinaryPrimitives.ReadUInt16LittleEndian(data[d.Byte..]),
                _ => (double)BinaryPrimitives.ReadUInt32LittleEndian(data[d.Byte..]),
            };
            var valid = d.Length switch { 1 => raw <= 0xFA, 2 => raw <= 0xFAFF, _ => raw <= 0xFAFF_FFFF };
            values.Add(new Reading(d.Spn, d.Name, valid ? Math.Round((raw * d.Scale) + d.Offset, 6) : null, d.Unit));
        }

        return values;
    }

    /// <summary>Encodes a scaled value into its raw field (for simulators and tests).</summary>
    public static void Encode(uint pgn, uint spn, double value, Span<byte> data)
    {
        var d = Definitions[pgn].Single(x => x.Spn == spn);
        var raw = (ulong)Math.Round((value - d.Offset) / d.Scale);
        switch (d.Length)
        {
            case 0: data[d.Byte] = (byte)((data[d.Byte] & 0xF0) | ((byte)raw & 0x0F)); break;
            case 1: data[d.Byte] = (byte)raw; break;
            case 2: BinaryPrimitives.WriteUInt16LittleEndian(data[d.Byte..], (ushort)raw); break;
            default: BinaryPrimitives.WriteUInt32LittleEndian(data[d.Byte..], (uint)raw); break;
        }
    }

    /// <summary>Frame-lane fields of a J1939 frame in the tap layout (identifier split into priority/PGN/addresses, data).</summary>
    public static IReadOnlyList<FrameField> Describe(in CanFrame frame)
    {
        var id = J1939Id.FromCanId(frame.Id);
        var fields = new List<FrameField>
        {
            new("Priority", 0, 1, FrameFieldKind.Header, id.Priority.ToString(CultureInfo.InvariantCulture)),
            new("PGN", 1, 2, FrameFieldKind.Function, $"{Pgn.Name(id.Pgn)} ({id.Pgn})"),
            new("SA", 3, 1, FrameFieldKind.Address, $"0x{id.Source:X2}{(id.IsPdu1 ? $" → 0x{id.Destination:X2}" : "")}"),
            new("Len", 4, 1, FrameFieldKind.Length, frame.Data.Length.ToString(CultureInfo.InvariantCulture)),
        };
        var data = frame.Data.Span;
        if (Knows(id.Pgn))
        {
            fields.Add(new FrameField("SPNs", 8, data.Length, FrameFieldKind.Data, string.Join(", ", Decode(id.Pgn, data))));
        }
        else if (id.Pgn == global::IoTCom.Net.Protocols.J1939.Pgn.TpConnection && data.Length >= 8)
        {
            var tp = TpConnectionMessage.Parse(data);
            fields.Add(new FrameField("TP.CM", 8, data.Length, FrameFieldKind.Function, $"{tp.Control} {Pgn.Name(tp.Pgn)} {tp.Size} B"));
        }
        else if (data.Length > 0)
        {
            fields.Add(new FrameField("Data", 8, data.Length, FrameFieldKind.Data, Convert.ToHexString(data)));
        }

        return fields;
    }
}
