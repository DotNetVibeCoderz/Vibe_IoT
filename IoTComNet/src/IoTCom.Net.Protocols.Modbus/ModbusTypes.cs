namespace IoTCom.Net.Protocols.Modbus;

/// <summary>Standard Modbus public function codes.</summary>
public enum ModbusFunctionCode : byte
{
    /// <summary>0x01 Read Coils.</summary>
    ReadCoils = 0x01,
    /// <summary>0x02 Read Discrete Inputs.</summary>
    ReadDiscreteInputs = 0x02,
    /// <summary>0x03 Read Holding Registers.</summary>
    ReadHoldingRegisters = 0x03,
    /// <summary>0x04 Read Input Registers.</summary>
    ReadInputRegisters = 0x04,
    /// <summary>0x05 Write Single Coil.</summary>
    WriteSingleCoil = 0x05,
    /// <summary>0x06 Write Single Register.</summary>
    WriteSingleRegister = 0x06,
    /// <summary>0x0F Write Multiple Coils.</summary>
    WriteMultipleCoils = 0x0F,
    /// <summary>0x10 Write Multiple Registers.</summary>
    WriteMultipleRegisters = 0x10,
    /// <summary>0x16 Mask Write Register.</summary>
    MaskWriteRegister = 0x16,
    /// <summary>0x17 Read/Write Multiple Registers.</summary>
    ReadWriteMultipleRegisters = 0x17,
    /// <summary>0x2B Encapsulated Interface Transport (MEI 0x0E = Read Device Identification).</summary>
    EncapsulatedInterface = 0x2B,
}

/// <summary>Modbus exception codes returned by devices.</summary>
public enum ModbusExceptionCode : byte
{
    /// <summary>0x01 The function code is not supported.</summary>
    IllegalFunction = 0x01,
    /// <summary>0x02 The address (or address + quantity) is out of range.</summary>
    IllegalDataAddress = 0x02,
    /// <summary>0x03 A value in the request is not allowed (e.g. quantity).</summary>
    IllegalDataValue = 0x03,
    /// <summary>0x04 Unrecoverable error while performing the action.</summary>
    ServerDeviceFailure = 0x04,
    /// <summary>0x05 Long running request accepted.</summary>
    Acknowledge = 0x05,
    /// <summary>0x06 Device busy.</summary>
    ServerDeviceBusy = 0x06,
    /// <summary>0x08 Memory parity error.</summary>
    MemoryParityError = 0x08,
    /// <summary>0x0A Gateway path unavailable.</summary>
    GatewayPathUnavailable = 0x0A,
    /// <summary>0x0B Gateway target device failed to respond.</summary>
    GatewayTargetFailedToRespond = 0x0B,
}

/// <summary>On-the-wire framing variant.</summary>
public enum ModbusFramingMode
{
    /// <summary>Modbus TCP (MBAP header, transaction ids, no CRC).</summary>
    Tcp = 0,
    /// <summary>Modbus RTU (binary, CRC-16/MODBUS). Also used for RTU-over-TCP.</summary>
    Rtu = 1,
    /// <summary>Modbus ASCII (':' hex ... LRC CR LF).</summary>
    Ascii = 2,
}

/// <summary>Register order for 32/64-bit values spread over several 16-bit registers.</summary>
public enum ModbusWordOrder
{
    /// <summary>ABCD — big-endian (Modbus spec default, most PLCs).</summary>
    BigEndian = 0,
    /// <summary>CDAB — word swapped (low word first; common on Schneider, some meters).</summary>
    WordSwap = 1,
    /// <summary>BADC — byte swapped within each register.</summary>
    ByteSwap = 2,
    /// <summary>DCBA — fully little-endian.</summary>
    LittleEndian = 3,
}

/// <summary>A Modbus exception response from a device.</summary>
public sealed class ModbusException : DeviceException
{
    /// <summary>Creates the exception.</summary>
    public ModbusException(ModbusExceptionCode code, ModbusFunctionCode function, byte unitId)
        : base($"Modbus exception {(byte)code:X2} ({code}) for {function} on unit {unitId}.", (int)code)
    {
        ExceptionCode = code;
        Function = function;
        UnitId = unitId;
    }

    /// <summary>Creates an exception.</summary>
    public ModbusException() { }
    /// <summary>Creates an exception with a message.</summary>
    public ModbusException(string message) : base(message) { }
    /// <summary>Creates an exception with a message and inner exception.</summary>
    public ModbusException(string message, Exception? inner) : base(message, inner) { }

    /// <summary>Exception code.</summary>
    public ModbusExceptionCode ExceptionCode { get; }
    /// <summary>Function that failed.</summary>
    public ModbusFunctionCode Function { get; }
    /// <summary>Responding unit.</summary>
    public byte UnitId { get; }
}

/// <summary>Protocol limits from the Modbus Application Protocol Specification V1.1b3.</summary>
public static class ModbusLimits
{
    /// <summary>Max coils / discrete inputs per read.</summary>
    public const int MaxReadBits = 2000;
    /// <summary>Max registers per read.</summary>
    public const int MaxReadRegisters = 125;
    /// <summary>Max coils per write.</summary>
    public const int MaxWriteBits = 1968;
    /// <summary>Max registers per write.</summary>
    public const int MaxWriteRegisters = 123;
    /// <summary>Max registers written by function 0x17.</summary>
    public const int MaxReadWriteWriteRegisters = 121;
    /// <summary>Max PDU length.</summary>
    public const int MaxPduLength = 253;
}

/// <summary>Device identification returned by function 0x2B / MEI 0x0E.</summary>
public sealed record ModbusDeviceIdentification(string VendorName, string ProductCode, string Revision, IReadOnlyDictionary<byte, string> Objects);
