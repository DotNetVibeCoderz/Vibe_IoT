using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace IoTCom.Net.Protocols.EtherCat;

/// <summary>EtherCAT datagram commands (ETG.1000.4).</summary>
public enum EcatCommand : byte
{
    /// <summary>No operation.</summary>
    Nop = 0,
    /// <summary>Auto-increment (positional) physical read.</summary>
    Aprd = 1,
    /// <summary>Auto-increment physical write.</summary>
    Apwr = 2,
    /// <summary>Auto-increment physical read/write.</summary>
    Aprw = 3,
    /// <summary>Configured-address physical read.</summary>
    Fprd = 4,
    /// <summary>Configured-address physical write.</summary>
    Fpwr = 5,
    /// <summary>Configured-address physical read/write.</summary>
    Fprw = 6,
    /// <summary>Broadcast read (data of all slaves OR-ed).</summary>
    Brd = 7,
    /// <summary>Broadcast write.</summary>
    Bwr = 8,
    /// <summary>Broadcast read/write.</summary>
    Brw = 9,
    /// <summary>Logical memory read (through FMMUs).</summary>
    Lrd = 10,
    /// <summary>Logical memory write.</summary>
    Lwr = 11,
    /// <summary>Logical memory read/write (the usual cyclic process-data exchange).</summary>
    Lrw = 12,
    /// <summary>Auto-increment read, multiple write (distributed clocks).</summary>
    Armw = 13,
    /// <summary>Configured-address read, multiple write.</summary>
    Frmw = 14,
}

/// <summary>EtherCAT state machine (AL) states.</summary>
public enum EcatState : byte
{
    /// <summary>Unknown / not read.</summary>
    None = 0,
    /// <summary>Init: no mailbox, no process data.</summary>
    Init = 1,
    /// <summary>Pre-Operational: mailbox (CoE) available.</summary>
    PreOp = 2,
    /// <summary>Bootstrap: firmware update over FoE.</summary>
    Boot = 3,
    /// <summary>Safe-Operational: inputs valid, outputs held in a safe state.</summary>
    SafeOp = 4,
    /// <summary>Operational: inputs and outputs exchanged.</summary>
    Op = 8,
}

/// <summary>One EtherCAT datagram: command, address, data, working counter.</summary>
public sealed class EcatDatagram
{
    /// <summary>Header (10 bytes) plus working counter (2 bytes).</summary>
    public const int Overhead = 12;

    /// <summary>Command.</summary>
    public EcatCommand Command { get; init; }

    /// <summary>Index chosen by the master to match replies.</summary>
    public byte Index { get; init; }

    /// <summary>
    /// 32-bit address: for positional and configured commands the low word is the slave address (ADP) and the high word
    /// the register offset (ADO); for logical commands the whole value is the logical address.
    /// </summary>
    public uint Address { get; set; }

    /// <summary>Data (read commands carry zeros the slaves fill in).</summary>
    public byte[] Data { get; init; } = [];

    /// <summary>Interrupt field.</summary>
    public ushort Irq { get; set; }

    /// <summary>Working counter: incremented by every slave that processed the datagram.</summary>
    public ushort WorkingCounter { get; set; }

    /// <summary>Circulating flag (a slave set it when the frame passed a processing unit twice).</summary>
    public bool Circulating { get; set; }

    /// <summary>Slave address part (ADP).</summary>
    public ushort Adp
    {
        get => (ushort)(Address & 0xFFFF);
        set => Address = (Address & 0xFFFF0000) | value;
    }

    /// <summary>Register offset part (ADO).</summary>
    public ushort Ado => (ushort)(Address >> 16);

    /// <summary>Bytes on the wire.</summary>
    public int EncodedLength => Overhead + Data.Length;

    /// <summary>True for logical commands.</summary>
    public bool IsLogical => Command is EcatCommand.Lrd or EcatCommand.Lwr or EcatCommand.Lrw;

    /// <summary>Auto-increment (positional) datagram: <paramref name="position"/> 0 is the first slave.</summary>
    public static EcatDatagram Positional(EcatCommand command, int position, ushort register, byte[] data, byte index = 0)
        => new() { Command = command, Index = index, Address = (uint)(ushort)(-position) | ((uint)register << 16), Data = data };

    /// <summary>Configured-address datagram.</summary>
    public static EcatDatagram Configured(EcatCommand command, ushort station, ushort register, byte[] data, byte index = 0)
        => new() { Command = command, Index = index, Address = station | ((uint)register << 16), Data = data };

