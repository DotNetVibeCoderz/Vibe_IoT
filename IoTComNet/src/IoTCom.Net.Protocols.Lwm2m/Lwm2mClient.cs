using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using IoTCom.Net.Protocols.Coap;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Protocols.Lwm2m;

/// <summary>An object instance on an LwM2M client: resource values, write validators and execute handlers.</summary>
public sealed class Lwm2mInstance
{
    private readonly Lock _gate = new();
    private readonly Dictionary<ushort, object> _single = [];
    private readonly Dictionary<ushort, SortedDictionary<ushort, object>> _multiple = [];
    private readonly Dictionary<ushort, Func<string?, bool>> _execute = [];
    private readonly Dictionary<ushort, Func<object, bool>> _accept = [];

    internal Lwm2mInstance(ushort objectId, ushort instanceId)
    {
        (ObjectId, InstanceId) = (objectId, instanceId);
        Definition = Lwm2mRegistry.Find(objectId);
    }

    /// <summary>Object id.</summary>
    public ushort ObjectId { get; }

    /// <summary>Instance id.</summary>
    public ushort InstanceId { get; }

    /// <summary>The object definition (null for objects the registry does not know: every resource is then read/write).</summary>
    public Lwm2mObjectDefinition? Definition { get; }

    /// <summary>The instance path.</summary>
    public Lwm2mPath Path => new(ObjectId, InstanceId);

    internal event Action<Lwm2mPath>? Changed;

    /// <summary>Raised after the server wrote a resource (path and new value).</summary>
    public event Action<Lwm2mValue>? Written;

    /// <summary>Sets a single-instance resource (notifies observers).</summary>
    public Lwm2mInstance Set(ushort resource, object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var type = Definition?.Resource(resource)?.Type;
        lock (_gate) _single[resource] = type is null or Lwm2mType.None ? value : Lwm2mValue.Normalise(value, type.Value);
        Changed?.Invoke(new Lwm2mPath(ObjectId, InstanceId, resource));
        return this;
    }

    /// <summary>Sets one instance of a multiple-instance resource.</summary>
    public Lwm2mInstance Set(ushort resource, ushort resourceInstance, object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var type = Definition?.Resource(resource)?.Type;
        lock (_gate)
        {
            if (!_multiple.TryGetValue(resource, out var map)) _multiple[resource] = map = [];
            map[resourceInstance] = type is null or Lwm2mType.None ? value : Lwm2mValue.Normalise(value, type.Value);
        }

        Changed?.Invoke(new Lwm2mPath(ObjectId, InstanceId, resource, resourceInstance));
        return this;
    }

    /// <summary>A resource value (single-instance resources).</summary>
    public object? Get(ushort resource)
    {
        lock (_gate) return _single.GetValueOrDefault(resource);
    }

    /// <summary>Handles Execute on a resource; return false to answer 4.00.</summary>
    public Lwm2mInstance OnExecute(ushort resource, Func<string?, bool> handler)
    {
        lock (_gate) _execute[resource] = handler;
        return this;
    }

    /// <summary>Validates writes to a resource; return false to answer 4.00 (the value is not stored).</summary>
    public Lwm2mInstance OnWrite(ushort resource, Func<object, bool> accept)
    {
        lock (_gate) _accept[resource] = accept;
        return this;
    }

    internal Lwm2mOperations Allowed(ushort resource)
    {
        lock (_gate)
        {
            if (Definition?.Resource(resource) is { } def) return def.Operations;
            return _execute.ContainsKey(resource) ? Lwm2mOperations.Execute : Lwm2mOperations.ReadWrite;
        }
    }

    internal bool Exists(ushort resource)
    {
        lock (_gate) return _single.ContainsKey(resource) || _multiple.ContainsKey(resource) || _execute.ContainsKey(resource);
    }

    internal IReadOnlyList<ushort> ResourceIds()
    {
        lock (_gate) return [.. _single.Keys.Concat(_multiple.Keys).Concat(_execute.Keys).Distinct().Order()];
    }

