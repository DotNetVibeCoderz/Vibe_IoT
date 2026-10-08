using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using IoTCom.Net.Gallery.Infrastructure;
using IoTCom.Net.Protocols.J1939;
using IoTCom.Net.Transport.Can;

namespace IoTCom.Net.Gallery.Demos;

/// <summary>
/// A truck instrument cluster on J1939: an engine ECU broadcasts EEC1, CCVS1, ET1, EFL/P1 and DM1 on a virtual CAN bus;
/// the cluster decodes the SPNs, requests the VIN (transport protocol) and shows active trouble codes.
/// </summary>
public sealed partial class J1939Demo : GalleryDemo
{
    public override string Id => "j1939-truck";
    public override Text Title => new("Truck cluster over J1939", "Panel truk lewat J1939");
    public override Text Summary => new(
        "A heavy-duty engine ECU broadcasts on J1939: watch engine speed, vehicle speed, temperatures and oil pressure decoded from SPNs, request the VIN, and see the amber lamp when DM1 reports a fault.",
        "ECU mesin truk berat menyiarkan di J1939: lihat putaran mesin, kecepatan kendaraan, suhu, dan tekanan oli yang diurai dari SPN, minta VIN, dan lihat lampu kuning saat DM1 melaporkan gangguan.");
    public override Text Docs => new(
        "J1939 runs trucks, buses, tractors, construction machines and marine engines on 250 or 500 kbit/s CAN with 29-bit identifiers. The identifier carries a priority, the parameter group number (PGN) and the source address. Each PGN packs suspect parameters (SPNs) with fixed scaling: EEC1 (PGN 61444) holds engine speed in 0.125 rpm steps, CCVS1 the vehicle speed in 1/256 km/h, ET1 the coolant temperature with a −40 °C offset. Values in the 0xFB–0xFF range mean 'not available' or 'error'.\n\nMessages longer than 8 bytes travel with the transport protocol: a BAM announces a broadcast and is followed by numbered data packets; a destination-specific transfer uses RTS, CTS and an end-of-message acknowledgement. Nodes claim their address with a 64-bit NAME, and the lower NAME wins a conflict. DM1 broadcasts the lamp status and active trouble codes as SPN + FMI.\n\nHere the ECU is IoTCom.Net's J1939EngineSimulator and the cluster uses J1939Node. The same code listens to a real truck through SocketCAN, slcan, gs_usb or PCAN.",
        "J1939 menjalankan truk, bus, traktor, alat berat, dan mesin kapal di CAN 250 atau 500 kbit/s dengan identifier 29-bit. Identifier membawa prioritas, parameter group number (PGN), dan alamat sumber. Setiap PGN memuat suspect parameter (SPN) dengan skala tetap: EEC1 (PGN 61444) berisi putaran mesin dalam langkah 0,125 rpm, CCVS1 kecepatan kendaraan dalam 1/256 km/jam, ET1 suhu pendingin dengan offset −40 °C. Nilai di rentang 0xFB–0xFF berarti 'tidak tersedia' atau 'error'.\n\nPesan lebih dari 8 byte dikirim dengan transport protocol: BAM mengumumkan siaran lalu diikuti paket data bernomor; transfer ke tujuan tertentu memakai RTS, CTS, dan acknowledgement akhir pesan. Node mengklaim alamatnya dengan NAME 64-bit, dan NAME yang lebih kecil memenangkan konflik. DM1 menyiarkan status lampu dan kode gangguan aktif sebagai SPN + FMI.\n\nDi sini ECU-nya adalah J1939EngineSimulator milik IoTCom.Net dan panelnya memakai J1939Node. Kode yang sama mendengarkan truk sungguhan lewat SocketCAN, slcan, gs_usb, atau PCAN.");
    public override string Category => "Automotive";
    public override IReadOnlyList<string> Protocols => ["SAE J1939", "CAN 29-bit", "DM1"];
    public override Difficulty Difficulty => Difficulty.Intermediate;
    public override string DocsPath => "docs/en/protocols/j1939.md";

    private J1939EngineSimulator? _engine;
    private J1939Node? _cluster;
    private VirtualCanBus? _bus;
    private CancellationTokenSource? _cts;

    /// <summary>Active trouble codes.</summary>
    public ObservableCollection<string> Dtcs { get; } = [];

    /// <summary>Recent PGNs.</summary>
    public ObservableCollection<string> Traffic { get; } = [];

    [ObservableProperty] private double _rpm;
    [ObservableProperty] private double _speed;
    [ObservableProperty] private double _coolant;
    [ObservableProperty] private double _oilPressure;
    [ObservableProperty] private double _fuelRate;
    [ObservableProperty] private double _battery;
    [ObservableProperty] private bool _amber;
    [ObservableProperty] private string _vin = "–";
    [ObservableProperty] private double _throttle = 35;

