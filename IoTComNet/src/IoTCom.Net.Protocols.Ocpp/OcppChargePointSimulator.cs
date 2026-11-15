using System.Globalization;
using Microsoft.Extensions.Logging;
using V16 = IoTCom.Net.Protocols.Ocpp.V16;
using V201 = IoTCom.Net.Protocols.Ocpp.V201;

namespace IoTCom.Net.Protocols.Ocpp;

/// <summary>Options for <see cref="OcppChargePointSimulator"/>.</summary>
public sealed class OcppChargePointSimulatorOptions
{
    /// <summary>Identity (default <c>SPKLU-KM57-01</c>).</summary>
    public string Identity { get; set; } = "SPKLU-KM57-01";

    /// <summary>OCPP version (default 1.6J).</summary>
    public OcppVersion Version { get; set; } = OcppVersion.Ocpp16;

    /// <summary>Number of connectors / EVSEs (default 2).</summary>
    public int Connectors { get; set; } = 2;

    /// <summary>Maximum power per connector, W (default 22 kW AC).</summary>
    public double MaxPowerW { get; set; } = 22_000;

    /// <summary>Vendor name.</summary>
    public string Vendor { get; set; } = "Gravicode";

    /// <summary>Model name.</summary>
    public string Model { get; set; } = "IoTCom EVSE-22";

    /// <summary>Serial number.</summary>
    public string SerialNumber { get; set; } = "GVC-EVSE-000417";

    /// <summary>Firmware version.</summary>
    public string FirmwareVersion { get; set; } = "1.4.2";

    /// <summary>Sample interval for meter values during a transaction (default 60 s of simulated time).</summary>
    public TimeSpan MeterValueInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Runs the physics on a background timer (one step per second) instead of explicit <see cref="OcppChargePointSimulator.StepAsync"/> calls.</summary>
    public bool AutoRun { get; set; }

    /// <summary>Simulated seconds per real second when <see cref="AutoRun"/> is on (default 1).</summary>
    public double TimeScale { get; set; } = 1;

    /// <summary>Reconnect after a lost connection (default true).</summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>Delay before a reconnect attempt (default 2 s).</summary>
    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Configures the underlying <see cref="OcppChargePoint"/> (URL or transport, password, timeouts).</summary>
    public Action<OcppChargePointOptions>? ChargePoint { get; set; }

    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }

    /// <summary>Connects to a CSMS URL.</summary>
    public OcppChargePointSimulatorOptions UseCsms(string url)
    {
        var prior = ChargePoint;
        ChargePoint = o => { prior?.Invoke(o); o.UseCsms(url); };
        return this;
    }

    /// <summary>Connects through an IoTCom transport (in-memory or TCP).</summary>
    public OcppChargePointSimulatorOptions UseTransport(TransportFactory factory)
    {
        var prior = ChargePoint;
        ChargePoint = o => { prior?.Invoke(o); o.UseTransport(factory); };
        return this;
    }
}

/// <summary>One simulated connector / EVSE.</summary>
public sealed class OcppSimulatedConnector
{
    internal OcppSimulatedConnector(int id, double meterWh) => (Id, MeterWh) = (id, meterWh);

    /// <summary>Connector (1.6) / EVSE (2.0.1) id.</summary>
    public int Id { get; }

    /// <summary>Status in the version's vocabulary (Available, Preparing, Charging, SuspendedEV, Finishing, Occupied, Unavailable, Faulted).</summary>
    public string Status { get; internal set; } = "Available";

    /// <summary>True while an EV is plugged in.</summary>
    public bool EvPlugged { get; internal set; }

    /// <summary>EV state of charge, percent.</summary>
    public double SoC { get; internal set; }

    /// <summary>EV battery capacity, Wh.</summary>
    public double BatteryWh { get; internal set; }

    /// <summary>EV acceptance limit, W.</summary>
    public double EvMaxPowerW { get; internal set; }

    /// <summary>Energy register, Wh (never resets).</summary>
    public double MeterWh { get; internal set; }

    /// <summary>Power being delivered, W.</summary>
    public double PowerW { get; internal set; }

    /// <summary>Limit from the CSMS's charging profile, W.</summary>
    public double? LimitW { get; internal set; }

    /// <summary>Running transaction id.</summary>
    public string? TransactionId { get; internal set; }

    /// <summary>Id tag of the running or pending transaction.</summary>
    public string? IdTag { get; internal set; }

    /// <summary>Energy register at transaction start, Wh.</summary>
    public double TransactionStartWh { get; internal set; }

    /// <summary>False after ChangeAvailability(Inoperative).</summary>
    public bool Operative { get; internal set; } = true;

    /// <summary>True while a fault is injected.</summary>
    public bool Faulted { get; internal set; }

    internal bool PendingInoperative;
    internal string? PendingRemoteTag;
    internal int? PendingRemoteStartId;
    internal int SeqNo;
    internal double SinceMeter;
}

/// <summary>
/// A complete simulated charge point for OCPP 1.6J or 2.0.1: boots, sends heartbeats and status, authorizes cards,
/// runs transactions with an EV model (state of charge, taper above 80 %, charging limits from the CSMS), reports
/// meter values, and answers remote start/stop, reset, unlock, availability, configuration and trigger requests.
/// </summary>
public sealed class OcppChargePointSimulator : IAsyncDisposable
{
    private readonly OcppChargePointSimulatorOptions _options;
    private readonly List<OcppSimulatedConnector> _connectors = [];
    private readonly Dictionary<string, (string Value, bool ReadOnly)> _config = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private double _sinceHeartbeat;
    private int _heartbeatSeconds = 300;
    private Task? _loop;
    private bool _stopping;
    private int _nextTx = 1;

    private OcppChargePointSimulator(OcppChargePointSimulatorOptions options)
    {
        _options = options;
        for (var i = 1; i <= options.Connectors; i++) _connectors.Add(new OcppSimulatedConnector(i, 1_250_000 + (i * 87_413)));
        ChargePoint = OcppChargePoint.Create(o =>
        {
            o.Identity = options.Identity;
            o.Version = options.Version;
            o.Logger = options.Logger;
            options.ChargePoint?.Invoke(o);
        });
        InitConfiguration();
        if (options.Version == OcppVersion.Ocpp16) Register16();
        else Register201();
        ChargePoint.ConnectionLost += OnConnectionLost;
    }

