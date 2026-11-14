using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Protocols.Ocpp;

/// <summary>Options for <see cref="OcppChargePoint"/>.</summary>
public sealed class OcppChargePointOptions : ITransportBuilder<OcppChargePointOptions>
{
    /// <summary>
    /// CSMS endpoint URL (<c>ws://</c> or <c>wss://</c>) without the identity, e.g. <c>wss://csms.example.com/ocpp/</c>;
    /// the identity is appended as the last path segment.
    /// </summary>
    public Uri? CsmsUrl { get; set; }

    /// <summary>Charge point identity (the last path segment of the WebSocket URL).</summary>
    public string Identity { get; set; } = "CP-001";

    /// <summary>OCPP version to request as the WebSocket subprotocol (default 1.6J).</summary>
    public OcppVersion Version { get; set; } = OcppVersion.Ocpp16;

    /// <summary>Basic authentication password (OCPP security profile 1/2; the user name is the identity). Never logged.</summary>
    public string? Password { get; set; }

    /// <summary>Client certificate for security profile 3 (wss only).</summary>
    public X509Certificate2? ClientCertificate { get; set; }

    /// <summary>Accept any server certificate (labs only; default false).</summary>
    public bool AcceptAnyServerCertificate { get; set; }

    /// <summary>Raw transport (for <c>UseInMemory</c>/<c>UseTcp</c>); when set, the WebSocket upgrade runs over it with plain HTTP.</summary>
    public TransportFactory? TransportFactory { get; set; }

    /// <summary>How long a CALL waits for its answer (default 30 s).</summary>
    public TimeSpan CallTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>WebSocket ping interval (default 30 s; zero disables).</summary>
    public TimeSpan KeepAlive { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Endpoint name.</summary>
    public string? Name { get; set; }

    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }

    /// <summary>Connects to a CSMS URL.</summary>
    public OcppChargePointOptions UseCsms(string url)
    {
        CsmsUrl = new Uri(url.EndsWith('/') ? url : url + "/");
        return this;
    }

    /// <inheritdoc />
    public OcppChargePointOptions UseTransport(TransportFactory factory)
    {
        TransportFactory = factory;
        return this;
    }
}

/// <summary>
/// OCPP charge point (client side): opens the WebSocket to a CSMS with the right subprotocol, sends requests such as
/// BootNotification, StatusNotification and MeterValues, and answers the CSMS's requests through handlers.
/// <see cref="OcppChargePointSimulator"/> builds a complete charger on top of it.
/// </summary>
public sealed class OcppChargePoint : EndpointBase, IClientEndpoint
{
    private readonly OcppChargePointOptions _options;
    private readonly List<Action<OcppSession>> _registrations = [];
    private OcppSession? _session;

    private OcppChargePoint(OcppChargePointOptions options) : base(options.Version == OcppVersion.Ocpp16 ? "ocpp1.6" : "ocpp2.0.1", options.Logger)
    {
        _options = options;
        Name = options.Name ?? options.Identity;
    }

    /// <summary>Creates a charge point.</summary>
    public static OcppChargePoint Create(Action<OcppChargePointOptions> configure)
    {
        var options = new OcppChargePointOptions();
        configure(options);
        if (options.CsmsUrl is null && options.TransportFactory is null)
            throw new ArgumentException("Set a CSMS URL (UseCsms) or a transport (UseInMemory/UseTcp).");
        return new OcppChargePoint(options);
    }

    /// <summary>Charge point identity.</summary>
    public string Identity => _options.Identity;

    /// <summary>Protocol version.</summary>
    public OcppVersion Version => _options.Version;

    /// <summary>The live session (null while disconnected).</summary>
    public OcppSession? Session => _session;

    /// <summary>Raised for every message sent or received.</summary>
    public event Action<OcppMessageEvent>? Message;

    /// <summary>Raised when the connection drops (not after <see cref="DisconnectAsync"/>).</summary>
    public event Action? ConnectionLost;

