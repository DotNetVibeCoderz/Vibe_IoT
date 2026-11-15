using System.Collections.ObjectModel;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using IoTCom.Net.Gallery.Infrastructure;
using IoTCom.Net.Protocols.Matter;
using IoTCom.Net.Transports;

namespace IoTCom.Net.Gallery.Demos;

/// <summary>One commissioning step as the timeline shows it.</summary>
public sealed partial class CommissioningStepRow : ObservableObject
{
    internal CommissioningStepRow(MatterCommissioningStep step, Text title, Text detail) => (Step, Title, Detail) = (step, title, detail);

    public MatterCommissioningStep Step { get; }
    public Text Title { get; }
    public Text Detail { get; }

    [ObservableProperty] private bool _done;
}

/// <summary>
/// A smart home over Matter: a bridge exposes four room lights, two temperature sensors and a desk lamp. The controller
/// commissions it from its pairing code — every step lights up on a timeline — then subscribes to the house and switches
/// lights behind a write lock. A light can be unplugged from the bridge and comes back as unreachable.
/// </summary>
public sealed partial class MatterHomeDemo : GalleryDemo
{
    public override string Id => "matter-home";
    public override Text Title => new("Smart home over Matter", "Rumah pintar lewat Matter");
    public override Text Summary => new(
        "A controller commissions a smart-home bridge from its pairing code — PASE, attestation, certificates and CASE light up step by step — then subscribes to four room lights and two temperature sensors and switches them behind a write lock.",
        "Controller meng-commission bridge rumah pintar dari kode pairing-nya — PASE, atestasi, sertifikat, dan CASE menyala langkah demi langkah — lalu berlangganan empat lampu ruangan dan dua sensor suhu serta menyalakannya di balik kunci tulis.");
    public override Text Docs => new(
        "Matter is the smart-home standard of the Connectivity Standards Alliance. A device is commissioned once: the controller proves it knows the setup passcode from the QR code or the 11-digit manual code with PASE (a SPAKE2+ password-authenticated key exchange, so the passcode never crosses the network), arms a fail-safe, checks the device's attestation certificate chain (DAC → PAI → a trusted PAA) and its signature, asks for a certificate signing request, installs its fabric's root certificate and an operational certificate (NOC) for a node id, and finally reaches the device again over CASE — a SIGMA key exchange with those certificates. From then on every message is encrypted with AES-CCM and every request carries a message counter.\n\nThe data model is a tree: endpoints hold clusters, clusters hold attributes and commands. A bridge has an Aggregator endpoint whose parts list names one endpoint per bridged device, each with Bridged Device Basic Information (label, reachable) and its own clusters — On/Off for a light, Temperature Measurement for a sensor. The controller reads attributes with paths that may contain wildcards, invokes commands such as On, Off and Toggle, and subscribes: the device sends a priming report, then a report whenever an attribute changes. Here the bridge is IoTCom's MatterDevice with MatterBridgeSimulator and the controller is MatterController, both on an in-memory network with IoTCom's test attestation; the same code runs over UDP 5540 with DNS-SD.",
        "Matter adalah standar rumah pintar dari Connectivity Standards Alliance. Sebuah perangkat di-commission sekali: controller membuktikan ia tahu setup passcode dari kode QR atau kode manual 11 digit dengan PASE (pertukaran kunci terautentikasi kata sandi SPAKE2+, sehingga passcode tidak pernah melintasi jaringan), memasang fail-safe, memeriksa rantai sertifikat atestasi perangkat (DAC → PAI → PAA tepercaya) beserta tanda tangannya, meminta certificate signing request, memasang sertifikat root fabric-nya dan sertifikat operasional (NOC) untuk sebuah node id, lalu menjangkau perangkat lagi lewat CASE — pertukaran kunci SIGMA dengan sertifikat tersebut. Sejak itu setiap pesan dienkripsi dengan AES-CCM dan setiap permintaan membawa penghitung pesan.\n\nModel datanya berupa pohon: endpoint berisi cluster, cluster berisi atribut dan perintah. Sebuah bridge punya endpoint Aggregator yang parts list-nya menyebut satu endpoint per perangkat yang dijembatani, masing-masing dengan Bridged Device Basic Information (label, reachable) dan cluster-nya sendiri — On/Off untuk lampu, Temperature Measurement untuk sensor. Controller membaca atribut dengan path yang boleh berisi wildcard, memanggil perintah seperti On, Off, dan Toggle, serta berlangganan: perangkat mengirim laporan awal, lalu laporan setiap kali atribut berubah. Di sini bridge-nya adalah MatterDevice milik IoTCom dengan MatterBridgeSimulator dan controller-nya MatterController, keduanya di jaringan dalam memori dengan atestasi uji IoTCom; kode yang sama berjalan di UDP 5540 dengan DNS-SD.");
    public override string Category => "Building";
    public override IReadOnlyList<string> Protocols => ["Matter", "PASE", "CASE", "UDP 5540"];
    public override Difficulty Difficulty => Difficulty.Advanced;
    public override string DocsPath => "docs/en/protocols/matter.md";