    internal List<Lwm2mValue> Values(Lwm2mPath within)
    {
        var list = new List<Lwm2mValue>();
        lock (_gate)
        {
            foreach (var (id, value) in _single)
                if ((Allowed(id) & Lwm2mOperations.Read) != 0) list.Add(new Lwm2mValue(new Lwm2mPath(ObjectId, InstanceId, id), value));
            foreach (var (id, map) in _multiple)
                if ((Allowed(id) & Lwm2mOperations.Read) != 0)
                    foreach (var (ri, value) in map) list.Add(new Lwm2mValue(new Lwm2mPath(ObjectId, InstanceId, id, ri), value));
        }

        return [.. list.Where(v => within.Contains(v.Path)).OrderBy(v => v.Path.ResourceId).ThenBy(v => v.Path.ResourceInstanceId)];
    }

    internal bool TryWrite(Lwm2mValue value, out string? problem)
    {
        var id = value.Path.ResourceId!.Value;
        Func<object, bool>? accept;
        lock (_gate) accept = _accept.GetValueOrDefault(id);
        if (accept is not null && !accept(value.Value))
        {
            problem = $"{value.Path} refused {Lwm2mValue.Format(value.Value)}";
            return false;
        }

        if (value.Path.ResourceInstanceId is { } ri) Set(id, ri, value.Value);
        else Set(id, value.Value);
        Written?.Invoke(value);
        problem = null;
        return true;
    }

    internal bool TryExecute(ushort resource, string? arguments)
    {
        Func<string?, bool>? handler;
        lock (_gate) handler = _execute.GetValueOrDefault(resource);
        return handler?.Invoke(arguments) ?? true;
    }
}

/// <summary>Options for <see cref="Lwm2mClient"/>.</summary>
public sealed class Lwm2mClientOptions : IDatagramBuilder<Lwm2mClientOptions>
{
    /// <summary>Endpoint client name (the device's identity at the server, e.g. a serial number or URN).</summary>
    public string EndpointName { get; set; } = "iotcom-device";

    /// <summary>Server address (default 127.0.0.1:5683).</summary>
    public EndPoint Server { get; set; } = new IPEndPoint(IPAddress.Loopback, 5683);

    /// <summary>Server host name, resolved when connecting (overrides <see cref="Server"/>).</summary>
    public string? ServerHost { get; set; }

    /// <summary>Server port when <see cref="ServerHost"/> is used.</summary>
    public int ServerPort { get; set; } = 5683;

    /// <summary>Registration lifetime (default 300 s). Updates are sent at 80 % of it.</summary>
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromSeconds(300);

    /// <summary>Binding mode (default "U": UDP, queue mode off).</summary>
    public string Binding { get; set; } = "U";

    /// <summary>LwM2M version announced (default 1.1).</summary>
    public string Version { get; set; } = "1.1";

    /// <summary>Format for reads without an Accept option when more than one value is returned (default TLV).</summary>
    public ushort DefaultFormat { get; set; } = Lwm2mFormat.Tlv;

    /// <summary>CoAP transmission parameters.</summary>
    public CoapTransmission Transmission { get; set; } = new();

    /// <summary>Socket factory (default UDP on any port).</summary>
    public DatagramTransportFactory? TransportFactory { get; set; }

    /// <summary>Endpoint name for logs and taps.</summary>
    public string? Name { get; set; }

    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }

    /// <inheritdoc />
    public Lwm2mClientOptions UseDatagramTransport(DatagramTransportFactory factory)
    {
        TransportFactory = factory;
        return this;
    }

    /// <summary>Uses a server by name or address (default port 5683).</summary>
    public Lwm2mClientOptions UseServer(string host, int port = 5683)
    {
        (ServerHost, ServerPort) = (host, port);
        return this;
    }

    /// <summary>Uses a server endpoint.</summary>
    public Lwm2mClientOptions UseServer(EndPoint server)
    {
        (Server, ServerHost) = (server, null);
        return this;
    }
}

/// <summary>What the server asked the client to do.</summary>
/// <param name="Operation">Read, Discover, Observe, Write, Execute, WriteAttributes…</param>
/// <param name="Path">Target path.</param>
/// <param name="Result">CoAP response code sent back.</param>
public sealed record Lwm2mRequestRecord(string Operation, string Path, CoapCode Result);

