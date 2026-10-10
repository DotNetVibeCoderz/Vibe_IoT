using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Adapters.Zenoh;

/// <summary>Zenoh session options.</summary>
public sealed class ZenohOptions
{
    /// <summary>Creates the engine (default: the native Zenoh library).</summary>
    public Func<ZenohOptions, IZenohBackend> BackendFactory { get; set; } = NativeZenohBackend.Open;

    /// <summary>Peer (default) or client.</summary>
    public ZenohMode Mode { get; set; } = ZenohMode.Peer;

    /// <summary>Endpoints to connect to, for example <c>tcp/192.168.1.10:7447</c> (a router or another peer).</summary>
    public List<string> ConnectEndpoints { get; } = [];

    /// <summary>Endpoints to listen on, for example <c>tcp/0.0.0.0:7447</c> or <c>udp/127.0.0.1:7448</c>.</summary>
    public List<string> ListenEndpoints { get; } = [];

    /// <summary>Discover other peers and routers by multicast scouting (default on). Switch it off for explicit topologies.</summary>
    public bool MulticastScouting { get; set; } = true;

    /// <summary>
    /// Refuse <see cref="ZenohSession.PutAsync(string, ReadOnlyMemory{byte}, string?, CancellationToken)"/>, <see cref="ZenohSession.DeleteAsync"/> and query
    /// replies with <see cref="ReadOnlyModeException"/>. Default <c>false</c>: publishing is what a pub/sub adapter is for, so
    /// writes are on unless you turn them off (the CLI turns them off for <c>sub</c> and <c>get</c>).
    /// </summary>
    public bool ReadOnly { get; set; }

    /// <summary>Default timeout of <see cref="ZenohSession.GetAsync(string, ReadOnlyMemory{byte}, TimeSpan?, CancellationToken)"/>.</summary>
    public TimeSpan QueryTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Friendly name.</summary>
    public string? Name { get; set; }

    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }

    /// <summary>Uses the native Zenoh library (<c>iotcom_zenoh</c>).</summary>
    public ZenohOptions UseNative()
    {
        BackendFactory = NativeZenohBackend.Open;
        return this;
    }

    /// <summary>Uses an in-process network instead of sockets.</summary>
    public ZenohOptions UseVirtual(VirtualZenohNetwork network, string? zid = null)
    {
        ArgumentNullException.ThrowIfNull(network);
        BackendFactory = _ => network.CreateBackend(zid);
        return this;
    }

    /// <summary>Connects to an endpoint (adds to <see cref="ConnectEndpoints"/>).</summary>
    public ZenohOptions Connect(string endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ConnectEndpoints.Add(endpoint);
        return this;
    }

    /// <summary>Listens on an endpoint (adds to <see cref="ListenEndpoints"/>).</summary>
    public ZenohOptions Listen(string endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ListenEndpoints.Add(endpoint);
        return this;
    }
}

/// <summary>
/// An Eclipse Zenoh session: publish with <see cref="PutAsync(string, ReadOnlyMemory{byte}, string?, CancellationToken)"/> and
/// <see cref="DeleteAsync"/>, subscribe to key expressions (callback, event or <see cref="IAsyncEnumerable{T}"/>), serve queries with
/// <see cref="DeclareQueryable"/> and ask with <see cref="GetAsync(string, ReadOnlyMemory{byte}, TimeSpan?, CancellationToken)"/>. The engine is the
/// Rust <c>zenoh</c> crate (T2-R: bound, not rewritten) or a <see cref="VirtualZenohNetwork"/>. Samples and replies are reported to the traffic tap.
/// </summary>
/// <remarks>
/// <see cref="ZenohOptions.ReadOnly"/> defaults to <c>false</c>. Wildcard key expressions are accepted for subscriptions,
/// queryables and queries; puts and deletes should use concrete keys.
/// </remarks>
/// <example>
/// <code>
/// await using var zenoh = ZenohSession.Create(o => o.Connect("tcp/192.168.1.10:7447"));
/// await zenoh.ConnectAsync();
/// await zenoh.PutAsync("plant/line1/temp", "21.5");
/// await foreach (var sample in zenoh.WatchAsync("plant/**", ct))
///     Console.WriteLine($"{sample.Key} = {sample.Text}");
/// </code>
/// </example>
public sealed class ZenohSession : EndpointBase, IClientEndpoint, IPublisher<byte[]>, IPublisher<string>, ISubscriber<byte[]>, ISubscriber<string>
{
    private readonly ZenohOptions _options;
    private readonly ConcurrentDictionary<IDisposable, byte> _declared = new();
    private IZenohBackend? _backend;

    private ZenohSession(ZenohOptions options) : base("zenoh", options.Logger)
    {
        _options = options;
        Name = options.Name;
    }

    /// <summary>Creates a session (not yet open; call <see cref="ConnectAsync"/>).</summary>
    public static ZenohSession Create(Action<ZenohOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var o = new ZenohOptions();
        configure(o);
        return new ZenohSession(o);
    }

