namespace IoTCom.Net.Protocols.Mavlink;

/// <summary>A MAVLink message (generated from dialect XML by the IoTCom.Net MAVLink source generator).</summary>
public interface IMavlinkMessage
{
    /// <summary>Message id.</summary>
    uint MessageId { get; }

    /// <summary>CRC_EXTRA seed derived from the message definition.</summary>
    byte CrcExtra { get; }

    /// <summary>MAVLink name, e.g. <c>HEARTBEAT</c>.</summary>
    string Name { get; }

    /// <summary>Payload length including extensions.</summary>
    int MaxPayloadLength { get; }

    /// <summary>Writes the full payload and returns its length.</summary>
    int Serialize(Span<byte> buffer);
}

/// <summary>Static facts about a message type.</summary>
/// <param name="Id">Message id.</param>
/// <param name="Name">MAVLink name.</param>
/// <param name="CrcExtra">CRC_EXTRA.</param>
/// <param name="MinLength">Payload length without extensions (MAVLink 1 length).</param>
/// <param name="MaxLength">Payload length with extensions.</param>
public readonly record struct MavlinkMessageInfo(uint Id, string Name, byte CrcExtra, int MinLength, int MaxLength);

/// <summary>A set of messages (a dialect): needed to check CRCs and to decode payloads.</summary>
public interface IMavlinkDialect
{
    /// <summary>Dialect name.</summary>
    string Name { get; }

    /// <summary>All messages.</summary>
    IReadOnlyList<MavlinkMessageInfo> Messages { get; }

    /// <summary>Looks up a message id.</summary>
    bool TryGetInfo(uint id, out MavlinkMessageInfo info);

    /// <summary>Decodes a payload, or returns null for an unknown id.</summary>
    IMavlinkMessage? Deserialize(uint id, ReadOnlySpan<byte> payload);
}

/// <summary>Combines dialects (e.g. <c>common</c> plus your own); the first dialect that knows an id wins.</summary>
public sealed class CompositeDialect(params IMavlinkDialect[] dialects) : IMavlinkDialect
{
    /// <inheritdoc />
    public string Name => string.Join("+", dialects.Select(d => d.Name));

    /// <inheritdoc />
    public IReadOnlyList<MavlinkMessageInfo> Messages => dialects.SelectMany(d => d.Messages).DistinctBy(m => m.Id).OrderBy(m => m.Id).ToList();

    /// <inheritdoc />
    public bool TryGetInfo(uint id, out MavlinkMessageInfo info)
    {
        foreach (var d in dialects)
            if (d.TryGetInfo(id, out info)) return true;
        info = default;
        return false;
    }

    /// <inheritdoc />
    public IMavlinkMessage? Deserialize(uint id, ReadOnlySpan<byte> payload)
    {
        foreach (var d in dialects)
            if (d.TryGetInfo(id, out _)) return d.Deserialize(id, payload);
        return null;
    }
}
