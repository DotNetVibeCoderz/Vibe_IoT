using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using IoTCom.Net.Gallery.Infrastructure;
using IoTCom.Net.Transport.Ble;

namespace IoTCom.Net.Gallery.Demos;

/// <summary>
/// Bluetooth Low Energy around a greenhouse office: a central scans a virtual radio, places devices on a proximity
/// radar by RSSI, follows a heart-rate strap and an environmental sensor through notifications, and switches a smart
/// plug only when writes are allowed.
/// </summary>
public sealed partial class BleDemo : GalleryDemo
{
    public override string Id => "ble-nearby";
    public override Text Title => new("Nearby Bluetooth devices", "Perangkat Bluetooth di sekitar");
    public override Text Summary => new(
        "Scan for BLE advertisements, see who is close by signal strength, subscribe to a heart-rate strap and a greenhouse sensor, and switch a smart plug only after allowing writes.",
        "Pindai advertisement BLE, lihat siapa yang dekat dari kekuatan sinyal, subscribe ke tali detak jantung dan sensor rumah kaca, dan nyalakan smart plug hanya setelah penulisan diizinkan.");
    public override Text Docs => new(
        "A BLE peripheral announces itself with advertisements: short packets with its name, the services it offers, manufacturer data (an iBeacon is Apple manufacturer data) and the transmit power. A central scans them and estimates distance from RSSI.\n\nTo talk to a device the central connects and discovers its GATT database: services contain characteristics, each with properties (read, write, notify). Standard ones have 16-bit UUIDs on the Bluetooth base — 0x2A37 is the Heart Rate Measurement, 0x2A6E the temperature in hundredths of a degree. Notifications push new values without polling.\n\nIoTCom.Net reaches the real radio through a Rust library built on btleplug (WinRT, BlueZ, CoreBluetooth). This demo uses the virtual radio, which offers the same API with simulated peripherals.",
        "Periferal BLE mengumumkan dirinya dengan advertisement: paket pendek berisi nama, layanan yang ditawarkan, data pabrikan (iBeacon adalah data pabrikan Apple), dan daya pancar. Central memindainya dan memperkirakan jarak dari RSSI.\n\nUntuk berbicara dengan perangkat, central tersambung dan menemukan basis data GATT-nya: layanan berisi characteristic, masing-masing dengan properti (read, write, notify). Yang standar punya UUID 16-bit di atas base Bluetooth — 0x2A37 adalah Heart Rate Measurement, 0x2A6E suhu dalam perseratus derajat. Notifikasi mendorong nilai baru tanpa polling.\n\nIoTCom.Net menjangkau radio sungguhan lewat pustaka Rust di atas btleplug (WinRT, BlueZ, CoreBluetooth). Demo ini memakai radio virtual, yang menawarkan API yang sama dengan periferal simulasi.");
    public override string Category => "Building";
    public override IReadOnlyList<string> Protocols => ["Bluetooth LE", "GATT", "iBeacon"];
    public override Difficulty Difficulty => Difficulty.Beginner;
    public override string DocsPath => "docs/en/protocols/ble.md";

    private VirtualBleNetwork? _network;
    private BleCentral? _central;
    private BleCentral? _writer;
    private BlePeripheral? _plug;
    private BlePeripheral? _writerPlug;
    private CancellationTokenSource? _cts;

    /// <summary>Devices seen, strongest first.</summary>
    public ObservableCollection<BleDeviceRow> Devices { get; } = [];

    /// <summary>Heart rate trend.</summary>
    public TrendBuffer HeartTrend { get; } = new(60, 20);

    [ObservableProperty] private int _heartRate;
    [ObservableProperty] private string _rr = "";
    [ObservableProperty] private string _temperature = "–";
    [ObservableProperty] private string _humidity = "–";
    [ObservableProperty] private string _pressure = "–";
    [ObservableProperty] private bool _plugOn;
    [ObservableProperty] private int _plugWatts;
    [ObservableProperty] private bool _allowWrite;
    [ObservableProperty] private string _lastAction = "";

    /// <summary>Raised when the radar should redraw.</summary>
    public event Action? Changed;

    protected override async Task OnStartAsync()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Devices.Clear();
        _network = new VirtualBleNetwork();
        _network.AddHeartRateStrap();
        _network.AddEnvironmentSensor();
        _network.AddBeacon();
        _network.AddSmartPlug();
        var sim = new VirtualBleSimulator(_network);
        _central = BleCentral.Create(o => { o.UseVirtual(_network); o.ReadOnly = true; });
        _central.AddTap(Tap);
        await _central.ConnectAsync(ct);

