namespace IoTCom.Net.Protocols.Bacnet;

/// <summary>Why a property read or write failed (becomes a BACnet Error-PDU).</summary>
public sealed class BacnetErrorException : ProtocolException
{
    /// <summary>Creates the exception.</summary>
    public BacnetErrorException(BacnetErrorClass errorClass, BacnetErrorCode errorCode, string? message = null)
        : base(message ?? $"{errorClass}/{errorCode}") => (ErrorClass, ErrorCode) = (errorClass, errorCode);

    /// <summary>Creates the exception.</summary>
    public BacnetErrorException() : this(BacnetErrorClass.Services, BacnetErrorCode.Other) { }

    /// <summary>Creates the exception.</summary>
    public BacnetErrorException(string message) : this(BacnetErrorClass.Services, BacnetErrorCode.Other, message) { }

    /// <summary>Creates the exception.</summary>
    public BacnetErrorException(string message, Exception? inner) : base(message, inner) => (ErrorClass, ErrorCode) = (BacnetErrorClass.Services, BacnetErrorCode.Other);

    /// <summary>Error class.</summary>
    public BacnetErrorClass ErrorClass { get; }

    /// <summary>Error code.</summary>
    public BacnetErrorCode ErrorCode { get; }
}

/// <summary>
/// A BACnet object hosted by <see cref="BacnetDevice"/>: analog, binary and multi-state inputs, outputs and values with the
/// required properties, a 16-level priority array for commandable objects, COV increments and out-of-service handling.
/// </summary>
public sealed class BacnetObject
{
    private readonly Dictionary<BacnetPropertyId, Func<IReadOnlyList<BacnetValue>>> _extra = [];
    private readonly BacnetValue?[] _priority = new BacnetValue?[16];
    private readonly Lock _gate = new();
    private BacnetValue _value;
    private BacnetValue _relinquishDefault;
    private bool _outOfService;
    private bool _fault;

    private BacnetObject(BacnetObjectId id, string name, BacnetValue value, bool commandable)
    {
        Id = id;
        Name = name;
        _value = value;
        _relinquishDefault = value;
        Commandable = commandable;
    }

    /// <summary>Object identifier.</summary>
    public BacnetObjectId Id { get; }

    /// <summary>Object name (unique within the device).</summary>
    public string Name { get; }

    /// <summary>Description.</summary>
    public string Description { get; set; } = "";

    /// <summary>Engineering units (analog objects).</summary>
    public BacnetUnits? Units { get; private init; }

    /// <summary>COV increment (analog objects; a change at least this large is notified).</summary>
    public float? CovIncrement { get; set; }

    /// <summary>State texts (multi-state objects, 1-based present value).</summary>
    public IReadOnlyList<string> StateText { get; private init; } = [];

    /// <summary>Active/inactive texts (binary objects).</summary>
    public (string Active, string Inactive) BinaryText { get; private init; } = ("Active", "Inactive");

    /// <summary>True for objects whose present value is commanded through the priority array.</summary>
    public bool Commandable { get; }

    /// <summary>True when the present value may be written by clients (commandable, or a value object, or out of service).</summary>
    public bool PresentValueWritable => Commandable || Id.Type is BacnetObjectType.AnalogValue or BacnetObjectType.BinaryValue or BacnetObjectType.MultiStateValue || OutOfService;

    /// <summary>Raised when the present value or status flags change (property, new value).</summary>
    public event Action<BacnetObject, BacnetPropertyId>? Changed;

    /// <summary>Out of service: the present value is decoupled from the physical input and may be written.</summary>
    public bool OutOfService
    {
        get => _outOfService;
        set
        {
            if (_outOfService == value) return;
            _outOfService = value;
            Changed?.Invoke(this, BacnetPropertyId.StatusFlags);
        }
    }

    /// <summary>Fault flag of the status flags (for example a broken sensor).</summary>
    public bool Fault
    {
        get => _fault;
        set
        {
            if (_fault == value) return;
            _fault = value;
            Changed?.Invoke(this, BacnetPropertyId.StatusFlags);
        }
    }

    /// <summary>Status flags: in-alarm, fault, overridden, out-of-service.</summary>
    public BacnetBitString StatusFlags => new([false, _fault, false, _outOfService]);