    /// <summary>Broadcast datagram.</summary>
    public static EcatDatagram Broadcast(EcatCommand command, ushort register, byte[] data, byte index = 0)
        => new() { Command = command, Index = index, Address = (uint)register << 16, Data = data };

    /// <summary>Logical datagram.</summary>
    public static EcatDatagram Logical(EcatCommand command, uint address, byte[] data, byte index = 0)
        => new() { Command = command, Index = index, Address = address, Data = data };

    /// <inheritdoc />
    public override string ToString()
    {
        var where = IsLogical ? $"0x{Address:X8}" : Command is EcatCommand.Brd or EcatCommand.Bwr or EcatCommand.Brw ? $"*:0x{Ado:X4}"
            : Command is EcatCommand.Aprd or EcatCommand.Apwr or EcatCommand.Aprw or EcatCommand.Armw ? $"#{(short)-(short)Adp}:0x{Ado:X4}" : $"0x{Adp:X4}:0x{Ado:X4}";
        return string.Create(CultureInfo.InvariantCulture, $"{Command.ToString().ToUpperInvariant()} {where} [{Data.Length}] wkc={WorkingCounter}");
    }
}

/// <summary>Ethernet frames with EtherType 0x88A4 carrying EtherCAT datagrams.</summary>
public static class EcatFrame
{
    /// <summary>EtherCAT EtherType.</summary>
    public const ushort EtherType = 0x88A4;

    /// <summary>Ethernet header (14) plus EtherCAT header (2).</summary>
    public const int HeaderLength = 16;

    /// <summary>Largest datagram payload sum in one standard frame (1500 − 2).</summary>
    public const int MaxPayload = 1498;

    /// <summary>Source address the master sends with; slaves set bit 1 of the first byte on the way back (SOEM uses the same).</summary>
    public static ReadOnlySpan<byte> MasterAddress => [0x01, 0x01, 0x01, 0x01, 0x01, 0x01];

