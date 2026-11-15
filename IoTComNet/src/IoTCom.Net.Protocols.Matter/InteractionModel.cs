using System.Globalization;

namespace IoTCom.Net.Protocols.Matter;

/// <summary>Interaction Model status codes (core spec §8.10).</summary>
public enum MatterStatus : byte
{
    /// <summary>Success.</summary>
    Success = 0x00,
    /// <summary>Failure.</summary>
    Failure = 0x01,
    /// <summary>Invalid subscription.</summary>
    InvalidSubscription = 0x7D,
    /// <summary>Access denied.</summary>
    UnsupportedAccess = 0x7E,
    /// <summary>Endpoint does not exist.</summary>
    UnsupportedEndpoint = 0x7F,
    /// <summary>Invalid action.</summary>
    InvalidAction = 0x80,
    /// <summary>Command not supported.</summary>
    UnsupportedCommand = 0x81,
    /// <summary>Command fields invalid.</summary>
    InvalidCommand = 0x85,
    /// <summary>Attribute not supported.</summary>
    UnsupportedAttribute = 0x86,
    /// <summary>Value out of range.</summary>
    ConstraintError = 0x87,
    /// <summary>Attribute is read-only.</summary>
    UnsupportedWrite = 0x88,
    /// <summary>Resource exhausted.</summary>
    ResourceExhausted = 0x89,
    /// <summary>Not found.</summary>
    NotFound = 0x8B,
    /// <summary>Wrong data type.</summary>
    InvalidDataType = 0x8D,
    /// <summary>Data version mismatch.</summary>
    DataVersionMismatch = 0x92,
    /// <summary>Timeout.</summary>
    Timeout = 0x94,
    /// <summary>Busy.</summary>
    Busy = 0x9C,
    /// <summary>Cluster not supported.</summary>
    UnsupportedCluster = 0xC3,
    /// <summary>Needs a timed interaction.</summary>
    NeedsTimedInteraction = 0xC6,
    /// <summary>Fail-safe not armed.</summary>
    FailsafeRequired = 0xCA,
}

/// <summary>An attribute path; null fields are wildcards.</summary>
/// <param name="Endpoint">Endpoint.</param>
/// <param name="Cluster">Cluster id.</param>
/// <param name="Attribute">Attribute id.</param>
public readonly record struct AttributePath(ushort? Endpoint, uint? Cluster, uint? Attribute)
{
    /// <summary>Encodes as an AttributePathIB list with <paramref name="tag"/>.</summary>
    public TlvElement Encode(TlvTag tag)
    {
        var items = new List<TlvElement>();
        if (Endpoint is { } e) items.Add(TlvElement.UInt(TlvTag.Context(2), e));
        if (Cluster is { } c) items.Add(TlvElement.UInt(TlvTag.Context(3), c));
        if (Attribute is { } a) items.Add(TlvElement.UInt(TlvTag.Context(4), a));
        return TlvElement.List(tag, [.. items]);
    }

    /// <summary>Decodes an AttributePathIB.</summary>
    public static AttributePath Decode(TlvElement e) => new(
        e[2] is { } ep ? (ushort)ep.AsUInt() : null, e[3] is { } c ? (uint)c.AsUInt() : null, e[4] is { } a ? (uint)a.AsUInt() : null);

    /// <inheritdoc />
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{Endpoint?.ToString(CultureInfo.InvariantCulture) ?? "*"}/0x{Cluster?.ToString("X4", CultureInfo.InvariantCulture) ?? "*"}/0x{Attribute?.ToString("X4", CultureInfo.InvariantCulture) ?? "*"}");
}

/// <summary>A command path.</summary>
/// <param name="Endpoint">Endpoint.</param>
/// <param name="Cluster">Cluster id.</param>
/// <param name="Command">Command id.</param>
public readonly record struct CommandPath(ushort Endpoint, uint Cluster, uint Command)
{
    /// <summary>Encodes as a CommandPathIB list.</summary>
    public TlvElement Encode(TlvTag tag) => TlvElement.List(tag, TlvElement.UInt(TlvTag.Context(0), Endpoint), TlvElement.UInt(TlvTag.Context(1), Cluster), TlvElement.UInt(TlvTag.Context(2), Command));

    /// <summary>Decodes a CommandPathIB.</summary>
    public static CommandPath Decode(TlvElement e) => new((ushort)e.Required(0).AsUInt(), (uint)e.Required(1).AsUInt(), (uint)e.Required(2).AsUInt());

    /// <inheritdoc />
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Endpoint}/0x{Cluster:X4}/cmd 0x{Command:X2}");
}

/// <summary>One attribute value or status in a report.</summary>
/// <param name="Path">Concrete path.</param>
/// <param name="DataVersion">Cluster data version.</param>
/// <param name="Value">Value (null with a status).</param>
/// <param name="Status">Status when the attribute could not be read.</param>
public sealed record AttributeReport(AttributePath Path, uint DataVersion, TlvElement? Value, MatterStatus Status = MatterStatus.Success);