    private static readonly IPEndPoint BridgeAt = new(IPAddress.Parse("10.0.0.70"), 5540);
    private InMemoryDatagramNetwork? _net;
    private MatterDevice? _device;
    private MatterController? _controller;
    private MatterNode? _node;
    private CancellationTokenSource? _cts;

    internal MatterBridgeSimulator? Sim { get; private set; }

    public ObservableCollection<CommissioningStepRow> Steps { get; } =
    [
        new(MatterCommissioningStep.Pase, new("PASE", "PASE"), new("SPAKE2+ with the setup passcode", "SPAKE2+ dengan setup passcode")),
        new(MatterCommissioningStep.ArmFailSafe, new("Fail-safe", "Fail-safe"), new("ArmFailSafe 60 s", "ArmFailSafe 60 dtk")),
        new(MatterCommissioningStep.Attestation, new("Attestation", "Atestasi"), new("DAC → PAI → PAA, signature", "DAC → PAI → PAA, tanda tangan")),
        new(MatterCommissioningStep.Csr, new("CSR", "CSR"), new("Operational key pair on the device", "Pasangan kunci operasional di perangkat")),
        new(MatterCommissioningStep.AddRoot, new("Root", "Root"), new("Fabric root certificate", "Sertifikat root fabric")),
        new(MatterCommissioningStep.AddNoc, new("NOC", "NOC"), new("Node 0x42 joins the fabric", "Node 0x42 bergabung ke fabric")),
        new(MatterCommissioningStep.Case, new("CASE", "CASE"), new("SIGMA with certificates", "SIGMA dengan sertifikat")),
        new(MatterCommissioningStep.Complete, new("Complete", "Selesai"), new("CommissioningComplete", "CommissioningComplete")),
    ];

    [ObservableProperty] private bool _writesAllowed;
    [ObservableProperty] private bool _commissioned;
    [ObservableProperty] private string _qr = "";
    [ObservableProperty] private string _manual = "";
    [ObservableProperty] private string _fabric = "";

    /// <summary>Raised when the house view should redraw.</summary>
    public event Action? Changed;

