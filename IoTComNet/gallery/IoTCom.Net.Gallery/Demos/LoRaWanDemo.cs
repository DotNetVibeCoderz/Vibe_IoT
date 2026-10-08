using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using IoTCom.Net.Gallery.Infrastructure;
using IoTCom.Net.Protocols.LoRaWan;
using IoTCom.Net.Transports;

namespace IoTCom.Net.Gallery.Demos;

/// <summary>
/// LoRaWAN network monitor: two gateways on a city map forward Semtech UDP to a light network server; four sensors
/// join over the air and report. Every uplink ripples out to the gateways that heard it (RSSI/SNR), every downlink
/// lands in RX1. Drag a sensor away and watch its spreading factor — and its time on air — grow until it is lost.
/// </summary>
public sealed partial class LoRaWanDemo : GalleryDemo
{
    public override string Id => "lorawan-network";
    public override Text Title => new("LoRaWAN network monitor", "Monitor jaringan LoRaWAN");
    public override Text Summary => new(
        "Sensors join over the air and report through two gateways to a light network server. Drag a sensor across the map: its link margin falls, its spreading factor climbs, and every uplink costs more airtime.",
        "Sensor join over the air dan melapor lewat dua gateway ke network server ringan. Seret sensor di peta: margin link-nya turun, spreading factor-nya naik, dan setiap uplink memakan airtime lebih lama.");
    public override Text Docs => new(
        "LoRaWAN trades speed for range. A sensor transmits a few bytes with a spreading factor between SF7 and SF12: every step doubles the time on air and lets the receiver dig about 2.5 dB deeper below the noise. The rings on the map show where each spreading factor still has a 6 dB margin.\n\nAny gateway that hears a frame forwards it — a JSON rxpk with RSSI, SNR and a microsecond timestamp — over the Semtech UDP protocol. The network server keeps one copy per uplink (deduplication), checks the MIC with the session key, rejects replayed frame counters and decrypts the payload.\n\nJoining is a handshake: the device sends a Join-Request signed with its AppKey; five seconds later the server answers through the best gateway with an encrypted Join-Accept, and both sides derive the same session keys.\n\nClass A devices listen only right after they transmit: a downlink waits in a queue and goes out exactly one second after the next uplink (RX1). Select a sensor and change its reporting interval, or ask for its battery with a MAC command.",
        "LoRaWAN menukar kecepatan dengan jangkauan. Sensor memancarkan beberapa byte dengan spreading factor antara SF7 dan SF12: setiap langkah menggandakan waktu di udara dan membuat penerima mampu menggali sekitar 2,5 dB lebih dalam di bawah derau. Lingkaran di peta menunjukkan di mana setiap spreading factor masih punya margin 6 dB.\n\nGateway mana pun yang mendengar sebuah frame meneruskannya — JSON rxpk berisi RSSI, SNR, dan timestamp mikrodetik — lewat protokol Semtech UDP. Network server menyimpan satu salinan per uplink (deduplikasi), memeriksa MIC dengan session key, menolak frame counter yang diputar ulang, dan mendekripsi payload.\n\nJoin adalah jabat tangan: perangkat mengirim Join-Request yang ditandatangani AppKey-nya; lima detik kemudian server menjawab lewat gateway terbaik dengan Join-Accept terenkripsi, dan kedua sisi menurunkan session key yang sama.\n\nPerangkat Class A hanya mendengar sesaat setelah mengirim: downlink menunggu di antrean dan dipancarkan tepat satu detik setelah uplink berikutnya (RX1). Pilih sebuah sensor lalu ubah interval laporannya, atau minta status baterainya dengan MAC command.");
    public override string Category => "Lpwan";
    public override IReadOnlyList<string> Protocols => ["LoRaWAN", "Semtech UDP", "Cayenne LPP", "AS923-2"];
    public override Difficulty Difficulty => Difficulty.Intermediate;
    public override string DocsPath => "docs/en/protocols/lorawan.md";

