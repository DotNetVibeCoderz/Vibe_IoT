using System.Text;

namespace IoTCom.Net.Adapters.Zenoh;

/// <summary>Whether a sample carries a value or announces that the key was deleted.</summary>
public enum ZenohSampleKind
{
    /// <summary>A value was put.</summary>
    Put = 0,
    /// <summary>The key was deleted.</summary>
    Delete = 1,
}

/// <summary>How a session takes part in the Zenoh network.</summary>
public enum ZenohMode
{
    /// <summary>Peer: talks directly to other peers (multicast scouting) and to the endpoints it connects to.</summary>
    Peer = 0,
    /// <summary>Client: connects to a router or peer and lets it do the routing.</summary>
    Client = 1,
}

/// <summary>A value (or deletion) published on a key.</summary>
/// <param name="Key">The key expression the sample was published on.</param>
/// <param name="Kind">Put or delete.</param>
/// <param name="Payload">Payload bytes (empty for deletes).</param>
/// <param name="Encoding">Encoding the publisher declared (for example <c>text/plain</c>), if any.</param>
/// <param name="Timestamp">Local receive time (UTC).</param>
public sealed record ZenohSample(string Key, ZenohSampleKind Kind, byte[] Payload, string? Encoding, DateTimeOffset Timestamp)
{
    /// <summary>The payload decoded as UTF-8.</summary>
    public string Text => System.Text.Encoding.UTF8.GetString(Payload);
}

/// <summary>An answer to a <c>get</c>: either a sample or an error payload from the queryable.</summary>
public sealed record ZenohReply
{
    /// <summary>The replied value, or <c>null</c> for an error reply.</summary>
    public ZenohSample? Sample { get; init; }

    /// <summary>The error payload of an error reply, or <c>null</c>.</summary>
    public byte[]? Error { get; init; }

    /// <summary>True for an error reply.</summary>
    public bool IsError => Sample is null;

    /// <summary>The error payload decoded as UTF-8 (empty for a value reply).</summary>
    public string ErrorText => Error is null ? "" : Encoding.UTF8.GetString(Error);
}

/// <summary>The answering side of a query, supplied by a backend.</summary>
public interface IZenohQueryResponder
{
    /// <summary>Sends one value reply.</summary>
    ValueTask ReplyAsync(string key, ReadOnlyMemory<byte> payload, string? encoding, CancellationToken ct);

    /// <summary>Sends an error reply.</summary>
    ValueTask ReplyErrorAsync(ReadOnlyMemory<byte> payload, CancellationToken ct);

    /// <summary>Tells the requester that no more replies follow. Safe to call more than once.</summary>
    void Finish();
}

/// <summary>A query received by a queryable. Reply as often as needed; the session finishes the query when the handler returns.</summary>
public sealed class ZenohQuery
{
    private readonly IZenohQueryResponder _responder;
    private readonly bool _readOnly;

    internal ZenohQuery(string key, string parameters, byte[]? payload, IZenohQueryResponder responder, bool readOnly)
    {
        Key = key;
        Parameters = parameters;
        Payload = payload;
        _responder = responder;
        _readOnly = readOnly;
    }

    /// <summary>The key expression that was asked for.</summary>
    public string Key { get; }

    /// <summary>The selector parameters (the part after <c>?</c>), empty when there are none.</summary>
    public string Parameters { get; }

    /// <summary>The optional request payload sent with the query.</summary>
    public byte[]? Payload { get; }

    /// <summary>Replies with a value. <paramref name="key"/> must intersect <see cref="Key"/>. Throws <see cref="ReadOnlyModeException"/> in read-only mode.</summary>
    public ValueTask ReplyAsync(string key, ReadOnlyMemory<byte> payload, string? encoding = null, CancellationToken ct = default)
    {
        if (_readOnly) throw new ReadOnlyModeException();
        ZenohKeyExpr.ThrowIfInvalid(key);
        return _responder.ReplyAsync(key, payload, encoding, ct);
    }

    /// <summary>Replies with a UTF-8 text value (<c>text/plain</c>).</summary>
    public ValueTask ReplyAsync(string key, string text, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        return ReplyAsync(key, Encoding.UTF8.GetBytes(text), "text/plain", ct);
    }

    /// <summary>Replies with an error payload. Throws <see cref="ReadOnlyModeException"/> in read-only mode.</summary>
    public ValueTask ReplyErrorAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        if (_readOnly) throw new ReadOnlyModeException();
        return _responder.ReplyErrorAsync(payload, ct);
    }

    /// <summary>Replies with a UTF-8 error message.</summary>
    public ValueTask ReplyErrorAsync(string message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        return ReplyErrorAsync(Encoding.UTF8.GetBytes(message), ct);
    }

    internal void Finish() => _responder.Finish();
}

/// <summary>
/// The engine under a <see cref="ZenohSession"/>: the native Zenoh library (<see cref="NativeZenohBackend"/>) or an
/// in-process <see cref="VirtualZenohNetwork"/>. Handlers are invoked from background threads.
/// </summary>
public interface IZenohBackend : IAsyncDisposable
{
    /// <summary>Zenoh id of the session (hex).</summary>
    string Zid { get; }

    /// <summary>Description for logs, for example <c>zenoh 1.10 peer</c>.</summary>
    string Description { get; }

    /// <summary>Puts a value on a key.</summary>
    Task PutAsync(string key, ReadOnlyMemory<byte> payload, string? encoding, CancellationToken ct);

    /// <summary>Deletes a key.</summary>
    Task DeleteAsync(string key, CancellationToken ct);

    /// <summary>Declares a subscriber; dispose the result to undeclare it.</summary>
    IDisposable DeclareSubscriber(string keyExpr, Action<ZenohSample> handler);

    /// <summary>Declares a queryable. <paramref name="handler"/> receives the query and its responder; the backend never finishes the query itself.</summary>
    IDisposable DeclareQueryable(string keyExpr, Action<string, string, byte[]?, IZenohQueryResponder> handler);

    /// <summary>Runs a query and calls <paramref name="onReply"/> per reply; completes when the query is finished or timed out.</summary>
    Task GetAsync(string selector, ReadOnlyMemory<byte> payload, TimeSpan timeout, Action<ZenohReply> onReply, CancellationToken ct);
}