        _ = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    sim.Step();
                    await Task.Delay(1000, ct);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, ct);

        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var ad in _central.WatchAsync(null, ct)) Ui(() => Seen(ad));
            }
            catch (OperationCanceledException)
            {
            }
        }, ct);

        await Task.Delay(300, ct);
        var strap = await _central.OpenAsync("C4:7C:8D:6A:21:0F", ct);
        var sensor = await _central.OpenAsync("E8:4F:25:10:7A:33", ct);
        var plug = _plug = await _central.OpenAsync("D0:8E:3A:55:10:C2", ct);
        Follow(strap, BleUuid.FromShort(0x2A37), v =>
        {
            var hr = GattValue.ParseHeartRate(v);
            HeartTrend.Add(hr.BeatsPerMinute);
            Ui(() =>
            {
                HeartRate = hr.BeatsPerMinute;
                Rr = hr.RrIntervals.Count > 0 ? $"RR {hr.RrIntervals[0]:0.000} s" : "";
            });
        }, ct);
        Follow(sensor, BleUuid.FromShort(0x2A6E), v => Ui(() => Temperature = GattValue.Describe(BleUuid.FromShort(0x2A6E), v)), ct);
        Follow(sensor, BleUuid.FromShort(0x2A6F), v => Ui(() => Humidity = GattValue.Describe(BleUuid.FromShort(0x2A6F), v)), ct);
        Pressure = GattValue.Describe(BleUuid.FromShort(0x2A6D), await sensor.ReadAsync(BleUuid.FromShort(0x2A6D), ct));
        Follow(plug, VirtualBleNetwork.SmartPlugPower, v => Ui(() => PlugWatts = BitConverter.ToUInt16(v)), ct);
        SetStatus(new Text("Virtual radio · 4 peripherals (heart-rate strap, greenhouse sensor, smart plug, iBeacon) · values change every second.",
            "Radio virtual · 4 periferal (tali detak jantung, sensor rumah kaca, smart plug, iBeacon) · nilai berubah tiap detik."));
    }

    private static void Follow(BlePeripheral p, Guid characteristic, Action<byte[]> onValue, CancellationToken ct) => _ = Task.Run(async () =>
    {
        try
        {
            await foreach (var v in p.SubscribeAsync(characteristic, ct)) onValue(v);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IoTComException or InvalidOperationException)
        {
        }
    }, ct);

    private void Seen(BleAdvertisement ad)
    {
        var row = Devices.FirstOrDefault(d => d.Id == ad.Id);
        var updated = new BleDeviceRow(ad);
        if (row is null) Devices.Add(updated);
        else Devices[Devices.IndexOf(row)] = updated;
        Changed?.Invoke();
    }

    /// <summary>Switches the smart plug's relay (refused by the read-only central unless writes are allowed).</summary>
    public async Task TogglePlugAsync()
    {
        if (_network is null || _central is null) return;
        try
        {
            var target = _plug!;
            if (AllowWrite)
            {
                if (_writerPlug is null)
                {
                    _writer = BleCentral.Create(o => o.UseVirtual(_network));
                    _writer.AddTap(Tap);
                    await _writer.ConnectAsync();
                    await _writer.ScanAsync(TimeSpan.FromMilliseconds(300));
                    _writerPlug = await _writer.OpenAsync("D0:8E:3A:55:10:C2");
                }

                target = _writerPlug;
            }

            await target.WriteAsync(VirtualBleNetwork.SmartPlugRelay, new[] { (byte)(PlugOn ? 0 : 1) });
            PlugOn = !PlugOn;
            LastAction = Loc.L($"Wrote relay = {(PlugOn ? 1 : 0)} to 6E400002…", $"Menulis relay = {(PlugOn ? 1 : 0)} ke 6E400002…");
        }
        catch (ReadOnlyModeException)
        {
            LastAction = Loc.L("Refused by the central: it is read-only. Tick “Allow writes” first.", "Ditolak oleh central: ia hanya-baca. Centang “Izinkan penulisan” dulu.");
        }
    }

    protected override async Task OnStopAsync()
    {
        if (_cts is not null) await _cts.CancelAsync();
        if (_writer is not null) await _writer.DisposeAsync();
        if (_central is not null) await _central.DisposeAsync();
        (_writer, _central, _network, _plug, _writerPlug) = (null, null, null, null, null);
        _cts?.Dispose();
        _cts = null;
        PlugOn = false;
        AllowWrite = false;
        Status = "";
    }
}

/// <summary>A device on the radar.</summary>
public sealed record BleDeviceRow(string Id, string Name, int Rssi, string Kind, double? Distance)
{
    public BleDeviceRow(BleAdvertisement ad) : this(ad.Id, ad.Name ?? ad.Id, ad.Rssi ?? -100,
        ad.IBeacon is not null ? "iBeacon" : ad.Services.Count > 0 ? BleUuid.Name(ad.Services[0]) : "–", ad.EstimatedDistance)
    {
    }

    public string Line => $"{Rssi} dBm{(Distance is { } d ? $" · ≈{d:0.0} m" : "")}";
}
