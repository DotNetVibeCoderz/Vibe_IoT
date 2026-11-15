using System.Collections.ObjectModel;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using IoTCom.Net.Adapters.Bacnet;
using IoTCom.Net.Gallery.Infrastructure;
using IoTCom.Net.Protocols.Bacnet;
using IoTCom.Net.Transports;

namespace IoTCom.Net.Gallery.Demos;

/// <summary>One BACnet point as the BMS sees it.</summary>
public sealed partial class BmsPoint : ObservableObject
{
    internal BmsPoint(BacnetObjectId id, string name) => (Id, Name) = (id, name);

    public BacnetObjectId Id { get; }
    public string Name { get; }
    public string Short => Id.ShortName;

    [ObservableProperty] private string _value = "";
    [ObservableProperty] private string _priority = "";
    [ObservableProperty] private bool _overridden;
}

/// <summary>
/// A BMS front end over BACnet/IP: the client adapter (ela-compil's library) discovers IoTCom's own BacnetDevice running
/// an air handling unit, subscribes to COV on every point, and commands the fan, set points and valves at priority 8
/// behind a write lock; relinquishing hands control back to the unit's loops.
/// </summary>
public sealed partial class BacnetAhuDemo : GalleryDemo
{
    public override string Id => "bacnet-ahu";
    public override Text Title => new("Air handling unit over BACnet", "AHU lewat BACnet");
    public override Text Summary => new(
        "A BMS front end finds an air handling unit with Who-Is, reads its objects, follows every point by COV, and commands the fan, set points and valves at priority 8 behind a write lock; relinquishing hands control back to the unit's own loops.",
        "Front end BMS menemukan air handling unit dengan Who-Is, membaca objeknya, mengikuti setiap titik lewat COV, dan memerintah kipas, set point, serta katup pada prioritas 8 di balik kunci tulis; relinquish mengembalikan kendali ke loop milik unit.");
    public override Text Docs => new(
        "BACnet (ASHRAE 135) is the protocol of building management systems. A device holds objects — Analog Input, Analog Output, Analog Value, Binary Input/Output/Value, Multi-state Value and more — and each object has properties such as Present_Value, Units and Status_Flags. A BMS finds devices with Who-Is (devices answer I-Am), reads properties with ReadProperty or ReadPropertyMultiple, and asks for change-of-value notifications with SubscribeCOV, so it hears about a temperature only when it moves by the object's COV increment.\n\nCommandable objects (outputs, and values that allow it) do not just hold a value: they have a 16-slot priority array. Every writer chooses a priority — 1 and 2 for life safety, 5 for critical equipment protection, 8 for a manual operator, 16 for the default schedule — and the present value is the highest-priority slot that is not NULL. Writing NULL at your priority relinquishes it; when every slot is empty the Relinquish_Default applies. That is how an operator overrides a fan without fighting the controller, and how the override is handed back.\n\nHere the BMS is IoTCom's BacnetIpClient, an adapter over the mature ela-compil BACnet library, and the unit is IoTCom's own BacnetDevice (BacnetAhuSimulator) — two independent BACnet implementations talking over BACnet/IP. The unit's valve and damper follow their own control loops while nothing overrides them.",
        "BACnet (ASHRAE 135) adalah protokol sistem manajemen gedung. Sebuah device memiliki objek — Analog Input, Analog Output, Analog Value, Binary Input/Output/Value, Multi-state Value, dan lainnya — dan setiap objek punya properti seperti Present_Value, Units, dan Status_Flags. BMS menemukan device dengan Who-Is (device menjawab I-Am), membaca properti dengan ReadProperty atau ReadPropertyMultiple, dan meminta notifikasi change-of-value dengan SubscribeCOV, sehingga BMS hanya mendengar suhu saat suhu itu berubah sebesar COV increment objeknya.\n\nObjek yang dapat diperintah (output, dan value yang mengizinkannya) tidak sekadar menyimpan nilai: objek itu punya priority array 16 slot. Setiap penulis memilih prioritas — 1 dan 2 untuk keselamatan jiwa, 5 untuk perlindungan peralatan kritis, 8 untuk operator manual, 16 untuk jadwal bawaan — dan present value adalah slot berprioritas tertinggi yang tidak NULL. Menulis NULL pada prioritas Anda berarti me-relinquish-nya; saat semua slot kosong, Relinquish_Default berlaku. Begitulah operator mengambil alih kipas tanpa berebut dengan pengendali, dan begitulah kendali dikembalikan.\n\nDi sini BMS-nya adalah BacnetIpClient milik IoTCom, adapter di atas pustaka BACnet ela-compil yang matang, dan unitnya adalah BacnetDevice milik IoTCom sendiri (BacnetAhuSimulator) — dua implementasi BACnet independen yang berbicara lewat BACnet/IP. Katup dan damper unit mengikuti loop kendalinya sendiri selama tidak ada yang mengambil alih.");
    public override string Category => "Building";
    public override IReadOnlyList<string> Protocols => ["BACnet/IP", "ASHRAE 135", "COV", "UDP 47808"];
    public override Difficulty Difficulty => Difficulty.Advanced;
    public override string DocsPath => "docs/en/protocols/bacnet.md";

