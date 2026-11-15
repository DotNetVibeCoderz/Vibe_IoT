using System.Globalization;

#pragma warning disable CS1591 // Enumeration members are the ASHRAE 135 names; see the standard for their meaning.

namespace IoTCom.Net.Protocols.Bacnet;

/// <summary>BACnet object types (ASHRAE 135 clause 21, the ones this library models).</summary>
public enum BacnetObjectType : ushort
{
    AnalogInput = 0, AnalogOutput = 1, AnalogValue = 2, BinaryInput = 3, BinaryOutput = 4, BinaryValue = 5, Calendar = 6,
    Command = 7, Device = 8, EventEnrollment = 9, File = 10, Group = 11, Loop = 12, MultiStateInput = 13, MultiStateOutput = 14,
    NotificationClass = 15, Program = 16, Schedule = 17, Averaging = 18, MultiStateValue = 19, TrendLog = 20,
}

/// <summary>BACnet property identifiers (a working subset of ASHRAE 135 clause 21).</summary>
public enum BacnetPropertyId : uint
{
    ActiveText = 4, All = 8, ApduTimeout = 11, ApplicationSoftwareVersion = 12, CovIncrement = 22, Description = 28,
    DeviceAddressBinding = 30, EventState = 36, FirmwareRevision = 44, HighLimit = 45, InactiveText = 46, LowLimit = 59,
    MaxApduLengthAccepted = 62, MaxPresValue = 65, MinPresValue = 69, ModelName = 70, NumberOfApduRetries = 73, NumberOfStates = 74,
    ObjectIdentifier = 75, ObjectList = 76, ObjectName = 77, ObjectType = 79, Optional = 80, OutOfService = 81, Polarity = 84,
    PresentValue = 85, PriorityArray = 87, ProtocolObjectTypesSupported = 96, ProtocolServicesSupported = 97, ProtocolVersion = 98,
    Reliability = 103, RelinquishDefault = 104, Required = 105, SegmentationSupported = 107, StateText = 110, StatusFlags = 111,
    SystemStatus = 112, Units = 117, VendorIdentifier = 120, VendorName = 121, ProtocolRevision = 139, DatabaseRevision = 155,
    CurrentCommandPriority = 431, PropertyList = 371,
}

/// <summary>Engineering units (a subset of BACnetEngineeringUnits).</summary>
public enum BacnetUnits : uint
{
    PercentRelativeHumidity = 29, Kilowatts = 48, Pascals = 53, DegreesCelsius = 62, NoUnits = 95, PartsPerMillion = 96,
    Percent = 98, CubicMetersPerHour = 135, KilowattHours = 19, Volts = 5, Amperes = 3, Hertz = 27, Seconds = 73, Minutes = 72,
}

/// <summary>Application tags.</summary>
public enum BacnetApplicationTag : byte
{
    Null = 0, Boolean = 1, UnsignedInteger = 2, SignedInteger = 3, Real = 4, Double = 5, OctetString = 6, CharacterString = 7,
    BitString = 8, Enumerated = 9, Date = 10, Time = 11, ObjectIdentifier = 12,
}

/// <summary>Error classes.</summary>
public enum BacnetErrorClass : uint
{
    Device = 0, Object = 1, Property = 2, Resources = 3, Security = 4, Services = 5,
}

/// <summary>Error codes (subset).</summary>
public enum BacnetErrorCode : uint
{
    Other = 0, ConfigurationInProgress = 2, DeviceBusy = 3, InvalidDataType = 9, MissingRequiredParameter = 16, ServiceRequestDenied = 29,
    UnknownObject = 31, UnknownProperty = 32, ValueOutOfRange = 37, WriteAccessDenied = 40, InvalidArrayIndex = 42,
    PropertyIsNotAnArray = 50, OptionalFunctionalityNotSupported = 45, Timeout = 30,
}

/// <summary>Reject reasons.</summary>
public enum BacnetRejectReason : byte
{
    Other = 0, BufferOverflow = 1, InconsistentParameters = 2, InvalidParameterDataType = 3, InvalidTag = 4, MissingRequiredParameter = 5,
    ParameterOutOfRange = 6, TooManyArguments = 7, UndefinedEnumeration = 8, UnrecognizedService = 9,
}

/// <summary>Abort reasons.</summary>
public enum BacnetAbortReason : byte
{
    Other = 0, BufferOverflow = 1, InvalidApduInThisState = 2, PreemptedByHigherPriorityTask = 3, SegmentationNotSupported = 4,
}

/// <summary>Confirmed service choices.</summary>
public enum BacnetConfirmedService : byte
{
    AcknowledgeAlarm = 0, ConfirmedCovNotification = 1, SubscribeCov = 5, ReadProperty = 12, ReadPropertyMultiple = 14, WriteProperty = 15,
    WritePropertyMultiple = 16, DeviceCommunicationControl = 17, ReinitializeDevice = 20,
}

