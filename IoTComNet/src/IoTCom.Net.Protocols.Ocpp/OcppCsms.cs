using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging;
using V16 = IoTCom.Net.Protocols.Ocpp.V16;
using V201 = IoTCom.Net.Protocols.Ocpp.V201;

namespace IoTCom.Net.Protocols.Ocpp;

/// <summary>A charging transaction as the CSMS sees it.</summary>
public sealed class OcppTransaction
{
    internal OcppTransaction(string id, int connectorId, string idTag, DateTimeOffset started, double meterStartWh) =>
        (Id, ConnectorId, IdTag, StartedAt, MeterStartWh, MeterWh) = (id, connectorId, idTag, started, meterStartWh, meterStartWh);

    /// <summary>Transaction id (an integer in 1.6, a string chosen by the station in 2.0.1).</summary>
    public string Id { get; }

    /// <summary>Connector (1.6) or EVSE (2.0.1) id.</summary>
    public int ConnectorId { get; }

    /// <summary>The id tag / token that started it.</summary>
    public string IdTag { get; }

    /// <summary>Start time reported by the charger.</summary>
    public DateTimeOffset StartedAt { get; }

    /// <summary>Energy register at the start, Wh.</summary>
    public double MeterStartWh { get; }

    /// <summary>Latest energy register, Wh.</summary>
    public double MeterWh { get; internal set; }

    /// <summary>Energy delivered so far, Wh.</summary>
    public double EnergyWh => Math.Max(0, MeterWh - MeterStartWh);

    /// <summary>End time, when stopped.</summary>
    public DateTimeOffset? StoppedAt { get; internal set; }

    /// <summary>Why it stopped, when stopped.</summary>
    public string? StopReason { get; internal set; }

    /// <inheritdoc />
    public override string ToString() => $"tx {Id} on {ConnectorId} by {IdTag}: {EnergyWh / 1000:0.00} kWh" + (StoppedAt is null ? "" : $", stopped ({StopReason})");
}

/// <summary>One connector (1.6) or EVSE (2.0.1) of a station.</summary>
public sealed class OcppConnectorState
{
    internal OcppConnectorState(int id) => Id = id;

    /// <summary>Connector or EVSE id (0 is the whole charge point in 1.6).</summary>
    public int Id { get; }

    /// <summary>Status as reported (1.6 ChargePointStatus or 2.0.1 ConnectorStatus name).</summary>
    public string Status { get; internal set; } = "Unknown";

    /// <summary>2.0.1 charging state of the running transaction (Charging, SuspendedEV, …), or the 1.6 status.</summary>
    public string? ChargingState { get; internal set; }

    /// <summary>Error code reported with the status (1.6), or null.</summary>
    public string? ErrorCode { get; internal set; }

    /// <summary>Running transaction, if any.</summary>
    public OcppTransaction? Transaction { get; internal set; }

    /// <summary>Latest power, W.</summary>
    public double? PowerW { get; internal set; }

    /// <summary>Latest energy register, Wh.</summary>
    public double? EnergyWh { get; internal set; }

    /// <summary>Latest state of charge, percent.</summary>
    public double? SoC { get; internal set; }

    /// <summary>Power limit set by this CSMS, W (null when none).</summary>
    public double? LimitW { get; internal set; }

    /// <summary>When it last changed.</summary>
    public DateTimeOffset Updated { get; internal set; } = DateTimeOffset.UtcNow;
}

/// <summary>A charge point known to the CSMS.</summary>
public sealed class OcppStation
{
    private readonly ConcurrentDictionary<int, OcppConnectorState> _connectors = new();

    internal OcppStation(string identity) => Identity = identity;

    /// <summary>Identity (last path segment of its WebSocket URL).</summary>
    public string Identity { get; }

    /// <summary>Protocol version of the current connection.</summary>
    public OcppVersion Version { get; internal set; }

    /// <summary>Vendor from BootNotification.</summary>
    public string? Vendor { get; internal set; }

    /// <summary>Model from BootNotification.</summary>
    public string? Model { get; internal set; }

    /// <summary>Serial number from BootNotification.</summary>
    public string? SerialNumber { get; internal set; }

    /// <summary>Firmware version from BootNotification.</summary>
    public string? FirmwareVersion { get; internal set; }

    /// <summary>True after an accepted BootNotification on the current connection.</summary>
    public bool Booted { get; internal set; }

    /// <summary>Peer address.</summary>
    public string RemoteAddress { get; internal set; } = "";

    /// <summary>True while connected.</summary>
    public bool IsOnline => Session is { IsOpen: true };

    /// <summary>Last message received.</summary>
    public DateTimeOffset LastSeen { get; internal set; }

