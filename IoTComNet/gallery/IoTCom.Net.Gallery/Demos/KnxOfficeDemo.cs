using System.Collections.ObjectModel;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using IoTCom.Net.Gallery.Infrastructure;
using IoTCom.Net.Protocols.Knx;
using IoTCom.Net.Transports;

namespace IoTCom.Net.Gallery.Demos;

/// <summary>One line of the group monitor.</summary>
public sealed record KnxMonitorRow(string Time, string Source, string Destination, string Service, string Value, string Name);

/// <summary>
/// An office floor on a KNX line behind a KNX IP router: the operator's visualisation talks over a KNXnet/IP tunnel,
/// switches and dims lights, moves blinds and changes set points (behind a write lock), and a group monitor decodes every
/// telegram with the project's datapoint types.
/// </summary>
public sealed partial class KnxOfficeDemo : GalleryDemo
{
    public override string Id => "knx-office-floor";
    public override Text Title => new("Office floor over KNX", "Lantai kantor lewat KNX");
    public override Text Summary => new(
        "Four rooms on a KNX line behind a KNX IP router: switch and dim the lights, move the blinds and change set points over a KNXnet/IP tunnel, and read every telegram in a group monitor decoded with the project's datapoint types.",
        "Empat ruangan di jalur KNX di belakang router KNX IP: nyalakan dan redupkan lampu, gerakkan tirai, dan ubah set point lewat tunnel KNXnet/IP, lalu baca setiap telegram di group monitor yang diurai dengan datapoint type proyek.");
    public override Text Docs => new(
        "KNX is the building bus for lighting, blinds and heating. Devices on a twisted-pair line have individual addresses (area.line.device, such as 1.1.4) and talk to each other through group addresses (main/middle/sub, such as 3/0/1). A wall switch does not address a lamp: it writes \"on\" to the group address 1/0/1, and every actuator that listens to 1/0/1 reacts. Each group address carries one datapoint type (DPT): 1.001 for a switch, 5.001 for a percentage, 9.001 for a temperature as a KNX 2-byte float.\n\nKNXnet/IP brings the line onto Ethernet. A KNX IP interface offers tunnelling: a client connects (CONNECT_REQUEST, getting a channel and its own individual address), keeps the connection alive with a heartbeat every 60 s, and exchanges cEMI frames — L_Data.req to send, L_Data.con as the confirmation, L_Data.ind for everything seen on the line — each tunnelling request acknowledged with its sequence number. A KNX IP router also forwards every telegram as a ROUTING_INDICATION to the multicast group 224.0.23.12, and answers SEARCH_REQUESTs so tools can find it.\n\nHere the router is KnxIpGateway with a KnxBuildingSimulator on its KnxBus, and the visualisation is a KnxTunnelClient. Writes are refused until the write lock is opened; reading and monitoring stay allowed.",
        "KNX adalah bus gedung untuk pencahayaan, tirai, dan pemanas. Perangkat di jalur twisted-pair punya individual address (area.line.device, misalnya 1.1.4) dan saling berbicara lewat group address (main/middle/sub, misalnya 3/0/1). Saklar dinding tidak mengalamati lampu: saklar menulis \"on\" ke group address 1/0/1, dan setiap aktuator yang mendengarkan 1/0/1 bereaksi. Setiap group address membawa satu datapoint type (DPT): 1.001 untuk saklar, 5.001 untuk persentase, 9.001 untuk suhu sebagai float 2-byte KNX.\n\nKNXnet/IP membawa jalur itu ke Ethernet. Antarmuka KNX IP menawarkan tunnelling: klien tersambung (CONNECT_REQUEST, mendapat channel dan individual address sendiri), menjaga sambungan dengan heartbeat setiap 60 dtk, dan bertukar frame cEMI — L_Data.req untuk mengirim, L_Data.con sebagai konfirmasi, L_Data.ind untuk semua yang terlihat di jalur — setiap tunnelling request diakui dengan nomor urutnya. Router KNX IP juga meneruskan setiap telegram sebagai ROUTING_INDICATION ke grup multicast 224.0.23.12, dan menjawab SEARCH_REQUEST agar alat bisa menemukannya.\n\nDi sini routernya adalah KnxIpGateway dengan KnxBuildingSimulator di KnxBus-nya, dan visualisasinya adalah KnxTunnelClient. Penulisan ditolak sampai kunci tulis dibuka; pembacaan dan pemantauan tetap diizinkan.");
    public override string Category => "Building";
    public override IReadOnlyList<string> Protocols => ["KNXnet/IP", "KNX TP1", "cEMI", "DPT"];
    public override Difficulty Difficulty => Difficulty.Intermediate;
    public override string DocsPath => "docs/en/protocols/knx.md";