    /// <summary>Creates a simulator.</summary>
    public static OcppChargePointSimulator Create(Action<OcppChargePointSimulatorOptions> configure)
    {
        var o = new OcppChargePointSimulatorOptions();
        configure(o);
        return new OcppChargePointSimulator(o);
    }

    /// <summary>The charge point endpoint (traffic tap, raw calls).</summary>
    public OcppChargePoint ChargePoint { get; }

    /// <summary>Identity.</summary>
    public string Identity => _options.Identity;

    /// <summary>Version.</summary>
    public OcppVersion Version => _options.Version;

    /// <summary>Maximum power per connector, W.</summary>
    public double MaxPowerW => _options.MaxPowerW;

    /// <summary>Connectors / EVSEs.</summary>
    public IReadOnlyList<OcppSimulatedConnector> Connectors => _connectors;

    /// <summary>Configuration keys (1.6) or Component/Variable names (2.0.1) with their values.</summary>
    public IReadOnlyDictionary<string, (string Value, bool ReadOnly)> Configuration => _config;

    /// <summary>True after an accepted BootNotification.</summary>
    public bool Registered { get; private set; }

    /// <summary>Raised after any state change (status, power, transaction).</summary>
    public event Action? Changed;

    /// <summary>Raised when a reset from the CSMS has been carried out.</summary>
    public event Action<string>? Reset;

    private void Notify() => Changed?.Invoke();

    private OcppSimulatedConnector Connector(int id) => _connectors.FirstOrDefault(c => c.Id == id) ?? throw new ArgumentOutOfRangeException(nameof(id), $"No connector {id}.");

    // ---- lifecycle ----------------------------------------------------------------------------------------------------