    /// <summary>The present value (for commandable objects: the highest-priority command, else the relinquish default).</summary>
    public BacnetValue PresentValue
    {
        get
        {
            lock (_gate)
            {
                if (!Commandable) return _value;
                foreach (var p in _priority)
                    if (p is { } v) return v;
                return _relinquishDefault;
            }
        }
    }

    /// <summary>The present value as a number.</summary>
    public double Value => PresentValue.AsDouble() ?? double.NaN;

    /// <summary>The priority level currently in control (1–16), or null when the relinquish default applies.</summary>
    public int? ActivePriority
    {
        get
        {
            lock (_gate)
            {
                for (var i = 0; i < 16; i++)
                    if (_priority[i] is not null) return i + 1;
                return null;
            }
        }
    }

    /// <summary>Priority array slots (index 0 = priority 1).</summary>
    public IReadOnlyList<BacnetValue?> PriorityArray
    {
        get
        {
            lock (_gate) return [.. _priority];
        }
    }

    /// <summary>Updates the present value from the process (inputs, or the device's own logic). Ignored while out of service.</summary>
    public void Update(double value)
    {
        if (_outOfService) return;
        SetRaw(Coerce(value));
    }

    /// <summary>The relinquish default of a commandable object.</summary>
    public BacnetValue RelinquishDefault
    {
        get
        {
            lock (_gate) return _relinquishDefault;
        }
    }

    private void SetRaw(BacnetValue v)
    {
        BacnetValue before;
        lock (_gate)
        {
            before = PresentValue;
            if (Commandable) _relinquishDefault = v;
            else _value = v;
        }

        if (!before.Equals(PresentValue)) Changed?.Invoke(this, BacnetPropertyId.PresentValue);
    }

    private BacnetValue Coerce(double v) => Id.Type switch
    {
        BacnetObjectType.AnalogInput or BacnetObjectType.AnalogOutput or BacnetObjectType.AnalogValue => BacnetValue.Real((float)v),
        BacnetObjectType.BinaryInput or BacnetObjectType.BinaryOutput or BacnetObjectType.BinaryValue => BacnetValue.Enumerated(v != 0 ? 1u : 0u),
        _ => BacnetValue.Unsigned((uint)v),
    };

    /// <summary>Adds a custom read-only property.</summary>
    public BacnetObject With(BacnetPropertyId property, Func<IReadOnlyList<BacnetValue>> read)
    {
        _extra[property] = read;
        return this;
    }

    // ---- factories ----------------------------------------------------------------------------------------------------

    /// <summary>An analog input.</summary>
    public static BacnetObject AnalogInput(uint instance, string name, BacnetUnits units, double value, float covIncrement = 0.1f) =>
        new(new(BacnetObjectType.AnalogInput, instance), name, BacnetValue.Real((float)value), false) { Units = units, CovIncrement = covIncrement };

    /// <summary>An analog output (commandable).</summary>
    public static BacnetObject AnalogOutput(uint instance, string name, BacnetUnits units, double relinquishDefault, float covIncrement = 1f) =>
        new(new(BacnetObjectType.AnalogOutput, instance), name, BacnetValue.Real((float)relinquishDefault), true) { Units = units, CovIncrement = covIncrement };

    /// <summary>An analog value (commandable when <paramref name="commandable"/>).</summary>
    public static BacnetObject AnalogValue(uint instance, string name, BacnetUnits units, double value, bool commandable = true, float covIncrement = 0.1f) =>
        new(new(BacnetObjectType.AnalogValue, instance), name, BacnetValue.Real((float)value), commandable) { Units = units, CovIncrement = covIncrement };

    /// <summary>A binary input.</summary>
    public static BacnetObject BinaryInput(uint instance, string name, bool value, string active = "On", string inactive = "Off") =>
        new(new(BacnetObjectType.BinaryInput, instance), name, BacnetValue.Enumerated(value ? 1u : 0u), false) { BinaryText = (active, inactive) };

    /// <summary>A binary output (commandable).</summary>
    public static BacnetObject BinaryOutput(uint instance, string name, bool relinquishDefault, string active = "On", string inactive = "Off") =>
        new(new(BacnetObjectType.BinaryOutput, instance), name, BacnetValue.Enumerated(relinquishDefault ? 1u : 0u), true) { BinaryText = (active, inactive) };

    /// <summary>A binary value.</summary>
    public static BacnetObject BinaryValue(uint instance, string name, bool value, bool commandable = true, string active = "On", string inactive = "Off") =>
        new(new(BacnetObjectType.BinaryValue, instance), name, BacnetValue.Enumerated(value ? 1u : 0u), commandable) { BinaryText = (active, inactive) };

