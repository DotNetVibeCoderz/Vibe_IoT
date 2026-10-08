using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace IoTCom.Net.Protocols.CanOpen;

/// <summary>CANopen basic data types (CiA 301, object indices 0x0001–0x001B).</summary>
public enum CanOpenDataType : ushort
{
    /// <summary>BOOLEAN.</summary>
    Boolean = 0x0001,
    /// <summary>INTEGER8.</summary>
    Integer8 = 0x0002,
    /// <summary>INTEGER16.</summary>
    Integer16 = 0x0003,
    /// <summary>INTEGER32.</summary>
    Integer32 = 0x0004,
    /// <summary>UNSIGNED8.</summary>
    Unsigned8 = 0x0005,
    /// <summary>UNSIGNED16.</summary>
    Unsigned16 = 0x0006,
    /// <summary>UNSIGNED32.</summary>
    Unsigned32 = 0x0007,
    /// <summary>REAL32.</summary>
    Real32 = 0x0008,
    /// <summary>VISIBLE_STRING.</summary>
    VisibleString = 0x0009,
    /// <summary>OCTET_STRING.</summary>
    OctetString = 0x000A,
    /// <summary>DOMAIN.</summary>
    Domain = 0x000F,
    /// <summary>INTEGER64.</summary>
    Integer64 = 0x0015,
    /// <summary>UNSIGNED64.</summary>
    Unsigned64 = 0x001B,
}

/// <summary>Access types.</summary>
public enum CanOpenAccess
{
    /// <summary>Read only.</summary>
    ReadOnly,
    /// <summary>Write only.</summary>
    WriteOnly,
    /// <summary>Read and write.</summary>
    ReadWrite,
    /// <summary>Constant.</summary>
    Const,
}

/// <summary>Size and value conversion for CANopen data types (little-endian).</summary>
public static class CanOpenValue
{
    /// <summary>Fixed size in bytes, or null for strings and domains.</summary>
    public static int? Size(CanOpenDataType t) => t switch
    {
        CanOpenDataType.Boolean or CanOpenDataType.Integer8 or CanOpenDataType.Unsigned8 => 1,
        CanOpenDataType.Integer16 or CanOpenDataType.Unsigned16 => 2,
        CanOpenDataType.Integer32 or CanOpenDataType.Unsigned32 or CanOpenDataType.Real32 => 4,
        CanOpenDataType.Integer64 or CanOpenDataType.Unsigned64 => 8,
        _ => null,
    };

    /// <summary>Encodes a .NET value for a type.</summary>
    public static byte[] Encode(CanOpenDataType t, object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var b = new byte[Size(t) ?? 0];
        var c = CultureInfo.InvariantCulture;
        switch (t)
        {
            case CanOpenDataType.Boolean: b[0] = Convert.ToBoolean(value, c) ? (byte)1 : (byte)0; break;
            case CanOpenDataType.Integer8: b[0] = unchecked((byte)Convert.ToSByte(value, c)); break;
            case CanOpenDataType.Unsigned8: b[0] = Convert.ToByte(value, c); break;
            case CanOpenDataType.Integer16: BinaryPrimitives.WriteInt16LittleEndian(b, Convert.ToInt16(value, c)); break;
            case CanOpenDataType.Unsigned16: BinaryPrimitives.WriteUInt16LittleEndian(b, Convert.ToUInt16(value, c)); break;
            case CanOpenDataType.Integer32: BinaryPrimitives.WriteInt32LittleEndian(b, Convert.ToInt32(value, c)); break;
            case CanOpenDataType.Unsigned32: BinaryPrimitives.WriteUInt32LittleEndian(b, Convert.ToUInt32(value, c)); break;
            case CanOpenDataType.Real32: BinaryPrimitives.WriteSingleLittleEndian(b, Convert.ToSingle(value, c)); break;
            case CanOpenDataType.Integer64: BinaryPrimitives.WriteInt64LittleEndian(b, Convert.ToInt64(value, c)); break;
            case CanOpenDataType.Unsigned64: BinaryPrimitives.WriteUInt64LittleEndian(b, Convert.ToUInt64(value, c)); break;
            case CanOpenDataType.VisibleString: return Encoding.ASCII.GetBytes(Convert.ToString(value, c) ?? "");
            default: return value as byte[] ?? throw new ArgumentException($"{t} values are byte arrays.", nameof(value));
        }

        return b;
    }