/// <summary>
/// LwM2M client (the device side, LwM2M 1.0/1.1 over CoAP/UDP): registers with the server, keeps the registration
/// alive with updates, deregisters, and serves Read, Discover, Write (replace and partial update), Execute, Observe
/// (with pmin/pmax from Write-Attributes) and observation cancel on its object instances.
/// </summary>
public sealed class Lwm2mClient : EndpointBase, IClientEndpoint
{
    private readonly Lwm2mClientOptions _options;
    private readonly ConcurrentDictionary<(ushort, ushort), Lwm2mInstance> _instances = new();
    private readonly ConcurrentDictionary<string, Observer> _observers = new();
    private readonly ConcurrentDictionary<string, (int? Pmin, int? Pmax)> _attributes = new();
    private CoapStack? _stack;
    private IDatagramTransport? _transport;
    private EndPoint? _server;
    private CancellationTokenSource? _cts;

    private sealed class Observer(EndPoint remote, ReadOnlyMemory<byte> token, Lwm2mPath path, ushort format)
    {
        public EndPoint Remote { get; } = remote;
        public ReadOnlyMemory<byte> Token { get; } = token;
        public Lwm2mPath Path { get; } = path;
        public ushort Format { get; } = format;
        public uint Sequence { get; set; } = 2;
        public long LastSent { get; set; } = Environment.TickCount64;
        public bool Dirty { get; set; }
    }

    private Lwm2mClient(Lwm2mClientOptions options) : base("lwm2m", options.Logger)
    {
        _options = options;
        Name = options.Name ?? options.EndpointName;
    }

    /// <summary>Creates a client.</summary>
    public static Lwm2mClient Create(Action<Lwm2mClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var o = new Lwm2mClientOptions();
        configure(o);
        o.TransportFactory ??= () => new Transports.UdpDatagramTransport(new IPEndPoint(IPAddress.Any, 0));
        return new Lwm2mClient(o);
    }

    /// <summary>Registration id assigned by the server (the location path).</summary>
    public string? RegistrationId { get; private set; }

    /// <summary>True while registered.</summary>
    public bool IsRegistered => RegistrationId is not null;

    /// <summary>Object instances.</summary>
    public IReadOnlyCollection<Lwm2mInstance> Instances => [.. _instances.Values.OrderBy(i => i.ObjectId).ThenBy(i => i.InstanceId)];

    /// <summary>Number of active observations.</summary>
    public int ObservationCount => _observers.Count;

    /// <summary>Raised after every request from the server.</summary>
    public event Action<Lwm2mRequestRecord>? RequestHandled;

    /// <summary>Raised when the registration was created or renewed (registration id).</summary>
    public event Action<string>? Registered;

    /// <summary>Adds an object instance (or returns the existing one).</summary>
    public Lwm2mInstance AddInstance(ushort objectId, ushort instanceId = 0)
    {
        var instance = _instances.GetOrAdd((objectId, instanceId), _ => new Lwm2mInstance(objectId, instanceId));
        instance.Changed -= OnChanged;
        instance.Changed += OnChanged;
        return instance;
    }

    /// <summary>An instance, if present.</summary>
    public Lwm2mInstance? Instance(ushort objectId, ushort instanceId = 0) => _instances.GetValueOrDefault((objectId, instanceId));

    private void OnChanged(Lwm2mPath path)
    {
        foreach (var o in _observers.Values)
            if (o.Path.Contains(path) || path.Contains(o.Path)) o.Dirty = true;
    }

    /// <summary>The registration links: one per object instance.</summary>
    public string Links() => string.Join(",", Instances.Select(i => $"<{i.Path}>"));

