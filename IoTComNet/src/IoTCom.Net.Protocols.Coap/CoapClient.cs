using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using IoTCom.Net.Transports;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Protocols.Coap;

/// <summary>A CoAP response (after Block2 reassembly).</summary>
public sealed class CoapResponse
{
    internal CoapResponse(CoapMessage message, ReadOnlyMemory<byte> payload)
    {
        Message = message;
        Payload = payload;
    }

    /// <summary>The (last) response message.</summary>
    public CoapMessage Message { get; }

    /// <summary>Response code.</summary>
    public CoapCode Code => Message.Code;

    /// <summary>Full payload (all blocks).</summary>
    public ReadOnlyMemory<byte> Payload { get; }

    /// <summary>Payload as UTF-8 text.</summary>
    public string PayloadText => Encoding.UTF8.GetString(Payload.Span);

    /// <summary>Content-Format.</summary>
    public ushort? ContentFormat => Message.ContentFormat;

    /// <summary>Observe sequence number of a notification.</summary>
    public uint? ObserveSequence => Message.Observe;

    /// <summary>Max-Age (seconds, default 60).</summary>
    public uint MaxAge => Message.MaxAge ?? 60;

    /// <summary>2.xx.</summary>
    public bool IsSuccess => Code.IsSuccess;

    /// <summary>Throws <see cref="CoapResponseException"/> unless 2.xx.</summary>
    public CoapResponse EnsureSuccess() => IsSuccess ? this : throw new CoapResponseException(this);

    /// <inheritdoc />
    public override string ToString() => $"{Code} {(ContentFormat is { } cf ? CoapContentFormat.Name(cf) + " " : "")}{Payload.Length} B";
}

/// <summary>The server answered with 4.xx or 5.xx.</summary>
public sealed class CoapResponseException : DeviceException
{
    /// <summary>Creates the exception.</summary>
    public CoapResponseException(CoapResponse response)
        : base($"CoAP {response.Code}{(response.Payload.IsEmpty ? "" : ": " + response.PayloadText)}") => Response = response;

    /// <summary>Creates the exception.</summary>
    public CoapResponseException() : base("CoAP error response") { }

    /// <summary>Creates the exception.</summary>
    public CoapResponseException(string message) : base(message) { }

    /// <summary>Creates the exception.</summary>
    public CoapResponseException(string message, Exception? inner) : base(message, inner) { }

    /// <summary>The response.</summary>
    public CoapResponse? Response { get; }
}

/// <summary>CoAP client options.</summary>
public sealed class CoapClientOptions : IDatagramBuilder<CoapClientOptions>
{
    /// <summary>Server host name or address.</summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>Server port (5683; 5684 is coaps).</summary>
    public int Port { get; set; } = 5683;

    /// <summary>Explicit server endpoint (overrides <see cref="Host"/>/<see cref="Port"/>).</summary>
    public EndPoint? Server { get; set; }

    /// <summary>Binds the local transport (default: UDP on any free port).</summary>
    public DatagramTransportFactory? TransportFactory { get; set; }

    /// <summary>Send requests as CON (reliable, default) or NON.</summary>
    public bool Confirmable { get; set; } = true;

    /// <summary>Blocks PUT, POST and DELETE (they change device state).</summary>
    public bool ReadOnly { get; set; }

    /// <summary>Transmission parameters.</summary>
    public CoapTransmission Transmission { get; set; } = new();

    /// <summary>Friendly name.</summary>
    public string? Name { get; set; }

    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }

    /// <summary>Sets the server.</summary>
    public CoapClientOptions UseServer(string host, int port = 5683)
    {
        (Host, Port, Server) = (host, port, null);
        return this;
    }

    /// <summary>Sets the server endpoint.</summary>
    public CoapClientOptions UseServer(EndPoint server)
    {
        Server = server;
        return this;
    }

    /// <inheritdoc />
    public CoapClientOptions UseDatagramTransport(DatagramTransportFactory factory)
    {
        TransportFactory = factory;
        return this;
    }
}