    private static readonly IPEndPoint Group = new(IPAddress.Parse("239.255.186.192"), BacnetDeviceOptions.DefaultPort);
    private const uint DeviceInstance = 2604;
    private InMemoryDatagramNetwork? _net;
    private BacnetIpClient? _bms;
    private readonly List<IAsyncDisposable> _subscriptions = [];
    private CancellationTokenSource? _cts;

    /// <summary>The unit (null while stopped).</summary>
    public BacnetAhuSimulator? Ahu { get; private set; }

    public ObservableCollection<BmsPoint> Points { get; } = [];

    [ObservableProperty] private bool _writesAllowed;
    [ObservableProperty] private double _setpointTarget = 13;
    [ObservableProperty] private double _valveTarget = 60;
    [ObservableProperty] private long _covCount;

    /// <summary>Raised when the schematic should redraw.</summary>
    public event Action? Changed;

    protected override async Task OnStartAsync()
    {
        _cts = new CancellationTokenSource();
        Points.Clear();
        _net = new InMemoryDatagramNetwork { Latency = TimeSpan.FromMilliseconds(5) };
        Ahu = BacnetAhuSimulator.Create(o => { o.UseInMemory(_net, new IPEndPoint(IPAddress.Parse("10.60.0.2"), BacnetDeviceOptions.DefaultPort)); o.BroadcastAddress = Group; });
        foreach (var o in Ahu.Device.Objects) o.Changed += (_, _) => Changed?.Invoke();
        await Ahu.StartAsync();

        _bms = BacnetIpClient.Create(o =>
        {
            o.UseInMemory(_net, new IPEndPoint(IPAddress.Parse("10.60.0.20"), BacnetDeviceOptions.DefaultPort));
            o.BroadcastAddress = Group;
            o.Timeout = TimeSpan.FromSeconds(2);
            o.AllowWrites();   // the demo's write lock sits in front of the client's writes
        });
        _bms.AddTap(Tap);
        await _bms.ConnectAsync();
        var found = await _bms.DiscoverAsync(TimeSpan.FromMilliseconds(300));
        AddLog(found.Count > 0 ? $"Who-Is → I-Am {found[0]}" : "no device answered Who-Is");

        foreach (var id in (await _bms.ReadObjectListAsync(DeviceInstance)).Where(o => o.Type != BacnetObjectType.Device))
        {
            var r = await _bms.ReadPropertiesAsync(DeviceInstance, id, [BacnetPropertyId.ObjectName]);
            var point = new BmsPoint(id, r[0].Values![0].ToString());
            Points.Add(point);
            await RefreshAsync(point);
            _subscriptions.Add(await _bms.SubscribeCovAsync(DeviceInstance, id, n => OnCov(point, n), confirmed: false, lifetime: 600));
        }

        AddLog($"ReadPropertyMultiple of {Points.Count} objects; SubscribeCOV on each");
        _ = TickAsync(_cts.Token);
        if (Environment.GetEnvironmentVariable("IOTCOM_GALLERY_SCREENSHOT") == "1")
        {
            WritesAllowed = true;
            SetpointTarget = 12.5;
            await SetpointAsync();
            ValveTarget = 75;
            await ValveOverrideAsync();
            for (var i = 0; i < 6; i++) Ahu.Step(10);
        }

        SetStatus(new Text("BACnet/IP device 2604 \"AHU-1 Lantai 3\" at 10.60.0.2:47808 (IoTCom BacnetDevice) · BMS client 10.60.0.20 (adapter over ela-compil BACnet) · Who-Is over the multicast group 239.255.186.192 · 10 simulated seconds per second.",
            "Device BACnet/IP 2604 \"AHU-1 Lantai 3\" di 10.60.0.2:47808 (BacnetDevice IoTCom) · klien BMS 10.60.0.20 (adapter di atas BACnet ela-compil) · Who-Is lewat grup multicast 239.255.186.192 · 10 detik simulasi per detik."));
    }

