using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using IoTCom.Net.Gallery.Infrastructure;
using IoTCom.Net.Protocols.Ocpp;
using IoTCom.Net.Transports;

namespace IoTCom.Net.Gallery.Demos;

/// <summary>One parking bay: a connector of a charger, as the CSMS and the driver see it.</summary>
public sealed partial class ChargeBay : ObservableObject
{
    internal ChargeBay(OcppChargePointSimulator charger, int connector, int index) => (Charger, Connector, Index) = (charger, connector, index);

    internal OcppChargePointSimulator Charger { get; }
    public int Connector { get; }
    internal int Index { get; }
    public string Station => Charger.Identity;
    public string Label => $"{Charger.Identity[^5..]} · {Connector}";
    public string Version => Charger.Version.DisplayName();
    public double MaxKw => Charger.MaxPowerW / 1000;

    [ObservableProperty] private string _status = "Offline";
    [ObservableProperty] private double _powerKw;
    [ObservableProperty] private double _soC;
    [ObservableProperty] private double _energyKWh;
    [ObservableProperty] private double? _limitKw;
    [ObservableProperty] private string? _transactionId;
    [ObservableProperty] private string? _car;
    [ObservableProperty] private bool _online;
    [ObservableProperty] private bool _faulted;
}

/// <summary>
/// A motorway rest-area charging hub run by a small CSMS: two AC wallboxes on OCPP 1.6J and a DC fast charger on
/// OCPP 2.0.1. Drivers plug in and tap cards; the operator starts and stops remotely, limits power, resets chargers
/// (behind a write lock), and load management keeps the site under its grid connection.
/// </summary>
public sealed partial class EvChargingDemo : GalleryDemo
{
    public override string Id => "ocpp-ev-charging";
    public override Text Title => new("EV charging hub over OCPP", "Hub pengisian EV lewat OCPP");
    public override Text Summary => new(
        "A rest-area charging hub with two AC wallboxes on OCPP 1.6J and a DC fast charger on OCPP 2.0.1: plug cars in, tap cards, start and stop remotely, cap the power, and let load management keep the site under its grid connection.",
        "Hub pengisian di rest area dengan dua wallbox AC ber-OCPP 1.6J dan satu pengisi cepat DC ber-OCPP 2.0.1: colokkan mobil, tempelkan kartu, mulai dan hentikan dari jauh, batasi daya, dan biarkan manajemen beban menjaga situs di bawah sambungan PLN-nya.");
    public override Text Docs => new(
        "OCPP (Open Charge Point Protocol) is how a charger talks to its back office, the CSMS. The charger opens a WebSocket to the CSMS with its identity as the last path segment and the OCPP version as the subprotocol (ocpp1.6 or ocpp2.0.1), so the charger is the client even though the CSMS sends it commands. Every message is a small JSON array: a CALL [2, id, action, payload], answered by a CALLRESULT [3, id, payload] or a CALLERROR [4, id, code, description, details]. Each side has at most one CALL outstanding.\n\nA charger boots with BootNotification, sends a Heartbeat at the interval the CSMS gives, and reports each connector with StatusNotification. A driver taps a card (Authorize), and the transaction is reported with StartTransaction / MeterValues / StopTransaction in 1.6, or with one TransactionEvent message (Started, Updated, Ended) in 2.0.1. The CSMS can start and stop remotely, reset, unlock, change availability, read and change configuration, and send charging profiles — power limits per connector, the basis of smart charging and load management.\n\nHere the CSMS is OcppCsms and every charger is an OcppChargePointSimulator with an EV model (state of charge, taper above 80 %). Commands are refused until the write lock is opened; load management then divides the site limit between the charging bays with SetChargingProfile.",
        "OCPP (Open Charge Point Protocol) adalah cara pengisi daya berbicara dengan back office-nya, CSMS. Pengisi membuka WebSocket ke CSMS dengan identitasnya sebagai segmen path terakhir dan versi OCPP sebagai subprotokol (ocpp1.6 atau ocpp2.0.1), jadi pengisi adalah klien walaupun CSMS yang mengirim perintah. Setiap pesan adalah array JSON kecil: CALL [2, id, aksi, payload], dijawab CALLRESULT [3, id, payload] atau CALLERROR [4, id, kode, deskripsi, detail]. Setiap sisi paling banyak punya satu CALL yang belum dijawab.\n\nPengisi melakukan boot dengan BootNotification, mengirim Heartbeat pada interval yang diberikan CSMS, dan melaporkan setiap konektor dengan StatusNotification. Pengemudi menempelkan kartu (Authorize), lalu transaksi dilaporkan dengan StartTransaction / MeterValues / StopTransaction di 1.6, atau dengan satu pesan TransactionEvent (Started, Updated, Ended) di 2.0.1. CSMS dapat memulai dan menghentikan dari jauh, reset, membuka kunci, mengubah ketersediaan, membaca dan mengubah konfigurasi, serta mengirim charging profile — batas daya per konektor, dasar smart charging dan manajemen beban.\n\nDi sini CSMS adalah OcppCsms dan setiap pengisi adalah OcppChargePointSimulator dengan model EV (state of charge, daya menurun di atas 80 %). Perintah ditolak sampai kunci tulis dibuka; manajemen beban lalu membagi batas situs ke bay yang sedang mengisi dengan SetChargingProfile.");
    public override string Category => "Energy";
    public override IReadOnlyList<string> Protocols => ["OCPP 1.6J", "OCPP 2.0.1", "WebSocket", "JSON"];
    public override Difficulty Difficulty => Difficulty.Intermediate;
    public override string DocsPath => "docs/en/protocols/ocpp.md";

