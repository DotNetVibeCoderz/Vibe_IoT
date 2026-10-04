using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace IoTCom.Net.Protocols.Dmx;

/// <summary>Art-Net op codes (little-endian on the wire).</summary>
public enum ArtNetOpCode : ushort
{
    /// <summary>Discovery request.</summary>
    Poll = 0x2000,
    /// <summary>Discovery reply.</summary>
    PollReply = 0x2100,
    /// <summary>DMX data.</summary>
    Dmx = 0x5000,
    /// <summary>Synchronous output trigger.</summary>
    Sync = 0x5200,
}

/// <summary>Information from an ArtPollReply.</summary>
public sealed record ArtNetNodeInfo(IPAddress Address, string ShortName, string LongName, string NodeReport, ushort EstaManufacturer, ushort Oem, int NumPorts, byte NetSwitch, byte SubSwitch, byte[] SwOut);

/// <summary>Art-Net 4 packet codec (allocation-free encoders, tolerant decoders).</summary>
public static class ArtNetPacket
{
    /// <summary>UDP port 6454.</summary>
    public const int Port = 0x1936;
    /// <summary>Protocol revision 14.</summary>
    public const ushort ProtocolVersion = 14;
    /// <summary>Header ID.</summary>
    public static ReadOnlySpan<byte> Id => "Art-Net\0"u8;
    /// <summary>ArtDmx header length.</summary>
    public const int DmxHeaderLength = 18;
    /// <summary>ArtPollReply length.</summary>
    public const int PollReplyLength = 239;

