using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace IoTCom.Net.Protocols.Modbus;

/// <summary>
/// Encodes request PDUs and decodes response PDUs (function code + data, without unit id / framing).
/// Pure functions: no I/O, no state. Shared by the client, the CLI and the Gallery inspector.
/// </summary>
public static class ModbusPdu
{
    /// <summary>Builds a read request (functions 0x01–0x04).</summary>
    public static byte[] Read(ModbusFunctionCode function, ushort address, ushort count)
    {
        var max = function is ModbusFunctionCode.ReadCoils or ModbusFunctionCode.ReadDiscreteInputs ? ModbusLimits.MaxReadBits : ModbusLimits.MaxReadRegisters;
        ValidateRange(address, count, max);
        var pdu = new byte[5];
        pdu[0] = (byte)function;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), address);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), count);
        return pdu;
    }

    /// <summary>Builds Write Single Coil (0x05).</summary>
    public static byte[] WriteSingleCoil(ushort address, bool value) =>
        [(byte)ModbusFunctionCode.WriteSingleCoil, (byte)(address >> 8), (byte)address, value ? (byte)0xFF : (byte)0x00, 0x00];

    /// <summary>Builds Write Single Register (0x06).</summary>
    public static byte[] WriteSingleRegister(ushort address, ushort value) =>
        [(byte)ModbusFunctionCode.WriteSingleRegister, (byte)(address >> 8), (byte)address, (byte)(value >> 8), (byte)value];

    /// <summary>Builds Write Multiple Coils (0x0F).</summary>
    public static byte[] WriteMultipleCoils(ushort address, ReadOnlySpan<bool> values)
    {
        ValidateRange(address, values.Length, ModbusLimits.MaxWriteBits);
        var byteCount = (values.Length + 7) / 8;
        var pdu = new byte[6 + byteCount];
        pdu[0] = (byte)ModbusFunctionCode.WriteMultipleCoils;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), address);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), (ushort)values.Length);
        pdu[5] = (byte)byteCount;
        PackBits(values, pdu.AsSpan(6));
        return pdu;
    }

    /// <summary>Builds Write Multiple Registers (0x10).</summary>
    public static byte[] WriteMultipleRegisters(ushort address, ReadOnlySpan<ushort> values)
    {
        ValidateRange(address, values.Length, ModbusLimits.MaxWriteRegisters);
        var pdu = new byte[6 + values.Length * 2];
        pdu[0] = (byte)ModbusFunctionCode.WriteMultipleRegisters;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), address);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), (ushort)values.Length);
        pdu[5] = (byte)(values.Length * 2);
        for (var i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(6 + i * 2), values[i]);
        return pdu;
    }

    /// <summary>Builds Mask Write Register (0x16): <c>result = (current AND andMask) OR (orMask AND NOT andMask)</c>.</summary>
    public static byte[] MaskWriteRegister(ushort address, ushort andMask, ushort orMask) =>
        [(byte)ModbusFunctionCode.MaskWriteRegister, (byte)(address >> 8), (byte)address, (byte)(andMask >> 8), (byte)andMask, (byte)(orMask >> 8), (byte)orMask];

    /// <summary>Builds Read/Write Multiple Registers (0x17).</summary>
    public static byte[] ReadWriteMultipleRegisters(ushort readAddress, ushort readCount, ushort writeAddress, ReadOnlySpan<ushort> values)
    {
        ValidateRange(readAddress, readCount, ModbusLimits.MaxReadRegisters);
        ValidateRange(writeAddress, values.Length, ModbusLimits.MaxReadWriteWriteRegisters);
        var pdu = new byte[10 + values.Length * 2];
        pdu[0] = (byte)ModbusFunctionCode.ReadWriteMultipleRegisters;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), readAddress);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), readCount);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(5), writeAddress);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(7), (ushort)values.Length);
        pdu[9] = (byte)(values.Length * 2);
        for (var i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(10 + i * 2), values[i]);
        return pdu;
    }

    /// <summary>Builds Read Device Identification (0x2B/0x0E) for basic objects (category 1).</summary>
    public static byte[] ReadDeviceIdentification(byte readDeviceIdCode = 0x01, byte objectId = 0x00) =>
        [(byte)ModbusFunctionCode.EncapsulatedInterface, 0x0E, readDeviceIdCode, objectId];

    /// <summary>Throws <see cref="ModbusException"/> when <paramref name="response"/> is an exception PDU for <paramref name="function"/>.</summary>
    public static void EnsureSuccess(ReadOnlySpan<byte> response, ModbusFunctionCode function, byte unitId)
    {
        if (response.IsEmpty) throw new ProtocolException("Empty Modbus response.");
        if (response[0] == ((byte)function | 0x80))
        {
            var code = response.Length > 1 ? response[1] : (byte)0;
            throw new ModbusException((ModbusExceptionCode)code, function, unitId);
        }
        if (response[0] != (byte)function)
            throw new ProtocolException($"Unexpected function 0x{response[0]:X2} in response to {function}.");
    }

    /// <summary>Decodes a 0x01/0x02 response into <paramref name="count"/> booleans.</summary>
    public static bool[] ParseBits(ReadOnlySpan<byte> response, int count)
    {
        if (response.Length < 2 || response[1] != (count + 7) / 8 || response.Length < 2 + response[1])
            throw new ProtocolException("Malformed bit read response.");
        var result = new bool[count];
        UnpackBits(response.Slice(2, response[1]), result);
        return result;
    }

    /// <summary>Decodes a 0x03/0x04/0x17 response into <paramref name="count"/> registers.</summary>
    public static ushort[] ParseRegisters(ReadOnlySpan<byte> response, int count)
    {
        if (response.Length < 2 || response[1] != count * 2 || response.Length < 2 + count * 2)
            throw new ProtocolException("Malformed register read response.");
        var result = new ushort[count];
        for (var i = 0; i < count; i++) result[i] = BinaryPrimitives.ReadUInt16BigEndian(response.Slice(2 + i * 2));
        return result;
    }

    /// <summary>Validates an echo response (0x05, 0x06, 0x0F, 0x10, 0x16) against the request.</summary>
    public static void ValidateEcho(ReadOnlySpan<byte> request, ReadOnlySpan<byte> response)
    {
        var n = request[0] is (byte)ModbusFunctionCode.MaskWriteRegister ? 7 : 5;
        if (response.Length < n || !response[..n].SequenceEqual(request[..n]))
            throw new ProtocolException("Write response does not echo the request.");
    }

    /// <summary>Decodes a Read Device Identification response.</summary>
    public static ModbusDeviceIdentification ParseDeviceIdentification(ReadOnlySpan<byte> response)
    {
        if (response.Length < 7 || response[1] != 0x0E) throw new ProtocolException("Malformed device identification response.");
        var count = response[6];
        var objects = new Dictionary<byte, string>();
        var p = 7;
        for (var i = 0; i < count; i++)
        {
            if (p + 2 > response.Length) throw new ProtocolException("Truncated device identification object.");
            var id = response[p];
            var len = response[p + 1];
            if (p + 2 + len > response.Length) throw new ProtocolException("Truncated device identification object.");
            objects[id] = Encoding.ASCII.GetString(response.Slice(p + 2, len));
            p += 2 + len;
        }
        return new ModbusDeviceIdentification(
            objects.GetValueOrDefault((byte)0) ?? "", objects.GetValueOrDefault((byte)1) ?? "", objects.GetValueOrDefault((byte)2) ?? "", objects);
    }

    /// <summary>Packs booleans LSB-first into bytes.</summary>
    public static void PackBits(ReadOnlySpan<bool> values, Span<byte> destination)
    {
        destination[..((values.Length + 7) / 8)].Clear();
        for (var i = 0; i < values.Length; i++)
            if (values[i]) destination[i >> 3] |= (byte)(1 << (i & 7));
    }

    /// <summary>Unpacks LSB-first bits.</summary>
    public static void UnpackBits(ReadOnlySpan<byte> source, Span<bool> destination)
    {
        for (var i = 0; i < destination.Length; i++) destination[i] = (source[i >> 3] & (1 << (i & 7))) != 0;
    }

    /// <summary>Human readable summary of a PDU, used by the traffic tap / inspector.</summary>
    public static string Describe(byte unitId, ReadOnlySpan<byte> pdu, bool isRequest)
    {
        if (pdu.IsEmpty) return $"unit={unitId} <empty>";
        var fc = pdu[0];
        var ic = CultureInfo.InvariantCulture;
        if ((fc & 0x80) != 0)
        {
            var code = pdu.Length > 1 ? (ModbusExceptionCode)pdu[1] : 0;
            return string.Create(ic, $"unit={unitId} EXCEPTION {(ModbusFunctionCode)(fc & 0x7F)} → {code}");
        }
        var name = Enum.IsDefined((ModbusFunctionCode)fc) ? ((ModbusFunctionCode)fc).ToString() : $"Function 0x{fc:X2}";
        static ushort U16(ReadOnlySpan<byte> p, int at) => p.Length >= at + 2 ? BinaryPrimitives.ReadUInt16BigEndian(p[at..]) : (ushort)0;
        switch ((ModbusFunctionCode)fc)
        {
            case ModbusFunctionCode.ReadCoils or ModbusFunctionCode.ReadDiscreteInputs or ModbusFunctionCode.ReadHoldingRegisters or ModbusFunctionCode.ReadInputRegisters:
                return isRequest
                    ? string.Create(ic, $"unit={unitId} {name} addr={U16(pdu, 1)} count={U16(pdu, 3)}")
                    : string.Create(ic, $"unit={unitId} {name} → {Bytes(pdu)}");
            case ModbusFunctionCode.WriteSingleCoil:
                return string.Create(ic, $"unit={unitId} {name} addr={U16(pdu, 1)} value={(U16(pdu, 3) == 0xFF00 ? "ON" : "OFF")}");
            case ModbusFunctionCode.WriteSingleRegister:
                return string.Create(ic, $"unit={unitId} {name} addr={U16(pdu, 1)} value={U16(pdu, 3)}");
            case ModbusFunctionCode.WriteMultipleCoils or ModbusFunctionCode.WriteMultipleRegisters:
                return string.Create(ic, $"unit={unitId} {name} addr={U16(pdu, 1)} count={U16(pdu, 3)}");
            case ModbusFunctionCode.ReadWriteMultipleRegisters:
                return isRequest
                    ? string.Create(ic, $"unit={unitId} {name} read={U16(pdu, 1)}+{U16(pdu, 3)} write={U16(pdu, 5)}+{U16(pdu, 7)}")
                    : string.Create(ic, $"unit={unitId} {name} → {Bytes(pdu)}");
            case ModbusFunctionCode.MaskWriteRegister:
                return string.Create(ic, $"unit={unitId} {name} addr={U16(pdu, 1)} and=0x{U16(pdu, 3):X4} or=0x{U16(pdu, 5):X4}");
            case ModbusFunctionCode.EncapsulatedInterface:
                return string.Create(ic, $"unit={unitId} Read Device Identification");
            default:
                return string.Create(ic, $"unit={unitId} {name} ({pdu.Length - 1} data bytes)");
        }
    }

    private static string Bytes(ReadOnlySpan<byte> pdu)
    {
        var n = pdu.Length > 1 ? pdu[1] : 0;
        return n == 1 ? "1 byte" : string.Create(CultureInfo.InvariantCulture, $"{n} bytes");
    }

    private static void ValidateRange(int address, int count, int max)
    {
        if (count < 1 || count > max) throw new ArgumentOutOfRangeException(nameof(count), count, $"Quantity must be between 1 and {max}.");
        if (address + count > 65536) throw new ArgumentOutOfRangeException(nameof(address), address, "Address + quantity exceeds the 16-bit address space.");
    }
}