    /// <summary>Grid connection of the site, kW (what the load bar is drawn against).</summary>
    public const double GridKw = 150;

    private static readonly (string Name, double SocStart, double BatteryKWh, double MaxKw)[] Cars =
    [
        ("Hatchback 37 kWh", 22, 37, 11), ("Sedan 60 kWh", 31, 60, 11), ("SUV 77 kWh", 15, 77, 150), ("Compact 50 kWh", 44, 50, 7.4), ("MPV 72 kWh", 27, 72, 100),
    ];

    private InMemoryTransportListener? _listener;
    private OcppCsms? _csms;
    private readonly List<OcppChargePointSimulator> _chargers = [];
    private CancellationTokenSource? _cts;
    private int _nextCar;

    /// <summary>Bays, west to east.</summary>
    public ObservableCollection<ChargeBay> Bays { get; } = [];

    [ObservableProperty] private ChargeBay? _selected;
    [ObservableProperty] private bool _commandsAllowed;
    [ObservableProperty] private bool _loadManagement;
    [ObservableProperty] private double _siteLimitKw = 100;
    [ObservableProperty] private double _limitTarget = 11;
    [ObservableProperty] private double _siteKw;
    [ObservableProperty] private double _sessionKWh;

    public TrendBuffer SiteTrend { get; } = new(120, 20);

    /// <summary>Raised when the forecourt should redraw.</summary>
    public event Action? Changed;

    protected override async Task OnStartAsync()
    {
        _cts = new CancellationTokenSource();
        Bays.Clear();
        _chargers.Clear();
        _listener = new InMemoryTransportListener("csms.spklu-km57");
        _csms = OcppCsms.Create(o =>
        {
            o.ListenInMemory(_listener);
            o.AuthorizedTags = new HashSet<string>(["04A1B2C3", "APP-7731", "0451FE22"], StringComparer.Ordinal);
            o.HeartbeatInterval = TimeSpan.FromSeconds(120);
            o.CallTimeout = TimeSpan.FromSeconds(5);
            o.AllowCommands();   // the demo's write lock sits in front of the CSMS's command calls
        });
        _csms.AddTap(Tap);
        _csms.StationChanged += _ => Ui(Refresh);
        _csms.TransactionStarted += (st, tx) => AddLog($"{st.Identity}: transaction {tx.Id} started on {tx.ConnectorId} by {tx.IdTag}");
        _csms.TransactionStopped += (st, tx) => AddLog($"{st.Identity}: transaction {tx.Id} ended ({tx.StopReason}), {tx.EnergyWh / 1000:0.00} kWh");
        _csms.StationConnected += st => AddLog($"{st.Identity} connected ({st.Version.DisplayName()})");
        _csms.StationDisconnected += st => AddLog($"{st.Identity} disconnected");
        await _csms.StartAsync();

        await AddChargerAsync("SPKLU-KM57-AC-01", OcppVersion.Ocpp16, 2, 22_000, "IoTCom EVSE-22");
        await AddChargerAsync("SPKLU-KM57-AC-02", OcppVersion.Ocpp16, 2, 22_000, "IoTCom EVSE-22");
        await AddChargerAsync("SPKLU-KM57-DC-01", OcppVersion.Ocpp201, 2, 60_000, "IoTCom DC-60");
        Selected = Bays[0];
        _ = TickAsync(_cts.Token);

        if (Environment.GetEnvironmentVariable("IOTCOM_GALLERY_SCREENSHOT") == "1")
        {
            foreach (var (bay, tag) in new[] { (Bays[0], "04A1B2C3"), (Bays[2], "0451FE22"), (Bays[4], "APP-7731"), (Bays[5], "04A1B2C3") })
            {
                Selected = bay;
                await PlugInAsync();
                await TapCardAsync(tag);
            }

            Selected = Bays[3];
            await PlugInAsync();
            CommandsAllowed = true;
            LoadManagement = true;
            Selected = Bays[1];
            await FaultAsync();
            Selected = Bays[4];
        }

        SetStatus(new Text("CSMS ws://csms.spklu-km57/ocpp/{identity} · AC-01 and AC-02 on OCPP 1.6J, DC-01 on OCPP 2.0.1 · 30 simulated seconds per second · cards 04A1B2C3 and 0451FE22 and the app token APP-7731 are on the list.",
            "CSMS ws://csms.spklu-km57/ocpp/{identity} · AC-01 dan AC-02 ber-OCPP 1.6J, DC-01 ber-OCPP 2.0.1 · 30 detik simulasi per detik · kartu 04A1B2C3 dan 0451FE22 serta token aplikasi APP-7731 ada di daftar."));
    }

