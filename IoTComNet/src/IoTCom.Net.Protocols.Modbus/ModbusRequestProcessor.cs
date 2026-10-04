using System.Buffers.Binary;
using System.Text;

namespace IoTCom.Net.Protocols.Modbus;

/// <summary>
/// Executes request PDUs against a <see cref="ModbusDataStore"/> and produces response PDUs.
/// Pure logic (no I/O): the server, simulators and gateways reuse it.
/// </summary>
public sealed class ModbusRequestProcessor(ModbusDataStore store)
{
    /// <summary>Data store served.</summary>
    public ModbusDataStore Store { get; } = store;

    /// <summary>Vendor name reported by Read Device Identification.</summary>
    public string VendorName { get; init; } = "Gravicode Studios";
    /// <summary>Product code reported by Read Device Identification.</summary>
    public string ProductCode { get; init; } = "IoTCom.Net Modbus Server";
    /// <summary>Revision reported by Read Device Identification.</summary>
    public string Revision { get; init; } = IoTComInfo.Version;

    /// <summary>When true, all write functions return IllegalFunction.</summary>
    public bool ReadOnly { get; init; }

    /// <summary>Processes <paramref name="request"/> and returns the response PDU.</summary>
    public byte[] Process(ReadOnlySpan<byte> request)
    {
        if (request.IsEmpty) return Error(0, ModbusExceptionCode.IllegalFunction);
        var fc = request[0];
        try
        {
            return (ModbusFunctionCode)fc switch
            {
                ModbusFunctionCode.ReadCoils => ReadBits(request, Store.Coils),
                ModbusFunctionCode.ReadDiscreteInputs => ReadBits(request, Store.DiscreteInputs),
                ModbusFunctionCode.ReadHoldingRegisters => ReadRegisters(request, Store.HoldingRegisters),
                ModbusFunctionCode.ReadInputRegisters => ReadRegisters(request, Store.InputRegisters),
                ModbusFunctionCode.WriteSingleCoil when !ReadOnly => WriteSingleCoil(request),
                ModbusFunctionCode.WriteSingleRegister when !ReadOnly => WriteSingleRegister(request),
                ModbusFunctionCode.WriteMultipleCoils when !ReadOnly => WriteMultipleCoils(request),
                ModbusFunctionCode.WriteMultipleRegisters when !ReadOnly => WriteMultipleRegisters(request),
                ModbusFunctionCode.MaskWriteRegister when !ReadOnly => MaskWrite(request),
                ModbusFunctionCode.ReadWriteMultipleRegisters when !ReadOnly => ReadWrite(request),
                ModbusFunctionCode.EncapsulatedInterface => DeviceIdentification(request),
                _ => Error(fc, ModbusExceptionCode.IllegalFunction),
            };
        }
        catch (ModbusRequestException ex)
        {
            return Error(fc, ex.Code);
        }
        catch (Exception)
        {
            return Error(fc, ModbusExceptionCode.ServerDeviceFailure);
        }
    }

    /// <summary>Builds an exception response PDU.</summary>
    public static byte[] Error(byte function, ModbusExceptionCode code) => [(byte)(function | 0x80), (byte)code];

    private static (int Address, int Count) AddrCount(ReadOnlySpan<byte> r, int max, int size)
    {
        if (r.Length < 5) throw new ModbusRequestException(ModbusExceptionCode.IllegalDataValue);
        var address = BinaryPrimitives.ReadUInt16BigEndian(r[1..]);
        var count = BinaryPrimitives.ReadUInt16BigEndian(r[3..]);
        if (count < 1 || count > max) throw new ModbusRequestException(ModbusExceptionCode.IllegalDataValue);
        if (address + count > size) throw new ModbusRequestException(ModbusExceptionCode.IllegalDataAddress);
        return (address, count);
    }

    private static byte[] ReadBits(ReadOnlySpan<byte> r, ModbusBitBank bank)
    {
        var (address, count) = AddrCount(r, ModbusLimits.MaxReadBits, bank.Count);
        var byteCount = (count + 7) / 8;
        var resp = new byte[2 + byteCount];
        resp[0] = r[0];
        resp[1] = (byte)byteCount;
        Span<bool> bits = count <= 256 ? stackalloc bool[count] : new bool[count];
        bank.Read(address, bits);
        ModbusPdu.PackBits(bits, resp.AsSpan(2));
        return resp;
    }