    private static readonly IPEndPoint ServerAddress = new(IPAddress.Parse("10.0.0.1"), 1700);
    private InMemoryDatagramNetwork? _network;
    private LoRaWanNetworkServer? _server;
    private LoRaWanSimulator? _sim;

    /// <summary>Everything the map draws, refreshed by the radio events.</summary>
    public RadioScene Scene { get; } = new();

    /// <summary>Uplinks and downlinks, newest first.</summary>
    public ObservableCollection<AirRow> Air { get; } = [];

    /// <summary>One row per device, as the network server sees it.</summary>
    public ObservableCollection<DeviceRow> DeviceRows { get; } = [];

    [ObservableProperty] private string? _selectedDevice;
    [ObservableProperty] private long _uplinks;
    [ObservableProperty] private long _lostUplinks;
    [ObservableProperty] private long _downlinks;
    [ObservableProperty] private long _duplicatesDropped;
    [ObservableProperty] private double _airtimeSeconds;
    [ObservableProperty] private string _lastAction = "";

    partial void OnSelectedDeviceChanged(string? value) => Scene.Selected = value;

    protected override async Task OnStartAsync()
    {
        // 1) The backhaul: gateways and the network server share an in-memory "UDP" network.
        _network = new InMemoryDatagramNetwork(seed: 5);
        _server = LoRaWanNetworkServer.Create(o =>
        {
            o.UseInMemory(_network, ServerAddress);
            o.Region = LoRaRegion.AS923Group2;
        });
        _server.AddTap(Tap);
        _server.UplinkReceived += OnUplink;
        _server.DeviceJoined += (_, d) => AddLog(L("joined", "join") + $" · {d.Name} → DevAddr {d.DevAddr}");
        _server.FrameRejected += (_, reason) => AddLog(reason);

        // 2) The radio: two gateways (real Semtech packet forwarders) and four sensors in Jakarta (AS923-2).
        var options = LoRaWanSimulatorOptions.Demo(ServerAddress, LoRaRegion.AS923Group2);
        options.GatewayTransportFactory = () => _network.Bind();
        options.KeepAliveInterval = TimeSpan.FromSeconds(5);
        for (var i = 0; i < options.Devices.Count; i++)
            options.Devices[i] = options.Devices[i] with { Interval = TimeSpan.FromSeconds(8 + (3 * i)) };
        _sim = new LoRaWanSimulator(options);
        foreach (var r in _sim.Registrations) _server.AddDevice(r);
        _sim.RadioActivity += OnRadio;

        Scene.Reset(options.Gateways, _sim.Devices);
        Ui(() =>
        {
            DeviceRows.Clear();
            foreach (var d in _sim.Devices) DeviceRows.Add(new DeviceRow(d.Name, d.Options.Sensor));
            Air.Clear();
            SelectedDevice ??= "water-12";
        });

        await _server.StartAsync();
        await _sim.StartAsync();
        SetStatus(new Text("AS923-2 · Semtech UDP to 10.0.0.1:1700 · joins answer after 5 s (JOIN_ACCEPT_DELAY1), downlinks 1 s after the uplink (RX1).",
            "AS923-2 · Semtech UDP ke 10.0.0.1:1700 · join dijawab setelah 5 detik (JOIN_ACCEPT_DELAY1), downlink 1 detik setelah uplink (RX1)."));
    }

    private void OnRadio(object? sender, LoRaWanRadioEvent e)
    {
        var device = _sim?.Devices.FirstOrDefault(d => d.Name == e.Device);
        Scene.Add(e, device);
        var row = AirRow.From(e, device);
        Ui(() =>
        {
            Air.Insert(0, row);
            while (Air.Count > 14) Air.RemoveAt(Air.Count - 1);
            if (e.Uplink)
            {
                Uplinks++;
                if (e.Receptions.Count == 0) LostUplinks++;
                DuplicatesDropped += Math.Max(0, e.Receptions.Count - 1);
                AirtimeSeconds += e.Airtime.TotalSeconds;
            }
            else
            {
                Downlinks++;
            }

            if (device is not null && DeviceRows.FirstOrDefault(r => r.Name == device.Name) is { } dr)
                dr.UpdateRadio(device, e);
        });
    }