    /// <summary>Decodes bytes of a type into a .NET value (long, ulong, float, bool, string or byte[]).</summary>
    public static object Decode(CanOpenDataType t, ReadOnlySpan<byte> v) => t switch
    {
        CanOpenDataType.Boolean => v.Length > 0 && v[0] != 0,
        CanOpenDataType.Integer8 => (long)unchecked((sbyte)v[0]),
        CanOpenDataType.Unsigned8 => (ulong)v[0],
        CanOpenDataType.Integer16 => (long)BinaryPrimitives.ReadInt16LittleEndian(v),
        CanOpenDataType.Unsigned16 => (ulong)BinaryPrimitives.ReadUInt16LittleEndian(v),
        CanOpenDataType.Integer32 => (long)BinaryPrimitives.ReadInt32LittleEndian(v),
        CanOpenDataType.Unsigned32 => (ulong)BinaryPrimitives.ReadUInt32LittleEndian(v),
        CanOpenDataType.Real32 => BinaryPrimitives.ReadSingleLittleEndian(v),
        CanOpenDataType.Integer64 => BinaryPrimitives.ReadInt64LittleEndian(v),
        CanOpenDataType.Unsigned64 => BinaryPrimitives.ReadUInt64LittleEndian(v),
        CanOpenDataType.VisibleString => Encoding.ASCII.GetString(v).TrimEnd('\0'),
        _ => v.ToArray(),
    };
}

/// <summary>One entry (index:sub-index) of an object dictionary.</summary>
public sealed class CanOpenEntry
{
    private byte[] _value;

    internal CanOpenEntry(ushort index, byte subIndex, string name, CanOpenDataType type, CanOpenAccess access, byte[] value, bool pdoMappable)
    {
        (Index, SubIndex, Name, Type, Access, _value, PdoMappable) = (index, subIndex, name, type, access, value, pdoMappable);
    }

    /// <summary>Index.</summary>
    public ushort Index { get; }

    /// <summary>Sub-index.</summary>
    public byte SubIndex { get; }

    /// <summary>Name.</summary>
    public string Name { get; }

    /// <summary>Data type.</summary>
    public CanOpenDataType Type { get; }

    /// <summary>Access.</summary>
    public CanOpenAccess Access { get; }

    /// <summary>May be mapped into a PDO.</summary>
    public bool PdoMappable { get; }

    /// <summary>Raw value (little-endian).</summary>
    public byte[] Raw
    {
        get => _value;
        set
        {
            _value = value;
            Changed?.Invoke(this);
        }
    }

    /// <summary>Decoded value.</summary>
    public object Value => CanOpenValue.Decode(Type, _value);

    /// <summary>Raised after the value changed (local write, SDO download or RPDO).</summary>
    public event Action<CanOpenEntry>? Changed;

    /// <summary>Validates a write coming over the network; return an SDO abort code to reject it.</summary>
    public Func<byte[], uint?>? Validate { get; set; }

    /// <inheritdoc />
    public override string ToString() => $"{Index:X4}:{SubIndex:X2} {Name} = {Value switch { byte[] b => Convert.ToHexString(b), IFormattable f => f.ToString(null, CultureInfo.InvariantCulture), var v => v }}";
}

/// <summary>An object dictionary: the variables a CANopen device exposes through SDO and PDO.</summary>
public sealed class ObjectDictionary
{
    private readonly ConcurrentDictionary<(ushort, byte), CanOpenEntry> _entries = new();

    /// <summary>All entries in index order.</summary>
    public IReadOnlyList<CanOpenEntry> Entries => [.. _entries.Values.OrderBy(e => e.Index).ThenBy(e => e.SubIndex)];

    /// <summary>Adds (or replaces) an entry.</summary>
    public CanOpenEntry Add(ushort index, byte subIndex, string name, CanOpenDataType type, CanOpenAccess access, object value, bool pdoMappable = false)
    {
        var e = new CanOpenEntry(index, subIndex, name, type, access, value is byte[] raw && CanOpenValue.Size(type) is null ? raw : CanOpenValue.Encode(type, value), pdoMappable);
        _entries[(index, subIndex)] = e;
        return e;
    }

