using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;

namespace IoTCom.Net.Adapters.Nats;

/// <summary>Options for <see cref="NatsEndpoint"/>.</summary>
public sealed class NatsEndpointOptions
{
    /// <summary>Server URL(s), comma separated (default nats://localhost:4222).</summary>
    public string Url { get; set; } = "nats://localhost:4222";

    /// <summary>User name (with <see cref="Password"/>).</summary>
    public string? UserName { get; set; }

    /// <summary>Password. A secret: it is never logged.</summary>
    public string? Password { get; set; }

    /// <summary>Authentication token. A secret: it is never logged.</summary>
    public string? Token { get; set; }

    /// <summary>Path of a .creds file (JWT + NKey seed) for NATS decentralised auth.</summary>
    public string? CredentialsFile { get; set; }

    /// <summary>Connection name shown by the server.</summary>
    public string? ClientName { get; set; }

    /// <summary>Time to wait for a reply in <see cref="NatsEndpoint.RequestAsync"/> (default 5 s).</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }

    /// <summary>Endpoint name.</summary>
    public string? Name { get; set; }

    /// <summary>Uses a server.</summary>
    public NatsEndpointOptions UseServer(string url)
    {
        Url = url;
        return this;
    }

    /// <summary>Authenticates with a user name and password.</summary>
    public NatsEndpointOptions WithCredentials(string userName, string password)
    {
        (UserName, Password) = (userName, password);
        return this;
    }

    /// <summary>Authenticates with a token.</summary>
    public NatsEndpointOptions WithToken(string token)
    {
        Token = token;
        return this;
    }
}

/// <summary>A received NATS message with its subject, reply subject and headers.</summary>
/// <param name="Subject">Subject.</param>
/// <param name="Data">Payload.</param>
/// <param name="ReplyTo">Reply subject, for requests.</param>
/// <param name="Headers">Headers (empty when none).</param>
public sealed record NatsReceived(string Subject, ReadOnlyMemory<byte> Data, string? ReplyTo, IReadOnlyDictionary<string, string> Headers);

/// <summary>
/// NATS as an IoTCom.Net endpoint, an adapter over the official NATS.Net client (not a rewrite): publish and subscribe
/// with subject wildcards (<c>*</c>, <c>&gt;</c>), queue groups, headers, and request/reply. Payloads are bytes; use the
/// payload codecs (SenML, JSON…) on top.
/// </summary>
public sealed class NatsEndpoint : EndpointBase, IClientEndpoint, IPublisher<ReadOnlyMemory<byte>>, ISubscriber<ReadOnlyMemory<byte>>
{
    private readonly NatsEndpointOptions _options;
    private NatsConnection? _connection;
    private long _received, _published;

    private NatsEndpoint(NatsEndpointOptions options) : base("nats", options.Logger)
    {
        _options = options;
        Name = options.Name;
    }

    /// <summary>Creates an endpoint.</summary>
    public static NatsEndpoint Create(Action<NatsEndpointOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var o = new NatsEndpointOptions();
        configure(o);
        return new NatsEndpoint(o);
    }

    /// <summary>True while connected.</summary>
    public bool IsConnected => _connection?.ConnectionState == NatsConnectionState.Open;

    /// <summary>Messages received by subscriptions.</summary>
    public long MessagesReceived => Interlocked.Read(ref _received);

    /// <summary>Messages published.</summary>
    public long MessagesPublished => Interlocked.Read(ref _published);

    private NatsOpts Opts()
    {
        var auth = NatsAuthOpts.Default;
        if (_options.UserName is not null) auth = auth with { Username = _options.UserName, Password = _options.Password };
        if (_options.Token is not null) auth = auth with { Token = _options.Token };
        if (_options.CredentialsFile is not null) auth = auth with { CredsFile = _options.CredentialsFile };
        return NatsOpts.Default with { Url = _options.Url, Name = _options.ClientName ?? Name ?? "iotcom", AuthOpts = auth, RequestTimeout = _options.RequestTimeout };
    }

