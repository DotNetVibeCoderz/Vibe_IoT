using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using IoTCom.Net.Protocols.Coap;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Protocols.Lwm2m;

/// <summary>A client registration.</summary>
public sealed class Lwm2mRegistration
{
    internal Lwm2mRegistration(string id, string endpoint) => (Id, Endpoint) = (id, endpoint);

    /// <summary>Registration id (location path "rd/{Id}").</summary>
    public string Id { get; }

    /// <summary>Endpoint client name.</summary>
    public string Endpoint { get; }

    /// <summary>Where the client registered from (requests go there).</summary>
    public EndPoint Address { get; internal set; } = new IPEndPoint(IPAddress.None, 0);

    /// <summary>Registration lifetime.</summary>
    public TimeSpan Lifetime { get; internal set; }

    /// <summary>Binding mode.</summary>
    public string Binding { get; internal set; } = "U";

    /// <summary>LwM2M version.</summary>
    public string Version { get; internal set; } = "1.0";

    /// <summary>Object instances the client announced.</summary>
    public IReadOnlyList<Lwm2mPath> Objects { get; internal set; } = [];

    /// <summary>First registration.</summary>
    public DateTimeOffset RegisteredAt { get; internal set; } = DateTimeOffset.UtcNow;

    /// <summary>Last register or update.</summary>
    public DateTimeOffset LastUpdate { get; internal set; } = DateTimeOffset.UtcNow;

    /// <summary>When the registration expires without an update.</summary>
    public DateTimeOffset Expires => LastUpdate + Lifetime;

    /// <inheritdoc />
    public override string ToString() => $"{Endpoint} ({Id}) at {Address}, lifetime {Lifetime.TotalSeconds:0} s, {string.Join(" ", Objects)}";
}

/// <summary>Why a registration ended.</summary>
public enum Lwm2mDeregistration
{
    /// <summary>The client deregistered.</summary>
    Deregistered,
    /// <summary>The lifetime passed without an update.</summary>
    Expired,
    /// <summary>The same endpoint registered again.</summary>
    Replaced,
}

/// <summary>An active observation; dispose to cancel it.</summary>
public sealed class Lwm2mObservation : IAsyncDisposable
{
    private readonly Func<Task> _cancel;

    internal Lwm2mObservation(string endpoint, Lwm2mPath path, IReadOnlyList<Lwm2mValue> initial, Func<Task> cancel) => (Endpoint, Path, Initial, _cancel) = (endpoint, path, initial, cancel);

    /// <summary>Observed client.</summary>
    public string Endpoint { get; }

    /// <summary>Observed path.</summary>
    public Lwm2mPath Path { get; }

    /// <summary>Values returned with the Observe request.</summary>
    public IReadOnlyList<Lwm2mValue> Initial { get; }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await _cancel().ConfigureAwait(false);
}

/// <summary>Options for <see cref="Lwm2mServer"/>.</summary>
public sealed class Lwm2mServerOptions : IDatagramBuilder<Lwm2mServerOptions>
{
    /// <summary>Socket factory (for example <c>UseUdp(5683)</c>).</summary>
    public DatagramTransportFactory? TransportFactory { get; set; }

    /// <summary>Refuses Write and Execute (default true). Reads, observations and discovery stay allowed.</summary>
    public bool ReadOnly { get; set; } = true;

    /// <summary>Time to wait for a client's answer (default 10 s).</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Grace period after a lifetime before a registration expires (default 5 s).</summary>
    public TimeSpan ExpiryGrace { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>CoAP transmission parameters.</summary>
    public CoapTransmission Transmission { get; set; } = new();

    /// <summary>Endpoint name.</summary>
    public string? Name { get; set; }

    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }

    /// <inheritdoc />
    public Lwm2mServerOptions UseDatagramTransport(DatagramTransportFactory factory)
    {
        TransportFactory = factory;
        return this;
    }

    /// <summary>Allows Write and Execute.</summary>
    public Lwm2mServerOptions AllowWrites()
    {
        ReadOnly = false;
        return this;
    }
}

/// <summary>
/// LwM2M server (device management side): accepts registrations, updates and deregistrations, expires silent clients,
/// and runs Read, Discover, Write, Execute, Write-Attributes and Observe on registered clients. Write and Execute are
/// refused until <see cref="Lwm2mServerOptions.AllowWrites"/>.
/// </summary>
public sealed class Lwm2mServer : EndpointBase, IServerEndpoint
{
    private readonly Lwm2mServerOptions _options;
    private readonly ConcurrentDictionary<string, Lwm2mRegistration> _byId = new();
    private CoapStack? _stack;
    private IDatagramTransport? _transport;
    private CancellationTokenSource? _cts;