    /// <summary>Looks an entry up.</summary>
    public CanOpenEntry? Find(ushort index, byte subIndex) => _entries.GetValueOrDefault((index, subIndex));

    /// <summary>True when any sub-index of <paramref name="index"/> exists.</summary>
    public bool HasIndex(ushort index) => _entries.Keys.Any(k => k.Item1 == index);

    /// <summary>The entry, or throws <see cref="KeyNotFoundException"/>.</summary>
    public CanOpenEntry this[ushort index, byte subIndex] => Find(index, subIndex) ?? throw new KeyNotFoundException($"{index:X4}:{subIndex:X2} is not in the object dictionary.");

    /// <summary>Sets a value locally (no access check).</summary>
    public void Set(ushort index, byte subIndex, object value)
    {
        var e = this[index, subIndex];
        e.Raw = CanOpenValue.Size(e.Type) is null && value is byte[] raw ? raw : CanOpenValue.Encode(e.Type, value);
    }

    /// <summary>
    /// A dictionary with the mandatory and common communication objects: device type (0x1000), error register (0x1001),
    /// device name (0x1008), hardware/software version (0x1009/0x100A), producer heartbeat time (0x1017) and identity
    /// (0x1018).
    /// </summary>
    public static ObjectDictionary CreateStandard(uint deviceType, string name, uint vendorId, uint productCode, uint revision, uint serial, ushort heartbeatMs = 1000, string hardware = "1.0", string software = "1.0")
    {
        var od = new ObjectDictionary();
        od.Add(0x1000, 0, "Device type", CanOpenDataType.Unsigned32, CanOpenAccess.ReadOnly, deviceType);
        od.Add(0x1001, 0, "Error register", CanOpenDataType.Unsigned8, CanOpenAccess.ReadOnly, (byte)0, pdoMappable: true);
        od.Add(0x1008, 0, "Manufacturer device name", CanOpenDataType.VisibleString, CanOpenAccess.Const, name);
        od.Add(0x1009, 0, "Manufacturer hardware version", CanOpenDataType.VisibleString, CanOpenAccess.Const, hardware);
        od.Add(0x100A, 0, "Manufacturer software version", CanOpenDataType.VisibleString, CanOpenAccess.Const, software);
        od.Add(0x1017, 0, "Producer heartbeat time", CanOpenDataType.Unsigned16, CanOpenAccess.ReadWrite, heartbeatMs);
        od.Add(0x1018, 0, "Identity: entries", CanOpenDataType.Unsigned8, CanOpenAccess.Const, (byte)4);
        od.Add(0x1018, 1, "Vendor-ID", CanOpenDataType.Unsigned32, CanOpenAccess.ReadOnly, vendorId);
        od.Add(0x1018, 2, "Product code", CanOpenDataType.Unsigned32, CanOpenAccess.ReadOnly, productCode);
        od.Add(0x1018, 3, "Revision number", CanOpenDataType.Unsigned32, CanOpenAccess.ReadOnly, revision);
        od.Add(0x1018, 4, "Serial number", CanOpenDataType.Unsigned32, CanOpenAccess.ReadOnly, serial);
        return od;
    }

