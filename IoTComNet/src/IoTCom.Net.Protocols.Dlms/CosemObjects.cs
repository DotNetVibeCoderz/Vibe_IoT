namespace IoTCom.Net.Protocols.Dlms;

/// <summary>
/// A COSEM object served by <see cref="DlmsServer"/>: an interface class instance with attributes (1 = logical name)
/// and methods. Derive from it, or use the built-in classes below.
/// </summary>
public abstract class CosemObject
{
    /// <summary>Creates the object.</summary>
    protected CosemObject(ushort classId, ObisCode logicalName, byte version = 0)
    {
        ClassId = classId;
        LogicalName = logicalName;
        Version = version;
    }

    /// <summary>Interface class.</summary>
    public ushort ClassId { get; }

    /// <summary>Logical name.</summary>
    public ObisCode LogicalName { get; }

    /// <summary>Class version.</summary>
    public byte Version { get; }

    /// <summary>Number of attributes.</summary>
    public abstract int AttributeCount { get; }

    /// <summary>Number of methods.</summary>
    public virtual int MethodCount => 0;

    /// <summary>Attributes a client may write (subject to its access level).</summary>
    public virtual IReadOnlyCollection<int> WritableAttributes => [];

    /// <summary>Reads an attribute; returns the value or an error.</summary>
    public (CosemData? Value, DataAccessResult Result) Get(int attribute, CosemAccess? access)
    {
        if (attribute == 1) return (CosemData.OctetString(LogicalName.ToBytes()), DataAccessResult.Success);
        if (attribute < 1 || attribute > AttributeCount) return (null, DataAccessResult.ObjectUndefined);
        try
        {
            return GetAttribute(attribute, access) is { } v ? (v, DataAccessResult.Success) : (null, DataAccessResult.ObjectUnavailable);
        }
        catch (ArgumentException)
        {
            return (null, DataAccessResult.TypeUnmatched);
        }
        catch (InvalidCastException)
        {
            return (null, DataAccessResult.TypeUnmatched);
        }
    }

    /// <summary>Writes an attribute.</summary>
    public DataAccessResult Set(int attribute, CosemData value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!WritableAttributes.Contains(attribute)) return DataAccessResult.ReadWriteDenied;
        try
        {
            return SetAttribute(attribute, value);
        }
        catch (Exception ex) when (ex is InvalidCastException or ArgumentException)
        {
            return DataAccessResult.TypeUnmatched;
        }
    }

    /// <summary>Invokes a method.</summary>
    public (ActionResult Result, CosemData? Return) Invoke(int method, CosemData? parameter)
    {
        if (method < 1 || method > MethodCount) return (ActionResult.ObjectUndefined, null);
        try
        {
            return InvokeMethod(method, parameter);
        }
        catch (Exception ex) when (ex is InvalidCastException or ArgumentException)
        {
            return (ActionResult.TypeUnmatched, null);
        }
    }

    /// <summary>Reads attribute 2…n; null means unavailable.</summary>
    protected abstract CosemData? GetAttribute(int attribute, CosemAccess? access);

    /// <summary>Writes a writable attribute.</summary>
    protected virtual DataAccessResult SetAttribute(int attribute, CosemData value) => DataAccessResult.ReadWriteDenied;

    /// <summary>Runs a method.</summary>
    protected virtual (ActionResult, CosemData?) InvokeMethod(int method, CosemData? parameter) => (ActionResult.ObjectUnavailable, null);

    /// <inheritdoc />
    public override string ToString() => $"{CosemClass.Name(ClassId)} {LogicalName}{(LogicalName.Description is { } d ? $" ({d})" : "")}";
}

/// <summary>Data (class 1): one value.</summary>
public sealed class CosemDataObject(ObisCode logicalName, Func<CosemData> value, Action<CosemData>? setter = null) : CosemObject(CosemClass.Data, logicalName)
{
    /// <inheritdoc />
    public override int AttributeCount => 2;

    /// <inheritdoc />
    public override IReadOnlyCollection<int> WritableAttributes => setter is null ? [] : [2];

    /// <inheritdoc />
    protected override CosemData? GetAttribute(int attribute, CosemAccess? access) => value();

    /// <inheritdoc />
    protected override DataAccessResult SetAttribute(int attribute, CosemData data)
    {
        setter!(data);
        return DataAccessResult.Success;
    }
}

/// <summary>Register (class 3): a value with a decimal scaler and a unit.</summary>
public sealed class CosemRegister : CosemObject
{
    private readonly Func<double> _value;

    /// <summary>Creates a register whose physical value is <paramref name="value"/> (in <paramref name="unit"/>).</summary>
    /// <param name="logicalName">OBIS code.</param>
    /// <param name="value">Physical value provider.</param>
    /// <param name="scaler">Decimal exponent: the transmitted integer is value / 10^scaler.</param>
    /// <param name="unit">Unit code (<see cref="CosemUnit"/>).</param>
    /// <param name="type">Integer type of attribute 2.</param>
    public CosemRegister(ObisCode logicalName, Func<double> value, sbyte scaler, byte unit, CosemDataType type = CosemDataType.UInt32)
        : base(CosemClass.Register, logicalName)
    {
        _value = value ?? throw new ArgumentNullException(nameof(value));
        Scaler = scaler;
        Unit = unit;
        Type = type;
    }