/// <summary>Unconfirmed service choices.</summary>
public enum BacnetUnconfirmedService : byte
{
    IAm = 0, IHave = 1, UnconfirmedCovNotification = 2, TimeSynchronization = 6, WhoHas = 7, WhoIs = 8, UtcTimeSynchronization = 9,
}

/// <summary>Segmentation support (I-Am, Device object).</summary>
public enum BacnetSegmentation : uint
{
    Both = 0, Transmit = 1, Receive = 2, None = 3,
}

/// <summary>A BACnet object identifier: 10-bit type, 22-bit instance.</summary>
public readonly record struct BacnetObjectId(BacnetObjectType Type, uint Instance) : IComparable<BacnetObjectId>
{
    /// <summary>Largest instance number (4194303 is reserved for "unconfigured").</summary>
    public const uint MaxInstance = 0x3FFFFF;

    /// <summary>The 32-bit encoding.</summary>
    public uint Raw => ((uint)Type << 22) | (Instance & MaxInstance);

    /// <summary>Decodes a 32-bit object identifier.</summary>
    public static BacnetObjectId FromRaw(uint raw) => new((BacnetObjectType)(raw >> 22), raw & MaxInstance);

    /// <summary>Parses "analog-input:1", "AI1", "ai:1" or "0:1".</summary>
    public static BacnetObjectId Parse(string text)
    {
        var t = text.Trim();
        string typePart, instPart;
        var sep = t.IndexOfAny([':', ',']);
        if (sep > 0) (typePart, instPart) = (t[..sep], t[(sep + 1)..]);
        else
        {
            var i = t.Length;
            while (i > 0 && char.IsDigit(t[i - 1])) i--;
            (typePart, instPart) = (t[..i], t[i..]);
        }

        if (!uint.TryParse(instPart, NumberStyles.None, CultureInfo.InvariantCulture, out var instance) || instance > MaxInstance)
            throw new FormatException($"'{text}' is not a BACnet object identifier (for example analog-input:1 or AI1).");
        var key = typePart.Replace("-", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal).ToUpperInvariant();
        BacnetObjectType type = key switch
        {
            "AI" => BacnetObjectType.AnalogInput, "AO" => BacnetObjectType.AnalogOutput, "AV" => BacnetObjectType.AnalogValue,
            "BI" => BacnetObjectType.BinaryInput, "BO" => BacnetObjectType.BinaryOutput, "BV" => BacnetObjectType.BinaryValue,
            "MSI" => BacnetObjectType.MultiStateInput, "MSO" => BacnetObjectType.MultiStateOutput, "MSV" => BacnetObjectType.MultiStateValue,
            "DEV" or "DEVICE" => BacnetObjectType.Device,
            _ when ushort.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var n) => (BacnetObjectType)n,
            _ => Enum.GetValues<BacnetObjectType>().FirstOrDefault(v => v.ToString().Equals(key, StringComparison.OrdinalIgnoreCase), (BacnetObjectType)ushort.MaxValue),
        };
        if ((ushort)type == ushort.MaxValue) throw new FormatException($"Unknown BACnet object type '{typePart}'.");
        return new BacnetObjectId(type, instance);
    }

    /// <summary>Short name such as <c>AI1</c> for the common types, else <c>type:instance</c>.</summary>
    public string ShortName => Type switch
    {
        BacnetObjectType.AnalogInput => $"AI{Instance}", BacnetObjectType.AnalogOutput => $"AO{Instance}", BacnetObjectType.AnalogValue => $"AV{Instance}",
        BacnetObjectType.BinaryInput => $"BI{Instance}", BacnetObjectType.BinaryOutput => $"BO{Instance}", BacnetObjectType.BinaryValue => $"BV{Instance}",
        BacnetObjectType.MultiStateInput => $"MSI{Instance}", BacnetObjectType.MultiStateOutput => $"MSO{Instance}", BacnetObjectType.MultiStateValue => $"MSV{Instance}",
        BacnetObjectType.Device => $"DEV{Instance}",
        _ => ToString(),
    };

    /// <inheritdoc />
    public int CompareTo(BacnetObjectId other) => Raw.CompareTo(other.Raw);

    /// <summary>Compares encodings.</summary>
    public static bool operator <(BacnetObjectId a, BacnetObjectId b) => a.Raw < b.Raw;

    /// <summary>Compares encodings.</summary>
    public static bool operator >(BacnetObjectId a, BacnetObjectId b) => a.Raw > b.Raw;

    /// <summary>Compares encodings.</summary>
    public static bool operator <=(BacnetObjectId a, BacnetObjectId b) => a.Raw <= b.Raw;

    /// <summary>Compares encodings.</summary>
    public static bool operator >=(BacnetObjectId a, BacnetObjectId b) => a.Raw >= b.Raw;

    /// <inheritdoc />
    public override string ToString() => $"{Type}:{Instance}";
}