    private async Task AddChargerAsync(string identity, OcppVersion version, int connectors, double maxW, string model)
    {
        var sim = OcppChargePointSimulator.Create(o =>
        {
            o.Identity = identity;
            o.Version = version;
            o.Connectors = connectors;
            o.MaxPowerW = maxW;
            o.Model = model;
            o.SerialNumber = "GVC-" + identity[^5..];
            o.AutoRun = true;
            o.TimeScale = 30;
            o.ReconnectDelay = TimeSpan.FromSeconds(1);
            o.UseTransport(_listener!.Connect);
        });
        sim.Changed += () => Ui(Refresh);
        sim.Reset += kind => AddLog($"{identity}: rebooted ({kind}) and booted again");
        _chargers.Add(sim);
        for (var c = 1; c <= connectors; c++) Bays.Add(new ChargeBay(sim, c, Bays.Count));
        await sim.StartAsync();
    }

    /// <summary>Copies the CSMS's view (and the cars) into the bays.</summary>
    private void Refresh()
    {
        if (_csms is null) return;
        double site = 0, energy = 0;
        foreach (var bay in Bays)
        {
            var sim = bay.Charger.Connectors[bay.Connector - 1];
            if (_csms.Stations.TryGetValue(bay.Station, out var st) && st.Connectors.TryGetValue(bay.Connector, out var c))
            {
                bay.Online = st.IsOnline;
                bay.Status = !st.IsOnline ? "Offline" : c.Transaction is not null ? (c.ChargingState == "Occupied" ? "Charging" : c.ChargingState ?? c.Status) : c.Status;
                bay.TransactionId = c.Transaction?.Id;
                bay.EnergyKWh = (c.Transaction?.EnergyWh ?? 0) / 1000;
                bay.LimitKw = c.LimitW / 1000;
            }

            bay.PowerKw = sim.PowerW / 1000;
            bay.SoC = sim.SoC;
            bay.Faulted = sim.Faulted;
            if (!sim.EvPlugged) bay.Car = null;
            site += bay.PowerKw;
            energy += bay.EnergyKWh;
        }

        SiteKw = site;
        SessionKWh = energy;
        Changed?.Invoke();
    }