    /// <summary>Last Heartbeat received.</summary>
    public DateTimeOffset? LastHeartbeat { get; internal set; }

    /// <summary>The live session, for typed or raw calls (null while offline).</summary>
    public OcppSession? Session { get; internal set; }

    /// <summary>Connectors (1.6) or EVSEs (2.0.1) seen so far, by id.</summary>
    public IReadOnlyDictionary<int, OcppConnectorState> Connectors => _connectors;

    internal OcppConnectorState Connector(int id) => _connectors.GetOrAdd(id, i => new OcppConnectorState(i));

    /// <inheritdoc />
    public override string ToString() => $"{Identity} ({Version.DisplayName()}, {Vendor} {Model}){(IsOnline ? "" : " offline")}";
}

/// <summary>A configuration key or device-model variable read from a station.</summary>
/// <param name="Key">Key (1.6) or Component/Variable (2.0.1).</param>
/// <param name="Value">Value, if readable.</param>
/// <param name="ReadOnly">True when the station refuses writes to it.</param>
public readonly record struct OcppConfigurationValue(string Key, string? Value, bool ReadOnly);

/// <summary>Options for <see cref="OcppCsms"/>.</summary>
public sealed class OcppCsmsOptions : IListenerBuilder<OcppCsmsOptions>
{
    /// <summary>Listener factory (<c>UseTcp(IPAddress.Any, 8887)</c> or <c>ListenInMemory</c>).</summary>
    public TransportListenerFactory? ListenerFactory { get; set; }

    /// <summary>Versions accepted, in preference order (default 2.0.1, then 1.6).</summary>
    public IList<OcppVersion> SupportedVersions { get; } = [OcppVersion.Ocpp201, OcppVersion.Ocpp16];

    /// <summary>Heartbeat interval given to booting stations (default 300 s).</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(300);

    /// <summary>Id tags / tokens that may charge; null accepts every tag (default).</summary>
    public ISet<string>? AuthorizedTags { get; set; }

    /// <summary>Checks a station's Basic password (security profile 1/2); null accepts every station.</summary>
    public Func<string, string?, bool>? ValidatePassword { get; set; }

    /// <summary>Server certificate for <c>wss://</c> (TLS over the listener); null serves plain <c>ws://</c>.</summary>
    public X509Certificate2? Certificate { get; set; }

    /// <summary>Refuses commands (remote start/stop, reset, unlock, availability, configuration, limits) — default true.</summary>
    public bool ReadOnly { get; set; } = true;

    /// <summary>How long a CALL waits for its answer (default 30 s).</summary>
    public TimeSpan CallTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>WebSocket ping interval (default 30 s).</summary>
    public TimeSpan KeepAlive { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Endpoint name.</summary>
    public string? Name { get; set; }

    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }

    /// <inheritdoc />
    public OcppCsmsOptions UseListener(TransportListenerFactory factory)
    {
        ListenerFactory = factory;
        return this;
    }

    /// <inheritdoc />
    OcppCsmsOptions IListenerBuilder<OcppCsmsOptions>.UseTransport(TransportFactory factory) =>
        throw new NotSupportedException("A CSMS accepts connections; use UseTcp(address, port) or ListenInMemory(listener).");

    /// <summary>Allows commands.</summary>
    public OcppCsmsOptions AllowCommands()
    {
        ReadOnly = false;
        return this;
    }
}

/// <summary>
/// A small CSMS / central system for OCPP 1.6J and 2.0.1: accepts charge points over WebSocket (ws or wss, Basic auth),
/// answers boot, heartbeat, status, authorization, transactions and meter values, keeps a live model of every station,
/// and sends version-independent commands. Commands are refused until <see cref="OcppCsmsOptions.AllowCommands"/>.
/// </summary>
public sealed class OcppCsms : EndpointBase, IServerEndpoint
{
    private readonly OcppCsmsOptions _options;
    private readonly ConcurrentDictionary<string, OcppStation> _stations = new(StringComparer.Ordinal);
    private ITransportListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _accept;
    private int _nextTransactionId = 1000;
    private int _nextRemoteStartId;
    private int _nextProfileId = 100;

    private OcppCsms(OcppCsmsOptions options) : base("ocpp", options.Logger)
    {
        _options = options;
        Name = options.Name ?? "csms";
    }

    /// <summary>Creates a CSMS.</summary>
    public static OcppCsms Create(Action<OcppCsmsOptions> configure)
    {
        var options = new OcppCsmsOptions();
        configure(options);
        return new OcppCsms(options);
    }

    /// <summary>True when commands are refused.</summary>
    public bool ReadOnly
    {
        get => _options.ReadOnly;
        set => _options.ReadOnly = value;
    }