    /// <summary>
    /// Declares PDO <paramref name="number"/> (1–4) in the communication (0x1800/0x1400) and mapping (0x1A00/0x1600)
    /// objects. <paramref name="transmit"/> selects TPDO or RPDO; <paramref name="transmissionType"/> is 0xFF/0xFE for
    /// event-driven and 1–240 for every n-th SYNC; <paramref name="eventTimerMs"/> applies to event-driven TPDOs.
    /// </summary>
    public void DefinePdo(bool transmit, int number, uint cobId, IReadOnlyList<(ushort Index, byte SubIndex)> objects, byte transmissionType = 0xFF, ushort eventTimerMs = 0)
    {
        ArgumentNullException.ThrowIfNull(objects);
        if (number is < 1 or > 512) throw new ArgumentOutOfRangeException(nameof(number));
        var comm = (ushort)((transmit ? 0x1800 : 0x1400) + number - 1);
        var map = (ushort)((transmit ? 0x1A00 : 0x1600) + number - 1);
        var kind = transmit ? "TPDO" : "RPDO";
        Add(comm, 0, $"{kind}{number} communication: entries", CanOpenDataType.Unsigned8, CanOpenAccess.Const, (byte)5);
        Add(comm, 1, $"{kind}{number} COB-ID", CanOpenDataType.Unsigned32, CanOpenAccess.ReadWrite, cobId);
        Add(comm, 2, $"{kind}{number} transmission type", CanOpenDataType.Unsigned8, CanOpenAccess.ReadWrite, transmissionType);
        if (transmit) Add(comm, 5, $"{kind}{number} event timer", CanOpenDataType.Unsigned16, CanOpenAccess.ReadWrite, eventTimerMs);
        Add(map, 0, $"{kind}{number} mapping: entries", CanOpenDataType.Unsigned8, CanOpenAccess.ReadWrite, (byte)objects.Count);
        for (var i = 0; i < objects.Count; i++)
        {
            var (index, sub) = objects[i];
            var e = this[index, sub];
            var bits = (CanOpenValue.Size(e.Type) ?? throw new ArgumentException($"{index:X4}:{sub:X2} has no fixed size and cannot be mapped.")) * 8;
            Add(map, (byte)(i + 1), $"{kind}{number} mapped object {i + 1}", CanOpenDataType.Unsigned32, CanOpenAccess.ReadWrite, PdoMapping.Entry(index, sub, (byte)bits));
        }
    }

    /// <summary>The mapping of PDO <paramref name="number"/> read from this dictionary.</summary>
    public PdoMapping Mapping(bool transmit, int number)
    {
        var map = (ushort)((transmit ? 0x1A00 : 0x1600) + number - 1);
        var count = Find(map, 0) is { } c ? Convert.ToInt32(c.Value, CultureInfo.InvariantCulture) : 0;
        return new PdoMapping([.. Enumerable.Range(1, count).Select(i => PdoMapping.Parse(Convert.ToUInt32(this[map, (byte)i].Value, CultureInfo.InvariantCulture)))]);
    }
}

/// <summary>One mapped object of a PDO.</summary>
/// <param name="Index">Index.</param>
/// <param name="SubIndex">Sub-index.</param>
/// <param name="Bits">Length in bits.</param>
public readonly record struct PdoMappedObject(ushort Index, byte SubIndex, byte Bits)
{
    /// <inheritdoc />
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Index:X4}:{SubIndex:X2}/{Bits}");
}

/// <summary>A PDO mapping (CiA 301 objects 0x1600/0x1A00: index &lt;&lt; 16 | sub &lt;&lt; 8 | bit length).</summary>
/// <param name="Objects">Mapped objects in order.</param>
public sealed record PdoMapping(IReadOnlyList<PdoMappedObject> Objects)
{
    /// <summary>Total bits.</summary>
    public int Bits => Objects.Sum(o => o.Bits);

    /// <summary>Encodes a mapping entry.</summary>
    public static uint Entry(ushort index, byte subIndex, byte bits) => ((uint)index << 16) | ((uint)subIndex << 8) | bits;

    /// <summary>Decodes a mapping entry.</summary>
    public static PdoMappedObject Parse(uint entry) => new((ushort)(entry >> 16), (byte)(entry >> 8), (byte)entry);

    /// <summary>Packs the mapped objects' values from <paramref name="od"/> into PDO data (byte-aligned objects).</summary>
    public byte[] Pack(ObjectDictionary od)
    {
        ArgumentNullException.ThrowIfNull(od);
        var data = new byte[(Bits + 7) / 8];
        var pos = 0;
        foreach (var o in Objects)
        {
            var raw = od[o.Index, o.SubIndex].Raw;
            raw.AsSpan(0, Math.Min(raw.Length, o.Bits / 8)).CopyTo(data.AsSpan(pos));
            pos += o.Bits / 8;
        }

        return data;
    }

    /// <summary>Splits PDO data into (object, bytes) pairs.</summary>
    public IReadOnlyList<(PdoMappedObject Object, byte[] Raw)> Unpack(ReadOnlySpan<byte> data)
    {
        if (data.Length * 8 < Bits) throw new FormatException($"PDO needs {Bits / 8} bytes, got {data.Length}.");
        var result = new List<(PdoMappedObject, byte[])>();
        var pos = 0;
        foreach (var o in Objects)
        {
            result.Add((o, data.Slice(pos, o.Bits / 8).ToArray()));
            pos += o.Bits / 8;
        }

        return result;
    }
}
