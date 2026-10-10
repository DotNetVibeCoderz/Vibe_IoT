using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using IoTCom.Net.Gallery.Infrastructure;
using IoTCom.Net.Protocols.Coap;
using IoTCom.Net.Protocols.Lwm2m;
using IoTCom.Net.Transports;

namespace IoTCom.Net.Gallery.Demos;

/// <summary>One pole on the street as the server sees it.</summary>
public sealed partial class StreetPole : ObservableObject
{
    internal StreetPole(string serial, double x) => (Serial, X) = (serial, x);

    public string Serial { get; }
    public string Endpoint => $"urn:dev:light:{Serial}";
    internal double X { get; }
    internal Lwm2mStreetLightSimulator? Device { get; set; }

    [ObservableProperty] private bool _registered;
    [ObservableProperty] private bool _on;
    [ObservableProperty] private long _dimmer;
    [ObservableProperty] private double _temperature;
    [ObservableProperty] private string _expires = "";
}

/// <summary>
/// Street lighting managed over OMA LwM2M: four lights register with one server, which reads them, switches and dims
/// them (behind a write lock), reboots them, observes their driver temperature, and expires a light that loses power.
/// </summary>
public sealed partial class StreetLightDemo : GalleryDemo
{
    public override string Id => "lwm2m-street-lights";
    public override Text Title => new("Street lights over LwM2M", "Lampu jalan lewat LwM2M");
    public override Text Summary => new(
        "Four smart street lights register with an LwM2M server: switch and dim them, reboot one, watch their driver temperature arrive as notifications, and cut the power to one to see its registration expire.",
        "Empat lampu jalan pintar mendaftar ke server LwM2M: nyalakan dan redupkan, reboot salah satunya, lihat suhu driver tiba sebagai notifikasi, dan putus listrik salah satunya untuk melihat registrasinya kedaluwarsa.");
    public override Text Docs => new(
        "OMA LwM2M is device management for constrained devices, on top of CoAP. A device (the LwM2M client) registers with the server (POST /rd with its endpoint name, lifetime and the object instances it hosts), renews the registration before the lifetime ends, and deregisters when it shuts down. If the server hears nothing for a whole lifetime, the registration expires.\n\nEverything the device exposes is an object with numbered resources from the OMA registry: Device (3) has manufacturer, serial number and Reboot; Location (6) latitude and longitude; Temperature (3303) the sensor value; Light Control (3311) on/off, dimmer and energy. The server reaches them by path — /3311/0/5851 is the dimmer of the first light-control instance — with Read, Write, Execute, Discover and Observe. Values travel as TLV, SenML JSON or CBOR, or plain text for a single resource.\n\nObserve turns a resource into a stream: the device notifies on change, but not more often than pmin seconds and at least every pmax seconds (set with Write-Attributes). Here each light is an Lwm2mStreetLightSimulator (an Lwm2mClient with four objects) and the operator uses Lwm2mServer; writes and executes are refused until the write lock is opened.",
        "OMA LwM2M adalah manajemen perangkat untuk perangkat terbatas, di atas CoAP. Perangkat (klien LwM2M) mendaftar ke server (POST /rd dengan nama endpoint, lifetime, dan instance objek yang dimilikinya), memperbarui registrasi sebelum lifetime habis, dan membatalkan registrasi saat dimatikan. Jika server tidak mendengar apa pun selama satu lifetime penuh, registrasinya kedaluwarsa.\n\nSemua yang dibuka perangkat adalah objek dengan resource bernomor dari registry OMA: Device (3) berisi pabrikan, nomor seri, dan Reboot; Location (6) lintang dan bujur; Temperature (3303) nilai sensor; Light Control (3311) nyala/mati, dimmer, dan energi. Server menjangkaunya lewat path — /3311/0/5851 adalah dimmer instance light-control pertama — dengan Read, Write, Execute, Discover, dan Observe. Nilai dikirim sebagai TLV, SenML JSON atau CBOR, atau teks biasa untuk satu resource.\n\nObserve mengubah resource menjadi aliran: perangkat memberi notifikasi saat berubah, tetapi tidak lebih sering dari pmin detik dan paling lambat setiap pmax detik (diatur dengan Write-Attributes). Di sini setiap lampu adalah Lwm2mStreetLightSimulator (Lwm2mClient dengan empat objek) dan operator memakai Lwm2mServer; penulisan dan eksekusi ditolak sampai kunci tulis dibuka.");
    public override string Category => "Lpwan";
    public override IReadOnlyList<string> Protocols => ["OMA LwM2M 1.1", "CoAP", "TLV", "SenML"];
    public override Difficulty Difficulty => Difficulty.Intermediate;
    public override string DocsPath => "docs/en/protocols/lwm2m.md";