    /// <inheritdoc />
    public async ValueTask ConnectAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_session is { IsOpen: true }) return;
        SetState(EndpointState.Connecting);
        try
        {
            var (socket, owner) = await OpenAsync(ct).ConfigureAwait(false);
            var session = new OcppSession(socket, Version, Identity, _options.CallTimeout, Logger,
                (d, json, summary) => Tap(d, Encoding.UTF8.GetBytes(json), () => summary), null, owner);
            session.Message += e => Message?.Invoke(e);
            lock (_registrations)
            {
                foreach (var r in _registrations) r(session);
            }

            _session = session;
            session.Start();
            _ = session.Completion.ContinueWith(_ =>
            {
                if (!ReferenceEquals(_session, session)) return;
                SetState(EndpointState.Disconnected);
                ConnectionLost?.Invoke();
            }, TaskScheduler.Default);
            SetState(EndpointState.Connected);
        }
        catch (Exception ex)
        {
            SetState(EndpointState.Faulted, ex);
            throw;
        }
    }

    private async Task<(WebSocket Socket, IAsyncDisposable? Owner)> OpenAsync(CancellationToken ct)
    {
        var path = new Uri(_options.CsmsUrl ?? new Uri("ws://in-memory/ocpp/"), Uri.EscapeDataString(Identity));
        if (_options.TransportFactory is not null)
        {
            var transport = _options.TransportFactory();
            await transport.OpenAsync(ct).ConfigureAwait(false);
            var stream = new DuplexPipeStream(transport.Pipe, transport);
            try
            {
                var socket = await OcppWebSocketHandshake.ConnectAsync(stream, path, Version, _options.Password is null ? null : Identity, _options.Password, _options.KeepAlive, ct).ConfigureAwait(false);
                return (socket, stream);
            }
            catch
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        var client = new ClientWebSocket();
        client.Options.AddSubProtocol(Version.SubProtocol());
        client.Options.KeepAliveInterval = _options.KeepAlive;
        if (_options.Password is not null)
            client.Options.SetRequestHeader("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Identity}:{_options.Password}")));
        if (_options.ClientCertificate is not null) client.Options.ClientCertificates.Add(_options.ClientCertificate);
#pragma warning disable CA5359 // Explicit opt-in for labs with self-signed CSMS certificates; off by default.
        if (_options.AcceptAnyServerCertificate) client.Options.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
#pragma warning restore CA5359
        try
        {
            await client.ConnectAsync(path, ct).ConfigureAwait(false);
        }
        catch (WebSocketException ex)
        {
            client.Dispose();
            throw new TransportException($"Could not open {path}: {ex.Message}", ex);
        }

        if (OcppVersions.FromSubProtocol(client.SubProtocol) != Version)
        {
            client.Abort();
            client.Dispose();
            throw new ProtocolException($"The CSMS did not accept {Version.SubProtocol()} (answered '{client.SubProtocol ?? "no subprotocol"}').");
        }

        return (client, null);
    }

    /// <summary>Registers a raw handler (kept across reconnects).</summary>
    public void On(string action, Func<JsonObject, CancellationToken, ValueTask<JsonObject>> handler) => Register(s => s.On(action, handler));

    /// <summary>Registers a typed handler (kept across reconnects).</summary>
    public void On<TRequest, TResponse>(Func<TRequest, CancellationToken, ValueTask<TResponse>> handler)
        where TRequest : class, IOcppRequest<TResponse>, new() where TResponse : class => Register(s => s.On(handler));

    private void Register(Action<OcppSession> registration)
    {
        lock (_registrations) _registrations.Add(registration);
        if (_session is { } s) registration(s);
    }

    /// <summary>Sends a typed request to the CSMS.</summary>
    public Task<TResponse> CallAsync<TResponse>(IOcppRequest<TResponse> request, CancellationToken ct = default) where TResponse : class =>
        (_session ?? throw new InvalidOperationException("Not connected. Call ConnectAsync first.")).CallAsync(request, ct);

    /// <summary>Sends an untyped request to the CSMS.</summary>
    public Task<JsonObject> CallAsync(string action, JsonObject payload, CancellationToken ct = default) =>
        (_session ?? throw new InvalidOperationException("Not connected. Call ConnectAsync first.")).CallAsync(action, payload, ct);

    /// <inheritdoc />
    public async ValueTask DisconnectAsync(CancellationToken ct = default)
    {
        var session = Interlocked.Exchange(ref _session, null);
        if (session is null) return;
        SetState(EndpointState.Stopping);
        await session.CloseAsync("", ct).ConfigureAwait(false);
        SetState(EndpointState.Disconnected);
    }

    /// <summary>Drops the connection without a close handshake, as a lost network would; <see cref="ConnectionLost"/> fires.</summary>
    public async ValueTask AbortAsync()
    {
        if (_session is { } s) await s.AbortAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore() => await DisconnectAsync().ConfigureAwait(false);
}