    private void OnUplink(object? sender, LoRaWanUplink up)
    {
        Ui(() =>
        {
            if (DeviceRows.FirstOrDefault(r => r.Name == up.Device.Name) is { } row) row.UpdateServer(up);
        });
    }

    /// <summary>Moves a sensor on the map (km east/north of the rooftop gateway).</summary>
    public void MoveDevice(string name, double x, double y)
    {
        _sim?.MoveDevice(name, x, y);
        Scene.Invalidate();
    }

    /// <summary>Queues a downlink on FPort 10 (reporting interval) for the selected sensor and wakes it.</summary>
    public void SetInterval(int seconds)
    {
        if (_server is null || _sim is null || SelectedDevice is not { } name) return;
        var device = _sim.Devices.First(d => d.Name == name);
        _server.EnqueueDownlink(device.Options.DevEui, 10, [(byte)(seconds >> 8), (byte)seconds]);
        _sim.TriggerUplink(name);
        LastAction = L($"Queued for {name}: FPort 10 = {seconds} s. It goes out in RX1, one second after the uplink this triggers.",
            $"Antre untuk {name}: FPort 10 = {seconds} dtk. Dipancarkan di RX1, satu detik setelah uplink yang dipicu ini.");
    }

    /// <summary>Queues DevStatusReq; the answer (battery, margin) rides on the following uplink.</summary>
    public void AskStatus()
    {
        if (_server is null || _sim is null || SelectedDevice is not { } name) return;
        var device = _sim.Devices.First(d => d.Name == name);
        _server.EnqueueMacCommand(device.Options.DevEui, LoRaWanMacCommand.DevStatusReq());
        _sim.TriggerUplink(name);
        LastAction = L($"DevStatusReq queued for {name}; DevStatusAns comes back in FOpts of the next uplink.",
            $"DevStatusReq antre untuk {name}; DevStatusAns kembali di FOpts uplink berikutnya.");
    }

    /// <summary>Makes the selected sensor report now.</summary>
    public void ReportNow()
    {
        if (_sim is null || SelectedDevice is not { } name) return;
        _sim.TriggerUplink(name);
        LastAction = L($"{name} transmits now.", $"{name} memancar sekarang.");
    }

    private static string L(string en, string id) => Loc.L(en, id);

    protected override async Task OnStopAsync()
    {
        if (_sim is not null)
        {
            _sim.RadioActivity -= OnRadio;
            await _sim.DisposeAsync();
        }

        if (_server is not null) await _server.DisposeAsync();
        (_sim, _server, _network) = (null, null, null);
        Status = "";
    }
}

/// <summary>One transmission in the air log.</summary>
public sealed record AirRow(string Time, bool Uplink, string Device, string DataRate, int SpreadingFactor, double AirtimeMs, string Heard, bool Lost, string Summary)
{
    internal static AirRow From(LoRaWanRadioEvent e, LoRaWanSimulatedDevice? device)
    {
        var sf = e.DataRate.Length > 2 ? LoRaDataRate.ParseDatr(e.DataRate).Sf : 7;
        var heard = e.Uplink
            ? string.Join("  ", e.Receptions.Select(r => $"{Short(r.Gateway)} {r.Rssi:0}/{r.Snr:+0.0;-0.0}"))
            : $"{Short(e.Gateway ?? "")} → {e.Device}";
        return new AirRow(e.Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture), e.Uplink, e.Device ?? "?", e.DataRate, sf,
            e.Airtime.TotalMilliseconds, heard, e.Uplink && e.Receptions.Count == 0, e.Summary);

        static string Short(string gateway) => gateway.StartsWith("gw-", StringComparison.Ordinal) ? gateway[3..] : gateway;
    }
}