    /// <summary>Encodes ArtDmx. Returns bytes written (18 + even-padded length).</summary>
    /// <param name="destination">At least 18 + 512 bytes.</param>
    /// <param name="portAddress">15-bit port-address: Net(7) | SubNet(4) | Universe(4).</param>
    /// <param name="data">2..512 channel values (padded to an even length).</param>
    /// <param name="sequence">1..255, 0 disables sequencing.</param>
    /// <param name="physical">Physical input port (informational).</param>
    public static int WriteDmx(Span<byte> destination, int portAddress, ReadOnlySpan<byte> data, byte sequence, byte physical = 0)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(portAddress, 0x7FFF);
        if (data.Length > 512) throw new ArgumentOutOfRangeException(nameof(data), "Max 512 channels.");
        var length = Math.Max(2, data.Length + (data.Length & 1));
        Id.CopyTo(destination);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[8..], (ushort)ArtNetOpCode.Dmx);
        BinaryPrimitives.WriteUInt16BigEndian(destination[10..], ProtocolVersion);
        destination[12] = sequence;
        destination[13] = physical;
        destination[14] = (byte)(portAddress & 0xFF); // SubUni
        destination[15] = (byte)(portAddress >> 8);   // Net
        BinaryPrimitives.WriteUInt16BigEndian(destination[16..], (ushort)length);
        var payload = destination.Slice(DmxHeaderLength, length);
        payload.Clear();
        data.CopyTo(payload);
        return DmxHeaderLength + length;
    }

    /// <summary>Encodes ArtPoll (14 bytes).</summary>
    public static int WritePoll(Span<byte> destination, byte flags = 0x02)
    {
        Id.CopyTo(destination);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[8..], (ushort)ArtNetOpCode.Poll);
        BinaryPrimitives.WriteUInt16BigEndian(destination[10..], ProtocolVersion);
        destination[12] = flags;    // bit1: send ArtPollReply on change
        destination[13] = 0x10;     // diag priority low
        return 14;
    }

    /// <summary>Encodes ArtSync (14 bytes).</summary>
    public static int WriteSync(Span<byte> destination)
    {
        Id.CopyTo(destination);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[8..], (ushort)ArtNetOpCode.Sync);
        BinaryPrimitives.WriteUInt16BigEndian(destination[10..], ProtocolVersion);
        destination[12] = 0;
        destination[13] = 0;
        return 14;
    }

    /// <summary>Encodes an ArtPollReply (239 bytes).</summary>
    public static int WritePollReply(Span<byte> d, IPAddress address, string shortName, string longName, int portAddress, int numPorts = 1, string nodeReport = "#0001 [0000] IoTCom.Net OK")
    {
        d[..PollReplyLength].Clear();
        Id.CopyTo(d);
        BinaryPrimitives.WriteUInt16LittleEndian(d[8..], (ushort)ArtNetOpCode.PollReply);
        var ip = address.MapToIPv4().GetAddressBytes();
        ip.CopyTo(d[10..]);
        BinaryPrimitives.WriteUInt16LittleEndian(d[14..], Port);
        BinaryPrimitives.WriteUInt16BigEndian(d[16..], 1);       // firmware version
        d[18] = (byte)((portAddress >> 8) & 0x7F);              // NetSwitch
        d[19] = (byte)((portAddress >> 4) & 0x0F);              // SubSwitch
        BinaryPrimitives.WriteUInt16BigEndian(d[20..], 0x00FF); // OEM: unknown
        d[23] = 0xD0;                                            // Status1: indicators normal, port-address by network
        BinaryPrimitives.WriteUInt16LittleEndian(d[24..], 0x7FF0); // ESTA: prototyping id
        Ascii(d.Slice(26, 18), shortName);
        Ascii(d.Slice(44, 64), longName);
        Ascii(d.Slice(108, 64), nodeReport);
        BinaryPrimitives.WriteUInt16BigEndian(d[172..], (ushort)Math.Clamp(numPorts, 0, 4));
        for (var i = 0; i < Math.Clamp(numPorts, 0, 4); i++)
        {
            d[174 + i] = 0x80;                                   // PortTypes: output, DMX512
            d[182 + i] = 0x80;                                   // GoodOutput: data transmitted
            d[190 + i] = (byte)((portAddress + i) & 0x0F);      // SwOut
        }
        d[211] = 0x01;                                           // Style: StNode
        return PollReplyLength;

        static void Ascii(Span<byte> field, string text)
        {
            var n = Encoding.ASCII.GetBytes(text.AsSpan(0, Math.Min(text.Length, field.Length - 1)), field);
            field[n..].Clear();
        }
    }

    /// <summary>Returns the op code, or null when the buffer is not an Art-Net packet.</summary>
    public static ArtNetOpCode? GetOpCode(ReadOnlySpan<byte> packet) =>
        packet.Length >= 10 && packet[..8].SequenceEqual(Id) ? (ArtNetOpCode)BinaryPrimitives.ReadUInt16LittleEndian(packet[8..]) : null;

    /// <summary>Decodes ArtDmx.</summary>
    public static bool TryReadDmx(ReadOnlySpan<byte> p, out int portAddress, out byte sequence, out ReadOnlySpan<byte> data)
    {
        portAddress = 0;
        sequence = 0;
        data = default;
        if (GetOpCode(p) != ArtNetOpCode.Dmx || p.Length < DmxHeaderLength) return false;
        var length = BinaryPrimitives.ReadUInt16BigEndian(p[16..]);
        if (length is < 2 or > 512 || p.Length < DmxHeaderLength + length) return false;
        sequence = p[12];
        portAddress = p[14] | ((p[15] & 0x7F) << 8);
        data = p.Slice(DmxHeaderLength, length);
        return true;
    }

    /// <summary>Decodes ArtPollReply.</summary>
    public static bool TryReadPollReply(ReadOnlySpan<byte> p, out ArtNetNodeInfo? info)
    {
        info = null;
        if (GetOpCode(p) != ArtNetOpCode.PollReply || p.Length < 207) return false;
        info = new ArtNetNodeInfo(
            new IPAddress(p.Slice(10, 4)),
            Text(p.Slice(26, 18)), Text(p.Slice(44, 64)), Text(p.Slice(108, 64)),
            BinaryPrimitives.ReadUInt16LittleEndian(p[24..]), BinaryPrimitives.ReadUInt16BigEndian(p[20..]),
            BinaryPrimitives.ReadUInt16BigEndian(p[172..]), p[18], p[19], p.Slice(190, 4).ToArray());
        return true;

        static string Text(ReadOnlySpan<byte> s)
        {
            var end = s.IndexOf((byte)0);
            return Encoding.ASCII.GetString(end < 0 ? s : s[..end]);
        }
    }

    /// <summary>Builds a port-address from Net (0–127), SubNet (0–15) and Universe (0–15).</summary>
    public static int PortAddress(int net, int subNet, int universe) => ((net & 0x7F) << 8) | ((subNet & 0x0F) << 4) | (universe & 0x0F);
}