/// <summary>
/// CoAP client (RFC 7252): GET/PUT/POST/DELETE with confirmable retransmission, separate responses, transparent
/// Block-wise transfer (RFC 7959) in both directions, Observe (RFC 7641) as <see cref="IAsyncEnumerable{T}"/>,
/// resource discovery (RFC 6690) and ping.
/// </summary>
/// <example>
/// <code>
/// await using var coap = CoapClient.Create(o => o.UseServer("192.168.1.40"));
/// await coap.ConnectAsync();
/// var temp = await coap.GetAsync("/sensors/temperature");
/// await foreach (var n in coap.ObserveAsync("/sensors/temperature")) Console.WriteLine(n.PayloadText);
/// </code>
/// </example>
public sealed class CoapClient : EndpointBase, IClientEndpoint, ISubscriber<CoapResponse>
{
    private readonly CoapClientOptions _options;
    private CoapStack? _stack;
    private EndPoint? _server;

    private CoapClient(CoapClientOptions options) : base("coap", options.Logger)
    {
        _options = options;
        Name = options.Name;
    }

    /// <summary>Creates a client (call <see cref="ConnectAsync"/>).</summary>
    public static CoapClient Create(Action<CoapClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var o = new CoapClientOptions();
        configure(o);
        return new CoapClient(o);
    }

    /// <summary>Options.</summary>
    public CoapClientOptions Options => _options;

    /// <summary>Message-layer counters (retransmissions, duplicates…).</summary>
    public CoapStatistics Statistics => _stack?.Statistics ?? new CoapStatistics();

    /// <summary>The server endpoint.</summary>
    public EndPoint? Server => _server;