/// <summary>A date (year 1900–2154, or unspecified fields as 255).</summary>
public readonly record struct BacnetDate(int Year, int Month, int Day, int DayOfWeek)
{
    /// <summary>From a <see cref="DateOnly"/>.</summary>
    public static BacnetDate From(DateOnly d) => new(d.Year, d.Month, d.Day, d.DayOfWeek == System.DayOfWeek.Sunday ? 7 : (int)d.DayOfWeek);

    /// <inheritdoc />
    public override string ToString() => $"{Year:D4}-{Month:D2}-{Day:D2}";
}

/// <summary>A time of day with hundredths.</summary>
public readonly record struct BacnetTime(int Hour, int Minute, int Second, int Hundredths)
{
    /// <summary>From a <see cref="TimeOnly"/>.</summary>
    public static BacnetTime From(TimeOnly t) => new(t.Hour, t.Minute, t.Second, t.Millisecond / 10);

    /// <inheritdoc />
    public override string ToString() => $"{Hour:D2}:{Minute:D2}:{Second:D2}.{Hundredths:D2}";
}

/// <summary>A bit string (status flags, services supported).</summary>
public sealed record BacnetBitString(bool[] Bits)
{
    /// <summary>Creates a bit string of <paramref name="length"/> bits with the given bits set.</summary>
    public static BacnetBitString Of(int length, params int[] set)
    {
        var bits = new bool[length];
        foreach (var i in set) bits[i] = true;
        return new BacnetBitString(bits);
    }

    /// <inheritdoc />
    public bool Equals(BacnetBitString? other) => other is not null && Bits.AsSpan().SequenceEqual(other.Bits);

    /// <inheritdoc />
    public override int GetHashCode() => Bits.Length;

    /// <inheritdoc />
    public override string ToString() => "{" + string.Join(",", Bits.Select(b => b ? "T" : "F")) + "}";
}

/// <summary>An application-tagged value: what properties hold and what travels on the wire.</summary>
/// <param name="Tag">Application tag.</param>
/// <param name="Value">null, bool, uint, int, float, double, byte[], string, <see cref="BacnetBitString"/>, enumerated (uint), <see cref="BacnetDate"/>, <see cref="BacnetTime"/>, <see cref="BacnetObjectId"/>.</param>
public readonly record struct BacnetValue(BacnetApplicationTag Tag, object? Value)
{
    /// <summary>Null (relinquishes a priority slot).</summary>
    public static BacnetValue Null { get; } = new(BacnetApplicationTag.Null, null);

    /// <summary>A REAL.</summary>
    public static BacnetValue Real(float v) => new(BacnetApplicationTag.Real, v);

    /// <summary>An unsigned integer.</summary>
    public static BacnetValue Unsigned(uint v) => new(BacnetApplicationTag.UnsignedInteger, v);

    /// <summary>A signed integer.</summary>
    public static BacnetValue Signed(int v) => new(BacnetApplicationTag.SignedInteger, v);

    /// <summary>An enumerated value.</summary>
    public static BacnetValue Enumerated(uint v) => new(BacnetApplicationTag.Enumerated, v);

    /// <summary>A boolean.</summary>
    public static BacnetValue Boolean(bool v) => new(BacnetApplicationTag.Boolean, v);

    /// <summary>A character string.</summary>
    public static BacnetValue Text(string v) => new(BacnetApplicationTag.CharacterString, v);

    /// <summary>An object identifier.</summary>
    public static BacnetValue Object(BacnetObjectId v) => new(BacnetApplicationTag.ObjectIdentifier, v);

    /// <summary>A bit string.</summary>
    public static BacnetValue Bits(BacnetBitString v) => new(BacnetApplicationTag.BitString, v);

    /// <summary>The value as a double when it is numeric (REAL, DOUBLE, unsigned, signed, enumerated, boolean).</summary>
    public double? AsDouble() => Value switch
    {
        float f => f, double d => d, uint u => u, int i => i, bool b => b ? 1 : 0, _ => null,
    };

    /// <inheritdoc />
    public override string ToString() => Value switch
    {
        null => "null",
        float f => f.ToString("0.###", CultureInfo.InvariantCulture),
        double d => d.ToString("0.###", CultureInfo.InvariantCulture),
        byte[] b => Convert.ToHexString(b),
        bool b => b ? "true" : "false",
        _ => Convert.ToString(Value, CultureInfo.InvariantCulture) ?? "",
    };
}