    private Lwm2mServer(Lwm2mServerOptions options) : base("lwm2m", options.Logger)
    {
        _options = options;
        Name = options.Name;
    }

    /// <summary>Creates a server.</summary>
    public static Lwm2mServer Create(Action<Lwm2mServerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var o = new Lwm2mServerOptions();
        configure(o);
        if (o.TransportFactory is null) throw new ArgumentException("A datagram transport is required (UseUdp(5683), UseInMemory).", nameof(configure));
        return new Lwm2mServer(o);
    }

    /// <summary>Current registrations.</summary>
    public IReadOnlyCollection<Lwm2mRegistration> Registrations => [.. _byId.Values.OrderBy(r => r.Endpoint, StringComparer.Ordinal)];

    /// <summary>Local endpoint after start.</summary>
    public EndPoint? LocalEndPoint => _transport?.LocalEndPoint;

    /// <summary>Raised when a client registers.</summary>
    public event Action<Lwm2mRegistration>? Registered;

    /// <summary>Raised when a client updates its registration.</summary>
    public event Action<Lwm2mRegistration>? Updated;

    /// <summary>Raised when a registration ends.</summary>
    public event Action<Lwm2mRegistration, Lwm2mDeregistration>? Deregistered;

    /// <summary>A registration by endpoint name.</summary>
    public Lwm2mRegistration? Find(string endpoint) => _byId.Values.FirstOrDefault(r => r.Endpoint == endpoint);