    /// <inheritdoc />
    public async ValueTask ConnectAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_connection is not null) return;
        SetState(EndpointState.Connecting);
        var connection = new NatsConnection(Opts());
        try
        {
            await connection.ConnectAsync().AsTask().WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is NatsException or OperationCanceledException or TimeoutException)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            SetState(EndpointState.Disconnected, ex);
            throw ex is OperationCanceledException ? ex : new TransportException($"Cannot connect to NATS at {_options.Url}: {ex.Message}", ex);
        }

        _connection = connection;
        SetState(EndpointState.Connected);
    }

    /// <inheritdoc />
    public async ValueTask DisconnectAsync(CancellationToken ct = default)
    {
        var connection = Interlocked.Exchange(ref _connection, null);
        if (connection is null) return;
        await connection.DisposeAsync().ConfigureAwait(false);
        SetState(EndpointState.Disconnected);
    }

    private async ValueTask<NatsConnection> ConnectionAsync(CancellationToken ct)
    {
        ThrowIfDisposed();
        if (_connection is null) await ConnectAsync(ct).ConfigureAwait(false);
        return _connection ?? throw new TransportException("Not connected to NATS.");
    }

    private static NatsHeaders? Headers(IReadOnlyDictionary<string, string>? headers, string? contentType)
    {
        if ((headers is null || headers.Count == 0) && contentType is null) return null;
        var h = new NatsHeaders();
        foreach (var (k, v) in headers ?? new Dictionary<string, string>()) h[k] = v;
        if (contentType is not null) h["Content-Type"] = contentType;
        return h;
    }

    private static Dictionary<string, string> Headers(NatsHeaders? headers) =>
        headers?.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.Ordinal) ?? [];

    /// <summary>Publishes to a subject (<see cref="PublishOptions.ContentType"/> becomes a Content-Type header).</summary>
    public async ValueTask PublishAsync(string topic, ReadOnlyMemory<byte> message, PublishOptions? options = null, CancellationToken ct = default) =>
        await PublishAsync(topic, message, null, options?.ContentType, ct).ConfigureAwait(false);

    /// <summary>Publishes with headers.</summary>
    public async ValueTask PublishAsync(string subject, ReadOnlyMemory<byte> data, IReadOnlyDictionary<string, string>? headers, string? contentType = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subject);
        var connection = await ConnectionAsync(ct).ConfigureAwait(false);
        Tap(FrameDirection.Outbound, data.Span, () => $"PUB {subject} {data.Length} B");
        await connection.PublishAsync(subject, data.ToArray(), Headers(headers, contentType), cancellationToken: ct).ConfigureAwait(false);
        Interlocked.Increment(ref _published);
    }

    /// <summary>Subscribes to a subject or wildcard (<c>plant.*.temperature</c>, <c>plant.&gt;</c>).</summary>
    public async IAsyncEnumerable<Message<ReadOnlyMemory<byte>>> SubscribeAsync(string filter, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var m in ReceiveAsync(filter, null, ct).ConfigureAwait(false))
            yield return new Message<ReadOnlyMemory<byte>>(m.Subject, m.Data, DateTimeOffset.UtcNow);
    }

    /// <summary>Subscribes with an optional queue group (each message goes to one member of the group).</summary>
    public async IAsyncEnumerable<NatsReceived> ReceiveAsync(string subject, string? queueGroup = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subject);
        var connection = await ConnectionAsync(ct).ConfigureAwait(false);
        await foreach (var msg in connection.SubscribeAsync<byte[]>(subject, queueGroup, cancellationToken: ct).ConfigureAwait(false))
        {
            var data = msg.Data ?? [];
            Interlocked.Increment(ref _received);
            Tap(FrameDirection.Inbound, data, () => $"MSG {msg.Subject} {data.Length} B");
            yield return new NatsReceived(msg.Subject, data, msg.ReplyTo, Headers(msg.Headers));
        }
    }

    /// <summary>Sends a request and waits for the first reply.</summary>
    /// <exception cref="IoTComTimeoutException">No reply in time.</exception>
    /// <exception cref="DeviceException">No responder is listening on the subject.</exception>
    public async Task<NatsReceived> RequestAsync(string subject, ReadOnlyMemory<byte> data, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subject);
        var connection = await ConnectionAsync(ct).ConfigureAwait(false);
        Tap(FrameDirection.Outbound, data.Span, () => $"REQ {subject} {data.Length} B");
        try
        {
            var reply = await connection.RequestAsync<byte[], byte[]>(subject, data.ToArray(),
                replyOpts: new NatsSubOpts { Timeout = timeout ?? _options.RequestTimeout }, cancellationToken: ct).ConfigureAwait(false);
            var payload = reply.Data ?? [];
            Tap(FrameDirection.Inbound, payload, () => $"REPLY {subject} {payload.Length} B");
            return new NatsReceived(reply.Subject, payload, reply.ReplyTo, Headers(reply.Headers));
        }
        catch (NatsNoRespondersException ex)
        {
            throw new DeviceException($"No NATS responder on \"{subject}\".", ex);
        }
        catch (NatsNoReplyException ex)
        {
            throw new IoTComTimeoutException($"No reply on \"{subject}\" within {(timeout ?? _options.RequestTimeout).TotalSeconds:0.#} s.", ex);
        }
    }

    /// <summary>
    /// Answers requests on <paramref name="subject"/> until cancelled: <paramref name="handler"/> returns the reply
    /// payload. Use a <paramref name="queueGroup"/> to spread requests over several responders.
    /// </summary>
    public async Task ServeAsync(string subject, Func<NatsReceived, ValueTask<ReadOnlyMemory<byte>>> handler, string? queueGroup = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var connection = await ConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            await foreach (var msg in connection.SubscribeAsync<byte[]>(subject, queueGroup, cancellationToken: ct).ConfigureAwait(false))
            {
                if (msg.ReplyTo is null) continue;
                var request = new NatsReceived(msg.Subject, msg.Data ?? [], msg.ReplyTo, Headers(msg.Headers));
                Interlocked.Increment(ref _received);
                var reply = await handler(request).ConfigureAwait(false);
                await msg.ReplyAsync(reply.ToArray(), cancellationToken: ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore() => await DisconnectAsync().ConfigureAwait(false);
}