    /// <summary>Raised when the gauges should redraw.</summary>
    public event Action? Changed;

    protected override async Task OnStartAsync()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Dtcs.Clear();
        Traffic.Clear();
        Vin = "–";
        Amber = false;
        var net = new VirtualCanNetwork("gallery-j1939");
        _engine = J1939EngineSimulator.Create(net.CreateNode());
        _engine.Throttle = Throttle;
        _bus = net.CreateNode();
        _bus.AddTap(Tap);
        _cluster = J1939Node.Create(_bus, o => { o.Address = 0x17; o.ReadOnly = true; o.Name = new J1939Name(0x1234, 0x7FF, 0, 0, 23, 0, 0, 1, true); });   // instrument cluster
        _cluster.MessageReceived += m => Ui(() => OnMessage(m));
        await _cluster.StartAsync(ct);
        await _engine.StartAsync(ct);
        if (Environment.GetEnvironmentVariable("IOTCOM_GALLERY_SCREENSHOT") == "1")
        {
            Throttle = 62;
            _engine.OilLeak();
            for (var i = 0; i < 60; i++) _engine.Step(0.5);
            await RequestVinAsync();
        }

        SetStatus(new Text("Virtual CAN 250 kbit/s · engine ECU 0x00 · instrument cluster 0x17 (read-only) · EEC1/CCVS1 every 100 ms, ET1/DM1 every second.",
            "CAN virtual 250 kbit/s · ECU mesin 0x00 · panel instrumen 0x17 (hanya-baca) · EEC1/CCVS1 tiap 100 ms, ET1/DM1 tiap detik."));
    }

    partial void OnThrottleChanged(double value)
    {
        if (_engine is not null) _engine.Throttle = value;
    }

    private static double? Spn(J1939Message m, uint spn) => m.Values.FirstOrDefault(v => v.Spn == spn)?.Value;

    private void OnMessage(J1939Message m)
    {
        switch (m.Pgn)
        {
            case Pgn.Eec1: Rpm = Spn(m, 190) ?? Rpm; break;
            case Pgn.Ccvs1: Speed = Spn(m, 84) ?? Speed; break;
            case Pgn.Et1: Coolant = Spn(m, 110) ?? Coolant; break;
            case Pgn.EflP1: OilPressure = Spn(m, 100) ?? OilPressure; break;
            case Pgn.Lfe1: FuelRate = Spn(m, 183) ?? FuelRate; break;
            case Pgn.Vep1: Battery = Spn(m, 168) ?? Battery; break;
            case Pgn.Dm1:
                var dm1 = J1939Dm1.Parse(m.Data);
                Amber = dm1.AmberWarningLamp == 1;
                Dtcs.Clear();
                foreach (var d in dm1.Dtcs) Dtcs.Add($"SPN {d.Spn} FMI {d.Fmi} · {J1939Spn.Name(d.Spn)} — {d.FailureMode} (×{d.OccurrenceCount})");
                break;
        }

        if (m.Pgn is not (Pgn.Eec1 or Pgn.Ccvs1))
        {
            Traffic.Insert(0, $"{DateTime.Now:HH:mm:ss.f}  {Pgn.Name(m.Pgn),-7} 0x{m.Source:X2}  {Convert.ToHexString(m.Data.AsSpan(0, Math.Min(8, m.Data.Length)))}{(m.Data.Length > 8 ? "…" : "")}");
            while (Traffic.Count > 10) Traffic.RemoveAt(Traffic.Count - 1);
        }

        Changed?.Invoke();
    }

    /// <summary>Requests the VIN from the engine (answered with RTS/CTS).</summary>
    public async Task RequestVinAsync()
    {
        if (_cluster is null) return;
        try
        {
            var vin = await _cluster.RequestAsync(Pgn.VehicleIdentification, 0x00);
            Vin = Encoding.ASCII.GetString(vin.Data).TrimEnd('*');
        }
        catch (IoTComException ex)
        {
            Vin = ex.Message;
        }
    }

    /// <summary>Starts the simulated oil leak.</summary>
    public void Leak() => _engine?.OilLeak();

    protected override async Task OnStopAsync()
    {
        if (_cts is not null) await _cts.CancelAsync();
        if (_cluster is not null) await _cluster.DisposeAsync();
        if (_engine is not null) await _engine.DisposeAsync();
        if (_bus is not null) await _bus.DisposeAsync();
        (_cluster, _engine, _bus) = (null, null, null);
        _cts?.Dispose();
        _cts = null;
        Status = "";
    }
}