    /// <summary>Encodes a full Ethernet frame (broadcast destination, padded to 60 bytes).</summary>
    public static byte[] Encode(IReadOnlyList<EcatDatagram> datagrams, ReadOnlySpan<byte> source = default)
    {
        var payload = EncodePayload(datagrams);
        var frame = new byte[Math.Max(60, 14 + payload.Length)];
        frame.AsSpan(0, 6).Fill(0xFF);
        (source.IsEmpty ? MasterAddress : source).CopyTo(frame.AsSpan(6, 6));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12), EtherType);
        payload.CopyTo(frame, 14);
        return frame;
    }

    /// <summary>Encodes the EtherCAT header and datagrams (no Ethernet header).</summary>
    public static byte[] EncodePayload(IReadOnlyList<EcatDatagram> datagrams)
    {
        if (datagrams.Count == 0) throw new ArgumentException("A frame needs at least one datagram.", nameof(datagrams));
        var length = datagrams.Sum(d => d.EncodedLength);
        if (length > MaxPayload) throw new ArgumentException($"Datagrams need {length} bytes; a frame holds {MaxPayload}.", nameof(datagrams));
        var buffer = new byte[2 + length];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, (ushort)(length | 0x1000));   // type 1 = EtherCAT commands
        var at = 2;
        for (var i = 0; i < datagrams.Count; i++)
        {
            var d = datagrams[i];
            if (d.Data.Length > 0x7FF) throw new ArgumentException("A datagram carries at most 2047 bytes.", nameof(datagrams));
            buffer[at] = (byte)d.Command;
            buffer[at + 1] = d.Index;
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(at + 2), d.Address);
            var lenField = d.Data.Length | (d.Circulating ? 0x4000 : 0) | (i < datagrams.Count - 1 ? 0x8000 : 0);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(at + 6), (ushort)lenField);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(at + 8), d.Irq);
            d.Data.CopyTo(buffer, at + 10);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(at + 10 + d.Data.Length), d.WorkingCounter);
            at += d.EncodedLength;
        }

        return buffer;
    }

    /// <summary>Decodes a full Ethernet frame; false (with a reason) for other EtherTypes or malformed frames.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> frame, out IReadOnlyList<EcatDatagram> datagrams, out string? error)
    {
        datagrams = [];
        if (frame.Length < 14)
        {
            error = $"An Ethernet frame needs at least 14 bytes; got {frame.Length}.";
            return false;
        }

        var type = BinaryPrimitives.ReadUInt16BigEndian(frame[12..]);
        if (type != EtherType)
        {
            error = $"EtherType 0x{type:X4} is not EtherCAT (0x88A4).";
            return false;
        }

        return TryDecodePayload(frame[14..], out datagrams, out error);
    }

    /// <summary>Decodes the EtherCAT header and datagrams. Ethernet padding after the datagrams is ignored.</summary>
    public static bool TryDecodePayload(ReadOnlySpan<byte> payload, out IReadOnlyList<EcatDatagram> datagrams, out string? error)
    {
        datagrams = [];
        if (payload.Length < 2)
        {
            error = "Missing EtherCAT header.";
            return false;
        }

        var header = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        var length = header & 0x7FF;
        var type = header >> 12;
        if (type != 1)
        {
            error = $"EtherCAT frame type {type} is not supported (1 = commands).";
            return false;
        }

        if ((header & 0x0800) != 0)
        {
            error = "The reserved header bit is set.";
            return false;
        }

        if (length > payload.Length - 2)
        {
            error = $"Header announces {length} bytes; {payload.Length - 2} present.";
            return false;
        }

        var list = new List<EcatDatagram>();
        var body = payload.Slice(2, length);
        var at = 0;
        while (true)
        {
            if (body.Length - at < EcatDatagram.Overhead)
            {
                error = $"Datagram {list.Count + 1} is truncated.";
                return false;
            }

            var command = body[at];
            if (command > (byte)EcatCommand.Frmw)
            {
                error = $"Unknown command 0x{command:X2}.";
                return false;
            }

            var lenField = BinaryPrimitives.ReadUInt16LittleEndian(body[(at + 6)..]);
            var dataLength = lenField & 0x7FF;
            if ((lenField & 0x3800) != 0)
            {
                error = $"Datagram {list.Count + 1} has reserved length bits set.";
                return false;
            }

            if (body.Length - at < EcatDatagram.Overhead + dataLength)
            {
                error = $"Datagram {list.Count + 1} announces {dataLength} data bytes; {body.Length - at - EcatDatagram.Overhead} present.";
                return false;
            }

            list.Add(new EcatDatagram
            {
                Command = (EcatCommand)command,
                Index = body[at + 1],
                Address = BinaryPrimitives.ReadUInt32LittleEndian(body[(at + 2)..]),
                Circulating = (lenField & 0x4000) != 0,
                Irq = BinaryPrimitives.ReadUInt16LittleEndian(body[(at + 8)..]),
                Data = body.Slice(at + 10, dataLength).ToArray(),
                WorkingCounter = BinaryPrimitives.ReadUInt16LittleEndian(body[(at + 10 + dataLength)..]),
            });
            at += EcatDatagram.Overhead + dataLength;
            var more = (lenField & 0x8000) != 0;
            if (!more)
            {
                if (at != body.Length)
                {
                    error = $"{body.Length - at} bytes follow the last datagram inside the announced length.";
                    return false;
                }

                break;
            }
        }

        datagrams = list;
        error = null;
        return true;
    }

    /// <summary>True when the frame came back from the slaves (bit 1 of the source address set), not the master's own echo.</summary>
    public static bool IsReturned(ReadOnlySpan<byte> frame) => frame.Length >= 7 && (frame[6] & 0x02) != 0;

    /// <summary>Frame anatomy for frame lanes and decoders.</summary>
    public static IReadOnlyList<FrameField> DescribeFields(ReadOnlySpan<byte> frame)
    {
        var fields = new List<FrameField>();
        var ethernet = frame.Length >= 14 && BinaryPrimitives.ReadUInt16BigEndian(frame[12..]) == EtherType;
        var offset = ethernet ? 14 : 0;
        if (ethernet)
        {
            fields.Add(new FrameField("Destination", 0, 6, FrameFieldKind.Address, Convert.ToHexString(frame[..6])));
            fields.Add(new FrameField("Source", 6, 6, FrameFieldKind.Address, Convert.ToHexString(frame.Slice(6, 6)) + (IsReturned(frame) ? " (returned)" : "")));
            fields.Add(new FrameField("EtherType", 12, 2, FrameFieldKind.Header, "0x88A4 EtherCAT"));
        }

        if (!TryDecodePayload(frame[offset..], out var datagrams, out var error))
        {
            fields.Add(new FrameField("Invalid", offset, frame.Length - offset, FrameFieldKind.Error, error));
            return fields;
        }

        fields.Add(new FrameField("Header", offset, 2, FrameFieldKind.Length, $"{datagrams.Sum(d => d.EncodedLength)} B, type 1"));
        var at = offset + 2;
        var n = 1;
        foreach (var d in datagrams)
        {
            fields.Add(new FrameField($"Cmd {n}", at, 2, FrameFieldKind.Function, $"{d.Command.ToString().ToUpperInvariant()} idx {d.Index}"));
            fields.Add(new FrameField("Address", at + 2, 4, FrameFieldKind.Address, d.ToString().Split(' ')[1]));
            fields.Add(new FrameField("Len/IRQ", at + 6, 4, FrameFieldKind.Length, d.Data.Length.ToString(CultureInfo.InvariantCulture)));
            if (d.Data.Length > 0) fields.Add(new FrameField("Data", at + 10, d.Data.Length, FrameFieldKind.Data, d.Data.Length <= 8 ? Convert.ToHexString(d.Data) : $"{d.Data.Length} bytes"));
            fields.Add(new FrameField("WKC", at + 10 + d.Data.Length, 2, FrameFieldKind.Checksum, d.WorkingCounter.ToString(CultureInfo.InvariantCulture)));
            at += d.EncodedLength;
            n++;
        }

        if (at < frame.Length) fields.Add(new FrameField("Padding", at, frame.Length - at, FrameFieldKind.Delimiter));
        return fields;
    }
}