    private static readonly IPEndPoint ServerAddress = new(IPAddress.Parse("10.40.0.1"), 5683);
    private static readonly string[] Serials = ["SL60-000417", "SL60-000418", "SL60-000419", "SL60-000420"];
    private InMemoryDatagramNetwork? _net;
    private Lwm2mServer? _server;
    private readonly Dictionary<string, Lwm2mObservation> _observations = [];
    private CancellationTokenSource? _cts;

    /// <summary>The poles, west to east.</summary>
    public ObservableCollection<StreetPole> Poles { get; } = [];

    /// <summary>Resources of the selected light's Device object.</summary>
    public ObservableCollection<string> DeviceInfo { get; } = [];

    [ObservableProperty] private StreetPole? _selected;
    [ObservableProperty] private bool _writesAllowed;
    [ObservableProperty] private double _dimmerTarget = 60;

    /// <summary>Raised when the street should redraw.</summary>
    public event Action? Changed;

    private static CoapTransmission Fast() => new() { AckTimeout = TimeSpan.FromMilliseconds(300), MaxRetransmit = 1, ResponseTimeout = TimeSpan.FromSeconds(3) };

    protected override async Task OnStartAsync()
    {
        _cts = new CancellationTokenSource();
        Poles.Clear();
        DeviceInfo.Clear();
        _observations.Clear();
        _net = new InMemoryDatagramNetwork { Latency = TimeSpan.FromMilliseconds(15) };
        _server = Lwm2mServer.Create(o =>
        {
            o.UseInMemory(_net, ServerAddress);
            o.Transmission = Fast();
            o.ExpiryGrace = TimeSpan.FromSeconds(1);
            o.RequestTimeout = TimeSpan.FromSeconds(3);
            o.AllowWrites();   // the demo's write lock sits in front of the server's write calls
        });
        _server.AddTap(Tap);
        _server.Registered += r => Ui(() => OnRegistered(r));
        _server.Deregistered += (r, why) => Ui(() => OnGone(r, why));
        await _server.StartAsync();
        for (var i = 0; i < Serials.Length; i++)
        {
            var pole = new StreetPole(Serials[i], (i + 0.5) / Serials.Length);
            Poles.Add(pole);
            await PowerOnAsync(pole);
        }

        Selected = Poles[0];
        _ = TickAsync(_cts.Token);
        if (Environment.GetEnvironmentVariable("IOTCOM_GALLERY_SCREENSHOT") == "1")
        {
            WritesAllowed = true;
            foreach (var (pole, dim) in Poles.Zip(new long[] { 100, 70, 35, 0 }))
            {
                Selected = pole;
                DimmerTarget = dim;
                await SwitchAsync(dim > 0);
            }

            Selected = Poles[1];
            await PowerCutAsync();
        }

        SetStatus(new Text("LwM2M server 10.40.0.1:5683 · four LwM2M 1.1 clients over CoAP/UDP · lifetime 8 s, renewed at 80 % · driver temperature observed with pmin 1, pmax 5.",
            "Server LwM2M 10.40.0.1:5683 · empat klien LwM2M 1.1 lewat CoAP/UDP · lifetime 8 dtk, diperbarui pada 80 % · suhu driver di-observe dengan pmin 1, pmax 5."));
    }

