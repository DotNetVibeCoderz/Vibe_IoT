using System.Buffers.Binary;

namespace IoTCom.Net.Protocols.Knx;

/// <summary>cEMI message codes used for data link telegrams.</summary>
public enum KnxMessageCode : byte
{
    /// <summary>L_Data.req: a client asks the interface to send.</summary>
    DataRequest = 0x11,
    /// <summary>L_Data.con: the interface confirms (or not) that it sent.</summary>
    DataConfirmation = 0x2E,
    /// <summary>L_Data.ind: a telegram seen on the bus.</summary>
    DataIndication = 0x29,
}

/// <summary>Application-layer services of a group telegram (APCI).</summary>
public enum KnxApci : ushort
{
    /// <summary>A_GroupValue_Read.</summary>
    GroupValueRead = 0x000,
    /// <summary>A_GroupValue_Response.</summary>
    GroupValueResponse = 0x040,
    /// <summary>A_GroupValue_Write.</summary>
    GroupValueWrite = 0x080,
}

/// <summary>Telegram priority (control field 1, bits 3–2).</summary>
public enum KnxPriority : byte
{
    /// <summary>System.</summary>
    System = 0,
    /// <summary>Normal.</summary>
    Normal = 1,
    /// <summary>Urgent.</summary>
    Urgent = 2,
    /// <summary>Low (default for group communication).</summary>
    Low = 3,
}

/// <summary>
/// A cEMI L_Data frame carrying a group telegram (KNX Standard 3/6/3, sans-I/O): message code, control fields, source and
/// destination, and the TPCI/APCI with its data. Telegrams to individual addresses are parsed too, but only group
/// services are interpreted.
/// </summary>
public sealed record KnxCemiFrame
{
    /// <summary>Message code.</summary>
    public KnxMessageCode MessageCode { get; init; } = KnxMessageCode.DataIndication;

    /// <summary>Source individual address.</summary>
    public KnxIndividualAddress Source { get; init; }

    /// <summary>Destination: a group address (raw) when <see cref="IsGroup"/>, else an individual address.</summary>
    public ushort Destination { get; init; }

    /// <summary>True when the destination is a group address.</summary>
    public bool IsGroup { get; init; } = true;

    /// <summary>Priority.</summary>
    public KnxPriority Priority { get; init; } = KnxPriority.Low;

    /// <summary>Hop count (0–7, 6 by default).</summary>
    public int HopCount { get; init; } = 6;

    /// <summary>Repeat flag cleared (a repetition); false for the first transmission.</summary>
    public bool Repeated { get; init; }

    /// <summary>Confirmation error flag (L_Data.con only): true when the interface could not send.</summary>
    public bool ConfirmError { get; init; }

    /// <summary>Application service.</summary>
    public KnxApci Apci { get; init; } = KnxApci.GroupValueWrite;

    /// <summary>Value carried by a write or response.</summary>
    public KnxGroupValue Value { get; init; } = KnxGroupValue.Empty;

    /// <summary>Destination as a group address.</summary>
    public KnxGroupAddress Group => new(Destination);

    /// <summary>A group telegram.</summary>
    public static KnxCemiFrame GroupTelegram(KnxMessageCode code, KnxIndividualAddress source, KnxGroupAddress destination, KnxApci apci, KnxGroupValue value) =>
        new() { MessageCode = code, Source = source, Destination = destination.Raw, IsGroup = true, Apci = apci, Value = value };