/// <summary>Mailbox protocols (ETG.1000.6).</summary>
public enum EcatMailboxType : byte
{
    /// <summary>Mailbox error reply.</summary>
    Error = 0,
    /// <summary>ADS over EtherCAT.</summary>
    AoE = 1,
    /// <summary>Ethernet over EtherCAT.</summary>
    EoE = 2,
    /// <summary>CANopen over EtherCAT.</summary>
    CoE = 3,
    /// <summary>File access over EtherCAT.</summary>
    FoE = 4,
    /// <summary>Servo profile over EtherCAT.</summary>
    SoE = 5,
    /// <summary>Vendor specific.</summary>
    VoE = 15,
}

/// <summary>A mailbox message: 6-byte header and payload, padded to the mailbox size when written.</summary>
/// <param name="Type">Protocol.</param>
/// <param name="Counter">Session counter 1–7 (0 = not used).</param>
/// <param name="Payload">Protocol payload.</param>
/// <param name="Address">Station address of the originator (usually 0).</param>
/// <param name="Priority">Priority 0–3.</param>
public sealed record EcatMailbox(EcatMailboxType Type, byte Counter, byte[] Payload, ushort Address = 0, byte Priority = 0)
{
    /// <summary>Header size.</summary>
    public const int HeaderLength = 6;

    /// <summary>Encodes the message, padded to <paramref name="mailboxSize"/> bytes (0 = no padding).</summary>
    public byte[] Encode(int mailboxSize = 0)
    {
        var size = Math.Max(HeaderLength + Payload.Length, mailboxSize);
        if (mailboxSize > 0 && HeaderLength + Payload.Length > mailboxSize) throw new ArgumentException($"The message needs {HeaderLength + Payload.Length} bytes; the mailbox holds {mailboxSize}.", nameof(mailboxSize));
        var b = new byte[size];
        BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)Payload.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), Address);
        b[4] = (byte)((Priority & 3) << 6);
        b[5] = (byte)(((byte)Type & 0x0F) | ((Counter & 7) << 4));
        Payload.CopyTo(b, HeaderLength);
        return b;
    }

    /// <summary>Decodes a mailbox (trailing padding ignored).</summary>
    public static bool TryDecode(ReadOnlySpan<byte> b, out EcatMailbox? mailbox, out string? error)
    {
        mailbox = null;
        if (b.Length < HeaderLength)
        {
            error = "A mailbox needs a 6-byte header.";
            return false;
        }

        var length = BinaryPrimitives.ReadUInt16LittleEndian(b);
        if (length > b.Length - HeaderLength)
        {
            error = $"Mailbox announces {length} bytes; {b.Length - HeaderLength} present.";
            return false;
        }

        mailbox = new EcatMailbox((EcatMailboxType)(b[5] & 0x0F), (byte)((b[5] >> 4) & 7), b.Slice(HeaderLength, length).ToArray(),
            BinaryPrimitives.ReadUInt16LittleEndian(b[2..]), (byte)(b[4] >> 6));
        error = null;
        return true;
    }
}

