namespace IoTCom.Net.Protocols.Modbus;

/// <summary>Which Modbus table a value lives in.</summary>
public enum ModbusTable
{
    /// <summary>Read/write bits (0x).</summary>
    Coils,
    /// <summary>Read-only bits (1x).</summary>
    DiscreteInputs,
    /// <summary>Read-only registers (3x).</summary>
    InputRegisters,
    /// <summary>Read/write registers (4x).</summary>
    HoldingRegisters,
}

/// <summary>Raised when values in a bank change.</summary>
public sealed class ModbusDataChangedEventArgs(ModbusTable table, int address, int count, bool fromRemote) : EventArgs
{
    /// <summary>Table that changed.</summary>
    public ModbusTable Table { get; } = table;
    /// <summary>First changed address.</summary>
    public int Address { get; } = address;
    /// <summary>Number of changed values.</summary>
    public int Count { get; } = count;
    /// <summary>True when the change came from a Modbus master write (as opposed to local code/simulator).</summary>
    public bool FromRemote { get; } = fromRemote;
}

/// <summary>Thread-safe bank of 16-bit registers.</summary>
public sealed class ModbusRegisterBank
{
    private readonly ushort[] _data;
    private readonly Lock _gate = new();

    internal ModbusRegisterBank(ModbusTable table, int size)
    {
        Table = table;
        _data = new ushort[size];
    }

    /// <summary>Table kind.</summary>
    public ModbusTable Table { get; }

    /// <summary>Number of registers.</summary>
    public int Count => _data.Length;

    /// <summary>Raised after values change (outside the lock).</summary>
    public event EventHandler<ModbusDataChangedEventArgs>? Changed;

    /// <summary>Gets or sets one register.</summary>
    public ushort this[int address]
    {
        get { lock (_gate) return _data[address]; }
        set => Write(address, [value]);
    }

    /// <summary>Copies registers into <paramref name="destination"/>.</summary>
    public void Read(int address, Span<ushort> destination)
    {
        lock (_gate) _data.AsSpan(address, destination.Length).CopyTo(destination);
    }

    /// <summary>Reads <paramref name="count"/> registers.</summary>
    public ushort[] Read(int address, int count)
    {
        var r = new ushort[count];
        Read(address, r);
        return r;
    }

    /// <summary>Writes registers atomically.</summary>
    public void Write(int address, ReadOnlySpan<ushort> values) => Write(address, values, fromRemote: false);

    internal void Write(int address, ReadOnlySpan<ushort> values, bool fromRemote)
    {
        lock (_gate) values.CopyTo(_data.AsSpan(address));
        Changed?.Invoke(this, new ModbusDataChangedEventArgs(Table, address, values.Length, fromRemote));
    }

    internal ushort MaskWrite(int address, ushort andMask, ushort orMask)
    {
        ushort result;
        lock (_gate)
        {
            result = (ushort)((_data[address] & andMask) | (orMask & ~andMask));
            _data[address] = result;
        }
        Changed?.Invoke(this, new ModbusDataChangedEventArgs(Table, address, 1, true));
        return result;
    }

    /// <summary>Reads a float spanning two registers.</summary>
    public float GetSingle(int address, ModbusWordOrder order = ModbusWordOrder.BigEndian)
    {
        Span<ushort> r = stackalloc ushort[2];
        Read(address, r);
        return ModbusConvert.ToSingle(r, order);
    }

    /// <summary>Writes a float spanning two registers.</summary>
    public void SetSingle(int address, float value, ModbusWordOrder order = ModbusWordOrder.BigEndian)
    {
        Span<ushort> r = stackalloc ushort[2];
        ModbusConvert.FromSingle(value, r, order);
        Write(address, r);
    }

    /// <summary>Reads a 32-bit integer spanning two registers.</summary>
    public int GetInt32(int address, ModbusWordOrder order = ModbusWordOrder.BigEndian)
    {
        Span<ushort> r = stackalloc ushort[2];
        Read(address, r);
        return ModbusConvert.ToInt32(r, order);
    }

    /// <summary>Writes a 32-bit integer spanning two registers.</summary>
    public void SetInt32(int address, int value, ModbusWordOrder order = ModbusWordOrder.BigEndian)
    {
        Span<ushort> r = stackalloc ushort[2];
        ModbusConvert.FromInt32(value, r, order);
        Write(address, r);
    }
}

/// <summary>Thread-safe bank of bits.</summary>
public sealed class ModbusBitBank
{
    private readonly bool[] _data;
    private readonly Lock _gate = new();

    internal ModbusBitBank(ModbusTable table, int size)
    {
        Table = table;
        _data = new bool[size];
    }

    /// <summary>Table kind.</summary>
    public ModbusTable Table { get; }

    /// <summary>Number of bits.</summary>
    public int Count => _data.Length;

    /// <summary>Raised after values change (outside the lock).</summary>
    public event EventHandler<ModbusDataChangedEventArgs>? Changed;

    /// <summary>Gets or sets one bit.</summary>
    public bool this[int address]
    {
        get { lock (_gate) return _data[address]; }
        set => Write(address, [value]);
    }

    /// <summary>Copies bits into <paramref name="destination"/>.</summary>
    public void Read(int address, Span<bool> destination)
    {
        lock (_gate) _data.AsSpan(address, destination.Length).CopyTo(destination);
    }

    /// <summary>Reads <paramref name="count"/> bits.</summary>
    public bool[] Read(int address, int count)
    {
        var r = new bool[count];
        Read(address, r);
        return r;
    }

    /// <summary>Writes bits atomically.</summary>
    public void Write(int address, ReadOnlySpan<bool> values) => Write(address, values, fromRemote: false);

    internal void Write(int address, ReadOnlySpan<bool> values, bool fromRemote)
    {
        lock (_gate) values.CopyTo(_data.AsSpan(address));
        Changed?.Invoke(this, new ModbusDataChangedEventArgs(Table, address, values.Length, fromRemote));
    }
}

/// <summary>
/// The four Modbus tables of a (virtual) device. Shared by <see cref="ModbusServer"/> and <see cref="ModbusSimulator"/>.
/// </summary>
public sealed class ModbusDataStore
{
    /// <summary>Creates a store. Sizes default to the full 65 536 address space.</summary>
    public ModbusDataStore(int coils = 65536, int discreteInputs = 65536, int inputRegisters = 65536, int holdingRegisters = 65536)
    {
        Coils = new ModbusBitBank(ModbusTable.Coils, coils);
        DiscreteInputs = new ModbusBitBank(ModbusTable.DiscreteInputs, discreteInputs);
        InputRegisters = new ModbusRegisterBank(ModbusTable.InputRegisters, inputRegisters);
        HoldingRegisters = new ModbusRegisterBank(ModbusTable.HoldingRegisters, holdingRegisters);
    }

    /// <summary>Coils (0x, read/write bits).</summary>
    public ModbusBitBank Coils { get; }
    /// <summary>Discrete inputs (1x, read-only bits).</summary>
    public ModbusBitBank DiscreteInputs { get; }
    /// <summary>Input registers (3x, read-only).</summary>
    public ModbusRegisterBank InputRegisters { get; }
    /// <summary>Holding registers (4x, read/write).</summary>
    public ModbusRegisterBank HoldingRegisters { get; }
}