    private void OnCov(BmsPoint point, BacnetCovNotification n)
    {
        var pv = n.Values.FirstOrDefault(v => v.Reference.Property == BacnetPropertyId.PresentValue);
        Ui(() =>
        {
            CovCount++;
            if (pv is not null && Ahu?.Device.Find(point.Id) is { } obj) point.Value = obj.FormatValue();
        });
    }

    private async Task RefreshAsync(BmsPoint point)
    {
        if (_bms is null || Ahu?.Device.Find(point.Id) is not { } obj) return;
        var props = obj.Commandable ? new[] { BacnetPropertyId.PresentValue, BacnetPropertyId.CurrentCommandPriority } : [BacnetPropertyId.PresentValue];
        var r = await _bms.ReadPropertiesAsync(DeviceInstance, point.Id, props);
        Ui(() =>
        {
            point.Value = obj.FormatValue();
            point.Priority = obj.Commandable ? r.Count > 1 && r[1].Ok && r[1].Values![0].Tag != BacnetApplicationTag.Null ? $"@{r[1].Values![0]}" : "auto" : "";
            point.Overridden = obj.Commandable && r.Count > 1 && r[1].Ok && r[1].Values![0].Tag != BacnetApplicationTag.Null;
        });
    }

    private async Task TickAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct);
                Ahu?.Step(10);
                Changed?.Invoke();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task Command(string what, string obj, BacnetValue value)
    {
        if (_bms is null) return;
        if (!WritesAllowed)
        {
            AddLog($"{what}: refused — the write lock is closed (read-only BMS)");
            return;
        }

        var id = BacnetObjectId.Parse(obj);
        try
        {
            await _bms.WritePropertyAsync(DeviceInstance, id, BacnetPropertyId.PresentValue, value, 8);
            AddLog($"WriteProperty {id.ShortName}.Present_Value = {value} @ priority 8 ({what}) → SimpleACK");
        }
        catch (IoTComException ex)
        {
            AddLog($"{what}: {ex.Message}");
        }

        if (Points.FirstOrDefault(p => p.Id == id) is { } point) await RefreshAsync(point);
        Changed?.Invoke();
    }

    public Task FanAsync(bool start) => Command(start ? "start the fan" : "stop the fan", "BO1", BacnetValue.Enumerated(start ? 1u : 0u));

    public Task RelinquishFanAsync() => Command("relinquish the fan", "BO1", BacnetValue.Null);

    public Task SetpointAsync() => Command("supply set point", "AV1", BacnetValue.Real((float)Math.Round(SetpointTarget * 2) / 2));

    public Task ValveOverrideAsync() => Command("override the chilled-water valve", "AO1", BacnetValue.Real((float)Math.Round(ValveTarget)));

    public Task ValveAutoAsync() => Command("valve back to automatic", "AO1", BacnetValue.Null);

    public Task OccupancyAsync(uint state) => Command("occupancy mode", "MSV1", BacnetValue.Unsigned(state));

    protected override async Task OnStopAsync()
    {
        if (_cts is not null) await _cts.CancelAsync();
        foreach (var s in _subscriptions) await s.DisposeAsync();
        _subscriptions.Clear();
        if (_bms is not null) await _bms.DisposeAsync();
        if (Ahu is not null) await Ahu.DisposeAsync();
        (_bms, Ahu, _net) = (null, null, null);
        _cts?.Dispose();
        _cts = null;
        WritesAllowed = false;
        Status = "";
        Changed?.Invoke();
    }
}