/// <summary>CoE services in the CoE header.</summary>
public enum CoeService : byte
{
    /// <summary>Emergency.</summary>
    Emergency = 1,
    /// <summary>SDO request.</summary>
    SdoRequest = 2,
    /// <summary>SDO response.</summary>
    SdoResponse = 3,
    /// <summary>SDO information.</summary>
    SdoInformation = 8,
}

/// <summary>CoE SDO messages (CANopen SDO in a mailbox): expedited and normal transfers, aborts.</summary>
public static class CoeSdo
{
    /// <summary>Largest payload of a normal (non-segmented) transfer in a mailbox of <paramref name="mailboxSize"/> bytes.</summary>
    public static int MaxNormalData(int mailboxSize) => mailboxSize - EcatMailbox.HeaderLength - 2 - 8;

    /// <summary>Upload (read) request.</summary>
    public static byte[] UploadRequest(ushort index, byte subIndex, bool completeAccess = false) => Sdo(0x40 | (completeAccess ? 0x10 : 0), index, subIndex, []);

    /// <summary>Download (write) request: expedited up to 4 bytes, normal above.</summary>
    public static byte[] DownloadRequest(ushort index, byte subIndex, ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) throw new ArgumentException("An SDO download needs data.", nameof(data));
        if (data.Length <= 4)
        {
            var padded = new byte[4];
            data.CopyTo(padded);
            return Sdo(0x23 | ((4 - data.Length) << 2), index, subIndex, padded);
        }

        var body = new byte[4 + data.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(body, (uint)data.Length);
        data.CopyTo(body.AsSpan(4));
        return Sdo(0x21, index, subIndex, body);
    }

    /// <summary>Abort with a CANopen abort code.</summary>
    public static byte[] Abort(ushort index, byte subIndex, uint code, bool response = true)
    {
        var data = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(data, code);
        return Sdo(0x80, index, subIndex, data, response);
    }

    /// <summary>Upload response (expedited up to 4 bytes, normal above).</summary>
    public static byte[] UploadResponse(ushort index, byte subIndex, ReadOnlySpan<byte> data)
    {
        if (data.Length is > 0 and <= 4)
        {
            var padded = new byte[4];
            data.CopyTo(padded);
            return Sdo(0x43 | ((4 - data.Length) << 2), index, subIndex, padded, response: true);
        }

        var body = new byte[4 + data.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(body, (uint)data.Length);
        data.CopyTo(body.AsSpan(4));
        return Sdo(0x41, index, subIndex, body, response: true);
    }

    /// <summary>Download response.</summary>
    public static byte[] DownloadResponse(ushort index, byte subIndex) => Sdo(0x60, index, subIndex, new byte[4], response: true);

    private static byte[] Sdo(int command, ushort index, byte subIndex, ReadOnlySpan<byte> data, bool response = false)
    {
        var b = new byte[2 + 4 + Math.Max(4, data.Length)];
        BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)((int)(response ? CoeService.SdoResponse : CoeService.SdoRequest) << 12));
        b[2] = (byte)command;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(3), index);
        b[5] = subIndex;
        data.CopyTo(b.AsSpan(6));
        return b;
    }

    /// <summary>A decoded SDO message.</summary>
    /// <param name="Service">CoE service.</param>
    /// <param name="Command">SDO command byte.</param>
    /// <param name="Index">Object index.</param>
    /// <param name="SubIndex">Sub-index.</param>
    /// <param name="Data">Transferred data (sized), or the abort code bytes.</param>
    public sealed record Message(CoeService Service, byte Command, ushort Index, byte SubIndex, byte[] Data)
    {
        /// <summary>True for an abort.</summary>
        public bool IsAbort => Command == 0x80;

        /// <summary>Abort code (when <see cref="IsAbort"/>).</summary>
        public uint AbortCode => IsAbort && Data.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(Data) : 0;

        /// <summary>Upload request.</summary>
        public bool IsUploadRequest => Service == CoeService.SdoRequest && (Command & 0xE0) == 0x40;

        /// <summary>Download request.</summary>
        public bool IsDownloadRequest => Service == CoeService.SdoRequest && (Command & 0xE0) == 0x20;
    }

    /// <summary>Decodes a CoE SDO payload (CoE header + SDO); expedited sizes and normal lengths are applied to <see cref="Message.Data"/>.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> coe, out Message? message, out string? error)
    {
        message = null;
        if (coe.Length < 10)
        {
            error = "A CoE SDO message needs 10 bytes.";
            return false;
        }

        var service = (CoeService)(BinaryPrimitives.ReadUInt16LittleEndian(coe) >> 12);
        if (service is not (CoeService.SdoRequest or CoeService.SdoResponse))
        {
            error = $"CoE service {(int)service} is not an SDO.";
            return false;
        }

        var command = coe[2];
        var index = BinaryPrimitives.ReadUInt16LittleEndian(coe[3..]);
        var sub = coe[5];
        var raw = coe[6..];
        byte[] data;
        var ccs = command & 0xE0;
        var expedited = (command & 0x02) != 0;
        var sized = (command & 0x01) != 0;
        var carriesData = (service == CoeService.SdoRequest && ccs == 0x20) || (service == CoeService.SdoResponse && ccs == 0x40);
        if (command == 0x80 || !carriesData)
            data = raw[..4].ToArray();
        else if (expedited)
            data = raw[..(sized ? 4 - ((command >> 2) & 3) : 4)].ToArray();
        else
        {
            var length = BinaryPrimitives.ReadUInt32LittleEndian(raw);
            if (length > raw.Length - 4)
            {
                error = $"SDO announces {length} bytes; {raw.Length - 4} present (segmented transfers are not supported).";
                return false;
            }

            data = raw.Slice(4, (int)length).ToArray();
        }

        message = new Message(service, command, index, sub, data);
        error = null;
        return true;
    }

    /// <summary>Text for common CANopen abort codes.</summary>
    public static string AbortText(uint code) => code switch
    {
        0x05040001 => "unknown command",
        0x06010000 => "unsupported access",
        0x06010001 => "write-only object",
        0x06010002 => "read-only object",
        0x06020000 => "object does not exist",
        0x06070010 => "data type or length mismatch",
        0x06090011 => "sub-index does not exist",
        0x06090030 => "value out of range",
        0x08000000 => "general error",
        0x08000022 => "not allowed in the current state",
        _ => $"0x{code:X8}",
    };
}