    /// <summary>Scaler.</summary>
    public sbyte Scaler { get; }

    /// <summary>Unit code.</summary>
    public byte Unit { get; }

    /// <summary>Type of the raw value.</summary>
    public CosemDataType Type { get; }

    /// <inheritdoc />
    public override int AttributeCount => 3;

    /// <inheritdoc />
    public override int MethodCount => 1;

    /// <summary>The raw value attribute 2 carries now.</summary>
    public CosemData RawValue => CosemData.Integer(Type, (long)Math.Round(_value() / Math.Pow(10, Scaler)));

    /// <inheritdoc />
    protected override CosemData? GetAttribute(int attribute, CosemAccess? access) => attribute switch
    {
        2 => RawValue,
        3 => CosemData.Structure(CosemData.Int8(Scaler), CosemData.Enum(Unit)),
        _ => null,
    };

    /// <inheritdoc />
    protected override (ActionResult, CosemData?) InvokeMethod(int method, CosemData? parameter) => (ActionResult.ReadWriteDenied, null); // reset: not for billing registers
}

/// <summary>Clock (class 8): local time and time zone.</summary>
public sealed class CosemClock(ObisCode logicalName, Func<DateTimeOffset> now) : CosemObject(CosemClass.Clock, logicalName)
{
    /// <summary>Offset applied by clients through SET (clock adjustment).</summary>
    public TimeSpan Adjustment { get; private set; }

    /// <summary>The meter's current time.</summary>
    public DateTimeOffset Now => now() + Adjustment;

    /// <inheritdoc />
    public override int AttributeCount => 9;

    /// <inheritdoc />
    public override IReadOnlyCollection<int> WritableAttributes => [2];

    /// <inheritdoc />
    protected override CosemData? GetAttribute(int attribute, CosemAccess? access) => attribute switch
    {
        2 => CosemData.DateTime(Now),
        3 => CosemData.Int16((short)-Now.Offset.TotalMinutes),
        4 => CosemData.UInt8(0),
        8 => CosemData.Boolean(false),
        9 => CosemData.Enum(1),
        _ => null,
    };

    /// <inheritdoc />
    protected override DataAccessResult SetAttribute(int attribute, CosemData value)
    {
        if (value.AsDateTime() is not { } t) return DataAccessResult.TypeUnmatched;
        Adjustment = t - now();
        return DataAccessResult.Success;
    }
}

/// <summary>A capture object of a profile: which attribute fills a column.</summary>
/// <param name="ClassId">Class.</param>
/// <param name="LogicalName">Object.</param>
/// <param name="Attribute">Attribute.</param>
public sealed record CaptureObject(ushort ClassId, ObisCode LogicalName, sbyte Attribute = 2)
{
    internal CosemData Encode() => CosemData.Structure(CosemData.UInt16(ClassId), CosemData.OctetString(LogicalName.ToBytes()), CosemData.Int8(Attribute), CosemData.UInt16(0));
}

/// <summary>
/// Profile generic (class 7): a buffer of captured rows (load profile, event log) with capture objects, a capture
/// period, and selective access by range (selector 1, on the first column) or by entry (selector 2).
/// </summary>
public sealed class CosemProfileGeneric : CosemObject
{
    private readonly List<CosemData[]> _rows = [];
    private readonly Lock _gate = new();

    /// <summary>Creates a profile.</summary>
    public CosemProfileGeneric(ObisCode logicalName, IReadOnlyList<CaptureObject> captureObjects, uint capturePeriodSeconds, int profileEntries = 2000)
        : base(CosemClass.ProfileGeneric, logicalName, 1)
    {
        CaptureObjects = captureObjects ?? throw new ArgumentNullException(nameof(captureObjects));
        CapturePeriod = capturePeriodSeconds;
        ProfileEntries = profileEntries;
    }

    /// <summary>Columns.</summary>
    public IReadOnlyList<CaptureObject> CaptureObjects { get; }

    /// <summary>Capture period in seconds.</summary>
    public uint CapturePeriod { get; }

    /// <summary>Maximum rows kept.</summary>
    public int ProfileEntries { get; }

    /// <summary>Rows in use.</summary>
    public int EntriesInUse
    {
        get
        {
            lock (_gate) return _rows.Count;
        }
    }