    /// <inheritdoc />
    public async ValueTask ConnectAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (IsRegistered) return;
        SetState(EndpointState.Connecting);
        _server = _options.ServerHost is { } host
            ? new IPEndPoint((await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false)).First(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork), _options.ServerPort)
            : _options.Server;
        if (_stack is null)
        {
            _transport = _options.TransportFactory!();
            _stack = new CoapStack(_transport, _options.Transmission, Logger, (d, bytes, m) => Tap(d, bytes, () => m.ToString())) { RequestHandler = OnRequestAsync };
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => NotifyLoopAsync(_cts.Token), CancellationToken.None);
            _ = Task.Run(() => UpdateLoopAsync(_cts.Token), CancellationToken.None);
        }

        try
        {
            await RegisterAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SetState(EndpointState.Disconnected, ex);
            throw;
        }

        SetState(EndpointState.Connected);
    }

    private async Task RegisterAsync(CancellationToken ct)
    {
        var request = Lwm2mExchange.Request(CoapCode.Post, ["rd"],
            [$"ep={_options.EndpointName}", string.Create(CultureInfo.InvariantCulture, $"lt={(int)_options.Lifetime.TotalSeconds}"), $"lwm2m={_options.Version}", $"b={_options.Binding}"]);
        request.ContentFormat = Lwm2mFormat.Link;
        request.Payload = Encoding.UTF8.GetBytes(Links());
        var response = await Lwm2mExchange.SendAsync(_stack!, request, _server!, _options.Transmission.ResponseTimeout, ct).ConfigureAwait(false);
        if (response.Code != CoapCode.Created) throw new Lwm2mException("Register", response.Code);
        var location = response.GetOptions(CoapOptionNumber.LocationPath).Select(o => o.Text).ToList();
        if (location.Count < 2 || location[0] != "rd") throw new ProtocolException("The server's Register response has no rd/{id} location.");
        RegistrationId = location[1];
        Registered?.Invoke(RegistrationId);
    }

    /// <summary>Sends a registration update now (with the object links when <paramref name="withObjects"/>); re-registers if the server forgot us.</summary>
    public async Task UpdateAsync(bool withObjects = false, CancellationToken ct = default)
    {
        if (RegistrationId is not { } id || _stack is null) throw new InvalidOperationException("Not registered.");
        var request = Lwm2mExchange.Request(CoapCode.Post, ["rd", id]);
        if (withObjects)
        {
            request.ContentFormat = Lwm2mFormat.Link;
            request.Payload = Encoding.UTF8.GetBytes(Links());
        }

        var response = await Lwm2mExchange.SendAsync(_stack, request, _server!, _options.Transmission.ResponseTimeout, ct).ConfigureAwait(false);
        if (response.Code == CoapCode.NotFound)
        {
            RegistrationId = null;
            await RegisterAsync(ct).ConfigureAwait(false);
        }
        else if (response.Code != CoapCode.Changed)
        {
            throw new Lwm2mException("Update", response.Code);
        }
    }

    private async Task UpdateLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.Lifetime * 0.8, ct).ConfigureAwait(false);
                if (IsRegistered) await UpdateAsync(ct: ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (IoTComException ex)
            {
                Logger.LogWarning(ex, "LwM2M registration update failed");
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisconnectAsync(CancellationToken ct = default)
    {
        if (RegistrationId is { } id && _stack is not null)
        {
            try
            {
                await Lwm2mExchange.SendAsync(_stack, Lwm2mExchange.Request(CoapCode.Delete, ["rd", id]), _server!, _options.Transmission.ResponseTimeout, ct).ConfigureAwait(false);
            }
            catch (IoTComException ex)
            {
                Logger.LogDebug(ex, "LwM2M deregister failed");
            }
        }

        RegistrationId = null;
        _observers.Clear();
        SetState(EndpointState.Disconnected);
    }

    /// <summary>
    /// Drops off the network without deregistering (a power cut or lost coverage): updates and notifications stop,
    /// requests go unanswered, and the server expires the registration after its lifetime.
    /// </summary>
    public async ValueTask AbortAsync()
    {
        if (_cts is not null) await _cts.CancelAsync().ConfigureAwait(false);
        if (_stack is not null) await _stack.DisposeAsync().ConfigureAwait(false);
        if (_transport is not null) await _transport.DisposeAsync().ConfigureAwait(false);
        (_stack, _transport, RegistrationId) = (null, null, null);
        _observers.Clear();
        SetState(EndpointState.Disconnected);
    }

    // ---- Requests from the server ----------------------------------------------------------------------------------

    private async Task OnRequestAsync(CoapMessage request, EndPoint remote)
    {
        var (operation, code, format, payload) = Handle(request, remote);
        var reply = Lwm2mExchange.Reply(request, code, format, payload);
        if (operation == "Observe" && code == CoapCode.Content) reply.Observe = 1;
        await _stack!.ReplyAsync(request, reply, remote, _cts?.Token ?? CancellationToken.None).ConfigureAwait(false);
        RequestHandled?.Invoke(new Lwm2mRequestRecord(operation, "/" + request.UriPath, code));
    }

    private (string Operation, CoapCode Code, ushort? Format, byte[]? Payload) Handle(CoapMessage request, EndPoint remote)
    {
        if (!Lwm2mPath.TryParse(request.UriPath, out var path)) return ("?", CoapCode.NotFound, null, null);
        var instances = _instances.Values.Where(i => i.ObjectId == path.ObjectId && (path.InstanceId is null || i.InstanceId == path.InstanceId)).ToList();
        if (instances.Count == 0) return (OperationName(request), CoapCode.NotFound, null, null);
        var instance = path.InstanceId is null ? null : instances[0];
        if (path.ResourceId is { } rid && instance is not null && !instance.Exists(rid)) return (OperationName(request), CoapCode.NotFound, null, null);
        try
        {
            if (request.Code == CoapCode.Get)
            {
                if (request.Accept == Lwm2mFormat.Link) return ("Discover", CoapCode.Content, Lwm2mFormat.Link, Encoding.UTF8.GetBytes(Discover(path, instances)));
                if (path.ResourceId is { } r && instance is not null && (instance.Allowed(r) & Lwm2mOperations.Read) == 0) return ("Read", CoapCode.MethodNotAllowed, null, null);
                var values = instances.SelectMany(i => i.Values(path)).ToList();
                var format = request.Accept ?? (values.Count == 1 && path.Depth >= 3 ? (values[0].Value is byte[] ? Lwm2mFormat.Opaque : Lwm2mFormat.Text) : _options.DefaultFormat);
                byte[] body;
                try
                {
                    body = Lwm2mContent.Encode(format, path, values);
                }
                catch (ArgumentException)
                {
                    return ("Read", CoapCode.NotAcceptable, null, null);
                }

                if (request.Observe == 0)
                {
                    _observers[Key(remote, request.Token)] = new Observer(remote, request.Token, path, format);
                    return ("Observe", CoapCode.Content, format, body);
                }

                if (request.Observe == 1) _observers.TryRemove(Key(remote, request.Token), out _);
                return ("Read", CoapCode.Content, format, body);
            }

            if (request.Code == CoapCode.Put && request.Payload.IsEmpty && request.UriQuery.Count > 0)
            {
                _attributes[path.ToString()] = (Attribute(request.UriQuery, "pmin"), Attribute(request.UriQuery, "pmax"));
                return ("WriteAttributes", CoapCode.Changed, null, null);
            }

            if (request.Code == CoapCode.Post && path.Depth == 3 && instance is not null && (instance.Allowed(path.ResourceId!.Value) & Lwm2mOperations.Execute) != 0)
            {
                var arguments = request.Payload.IsEmpty ? null : request.PayloadText;
                return ("Execute", instance.TryExecute(path.ResourceId!.Value, arguments) ? CoapCode.Changed : CoapCode.BadRequest, null, null);
            }

            if (request.Code == CoapCode.Put || request.Code == CoapCode.Post)
            {
                if (path.Depth < 2 || instance is null) return ("Write", CoapCode.MethodNotAllowed, null, null);
                var values = Lwm2mContent.Decode(request.ContentFormat ?? Lwm2mFormat.Text, path, request.Payload);
                foreach (var v in values)
                    if (v.Path.InstanceId != instance.InstanceId || v.Path.ObjectId != instance.ObjectId || (instance.Allowed(v.Path.ResourceId!.Value) & Lwm2mOperations.Write) == 0)
                        return ("Write", CoapCode.MethodNotAllowed, null, null);
                foreach (var v in values)
                    if (!instance.TryWrite(v, out _)) return ("Write", CoapCode.BadRequest, null, null);
                return ("Write", CoapCode.Changed, null, null);
            }

            return (OperationName(request), CoapCode.MethodNotAllowed, null, null);
        }
        catch (ProtocolException ex)
        {
            Logger.LogDebug(ex, "LwM2M request {Request} rejected", request);
            return (OperationName(request), request.ContentFormat is { } cf && cf is not (Lwm2mFormat.Tlv or Lwm2mFormat.Text or Lwm2mFormat.Opaque or Lwm2mFormat.SenMLJson or Lwm2mFormat.SenMLCbor)
                ? CoapCode.UnsupportedContentFormat : CoapCode.BadRequest, null, null);
        }
    }

    private static string OperationName(CoapMessage request) => request.Code == CoapCode.Get ? "Read" : request.Code == CoapCode.Delete ? "Delete" : "Write";

    private static int? Attribute(IReadOnlyList<string> query, string name) =>
        query.Select(q => q.Split('=', 2)).Where(p => p.Length == 2 && p[0] == name && int.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out _))
            .Select(p => (int?)int.Parse(p[1], CultureInfo.InvariantCulture)).FirstOrDefault();

    private string Discover(Lwm2mPath path, List<Lwm2mInstance> instances)
    {
        var links = new List<string>();
        if (path.Depth == 1) links.Add($"<{path}>");
        foreach (var i in instances)
        {
            links.Add($"<{i.Path}>{AttributesText(i.Path)}");
            foreach (var r in i.ResourceIds().Where(r => path.ResourceId is null || r == path.ResourceId))
                links.Add($"<{i.Path}/{r}>{AttributesText(new Lwm2mPath(i.ObjectId, i.InstanceId, r))}");
        }

        return string.Join(",", links);
    }

    private string AttributesText(Lwm2mPath path) => _attributes.TryGetValue(path.ToString(), out var a)
        ? (a.Pmin is { } min ? string.Create(CultureInfo.InvariantCulture, $";pmin={min}") : "") + (a.Pmax is { } max ? string.Create(CultureInfo.InvariantCulture, $";pmax={max}") : "")
        : "";

    private static string Key(EndPoint remote, ReadOnlyMemory<byte> token) => $"{remote}|{Convert.ToHexString(token.Span)}";

    // ---- Notifications ----------------------------------------------------------------------------------------------

    private (int Pmin, int Pmax) AttributesFor(Lwm2mPath path)
    {
        // The most specific path with attributes wins; then its parents (LwM2M attribute inheritance).
        for (var p = (Lwm2mPath?)path; p is { } at; p = Parent(at))
            if (_attributes.TryGetValue(at.ToString(), out var a)) return (a.Pmin ?? 0, a.Pmax ?? 0);
        return (0, 0);
    }

    private static Lwm2mPath? Parent(Lwm2mPath p) => p.Depth switch
    {
        4 => p with { ResourceInstanceId = null },
        3 => p with { ResourceId = null },
        2 => p with { InstanceId = null },
        _ => null,
    };

    private async Task NotifyLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var now = Environment.TickCount64;
            foreach (var (key, o) in _observers)
            {
                var (pmin, pmax) = AttributesFor(o.Path);
                var elapsed = now - o.LastSent;
                var due = (o.Dirty && elapsed >= pmin * 1000L) || (pmax > 0 && elapsed >= pmax * 1000L);
                if (!due) continue;
                o.Dirty = false;
                o.LastSent = now;
                _ = NotifyAsync(key, o, ct);
            }
        }
    }

    private async Task NotifyAsync(string key, Observer o, CancellationToken ct)
    {
        try
        {
            var values = _instances.Values.Where(i => i.ObjectId == o.Path.ObjectId && (o.Path.InstanceId is null || i.InstanceId == o.Path.InstanceId)).SelectMany(i => i.Values(o.Path)).ToList();
            var notification = new CoapMessage { Code = CoapCode.Content, Token = o.Token, MessageId = _stack!.NextMessageId(), Payload = Lwm2mContent.Encode(o.Format, o.Path, values) };
            notification.Observe = o.Sequence++ & 0xFF_FFFF;
            notification.ContentFormat = o.Format;
            await _stack.SendConfirmableAsync(notification, o.Remote, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CoapResetException or IoTComTimeoutException)
        {
            _observers.TryRemove(key, out _);   // the server forgot the observation
        }
        catch (Exception ex) when (ex is OperationCanceledException or ArgumentException or TransportException)
        {
            Logger.LogDebug(ex, "LwM2M notification for {Path} failed", o.Path);
        }
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore()
    {
        await DisconnectAsync().ConfigureAwait(false);
        if (_cts is not null) await _cts.CancelAsync().ConfigureAwait(false);
        if (_stack is not null) await _stack.DisposeAsync().ConfigureAwait(false);
        if (_transport is not null) await _transport.DisposeAsync().ConfigureAwait(false);
        _cts?.Dispose();
    }
}