/// <summary>Text for AL status codes (ETG.1000.6 table 11).</summary>
public static class EcatAlStatus
{
    /// <summary>Describes an AL status code.</summary>
    public static string Describe(ushort code) => code switch
    {
        0x0000 => "no error",
        0x0001 => "unspecified error",
        0x0011 => "invalid requested state change",
        0x0012 => "unknown requested state",
        0x0013 => "bootstrap not supported",
        0x0016 => "invalid mailbox configuration",
        0x001A => "synchronization error",
        0x001B => "sync manager watchdog",
        0x001D => "invalid output configuration",
        0x001E => "invalid input configuration",
        0x0024 => "invalid output FMMU configuration",
        _ => $"0x{code:X4}",
    };

    /// <summary>Formats a state.</summary>
    public static string Name(EcatState state) => state switch
    {
        EcatState.Init => "INIT",
        EcatState.PreOp => "PRE-OP",
        EcatState.Boot => "BOOT",
        EcatState.SafeOp => "SAFE-OP",
        EcatState.Op => "OP",
        _ => "?",
    };
}

/// <summary>ESC register addresses used by the master and the simulator.</summary>
public static class EcatRegister
{
    /// <summary>ESC type.</summary>
    public const ushort Type = 0x0000;
    /// <summary>Configured station address.</summary>
    public const ushort StationAddress = 0x0010;
    /// <summary>Configured station alias.</summary>
    public const ushort StationAlias = 0x0012;
    /// <summary>DL status (link and loop state of the ports).</summary>
    public const ushort DlStatus = 0x0110;
    /// <summary>AL control (requested state, error acknowledge 0x10).</summary>
    public const ushort AlControl = 0x0120;
    /// <summary>AL status (current state, error flag 0x10).</summary>
    public const ushort AlStatus = 0x0130;
    /// <summary>AL status code.</summary>
    public const ushort AlStatusCode = 0x0134;
    /// <summary>SII EEPROM control/status.</summary>
    public const ushort SiiControl = 0x0502;
    /// <summary>SII EEPROM word address.</summary>
    public const ushort SiiAddress = 0x0504;
    /// <summary>SII EEPROM data.</summary>
    public const ushort SiiData = 0x0508;
    /// <summary>First FMMU (16 bytes each).</summary>
    public const ushort Fmmu0 = 0x0600;
    /// <summary>First SyncManager (8 bytes each).</summary>
    public const ushort Sm0 = 0x0800;
    /// <summary>DC system time.</summary>
    public const ushort DcSystemTime = 0x0910;
}