    protected override async Task OnStartAsync()
    {
        _cts = new CancellationTokenSource();
        foreach (var s in Steps) s.Done = false;
        Commissioned = false;
        _net = new InMemoryDatagramNetwork();
        _device = MatterDevice.Create(o => { o.UseInMemory(_net, BridgeAt); o.ProductName = "IoTCom smart home bridge"; o.Reliability.Interval = TimeSpan.FromMilliseconds(150); });
        Sim = new MatterBridgeSimulator(_device);
        await _device.StartAsync();
        _controller = MatterController.Create(o => { o.UseInMemory(_net); o.AllowWrites(); o.TrustTestDevices(); o.Reliability.Interval = TimeSpan.FromMilliseconds(150); });   // the demo's lock sits in front
        _controller.AddTap(Tap);
        await _controller.ConnectAsync();
        (Qr, Manual) = (_device.SetupPayload.ToQrCode(), _device.SetupPayload.ToManualCode());
        AddLog($"Bridge {BridgeAt}: commissioning window open, QR {Qr}, manual code {Manual}");
        _ = LoopAsync(_cts.Token);
        if (Environment.GetEnvironmentVariable("IOTCOM_GALLERY_SCREENSHOT") == "1")
        {
            WritesAllowed = true;
            await CommissionAsync();
            await ToggleAsync(Sim.Lights[0].Endpoint);
            await ToggleAsync(Sim.Lights[2].Endpoint);
            Sim.SetReachable(Sim.Lights[3].Endpoint, false);
            await Task.Delay(400);
        }

        SetStatus(new Text("Matter bridge (MatterDevice + MatterBridgeSimulator) and controller (MatterController) on an in-memory network · IoTCom test attestation · UDP 5540 with DNS-SD in real deployments.",
            "Bridge Matter (MatterDevice + MatterBridgeSimulator) dan controller (MatterController) di jaringan dalam memori · atestasi uji IoTCom · UDP 5540 dengan DNS-SD di penerapan nyata."));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(500, ct);
                Sim?.Step(30);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async Task CommissionAsync()
    {
        if (_controller is null || _device is null || Commissioned) return;
        if (!WritesAllowed)
        {
            AddLog("commission: refused — the write lock is closed (commissioning adds a fabric to the bridge)");
            return;
        }

        try
        {
            var progress = new Progress<MatterCommissioningStep>(step => Ui(() =>
            {
                Steps.First(s => s.Step == step).Done = true;
                AddLog($"commissioning: {step}");
            }));
            _node = await _controller.CommissionAsync(BridgeAt, MatterSetupPayload.FromManualCode(Manual), 0x42, progress);
            await Task.Delay(50);
            foreach (var s in Steps) s.Done = true;
            Commissioned = true;
            Fabric = $"fabric 0x{_controller.FabricId:X} · compressed {_controller.CompressedFabricId} · node 0x42";
            await _node.SubscribeAsync([new AttributePath(null, MatterIds.OnOff, 0), new AttributePath(null, MatterIds.TemperatureMeasurement, 0), new AttributePath(null, MatterIds.BridgedDeviceBasicInformation, 0x11)],
                0, 60, r => Ui(() =>
                {
                    AddLog($"report {r.Path} = {r.Value}");
                    Changed?.Invoke();
                }));
            AddLog("subscribed to On/Off, Temperature Measurement and Reachable on every endpoint");
        }
        catch (IoTComException ex)
        {
            AddLog($"commission: {ex.Message}");
        }

        Changed?.Invoke();
    }

    public async Task ToggleAsync(ushort endpoint)
    {
        if (_node is null) return;
        if (!WritesAllowed)
        {
            AddLog($"toggle endpoint {endpoint}: refused — the write lock is closed");
            return;
        }

        var (_, status) = await _node.InvokeAsync(endpoint, MatterIds.OnOff, 2);
        AddLog($"InvokeRequest {endpoint}/0x0006/Toggle → {status}");
        Changed?.Invoke();
    }

    public void TogglePlug(ushort endpoint)
    {
        if (Sim is null) return;
        var light = Sim.Lights.First(l => l.Endpoint == endpoint);
        Sim.SetReachable(endpoint, !light.Info.Reachable);
        AddLog($"{light.Label}: {(light.Info.Reachable ? "plugged back into the bridge" : "unplugged — the bridge reports Reachable = false")}");
        Changed?.Invoke();
    }

    protected override async Task OnStopAsync()
    {
        if (_cts is not null) await _cts.CancelAsync();
        if (_controller is not null) await _controller.DisposeAsync();
        if (_device is not null) await _device.DisposeAsync();
        (_controller, _device, _node, Sim) = (null, null, null, null);
        _cts?.Dispose();
        _cts = null;
        WritesAllowed = false;
        Commissioned = false;
        (Qr, Manual, Fabric, Status) = ("", "", "", "");
    }
}