/// <summary>Interaction Model message codecs (core spec §10.6).</summary>
public static class InteractionModel
{
    /// <summary>Interaction Model revision sent in every message.</summary>
    public const byte Revision = 11;

    private static TlvElement Rev => TlvElement.UInt(TlvTag.Context(0xFF), Revision);

    private static TlvElement Status(TlvTag tag, MatterStatus status) => TlvElement.Structure(tag, TlvElement.UInt(TlvTag.Context(0), (byte)status));

    /// <summary>ReadRequestMessage.</summary>
    public static byte[] ReadRequest(IEnumerable<AttributePath> paths, bool fabricFiltered = true) => Tlv.Encode(TlvElement.Structure(TlvTag.Anonymous,
        TlvElement.Array(TlvTag.Context(0), [.. paths.Select(p => p.Encode(TlvTag.Anonymous))]),
        TlvElement.Bool(TlvTag.Context(3), fabricFiltered), Rev));

    /// <summary>Paths of a ReadRequestMessage (or of a SubscribeRequestMessage's tag 3).</summary>
    public static IReadOnlyList<AttributePath> DecodePaths(TlvElement message, byte tag) => message[tag]?.Children.Select(AttributePath.Decode).ToList() ?? [];

    /// <summary>ReportDataMessage.</summary>
    public static byte[] ReportData(IEnumerable<AttributeReport> reports, uint? subscriptionId = null, bool suppressResponse = true)
    {
        var items = new List<TlvElement>();
        if (subscriptionId is { } id) items.Add(TlvElement.UInt(TlvTag.Context(0), id));
        items.Add(TlvElement.Array(TlvTag.Context(1), [.. reports.Select(r => r.Value is not null
            ? TlvElement.Structure(TlvTag.Anonymous, TlvElement.Structure(TlvTag.Context(1),
                TlvElement.UInt(TlvTag.Context(0), r.DataVersion), r.Path.Encode(TlvTag.Context(1)), r.Value.WithTag(TlvTag.Context(2))))
            : TlvElement.Structure(TlvTag.Anonymous, TlvElement.Structure(TlvTag.Context(0), r.Path.Encode(TlvTag.Context(0)), Status(TlvTag.Context(1), r.Status))))]));
        if (suppressResponse) items.Add(TlvElement.Bool(TlvTag.Context(4), true));
        items.Add(Rev);
        return Tlv.Encode(TlvElement.Structure(TlvTag.Anonymous, [.. items]));
    }

    /// <summary>Decodes a ReportDataMessage.</summary>
    public static (uint? SubscriptionId, IReadOnlyList<AttributeReport> Reports, bool SuppressResponse) DecodeReportData(byte[] payload)
    {
        var m = Tlv.Decode(payload);
        var reports = new List<AttributeReport>();
        foreach (var r in m[1]?.Children ?? [])
        {
            if (r[1] is { } data)
                reports.Add(new AttributeReport(AttributePath.Decode(data.Required(1)), (uint)(data[0]?.AsUInt() ?? 0), data.Required(2).WithTag(TlvTag.Anonymous)));
            else if (r[0] is { } status)
                reports.Add(new AttributeReport(AttributePath.Decode(status.Required(0)), 0, null, (MatterStatus)status.Required(1).Required(0).AsUInt()));
        }

        return (m[0] is { } s ? (uint)s.AsUInt() : null, reports, m[4]?.AsBool() ?? false);
    }

    /// <summary>WriteRequestMessage with one or more attribute values.</summary>
    public static byte[] WriteRequest(IEnumerable<(AttributePath Path, TlvElement Value)> writes, bool timed = false) => Tlv.Encode(TlvElement.Structure(TlvTag.Anonymous,
        TlvElement.Bool(TlvTag.Context(0), false), TlvElement.Bool(TlvTag.Context(1), timed),
        TlvElement.Array(TlvTag.Context(2), [.. writes.Select(w => TlvElement.Structure(TlvTag.Anonymous, w.Path.Encode(TlvTag.Context(1)), w.Value.WithTag(TlvTag.Context(2))))]),
        Rev));

    /// <summary>Decodes a WriteRequestMessage.</summary>
    public static IReadOnlyList<(AttributePath Path, TlvElement Value)> DecodeWriteRequest(byte[] payload)
        => [.. (Tlv.Decode(payload)[2]?.Children ?? []).Select(w => (AttributePath.Decode(w.Required(1)), w.Required(2).WithTag(TlvTag.Anonymous)))];

    /// <summary>WriteResponseMessage.</summary>
    public static byte[] WriteResponse(IEnumerable<(AttributePath Path, MatterStatus Status)> results) => Tlv.Encode(TlvElement.Structure(TlvTag.Anonymous,
        TlvElement.Array(TlvTag.Context(0), [.. results.Select(r => TlvElement.Structure(TlvTag.Anonymous, r.Path.Encode(TlvTag.Context(0)), Status(TlvTag.Context(1), r.Status)))]),
        Rev));