/// <summary>SII (slave information interface) EEPROM layout and category parsing.</summary>
public static class EcatSii
{
    /// <summary>Word address of the vendor ID.</summary>
    public const int VendorId = 0x08;
    /// <summary>Word address of the product code.</summary>
    public const int ProductCode = 0x0A;
    /// <summary>Word address of the revision number.</summary>
    public const int Revision = 0x0C;
    /// <summary>Word address of the serial number.</summary>
    public const int Serial = 0x0E;
    /// <summary>Word address of the standard receive mailbox offset (followed by size, send offset, send size, protocols).</summary>
    public const int MailboxConfig = 0x18;
    /// <summary>Word address of the first category.</summary>
    public const int FirstCategory = 0x40;

    /// <summary>Category types.</summary>
    public const ushort Strings = 10, General = 30, Fmmu = 40, SyncManager = 41, TxPdo = 50, RxPdo = 51, End = 0xFFFF;

    /// <summary>Builds an SII image: identity, mailbox configuration and the given categories.</summary>
    public static byte[] Build(uint vendor, uint product, uint revision, uint serial, (ushort RxOffset, ushort RxSize, ushort TxOffset, ushort TxSize, ushort Protocols) mailbox,
        IEnumerable<(ushort Type, byte[] Data)> categories)
    {
        var image = new List<byte>(new byte[FirstCategory * 2]);
        void Word(int at, ushort v)
        {
            image[at * 2] = (byte)v;
            image[(at * 2) + 1] = (byte)(v >> 8);
        }

        void DWord(int at, uint v)
        {
            Word(at, (ushort)v);
            Word(at + 1, (ushort)(v >> 16));
        }

        DWord(VendorId, vendor);
        DWord(ProductCode, product);
        DWord(Revision, revision);
        DWord(Serial, serial);
        Word(MailboxConfig, mailbox.RxOffset);
        Word(MailboxConfig + 1, mailbox.RxSize);
        Word(MailboxConfig + 2, mailbox.TxOffset);
        Word(MailboxConfig + 3, mailbox.TxSize);
        Word(MailboxConfig + 4, mailbox.Protocols);
        foreach (var (type, data) in categories)
        {
            var padded = data.Length % 2 == 0 ? data : [.. data, 0];
            image.AddRange([(byte)type, (byte)(type >> 8), (byte)(padded.Length / 2), (byte)((padded.Length / 2) >> 8)]);
            image.AddRange(padded);
        }

        image.AddRange([0xFF, 0xFF, 0, 0]);
        return [.. image];
    }

    /// <summary>A strings category from the given strings (index 1 = first).</summary>
    public static byte[] StringsCategory(params string[] strings)
    {
        var b = new List<byte> { (byte)strings.Length };
        foreach (var s in strings)
        {
            var bytes = Encoding.ASCII.GetBytes(s);
            b.Add((byte)bytes.Length);
            b.AddRange(bytes);
        }

        return [.. b];
    }

    /// <summary>A general category naming string <paramref name="nameIndex"/>, with CoE details when the slave has a mailbox.</summary>
    public static byte[] GeneralCategory(byte nameIndex, bool coe)
    {
        var b = new byte[32];
        b[3] = nameIndex;
        b[5] = (byte)(coe ? 0x01 : 0);   // CoE details: SDO set
        return b;
    }