    /// <summary>Known stations (online and offline), by identity.</summary>
    public IReadOnlyDictionary<string, OcppStation> Stations => _stations;

    /// <summary>A station connected (WebSocket open).</summary>
    public event Action<OcppStation>? StationConnected;

    /// <summary>A station's connection closed.</summary>
    public event Action<OcppStation>? StationDisconnected;

    /// <summary>A station's model changed (boot, status, meter values, transactions).</summary>
    public event Action<OcppStation>? StationChanged;

    /// <summary>A transaction started.</summary>
    public event Action<OcppStation, OcppTransaction>? TransactionStarted;

    /// <summary>A transaction stopped.</summary>
    public event Action<OcppStation, OcppTransaction>? TransactionStopped;

    /// <summary>Every message sent or received, on every connection.</summary>
    public event Action<OcppMessageEvent>? Message;

    /// <inheritdoc />
    public async ValueTask StartAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_listener is not null) return;
        if (_options.ListenerFactory is null) throw new InvalidOperationException("Configure a listener: UseTcp(IPAddress.Any, 8887) or ListenInMemory(listener).");
        SetState(EndpointState.Connecting);
        _listener = _options.ListenerFactory();
        await _listener.StartAsync(ct).ConfigureAwait(false);
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _accept = Task.Run(() => AcceptLoopAsync(_listener, token), CancellationToken.None);
        SetState(EndpointState.Listening);
    }

    /// <summary>Local address of the listener (after start).</summary>
    public string? LocalAddress => _listener?.LocalAddress;

    private async Task AcceptLoopAsync(ITransportListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            ITransport transport;
            try
            {
                transport = await listener.AcceptAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is TransportException or ObjectDisposedException or InvalidOperationException)
            {
                if (ct.IsCancellationRequested) break;
                Logger.LogWarning(ex, "OCPP accept failed");
                continue;
            }

            _ = Task.Run(() => HandshakeAsync(transport, ct), CancellationToken.None);
        }
    }

    private async Task HandshakeAsync(ITransport transport, CancellationToken ct)
    {
        Stream stream = new DuplexPipeStream(transport.Pipe, transport);
        try
        {
            if (_options.Certificate is { } cert)
            {
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = cert, ClientCertificateRequired = false }, ct).ConfigureAwait(false);
                stream = ssl;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var accepted = await OcppWebSocketHandshake.AcceptAsync(stream, transport.Info.RemoteAddress ?? "?", [.. _options.SupportedVersions],
                _options.ValidatePassword is null ? null : r => _options.ValidatePassword(r.Identity, r.Password), _options.KeepAlive, timeout.Token).ConfigureAwait(false);
            if (accepted is null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                return;
            }

            var (socket, request, version) = accepted.Value;
            await AttachAsync(socket, request.Identity, version, request.RemoteAddress, stream).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "OCPP handshake failed");
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Serves a WebSocket accepted elsewhere (for example ASP.NET Core's <c>HttpContext.WebSockets</c>): the subprotocol
    /// must already be negotiated. Completes when the connection closes.
    /// </summary>
    public async Task AcceptWebSocketAsync(WebSocket socket, string identity, OcppVersion version, string remoteAddress = "", CancellationToken ct = default)
    {
        var session = await AttachAsync(socket, identity, version, remoteAddress, null).ConfigureAwait(false);
        await session.Completion.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task<OcppSession> AttachAsync(WebSocket socket, string identity, OcppVersion version, string remote, IAsyncDisposable? owner)
    {
        var station = _stations.GetOrAdd(identity, id => new OcppStation(id));
        if (station.Session is { } old) await old.AbortAsync().ConfigureAwait(false);
        var session = new OcppSession(socket, version, identity, _options.CallTimeout, Logger,
            (d, json, summary) => Tap(d, Encoding.UTF8.GetBytes(json), () => summary), () => !_options.ReadOnly, owner);
        session.Message += e =>
        {
            if (e.Direction == FrameDirection.Inbound) station.LastSeen = DateTimeOffset.UtcNow;
            Message?.Invoke(e);
        };
        if (version == OcppVersion.Ocpp16) Register16(session, station);
        else Register201(session, station);
        station.Version = version;
        station.RemoteAddress = remote;
        station.Booted = false;
        station.Session = session;
        station.LastSeen = DateTimeOffset.UtcNow;
        session.Start();
        _ = session.Completion.ContinueWith(_ =>
        {
            if (!ReferenceEquals(station.Session, session)) return;
            station.Session = null;
            StationDisconnected?.Invoke(station);
            StationChanged?.Invoke(station);
        }, TaskScheduler.Default);
        Logger.LogInformation("OCPP station {Identity} connected ({Version}) from {Remote}", identity, version.DisplayName(), remote);
        StationConnected?.Invoke(station);
        StationChanged?.Invoke(station);
        return session;
    }

    private bool IsAuthorized(string tag) => _options.AuthorizedTags is null || _options.AuthorizedTags.Contains(tag);

    private void Changed(OcppStation station) => StationChanged?.Invoke(station);

    // ---- OCPP 1.6 -----------------------------------------------------------------------------------------------------

    private void Register16(OcppSession s, OcppStation st)
    {
        s.On<V16.BootNotificationRequest, V16.BootNotificationResponse>((r, _) =>
        {
            (st.Vendor, st.Model, st.SerialNumber, st.FirmwareVersion, st.Booted) = (r.ChargePointVendor, r.ChargePointModel, r.ChargePointSerialNumber ?? r.ChargeBoxSerialNumber, r.FirmwareVersion, true);
            Changed(st);
            return ValueTask.FromResult(new V16.BootNotificationResponse { Status = V16.RegistrationStatus.Accepted, CurrentTime = DateTimeOffset.UtcNow, Interval = (int)_options.HeartbeatInterval.TotalSeconds });
        });
        s.On<V16.HeartbeatRequest, V16.HeartbeatResponse>((_, _) =>
        {
            st.LastHeartbeat = DateTimeOffset.UtcNow;
            Changed(st);
            return ValueTask.FromResult(new V16.HeartbeatResponse { CurrentTime = DateTimeOffset.UtcNow });
        });
        s.On<V16.StatusNotificationRequest, V16.StatusNotificationResponse>((r, _) =>
        {
            var c = st.Connector(r.ConnectorId);
            c.Status = r.Status.ToString();
            c.ChargingState = c.Status;
            c.ErrorCode = r.ErrorCode == V16.ChargePointErrorCode.NoError ? null : r.ErrorCode.ToString();
            c.Updated = DateTimeOffset.UtcNow;
            Changed(st);
            return ValueTask.FromResult(new V16.StatusNotificationResponse());
        });
        s.On<V16.AuthorizeRequest, V16.AuthorizeResponse>((r, _) => ValueTask.FromResult(new V16.AuthorizeResponse
        {
            IdTagInfo = new V16.IdTagInfo { Status = IsAuthorized(r.IdTag) ? V16.AuthorizationStatus.Accepted : V16.AuthorizationStatus.Invalid },
        }));
        s.On<V16.StartTransactionRequest, V16.StartTransactionResponse>((r, _) =>
        {
            var ok = IsAuthorized(r.IdTag);
            var id = Interlocked.Increment(ref _nextTransactionId);
            var tx = new OcppTransaction(id.ToString(CultureInfo.InvariantCulture), r.ConnectorId, r.IdTag, r.Timestamp, r.MeterStart);
            var c = st.Connector(r.ConnectorId);
            c.Transaction = tx;
            c.EnergyWh = r.MeterStart;
            c.Updated = DateTimeOffset.UtcNow;
            TransactionStarted?.Invoke(st, tx);
            Changed(st);
            return ValueTask.FromResult(new V16.StartTransactionResponse
            {
                TransactionId = id,
                IdTagInfo = new V16.IdTagInfo { Status = ok ? V16.AuthorizationStatus.Accepted : V16.AuthorizationStatus.Invalid },
            });
        });
        s.On<V16.StopTransactionRequest, V16.StopTransactionResponse>((r, _) =>
        {
            var id = r.TransactionId.ToString(CultureInfo.InvariantCulture);
            var c = st.Connectors.Values.FirstOrDefault(x => x.Transaction?.Id == id);
            if (c?.Transaction is { } tx)
            {
                tx.MeterWh = r.MeterStop;
                tx.StoppedAt = r.Timestamp;
                tx.StopReason = (r.Reason ?? V16.StopReason.Local).ToString();
                c.Transaction = null;
                c.EnergyWh = r.MeterStop;
                c.PowerW = 0;
                c.LimitW = null;
                c.Updated = DateTimeOffset.UtcNow;
                TransactionStopped?.Invoke(st, tx);
                Changed(st);
            }

            return ValueTask.FromResult(new V16.StopTransactionResponse
            {
                IdTagInfo = r.IdTag is null ? null : new V16.IdTagInfo { Status = IsAuthorized(r.IdTag) ? V16.AuthorizationStatus.Accepted : V16.AuthorizationStatus.Invalid },
            });
        });
        s.On<V16.MeterValuesRequest, V16.MeterValuesResponse>((r, _) =>
        {
            var c = st.Connector(r.ConnectorId);
            foreach (var v in r.MeterValue.SelectMany(m => m.SampledValue))
            {
                if (!double.TryParse(v.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) continue;
                Apply(c, v.Measurand, v.Unit, value);
            }

            c.Updated = DateTimeOffset.UtcNow;
            Changed(st);
            return ValueTask.FromResult(new V16.MeterValuesResponse());
        });
        s.On<V16.DataTransferRequest, V16.DataTransferResponse>((_, _) => ValueTask.FromResult(new V16.DataTransferResponse { Status = V16.DataTransferStatus.UnknownVendorId }));
    }

    private static void Apply(OcppConnectorState c, string? measurand, string? unit, double value)
    {
        switch (measurand ?? OcppMeasurand.EnergyActiveImportRegister)
        {
            case OcppMeasurand.EnergyActiveImportRegister:
                var wh = unit == "kWh" ? value * 1000 : value;
                c.EnergyWh = wh;
                if (c.Transaction is { } tx) tx.MeterWh = wh;
                break;
            case OcppMeasurand.PowerActiveImport:
                c.PowerW = unit == "kW" ? value * 1000 : value;
                break;
            case OcppMeasurand.SoC:
                c.SoC = value;
                break;
        }
    }

    // ---- OCPP 2.0.1 ---------------------------------------------------------------------------------------------------

    private void Register201(OcppSession s, OcppStation st)
    {
        s.On<V201.BootNotificationRequest, V201.BootNotificationResponse>((r, _) =>
        {
            (st.Vendor, st.Model, st.SerialNumber, st.FirmwareVersion, st.Booted) = (r.ChargingStation.VendorName, r.ChargingStation.Model, r.ChargingStation.SerialNumber, r.ChargingStation.FirmwareVersion, true);
            Changed(st);
            return ValueTask.FromResult(new V201.BootNotificationResponse { Status = V201.RegistrationStatus.Accepted, CurrentTime = DateTimeOffset.UtcNow, Interval = (int)_options.HeartbeatInterval.TotalSeconds });
        });
        s.On<V201.HeartbeatRequest, V201.HeartbeatResponse>((_, _) =>
        {
            st.LastHeartbeat = DateTimeOffset.UtcNow;
            Changed(st);
            return ValueTask.FromResult(new V201.HeartbeatResponse { CurrentTime = DateTimeOffset.UtcNow });
        });
        s.On<V201.StatusNotificationRequest, V201.StatusNotificationResponse>((r, _) =>
        {
            var c = st.Connector(r.EvseId);
            c.Status = r.ConnectorStatus.ToString();
            if (c.Transaction is null) c.ChargingState = c.Status;
            c.Updated = DateTimeOffset.UtcNow;
            Changed(st);
            return ValueTask.FromResult(new V201.StatusNotificationResponse());
        });
        s.On<V201.AuthorizeRequest, V201.AuthorizeResponse>((r, _) => ValueTask.FromResult(new V201.AuthorizeResponse
        {
            IdTokenInfo = new V201.IdTokenInfo { Status = IsAuthorized(r.IdToken.Value) ? V201.AuthorizationStatus.Accepted : V201.AuthorizationStatus.Invalid },
        }));
        s.On<V201.TransactionEventRequest, V201.TransactionEventResponse>((r, _) =>
        {
            var evse = r.Evse?.Id ?? st.Connectors.Values.FirstOrDefault(x => x.Transaction?.Id == r.TransactionInfo.TransactionId)?.Id ?? 1;
            var c = st.Connector(evse);
            if (c.Transaction is null || c.Transaction.Id != r.TransactionInfo.TransactionId)
            {
                var start = r.MeterValue?.SelectMany(m => m.SampledValue).FirstOrDefault(v => (v.Measurand ?? OcppMeasurand.EnergyActiveImportRegister) == OcppMeasurand.EnergyActiveImportRegister);
                var startWh = start is null ? c.EnergyWh ?? 0 : (double)start.Value * (start.UnitOfMeasure?.Unit == "kWh" ? 1000 : 1);
                c.Transaction = new OcppTransaction(r.TransactionInfo.TransactionId, evse, r.IdToken?.Value ?? "", r.Timestamp, startWh);
                TransactionStarted?.Invoke(st, c.Transaction);
            }

            foreach (var v in r.MeterValue?.SelectMany(m => m.SampledValue) ?? [])
            {
                var unit = v.UnitOfMeasure?.Unit;
                var value = (double)v.Value * Math.Pow(10, v.UnitOfMeasure?.Multiplier ?? 0);
                Apply(c, v.Measurand, unit, value);
            }

            if (r.TransactionInfo.ChargingState is { } cs) c.ChargingState = cs.ToString();
            if (r.EventType == V201.TransactionEventType.Ended && c.Transaction is { } tx)
            {
                tx.StoppedAt = r.Timestamp;
                tx.StopReason = (r.TransactionInfo.StoppedReason ?? V201.Reason.Local).ToString();
                c.Transaction = null;
                c.PowerW = 0;
                c.LimitW = null;
                c.ChargingState = c.Status;
                TransactionStopped?.Invoke(st, tx);
            }

            c.Updated = DateTimeOffset.UtcNow;
            Changed(st);
            return ValueTask.FromResult(new V201.TransactionEventResponse
            {
                IdTokenInfo = r.IdToken is null ? null : new V201.IdTokenInfo { Status = IsAuthorized(r.IdToken.Value) ? V201.AuthorizationStatus.Accepted : V201.AuthorizationStatus.Invalid },
            });
        });
        s.On<V201.MeterValuesRequest, V201.MeterValuesResponse>((r, _) =>
        {
            var c = st.Connector(r.EvseId);
            foreach (var v in r.MeterValue.SelectMany(m => m.SampledValue)) Apply(c, v.Measurand, v.UnitOfMeasure?.Unit, (double)v.Value);
            c.Updated = DateTimeOffset.UtcNow;
            Changed(st);
            return ValueTask.FromResult(new V201.MeterValuesResponse());
        });
        s.On<V201.DataTransferRequest, V201.DataTransferResponse>((_, _) => ValueTask.FromResult(new V201.DataTransferResponse { Status = V201.DataTransferStatus.UnknownVendorId }));
    }

    // ---- version-independent operations -------------------------------------------------------------------------------

    /// <summary>The live session of a station.</summary>
    /// <exception cref="InvalidOperationException">The station is unknown or offline.</exception>
    public OcppSession Session(string identity) =>
        _stations.TryGetValue(identity, out var st) && st.Session is { IsOpen: true } s ? s : throw new InvalidOperationException($"Station '{identity}' is not connected.");

    private void EnsureCommands(string what)
    {
        if (_options.ReadOnly) throw new ReadOnlyModeException($"{what} changes a charger's state; the CSMS is read-only until AllowCommands().");
    }

    /// <summary>Starts a transaction remotely (RemoteStartTransaction / RequestStartTransaction). Returns true when accepted.</summary>
    public async Task<bool> RemoteStartAsync(string identity, int connectorId, string idTag, CancellationToken ct = default)
    {
        EnsureCommands("Remote start");
        var s = Session(identity);
        if (s.Version == OcppVersion.Ocpp16)
            return (await s.CallAsync(new V16.RemoteStartTransactionRequest { ConnectorId = connectorId, IdTag = idTag }, ct).ConfigureAwait(false)).Status == OcppGenericStatus.Accepted;
        var r = await s.CallAsync(new V201.RequestStartTransactionRequest
        {
            EvseId = connectorId,
            RemoteStartId = Interlocked.Increment(ref _nextRemoteStartId),
            IdToken = new V201.IdToken { Value = idTag, Type = V201.IdTokenType.Central },
        }, ct).ConfigureAwait(false);
        return r.Status == V201.RequestStartStopStatus.Accepted;
    }

    /// <summary>Stops a transaction remotely. Returns true when accepted.</summary>
    public async Task<bool> RemoteStopAsync(string identity, string transactionId, CancellationToken ct = default)
    {
        EnsureCommands("Remote stop");
        var s = Session(identity);
        if (s.Version == OcppVersion.Ocpp16)
            return (await s.CallAsync(new V16.RemoteStopTransactionRequest { TransactionId = int.Parse(transactionId, CultureInfo.InvariantCulture) }, ct).ConfigureAwait(false)).Status == OcppGenericStatus.Accepted;
        return (await s.CallAsync(new V201.RequestStopTransactionRequest { TransactionId = transactionId }, ct).ConfigureAwait(false)).Status == V201.RequestStartStopStatus.Accepted;
    }

    /// <summary>Resets a station (hard = Hard/Immediate, soft = Soft/OnIdle). Returns the station's status.</summary>
    public async Task<string> ResetAsync(string identity, bool hard, CancellationToken ct = default)
    {
        EnsureCommands("Reset");
        var s = Session(identity);
        if (s.Version == OcppVersion.Ocpp16)
            return (await s.CallAsync(new V16.ResetRequest { Type = hard ? V16.ResetType.Hard : V16.ResetType.Soft }, ct).ConfigureAwait(false)).Status.ToString();
        return (await s.CallAsync(new V201.ResetRequest { Type = hard ? V201.ResetType.Immediate : V201.ResetType.OnIdle }, ct).ConfigureAwait(false)).Status.ToString();
    }

    /// <summary>Unlocks a connector. Returns the station's status.</summary>
    public async Task<string> UnlockAsync(string identity, int connectorId, CancellationToken ct = default)
    {
        EnsureCommands("Unlock");
        var s = Session(identity);
        if (s.Version == OcppVersion.Ocpp16)
            return (await s.CallAsync(new V16.UnlockConnectorRequest { ConnectorId = connectorId }, ct).ConfigureAwait(false)).Status.ToString();
        return (await s.CallAsync(new V201.UnlockConnectorRequest { EvseId = connectorId, ConnectorId = 1 }, ct).ConfigureAwait(false)).Status.ToString();
    }

    /// <summary>Makes a connector (0 = whole station) operative or inoperative. Returns the station's status.</summary>
    public async Task<string> SetAvailabilityAsync(string identity, int connectorId, bool operative, CancellationToken ct = default)
    {
        EnsureCommands("Change availability");
        var s = Session(identity);
        if (s.Version == OcppVersion.Ocpp16)
            return (await s.CallAsync(new V16.ChangeAvailabilityRequest { ConnectorId = connectorId, Type = operative ? V16.AvailabilityType.Operative : V16.AvailabilityType.Inoperative }, ct).ConfigureAwait(false)).Status.ToString();
        return (await s.CallAsync(new V201.ChangeAvailabilityRequest
        {
            Evse = connectorId == 0 ? null : new V201.Evse { Id = connectorId },
            OperationalStatus = operative ? V201.OperationalStatus.Operative : V201.OperationalStatus.Inoperative,
        }, ct).ConfigureAwait(false)).Status.ToString();
    }

    /// <summary>
    /// Limits the charging power of a connector with a TxDefaultProfile (watts), or clears the limit (null).
    /// Returns the station's status.
    /// </summary>
    public async Task<string> SetPowerLimitAsync(string identity, int connectorId, double? watts, CancellationToken ct = default)
    {
        EnsureCommands("Charging limit");
        var s = Session(identity);
        string status;
        if (watts is null)
        {
            status = s.Version == OcppVersion.Ocpp16
                ? (await s.CallAsync(new V16.ClearChargingProfileRequest { ConnectorId = connectorId, ChargingProfilePurpose = V16.ChargingProfilePurpose.TxDefaultProfile }, ct).ConfigureAwait(false)).Status.ToString()
                : (await s.CallAsync(new V201.ClearChargingProfileRequest { ChargingProfileCriteria = new V201.ClearChargingProfileCriterion { EvseId = connectorId, ChargingProfilePurpose = V201.ChargingProfilePurpose.TxDefaultProfile } }, ct).ConfigureAwait(false)).Status.ToString();
        }
        else
        {
            var id = Interlocked.Increment(ref _nextProfileId);
            var limit = (decimal)Math.Round(watts.Value);
            status = s.Version == OcppVersion.Ocpp16
                ? (await s.CallAsync(new V16.SetChargingProfileRequest
                {
                    ConnectorId = connectorId,
                    CsChargingProfiles = new V16.ChargingProfile
                    {
                        ChargingProfileId = id, StackLevel = 0, ChargingProfilePurpose = V16.ChargingProfilePurpose.TxDefaultProfile, ChargingProfileKind = V16.ChargingProfileKind.Relative,
                        ChargingSchedule = new V16.ChargingSchedule { ChargingRateUnit = V16.ChargingRateUnit.W, ChargingSchedulePeriod = [new V16.ChargingSchedulePeriod { StartPeriod = 0, Limit = limit }] },
                    },
                }, ct).ConfigureAwait(false)).Status.ToString()
                : (await s.CallAsync(new V201.SetChargingProfileRequest
                {
                    EvseId = connectorId,
                    ChargingProfile = new V201.ChargingProfile
                    {
                        Id = id, StackLevel = 0, ChargingProfilePurpose = V201.ChargingProfilePurpose.TxDefaultProfile, ChargingProfileKind = V201.ChargingProfileKind.Relative,
                        ChargingSchedule = [new V201.ChargingSchedule { Id = id, ChargingRateUnit = V201.ChargingRateUnit.W, ChargingSchedulePeriod = [new V201.ChargingSchedulePeriod { StartPeriod = 0, Limit = limit }] }],
                    },
                }, ct).ConfigureAwait(false)).Status.ToString();
        }

        if (status == "Accepted" && _stations.TryGetValue(identity, out var st))
        {
            st.Connector(connectorId).LimitW = watts;
            Changed(st);
        }

        return status;
    }

    /// <summary>Variables read from a 2.0.1 station by <see cref="ReadConfigurationAsync"/>.</summary>
    public static IReadOnlyList<(string Component, string Variable)> StandardVariables { get; } =
    [
        ("OCPPCommCtrlr", "HeartbeatInterval"), ("SampledDataCtrlr", "TxUpdatedInterval"), ("SampledDataCtrlr", "TxUpdatedMeasurands"),
        ("AuthCtrlr", "AuthorizeRemoteStart"), ("TxCtrlr", "EVConnectionTimeOut"), ("ChargingStation", "AvailabilityState"),
        ("SmartChargingCtrlr", "Enabled"), ("DeviceDataCtrlr", "ItemsPerMessage"),
    ];

    /// <summary>Reads a station's configuration (GetConfiguration in 1.6; GetVariables of <see cref="StandardVariables"/> in 2.0.1). Allowed in read-only mode.</summary>
    public async Task<IReadOnlyList<OcppConfigurationValue>> ReadConfigurationAsync(string identity, IEnumerable<string>? keys = null, CancellationToken ct = default)
    {
        var s = Session(identity);
        if (s.Version == OcppVersion.Ocpp16)
        {
            var r = await s.CallAsync(new V16.GetConfigurationRequest { Key = keys?.ToList() }, ct).ConfigureAwait(false);
            return [.. (r.ConfigurationKey ?? []).Select(k => new OcppConfigurationValue(k.Key, k.Value, k.Readonly))];
        }

        var wanted = keys?.Select(k => k.Split('/', 2)).Select(p => p.Length == 2 ? (p[0], p[1]) : ("ChargingStation", p[0])).ToList() ?? [.. StandardVariables];
        var res = await s.CallAsync(new V201.GetVariablesRequest
        {
            GetVariableData = [.. wanted.Select(w => new V201.GetVariableData { Component = new V201.Component { Name = w.Item1 }, Variable = new V201.Variable { Name = w.Item2 } })],
        }, ct).ConfigureAwait(false);
        return [.. res.GetVariableResult.Where(v => v.AttributeStatus == V201.GetVariableStatus.Accepted)
            .Select(v => new OcppConfigurationValue($"{v.Component.Name}/{v.Variable.Name}", v.AttributeValue, false))];
    }

    /// <summary>Changes one configuration key (1.6 ChangeConfiguration; 2.0.1 SetVariables with <c>Component/Variable</c>). Returns the status.</summary>
    public async Task<string> WriteConfigurationAsync(string identity, string key, string value, CancellationToken ct = default)
    {
        EnsureCommands("Configuration change");
        var s = Session(identity);
        if (s.Version == OcppVersion.Ocpp16)
            return (await s.CallAsync(new V16.ChangeConfigurationRequest { Key = key, Value = value }, ct).ConfigureAwait(false)).Status.ToString();
        var p = key.Split('/', 2);
        var r = await s.CallAsync(new V201.SetVariablesRequest
        {
            SetVariableData = [new V201.SetVariableData { Component = new V201.Component { Name = p.Length == 2 ? p[0] : "ChargingStation" }, Variable = new V201.Variable { Name = p[^1] }, AttributeValue = value }],
        }, ct).ConfigureAwait(false);
        return r.SetVariableResult.FirstOrDefault()?.AttributeStatus.ToString() ?? "Rejected";
    }

    /// <summary>Asks a station to send a message now (BootNotification, Heartbeat, StatusNotification, MeterValues). Allowed in read-only mode.</summary>
    public async Task<string> TriggerAsync(string identity, string message, int? connectorId = null, CancellationToken ct = default)
    {
        var s = Session(identity);
        if (s.Version == OcppVersion.Ocpp16)
            return (await s.CallAsync(new V16.TriggerMessageRequest { RequestedMessage = Enum.Parse<V16.MessageTrigger>(message), ConnectorId = connectorId }, ct).ConfigureAwait(false)).Status.ToString();
        return (await s.CallAsync(new V201.TriggerMessageRequest
        {
            RequestedMessage = Enum.Parse<V201.MessageTrigger>(message),
            Evse = connectorId is null ? null : new V201.Evse { Id = connectorId.Value },
        }, ct).ConfigureAwait(false)).Status.ToString();
    }

    /// <inheritdoc />
    public async ValueTask StopAsync(CancellationToken ct = default)
    {
        if (_listener is null) return;
        SetState(EndpointState.Stopping);
        await (_cts?.CancelAsync() ?? Task.CompletedTask).ConfigureAwait(false);
        await _listener.DisposeAsync().ConfigureAwait(false);
        if (_accept is not null) await _accept.ConfigureAwait(false);
        foreach (var st in _stations.Values)
        {
            if (st.Session is { } s) await s.CloseAsync("CSMS stopping", ct).ConfigureAwait(false);
        }

        _listener = null;
        _cts?.Dispose();
        _cts = null;
        SetState(EndpointState.Disconnected);
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore() => await StopAsync().ConfigureAwait(false);
}