    /// <inheritdoc />
    public ValueTask StartAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_stack is not null) return ValueTask.CompletedTask;
        SetState(EndpointState.Connecting);
        _transport = _options.TransportFactory!();
        _stack = new CoapStack(_transport, _options.Transmission, Logger, (d, bytes, m) => Tap(d, bytes, () => m.ToString())) { RequestHandler = OnRequestAsync };
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ExpiryLoopAsync(_cts.Token), CancellationToken.None);
        SetState(EndpointState.Listening);
        return ValueTask.CompletedTask;
    }

    // ---- Registration interface -------------------------------------------------------------------------------------

    private async Task OnRequestAsync(CoapMessage request, EndPoint remote)
    {
        var segments = request.UriPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        CoapMessage reply;
        if (segments is ["rd"] && request.Code == CoapCode.Post) reply = Register(request, remote);
        else if (segments is ["rd", var id] && request.Code == CoapCode.Post) reply = Update(request, remote, id);
        else if (segments is ["rd", var gone] && request.Code == CoapCode.Delete) reply = Deregister(request, gone);
        else reply = Lwm2mExchange.Reply(request, CoapCode.NotFound);
        await _stack!.ReplyAsync(request, reply, remote, _cts?.Token ?? CancellationToken.None).ConfigureAwait(false);
    }

    private static Dictionary<string, string> Query(CoapMessage m) =>
        m.UriQuery.Select(q => q.Split('=', 2)).Where(p => p.Length == 2).GroupBy(p => p[0]).ToDictionary(g => g.Key, g => g.First()[1]);

    private static IReadOnlyList<Lwm2mPath> Objects(CoapMessage m) =>
        [.. CoapLinkFormat.Parse(m.PayloadText).Select(l => Lwm2mPath.TryParse(l.Path, out var p) ? p : (Lwm2mPath?)null).OfType<Lwm2mPath>()];

    private CoapMessage Register(CoapMessage request, EndPoint remote)
    {
        var q = Query(request);
        if (!q.TryGetValue("ep", out var endpoint) || endpoint.Length == 0) return Lwm2mExchange.Reply(request, CoapCode.BadRequest);
        foreach (var old in _byId.Values.Where(r => r.Endpoint == endpoint).ToList())
            if (_byId.TryRemove(old.Id, out _)) Deregistered?.Invoke(old, Lwm2mDeregistration.Replaced);
        var registration = new Lwm2mRegistration(Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant(), endpoint)
        {
            Address = remote,
            Lifetime = TimeSpan.FromSeconds(q.TryGetValue("lt", out var lt) && int.TryParse(lt, CultureInfo.InvariantCulture, out var s) && s > 0 ? s : 86_400),
            Binding = q.GetValueOrDefault("b", "U"),
            Version = q.GetValueOrDefault("lwm2m", "1.0"),
            Objects = Objects(request),
        };
        _byId[registration.Id] = registration;
        Registered?.Invoke(registration);
        var reply = Lwm2mExchange.Reply(request, CoapCode.Created);
        reply.AddOption(CoapOptionNumber.LocationPath, "rd");
        reply.AddOption(CoapOptionNumber.LocationPath, registration.Id);
        return reply;
    }

    private CoapMessage Update(CoapMessage request, EndPoint remote, string id)
    {
        if (!_byId.TryGetValue(id, out var r)) return Lwm2mExchange.Reply(request, CoapCode.NotFound);
        var q = Query(request);
        if (q.TryGetValue("lt", out var lt) && int.TryParse(lt, CultureInfo.InvariantCulture, out var s) && s > 0) r.Lifetime = TimeSpan.FromSeconds(s);
        if (q.TryGetValue("b", out var b)) r.Binding = b;
        if (!request.Payload.IsEmpty) r.Objects = Objects(request);
        r.Address = remote;
        r.LastUpdate = DateTimeOffset.UtcNow;
        Updated?.Invoke(r);
        return Lwm2mExchange.Reply(request, CoapCode.Changed);
    }

    private CoapMessage Deregister(CoapMessage request, string id)
    {
        if (!_byId.TryRemove(id, out var r)) return Lwm2mExchange.Reply(request, CoapCode.NotFound);
        Deregistered?.Invoke(r, Lwm2mDeregistration.Deregistered);
        return Lwm2mExchange.Reply(request, CoapCode.Deleted);
    }

    private async Task ExpiryLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var now = DateTimeOffset.UtcNow;
            foreach (var r in _byId.Values.Where(r => now > r.Expires + _options.ExpiryGrace).ToList())
                if (_byId.TryRemove(r.Id, out _)) Deregistered?.Invoke(r, Lwm2mDeregistration.Expired);
        }
    }

    // ---- Device management -------------------------------------------------------------------------------------------

    private Lwm2mRegistration Require(string endpoint) =>
        Find(endpoint) ?? throw new Lwm2mException($"\"{endpoint}\" is not registered.");

    private async Task<CoapMessage> SendAsync(Lwm2mRegistration r, CoapMessage request, CancellationToken ct, bool routeSeparate = true)
    {
        ThrowIfDisposed();
        var stack = _stack ?? throw new InvalidOperationException("The server is not started.");
        return await Lwm2mExchange.SendAsync(stack, request, r.Address, _options.RequestTimeout, ct, routeSeparate).ConfigureAwait(false);
    }

    private static void Expect(CoapMessage response, CoapCode expected, string operation, Lwm2mPath path)
    {
        if (response.Code != expected) throw new Lwm2mException($"{operation} {path}", response.Code);
    }

    /// <summary>Reads values (Accept <paramref name="format"/>: TLV by default; SenML JSON/CBOR or text also work).</summary>
    public async Task<IReadOnlyList<Lwm2mValue>> ReadAsync(string endpoint, Lwm2mPath path, ushort? format = null, CancellationToken ct = default)
    {
        var request = Lwm2mExchange.Request(CoapCode.Get, path.Segments());
        if (format is { } f) request.Accept = f;
        var response = await SendAsync(Require(endpoint), request, ct).ConfigureAwait(false);
        Expect(response, CoapCode.Content, "Read", path);
        return Lwm2mContent.Decode(response.ContentFormat ?? Lwm2mFormat.Text, path, response.Payload);
    }

    /// <summary>Reads one resource value.</summary>
    public async Task<object> ReadValueAsync(string endpoint, Lwm2mPath path, CancellationToken ct = default) =>
        await ReadAsync(endpoint, path, ct: ct).ConfigureAwait(false) is [var first, ..] ? first.Value : throw new Lwm2mException($"{path} returned no value.");

    /// <summary>Discovers the instances, resources and attributes below a path.</summary>
    public async Task<IReadOnlyList<CoapLink>> DiscoverAsync(string endpoint, Lwm2mPath path, CancellationToken ct = default)
    {
        var request = Lwm2mExchange.Request(CoapCode.Get, path.Segments());
        request.Accept = Lwm2mFormat.Link;
        var response = await SendAsync(Require(endpoint), request, ct).ConfigureAwait(false);
        Expect(response, CoapCode.Content, "Discover", path);
        return CoapLinkFormat.Parse(response.PayloadText);
    }

    /// <summary>Writes values: replace (PUT) or partial update (POST), TLV by default.</summary>
    /// <exception cref="ReadOnlyModeException">The server is read-only.</exception>
    public async Task WriteAsync(string endpoint, Lwm2mPath path, IReadOnlyList<Lwm2mValue> values, bool replace = true, ushort format = Lwm2mFormat.Tlv, CancellationToken ct = default)
    {
        if (_options.ReadOnly) throw new ReadOnlyModeException($"Refusing to write {path} on {endpoint}: the LwM2M server is read-only (AllowWrites()).");
        var request = Lwm2mExchange.Request(replace ? CoapCode.Put : CoapCode.Post, path.Segments());
        request.ContentFormat = format;
        request.Payload = Lwm2mContent.Encode(format, path, values);
        var response = await SendAsync(Require(endpoint), request, ct).ConfigureAwait(false);
        Expect(response, CoapCode.Changed, "Write", path);
    }

    /// <summary>Writes one resource.</summary>
    public Task WriteAsync(string endpoint, Lwm2mPath path, object value, CancellationToken ct = default) =>
        WriteAsync(endpoint, path, [new Lwm2mValue(path, value)], ct: ct);

    /// <summary>Executes a resource (reboot, reset…), with optional arguments.</summary>
    /// <exception cref="ReadOnlyModeException">The server is read-only.</exception>
    public async Task ExecuteAsync(string endpoint, Lwm2mPath path, string? arguments = null, CancellationToken ct = default)
    {
        if (_options.ReadOnly) throw new ReadOnlyModeException($"Refusing to execute {path} on {endpoint}: the LwM2M server is read-only (AllowWrites()).");
        var request = Lwm2mExchange.Request(CoapCode.Post, path.Segments());
        if (arguments is not null) request.Payload = Encoding.UTF8.GetBytes(arguments);
        var response = await SendAsync(Require(endpoint), request, ct).ConfigureAwait(false);
        Expect(response, CoapCode.Changed, "Execute", path);
    }

    /// <summary>Sets notification attributes (minimum and maximum period in seconds).</summary>
    public async Task WriteAttributesAsync(string endpoint, Lwm2mPath path, int? pmin = null, int? pmax = null, CancellationToken ct = default)
    {
        var query = new List<string>();
        if (pmin is { } min) query.Add(string.Create(CultureInfo.InvariantCulture, $"pmin={min}"));
        if (pmax is { } max) query.Add(string.Create(CultureInfo.InvariantCulture, $"pmax={max}"));
        var response = await SendAsync(Require(endpoint), Lwm2mExchange.Request(CoapCode.Put, path.Segments(), query), ct).ConfigureAwait(false);
        Expect(response, CoapCode.Changed, "Write-Attributes", path);
    }

    /// <summary>Observes a path; <paramref name="onNotify"/> runs for every notification. Dispose the result to cancel.</summary>
    public async Task<Lwm2mObservation> ObserveAsync(string endpoint, Lwm2mPath path, Action<IReadOnlyList<Lwm2mValue>> onNotify, ushort? format = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(onNotify);
        var r = Require(endpoint);
        var stack = _stack ?? throw new InvalidOperationException("The server is not started.");
        var request = Lwm2mExchange.Request(CoapCode.Get, path.Segments());
        request.Observe = 0;
        if (format is { } f) request.Accept = f;
        request.Token = CoapStack.NewToken();
        var notifications = stack.RegisterToken(r.Address, request.Token, (m, _) =>
        {
            if (!m.Code.IsSuccess || m.Observe is null) return;
            try
            {
                onNotify(Lwm2mContent.Decode(m.ContentFormat ?? Lwm2mFormat.Text, path, m.Payload));
            }
            catch (ProtocolException ex)
            {
                Logger.LogDebug(ex, "Ignoring an LwM2M notification that could not be decoded");
            }
        });
        CoapMessage response;
        try
        {
            response = await SendAsync(r, request, ct, routeSeparate: false).ConfigureAwait(false);
            Expect(response, CoapCode.Content, "Observe", path);
        }
        catch
        {
            notifications.Dispose();
            throw;
        }

        var initial = Lwm2mContent.Decode(response.ContentFormat ?? Lwm2mFormat.Text, path, response.Payload);
        var token = request.Token;
        return new Lwm2mObservation(endpoint, path, initial, async () =>
        {
            notifications.Dispose();
            var cancel = Lwm2mExchange.Request(CoapCode.Get, path.Segments());
            cancel.Observe = 1;
            cancel.Token = token;
            try
            {
                await SendAsync(r, cancel, CancellationToken.None).ConfigureAwait(false);
            }
            catch (IoTComException ex)
            {
                Logger.LogDebug(ex, "LwM2M observation cancel failed");
            }
        });
    }

    /// <inheritdoc />
    public async ValueTask StopAsync(CancellationToken ct = default)
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts is null) return;
        SetState(EndpointState.Stopping);
        await cts.CancelAsync().ConfigureAwait(false);
        if (_stack is not null) await _stack.DisposeAsync().ConfigureAwait(false);
        if (_transport is not null) await _transport.DisposeAsync().ConfigureAwait(false);
        (_stack, _transport) = (null, null);
        cts.Dispose();
        SetState(EndpointState.Disconnected);
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore() => await StopAsync().ConfigureAwait(false);
}