    /// <summary>A SyncManager category entry list.</summary>
    public static byte[] SyncManagerCategory(params (ushort Start, ushort Length, byte Control, byte Type)[] sms)
    {
        var b = new byte[sms.Length * 8];
        for (var i = 0; i < sms.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(i * 8), sms[i].Start);
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan((i * 8) + 2), sms[i].Length);
            b[(i * 8) + 4] = sms[i].Control;
            b[(i * 8) + 6] = 1;
            b[(i * 8) + 7] = sms[i].Type;
        }

        return b;
    }

    /// <summary>A PDO category with one PDO of the given entries (index, sub-index, bit length).</summary>
    public static byte[] PdoCategory(ushort pdoIndex, byte syncManager, params (ushort Index, byte Sub, byte Bits)[] entries)
    {
        var b = new byte[8 + (entries.Length * 8)];
        BinaryPrimitives.WriteUInt16LittleEndian(b, pdoIndex);
        b[2] = (byte)entries.Length;
        b[3] = syncManager;
        for (var i = 0; i < entries.Length; i++)
        {
            var at = 8 + (i * 8);
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at), entries[i].Index);
            b[at + 2] = entries[i].Sub;
            b[at + 5] = entries[i].Bits;
        }

        return b;
    }

    /// <summary>What the master needs from an SII image.</summary>
    /// <param name="Vendor">Vendor ID.</param>
    /// <param name="Product">Product code.</param>
    /// <param name="Revision">Revision.</param>
    /// <param name="Serial">Serial number.</param>
    /// <param name="Name">Device name from the general category.</param>
    /// <param name="Mailbox">Mailbox configuration (sizes 0 = no mailbox).</param>
    /// <param name="SyncManagers">SyncManager defaults.</param>
    /// <param name="OutputBits">Output process data bits (RxPDOs).</param>
    /// <param name="InputBits">Input process data bits (TxPDOs).</param>
    public sealed record Info(uint Vendor, uint Product, uint Revision, uint Serial, string Name,
        (ushort RxOffset, ushort RxSize, ushort TxOffset, ushort TxSize, ushort Protocols) Mailbox,
        IReadOnlyList<(ushort Start, ushort Length, byte Control, byte Type)> SyncManagers, int OutputBits, int InputBits);

    /// <summary>Parses an SII image read word by word.</summary>
    public static Info Parse(ReadOnlySpan<byte> image)
    {
        if (image.Length < FirstCategory * 2) throw new ProtocolException($"An SII image needs at least {FirstCategory * 2} bytes.");
        ushort W(ReadOnlySpan<byte> img, int word) => BinaryPrimitives.ReadUInt16LittleEndian(img[(word * 2)..]);
        uint D(ReadOnlySpan<byte> img, int word) => BinaryPrimitives.ReadUInt32LittleEndian(img[(word * 2)..]);
        var strings = new List<string>();
        var nameIndex = 0;
        var sms = new List<(ushort, ushort, byte, byte)>();
        int outBits = 0, inBits = 0;
        var at = FirstCategory * 2;
        while (at + 4 <= image.Length)
        {
            var type = BinaryPrimitives.ReadUInt16LittleEndian(image[at..]);
            if (type == End) break;
            var words = BinaryPrimitives.ReadUInt16LittleEndian(image[(at + 2)..]);
            var start = at + 4;
            if (start + (words * 2) > image.Length) throw new ProtocolException($"SII category {type} runs past the image.");
            var data = image.Slice(start, words * 2);
            switch (type)
            {
                case Strings when data.Length > 0:
                    var pos = 1;
                    for (var i = 0; i < data[0] && pos < data.Length; i++)
                    {
                        var len = data[pos];
                        if (pos + 1 + len > data.Length) break;
                        strings.Add(Encoding.ASCII.GetString(data.Slice(pos + 1, len)));
                        pos += 1 + len;
                    }

                    break;
                case General when data.Length >= 4:
                    nameIndex = data[3];
                    break;
                case SyncManager:
                    for (var i = 0; i + 8 <= data.Length; i += 8)
                        sms.Add((BinaryPrimitives.ReadUInt16LittleEndian(data[i..]), BinaryPrimitives.ReadUInt16LittleEndian(data[(i + 2)..]), data[i + 4], data[i + 7]));
                    break;
                case TxPdo or RxPdo:
                    var p = 0;
                    while (p + 8 <= data.Length)
                    {
                        var entries = data[p + 2];
                        if (p + 8 + (entries * 8) > data.Length) break;
                        for (var e = 0; e < entries; e++)
                        {
                            var bits = data[p + 8 + (e * 8) + 5];
                            if (type == TxPdo) inBits += bits;
                            else outBits += bits;
                        }

                        p += 8 + (entries * 8);
                    }

                    break;
            }

            at = start + (words * 2);
        }

        var name = nameIndex >= 1 && nameIndex <= strings.Count ? strings[nameIndex - 1] : "";
        return new Info(D(image, VendorId), D(image, ProductCode), D(image, Revision), D(image, Serial), name,
            (W(image, MailboxConfig), W(image, MailboxConfig + 1), W(image, MailboxConfig + 2), W(image, MailboxConfig + 3), W(image, MailboxConfig + 4)),
            sms, outBits, inBits);
    }
}