/// <summary>A device as the dashboard shows it: radio side (simulator) and server side (network server).</summary>
public sealed partial class DeviceRow(string name, LoRaWanSensorKind sensor) : ObservableObject
{
    public string Name { get; } = name;

    public LoRaWanSensorKind Sensor { get; } = sensor;

    [ObservableProperty] private string _state = "joining";
    [ObservableProperty] private string _dataRate = "—";
    [ObservableProperty] private int _spreadingFactor;
    [ObservableProperty] private string _link = "";
    [ObservableProperty] private string _reading = "";
    [ObservableProperty] private string _counters = "";
    [ObservableProperty] private string _battery = "";

    internal void UpdateRadio(LoRaWanSimulatedDevice device, LoRaWanRadioEvent e)
    {
        if (!e.Uplink) return;
        DataRate = e.DataRate;
        SpreadingFactor = LoRaDataRate.ParseDatr(e.DataRate).Sf;
        Link = e.Receptions.Count == 0 ? Loc.L("lost — no gateway heard it", "hilang — tak ada gateway yang mendengar")
            : string.Join("  ", e.Receptions.Select(r => $"{r.Gateway[3..]} {r.Snr:+0.0;-0.0} dB"));
        State = device.Mac.IsActivated ? device.Mac.DevAddr.ToString() : Loc.L("joining", "join…");
    }

    internal void UpdateServer(LoRaWanUplink up)
    {
        Reading = LoRaWanSimulator.DescribePayload(up.FPort, up.Payload);
        Counters = $"FCnt {up.FCnt} · ↑{up.Device.UplinkCount} ↓{up.Device.DownlinkCount}";
        if (up.Device.Battery is { } b) Battery = $"{b * 100 / 254} %";
        State = up.Device.DevAddr.ToString();
    }
}

/// <summary>What the radio map renders: gateways, devices and recent transmissions (thread-safe snapshot).</summary>
public sealed class RadioScene
{
    private readonly Lock _gate = new();
    private readonly List<RadioPulse> _pulses = [];
    private List<LoRaWanSimulatedGateway> _gateways = [];
    private IReadOnlyList<LoRaWanSimulatedDevice> _devices = [];

    /// <summary>Raised when something changed (the map invalidates itself).</summary>
    public event Action? Changed;

    public string? Selected { get; set; }

    internal void Reset(IEnumerable<LoRaWanSimulatedGateway> gateways, IReadOnlyList<LoRaWanSimulatedDevice> devices)
    {
        lock (_gate)
        {
            _gateways = [.. gateways];
            _devices = devices;
            _pulses.Clear();
        }

        Changed?.Invoke();
    }

    internal void Add(LoRaWanRadioEvent e, LoRaWanSimulatedDevice? device)
    {
        if (device is null) return;
        lock (_gate)
        {
            _pulses.Add(new RadioPulse(DateTime.UtcNow, e, device.X, device.Y));
            _pulses.RemoveAll(p => DateTime.UtcNow - p.Start > TimeSpan.FromSeconds(6));
        }

        Changed?.Invoke();
    }

    internal void Invalidate() => Changed?.Invoke();

    internal (IReadOnlyList<LoRaWanSimulatedGateway> Gateways, IReadOnlyList<(string Name, double X, double Y, int Sf, bool Joined, LoRaWanSensorKind Kind)> Devices, IReadOnlyList<RadioPulse> Pulses) Snapshot()
    {
        lock (_gate)
        {
            return (_gateways,
                [.. _devices.Select(d => (d.Name, d.X, d.Y, LoRaRegion.AS923Group2.DataRates[Math.Clamp(d.Mac.DataRate, 0, 5)].SpreadingFactor, d.Mac.IsActivated, d.Options.Sensor))],
                [.. _pulses]);
        }
    }
}

/// <summary>One transmission being animated on the map.</summary>
internal sealed record RadioPulse(DateTime Start, LoRaWanRadioEvent Event, double X, double Y);