    private static byte[] ReadRegisters(ReadOnlySpan<byte> r, ModbusRegisterBank bank)
    {
        var (address, count) = AddrCount(r, ModbusLimits.MaxReadRegisters, bank.Count);
        var resp = new byte[2 + count * 2];
        resp[0] = r[0];
        resp[1] = (byte)(count * 2);
        Span<ushort> regs = stackalloc ushort[count];
        bank.Read(address, regs);
        for (var i = 0; i < count; i++) BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(2 + i * 2), regs[i]);
        return resp;
    }

    private byte[] WriteSingleCoil(ReadOnlySpan<byte> r)
    {
        if (r.Length < 5) throw new ModbusRequestException(ModbusExceptionCode.IllegalDataValue);
        var address = BinaryPrimitives.ReadUInt16BigEndian(r[1..]);
        var value = BinaryPrimitives.ReadUInt16BigEndian(r[3..]);
        if (value is not 0xFF00 and not 0x0000) throw new ModbusRequestException(ModbusExceptionCode.IllegalDataValue);
        if (address >= Store.Coils.Count) throw new ModbusRequestException(ModbusExceptionCode.IllegalDataAddress);
        Store.Coils.Write(address, [value == 0xFF00], fromRemote: true);
        return r[..5].ToArray();
    }

    private byte[] WriteSingleRegister(ReadOnlySpan<byte> r)
    {
        if (r.Length < 5) throw new ModbusRequestException(ModbusExceptionCode.IllegalDataValue);
        var address = BinaryPrimitives.ReadUInt16BigEndian(r[1..]);
        if (address >= Store.HoldingRegisters.Count) throw new ModbusRequestException(ModbusExceptionCode.IllegalDataAddress);
        Store.HoldingRegisters.Write(address, [BinaryPrimitives.ReadUInt16BigEndian(r[3..])], fromRemote: true);
        return r[..5].ToArray();
    }

    private byte[] WriteMultipleCoils(ReadOnlySpan<byte> r)
    {
        var (address, count) = AddrCount(r, ModbusLimits.MaxWriteBits, Store.Coils.Count);
        if (r.Length < 6 || r[5] != (count + 7) / 8 || r.Length < 6 + r[5]) throw new ModbusRequestException(ModbusExceptionCode.IllegalDataValue);
        var bits = new bool[count];
        ModbusPdu.UnpackBits(r.Slice(6, r[5]), bits);
        Store.Coils.Write(address, bits, fromRemote: true);
        return r[..5].ToArray();
    }

    private byte[] WriteMultipleRegisters(ReadOnlySpan<byte> r)
    {
        var (address, count) = AddrCount(r, ModbusLimits.MaxWriteRegisters, Store.HoldingRegisters.Count);
        if (r.Length < 6 || r[5] != count * 2 || r.Length < 6 + count * 2) throw new ModbusRequestException(ModbusExceptionCode.IllegalDataValue);
        Span<ushort> values = stackalloc ushort[count];
        for (var i = 0; i < count; i++) values[i] = BinaryPrimitives.ReadUInt16BigEndian(r[(6 + i * 2)..]);
        Store.HoldingRegisters.Write(address, values, fromRemote: true);
        return r[..5].ToArray();
    }

    private byte[] MaskWrite(ReadOnlySpan<byte> r)
    {
        if (r.Length < 7) throw new ModbusRequestException(ModbusExceptionCode.IllegalDataValue);
        var address = BinaryPrimitives.ReadUInt16BigEndian(r[1..]);
        if (address >= Store.HoldingRegisters.Count) throw new ModbusRequestException(ModbusExceptionCode.IllegalDataAddress);
        Store.HoldingRegisters.MaskWrite(address, BinaryPrimitives.ReadUInt16BigEndian(r[3..]), BinaryPrimitives.ReadUInt16BigEndian(r[5..]));
        return r[..7].ToArray();
    }

    private byte[] ReadWrite(ReadOnlySpan<byte> r)
    {
        if (r.Length < 10) throw new ModbusRequestException(ModbusExceptionCode.IllegalDataValue);
        var (readAddr, readCount) = AddrCount(r, ModbusLimits.MaxReadRegisters, Store.HoldingRegisters.Count);
        var (writeAddr, writeCount) = AddrCount(r[4..], ModbusLimits.MaxReadWriteWriteRegisters, Store.HoldingRegisters.Count);
        if (r[9] != writeCount * 2 || r.Length < 10 + writeCount * 2) throw new ModbusRequestException(ModbusExceptionCode.IllegalDataValue);
        Span<ushort> values = stackalloc ushort[writeCount];
        for (var i = 0; i < writeCount; i++) values[i] = BinaryPrimitives.ReadUInt16BigEndian(r[(10 + i * 2)..]);
        Store.HoldingRegisters.Write(writeAddr, values, fromRemote: true); // spec: write happens before read
        var resp = new byte[2 + readCount * 2];
        resp[0] = r[0];
        resp[1] = (byte)(readCount * 2);
        Span<ushort> regs = stackalloc ushort[readCount];
        Store.HoldingRegisters.Read(readAddr, regs);
        for (var i = 0; i < readCount; i++) BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(2 + i * 2), regs[i]);
        return resp;
    }

    private byte[] DeviceIdentification(ReadOnlySpan<byte> r)
    {
        if (r.Length < 4 || r[1] != 0x0E) throw new ModbusRequestException(ModbusExceptionCode.IllegalFunction);
        if (r[2] is < 1 or > 4) throw new ModbusRequestException(ModbusExceptionCode.IllegalDataValue);
        string[] objects = [VendorName, ProductCode, Revision];
        var body = new List<byte> { r[0], 0x0E, r[2], 0x01, 0x00, 0x00, (byte)objects.Length };
        for (var i = 0; i < objects.Length; i++)
        {
            var bytes = Encoding.ASCII.GetBytes(objects[i]);
            var len = Math.Min(bytes.Length, 64);
            body.Add((byte)i);
            body.Add((byte)len);
            body.AddRange(bytes.AsSpan(0, len));
        }
        return [.. body];
    }

    private sealed class ModbusRequestException(ModbusExceptionCode code) : Exception
    {
        public ModbusExceptionCode Code { get; } = code;
    }
}