    /// <summary>Appends a row (one value per capture object).</summary>
    public void Capture(params CosemData[] row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.Length != CaptureObjects.Count) throw new ArgumentException($"A row has {CaptureObjects.Count} values.", nameof(row));
        lock (_gate)
        {
            _rows.Add(row);
            if (_rows.Count > ProfileEntries) _rows.RemoveAt(0);
        }
    }

    /// <inheritdoc />
    public override int AttributeCount => 8;

    /// <inheritdoc />
    public override int MethodCount => 2;

    /// <inheritdoc />
    protected override CosemData? GetAttribute(int attribute, CosemAccess? access) => attribute switch
    {
        2 => Buffer(access),
        3 => CosemData.Array(CaptureObjects.Select(c => c.Encode())),
        4 => CosemData.UInt32(CapturePeriod),
        5 => CosemData.Enum(1),
        6 => CaptureObjects.Count > 0 ? CaptureObjects[0].Encode() : CosemData.Null,
        7 => CosemData.UInt32((uint)EntriesInUse),
        8 => CosemData.UInt32((uint)ProfileEntries),
        _ => null,
    };

    private CosemData Buffer(CosemAccess? access)
    {
        List<CosemData[]> rows;
        lock (_gate) rows = [.. _rows];
        if (access is { Selector: 1 } range)
        {
            // range_descriptor: { restricting_object, from_value, to_value, selected_values }
            var p = range.Parameters;
            var from = p[1].AsDateTime();
            var to = p[2].AsDateTime();
            rows = [.. rows.Where(r => r[0].AsDateTime() is { } t && (from is null || t >= from) && (to is null || t <= to))];
        }
        else if (access is { Selector: 2 } entry)
        {
            // entry_descriptor: { from_entry, to_entry, from_selected_value, to_selected_value }, 1-based, 0 = last
            var first = (int)Math.Max(1, entry.Parameters[0].AsInt64());
            var last = entry.Parameters[1].AsInt64() is var l and > 0 ? (int)l : rows.Count;
            rows = [.. rows.Skip(first - 1).Take(Math.Max(0, last - first + 1))];
        }
        else if (access is not null)
        {
            throw new ArgumentException($"Selector {access.Selector} is not supported.");
        }

        return CosemData.Array(rows.Select(r => CosemData.Structure(r)));
    }

    /// <inheritdoc />
    protected override (ActionResult, CosemData?) InvokeMethod(int method, CosemData? parameter)
    {
        if (method == 1)
        {
            lock (_gate) _rows.Clear();
            return (ActionResult.Success, null);
        }

        return (ActionResult.ReadWriteDenied, null);
    }
}

/// <summary>Disconnect control (class 70): the supply relay, switched by remote_disconnect / remote_reconnect.</summary>
public sealed class CosemDisconnectControl(ObisCode logicalName) : CosemObject(CosemClass.DisconnectControl, logicalName)
{
    /// <summary>Supply connected.</summary>
    public bool Connected { get; private set; } = true;

    /// <summary>Raised when the relay changes (true = connected).</summary>
    public event Action<bool>? Switched;

    /// <inheritdoc />
    public override int AttributeCount => 4;

    /// <inheritdoc />
    public override int MethodCount => 2;

    /// <inheritdoc />
    protected override CosemData? GetAttribute(int attribute, CosemAccess? access) => attribute switch
    {
        2 => CosemData.Boolean(Connected),
        3 => CosemData.Enum(Connected ? (byte)1 : (byte)0),
        4 => CosemData.Enum(1),
        _ => null,
    };

    /// <inheritdoc />
    protected override (ActionResult, CosemData?) InvokeMethod(int method, CosemData? parameter)
    {
        var connect = method == 2;
        if (Connected != connect)
        {
            Connected = connect;
            Switched?.Invoke(connect);
        }

        return (ActionResult.Success, null);
    }
}

/// <summary>Association LN (class 15): the object list of the current association (and HLS authentication, handled by the server).</summary>
public sealed class CosemAssociationLn(ObisCode logicalName, Func<IEnumerable<CosemObject>> objects) : CosemObject(CosemClass.AssociationLn, logicalName, 1)
{
    /// <inheritdoc />
    public override int AttributeCount => 9;

    /// <inheritdoc />
    public override int MethodCount => 1;

    /// <inheritdoc />
    protected override CosemData? GetAttribute(int attribute, CosemAccess? access) => attribute switch
    {
        2 => CosemData.Array(objects().Select(o => CosemData.Structure(
            CosemData.UInt16(o.ClassId),
            CosemData.UInt8(o.Version),
            CosemData.OctetString(o.LogicalName.ToBytes()),
            CosemData.Structure(
                CosemData.Array(Enumerable.Range(1, o.AttributeCount).Select(a => CosemData.Structure(
                    CosemData.Int8((sbyte)a), CosemData.Enum(o.WritableAttributes.Contains(a) ? (byte)3 : (byte)1), CosemData.Null))),
                CosemData.Array(Enumerable.Range(1, o.MethodCount).Select(m => CosemData.Structure(CosemData.Int8((sbyte)m), CosemData.Boolean(true)))))))),
        3 => CosemData.Structure(CosemData.Int8(16), CosemData.UInt16(1)),
        8 => CosemData.Enum(2),
        _ => null,
    };

    /// <inheritdoc />
    protected override (ActionResult, CosemData?) InvokeMethod(int method, CosemData? parameter) => (ActionResult.ReadWriteDenied, null);
}
