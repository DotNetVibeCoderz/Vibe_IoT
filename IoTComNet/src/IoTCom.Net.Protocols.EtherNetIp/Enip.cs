using System.Buffers.Binary;
using System.Net;
using System.Text;

#pragma warning disable CS1591 // Names follow the ODVA EtherNet/IP specification.

namespace IoTCom.Net.Protocols.EtherNetIp;

/// <summary>Encapsulation commands.</summary>
public enum EnipCommand : ushort
{
    Nop = 0x0000, ListServices = 0x0004, ListIdentity = 0x0063, ListInterfaces = 0x0064, RegisterSession = 0x0065,
    UnregisterSession = 0x0066, SendRRData = 0x006F, SendUnitData = 0x0070,
}

/// <summary>Encapsulation status codes.</summary>
public enum EnipStatus : uint
{
    Success = 0x00, InvalidCommand = 0x01, InsufficientMemory = 0x02, IncorrectData = 0x03, InvalidSessionHandle = 0x64,
    InvalidLength = 0x65, UnsupportedProtocolRevision = 0x69,
}

/// <summary>Common Packet Format item types.</summary>
public enum CpfItemType : ushort
{
    NullAddress = 0x0000, ListIdentity = 0x000C, ConnectedAddress = 0x00A1, ConnectedData = 0x00B1, UnconnectedData = 0x00B2,
    ListServices = 0x0100, SockaddrOtoT = 0x8000, SockaddrTtoO = 0x8001, SequencedAddress = 0x8002,
}

/// <summary>One Common Packet Format item.</summary>
public sealed record CpfItem(CpfItemType Type, byte[] Data);

/// <summary>An encapsulation packet: 24-byte header (little-endian) and command data (sans-I/O).</summary>
public sealed record EnipPacket
{
    /// <summary>Header size.</summary>
    public const int HeaderSize = 24;

    /// <summary>Well-known TCP and UDP port.</summary>
    public const int Port = 44818;

    public EnipCommand Command { get; init; }
    public uint Session { get; init; }
    public EnipStatus Status { get; init; }
    public byte[] SenderContext { get; init; } = new byte[8];
    public uint Options { get; init; }
    public byte[] Data { get; init; } = [];