    /// <summary>A multi-state input (states are 1-based).</summary>
    public static BacnetObject MultiStateInput(uint instance, string name, IReadOnlyList<string> states, uint value) =>
        new(new(BacnetObjectType.MultiStateInput, instance), name, BacnetValue.Unsigned(value), false) { StateText = states };

    /// <summary>A multi-state value (states are 1-based).</summary>
    public static BacnetObject MultiStateValue(uint instance, string name, IReadOnlyList<string> states, uint value, bool commandable = true) =>
        new(new(BacnetObjectType.MultiStateValue, instance), name, BacnetValue.Unsigned(value), commandable) { StateText = states };

    // ---- properties ---------------------------------------------------------------------------------------------------

    private bool IsAnalog => Id.Type is BacnetObjectType.AnalogInput or BacnetObjectType.AnalogOutput or BacnetObjectType.AnalogValue;

    private bool IsBinary => Id.Type is BacnetObjectType.BinaryInput or BacnetObjectType.BinaryOutput or BacnetObjectType.BinaryValue;

    private bool IsMultiState => Id.Type is BacnetObjectType.MultiStateInput or BacnetObjectType.MultiStateOutput or BacnetObjectType.MultiStateValue;

    /// <summary>Properties this object has (what <c>PropertyList</c> and <c>ALL</c> return).</summary>
    public IReadOnlyList<BacnetPropertyId> Properties
    {
        get
        {
            var list = new List<BacnetPropertyId>
            {
                BacnetPropertyId.ObjectIdentifier, BacnetPropertyId.ObjectName, BacnetPropertyId.ObjectType, BacnetPropertyId.PresentValue,
                BacnetPropertyId.Description, BacnetPropertyId.StatusFlags, BacnetPropertyId.EventState, BacnetPropertyId.OutOfService,
            };
            if (IsAnalog) list.AddRange([BacnetPropertyId.Units, BacnetPropertyId.CovIncrement]);
            if (IsBinary)
            {
                list.AddRange([BacnetPropertyId.ActiveText, BacnetPropertyId.InactiveText]);
                if (Id.Type != BacnetObjectType.BinaryValue) list.Add(BacnetPropertyId.Polarity);
            }

            if (IsMultiState) list.AddRange([BacnetPropertyId.NumberOfStates, BacnetPropertyId.StateText]);
            if (Commandable) list.AddRange([BacnetPropertyId.PriorityArray, BacnetPropertyId.RelinquishDefault, BacnetPropertyId.CurrentCommandPriority]);
            list.AddRange(_extra.Keys.Where(k => !list.Contains(k)));
            list.Add(BacnetPropertyId.PropertyList);
            return list;
        }
    }

    /// <summary>Reads a property (array index honoured for PriorityArray, StateText and PropertyList).</summary>
    /// <exception cref="BacnetErrorException">Unknown property, wrong index, or not an array.</exception>
    public IReadOnlyList<BacnetValue> Read(BacnetPropertyReference reference)
    {
        var all = reference.Property switch
        {
            BacnetPropertyId.ObjectIdentifier => [BacnetValue.Object(Id)],
            BacnetPropertyId.ObjectName => [BacnetValue.Text(Name)],
            BacnetPropertyId.ObjectType => [BacnetValue.Enumerated((uint)Id.Type)],
            BacnetPropertyId.PresentValue => [PresentValue],
            BacnetPropertyId.Description => [BacnetValue.Text(Description)],
            BacnetPropertyId.StatusFlags => [BacnetValue.Bits(StatusFlags)],
            BacnetPropertyId.EventState => [BacnetValue.Enumerated(0)],
            BacnetPropertyId.OutOfService => [BacnetValue.Boolean(_outOfService)],
            BacnetPropertyId.Units when IsAnalog => [BacnetValue.Enumerated((uint)(Units ?? BacnetUnits.NoUnits))],
            BacnetPropertyId.CovIncrement when IsAnalog => [BacnetValue.Real(CovIncrement ?? 0)],
            BacnetPropertyId.ActiveText when IsBinary => [BacnetValue.Text(BinaryText.Active)],
            BacnetPropertyId.InactiveText when IsBinary => [BacnetValue.Text(BinaryText.Inactive)],
            BacnetPropertyId.Polarity when IsBinary && Id.Type != BacnetObjectType.BinaryValue => [BacnetValue.Enumerated(0)],
            BacnetPropertyId.NumberOfStates when IsMultiState => [BacnetValue.Unsigned((uint)StateText.Count)],
            BacnetPropertyId.StateText when IsMultiState => [.. StateText.Select(BacnetValue.Text)],
            BacnetPropertyId.PriorityArray when Commandable => [.. PriorityArray.Select(v => v ?? BacnetValue.Null)],
            BacnetPropertyId.RelinquishDefault when Commandable => [RelinquishDefault],
            BacnetPropertyId.CurrentCommandPriority when Commandable => [ActivePriority is { } p ? BacnetValue.Unsigned((uint)p) : BacnetValue.Null],
            BacnetPropertyId.PropertyList => [.. Properties.Where(p => p is not (BacnetPropertyId.ObjectIdentifier or BacnetPropertyId.ObjectName or BacnetPropertyId.ObjectType or BacnetPropertyId.PropertyList)).Select(p => BacnetValue.Enumerated((uint)p))],
            _ when _extra.TryGetValue(reference.Property, out var read) => read(),
            _ => throw new BacnetErrorException(BacnetErrorClass.Property, BacnetErrorCode.UnknownProperty, $"{Id} has no property {reference.Property}."),
        };
        return Index(reference, all, reference.Property is BacnetPropertyId.PriorityArray or BacnetPropertyId.StateText or BacnetPropertyId.PropertyList);
    }