    /// <summary>The options this session was created with.</summary>
    public ZenohOptions Options => _options;

    /// <summary>True when puts, deletes and replies are refused.</summary>
    public bool ReadOnly => _options.ReadOnly;

    /// <summary>The Zenoh id of this session; empty before <see cref="ConnectAsync"/>.</summary>
    public string Zid => _backend?.Zid ?? "";

    /// <summary>The engine, for diagnostics (for example <see cref="NativeZenohBackend.ReadInfo"/>).</summary>
    public IZenohBackend? Backend => _backend;

    /// <summary>Raised for every sample received by any subscription made with <see cref="Subscribe"/>, <see cref="WatchAsync"/> or <see cref="SubscribeAsync(string, CancellationToken)"/>.</summary>
    public event EventHandler<ZenohSample>? SampleReceived;

    private IZenohBackend Engine => _backend ?? throw new InvalidOperationException("Call ConnectAsync first to open the session.");

    /// <summary>Opens the session (peer or client, listening and connecting as configured).</summary>
    public async ValueTask ConnectAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_backend is not null) return;
        SetState(EndpointState.Connecting);
        try
        {
            // Opening may block on the network (connect, scouting): keep it off the caller's thread.
            _backend = await Task.Run(() => _options.BackendFactory(_options), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TransportException or PlatformNotSupportedException or ArgumentException)
        {
            SetState(EndpointState.Disconnected, ex);
            throw;
        }

        SetState(EndpointState.Connected);
        Logger.LogInformation("Zenoh session {Zid}: {Description}", _backend.Zid, _backend.Description);
    }

    /// <summary>Undeclares everything and closes the session.</summary>
    public async ValueTask DisconnectAsync(CancellationToken ct = default)
    {
        var backend = Interlocked.Exchange(ref _backend, null);
        if (backend is null) return;
        SetState(EndpointState.Stopping);
        foreach (var d in _declared.Keys) d.Dispose();
        _declared.Clear();
        await backend.DisposeAsync().ConfigureAwait(false);
        SetState(EndpointState.Disconnected);
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore() => await DisconnectAsync().ConfigureAwait(false);

    /// <summary>Puts a value on a concrete key. Throws <see cref="ReadOnlyModeException"/> in read-only mode.</summary>
    /// <param name="key">Key expression, for example <c>plant/line1/temp</c>.</param>
    /// <param name="payload">The value.</param>
    /// <param name="encoding">Optional encoding label such as <c>text/plain</c> or <c>application/json</c>.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task PutAsync(string key, ReadOnlyMemory<byte> payload, string? encoding = null, CancellationToken ct = default)
    {
        if (_options.ReadOnly) throw new ReadOnlyModeException();
        ZenohKeyExpr.ThrowIfInvalid(key);
        var engine = Engine;
        Tap(FrameDirection.Outbound, payload.Span, () => $"put {key}");
        await engine.PutAsync(key, payload, encoding, ct).ConfigureAwait(false);
    }

    /// <summary>Puts a UTF-8 text value (<c>text/plain</c>).</summary>
    public Task PutAsync(string key, string text, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        return PutAsync(key, Encoding.UTF8.GetBytes(text), "text/plain", ct);
    }

    /// <summary>Deletes a key (subscribers see a <see cref="ZenohSampleKind.Delete"/> sample). Throws <see cref="ReadOnlyModeException"/> in read-only mode.</summary>
    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        if (_options.ReadOnly) throw new ReadOnlyModeException();
        ZenohKeyExpr.ThrowIfInvalid(key);
        var engine = Engine;
        Tap(FrameDirection.Outbound, [], () => $"delete {key}");
        await engine.DeleteAsync(key, ct).ConfigureAwait(false);
    }

    private void OnSample(ZenohSample sample)
    {
        Tap(FrameDirection.Inbound, sample.Payload, () => $"{(sample.Kind == ZenohSampleKind.Put ? "sample" : "delete")} {sample.Key}");
        SampleReceived?.Invoke(this, sample);
    }

    /// <summary>
    /// Subscribes to a key expression and calls <paramref name="handler"/> for every put and delete (on a background thread,
    /// in order). Dispose the result to unsubscribe.
    /// </summary>
    public IDisposable Subscribe(string keyExpr, Action<ZenohSample> handler)
    {
        ZenohKeyExpr.ThrowIfInvalid(keyExpr);
        ArgumentNullException.ThrowIfNull(handler);
        var sub = Engine.DeclareSubscriber(keyExpr, sample =>
        {
            OnSample(sample);
            handler(sample);
        });
        return Track(sub);
    }

    private TrackedDisposable Track(IDisposable inner)
    {
        var tracked = new TrackedDisposable(inner, this);
        _declared[tracked] = 0;
        return tracked;
    }

    private sealed class TrackedDisposable(IDisposable inner, ZenohSession owner) : IDisposable
    {
        public void Dispose()
        {
            owner._declared.TryRemove(this, out _);
            inner.Dispose();
        }
    }

    /// <summary>Subscribes and yields every put and delete sample until <paramref name="ct"/> is cancelled (then unsubscribes).</summary>
    public async IAsyncEnumerable<ZenohSample> WatchAsync(string keyExpr, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = Channel.CreateBounded<ZenohSample>(new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        using var sub = Subscribe(keyExpr, s => channel.Writer.TryWrite(s));
        await foreach (var sample in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false)) yield return sample;
    }

    /// <summary>Shared <see cref="ISubscriber{T}"/> view: yields the payload of every put (deletes are skipped).</summary>
    public async IAsyncEnumerable<Message<byte[]>> SubscribeAsync(string filter, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var s in WatchAsync(filter, ct).ConfigureAwait(false))
            if (s.Kind == ZenohSampleKind.Put) yield return new Message<byte[]>(s.Key, s.Payload, s.Timestamp);
    }

    async IAsyncEnumerable<Message<string>> ISubscriber<string>.SubscribeAsync(string filter, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var m in SubscribeAsync(filter, ct).ConfigureAwait(false))
            yield return new Message<string>(m.Topic, Encoding.UTF8.GetString(m.Payload), m.Timestamp);
    }

    /// <summary>Shared <see cref="IPublisher{T}"/> view: puts <paramref name="message"/> on <paramref name="topic"/> (<see cref="PublishOptions.ContentType"/> becomes the encoding).</summary>
    public async ValueTask PublishAsync(string topic, byte[] message, PublishOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        await PutAsync(topic, message, options?.ContentType, ct).ConfigureAwait(false);
    }

    /// <summary>Shared <see cref="IPublisher{T}"/> view for text: puts the UTF-8 bytes (encoding defaults to <c>text/plain</c>).</summary>
    public async ValueTask PublishAsync(string topic, string message, PublishOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        await PutAsync(topic, Encoding.UTF8.GetBytes(message), options?.ContentType ?? "text/plain", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Declares a queryable: <paramref name="handler"/> runs for every query whose key intersects <paramref name="keyExpr"/>
    /// and answers with <see cref="ZenohQuery.ReplyAsync(string, ReadOnlyMemory{byte}, string?, CancellationToken)"/>. The query is finished when the handler
    /// returns (an exception is logged and finishes it too). Replying is refused in read-only mode. Dispose the result to undeclare.
    /// </summary>
    public IDisposable DeclareQueryable(string keyExpr, Func<ZenohQuery, ValueTask> handler)
    {
        ZenohKeyExpr.ThrowIfInvalid(keyExpr);
        ArgumentNullException.ThrowIfNull(handler);
        var readOnly = _options.ReadOnly;
        var queryable = Engine.DeclareQueryable(keyExpr, (key, parameters, payload, responder) =>
        {
            _ = RunQueryAsync(handler, new ZenohQuery(key, parameters, payload, responder, readOnly));
        });
        return Track(queryable);
    }

    private async Task RunQueryAsync(Func<ZenohQuery, ValueTask> handler, ZenohQuery query)
    {
        Tap(FrameDirection.Inbound, query.Payload ?? [], () => $"query {query.Key}{(query.Parameters.Length > 0 ? "?" + query.Parameters : "")}");
        try
        {
            await handler(query).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Logger.LogWarning(ex, "Zenoh queryable handler for {Key} failed", query.Key);
        }
        finally
        {
            query.Finish();
        }
    }

    /// <summary>
    /// Asks every queryable matching <paramref name="selector"/> (a key expression with optional <c>?parameters</c>) and returns
    /// the replies once all have answered or <paramref name="timeout"/> (default <see cref="ZenohOptions.QueryTimeout"/>) passed.
    /// An empty list means nobody answered.
    /// </summary>
    public async Task<IReadOnlyList<ZenohReply>> GetAsync(string selector, ReadOnlyMemory<byte> payload = default, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        var q = selector.IndexOf('?', StringComparison.Ordinal);
        ZenohKeyExpr.ThrowIfInvalid(q < 0 ? selector : selector[..q], nameof(selector));
        var engine = Engine;
        Tap(FrameDirection.Outbound, payload.Span, () => $"get {selector}");
        var replies = new List<ZenohReply>();
        await engine.GetAsync(selector, payload, timeout ?? _options.QueryTimeout, r =>
        {
            Tap(FrameDirection.Inbound, r.Sample is { } s ? s.Payload : r.Error ?? [], () => r.IsError ? $"reply error {r.ErrorText}" : $"reply {r.Sample!.Key}");
            lock (replies) replies.Add(r);
        }, ct).ConfigureAwait(false);
        lock (replies) return [.. replies];
    }

    /// <summary>Like <see cref="GetAsync(string, ReadOnlyMemory{byte}, TimeSpan?, CancellationToken)"/>, with a text request body.</summary>
    public Task<IReadOnlyList<ZenohReply>> GetAsync(string selector, string body, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        return GetAsync(selector, Encoding.UTF8.GetBytes(body), timeout, ct);
    }
}
