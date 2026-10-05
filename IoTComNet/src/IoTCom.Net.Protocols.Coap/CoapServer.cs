using System.Collections.Concurrent;
using System.Net;
using System.Text;
using IoTCom.Net.Transports;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Protocols.Coap;

/// <summary>A request as seen by a resource handler.</summary>
public sealed class CoapRequest
{
    internal CoapRequest(CoapMessage message, EndPoint remote, ReadOnlyMemory<byte> payload)
    {
        Message = message;
        Remote = remote;
        Payload = payload;
    }

    /// <summary>The request message (for Block1 uploads: the last block).</summary>
    public CoapMessage Message { get; }

    /// <summary>Client address.</summary>
    public EndPoint Remote { get; }

    /// <summary>Method (GET, POST, PUT, DELETE).</summary>
    public CoapCode Method => Message.Code;

    /// <summary>Uri-Path.</summary>
    public string Path => Message.UriPath;

    /// <summary>Uri-Query parameters.</summary>
    public IReadOnlyList<string> Query => Message.UriQuery;

    /// <summary>Full payload (Block1 uploads reassembled).</summary>
    public ReadOnlyMemory<byte> Payload { get; }

    /// <summary>Payload as UTF-8 text.</summary>
    public string PayloadText => Encoding.UTF8.GetString(Payload.Span);

    /// <summary>Requested Accept format.</summary>
    public ushort? Accept => Message.Accept;

    /// <summary>Value of a <c>key=value</c> query parameter.</summary>
    public string? QueryValue(string key) =>
        Query.Select(q => q.Split('=', 2)).FirstOrDefault(p => p[0] == key) is { Length: 2 } kv ? kv[1] : null;
}

/// <summary>The response a handler returns.</summary>
/// <param name="Code">Response code.</param>
/// <param name="Payload">Body.</param>
/// <param name="ContentFormat">Content-Format of the body.</param>
public sealed record CoapReply(CoapCode Code, ReadOnlyMemory<byte> Payload = default, ushort? ContentFormat = null)
{
    /// <summary>Max-Age in seconds.</summary>
    public uint? MaxAge { get; init; }

    /// <summary>2.05 Content with text.</summary>
    public static CoapReply Content(string text, ushort format = CoapContentFormat.TextPlain) => new(CoapCode.Content, Encoding.UTF8.GetBytes(text), format);

    /// <summary>2.05 Content with bytes.</summary>
    public static CoapReply Content(ReadOnlyMemory<byte> body, ushort format) => new(CoapCode.Content, body, format);

    /// <summary>2.04 Changed.</summary>
    public static CoapReply Changed() => new(CoapCode.Changed);

    /// <summary>2.01 Created.</summary>
    public static CoapReply Created() => new(CoapCode.Created);

    /// <summary>2.02 Deleted.</summary>
    public static CoapReply Deleted() => new(CoapCode.Deleted);

    /// <summary>An error with a diagnostic payload.</summary>
    public static CoapReply Error(CoapCode code, string? diagnostic = null) =>
        new(code, diagnostic is null ? default : Encoding.UTF8.GetBytes(diagnostic), diagnostic is null ? null : CoapContentFormat.TextPlain);
}

/// <summary>Handles one request.</summary>
public delegate ValueTask<CoapReply> CoapHandler(CoapRequest request, CancellationToken ct);

/// <summary>A resource: path, link attributes and method handlers. Observable resources notify observers on <see cref="NotifyAsync"/>.</summary>
public sealed class CoapResource
{
    private readonly ConcurrentDictionary<string, Observer> _observers = new();
    private int _sequence = 2;

    internal sealed record Observer(EndPoint Remote, ReadOnlyMemory<byte> Token, ushort? Accept, CoapMessage Request)
    {
        public int Sent;
    }

    internal CoapResource(CoapServer server, string path)
    {
        Server = server;
        Path = path;
    }

    internal CoapServer Server { get; }

    /// <summary>Path, e.g. "/sensors/temperature".</summary>
    public string Path { get; }