    internal static IReadOnlyList<BacnetValue> Index(BacnetPropertyReference reference, IReadOnlyList<BacnetValue> all, bool isArray)
    {
        if (reference.ArrayIndex is not { } index) return all;
        if (!isArray) throw new BacnetErrorException(BacnetErrorClass.Property, BacnetErrorCode.PropertyIsNotAnArray, $"{reference.Property} is not an array.");
        if (index == 0) return [BacnetValue.Unsigned((uint)all.Count)];
        if (index > all.Count) throw new BacnetErrorException(BacnetErrorClass.Property, BacnetErrorCode.InvalidArrayIndex, $"{reference.Property} has {all.Count} elements.");
        return [all[(int)index - 1]];
    }

    /// <summary>Writes a property from a client (WriteProperty); priority 1–16 applies to commandable present values (default 16).</summary>
    /// <exception cref="BacnetErrorException">Not writable, wrong data type, or out of range.</exception>
    public void Write(BacnetPropertyReference reference, IReadOnlyList<BacnetValue> values, byte? priority)
    {
        if (values.Count != 1) throw new BacnetErrorException(BacnetErrorClass.Property, BacnetErrorCode.InvalidDataType, "Exactly one value is expected.");
        var v = values[0];
        switch (reference.Property)
        {
            case BacnetPropertyId.PresentValue when PresentValueWritable:
                if (reference.ArrayIndex is not null) throw new BacnetErrorException(BacnetErrorClass.Property, BacnetErrorCode.PropertyIsNotAnArray);
                WritePresentValue(v, priority);
                return;
            case BacnetPropertyId.OutOfService:
                OutOfService = v.Value is bool b ? b : throw new BacnetErrorException(BacnetErrorClass.Property, BacnetErrorCode.InvalidDataType, "OutOfService takes a BOOLEAN.");
                return;
            case BacnetPropertyId.RelinquishDefault when Commandable:
                var d = Validate(v);
                BacnetValue before;
                lock (_gate)
                {
                    before = PresentValue;
                    _relinquishDefault = d;
                }

                if (!before.Equals(PresentValue)) Changed?.Invoke(this, BacnetPropertyId.PresentValue);
                return;
            case BacnetPropertyId.CovIncrement when IsAnalog:
                CovIncrement = (float)(v.AsDouble() ?? throw new BacnetErrorException(BacnetErrorClass.Property, BacnetErrorCode.InvalidDataType));
                return;
            default:
                if (reference.Property == BacnetPropertyId.PresentValue || Properties.Contains(reference.Property) || _extra.ContainsKey(reference.Property))
                    throw new BacnetErrorException(BacnetErrorClass.Property, BacnetErrorCode.WriteAccessDenied, $"{reference.Property} of {Id} is read-only.");
                throw new BacnetErrorException(BacnetErrorClass.Property, BacnetErrorCode.UnknownProperty, $"{Id} has no property {reference.Property}.");
        }
    }

