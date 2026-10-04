namespace IoTCom.Net.Protocols.Modbus;

/// <summary>
/// Modbus master operations. Implemented by the managed <see cref="ModbusClient"/> and by the Rust-backed
/// <c>NativeModbusClient</c> (package IoTCom.Net.Native.Modbus), so both are interchangeable.
/// </summary>
public interface IModbusClient : IClientEndpoint
{
    /// <summary>Default unit id used when a call does not specify one.</summary>
    byte UnitId { get; }

    /// <summary>Reads coils (0x01).</summary>
    ValueTask<bool[]> ReadCoilsAsync(ushort address, ushort count, byte? unitId = null, CancellationToken ct = default);

    /// <summary>Reads discrete inputs (0x02).</summary>
    ValueTask<bool[]> ReadDiscreteInputsAsync(ushort address, ushort count, byte? unitId = null, CancellationToken ct = default);

    /// <summary>Reads holding registers (0x03).</summary>
    ValueTask<ushort[]> ReadHoldingRegistersAsync(ushort address, ushort count, byte? unitId = null, CancellationToken ct = default);

    /// <summary>Reads input registers (0x04).</summary>
    ValueTask<ushort[]> ReadInputRegistersAsync(ushort address, ushort count, byte? unitId = null, CancellationToken ct = default);

    /// <summary>Writes one coil (0x05).</summary>
    ValueTask WriteSingleCoilAsync(ushort address, bool value, byte? unitId = null, CancellationToken ct = default);

    /// <summary>Writes one holding register (0x06).</summary>
    ValueTask WriteSingleRegisterAsync(ushort address, ushort value, byte? unitId = null, CancellationToken ct = default);

    /// <summary>Writes several coils (0x0F).</summary>
    ValueTask WriteMultipleCoilsAsync(ushort address, ReadOnlyMemory<bool> values, byte? unitId = null, CancellationToken ct = default);

    /// <summary>Writes several holding registers (0x10).</summary>
    ValueTask WriteMultipleRegistersAsync(ushort address, ReadOnlyMemory<ushort> values, byte? unitId = null, CancellationToken ct = default);

    /// <summary>Sends a raw PDU and returns the raw response PDU (exception responses throw <see cref="ModbusException"/>).</summary>
    ValueTask<byte[]> SendAsync(ReadOnlyMemory<byte> pdu, byte? unitId = null, CancellationToken ct = default);
}

/// <summary>Typed helpers on top of <see cref="IModbusClient"/>.</summary>
public static class ModbusClientExtensions
{
    /// <summary>Reads a float from two holding registers.</summary>
    public static async ValueTask<float> ReadSingleAsync(this IModbusClient client, ushort address, ModbusWordOrder order = ModbusWordOrder.BigEndian, byte? unitId = null, CancellationToken ct = default)
        => ModbusConvert.ToSingle(await client.ReadHoldingRegistersAsync(address, 2, unitId, ct).ConfigureAwait(false), order);

    /// <summary>Reads a 32-bit signed integer from two holding registers.</summary>
    public static async ValueTask<int> ReadInt32Async(this IModbusClient client, ushort address, ModbusWordOrder order = ModbusWordOrder.BigEndian, byte? unitId = null, CancellationToken ct = default)
        => ModbusConvert.ToInt32(await client.ReadHoldingRegistersAsync(address, 2, unitId, ct).ConfigureAwait(false), order);

    /// <summary>Writes a float to two holding registers.</summary>
    public static ValueTask WriteSingleAsync(this IModbusClient client, ushort address, float value, ModbusWordOrder order = ModbusWordOrder.BigEndian, byte? unitId = null, CancellationToken ct = default)
    {
        var regs = new ushort[2];
        ModbusConvert.FromSingle(value, regs, order);
        return client.WriteMultipleRegistersAsync(address, regs, unitId, ct);
    }

    /// <summary>Reads an ASCII string from <paramref name="registerCount"/> holding registers.</summary>
    public static async ValueTask<string> ReadStringAsync(this IModbusClient client, ushort address, ushort registerCount, byte? unitId = null, CancellationToken ct = default)
        => ModbusConvert.ToAsciiString(await client.ReadHoldingRegistersAsync(address, registerCount, unitId, ct).ConfigureAwait(false));

    /// <summary>Reads the device identification (0x2B/0x0E, basic objects).</summary>
    public static async ValueTask<ModbusDeviceIdentification> ReadDeviceIdentificationAsync(this IModbusClient client, byte? unitId = null, CancellationToken ct = default)
        => ModbusPdu.ParseDeviceIdentification(await client.SendAsync(ModbusPdu.ReadDeviceIdentification(), unitId, ct).ConfigureAwait(false));
}