    /// <summary>GET handler.</summary>
    public CoapHandler? Get { get; set; }

    /// <summary>PUT handler.</summary>
    public CoapHandler? Put { get; set; }

    /// <summary>POST handler.</summary>
    public CoapHandler? Post { get; set; }

    /// <summary>DELETE handler.</summary>
    public CoapHandler? Delete { get; set; }

    /// <summary>Accepts Observe registrations.</summary>
    public bool Observable { get; set; }

    /// <summary>Link attributes advertised in /.well-known/core (rt, if, title, ct…).</summary>
    public Dictionary<string, string> Attributes { get; } = new(StringComparer.Ordinal);

    /// <summary>Number of current observers.</summary>
    public int ObserverCount => _observers.Count;

    internal int NextSequence() => Interlocked.Increment(ref _sequence) & 0xFFFFFF;

    internal static string Key(EndPoint remote, ReadOnlyMemory<byte> token) => remote + "/" + Convert.ToHexString(token.Span);

    internal void AddObserver(Observer o) => _observers[Key(o.Remote, o.Token)] = o;

    internal bool RemoveObserver(EndPoint remote, ReadOnlyMemory<byte> token) => _observers.TryRemove(Key(remote, token), out _);

    internal void RemoveObserversOf(EndPoint remote, Func<Observer, bool> match)
    {
        foreach (var (k, o) in _observers)
            if (Equals(o.Remote, remote) && match(o)) _observers.TryRemove(k, out _);
    }

    internal IEnumerable<Observer> Observers => _observers.Values;

    /// <summary>Re-runs the GET handler for every observer and sends a notification (NON; every 5th CON).</summary>
    public Task NotifyAsync(CancellationToken ct = default) => Server.NotifyAsync(this, ct);
}

/// <summary>CoAP server options.</summary>
public sealed class CoapServerOptions : IDatagramBuilder<CoapServerOptions>
{
    /// <summary>Binds the transport (default: UDP 5683 on all interfaces).</summary>
    public DatagramTransportFactory? TransportFactory { get; set; }

    /// <summary>UDP port when no transport factory is given.</summary>
    public int Port { get; set; } = 5683;

    /// <summary>If a handler takes longer, an empty ACK is sent first and the response follows separately.</summary>
    public TimeSpan SeparateResponseAfter { get; set; } = TimeSpan.FromMilliseconds(800);

    /// <summary>Transmission parameters (Block2 size for large bodies, retransmission of CON notifications).</summary>
    public CoapTransmission Transmission { get; set; } = new();

    /// <summary>Friendly name.</summary>
    public string? Name { get; set; }

    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }

    /// <inheritdoc />
    public CoapServerOptions UseDatagramTransport(DatagramTransportFactory factory)
    {
        TransportFactory = factory;
        return this;
    }
}

/// <summary>
/// CoAP server: resource routing, piggybacked or separate responses, Observe notifications, Block2 for large
/// representations, Block1 uploads, duplicate suppression and an automatic <c>/.well-known/core</c>.
/// </summary>
/// <example>
/// <code>
/// await using var server = CoapServer.Create(o => o.Port = 5683);
/// var temp = server.Map("/sensors/temperature", get: (_, _) => ValueTask.FromResult(CoapReply.Content("21.5")), observable: true);
/// await server.StartAsync();
/// await temp.NotifyAsync();   // after the value changed
/// </code>
/// </example>
public sealed class CoapServer : EndpointBase, IServerEndpoint
{
    private readonly CoapServerOptions _options;
    private readonly ConcurrentDictionary<string, CoapResource> _resources = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, MemoryStream> _uploads = new();
    private CoapStack? _stack;
    private CancellationTokenSource? _cts;
    private long _requests;

    private CoapServer(CoapServerOptions options) : base("coap", options.Logger)
    {
        _options = options;
        Name = options.Name;
    }

    /// <summary>Creates a server (call <see cref="StartAsync"/>).</summary>
    public static CoapServer Create(Action<CoapServerOptions>? configure = null)
    {
        var o = new CoapServerOptions();
        configure?.Invoke(o);
        return new CoapServer(o);
    }