    private BacnetValue Validate(BacnetValue v)
    {
        if (IsAnalog)
        {
            if (v.Tag is not (BacnetApplicationTag.Real or BacnetApplicationTag.Double or BacnetApplicationTag.UnsignedInteger or BacnetApplicationTag.SignedInteger))
                throw new BacnetErrorException(BacnetErrorClass.Property, BacnetErrorCode.InvalidDataType, $"{Id} takes a REAL.");
            return BacnetValue.Real((float)v.AsDouble()!.Value);
        }

        if (IsBinary)
        {
            if (v.Tag is not (BacnetApplicationTag.Enumerated or BacnetApplicationTag.UnsignedInteger or BacnetApplicationTag.Boolean))
                throw new BacnetErrorException(BacnetErrorClass.Property, BacnetErrorCode.InvalidDataType, $"{Id} takes an ENUMERATED (0 inactive, 1 active).");
            var n = v.AsDouble()!.Value;
            if (n is not (0 or 1)) throw new BacnetErrorException(BacnetErrorClass.Property, BacnetErrorCode.ValueOutOfRange, $"{Id} accepts 0 or 1.");
            return BacnetValue.Enumerated((uint)n);
        }

        if (v.Tag is not (BacnetApplicationTag.UnsignedInteger or BacnetApplicationTag.Enumerated))
            throw new BacnetErrorException(BacnetErrorClass.Property, BacnetErrorCode.InvalidDataType, $"{Id} takes an UNSIGNED state number.");
        var s = (uint)v.AsDouble()!.Value;
        if (s < 1 || s > StateText.Count) throw new BacnetErrorException(BacnetErrorClass.Property, BacnetErrorCode.ValueOutOfRange, $"{Id} has states 1–{StateText.Count}.");
        return BacnetValue.Unsigned(s);
    }

    private void WritePresentValue(BacnetValue v, byte? priority)
    {
        BacnetValue before;
        if (Commandable)
        {
            var slot = (priority ?? 16) - 1;
            if (slot == 5) throw new BacnetErrorException(BacnetErrorClass.Property, BacnetErrorCode.WriteAccessDenied, "Priority 6 is reserved for minimum on/off.");
            var value = v.Tag == BacnetApplicationTag.Null ? (BacnetValue?)null : Validate(v);
            lock (_gate)
            {
                before = PresentValue;
                _priority[slot] = value;
            }
        }
        else
        {
            if (v.Tag == BacnetApplicationTag.Null) throw new BacnetErrorException(BacnetErrorClass.Property, BacnetErrorCode.InvalidDataType, $"{Id} is not commandable; NULL cannot be written.");
            var value = Validate(v);
            lock (_gate)
            {
                before = _value;
                _value = value;
            }
        }

        if (!before.Equals(PresentValue)) Changed?.Invoke(this, BacnetPropertyId.PresentValue);
    }

    /// <summary>Formats the present value with units or state text.</summary>
    public string FormatValue()
    {
        var v = PresentValue;
        if (IsBinary) return v.AsDouble() == 1 ? BinaryText.Active : BinaryText.Inactive;
        if (IsMultiState && v.AsDouble() is { } s && s >= 1 && s <= StateText.Count) return StateText[(int)s - 1];
        return v + (Units is { } u ? " " + BacnetText.Unit(u) : "");
    }

    /// <inheritdoc />
    public override string ToString() => $"{Id.ShortName} {Name} = {FormatValue()}";
}

/// <summary>Display texts for units.</summary>
public static class BacnetText
{
    /// <summary>Symbol of a unit.</summary>
    public static string Unit(BacnetUnits units) => units switch
    {
        BacnetUnits.DegreesCelsius => "°C",
        BacnetUnits.Percent => "%",
        BacnetUnits.PercentRelativeHumidity => "%RH",
        BacnetUnits.Pascals => "Pa",
        BacnetUnits.PartsPerMillion => "ppm",
        BacnetUnits.Kilowatts => "kW",
        BacnetUnits.KilowattHours => "kWh",
        BacnetUnits.CubicMetersPerHour => "m³/h",
        BacnetUnits.Volts => "V",
        BacnetUnits.Amperes => "A",
        BacnetUnits.Hertz => "Hz",
        BacnetUnits.Seconds => "s",
        BacnetUnits.Minutes => "min",
        _ => "",
    };
}
