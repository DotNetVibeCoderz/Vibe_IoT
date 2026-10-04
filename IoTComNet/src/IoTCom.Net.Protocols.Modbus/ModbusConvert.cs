using System.Buffers.Binary;

namespace IoTCom.Net.Protocols.Modbus;

/// <summary>Converts multi-register values (32/64-bit integers, floats, strings) honouring <see cref="ModbusWordOrder"/>.</summary>
public static class ModbusConvert
{
    /// <summary>Reads a 32-bit unsigned integer from two registers.</summary>
    public static uint ToUInt32(ReadOnlySpan<ushort> registers, ModbusWordOrder order = ModbusWordOrder.BigEndian)
    {
        Span<byte> b = stackalloc byte[4];
        ToBytes(registers[..2], b, order);
        return BinaryPrimitives.ReadUInt32BigEndian(b);
    }

    /// <summary>Reads a 32-bit signed integer from two registers.</summary>
    public static int ToInt32(ReadOnlySpan<ushort> registers, ModbusWordOrder order = ModbusWordOrder.BigEndian) => unchecked((int)ToUInt32(registers, order));

    /// <summary>Reads an IEEE-754 single from two registers.</summary>
    public static float ToSingle(ReadOnlySpan<ushort> registers, ModbusWordOrder order = ModbusWordOrder.BigEndian) => BitConverter.Int32BitsToSingle(ToInt32(registers, order));

    /// <summary>Reads a 64-bit unsigned integer from four registers.</summary>
    public static ulong ToUInt64(ReadOnlySpan<ushort> registers, ModbusWordOrder order = ModbusWordOrder.BigEndian)
    {
        Span<byte> b = stackalloc byte[8];
        ToBytes(registers[..4], b, order);
        return BinaryPrimitives.ReadUInt64BigEndian(b);
    }

    /// <summary>Reads an IEEE-754 double from four registers.</summary>
    public static double ToDouble(ReadOnlySpan<ushort> registers, ModbusWordOrder order = ModbusWordOrder.BigEndian) => BitConverter.Int64BitsToDouble(unchecked((long)ToUInt64(registers, order)));

    /// <summary>Writes a 32-bit unsigned integer into two registers.</summary>
    public static void FromUInt32(uint value, Span<ushort> registers, ModbusWordOrder order = ModbusWordOrder.BigEndian)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, value);
        FromBytes(b, registers[..2], order);
    }

    /// <summary>Writes a 32-bit signed integer into two registers.</summary>
    public static void FromInt32(int value, Span<ushort> registers, ModbusWordOrder order = ModbusWordOrder.BigEndian) => FromUInt32(unchecked((uint)value), registers, order);

    /// <summary>Writes an IEEE-754 single into two registers.</summary>
    public static void FromSingle(float value, Span<ushort> registers, ModbusWordOrder order = ModbusWordOrder.BigEndian) => FromInt32(BitConverter.SingleToInt32Bits(value), registers, order);

    /// <summary>Writes a 64-bit unsigned integer into four registers.</summary>
    public static void FromUInt64(ulong value, Span<ushort> registers, ModbusWordOrder order = ModbusWordOrder.BigEndian)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(b, value);
        FromBytes(b, registers[..4], order);
    }

    /// <summary>Writes an IEEE-754 double into four registers.</summary>
    public static void FromDouble(double value, Span<ushort> registers, ModbusWordOrder order = ModbusWordOrder.BigEndian) => FromUInt64(unchecked((ulong)BitConverter.DoubleToInt64Bits(value)), registers, order);

    /// <summary>Decodes an ASCII string packed two characters per register (high byte first), trimming NULs and spaces.</summary>
    public static string ToAsciiString(ReadOnlySpan<ushort> registers)
    {
        Span<byte> b = registers.Length <= 128 ? stackalloc byte[registers.Length * 2] : new byte[registers.Length * 2];
        for (var i = 0; i < registers.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(b[(i * 2)..], registers[i]);
        return System.Text.Encoding.ASCII.GetString(b).TrimEnd('\0', ' ');
    }

    /// <summary>Converts registers to big-endian bytes after undoing <paramref name="order"/>.</summary>
    public static void ToBytes(ReadOnlySpan<ushort> registers, Span<byte> destination, ModbusWordOrder order)
    {
        var n = registers.Length;
        var wordSwap = order is ModbusWordOrder.WordSwap or ModbusWordOrder.LittleEndian;
        var byteSwap = order is ModbusWordOrder.ByteSwap or ModbusWordOrder.LittleEndian;
        for (var i = 0; i < n; i++)
        {
            var r = registers[wordSwap ? n - 1 - i : i];
            if (byteSwap) r = BinaryPrimitives.ReverseEndianness(r);
            BinaryPrimitives.WriteUInt16BigEndian(destination[(i * 2)..], r);
        }
    }

    /// <summary>Converts big-endian bytes to registers applying <paramref name="order"/>.</summary>
    public static void FromBytes(ReadOnlySpan<byte> source, Span<ushort> registers, ModbusWordOrder order)
    {
        var n = registers.Length;
        var wordSwap = order is ModbusWordOrder.WordSwap or ModbusWordOrder.LittleEndian;
        var byteSwap = order is ModbusWordOrder.ByteSwap or ModbusWordOrder.LittleEndian;
        for (var i = 0; i < n; i++)
        {
            var r = BinaryPrimitives.ReadUInt16BigEndian(source[(i * 2)..]);
            if (byteSwap) r = BinaryPrimitives.ReverseEndianness(r);
            registers[wordSwap ? n - 1 - i : i] = r;
        }
    }
}