    /// <summary>Local address after start.</summary>
    public EndPoint? LocalEndPoint => _stack?.LocalEndPoint;

    /// <summary>Requests handled.</summary>
    public long RequestCount => Interlocked.Read(ref _requests);

    /// <summary>Message-layer counters.</summary>
    public CoapStatistics Statistics => _stack?.Statistics ?? new CoapStatistics();

    /// <summary>Registered resources.</summary>
    public IReadOnlyCollection<CoapResource> Resources => [.. _resources.Values];

    /// <summary>Adds (or replaces) a resource.</summary>
    public CoapResource Map(string path, CoapHandler? get = null, CoapHandler? put = null, CoapHandler? post = null, CoapHandler? delete = null,
        bool observable = false, string? resourceType = null, string? title = null, ushort? contentFormat = null)
    {
        var normalized = "/" + path.Trim('/');
        var r = new CoapResource(this, normalized) { Get = get, Put = put, Post = post, Delete = delete, Observable = observable };
        if (resourceType is not null) r.Attributes["rt"] = resourceType;
        if (title is not null) r.Attributes["title"] = title;
        if (contentFormat is { } ct) r.Attributes["ct"] = ct.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (observable) r.Attributes["obs"] = "";
        _resources[normalized] = r;
        return r;
    }