    private async Task PowerOnAsync(StreetPole pole)
    {
        var index = Poles.IndexOf(pole);
        var light = Lwm2mStreetLightSimulator.Create(o =>
        {
            o.UseInMemory(_net!);
            o.UseServer(ServerAddress);
            o.Lifetime = TimeSpan.FromSeconds(8);
            o.Transmission = Fast();
        }, pole.Serial, -6.9214, 107.6060 + (index * 0.0006), seed: 40 + index);
        pole.Device = light;
        await light.StartAsync();
    }

    private void OnRegistered(Lwm2mRegistration r)
    {
        if (Poles.FirstOrDefault(p => p.Endpoint == r.Endpoint) is not { } pole) return;
        pole.Registered = true;
        AddLog($"Register {r.Endpoint} → 2.01 Created rd/{r.Id}, lifetime {r.Lifetime.TotalSeconds:0} s, {string.Join(" ", r.Objects)}");
        _ = ObserveAsync(pole);
        Changed?.Invoke();
    }

    private void OnGone(Lwm2mRegistration r, Lwm2mDeregistration why)
    {
        if (Poles.FirstOrDefault(p => p.Endpoint == r.Endpoint) is not { } pole || why == Lwm2mDeregistration.Replaced) return;
        pole.Registered = false;
        _observations.Remove(pole.Endpoint);
        AddLog(why == Lwm2mDeregistration.Expired ? $"{r.Endpoint}: registration EXPIRED (no update for {r.Lifetime.TotalSeconds:0} s)" : $"Deregister {r.Endpoint} → 2.02 Deleted");
        Changed?.Invoke();
    }

    private async Task ObserveAsync(StreetPole pole)
    {
        try
        {
            var path = Lwm2mPath.Parse("/3303/0/5700");
            await _server!.WriteAttributesAsync(pole.Endpoint, path, pmin: 1, pmax: 5);
            var observation = await _server.ObserveAsync(pole.Endpoint, path, values => Ui(() =>
            {
                pole.Temperature = Convert.ToDouble(values[0].Value, CultureInfo.InvariantCulture);
                Changed?.Invoke();
            }));
            Ui(() =>
            {
                _observations[pole.Endpoint] = observation;
                pole.Temperature = Convert.ToDouble(observation.Initial[0].Value, CultureInfo.InvariantCulture);
            });
            var light = await _server.ReadAsync(pole.Endpoint, Lwm2mPath.Parse("/3311/0"));
            Ui(() => ApplyLight(pole, light));
        }
        catch (IoTComException ex)
        {
            Ui(() => AddLog($"{pole.Endpoint}: {ex.Message}"));
        }
    }

    private void ApplyLight(StreetPole pole, IReadOnlyList<Lwm2mValue> values)
    {
        foreach (var v in values)
        {
            if (v.Path.ResourceId == 5850) pole.On = v.Value is true;
            if (v.Path.ResourceId == 5851) pole.Dimmer = Convert.ToInt64(v.Value, CultureInfo.InvariantCulture);
        }

        Changed?.Invoke();
    }