    private static readonly IPEndPoint RouterAddress = new(IPAddress.Parse("10.50.0.2"), KnxIp.Port);
    private InMemoryDatagramNetwork? _net;
    private KnxBus? _bus;
    private KnxIpGateway? _router;
    private KnxTunnelClient? _tunnel;
    private CancellationTokenSource? _cts;

    /// <summary>The floor (null while stopped).</summary>
    public KnxBuildingSimulator? Floor { get; private set; }

    public ObservableCollection<KnxMonitorRow> Monitor { get; } = [];

    [ObservableProperty] private int _selectedRoom = 1;
    [ObservableProperty] private bool _writesAllowed;
    [ObservableProperty] private double _dimTarget = 80;
    [ObservableProperty] private double _blindTarget = 50;
    [ObservableProperty] private double _setpointTarget = 21;
    [ObservableProperty] private string _tunnelInfo = "";
    [ObservableProperty] private long _telegrams;

    /// <summary>Raised when the floor plan should redraw.</summary>
    public event Action? Changed;

    protected override async Task OnStartAsync()
    {
        _cts = new CancellationTokenSource();
        Monitor.Clear();
        _net = new InMemoryDatagramNetwork { Latency = TimeSpan.FromMilliseconds(5) };
        _bus = new KnxBus();
        Floor = new KnxBuildingSimulator(_bus);
        Floor.Changed += () => Changed?.Invoke();
        _router = KnxIpGateway.Create(o => { o.UseInMemory(_net, RouterAddress); o.Bus = _bus; o.FriendlyName = "Floor 3 KNX IP Router"; });
        await _router.StartAsync();
        var found = await KnxDiscovery.SearchAsync(_net.Bind(), TimeSpan.FromMilliseconds(200));
        AddLog(found.Count > 0 ? $"SEARCH_RESPONSE: {found[0]}" : "no KNX IP interface found");

        _tunnel = KnxTunnelClient.Create(o =>
        {
            o.UseInMemory(_net).UseGateway(RouterAddress);
            o.AllowWrites();   // the demo's write lock sits in front of the tunnel's writes
        });
        _tunnel.AddTap(Tap);
        _tunnel.GroupValueReceived += OnTelegram;
        await _tunnel.ConnectAsync();
        TunnelInfo = $"tunnel channel {_tunnel.Channel} · {_tunnel.Address} via {RouterAddress}";
        _ = TickAsync(_cts.Token);

        if (Environment.GetEnvironmentVariable("IOTCOM_GALLERY_SCREENSHOT") == "1")
        {
            WritesAllowed = true;
            (SelectedRoom, DimTarget) = (1, 70);
            await DimAsync();
            (SelectedRoom, BlindTarget) = (2, 60);
            await BlindAsync();
            SelectedRoom = 4;
            await LightAsync(true);
            (SelectedRoom, SetpointTarget) = (3, 23.5);
            await SetpointAsync();
            Floor.Step(10);
        }

        SetStatus(new Text("KNX IP router 10.50.0.2:3671 (tunnelling and routing) · line 1.1 with switching, dimming and blind actuators, four room controllers, presence detectors and a weather station · 30 simulated seconds per second.",
            "Router KNX IP 10.50.0.2:3671 (tunnelling dan routing) · jalur 1.1 dengan aktuator saklar, dimmer, dan tirai, empat pengendali ruangan, detektor kehadiran, dan stasiun cuaca · 30 detik simulasi per detik."));
    }