    /// <summary>Encodes the frame (no additional information).</summary>
    public byte[] Encode()
    {
        var dataLen = Value.IsShort ? 0 : Value.Bytes.Length;
        var npduLength = 1 + dataLen;                       // APDU length after the TPCI byte
        var b = new byte[2 + 2 + 4 + 1 + 2 + dataLen];
        b[0] = (byte)MessageCode;
        b[1] = 0;                                           // no additional info
        b[2] = (byte)(0x80 | (Repeated ? 0 : 0x20) | 0x10 | ((int)Priority << 2) | (ConfirmError ? 1 : 0)); // standard frame, broadcast
        b[3] = (byte)((IsGroup ? 0x80 : 0) | ((HopCount & 7) << 4));
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4), Source.Raw);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(6), Destination);
        b[8] = (byte)npduLength;
        var apci = (ushort)Apci;
        b[9] = (byte)((apci >> 8) & 0x03);                  // TPCI 0 (unnumbered data) + APCI high bits
        b[10] = (byte)(apci & 0xC0);
        if (Value.IsShort) b[10] |= (byte)(Value.Bytes[0] & 0x3F);
        else Value.Bytes.CopyTo(b, 11);
        return b;
    }

    /// <summary>Decodes a cEMI L_Data frame.</summary>
    public static KnxCemiFrame Decode(ReadOnlySpan<byte> b)
    {
        if (b.Length < 2) throw new ProtocolException("cEMI frame too short.");
        var code = b[0];
        if (code is not (0x11 or 0x2E or 0x29)) throw new ProtocolException($"cEMI message code 0x{code:X2} is not an L_Data frame.");
        var info = b[1];
        var o = 2 + info;
        if (b.Length < o + 7) throw new ProtocolException("cEMI L_Data frame too short.");
        var c1 = b[o];
        var c2 = b[o + 1];
        if ((c1 & 0x80) == 0) throw new ProtocolException("Extended cEMI frames are not supported.");
        var source = BinaryPrimitives.ReadUInt16BigEndian(b[(o + 2)..]);
        var dest = BinaryPrimitives.ReadUInt16BigEndian(b[(o + 4)..]);
        var len = b[o + 6];
        var tpdu = b[(o + 7)..];
        if (tpdu.Length != len + 1) throw new ProtocolException($"cEMI NPDU length {len} does not match the {tpdu.Length - 1} byte(s) present.");
        KnxApci apci = KnxApci.GroupValueRead;
        var value = KnxGroupValue.Empty;
        if (len >= 1)
        {
            var a = (ushort)(((tpdu[0] & 0x03) << 8) | (tpdu[1] & 0xC0));
            apci = (KnxApci)a;
            if (len == 1) value = apci == KnxApci.GroupValueRead ? KnxGroupValue.Empty : KnxGroupValue.Short(tpdu[1] & 0x3F);
            else value = KnxGroupValue.Of(tpdu[2..].ToArray());
        }

        return new KnxCemiFrame
        {
            MessageCode = (KnxMessageCode)code,
            Repeated = (c1 & 0x20) == 0,
            Priority = (KnxPriority)((c1 >> 2) & 3),
            ConfirmError = (c1 & 1) != 0,
            IsGroup = (c2 & 0x80) != 0,
            HopCount = (c2 >> 4) & 7,
            Source = new KnxIndividualAddress(source),
            Destination = dest,
            Apci = apci,
            Value = value,
        };
    }

    /// <summary>Destination as text (group or individual).</summary>
    public string DestinationText => IsGroup ? Group.ToString() : new KnxIndividualAddress(Destination).ToString();

    /// <inheritdoc />
    public override string ToString()
    {
        var service = Apci switch { KnxApci.GroupValueRead => "Read", KnxApci.GroupValueResponse => "Response", KnxApci.GroupValueWrite => "Write", _ => $"APCI 0x{(ushort)Apci:X3}" };
        var code = MessageCode switch { KnxMessageCode.DataRequest => "L_Data.req", KnxMessageCode.DataConfirmation => ConfirmError ? "L_Data.con (error)" : "L_Data.con", _ => "L_Data.ind" };
        return $"{code} {Source} → {DestinationText} {service}" + (Apci == KnxApci.GroupValueRead ? "" : $" {Value}");
    }

    /// <summary>Frame lane fields of a cEMI frame starting at <paramref name="offset"/>.</summary>
    public static IReadOnlyList<FrameField> Describe(ReadOnlySpan<byte> b, int offset = 0)
    {
        var fields = new List<FrameField>();
        if (b.Length < 2)
        {
            fields.Add(new FrameField("cEMI", offset, b.Length, FrameFieldKind.Error, "too short"));
            return fields;
        }

        fields.Add(new FrameField("Message code", offset, 1, FrameFieldKind.Function, ((KnxMessageCode)b[0]).ToString()));
        fields.Add(new FrameField("Add. info", offset + 1, 1 + b[1], FrameFieldKind.Length, b[1].ToString(System.Globalization.CultureInfo.InvariantCulture)));
        try
        {
            var f = Decode(b);
            var o = offset + 2 + b[1];
            fields.Add(new FrameField("Control 1", o, 1, FrameFieldKind.Header, $"{f.Priority}{(f.Repeated ? ", repeated" : "")}{(f.ConfirmError ? ", error" : "")}"));
            fields.Add(new FrameField("Control 2", o + 1, 1, FrameFieldKind.Header, $"{(f.IsGroup ? "group" : "individual")}, hops {f.HopCount}"));
            fields.Add(new FrameField("Source", o + 2, 2, FrameFieldKind.Address, f.Source.ToString()));
            fields.Add(new FrameField("Destination", o + 4, 2, FrameFieldKind.Address, f.DestinationText));
            fields.Add(new FrameField("Length", o + 6, 1, FrameFieldKind.Length, b[o - offset + 6].ToString(System.Globalization.CultureInfo.InvariantCulture)));
            fields.Add(new FrameField("TPCI/APCI", o + 7, 2, FrameFieldKind.Function, f.Apci.ToString() + (f.Value.IsShort ? $" (value {f.Value.Bytes[0]})" : "")));
            if (!f.Value.IsShort && f.Value.Bytes.Length > 0) fields.Add(new FrameField("Data", o + 9, f.Value.Bytes.Length, FrameFieldKind.Data, Convert.ToHexString(f.Value.Bytes)));
        }
        catch (ProtocolException ex)
        {
            fields.Add(new FrameField("L_Data", offset + 2, Math.Max(0, b.Length - 2), FrameFieldKind.Error, ex.Message));
        }

        return fields;
    }
}