    /// <inheritdoc />
    public async ValueTask ConnectAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_stack is not null) return;
        SetState(EndpointState.Connecting);
        try
        {
            _server = _options.Server ?? await ResolveAsync(_options.Host, _options.Port, ct).ConfigureAwait(false);
            var transport = (_options.TransportFactory ?? (() => new UdpDatagramTransport(new IPEndPoint(
                _server is IPEndPoint ip && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0))))();
            _stack = new CoapStack(transport, _options.Transmission, Logger, (dir, bytes, m) => Tap(dir, bytes, m.ToString));
            SetState(EndpointState.Connected);
        }
        catch (Exception ex)
        {
            SetState(EndpointState.Faulted, ex);
            throw;
        }
    }

    private static async Task<EndPoint> ResolveAsync(string host, int port, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var ip)) return new IPEndPoint(ip, port);
        var addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        var pick = addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? addresses.First();
        return new IPEndPoint(pick, port);
    }

    /// <inheritdoc />
    public async ValueTask DisconnectAsync(CancellationToken ct = default)
    {
        if (_stack is null) return;
        await _stack.DisposeAsync().ConfigureAwait(false);
        _stack = null;
        SetState(EndpointState.Disconnected);
    }

    private CoapStack Stack => _stack ?? throw new InvalidOperationException("CoAP client is not connected. Call ConnectAsync first.");

    /// <summary>GET.</summary>
    public Task<CoapResponse> GetAsync(string path, ushort? accept = null, CancellationToken ct = default)
    {
        var m = NewRequest(CoapCode.Get, path);
        m.Accept = accept;
        return SendAsync(m, ct);
    }

    /// <summary>PUT (blocked in read-only mode).</summary>
    public Task<CoapResponse> PutAsync(string path, ReadOnlyMemory<byte> payload, ushort contentFormat = CoapContentFormat.TextPlain, CancellationToken ct = default) =>
        SendWithBodyAsync(CoapCode.Put, path, payload, contentFormat, ct);

    /// <summary>PUT text.</summary>
    public Task<CoapResponse> PutAsync(string path, string text, CancellationToken ct = default) =>
        PutAsync(path, Encoding.UTF8.GetBytes(text), CoapContentFormat.TextPlain, ct);

    /// <summary>POST (blocked in read-only mode).</summary>
    public Task<CoapResponse> PostAsync(string path, ReadOnlyMemory<byte> payload, ushort contentFormat = CoapContentFormat.TextPlain, CancellationToken ct = default) =>
        SendWithBodyAsync(CoapCode.Post, path, payload, contentFormat, ct);

    /// <summary>DELETE (blocked in read-only mode).</summary>
    public Task<CoapResponse> DeleteAsync(string path, CancellationToken ct = default) => SendAsync(NewRequest(CoapCode.Delete, path), ct);

    /// <summary>Discovers resources (<c>GET /.well-known/core</c>, optionally filtered, e.g. <c>rt=temperature-c</c>).</summary>
    public async Task<IReadOnlyList<CoapLink>> DiscoverAsync(string? query = null, CancellationToken ct = default)
    {
        var r = (await GetAsync("/.well-known/core" + (query is null ? "" : "?" + query), CoapContentFormat.LinkFormat, ct).ConfigureAwait(false)).EnsureSuccess();
        return CoapLinkFormat.Parse(r.PayloadText);
    }

    /// <summary>CoAP ping: an empty CON answered by RST. Returns the round-trip time.</summary>
    public async Task<TimeSpan> PingAsync(CancellationToken ct = default)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            await Stack.SendConfirmableAsync(CoapMessage.Empty(CoapType.Confirmable, Stack.NextMessageId()), _server!, ct).ConfigureAwait(false);
        }
        catch (CoapResetException)
        {
            // RST is the expected answer to a ping.
        }
        return System.Diagnostics.Stopwatch.GetElapsedTime(started);
    }

    private CoapMessage NewRequest(CoapCode code, string path)
    {
        var m = new CoapMessage { Code = code, Type = _options.Confirmable ? CoapType.Confirmable : CoapType.NonConfirmable };
        var q = path.IndexOf('?', StringComparison.Ordinal);
        m.UriPath = q < 0 ? path : path[..q];
        if (q >= 0)
            foreach (var part in path[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
                m.AddOption(CoapOptionNumber.UriQuery, Uri.UnescapeDataString(part));
        return m;
    }

    private async Task<CoapResponse> SendWithBodyAsync(CoapCode code, string path, ReadOnlyMemory<byte> payload, ushort contentFormat, CancellationToken ct)
    {
        if (_options.ReadOnly) throw new ReadOnlyModeException($"CoAP {code} {path} is blocked: the client is in read-only mode.");
        var size = _options.Transmission.BlockSize;
        if (payload.Length <= size)
        {
            var m = NewRequest(code, path);
            m.ContentFormat = contentFormat;
            m.Payload = payload;
            return await SendAsync(m, ct).ConfigureAwait(false);
        }
        // Block1: upload in blocks; the server answers 2.31 Continue until the last one.
        var szx = CoapBlock.ExponentFor(size);
        CoapResponse? last = null;
        for (uint n = 0; n * size < payload.Length; n++)
        {
            var m = NewRequest(code, path);
            m.ContentFormat = contentFormat;
            var offset = (int)(n * size);
            var more = offset + size < payload.Length;
            m.Payload = payload.Slice(offset, Math.Min(size, payload.Length - offset));
            m.Block1 = new CoapBlock(n, more, szx);
            if (n == 0) m.AddOption(CoapOptionNumber.Size1, (uint)payload.Length);
            last = await ExchangeAsync(m, ct).ConfigureAwait(false);
            if (!last.IsSuccess) return last;
        }
        return last!;
    }

    /// <summary>Sends a request and returns the response; Block2 bodies are fetched and reassembled.</summary>
    public async Task<CoapResponse> SendAsync(CoapMessage request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_options.ReadOnly && (request.Code == CoapCode.Put || request.Code == CoapCode.Post || request.Code == CoapCode.Delete))
            throw new ReadOnlyModeException($"CoAP {request.Code} is blocked: the client is in read-only mode.");
        var first = await ExchangeAsync(request, ct).ConfigureAwait(false);
        if (first.Message.Block2 is not { More: true } block) return first;

        // Block2: keep asking for the next block with the same request options.
        using var body = new MemoryStream();
        body.Write(first.Payload.Span);
        var szx = block.SizeExponent;
        var next = block.Number + 1;
        var response = first;
        while (true)
        {
            var m = CloneRequest(request);
            m.Block2 = new CoapBlock(next, false, szx);
            response = await ExchangeAsync(m, ct).ConfigureAwait(false);
            if (!response.IsSuccess) return response;
            body.Write(response.Payload.Span);
            if (response.Message.Block2 is not { More: true } b) break;
            next = b.Number + 1;
            szx = b.SizeExponent;
        }
        return new CoapResponse(response.Message, body.ToArray());
    }

    private static CoapMessage CloneRequest(CoapMessage request)
    {
        var m = new CoapMessage { Code = request.Code, Type = request.Type, Payload = request.Payload };
        foreach (var o in request.Options)
            if (o.Number is not (CoapOptionNumber.Block2 or CoapOptionNumber.Observe)) m.AddOption(o.Number, o.Value);
        return m;
    }

    /// <summary>One request/response exchange (no block handling).</summary>
    private async Task<CoapResponse> ExchangeAsync(CoapMessage request, CancellationToken ct)
    {
        ThrowIfDisposed();
        var stack = Stack;
        request.MessageId = stack.NextMessageId();
        if (request.Token.IsEmpty) request.Token = CoapStack.NewToken();
        var separate = new TaskCompletionSource<CoapMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = stack.RegisterToken(_server!, request.Token, (m, _) => separate.TrySetResult(m));
        CoapMessage response;
        if (request.Type == CoapType.Confirmable)
        {
            var ack = await stack.SendConfirmableAsync(request, _server!, ct).ConfigureAwait(false);
            response = ack.Code.Value != 0 ? ack : await WaitAsync(separate.Task, ct).ConfigureAwait(false);
        }
        else
        {
            await stack.SendAsync(request, _server!, ct).ConfigureAwait(false);
            response = await WaitAsync(separate.Task, ct).ConfigureAwait(false);
        }
        return new CoapResponse(response, response.Payload);
    }

    private async Task<CoapMessage> WaitAsync(Task<CoapMessage> task, CancellationToken ct)
    {
        try
        {
            return await task.WaitAsync(_options.Transmission.ResponseTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new IoTComTimeoutException($"No CoAP response within {_options.Transmission.ResponseTimeout.TotalSeconds:0} s.");
        }
    }

    /// <summary>
    /// Observes a resource (RFC 7641): yields the current state, then every notification until <paramref name="ct"/>
    /// is cancelled (the observation is then cancelled on the server). Out-of-order notifications are dropped.
    /// </summary>
    public async IAsyncEnumerable<CoapResponse> ObserveAsync(string path, ushort? accept = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var stack = Stack;
        var request = NewRequest(CoapCode.Get, path);
        request.Accept = accept;
        request.Observe = 0;
        request.Token = CoapStack.NewToken();
        request.MessageId = stack.NextMessageId();
        var channel = Channel.CreateBounded<CoapMessage>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });
        var reg = stack.RegisterToken(_server!, request.Token, (m, _) => channel.Writer.TryWrite(m));
        try
        {
            if (request.Type == CoapType.Confirmable)
            {
                var ack = await stack.SendConfirmableAsync(request, _server!, ct).ConfigureAwait(false);
                if (ack.Code.Value != 0) channel.Writer.TryWrite(ack);
            }
            else
            {
                await stack.SendAsync(request, _server!, ct).ConfigureAwait(false);
            }

            uint? lastSeq = null;
            var lastAt = DateTime.UtcNow;
            while (true)
            {
                CoapMessage m;
                try
                {
                    m = await channel.Reader.ReadAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }
                // RFC 7641 §3.4 freshness: newer if V1 < V2 < V1 + 2^23 (mod 2^24), or after 128 s.
                if (m.Observe is { } seq && lastSeq is { } prev && DateTime.UtcNow - lastAt < TimeSpan.FromSeconds(128))
                {
                    var newer = (prev < seq && seq - prev < (1u << 23)) || (prev > seq && prev - seq > (1u << 23));
                    if (!newer) continue;
                }
                lastSeq = m.Observe ?? lastSeq;
                lastAt = DateTime.UtcNow;
                var payload = m.Payload;
                if (m.Block2 is { More: true })
                {
                    // Large notification: fetch the rest of the representation with plain GETs.
                    var full = await SendAsync(CloneRequest(request), ct).ConfigureAwait(false);
                    payload = full.Payload;
                }
                yield return new CoapResponse(m, payload);
                if (m.Observe is null) yield break; // server does not support observation of this resource
            }
        }
        finally
        {
            reg.Dispose();
            await CancelObservationAsync(request).ConfigureAwait(false);
        }
    }

    private async Task CancelObservationAsync(CoapMessage request)
    {
        // Explicit deregistration (GET with Observe = 1, same token) so the server stops immediately.
        if (_stack is null) return;
        try
        {
            var m = CloneRequest(request);
            m.Token = request.Token;
            m.Observe = 1;
            m.Type = CoapType.NonConfirmable;
            m.MessageId = _stack.NextMessageId();
            await _stack.SendAsync(m, _server!, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IoTComException or ObjectDisposedException or System.Net.Sockets.SocketException)
        {
            Logger.LogDebug(ex, "CoAP deregistration failed");
        }
    }

    /// <summary>Observes <paramref name="filter"/> (a resource path) as a subscription.</summary>
    public async IAsyncEnumerable<Message<CoapResponse>> SubscribeAsync(string filter, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var r in ObserveAsync(filter, ct: ct).ConfigureAwait(false))
            yield return new Message<CoapResponse>(filter, r, DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore()
    {
        if (_stack is not null) await _stack.DisposeAsync().ConfigureAwait(false);
        _stack = null;
    }
}