    /// <summary>Connects, boots and reports every connector's status.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        await ChargePoint.ConnectAsync(ct).ConfigureAwait(false);
        await BootAsync(_options.Version == OcppVersion.Ocpp16 ? null : V201.BootReason.PowerUp, ct).ConfigureAwait(false);
        var runToken = _cts.Token;   // read now: a concurrent teardown may null _cts before the task runs
        if (_options.AutoRun && _loop is null) _loop = Task.Run(() => RunAsync(runToken), CancellationToken.None);
    }

    private async Task BootAsync(V201.BootReason? reason, CancellationToken ct)
    {
        if (_options.Version == OcppVersion.Ocpp16)
        {
            var r = await ChargePoint.CallAsync(new V16.BootNotificationRequest
            {
                ChargePointVendor = _options.Vendor, ChargePointModel = _options.Model, ChargePointSerialNumber = _options.SerialNumber,
                FirmwareVersion = _options.FirmwareVersion, MeterType = "IoTCom DIN-rail meter",
            }, ct).ConfigureAwait(false);
            Registered = r.Status == V16.RegistrationStatus.Accepted;
            if (r.Interval > 0) SetHeartbeat(r.Interval);
        }
        else
        {
            var r = await ChargePoint.CallAsync(new V201.BootNotificationRequest
            {
                Reason = reason ?? V201.BootReason.PowerUp,
                ChargingStation = new V201.ChargingStation { VendorName = _options.Vendor, Model = _options.Model, SerialNumber = _options.SerialNumber, FirmwareVersion = _options.FirmwareVersion },
            }, ct).ConfigureAwait(false);
            Registered = r.Status == V201.RegistrationStatus.Accepted;
            if (r.Interval > 0) SetHeartbeat(r.Interval);
        }

        if (!Registered) return;
        if (_options.Version == OcppVersion.Ocpp16) await SendStatusAsync(0, ct).ConfigureAwait(false);
        foreach (var c in _connectors) await SendStatusAsync(c.Id, ct).ConfigureAwait(false);
        Notify();
    }

    private void SetHeartbeat(int seconds)
    {
        _heartbeatSeconds = Math.Max(1, seconds);
        var key = _options.Version == OcppVersion.Ocpp16 ? "HeartbeatInterval" : "OCPPCommCtrlr/HeartbeatInterval";
        _config[key] = (_heartbeatSeconds.ToString(CultureInfo.InvariantCulture), false);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var last = DateTime.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
                var now = DateTime.UtcNow;
                var dt = (now - last).TotalSeconds * _options.TimeScale;
                last = now;
                if (ChargePoint.Session is { IsOpen: true }) await StepAsync(TimeSpan.FromSeconds(dt), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IoTComException ex)
            {
                _options.Logger?.LogDebug(ex, "OCPP simulator step failed");
            }
        }
    }

    private void OnConnectionLost()
    {
        Registered = false;
        Notify();
        if (!_options.AutoReconnect || _stopping) return;
        _ = Task.Run(async () =>
        {
            while (!_stopping && !_cts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_options.ReconnectDelay, _cts.Token).ConfigureAwait(false);
                    await ChargePoint.ConnectAsync(_cts.Token).ConfigureAwait(false);
                    await BootAsync(_options.Version == OcppVersion.Ocpp16 ? null : V201.BootReason.Unknown, _cts.Token).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex) when (ex is IoTComException or IOException)
                {
                    _options.Logger?.LogDebug(ex, "OCPP simulator reconnect failed");
                }
            }
        });
    }

    /// <summary>Drops the connection without a close handshake (a lost cellular link); it reconnects when <see cref="OcppChargePointSimulatorOptions.AutoReconnect"/> is on.</summary>
    public ValueTask DropConnectionAsync() => ChargePoint.AbortAsync();

    /// <summary>Stops the simulator and closes the connection.</summary>
    public async ValueTask DisposeAsync()
    {
        _stopping = true;
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null) await _loop.ConfigureAwait(false);
        await ChargePoint.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
        _gate.Dispose();
    }

    // ---- what a driver does -------------------------------------------------------------------------------------------

    /// <summary>Plugs an EV in. A pending remote start begins charging right away.</summary>
    public async Task PlugInAsync(int connectorId, double socPercent = 20, double batteryKWh = 60, double evMaxKW = 11, CancellationToken ct = default)
    {
        var c = Connector(connectorId);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (c.EvPlugged) return;
            (c.EvPlugged, c.SoC, c.BatteryWh, c.EvMaxPowerW) = (true, socPercent, batteryKWh * 1000, evMaxKW * 1000);
            if (!c.Faulted && c.Operative) c.Status = _options.Version == OcppVersion.Ocpp16 ? "Preparing" : "Occupied";
            await SendStatusAsync(c.Id, ct).ConfigureAwait(false);
            if (c.PendingRemoteTag is { } tag) await StartTransactionAsync(c, tag, true, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        Notify();
    }

    /// <summary>A driver presents a card: Authorize, then start (or remember the tag until an EV is plugged in). Returns false when the CSMS refuses the tag.</summary>
    public async Task<bool> PresentCardAsync(int connectorId, string idTag, CancellationToken ct = default)
    {
        var c = Connector(connectorId);
        bool accepted;
        if (_options.Version == OcppVersion.Ocpp16)
            accepted = (await ChargePoint.CallAsync(new V16.AuthorizeRequest { IdTag = idTag }, ct).ConfigureAwait(false)).IdTagInfo.Status == V16.AuthorizationStatus.Accepted;
        else
            accepted = (await ChargePoint.CallAsync(new V201.AuthorizeRequest { IdToken = new V201.IdToken { Value = idTag, Type = V201.IdTokenType.ISO14443 } }, ct).ConfigureAwait(false)).IdTokenInfo.Status == V201.AuthorizationStatus.Accepted;
        if (!accepted) return false;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (c.TransactionId is not null) return true;
            if (c.EvPlugged) await StartTransactionAsync(c, idTag, false, ct).ConfigureAwait(false);
            else c.PendingRemoteTag = idTag;
        }
        finally
        {
            _gate.Release();
        }

        Notify();
        return true;
    }

    /// <summary>Stops charging from the charger (driver pressed stop / presented the card again).</summary>
    public Task StopLocallyAsync(int connectorId, CancellationToken ct = default) => StopAsync(Connector(connectorId), "Local", ct);

    /// <summary>Unplugs the EV; a running transaction ends with EVDisconnected.</summary>
    public async Task UnplugAsync(int connectorId, CancellationToken ct = default)
    {
        var c = Connector(connectorId);
        if (c.TransactionId is not null) await StopAsync(c, "EVDisconnected", ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            c.EvPlugged = false;
            c.PendingRemoteTag = null;
            c.PowerW = 0;
            if (!c.Faulted) c.Status = c.Operative ? "Available" : "Unavailable";
            await SendStatusAsync(c.Id, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        Notify();
    }

    /// <summary>Injects (or clears) a ground fault on a connector; a running transaction stops.</summary>
    public async Task SetFaultAsync(int connectorId, bool faulted, CancellationToken ct = default)
    {
        var c = Connector(connectorId);
        if (faulted && c.TransactionId is not null) await StopAsync(c, _options.Version == OcppVersion.Ocpp16 ? "Other" : "GroundFault", ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            c.Faulted = faulted;
            c.PowerW = 0;
            c.Status = faulted ? "Faulted" : !c.Operative ? "Unavailable" : c.EvPlugged ? (_options.Version == OcppVersion.Ocpp16 ? "Preparing" : "Occupied") : "Available";
            await SendStatusAsync(c.Id, ct, faulted ? "GroundFailure" : null).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        Notify();
    }

    // ---- physics ------------------------------------------------------------------------------------------------------

    /// <summary>Advances the simulation: energy, state of charge, meter values and heartbeats.</summary>
    public async Task StepAsync(TimeSpan dt, CancellationToken ct = default)
    {
        var seconds = dt.TotalSeconds;
        if (seconds <= 0) return;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var c in _connectors.Where(c => c.TransactionId is not null))
            {
                var wasCharging = c.PowerW > 0;
                var before = c.Status;
                var power = TargetPower(c);
                c.PowerW = power;
                var wh = power * seconds / 3600;
                c.MeterWh += wh;
                c.SoC = Math.Min(100, c.SoC + (wh / c.BatteryWh * 100));
                c.Status = _options.Version == OcppVersion.Ocpp16 ? (power > 0 ? "Charging" : "SuspendedEV") : "Occupied";
                c.SinceMeter += seconds;
                if (_options.Version == OcppVersion.Ocpp16 && before != c.Status) await SendStatusAsync(c.Id, ct).ConfigureAwait(false);
                if (_options.Version == OcppVersion.Ocpp201 && wasCharging != power > 0)
                {
                    c.SinceMeter = 0;
                    await SendMeterValuesAsync(c, ct, reason: V201.TriggerReason.ChargingStateChanged).ConfigureAwait(false);
                }

                if (c.SinceMeter >= _options.MeterValueInterval.TotalSeconds)
                {
                    c.SinceMeter = 0;
                    await SendMeterValuesAsync(c, ct).ConfigureAwait(false);
                }
            }

            _sinceHeartbeat += seconds;
            if (_sinceHeartbeat >= _heartbeatSeconds)
            {
                _sinceHeartbeat = 0;
                if (_options.Version == OcppVersion.Ocpp16) await ChargePoint.CallAsync(new V16.HeartbeatRequest(), ct).ConfigureAwait(false);
                else await ChargePoint.CallAsync(new V201.HeartbeatRequest(), ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }

        Notify();
    }

    /// <summary>Power the EV takes now: charger, EV and profile limits, tapering from 80 % to 100 % state of charge.</summary>
    private double TargetPower(OcppSimulatedConnector c)
    {
        if (c.SoC >= 100) return 0;
        var p = Math.Min(_options.MaxPowerW, c.EvMaxPowerW);
        if (c.LimitW is { } limit) p = Math.Min(p, limit);
        if (c.SoC > 80) p *= Math.Max(0.1, (100 - c.SoC) / 20);
        return Math.Round(p);
    }

    // ---- transactions -------------------------------------------------------------------------------------------------

    private async Task StartTransactionAsync(OcppSimulatedConnector c, string tag, bool remote, CancellationToken ct)
    {
        c.PendingRemoteTag = null;
        c.IdTag = tag;
        c.TransactionStartWh = c.MeterWh;
        c.SinceMeter = 0;
        c.PowerW = TargetPower(c);
        if (_options.Version == OcppVersion.Ocpp16)
        {
            var r = await ChargePoint.CallAsync(new V16.StartTransactionRequest
            {
                ConnectorId = c.Id, IdTag = tag, MeterStart = (int)Math.Round(c.MeterWh), Timestamp = DateTimeOffset.UtcNow,
            }, ct).ConfigureAwait(false);
            if (r.IdTagInfo.Status != V16.AuthorizationStatus.Accepted)
            {
                // Deauthorized at start: the transaction exists at the CSMS, so it must be stopped.
                c.TransactionId = r.TransactionId.ToString(CultureInfo.InvariantCulture);
                await StopCoreAsync(c, "DeAuthorized", ct).ConfigureAwait(false);
                return;
            }

            c.TransactionId = r.TransactionId.ToString(CultureInfo.InvariantCulture);
            c.Status = c.PowerW > 0 ? "Charging" : "SuspendedEV";
            await SendStatusAsync(c.Id, ct).ConfigureAwait(false);
        }
        else
        {
            c.TransactionId = $"TX-{_options.Identity[^2..]}-{_nextTx++:D5}";
            c.SeqNo = 0;
            var remoteId = c.PendingRemoteStartId;
            c.PendingRemoteStartId = null;
            var r = await ChargePoint.CallAsync(new V201.TransactionEventRequest
            {
                EventType = V201.TransactionEventType.Started, Timestamp = DateTimeOffset.UtcNow, SeqNo = c.SeqNo++,
                TriggerReason = remote ? V201.TriggerReason.RemoteStart : V201.TriggerReason.Authorized,
                TransactionInfo = new V201.Transaction { TransactionId = c.TransactionId, ChargingState = V201.ChargingState.Charging, RemoteStartId = remoteId },
                IdToken = new V201.IdToken { Value = tag, Type = remote ? V201.IdTokenType.Central : V201.IdTokenType.ISO14443 },
                Evse = new V201.Evse { Id = c.Id, ConnectorId = 1 },
                MeterValue = [Meter201(c, V201.ReadingContext.TransactionBegin)],
            }, ct).ConfigureAwait(false);
            if (r.IdTokenInfo is { Status: not V201.AuthorizationStatus.Accepted })
            {
                await StopCoreAsync(c, "DeAuthorized", ct).ConfigureAwait(false);
                return;
            }

            c.Status = "Occupied";
        }
    }

    private async Task StopAsync(OcppSimulatedConnector c, string reason, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (c.TransactionId is not null) await StopCoreAsync(c, reason, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        Notify();
    }

    private async Task StopCoreAsync(OcppSimulatedConnector c, string reason, CancellationToken ct)
    {
        var tx = c.TransactionId!;
        c.PowerW = 0;
        if (_options.Version == OcppVersion.Ocpp16)
        {
            await ChargePoint.CallAsync(new V16.StopTransactionRequest
            {
                TransactionId = int.Parse(tx, CultureInfo.InvariantCulture), MeterStop = (int)Math.Round(c.MeterWh), Timestamp = DateTimeOffset.UtcNow,
                IdTag = c.IdTag, Reason = Enum.Parse<V16.StopReason>(reason),
                TransactionData = [Meter16(c, V16.ReadingContext.TransactionEnd)],
            }, ct).ConfigureAwait(false);
        }
        else
        {
            var why = reason switch { "UnlockCommand" or "Other" => V201.Reason.Other, "HardReset" or "SoftReset" => V201.Reason.ImmediateReset, _ => Enum.Parse<V201.Reason>(reason) };
            await ChargePoint.CallAsync(new V201.TransactionEventRequest
            {
                EventType = V201.TransactionEventType.Ended, Timestamp = DateTimeOffset.UtcNow, SeqNo = c.SeqNo++,
                TriggerReason = reason switch { "Remote" => V201.TriggerReason.RemoteStop, "EVDisconnected" => V201.TriggerReason.EVCommunicationLost, "DeAuthorized" => V201.TriggerReason.Deauthorized, "UnlockCommand" => V201.TriggerReason.UnlockCommand, "GroundFault" => V201.TriggerReason.AbnormalCondition, _ => V201.TriggerReason.StopAuthorized },
                TransactionInfo = new V201.Transaction { TransactionId = tx, ChargingState = c.EvPlugged ? V201.ChargingState.EVConnected : V201.ChargingState.Idle, StoppedReason = why },
                Evse = new V201.Evse { Id = c.Id, ConnectorId = 1 },
                MeterValue = [Meter201(c, V201.ReadingContext.TransactionEnd)],
            }, ct).ConfigureAwait(false);
        }

        c.TransactionId = null;
        c.IdTag = null;
        c.LimitW = null;
        if (c.PendingInoperative)
        {
            c.PendingInoperative = false;
            c.Operative = false;
        }

        if (!c.Faulted)
        {
            c.Status = !c.Operative ? "Unavailable" : c.EvPlugged ? (_options.Version == OcppVersion.Ocpp16 ? "Finishing" : "Occupied") : "Available";
            await SendStatusAsync(c.Id, ct).ConfigureAwait(false);
        }
    }

    // ---- messages -----------------------------------------------------------------------------------------------------

    private async Task SendStatusAsync(int connectorId, CancellationToken ct, string? errorCode = null)
    {
        if (_options.Version == OcppVersion.Ocpp16)
        {
            var status = connectorId == 0 ? (_connectors.All(c => !c.Operative) ? V16.ChargePointStatus.Unavailable : V16.ChargePointStatus.Available) : Enum.Parse<V16.ChargePointStatus>(Connector(connectorId).Status);
            await ChargePoint.CallAsync(new V16.StatusNotificationRequest
            {
                ConnectorId = connectorId, Status = status, Timestamp = DateTimeOffset.UtcNow,
                ErrorCode = errorCode is null ? V16.ChargePointErrorCode.NoError : Enum.Parse<V16.ChargePointErrorCode>(errorCode),
            }, ct).ConfigureAwait(false);
        }
        else
        {
            var status = Connector(connectorId).Status switch { "Available" => V201.ConnectorStatus.Available, "Unavailable" => V201.ConnectorStatus.Unavailable, "Faulted" => V201.ConnectorStatus.Faulted, "Reserved" => V201.ConnectorStatus.Reserved, _ => V201.ConnectorStatus.Occupied };
            await ChargePoint.CallAsync(new V201.StatusNotificationRequest { EvseId = connectorId, ConnectorId = 1, ConnectorStatus = status, Timestamp = DateTimeOffset.UtcNow }, ct).ConfigureAwait(false);
        }
    }

    private static V16.MeterValue Meter16(OcppSimulatedConnector c, V16.ReadingContext context) => new()
    {
        Timestamp = DateTimeOffset.UtcNow,
        SampledValue =
        [
            new V16.SampledValue { Value = Math.Round(c.MeterWh).ToString(CultureInfo.InvariantCulture), Measurand = OcppMeasurand.EnergyActiveImportRegister, Unit = "Wh", Context = context },
            new V16.SampledValue { Value = Math.Round(c.PowerW).ToString(CultureInfo.InvariantCulture), Measurand = OcppMeasurand.PowerActiveImport, Unit = "W", Context = context },
            new V16.SampledValue { Value = Math.Round(c.SoC, 1).ToString(CultureInfo.InvariantCulture), Measurand = OcppMeasurand.SoC, Unit = "Percent", Context = context, Location = "EV" },
        ],
    };

    private static V201.MeterValue Meter201(OcppSimulatedConnector c, V201.ReadingContext context) => new()
    {
        Timestamp = DateTimeOffset.UtcNow,
        SampledValue =
        [
            new V201.SampledValue { Value = (decimal)Math.Round(c.MeterWh), Measurand = OcppMeasurand.EnergyActiveImportRegister, UnitOfMeasure = new V201.UnitOfMeasure { Unit = "Wh" }, Context = context },
            new V201.SampledValue { Value = (decimal)Math.Round(c.PowerW), Measurand = OcppMeasurand.PowerActiveImport, UnitOfMeasure = new V201.UnitOfMeasure { Unit = "W" }, Context = context },
            new V201.SampledValue { Value = (decimal)Math.Round(c.SoC, 1), Measurand = OcppMeasurand.SoC, UnitOfMeasure = new V201.UnitOfMeasure { Unit = "Percent" }, Context = context, Location = "EV" },
        ],
    };

    private async Task SendMeterValuesAsync(OcppSimulatedConnector c, CancellationToken ct, bool triggered = false, V201.TriggerReason? reason = null)
    {
        if (_options.Version == OcppVersion.Ocpp16)
        {
            await ChargePoint.CallAsync(new V16.MeterValuesRequest
            {
                ConnectorId = c.Id,
                TransactionId = c.TransactionId is null ? null : int.Parse(c.TransactionId, CultureInfo.InvariantCulture),
                MeterValue = [Meter16(c, triggered ? V16.ReadingContext.Trigger : V16.ReadingContext.SamplePeriodic)],
            }, ct).ConfigureAwait(false);
        }
        else if (c.TransactionId is not null)
        {
            await ChargePoint.CallAsync(new V201.TransactionEventRequest
            {
                EventType = V201.TransactionEventType.Updated, Timestamp = DateTimeOffset.UtcNow, SeqNo = c.SeqNo++,
                TriggerReason = reason ?? (triggered ? V201.TriggerReason.Trigger : V201.TriggerReason.MeterValuePeriodic),
                TransactionInfo = new V201.Transaction { TransactionId = c.TransactionId, ChargingState = c.PowerW > 0 ? V201.ChargingState.Charging : V201.ChargingState.SuspendedEV },
                Evse = new V201.Evse { Id = c.Id, ConnectorId = 1 },
                MeterValue = [Meter201(c, triggered ? V201.ReadingContext.Trigger : V201.ReadingContext.SamplePeriodic)],
            }, ct).ConfigureAwait(false);
        }
        else
        {
            await ChargePoint.CallAsync(new V201.MeterValuesRequest { EvseId = c.Id, MeterValue = [Meter201(c, V201.ReadingContext.Trigger)] }, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Runs work after the current answer has been sent (a charger replies first, then acts).</summary>
    private void Later(Func<CancellationToken, Task> work) => _ = Task.Run(async () =>
    {
        try
        {
            await Task.Delay(20, _cts.Token).ConfigureAwait(false);
            await work(_cts.Token).ConfigureAwait(false);
            Notify();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IoTComException or InvalidOperationException)
        {
            _options.Logger?.LogDebug(ex, "OCPP simulator follow-up failed");
        }
    });

    private async Task DoResetAsync(string kind, CancellationToken ct)
    {
        foreach (var c in _connectors.Where(c => c.TransactionId is not null)) await StopAsync(c, kind, ct).ConfigureAwait(false);
        _stopping = true;
        try
        {
            await ChargePoint.DisconnectAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _stopping = false;
        }

        foreach (var c in _connectors) c.LimitW = null;
        await Task.Delay(TimeSpan.FromMilliseconds(300), ct).ConfigureAwait(false);
        await ChargePoint.ConnectAsync(ct).ConfigureAwait(false);
        await BootAsync(_options.Version == OcppVersion.Ocpp16 ? null : V201.BootReason.RemoteReset, ct).ConfigureAwait(false);
        Reset?.Invoke(kind);
    }

    // ---- configuration ------------------------------------------------------------------------------------------------

    private void InitConfiguration()
    {
        var n = _options.Connectors.ToString(CultureInfo.InvariantCulture);
        var meter = ((int)_options.MeterValueInterval.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        if (_options.Version == OcppVersion.Ocpp16)
        {
            _config["HeartbeatInterval"] = ("300", false);
            _config["MeterValueSampleInterval"] = (meter, false);
            _config["MeterValuesSampledData"] = ("Energy.Active.Import.Register,Power.Active.Import,SoC", false);
            _config["NumberOfConnectors"] = (n, true);
            _config["AuthorizeRemoteTxRequests"] = ("false", false);
            _config["ConnectionTimeOut"] = ("120", false);
            _config["ChargeProfileMaxStackLevel"] = ("8", true);
            _config["SupportedFeatureProfiles"] = ("Core,SmartCharging,RemoteTrigger", true);
        }
        else
        {
            _config["OCPPCommCtrlr/HeartbeatInterval"] = ("300", false);
            _config["SampledDataCtrlr/TxUpdatedInterval"] = (meter, false);
            _config["SampledDataCtrlr/TxUpdatedMeasurands"] = ("Energy.Active.Import.Register,Power.Active.Import,SoC", false);
            _config["AuthCtrlr/AuthorizeRemoteStart"] = ("false", false);
            _config["TxCtrlr/EVConnectionTimeOut"] = ("120", false);
            _config["ChargingStation/AvailabilityState"] = ("Available", true);
            _config["SmartChargingCtrlr/Enabled"] = ("true", false);
            _config["DeviceDataCtrlr/ItemsPerMessage"] = ("16", true);
        }
    }

    private string WriteConfig(string key, string value)
    {
        if (!_config.TryGetValue(key, out var current)) return "NotSupported";
        if (current.ReadOnly) return "Rejected";
        if (key.EndsWith("HeartbeatInterval", StringComparison.Ordinal) || key.EndsWith("Interval", StringComparison.Ordinal))
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0) return "Rejected";
            if (key.EndsWith("HeartbeatInterval", StringComparison.Ordinal)) _heartbeatSeconds = seconds;
            else _options.MeterValueInterval = TimeSpan.FromSeconds(seconds);
        }

        _config[key] = (value, false);
        return "Accepted";
    }

    private static double? LimitWatts(string unit, decimal limit, int? phases) =>
        unit == "W" ? (double)limit : (double)limit * 230 * (phases ?? 3);

    // ---- OCPP 1.6 handlers --------------------------------------------------------------------------------------------

    private void Register16()
    {
        var cp = ChargePoint;
        cp.On<V16.RemoteStartTransactionRequest, V16.RemoteStartTransactionResponse>((r, _) =>
        {
            var c = r.ConnectorId is { } id ? _connectors.FirstOrDefault(x => x.Id == id) : _connectors.FirstOrDefault(x => x.TransactionId is null && x.Operative && !x.Faulted && x.EvPlugged)
                ?? _connectors.FirstOrDefault(x => x.TransactionId is null && x.Operative && !x.Faulted);
            if (c is null || c.TransactionId is not null || !c.Operative || c.Faulted)
                return ValueTask.FromResult(new V16.RemoteStartTransactionResponse { Status = OcppGenericStatus.Rejected });
            if (r.ChargingProfile is { } p && p.ChargingSchedule.ChargingSchedulePeriod.FirstOrDefault() is { } period)
                c.LimitW = LimitWatts(p.ChargingSchedule.ChargingRateUnit.ToString(), period.Limit, period.NumberPhases);
            c.PendingRemoteTag = r.IdTag;
            Later(async ct =>
            {
                await _gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (c.EvPlugged && c.PendingRemoteTag is { } tag) await StartTransactionAsync(c, tag, true, ct).ConfigureAwait(false);
                    else if (c.PendingRemoteTag is not null && c.Status == "Available")
                    {
                        c.Status = "Preparing";
                        await SendStatusAsync(c.Id, ct).ConfigureAwait(false);
                    }
                }
                finally
                {
                    _gate.Release();
                }
            });
            return ValueTask.FromResult(new V16.RemoteStartTransactionResponse { Status = OcppGenericStatus.Accepted });
        });
        cp.On<V16.RemoteStopTransactionRequest, V16.RemoteStopTransactionResponse>((r, _) =>
        {
            var c = _connectors.FirstOrDefault(x => x.TransactionId == r.TransactionId.ToString(CultureInfo.InvariantCulture));
            if (c is null) return ValueTask.FromResult(new V16.RemoteStopTransactionResponse { Status = OcppGenericStatus.Rejected });
            Later(ct => StopAsync(c, "Remote", ct));
            return ValueTask.FromResult(new V16.RemoteStopTransactionResponse { Status = OcppGenericStatus.Accepted });
        });
        cp.On<V16.ResetRequest, V16.ResetResponse>((r, _) =>
        {
            Later(ct => DoResetAsync(r.Type == V16.ResetType.Hard ? "HardReset" : "SoftReset", ct));
            return ValueTask.FromResult(new V16.ResetResponse { Status = OcppGenericStatus.Accepted });
        });
        cp.On<V16.UnlockConnectorRequest, V16.UnlockConnectorResponse>((r, _) =>
        {
            var c = _connectors.FirstOrDefault(x => x.Id == r.ConnectorId);
            if (c is null) return ValueTask.FromResult(new V16.UnlockConnectorResponse { Status = V16.UnlockStatus.NotSupported });
            if (c.TransactionId is not null) Later(ct => StopAsync(c, "UnlockCommand", ct));
            return ValueTask.FromResult(new V16.UnlockConnectorResponse { Status = V16.UnlockStatus.Unlocked });
        });
        cp.On<V16.ChangeAvailabilityRequest, V16.ChangeAvailabilityResponse>((r, _) =>
        {
            var targets = r.ConnectorId == 0 ? _connectors : _connectors.Where(x => x.Id == r.ConnectorId).ToList();
            if (targets.Count == 0) return ValueTask.FromResult(new V16.ChangeAvailabilityResponse { Status = V16.AvailabilityStatus.Rejected });
            var operative = r.Type == V16.AvailabilityType.Operative;
            var scheduled = false;
            foreach (var c in targets)
            {
                if (!operative && c.TransactionId is not null)
                {
                    c.PendingInoperative = true;
                    scheduled = true;
                    continue;
                }

                c.Operative = operative;
                c.PendingInoperative = false;
                if (!c.Faulted && c.TransactionId is null) c.Status = !operative ? "Unavailable" : c.EvPlugged ? "Preparing" : "Available";
            }

            Later(async ct =>
            {
                foreach (var c in targets.Where(c => c.TransactionId is null)) await SendStatusAsync(c.Id, ct).ConfigureAwait(false);
            });
            return ValueTask.FromResult(new V16.ChangeAvailabilityResponse { Status = scheduled ? V16.AvailabilityStatus.Scheduled : V16.AvailabilityStatus.Accepted });
        });
        cp.On<V16.GetConfigurationRequest, V16.GetConfigurationResponse>((r, _) =>
        {
            var keys = r.Key is { Count: > 0 } k ? k : [.. _config.Keys];
            return ValueTask.FromResult(new V16.GetConfigurationResponse
            {
                ConfigurationKey = [.. keys.Where(_config.ContainsKey).Select(x => new V16.KeyValue { Key = x, Value = _config[x].Value, Readonly = _config[x].ReadOnly })],
                UnknownKey = keys.Where(x => !_config.ContainsKey(x)).ToList() is { Count: > 0 } unknown ? unknown : null,
            });
        });
        cp.On<V16.ChangeConfigurationRequest, V16.ChangeConfigurationResponse>((r, _) =>
            ValueTask.FromResult(new V16.ChangeConfigurationResponse { Status = Enum.Parse<V16.ConfigurationStatus>(WriteConfig(r.Key, r.Value)) }));
        cp.On<V16.TriggerMessageRequest, V16.TriggerMessageResponse>((r, _) =>
        {
            Func<CancellationToken, Task>? work = r.RequestedMessage switch
            {
                V16.MessageTrigger.Heartbeat => ct => ChargePoint.CallAsync(new V16.HeartbeatRequest(), ct),
                V16.MessageTrigger.BootNotification => ct => BootAsync(null, ct),
                V16.MessageTrigger.StatusNotification => async ct =>
                {
                    foreach (var c in _connectors.Where(c => r.ConnectorId is null || c.Id == r.ConnectorId)) await SendStatusAsync(c.Id, ct).ConfigureAwait(false);
                },
                V16.MessageTrigger.MeterValues => async ct =>
                {
                    foreach (var c in _connectors.Where(c => r.ConnectorId is null || c.Id == r.ConnectorId)) await SendMeterValuesAsync(c, ct, triggered: true).ConfigureAwait(false);
                },
                _ => null,
            };
            if (work is null) return ValueTask.FromResult(new V16.TriggerMessageResponse { Status = V16.TriggerMessageStatus.NotImplemented });
            Later(work);
            return ValueTask.FromResult(new V16.TriggerMessageResponse { Status = V16.TriggerMessageStatus.Accepted });
        });
        cp.On<V16.SetChargingProfileRequest, V16.SetChargingProfileResponse>((r, _) =>
        {
            var p = r.CsChargingProfiles;
            var period = p.ChargingSchedule.ChargingSchedulePeriod.OrderBy(x => x.StartPeriod).FirstOrDefault();
            if (period is null || p.ChargingProfilePurpose == V16.ChargingProfilePurpose.ChargePointMaxProfile && r.ConnectorId != 0)
                return ValueTask.FromResult(new V16.SetChargingProfileResponse { Status = V16.ChargingProfileStatus.Rejected });
            var watts = LimitWatts(p.ChargingSchedule.ChargingRateUnit.ToString(), period.Limit, period.NumberPhases);
            foreach (var c in _connectors.Where(c => r.ConnectorId == 0 || c.Id == r.ConnectorId)) c.LimitW = watts;
            Notify();
            return ValueTask.FromResult(new V16.SetChargingProfileResponse { Status = V16.ChargingProfileStatus.Accepted });
        });
        cp.On<V16.ClearChargingProfileRequest, V16.ClearChargingProfileResponse>((r, _) =>
        {
            foreach (var c in _connectors.Where(c => r.ConnectorId is null or 0 || c.Id == r.ConnectorId)) c.LimitW = null;
            Notify();
            return ValueTask.FromResult(new V16.ClearChargingProfileResponse { Status = V16.ClearChargingProfileStatus.Accepted });
        });
        cp.On<V16.ClearCacheRequest, V16.ClearCacheResponse>((_, _) => ValueTask.FromResult(new V16.ClearCacheResponse { Status = OcppGenericStatus.Accepted }));
        cp.On<V16.DataTransferRequest, V16.DataTransferResponse>((_, _) => ValueTask.FromResult(new V16.DataTransferResponse { Status = V16.DataTransferStatus.UnknownVendorId }));
    }

    // ---- OCPP 2.0.1 handlers ------------------------------------------------------------------------------------------

    private void Register201()
    {
        var cp = ChargePoint;
        cp.On<V201.RequestStartTransactionRequest, V201.RequestStartTransactionResponse>((r, _) =>
        {
            var c = r.EvseId is { } id ? _connectors.FirstOrDefault(x => x.Id == id) : _connectors.FirstOrDefault(x => x.TransactionId is null && x.Operative && !x.Faulted && x.EvPlugged)
                ?? _connectors.FirstOrDefault(x => x.TransactionId is null && x.Operative && !x.Faulted);
            if (c is null || c.TransactionId is not null || !c.Operative || c.Faulted)
                return ValueTask.FromResult(new V201.RequestStartTransactionResponse { Status = V201.RequestStartStopStatus.Rejected, TransactionId = c?.TransactionId });
            if (r.ChargingProfile?.ChargingSchedule.FirstOrDefault() is { } sched && sched.ChargingSchedulePeriod.FirstOrDefault() is { } period)
                c.LimitW = LimitWatts(sched.ChargingRateUnit.ToString(), period.Limit, period.NumberPhases);
            c.PendingRemoteTag = r.IdToken.Value;
            c.PendingRemoteStartId = r.RemoteStartId;
            Later(async ct =>
            {
                await _gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (c.EvPlugged && c.PendingRemoteTag is { } tag) await StartTransactionAsync(c, tag, true, ct).ConfigureAwait(false);
                }
                finally
                {
                    _gate.Release();
                }
            });
            return ValueTask.FromResult(new V201.RequestStartTransactionResponse { Status = V201.RequestStartStopStatus.Accepted });
        });
        cp.On<V201.RequestStopTransactionRequest, V201.RequestStopTransactionResponse>((r, _) =>
        {
            var c = _connectors.FirstOrDefault(x => x.TransactionId == r.TransactionId);
            if (c is null) return ValueTask.FromResult(new V201.RequestStopTransactionResponse { Status = V201.RequestStartStopStatus.Rejected });
            Later(ct => StopAsync(c, "Remote", ct));
            return ValueTask.FromResult(new V201.RequestStopTransactionResponse { Status = V201.RequestStartStopStatus.Accepted });
        });
        cp.On<V201.ResetRequest, V201.ResetResponse>((r, _) =>
        {
            var busy = _connectors.Any(c => c.TransactionId is not null);
            if (r.Type == V201.ResetType.OnIdle && busy) return ValueTask.FromResult(new V201.ResetResponse { Status = V201.ResetStatus.Scheduled });
            Later(ct => DoResetAsync("HardReset", ct));
            return ValueTask.FromResult(new V201.ResetResponse { Status = V201.ResetStatus.Accepted });
        });
        cp.On<V201.UnlockConnectorRequest, V201.UnlockConnectorResponse>((r, _) =>
        {
            var c = _connectors.FirstOrDefault(x => x.Id == r.EvseId);
            if (c is null || r.ConnectorId != 1) return ValueTask.FromResult(new V201.UnlockConnectorResponse { Status = V201.UnlockStatus.UnknownConnector });
            if (c.TransactionId is not null) return ValueTask.FromResult(new V201.UnlockConnectorResponse { Status = V201.UnlockStatus.OngoingAuthorizedTransaction });
            return ValueTask.FromResult(new V201.UnlockConnectorResponse { Status = V201.UnlockStatus.Unlocked });
        });
        cp.On<V201.ChangeAvailabilityRequest, V201.ChangeAvailabilityResponse>((r, _) =>
        {
            var targets = r.Evse is null ? _connectors : _connectors.Where(x => x.Id == r.Evse.Id).ToList();
            if (targets.Count == 0) return ValueTask.FromResult(new V201.ChangeAvailabilityResponse { Status = V201.ChangeAvailabilityStatus.Rejected });
            var operative = r.OperationalStatus == V201.OperationalStatus.Operative;
            var scheduled = false;
            foreach (var c in targets)
            {
                if (!operative && c.TransactionId is not null)
                {
                    c.PendingInoperative = true;
                    scheduled = true;
                    continue;
                }

                c.Operative = operative;
                if (!c.Faulted && c.TransactionId is null) c.Status = !operative ? "Unavailable" : c.EvPlugged ? "Occupied" : "Available";
            }

            Later(async ct =>
            {
                foreach (var c in targets.Where(c => c.TransactionId is null)) await SendStatusAsync(c.Id, ct).ConfigureAwait(false);
            });
            return ValueTask.FromResult(new V201.ChangeAvailabilityResponse { Status = scheduled ? V201.ChangeAvailabilityStatus.Scheduled : V201.ChangeAvailabilityStatus.Accepted });
        });
        cp.On<V201.GetVariablesRequest, V201.GetVariablesResponse>((r, _) => ValueTask.FromResult(new V201.GetVariablesResponse
        {
            GetVariableResult = [.. r.GetVariableData.Select(d =>
            {
                var key = $"{d.Component.Name}/{d.Variable.Name}";
                var known = _config.TryGetValue(key, out var v);
                var componentKnown = _config.Keys.Any(k => k.StartsWith(d.Component.Name + "/", StringComparison.Ordinal));
                return new V201.GetVariableResult
                {
                    Component = d.Component, Variable = d.Variable, AttributeType = d.AttributeType,
                    AttributeStatus = known ? V201.GetVariableStatus.Accepted : componentKnown ? V201.GetVariableStatus.UnknownVariable : V201.GetVariableStatus.UnknownComponent,
                    AttributeValue = known ? v.Value : null,
                };
            })],
        }));
        cp.On<V201.SetVariablesRequest, V201.SetVariablesResponse>((r, _) => ValueTask.FromResult(new V201.SetVariablesResponse
        {
            SetVariableResult = [.. r.SetVariableData.Select(d =>
            {
                var status = WriteConfig($"{d.Component.Name}/{d.Variable.Name}", d.AttributeValue);
                return new V201.SetVariableResult
                {
                    Component = d.Component, Variable = d.Variable, AttributeType = d.AttributeType,
                    AttributeStatus = status == "NotSupported" ? V201.SetVariableStatus.UnknownVariable : Enum.Parse<V201.SetVariableStatus>(status),
                };
            })],
        }));
        cp.On<V201.TriggerMessageRequest, V201.TriggerMessageResponse>((r, _) =>
        {
            var evse = r.Evse?.Id;
            Func<CancellationToken, Task>? work = r.RequestedMessage switch
            {
                V201.MessageTrigger.Heartbeat => ct => ChargePoint.CallAsync(new V201.HeartbeatRequest(), ct),
                V201.MessageTrigger.BootNotification => ct => BootAsync(V201.BootReason.Triggered, ct),
                V201.MessageTrigger.StatusNotification => async ct =>
                {
                    foreach (var c in _connectors.Where(c => evse is null || c.Id == evse)) await SendStatusAsync(c.Id, ct).ConfigureAwait(false);
                },
                V201.MessageTrigger.MeterValues or V201.MessageTrigger.TransactionEvent => async ct =>
                {
                    foreach (var c in _connectors.Where(c => evse is null || c.Id == evse)) await SendMeterValuesAsync(c, ct, triggered: true).ConfigureAwait(false);
                },
                _ => null,
            };
            if (work is null) return ValueTask.FromResult(new V201.TriggerMessageResponse { Status = V201.TriggerMessageStatus.NotImplemented });
            Later(work);
            return ValueTask.FromResult(new V201.TriggerMessageResponse { Status = V201.TriggerMessageStatus.Accepted });
        });
        cp.On<V201.SetChargingProfileRequest, V201.SetChargingProfileResponse>((r, _) =>
        {
            var sched = r.ChargingProfile.ChargingSchedule.FirstOrDefault();
            var period = sched?.ChargingSchedulePeriod.OrderBy(x => x.StartPeriod).FirstOrDefault();
            if (sched is null || period is null) return ValueTask.FromResult(new V201.SetChargingProfileResponse { Status = V201.ChargingProfileStatus.Rejected });
            var watts = LimitWatts(sched.ChargingRateUnit.ToString(), period.Limit, period.NumberPhases);
            foreach (var c in _connectors.Where(c => r.EvseId == 0 || c.Id == r.EvseId)) c.LimitW = watts;
            Notify();
            return ValueTask.FromResult(new V201.SetChargingProfileResponse { Status = V201.ChargingProfileStatus.Accepted });
        });
        cp.On<V201.ClearChargingProfileRequest, V201.ClearChargingProfileResponse>((r, _) =>
        {
            var evse = r.ChargingProfileCriteria?.EvseId;
            foreach (var c in _connectors.Where(c => evse is null or 0 || c.Id == evse)) c.LimitW = null;
            Notify();
            return ValueTask.FromResult(new V201.ClearChargingProfileResponse { Status = V201.ClearChargingProfileStatus.Accepted });
        });
        cp.On<V201.ClearCacheRequest, V201.ClearCacheResponse>((_, _) => ValueTask.FromResult(new V201.ClearCacheResponse { Status = V201.ClearCacheStatus.Accepted }));
        cp.On<V201.DataTransferRequest, V201.DataTransferResponse>((_, _) => ValueTask.FromResult(new V201.DataTransferResponse { Status = V201.DataTransferStatus.UnknownVendorId }));
    }
}