    /// <summary>Encodes header and data.</summary>
    public byte[] Encode()
    {
        var b = new byte[HeaderSize + Data.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)Command);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), (ushort)Data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), Session);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), (uint)Status);
        SenderContext.AsSpan(0, Math.Min(8, SenderContext.Length)).CopyTo(b.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(20), Options);
        Data.CopyTo(b, HeaderSize);
        return b;
    }

    /// <summary>Length of the packet starting at <paramref name="b"/> (header + data), or -1 when the header is incomplete.</summary>
    public static int FrameLength(ReadOnlySpan<byte> b) => b.Length < HeaderSize ? -1 : HeaderSize + BinaryPrimitives.ReadUInt16LittleEndian(b[2..]);

    /// <summary>Decodes one packet (exactly one).</summary>
    public static EnipPacket Decode(ReadOnlySpan<byte> b)
    {
        if (b.Length < HeaderSize) throw new ProtocolException("Encapsulation packet shorter than the 24-byte header.");
        var len = BinaryPrimitives.ReadUInt16LittleEndian(b[2..]);
        if (b.Length != HeaderSize + len) throw new ProtocolException($"Encapsulation length {len} does not match the {b.Length - HeaderSize} data byte(s).");
        return new EnipPacket
        {
            Command = (EnipCommand)BinaryPrimitives.ReadUInt16LittleEndian(b),
            Session = BinaryPrimitives.ReadUInt32LittleEndian(b[4..]),
            Status = (EnipStatus)BinaryPrimitives.ReadUInt32LittleEndian(b[8..]),
            SenderContext = b.Slice(12, 8).ToArray(),
            Options = BinaryPrimitives.ReadUInt32LittleEndian(b[20..]),
            Data = b[HeaderSize..].ToArray(),
        };
    }

    // ---- Common Packet Format ------------------------------------------------------------------------------------------

    /// <summary>Encodes a CPF item list.</summary>
    public static byte[] Cpf(params CpfItem[] items)
    {
        var b = new List<byte> { (byte)items.Length, (byte)(items.Length >> 8) };
        foreach (var i in items)
        {
            b.Add((byte)i.Type);
            b.Add((byte)((ushort)i.Type >> 8));
            b.Add((byte)i.Data.Length);
            b.Add((byte)(i.Data.Length >> 8));
            b.AddRange(i.Data);
        }

        return [.. b];
    }

    /// <summary>Decodes a CPF item list.</summary>
    public static IReadOnlyList<CpfItem> DecodeCpf(ReadOnlySpan<byte> b)
    {
        if (b.Length < 2) throw new ProtocolException("CPF item count missing.");
        var count = BinaryPrimitives.ReadUInt16LittleEndian(b);
        var items = new List<CpfItem>();
        var o = 2;
        for (var i = 0; i < count; i++)
        {
            if (o + 4 > b.Length) throw new ProtocolException("CPF item header truncated.");
            var type = (CpfItemType)BinaryPrimitives.ReadUInt16LittleEndian(b[o..]);
            var len = BinaryPrimitives.ReadUInt16LittleEndian(b[(o + 2)..]);
            if (o + 4 + len > b.Length) throw new ProtocolException($"CPF item {type} declares {len} byte(s) but fewer remain.");
            items.Add(new CpfItem(type, b.Slice(o + 4, len).ToArray()));
            o += 4 + len;
        }

        if (o != b.Length) throw new ProtocolException("Trailing bytes after the CPF items.");
        return items;
    }

    /// <summary>SendRRData data: interface handle 0, timeout, and the null-address/unconnected-data pair carrying a CIP message.</summary>
    public static byte[] SendRRData(ReadOnlySpan<byte> cip, ushort timeoutSeconds = 10)
    {
        var cpf = Cpf(new CpfItem(CpfItemType.NullAddress, []), new CpfItem(CpfItemType.UnconnectedData, cip.ToArray()));
        var b = new byte[6 + cpf.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), timeoutSeconds);
        cpf.CopyTo(b, 6);
        return b;
    }

    /// <summary>The CIP message inside SendRRData data.</summary>
    public static byte[] UnconnectedPayload(ReadOnlySpan<byte> sendRRData)
    {
        if (sendRRData.Length < 6) throw new ProtocolException("SendRRData shorter than its interface handle and timeout.");
        var items = DecodeCpf(sendRRData[6..]);
        return items.FirstOrDefault(i => i.Type == CpfItemType.UnconnectedData)?.Data ?? throw new ProtocolException("SendRRData without an unconnected data item.");
    }

    /// <summary>RegisterSession data (protocol version 1, options 0).</summary>
    public static byte[] RegisterSessionData { get; } = [1, 0, 0, 0];

    /// <summary>One-line description.</summary>
    public string Describe()
    {
        var head = $"{Command}" + (Session != 0 ? $" session 0x{Session:X8}" : "") + (Status != EnipStatus.Success ? $" status {Status}" : "");
        try
        {
            switch (Command)
            {
                case EnipCommand.SendRRData when Data.Length > 6:
                    var cip = UnconnectedPayload(Data);
                    return head + ": " + ((cip[0] & 0x80) != 0 ? CipResponse.Decode(cip).ToString() : CipRequest.Decode(cip).ToString());
                case EnipCommand.ListIdentity when Data.Length > 2:
                    return head + ": " + EnipIdentity.Decode(DecodeCpf(Data)[0].Data).ProductName;
                default:
                    return head;
            }
        }
        catch (ProtocolException ex)
        {
            return head + " (" + ex.Message + ")";
        }
    }

    /// <summary>Frame lane fields.</summary>
    public static IReadOnlyList<FrameField> DescribeFields(ReadOnlySpan<byte> b)
    {
        var fields = new List<FrameField>();
        EnipPacket p;
        try
        {
            p = Decode(b);
        }
        catch (ProtocolException ex)
        {
            fields.Add(new FrameField("Invalid", 0, b.Length, FrameFieldKind.Error, ex.Message));
            return fields;
        }

        fields.Add(new FrameField("Command", 0, 2, FrameFieldKind.Function, p.Command.ToString()));
        fields.Add(new FrameField("Length", 2, 2, FrameFieldKind.Length, p.Data.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        fields.Add(new FrameField("Session", 4, 4, FrameFieldKind.Address, $"0x{p.Session:X8}"));
        fields.Add(new FrameField("Status", 8, 4, p.Status == EnipStatus.Success ? FrameFieldKind.Header : FrameFieldKind.Error, p.Status.ToString()));
        fields.Add(new FrameField("Sender context", 12, 8, FrameFieldKind.Header, Convert.ToHexString(p.SenderContext)));
        fields.Add(new FrameField("Options", 20, 4, FrameFieldKind.Header));
        if (p.Command == EnipCommand.SendRRData && p.Data.Length >= 6)
        {
            fields.Add(new FrameField("Interface/timeout", 24, 6, FrameFieldKind.Header));
            try
            {
                var cip = UnconnectedPayload(p.Data);
                var start = b.Length - cip.Length;
                fields.Add(new FrameField("CPF items", 30, start - 30, FrameFieldKind.Length, "null address + unconnected data"));
                var reply = (cip[0] & 0x80) != 0;
                fields.Add(new FrameField("CIP", start, cip.Length, reply ? FrameFieldKind.Data : FrameFieldKind.Function, reply ? CipResponse.Decode(cip).ToString() : CipRequest.Decode(cip).ToString()));
            }
            catch (ProtocolException ex)
            {
                fields.Add(new FrameField("CPF", 30, b.Length - 30, FrameFieldKind.Error, ex.Message));
            }
        }
        else if (p.Data.Length > 0)
        {
            fields.Add(new FrameField("Data", 24, p.Data.Length, FrameFieldKind.Data));
        }

        return fields;
    }
}

/// <summary>The CIP Identity item of a ListIdentity reply.</summary>
public sealed record EnipIdentity
{
    public ushort VendorId { get; init; } = 0xFFFF;
    public ushort DeviceType { get; init; } = 0x0E;   // programmable logic controller
    public ushort ProductCode { get; init; } = 1;
    public byte RevisionMajor { get; init; } = 1;
    public byte RevisionMinor { get; init; }
    public ushort Status { get; init; }
    public uint SerialNumber { get; init; }
    public string ProductName { get; init; } = "";
    public byte State { get; init; } = 3;
    public IPEndPoint? Address { get; init; }

    /// <summary>Encodes the identity item data.</summary>
    public byte[] Encode()
    {
        var name = Encoding.ASCII.GetBytes(ProductName);
        var b = new byte[2 + 16 + 14 + 1 + name.Length + 1];
        BinaryPrimitives.WriteUInt16LittleEndian(b, 1);   // encapsulation protocol version
        BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(2), 2);   // AF_INET (big-endian socket address)
        var ep = Address ?? new IPEndPoint(IPAddress.Any, EnipPacket.Port);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4), (ushort)ep.Port);
        ep.Address.MapToIPv4().GetAddressBytes().CopyTo(b, 6);
        var o = 18;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o), VendorId);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o + 2), DeviceType);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o + 4), ProductCode);
        b[o + 6] = RevisionMajor;
        b[o + 7] = RevisionMinor;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o + 8), Status);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o + 10), SerialNumber);
        b[o + 14] = (byte)name.Length;
        name.CopyTo(b, o + 15);
        b[^1] = State;
        return b;
    }

    /// <summary>Decodes the identity item data.</summary>
    public static EnipIdentity Decode(ReadOnlySpan<byte> b)
    {
        if (b.Length < 33) throw new ProtocolException("Identity item shorter than 33 bytes.");
        var nameLen = b[32];
        if (b.Length < 33 + nameLen + 1) throw new ProtocolException("Identity product name truncated.");
        return new EnipIdentity
        {
            Address = new IPEndPoint(new IPAddress(b.Slice(6, 4)), BinaryPrimitives.ReadUInt16BigEndian(b[4..])),
            VendorId = BinaryPrimitives.ReadUInt16LittleEndian(b[18..]),
            DeviceType = BinaryPrimitives.ReadUInt16LittleEndian(b[20..]),
            ProductCode = BinaryPrimitives.ReadUInt16LittleEndian(b[22..]),
            RevisionMajor = b[24],
            RevisionMinor = b[25],
            Status = BinaryPrimitives.ReadUInt16LittleEndian(b[26..]),
            SerialNumber = BinaryPrimitives.ReadUInt32LittleEndian(b[28..]),
            ProductName = Encoding.ASCII.GetString(b.Slice(33, nameLen)),
            State = b[33 + nameLen],
        };
    }

    /// <summary>The Identity object's Get_Attributes_All reply data (attributes 1–7).</summary>
    public byte[] AttributesAll() => Encode()[18..^1];

    /// <inheritdoc />
    public override string ToString() => $"{ProductName} (vendor {VendorId}, type 0x{DeviceType:X2}, product {ProductCode}, rev {RevisionMajor}.{RevisionMinor:D3}, serial {SerialNumber:X8}){(Address is null ? "" : $" at {Address}")}";
}