    /// <summary>Decodes a WriteResponseMessage.</summary>
    public static IReadOnlyList<(AttributePath Path, MatterStatus Status)> DecodeWriteResponse(byte[] payload)
        => [.. (Tlv.Decode(payload)[0]?.Children ?? []).Select(r => (AttributePath.Decode(r.Required(0)), (MatterStatus)r.Required(1).Required(0).AsUInt()))];

    /// <summary>InvokeRequestMessage with one command.</summary>
    public static byte[] InvokeRequest(CommandPath path, TlvElement? fields, bool timed = false) => Tlv.Encode(TlvElement.Structure(TlvTag.Anonymous,
        TlvElement.Bool(TlvTag.Context(0), false), TlvElement.Bool(TlvTag.Context(1), timed),
        TlvElement.Array(TlvTag.Context(2), TlvElement.Structure(TlvTag.Anonymous, path.Encode(TlvTag.Context(0)), (fields ?? TlvElement.Structure(TlvTag.Anonymous)).WithTag(TlvTag.Context(1)))),
        Rev));

    /// <summary>Decodes an InvokeRequestMessage.</summary>
    public static IReadOnlyList<(CommandPath Path, TlvElement Fields)> DecodeInvokeRequest(byte[] payload)
        => [.. (Tlv.Decode(payload)[2]?.Children ?? []).Select(c => (CommandPath.Decode(c.Required(0)), (c[1] ?? TlvElement.Structure(TlvTag.Context(1))).WithTag(TlvTag.Anonymous)))];

    /// <summary>InvokeResponseMessage: a response command with fields, or a status.</summary>
    public static byte[] InvokeResponse(CommandPath path, uint? responseCommand, TlvElement? fields, MatterStatus status, byte? clusterStatus = null)
    {
        TlvElement ib = responseCommand is { } rc
            ? TlvElement.Structure(TlvTag.Anonymous, TlvElement.Structure(TlvTag.Context(0), (path with { Command = rc }).Encode(TlvTag.Context(0)),
                (fields ?? TlvElement.Structure(TlvTag.Anonymous)).WithTag(TlvTag.Context(1))))
            : TlvElement.Structure(TlvTag.Anonymous, TlvElement.Structure(TlvTag.Context(1), path.Encode(TlvTag.Context(0)),
                clusterStatus is { } cs
                    ? TlvElement.Structure(TlvTag.Context(1), TlvElement.UInt(TlvTag.Context(0), (byte)status), TlvElement.UInt(TlvTag.Context(1), cs))
                    : Status(TlvTag.Context(1), status)));
        return Tlv.Encode(TlvElement.Structure(TlvTag.Anonymous, TlvElement.Bool(TlvTag.Context(0), false), TlvElement.Array(TlvTag.Context(1), ib), Rev));
    }

    /// <summary>Decodes an InvokeResponseMessage (first response).</summary>
    public static (CommandPath Path, TlvElement? Fields, MatterStatus Status, byte? ClusterStatus) DecodeInvokeResponse(byte[] payload)
    {
        var responses = Tlv.Decode(payload)[1]?.Children;
        var ib = responses is { Count: > 0 } ? responses[0] : throw new ProtocolException("InvokeResponse without responses.");
        if (ib[0] is { } command) return (CommandPath.Decode(command.Required(0)), command[1]?.WithTag(TlvTag.Anonymous), MatterStatus.Success, null);
        var status = ib.Required(1);
        var s = status.Required(1);
        return (CommandPath.Decode(status.Required(0)), null, (MatterStatus)s.Required(0).AsUInt(), s[1] is { } cs ? (byte)cs.AsUInt() : null);
    }

    /// <summary>SubscribeRequestMessage.</summary>
    public static byte[] SubscribeRequest(IEnumerable<AttributePath> paths, ushort minInterval, ushort maxInterval, bool keepSubscriptions = false) => Tlv.Encode(TlvElement.Structure(TlvTag.Anonymous,
        TlvElement.Bool(TlvTag.Context(0), keepSubscriptions), TlvElement.UInt(TlvTag.Context(1), minInterval), TlvElement.UInt(TlvTag.Context(2), maxInterval),
        TlvElement.Array(TlvTag.Context(3), [.. paths.Select(p => p.Encode(TlvTag.Anonymous))]),
        TlvElement.Bool(TlvTag.Context(7), true), Rev));

    /// <summary>SubscribeResponseMessage.</summary>
    public static byte[] SubscribeResponse(uint subscriptionId, ushort maxInterval) => Tlv.Encode(TlvElement.Structure(TlvTag.Anonymous,
        TlvElement.UInt(TlvTag.Context(0), subscriptionId), TlvElement.UInt(TlvTag.Context(2), maxInterval), Rev));

    /// <summary>StatusResponseMessage.</summary>
    public static byte[] StatusResponse(MatterStatus status) => Tlv.Encode(TlvElement.Structure(TlvTag.Anonymous, TlvElement.UInt(TlvTag.Context(0), (byte)status), Rev));

    /// <summary>Decodes a StatusResponseMessage.</summary>
    public static MatterStatus DecodeStatusResponse(byte[] payload) => (MatterStatus)Tlv.Decode(payload).Required(0).AsUInt();
}