    private async Task TickAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct);
                Ui(() =>
                {
                    Refresh();
                    SiteTrend.Add(SiteKw);
                });
                if (LoadManagement && CommandsAllowed) await BalanceAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Load management: share the site limit equally between bays with a car that takes power.</summary>
    private async Task BalanceAsync()
    {
        var active = Bays.Where(b => b.TransactionId is not null && b.SoC < 100).ToList();
        if (active.Count == 0 || _csms is null) return;
        var demand = active.Sum(b => Math.Min(b.MaxKw, CarMax(b)));
        foreach (var bay in active)
        {
            double? target = demand <= SiteLimitKw ? null : Math.Floor(SiteLimitKw / active.Count * 10) / 10;
            if (target is null && bay.LimitKw is null) continue;
            if (target is not null && bay.LimitKw is { } current && Math.Abs(current - target.Value) < 0.5) continue;
            try
            {
                var status = await _csms.SetPowerLimitAsync(bay.Station, bay.Connector, target * 1000);
                AddLog(target is null
                    ? $"Load management: {bay.Label} limit cleared ({status})"
                    : $"Load management: {bay.Label} → {target:0.0} kW so the site stays under {SiteLimitKw:0} kW ({status})");
            }
            catch (IoTComException ex)
            {
                AddLog($"Load management: {bay.Label}: {ex.Message}");
            }
        }
    }

    private static double CarMax(ChargeBay b) => b.Charger.Connectors[b.Connector - 1].EvMaxPowerW / 1000;

    // ---- what drivers do (no lock: these are events in the field) ----------------------------------------------------

    public async Task PlugInAsync()
    {
        if (Selected is not { } bay) return;
        var sim = bay.Charger.Connectors[bay.Connector - 1];
        if (sim.EvPlugged) return;
        var car = Cars[_nextCar++ % Cars.Length];
        bay.Car = car.Name;
        await Field(bay, $"{car.Name} plugged into {bay.Label} at {car.SocStart:0} %", () => bay.Charger.PlugInAsync(bay.Connector, car.SocStart, car.BatteryKWh, car.MaxKw));
    }

    public Task TapCardAsync(string tag) => Selected is not { } bay ? Task.CompletedTask : Field(bay, null, async () =>
    {
        var ok = await bay.Charger.PresentCardAsync(bay.Connector, tag);
        AddLog(ok ? $"Card {tag} at {bay.Label}: Authorize → Accepted" : $"Card {tag} at {bay.Label}: Authorize → Invalid (not on the CSMS list)");
    });

    public Task UnplugAsync() => Selected is not { } bay ? Task.CompletedTask : Field(bay, $"Car unplugged from {bay.Label}", () => bay.Charger.UnplugAsync(bay.Connector));

    public Task FaultAsync() => Selected is not { } bay ? Task.CompletedTask : Field(bay, bay.Faulted ? $"{bay.Label}: fault cleared" : $"{bay.Label}: ground fault — the charger stops and reports Faulted",
        () => bay.Charger.SetFaultAsync(bay.Connector, !bay.Faulted));

    public Task DropLinkAsync() => Selected is not { } bay ? Task.CompletedTask : Field(bay, $"{bay.Station}: cellular link lost — it reconnects and boots again", async () => await bay.Charger.DropConnectionAsync());

    private async Task Field(ChargeBay bay, string? log, Func<Task> action)
    {
        try
        {
            await action();
            if (log is not null) AddLog(log);
        }
        catch (Exception ex) when (ex is IoTComException or InvalidOperationException)
        {
            AddLog($"{bay.Label}: {ex.Message}");
        }

        Ui(Refresh);
    }

    // ---- what the operator does (behind the write lock) ---------------------------------------------------------------

    private async Task Guarded(string what, Func<OcppCsms, ChargeBay, Task<string>> action)
    {
        if (Selected is not { } bay || _csms is null) return;
        if (!CommandsAllowed)
        {
            AddLog($"{what} {bay.Label}: refused — the write lock is closed (read-only operator)");
            return;
        }

        try
        {
            AddLog($"{what} {bay.Label} → {await action(_csms, bay)}");
        }
        catch (IoTComException ex)
        {
            AddLog($"{what} {bay.Label}: {ex.Message}");
        }

        Ui(Refresh);
    }

    public Task RemoteStartAsync() => Guarded("Remote start (app token APP-7731)", async (csms, b) => await csms.RemoteStartAsync(b.Station, b.Connector, "APP-7731") ? "Accepted" : "Rejected");

    public Task RemoteStopAsync() => Guarded("Remote stop", async (csms, b) =>
        b.TransactionId is { } tx ? (await csms.RemoteStopAsync(b.Station, tx) ? "Accepted" : "Rejected") : "no transaction");

    public Task LimitAsync() => Guarded($"Limit to {LimitTarget:0.0} kW", (csms, b) => csms.SetPowerLimitAsync(b.Station, b.Connector, LimitTarget * 1000));

    public Task ClearLimitAsync() => Guarded("Clear limit", (csms, b) => csms.SetPowerLimitAsync(b.Station, b.Connector, null));

    public Task AvailabilityAsync() => Guarded("Change availability", (csms, b) => csms.SetAvailabilityAsync(b.Station, b.Connector, b.Status == "Unavailable"));

    public Task ResetAsync() => Guarded("Hard reset", (csms, b) => csms.ResetAsync(b.Station, hard: true));

    partial void OnLoadManagementChanged(bool value)
    {
        if (value && !CommandsAllowed) AddLog("Load management needs the write lock open: it sends charging profiles.");
        else AddLog(value ? $"Load management on: the site stays under {SiteLimitKw:0} kW" : "Load management off");
    }

    protected override async Task OnStopAsync()
    {
        if (_cts is not null) await _cts.CancelAsync();
        foreach (var c in _chargers) await c.DisposeAsync();
        _chargers.Clear();
        if (_csms is not null) await _csms.DisposeAsync();
        (_csms, _listener) = (null, null);
        _cts?.Dispose();
        _cts = null;
        CommandsAllowed = false;
        LoadManagement = false;
        Status = "";
    }
}