    private async Task TickAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct);
                foreach (var pole in Poles.ToList())
                    if (pole.Registered) pole.Device?.Step(30);
                Ui(() =>
                {
                    foreach (var pole in Poles)
                        pole.Expires = _server?.Find(pole.Endpoint) is { } r ? string.Create(CultureInfo.InvariantCulture, $"renew in {Math.Max(0, (r.Expires - DateTimeOffset.UtcNow).TotalSeconds):0} s") : "not registered";
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    partial void OnSelectedChanged(StreetPole? value) => _ = ReadDeviceAsync(value);

    private async Task ReadDeviceAsync(StreetPole? pole)
    {
        Ui(() => DeviceInfo.Clear());
        if (pole is null || _server is null || !pole.Registered) return;
        try
        {
            var values = await _server.ReadAsync(pole.Endpoint, Lwm2mPath.Parse("/3/0"));
            Ui(() =>
            {
                DeviceInfo.Clear();
                foreach (var v in values.Where(v => v.Path.ResourceId is 0 or 1 or 2 or 3 or 17 or 15))
                    DeviceInfo.Add($"{v.Path,-9} {Lwm2mRegistry.Find(3, v.Path.ResourceId!.Value)?.Name,-17} {Lwm2mValue.Format(v.Value)}");
                AddLog($"Read {pole.Endpoint} /3/0 → 2.05 Content ({values.Count} values, TLV)");
            });
        }
        catch (IoTComException ex)
        {
            Ui(() => AddLog($"Read {pole.Endpoint}: {ex.Message}"));
        }
    }

    private async Task<bool> GuardedAsync(string what, Func<Lwm2mServer, StreetPole, Task> action)
    {
        if (Selected is not { } pole || _server is null) return false;
        if (!WritesAllowed)
        {
            Ui(() => AddLog($"{what} {pole.Endpoint}: refused — the write lock is closed (read-only operator)"));
            return false;
        }

        try
        {
            await action(_server, pole);
            return true;
        }
        catch (IoTComException ex)
        {
            Ui(() => AddLog($"{what} {pole.Endpoint}: {ex.Message}"));
            return false;
        }
    }

    /// <summary>Switches the selected light (writing the dimmer first when switching on).</summary>
    public async Task SwitchAsync(bool on)
    {
        var target = (long)DimmerTarget;
        if (await GuardedAsync("Write", async (server, pole) =>
        {
            await server.WriteAsync(pole.Endpoint, Lwm2mPath.Parse("/3311/0"), [new(Lwm2mPath.Parse("/3311/0/5850"), on), new(Lwm2mPath.Parse("/3311/0/5851"), target)], replace: false);
            var light = await server.ReadAsync(pole.Endpoint, Lwm2mPath.Parse("/3311/0"));
            Ui(() =>
            {
                ApplyLight(pole, light);
                AddLog($"Write {pole.Endpoint} /3311/0 on={on}, dimmer={target} % → 2.04 Changed");
            });
        }).ConfigureAwait(false)) Changed?.Invoke();
    }

    /// <summary>Executes Reboot on the selected light.</summary>
    public Task RebootAsync() => GuardedAsync("Execute", async (server, pole) =>
    {
        await server.ExecuteAsync(pole.Endpoint, Lwm2mPath.Parse("/3/0/4"));
        Ui(() => AddLog($"Execute {pole.Endpoint} /3/0/4 (Reboot) → 2.04 Changed"));
    });

    /// <summary>Cuts the selected light's power: it stops talking and its registration expires.</summary>
    public async Task PowerCutAsync()
    {
        if (Selected is not { Device: { } device } pole) return;
        await device.Client.AbortAsync();
        pole.Device = null;
        Ui(() =>
        {
            pole.On = false;
            AddLog($"{pole.Endpoint}: power cut — no deregistration; the server waits for the lifetime to pass");
            Changed?.Invoke();
        });
    }

    /// <summary>Restores power to the selected light: it boots and registers again.</summary>
    public async Task PowerOnSelectedAsync()
    {
        if (Selected is not { Device: null } pole) return;
        await PowerOnAsync(pole);
    }

    protected override async Task OnStopAsync()
    {
        if (_cts is not null) await _cts.CancelAsync();
        foreach (var o in _observations.Values.ToList()) await o.DisposeAsync();
        _observations.Clear();
        foreach (var pole in Poles)
            if (pole.Device is { } d) await d.DisposeAsync();
        if (_server is not null) await _server.DisposeAsync();
        (_server, _net) = (null, null);
        _cts?.Dispose();
        _cts = null;
        WritesAllowed = false;
        Status = "";
    }
}
