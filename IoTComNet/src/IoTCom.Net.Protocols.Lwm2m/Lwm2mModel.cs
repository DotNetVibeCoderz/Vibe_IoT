using System.Collections.Concurrent;
using System.Globalization;

namespace IoTCom.Net.Protocols.Lwm2m;

/// <summary>An LwM2M path: object, instance, resource and resource instance ("/3/0/9", "/3303", "/3/0/11/0").</summary>
/// <param name="ObjectId">Object id.</param>
/// <param name="InstanceId">Object instance id.</param>
/// <param name="ResourceId">Resource id.</param>
/// <param name="ResourceInstanceId">Resource instance id (multiple-instance resources).</param>
public readonly record struct Lwm2mPath(ushort ObjectId, ushort? InstanceId = null, ushort? ResourceId = null, ushort? ResourceInstanceId = null)
{
    /// <summary>Parses "/3/0/9" (leading slash optional).</summary>
    /// <exception cref="FormatException">Not a path of 1–4 numbers below 65535.</exception>
    public static Lwm2mPath Parse(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var parts = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is 0 or > 4) throw new FormatException($"\"{path}\" is not an LwM2M path.");
        var ids = new ushort[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (!ushort.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out ids[i]) || ids[i] == ushort.MaxValue)
                throw new FormatException($"\"{path}\" is not an LwM2M path.");
        return new Lwm2mPath(ids[0], ids.Length > 1 ? ids[1] : null, ids.Length > 2 ? ids[2] : null, ids.Length > 3 ? ids[3] : null);
    }

    /// <summary>Parses the CoAP Uri-Path segments.</summary>
    public static bool TryParse(string path, out Lwm2mPath result)
    {
        try
        {
            result = Parse(path);
            return true;
        }
        catch (FormatException)
        {
            result = default;
            return false;
        }
    }

    /// <summary>Number of levels (1 = object … 4 = resource instance).</summary>
    public int Depth => ResourceInstanceId is not null ? 4 : ResourceId is not null ? 3 : InstanceId is not null ? 2 : 1;

    /// <summary>True when <paramref name="other"/> is this path or below it.</summary>
    public bool Contains(Lwm2mPath other) =>
        other.ObjectId == ObjectId
        && (InstanceId is null || other.InstanceId == InstanceId)
        && (ResourceId is null || other.ResourceId == ResourceId)
        && (ResourceInstanceId is null || other.ResourceInstanceId == ResourceInstanceId);

    /// <summary>The CoAP Uri-Path segments.</summary>
    public IEnumerable<string> Segments()
    {
        yield return ObjectId.ToString(CultureInfo.InvariantCulture);
        if (InstanceId is { } i) yield return i.ToString(CultureInfo.InvariantCulture);
        if (ResourceId is { } r) yield return r.ToString(CultureInfo.InvariantCulture);
        if (ResourceInstanceId is { } ri) yield return ri.ToString(CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public override string ToString() => "/" + string.Join("/", Segments());
}

/// <summary>Resource data types (LwM2M 1.1 appendix C).</summary>
public enum Lwm2mType
{
    /// <summary>Executable resource (no value).</summary>
    None,
    /// <summary>UTF-8 string.</summary>
    String,
    /// <summary>Signed integer (stored as <see cref="long"/>).</summary>
    Integer,
    /// <summary>Unsigned integer (stored as <see cref="ulong"/>).</summary>
    UnsignedInteger,
    /// <summary>Float (stored as <see cref="double"/>).</summary>
    Float,
    /// <summary>Boolean.</summary>
    Boolean,
    /// <summary>Opaque bytes.</summary>
    Opaque,
    /// <summary>Time (stored as <see cref="DateTimeOffset"/>, seconds since 1970 on the wire).</summary>
    Time,
    /// <summary>Object link (stored as <see cref="Lwm2mObjectLink"/>).</summary>
    ObjectLink,
}

/// <summary>Operations a resource allows.</summary>
[Flags]
public enum Lwm2mOperations
{
    /// <summary>None.</summary>
    None = 0,
    /// <summary>Read.</summary>
    Read = 1,
    /// <summary>Write.</summary>
    Write = 2,
    /// <summary>Read and write.</summary>
    ReadWrite = Read | Write,
    /// <summary>Execute.</summary>
    Execute = 4,
}

/// <summary>An object link value (object id : instance id).</summary>
/// <param name="ObjectId">Target object.</param>
/// <param name="InstanceId">Target instance.</param>
public readonly record struct Lwm2mObjectLink(ushort ObjectId, ushort InstanceId)
{
    /// <inheritdoc />
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{ObjectId}:{InstanceId}");
}

/// <summary>A resource of an object definition.</summary>
/// <param name="Id">Resource id.</param>
/// <param name="Name">Name.</param>
/// <param name="Type">Data type.</param>
/// <param name="Operations">Allowed operations.</param>
/// <param name="Multiple">Multiple resource instances.</param>
/// <param name="Mandatory">Mandatory.</param>
/// <param name="Units">Units.</param>
public sealed record Lwm2mResourceDefinition(ushort Id, string Name, Lwm2mType Type, Lwm2mOperations Operations, bool Multiple = false, bool Mandatory = false, string? Units = null);

/// <summary>An object definition (from the OMA LwM2M registry).</summary>
/// <param name="Id">Object id.</param>
/// <param name="Name">Name.</param>
/// <param name="MultipleInstances">More than one instance allowed.</param>
/// <param name="Resources">Resources.</param>
public sealed record Lwm2mObjectDefinition(ushort Id, string Name, bool MultipleInstances, IReadOnlyList<Lwm2mResourceDefinition> Resources)
{
    /// <summary>A resource by id.</summary>
    public Lwm2mResourceDefinition? Resource(ushort id) => Resources.FirstOrDefault(r => r.Id == id);
}

/// <summary>A value at a resource or resource-instance path.</summary>
/// <param name="Path">Resource (depth 3) or resource instance (depth 4) path.</param>
/// <param name="Value">string, long, ulong, double, bool, byte[], DateTimeOffset or <see cref="Lwm2mObjectLink"/>.</param>
public sealed record Lwm2mValue(Lwm2mPath Path, object Value)
{
    /// <summary>The value's type.</summary>
    public Lwm2mType Type => TypeOf(Value);

    /// <summary>The LwM2M type of a CLR value.</summary>
    public static Lwm2mType TypeOf(object value) => value switch
    {
        string => Lwm2mType.String,
        long or int or short or sbyte => Lwm2mType.Integer,
        ulong or uint or ushort or byte => Lwm2mType.UnsignedInteger,
        double or float => Lwm2mType.Float,
        bool => Lwm2mType.Boolean,
        byte[] => Lwm2mType.Opaque,
        DateTimeOffset => Lwm2mType.Time,
        Lwm2mObjectLink => Lwm2mType.ObjectLink,
        _ => throw new ArgumentException($"{value.GetType().Name} is not an LwM2M value type.", nameof(value)),
    };

    /// <summary>Normalises int/float/etc. to the stored CLR type of <paramref name="type"/>.</summary>
    public static object Normalise(object value, Lwm2mType type) => type switch
    {
        Lwm2mType.Integer => Convert.ToInt64(value, CultureInfo.InvariantCulture),
        Lwm2mType.UnsignedInteger => Convert.ToUInt64(value, CultureInfo.InvariantCulture),
        Lwm2mType.Float => Convert.ToDouble(value, CultureInfo.InvariantCulture),
        Lwm2mType.Boolean => value is bool b ? b : Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0,
        Lwm2mType.Time => value is DateTimeOffset t ? t : DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(value, CultureInfo.InvariantCulture)),
        Lwm2mType.String => value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
        _ => value,
    };

    /// <summary>The value as text.</summary>
    public static string Format(object value) => value switch
    {
        byte[] b => Convert.ToHexString(b),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        DateTimeOffset t => t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    /// <inheritdoc />
    public override string ToString() => $"{Path} = {Format(Value)}";
}

/// <summary>Standard object definitions (the subset this library models).</summary>
public static class Lwm2mRegistry
{
    private static readonly ConcurrentDictionary<ushort, Lwm2mObjectDefinition> Known = new(new Dictionary<ushort, Lwm2mObjectDefinition>
    {
        [1] = new(1, "LwM2M Server", true,
        [
            new(0, "Short Server ID", Lwm2mType.Integer, Lwm2mOperations.Read, Mandatory: true),
            new(1, "Lifetime", Lwm2mType.Integer, Lwm2mOperations.ReadWrite, Mandatory: true, Units: "s"),
            new(2, "Default Minimum Period", Lwm2mType.Integer, Lwm2mOperations.ReadWrite, Units: "s"),
            new(3, "Default Maximum Period", Lwm2mType.Integer, Lwm2mOperations.ReadWrite, Units: "s"),
            new(6, "Notification Storing", Lwm2mType.Boolean, Lwm2mOperations.ReadWrite, Mandatory: true),
            new(7, "Binding", Lwm2mType.String, Lwm2mOperations.ReadWrite, Mandatory: true),
            new(8, "Registration Update Trigger", Lwm2mType.None, Lwm2mOperations.Execute, Mandatory: true),
        ]),
        [3] = new(3, "Device", false,
        [
            new(0, "Manufacturer", Lwm2mType.String, Lwm2mOperations.Read),
            new(1, "Model Number", Lwm2mType.String, Lwm2mOperations.Read),
            new(2, "Serial Number", Lwm2mType.String, Lwm2mOperations.Read),
            new(3, "Firmware Version", Lwm2mType.String, Lwm2mOperations.Read),
            new(4, "Reboot", Lwm2mType.None, Lwm2mOperations.Execute, Mandatory: true),
            new(6, "Available Power Sources", Lwm2mType.Integer, Lwm2mOperations.Read, Multiple: true),
            new(9, "Battery Level", Lwm2mType.Integer, Lwm2mOperations.Read, Units: "%"),
            new(11, "Error Code", Lwm2mType.Integer, Lwm2mOperations.Read, Multiple: true, Mandatory: true),
            new(13, "Current Time", Lwm2mType.Time, Lwm2mOperations.ReadWrite),
            new(14, "UTC Offset", Lwm2mType.String, Lwm2mOperations.ReadWrite),
            new(15, "Timezone", Lwm2mType.String, Lwm2mOperations.ReadWrite),
            new(16, "Supported Binding and Modes", Lwm2mType.String, Lwm2mOperations.Read, Mandatory: true),
            new(17, "Device Type", Lwm2mType.String, Lwm2mOperations.Read),
        ]),
        [6] = new(6, "Location", false,
        [
            new(0, "Latitude", Lwm2mType.Float, Lwm2mOperations.Read, Mandatory: true, Units: "lat"),
            new(1, "Longitude", Lwm2mType.Float, Lwm2mOperations.Read, Mandatory: true, Units: "lon"),
            new(2, "Altitude", Lwm2mType.Float, Lwm2mOperations.Read, Units: "m"),
            new(5, "Timestamp", Lwm2mType.Time, Lwm2mOperations.Read, Mandatory: true),
        ]),
        [3303] = new(3303, "Temperature", true,
        [
            new(5700, "Sensor Value", Lwm2mType.Float, Lwm2mOperations.Read, Mandatory: true),
            new(5601, "Min Measured Value", Lwm2mType.Float, Lwm2mOperations.Read),
            new(5602, "Max Measured Value", Lwm2mType.Float, Lwm2mOperations.Read),
            new(5605, "Reset Min and Max Measured Values", Lwm2mType.None, Lwm2mOperations.Execute),
            new(5701, "Sensor Units", Lwm2mType.String, Lwm2mOperations.Read),
        ]),
        [3311] = new(3311, "Light Control", true,
        [
            new(5850, "On/Off", Lwm2mType.Boolean, Lwm2mOperations.ReadWrite, Mandatory: true),
            new(5851, "Dimmer", Lwm2mType.Integer, Lwm2mOperations.ReadWrite, Units: "%"),
            new(5852, "On Time", Lwm2mType.Integer, Lwm2mOperations.ReadWrite, Units: "s"),
            new(5805, "Cumulative Active Power", Lwm2mType.Float, Lwm2mOperations.Read, Units: "Wh"),
            new(5820, "Power Factor", Lwm2mType.Float, Lwm2mOperations.Read),
            new(5706, "Colour", Lwm2mType.String, Lwm2mOperations.ReadWrite),
        ]),
    });

    /// <summary>Known definitions.</summary>
    public static IReadOnlyCollection<Lwm2mObjectDefinition> Objects => [.. Known.Values];

    /// <summary>The definition of an object, if known.</summary>
    public static Lwm2mObjectDefinition? Find(ushort objectId) => Known.GetValueOrDefault(objectId);

    /// <summary>The definition of a resource, if known.</summary>
    public static Lwm2mResourceDefinition? Find(ushort objectId, ushort resourceId) => Find(objectId)?.Resource(resourceId);

    /// <summary>Adds or replaces a definition (vendor objects, other OMA objects).</summary>
    public static void Register(Lwm2mObjectDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Known[definition.Id] = definition;
    }
}