    /// <inheritdoc />
    public ValueTask StartAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_stack is not null) return ValueTask.CompletedTask;
        var transport = (_options.TransportFactory ?? (() => new UdpDatagramTransport(new IPEndPoint(IPAddress.Any, _options.Port))))();
        _cts = new CancellationTokenSource();
        _stack = new CoapStack(transport, _options.Transmission, Logger, (dir, bytes, m) => Tap(dir, bytes, m.ToString))
        {
            RequestHandler = OnRequestAsync,
        };
        _stack.ResetReceived += OnReset;
        SetState(EndpointState.Listening);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask StopAsync(CancellationToken ct = default)
    {
        if (_stack is null) return;
        SetState(EndpointState.Stopping);
        if (_cts is not null) await _cts.CancelAsync().ConfigureAwait(false);
        await _stack.DisposeAsync().ConfigureAwait(false);
        _cts?.Dispose();
        (_stack, _cts) = (null, null);
        SetState(EndpointState.Disconnected);
    }

    private readonly ConcurrentDictionary<string, (CoapResource Resource, CoapResource.Observer Observer)> _notificationMids = new();

    private void OnReset(EndPoint remote, ushort mid)
    {
        // A RST to a notification means the client forgot the observation.
        if (_notificationMids.TryRemove(remote + "#" + mid, out var x)) x.Resource.RemoveObserver(x.Observer.Remote, x.Observer.Token);
    }

    private Task OnRequestAsync(CoapMessage request, EndPoint remote)
    {
        // Handlers may be slow: run them off the receive loop.
        _ = Task.Run(() => ProcessAsync(request, remote, _cts?.Token ?? CancellationToken.None));
        return Task.CompletedTask;
    }

    private async Task ProcessAsync(CoapMessage request, EndPoint remote, CancellationToken ct)
    {
        Interlocked.Increment(ref _requests);
        var stack = _stack;
        if (stack is null) return;
        try
        {
            var work = BuildReplyAsync(request, remote, ct);
            var separate = false;
            if (request.Type == CoapType.Confirmable && await Task.WhenAny(work, Task.Delay(_options.SeparateResponseAfter, ct)).ConfigureAwait(false) != work)
            {
                // Slow handler: acknowledge now, answer later as a separate confirmable response.
                separate = true;
                await stack.ReplyAsync(request, CoapMessage.Empty(CoapType.Acknowledgement, request.MessageId), remote, ct).ConfigureAwait(false);
            }
            var reply = await work.ConfigureAwait(false);
            if (reply is null) return;
            reply.Token = request.Token;
            if (separate)
            {
                reply.MessageId = stack.NextMessageId();
                await stack.SendConfirmableAsync(reply, remote, ct).ConfigureAwait(false);
            }
            else
            {
                reply.Type = request.Type == CoapType.Confirmable ? CoapType.Acknowledgement : CoapType.NonConfirmable;
                reply.MessageId = request.Type == CoapType.Confirmable ? request.MessageId : stack.NextMessageId();
                await stack.ReplyAsync(request, reply, remote, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (IoTComException ex)
        {
            Logger.LogDebug(ex, "CoAP response to {Remote} not delivered", remote);
        }
    }

    private async Task<CoapMessage?> BuildReplyAsync(CoapMessage request, EndPoint remote, CancellationToken ct)
    {
        var path = request.UriPath;
        if (path == "/.well-known/core" && request.Code == CoapCode.Get) return Discovery(request);
        if (!_resources.TryGetValue(path, out var resource)) return ErrorMessage(CoapCode.NotFound);

        // Unknown critical options must be rejected (RFC 7252 §5.4.1).
        foreach (var o in request.Options)
            if (CoapOptionNumber.IsCritical(o.Number) && o.Number is not (CoapOptionNumber.UriPath or CoapOptionNumber.UriQuery or CoapOptionNumber.UriHost
                    or CoapOptionNumber.UriPort or CoapOptionNumber.Accept or CoapOptionNumber.Block1 or CoapOptionNumber.Block2 or CoapOptionNumber.IfMatch or CoapOptionNumber.IfNoneMatch))
                return ErrorMessage(CoapCode.BadOption, $"Unsupported critical option {o.Number}");

        var handler = request.Code.Value switch
        {
            0x01 => resource.Get,
            0x02 => resource.Post,
            0x03 => resource.Put,
            0x04 => resource.Delete,
            _ => null,
        };
        if (handler is null) return ErrorMessage(CoapCode.MethodNotAllowed);

        var payload = request.Payload;
        if (request.Block1 is { } b1)
        {
            var key = remote + "|" + path;
            var buffer = b1.Number == 0 ? _uploads[key] = new MemoryStream() : _uploads.GetValueOrDefault(key);
            if (buffer is null || buffer.Length != b1.Offset)
            {
                _uploads.TryRemove(key, out _);
                return ErrorMessage(CoapCode.RequestEntityIncomplete);
            }
            buffer.Write(request.Payload.Span);
            if (b1.More)
            {
                var cont = new CoapMessage { Code = CoapCode.Continue };
                cont.Block1 = b1;
                return cont;
            }
            _uploads.TryRemove(key, out _);
            payload = buffer.ToArray();
        }

        // Observe registration / deregistration on GET.
        var register = false;
        if (request.Code == CoapCode.Get && request.Observe is { } obs && resource.Observable)
        {
            if (obs == 0) register = true;
            else if (obs == 1) resource.RemoveObserver(remote, request.Token);
        }

        CoapReply reply;
        try
        {
            reply = await handler(new CoapRequest(request, remote, payload), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "CoAP handler for {Path} failed", path);
            return ErrorMessage(CoapCode.InternalServerError, ex.Message);
        }

        var message = ToMessage(reply, request.Block2, register ? resource.NextSequence() : null);
        if (register && reply.Code.IsSuccess)
        {
            resource.RemoveObserversOf(remote, o => o.Token.Span.SequenceEqual(request.Token.Span));
            resource.AddObserver(new CoapResource.Observer(remote, request.Token, request.Accept, request));
        }
        if (request.Block1 is { } last) message.Block1 = last; // echo the final Block1 (RFC 7959 §2.3)
        return message;
    }

    private CoapMessage ToMessage(CoapReply reply, CoapBlock? requestedBlock, int? observeSequence)
    {
        var m = new CoapMessage { Code = reply.Code, ContentFormat = reply.ContentFormat, MaxAge = reply.MaxAge };
        if (observeSequence is { } seq) m.Observe = (uint)seq;
        var body = reply.Payload;
        var size = requestedBlock is { } rb ? Math.Min(rb.Size, _options.Transmission.BlockSize) : _options.Transmission.BlockSize;
        if (body.Length > size || requestedBlock is not null)
        {
            // Block2: serve the requested block (block 0 by default).
            var szx = CoapBlock.ExponentFor(size);
            var num = requestedBlock?.Number ?? 0;
            var offset = (long)num * size;
            if (offset > body.Length && body.Length > 0) return ErrorMessage(CoapCode.BadOption, "Block out of range");
            var length = (int)Math.Min(size, Math.Max(0, body.Length - offset));
            m.Payload = body.Slice((int)offset, length);
            m.Block2 = new CoapBlock(num, offset + length < body.Length, szx);
            if (num == 0) m.AddOption(CoapOptionNumber.Size2, (uint)body.Length);
        }
        else
        {
            m.Payload = body;
        }
        return m;
    }

    private static CoapMessage ErrorMessage(CoapCode code, string? diagnostic = null) =>
        new() { Code = code, Payload = diagnostic is null ? default : Encoding.UTF8.GetBytes(diagnostic), ContentFormat = diagnostic is null ? null : CoapContentFormat.TextPlain };

    private CoapMessage Discovery(CoapMessage request)
    {
        // RFC 6690 §4.1: optional filtering by one attribute, e.g. ?rt=temperature-c (trailing * = prefix match).
        var queries = request.UriQuery;
        var filter = queries.Count > 0 ? queries[0].Split('=', 2) : null;
        var links = _resources.Values.OrderBy(r => r.Path, StringComparer.Ordinal)
            .Select(r => new CoapLink(r.Path, r.Attributes))
            .Where(l => filter is not { Length: 2 } f || (f[0] == "href"
                ? Match(l.Path, f[1])
                : l.Attributes.TryGetValue(f[0], out var v) && Match(v, f[1])));
        var reply = CoapReply.Content(CoapLinkFormat.Format(links), CoapContentFormat.LinkFormat);
        return ToMessage(reply, request.Block2, null);

        static bool Match(string value, string pattern) =>
            pattern.EndsWith('*') ? value.StartsWith(pattern[..^1], StringComparison.Ordinal) : value == pattern;
    }

    internal async Task NotifyAsync(CoapResource resource, CancellationToken ct)
    {
        var stack = _stack;
        if (stack is null || resource.Get is null) return;
        foreach (var observer in resource.Observers.ToList())
        {
            CoapReply reply;
            try
            {
                reply = await resource.Get(new CoapRequest(observer.Request, observer.Remote, default), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogWarning(ex, "CoAP notification for {Path} failed", resource.Path);
                continue;
            }
            var m = ToMessage(reply, null, resource.NextSequence());
            m.Token = observer.Token;
            m.MessageId = stack.NextMessageId();
            if (!reply.Code.IsSuccess)
            {
                resource.RemoveObserver(observer.Remote, observer.Token); // an error ends the observation (RFC 7641 §4.2)
                m.Observe = null;
            }
            // Every 5th notification is confirmable so dead observers are detected (RFC 7641 §4.5).
            var confirmable = Interlocked.Increment(ref observer.Sent) % 5 == 0;
            _notificationMids[observer.Remote + "#" + m.MessageId] = (resource, observer);
            try
            {
                if (confirmable)
                {
                    await stack.SendConfirmableAsync(m, observer.Remote, ct).ConfigureAwait(false);
                }
                else
                {
                    m.Type = CoapType.NonConfirmable;
                    await stack.SendAsync(m, observer.Remote, ct).ConfigureAwait(false);
                }
            }
            catch (IoTComException)
            {
                resource.RemoveObserver(observer.Remote, observer.Token); // RST or no ACK: the observer is gone
            }
            finally
            {
                if (confirmable) _notificationMids.TryRemove(observer.Remote + "#" + m.MessageId, out _);
            }
        }
        // Keep the RST map small: forget NON notifications older than the last few hundred.
        if (_notificationMids.Count > 1024) _notificationMids.Clear();
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore() => await StopAsync().ConfigureAwait(false);
}
