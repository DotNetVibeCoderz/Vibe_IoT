using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace IoTCom.Net.Protocols.Dmx;

/// <summary>sACN (ANSI E1.31-2018) data packet codec.</summary>
public static class SacnPacket
{
    /// <summary>UDP port 5568.</summary>
    public const int Port = 5568;
    /// <summary>Header length before the DMX slots (start code included).</summary>
    public const int HeaderLength = 126;
    /// <summary>Default priority.</summary>
    public const byte DefaultPriority = 100;

    private static ReadOnlySpan<byte> AcnId => "ASC-E1.17\0\0\0"u8;

    /// <summary>Multicast group for <paramref name="universe"/>: 239.255.{hi}.{lo}.</summary>
    public static IPAddress MulticastAddress(int universe)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(universe, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(universe, 63999);
        return new IPAddress([239, 255, (byte)(universe >> 8), (byte)universe]);
    }

    /// <summary>Encodes a data packet. Returns bytes written (126 + slots).</summary>
    public static int WriteData(Span<byte> d, Guid cid, string sourceName, int universe, ReadOnlySpan<byte> slots, byte sequence, byte priority = DefaultPriority, bool streamTerminated = false, ushort syncAddress = 0)
    {
        if (slots.Length > 512) throw new ArgumentOutOfRangeException(nameof(slots), "Max 512 slots.");
        var total = HeaderLength + slots.Length;
        d[..HeaderLength].Clear();
        // Root layer
        BinaryPrimitives.WriteUInt16BigEndian(d, 0x0010);
        BinaryPrimitives.WriteUInt16BigEndian(d[2..], 0x0000);
        AcnId.CopyTo(d[4..]);
        BinaryPrimitives.WriteUInt16BigEndian(d[16..], (ushort)(0x7000 | (total - 16)));
        BinaryPrimitives.WriteUInt32BigEndian(d[18..], 0x00000004);
        cid.TryWriteBytes(d.Slice(22, 16), bigEndian: true, out _);
        // Framing layer
        BinaryPrimitives.WriteUInt16BigEndian(d[38..], (ushort)(0x7000 | (total - 38)));
        BinaryPrimitives.WriteUInt32BigEndian(d[40..], 0x00000002);
        var name = d.Slice(44, 64);
        Encoding.UTF8.GetBytes(sourceName.AsSpan(0, Math.Min(sourceName.Length, 63)), name);
        d[108] = priority;
        BinaryPrimitives.WriteUInt16BigEndian(d[109..], syncAddress);
        d[111] = sequence;
        d[112] = (byte)(streamTerminated ? 0x40 : 0x00);
        BinaryPrimitives.WriteUInt16BigEndian(d[113..], (ushort)universe);
        // DMP layer
        BinaryPrimitives.WriteUInt16BigEndian(d[115..], (ushort)(0x7000 | (total - 115)));
        d[117] = 0x02;
        d[118] = 0xA1;
        BinaryPrimitives.WriteUInt16BigEndian(d[119..], 0x0000);
        BinaryPrimitives.WriteUInt16BigEndian(d[121..], 0x0001);
        BinaryPrimitives.WriteUInt16BigEndian(d[123..], (ushort)(slots.Length + 1));
        d[125] = 0x00; // DMX start code
        slots.CopyTo(d[HeaderLength..]);
        return total;
    }

    /// <summary>Decodes a data packet with start code 0.</summary>
    public static bool TryReadData(ReadOnlySpan<byte> p, out int universe, out byte sequence, out byte priority, out string sourceName, out ReadOnlySpan<byte> slots, out bool terminated)
    {
        universe = 0; sequence = 0; priority = 0; sourceName = string.Empty; slots = default; terminated = false;
        if (p.Length < HeaderLength || !p.Slice(4, 12).SequenceEqual(AcnId)) return false;
        if (BinaryPrimitives.ReadUInt32BigEndian(p[18..]) != 4 || BinaryPrimitives.ReadUInt32BigEndian(p[40..]) != 2 || p[117] != 0x02) return false;
        var count = BinaryPrimitives.ReadUInt16BigEndian(p[123..]);
        if (count < 1 || p.Length < HeaderLength - 1 + count || p[125] != 0) return false;
        universe = BinaryPrimitives.ReadUInt16BigEndian(p[113..]);
        sequence = p[111];
        priority = p[108];
        terminated = (p[112] & 0x40) != 0;
        var name = p.Slice(44, 64);
        var end = name.IndexOf((byte)0);
        sourceName = Encoding.UTF8.GetString(end < 0 ? name : name[..end]);
        slots = p.Slice(HeaderLength, count - 1);
        return true;
    }

    /// <summary>E1.31 6.7.2 sequence check: true when <paramref name="received"/> should be discarded as out of order.</summary>
    public static bool IsOutOfOrder(byte last, byte received)
    {
        var diff = (sbyte)(received - last);
        return diff is <= 0 and > -20;
    }
}