    private void OnTelegram(KnxGroupEvent e)
    {
        var info = Floor?.Find(e.Destination);
        var value = e.Service == KnxApci.GroupValueRead ? "" : info is null ? e.Value.ToString() : KnxDpt.Format(info.Dpt, e.Value);
        var row = new KnxMonitorRow(e.Time.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture), e.Source.ToString(), e.Destination.ToString(),
            e.Service switch { KnxApci.GroupValueRead => "Read", KnxApci.GroupValueResponse => "Response", _ => "Write" }, value, info?.Name ?? "");
        Ui(() =>
        {
            Monitor.Insert(0, row);
            while (Monitor.Count > 80) Monitor.RemoveAt(Monitor.Count - 1);
            Telegrams = _bus?.Telegrams ?? 0;
        });
    }

    private async Task TickAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct);
                Floor?.Step(30);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    partial void OnSelectedRoomChanged(int value)
    {
        if (Floor?.Rooms.FirstOrDefault(r => r.Number == value) is not { } room) return;
        (DimTarget, BlindTarget, SetpointTarget) = (room.LightOn ? room.Dim : 80, room.BlindTarget, room.Setpoint);
        Changed?.Invoke();
        _ = ReadRoomAsync(value);
    }

    /// <summary>Reads the selected room's temperature over the tunnel (allowed in read-only mode).</summary>
    private async Task ReadRoomAsync(int room)
    {
        if (_tunnel is null) return;
        try
        {
            var t = await _tunnel.ReadAsync($"3/0/{room}", "9.001");
            AddLog($"GroupValueRead 3/0/{room} → Response {t:0.0} °C");
        }
        catch (IoTComException ex)
        {
            AddLog($"Read 3/0/{room}: {ex.Message}");
        }
    }

    private async Task Guarded(string what, string address, string dpt, object value)
    {
        if (_tunnel is null) return;
        if (!WritesAllowed)
        {
            AddLog($"{what} ({address}): refused — the write lock is closed (read-only visualisation)");
            return;
        }

        try
        {
            await _tunnel.WriteAsync(address, dpt, value);
            AddLog($"GroupValueWrite {address} = {KnxDpt.Format(dpt, KnxDpt.Encode(dpt, value))} ({what}) → L_Data.con OK");
        }
        catch (IoTComException ex)
        {
            AddLog($"{what} ({address}): {ex.Message}");
        }
    }

    public Task LightAsync(bool on) => Guarded(on ? "light on" : "light off", $"1/0/{SelectedRoom}", "1.001", on);

    public Task DimAsync() => Guarded("dim", $"1/2/{SelectedRoom}", "5.001", Math.Round(DimTarget));

    public Task BlindAsync() => Guarded("blind", $"2/1/{SelectedRoom}", "5.001", Math.Round(BlindTarget));

    public Task BlindUpDownAsync(bool down) => Guarded(down ? "blind down" : "blind up", $"2/0/{SelectedRoom}", "1.008", down);

    public Task SetpointAsync() => Guarded("set point", $"3/1/{SelectedRoom}", "9.001", Math.Round(SetpointTarget * 2) / 2);

    public Task EconomyAsync() => Guarded("economy mode", $"3/2/{SelectedRoom}", "20.102", 3);

    protected override async Task OnStopAsync()
    {
        if (_cts is not null) await _cts.CancelAsync();
        if (_tunnel is not null) await _tunnel.DisposeAsync();
        if (_router is not null) await _router.DisposeAsync();
        if (_bus is not null) await _bus.DisposeAsync();
        (_tunnel, _router, _bus, _net, Floor) = (null, null, null, null, null);
        _cts?.Dispose();
        _cts = null;
        WritesAllowed = false;
        TunnelInfo = "";
        Status = "";
        Changed?.Invoke();
    }
}
