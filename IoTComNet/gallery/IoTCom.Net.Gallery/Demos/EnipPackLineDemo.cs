using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using IoTCom.Net.Gallery.Infrastructure;
using IoTCom.Net.Protocols.EtherNetIp;
using IoTCom.Net.Transports;

namespace IoTCom.Net.Gallery.Demos;

/// <summary>One Logix tag as the HMI reads it.</summary>
public sealed partial class LogixTagRow : ObservableObject
{
    internal LogixTagRow(string name, string access) => (Name, Access) = (name, access);

    public string Name { get; }
    public string Access { get; }

    [ObservableProperty] private string _value = "";
}

/// <summary>
/// A packaging line's HMI over EtherNet/IP explicit messaging: one Multiple_Service_Packet per second reads the PLC's
/// Logix tags; the operator changes speed, starts and stops the conveyor and moves the seal set point behind a write
/// lock. A seal outside the film's window sends packs to the reject bin.
/// </summary>
public sealed partial class EnipPackLineDemo : GalleryDemo
{
    public override string Id => "enip-packline";
    public override Text Title => new("Packaging line over EtherNet/IP", "Lini pengemasan lewat EtherNet/IP");
    public override Text Summary => new(
        "An HMI polls a packaging-line PLC's Logix tags over EtherNet/IP explicit messaging, one Multiple_Service_Packet per second; change speed, stop the conveyor or cool the seal jaws behind a write lock and watch rejects climb.",
        "HMI membaca tag Logix PLC lini pengemasan lewat EtherNet/IP explicit messaging, satu Multiple_Service_Packet per detik; ubah kecepatan, hentikan konveyor, atau dinginkan seal jaw di balik kunci tulis dan lihat reject naik.");
    public override Text Docs => new(
        "EtherNet/IP carries CIP (the Common Industrial Protocol) over Ethernet. An originator such as an HMI opens TCP 44818, registers a session (RegisterSession returns a session handle), and sends CIP requests inside SendRRData: an unconnected message addressed to an object by an EPATH — class, instance and attribute, or a symbolic tag name. ListIdentity, also on UDP 44818 as a broadcast, tells who is on the network.\n\nA CIP request is a service code, a path and data. Get_Attribute_Single (0x0E) on class 1 instance 1 attribute 7 reads the device's product name from the Identity object. Logix controllers add Read Tag (0x4C) and Write Tag (0x4D) on symbolic paths, with typed data: REAL 0xCA, DINT 0xC4, BOOL 0xC1. Multiple_Service_Packet (0x0A) batches several requests into one round trip, which is how this HMI reads eight tags at once. To reach a controller in a chassis, the request is wrapped in Unconnected_Send with a route path (backplane port 1, slot 0).\n\nThe answers carry a general status: 0x05 path destination unknown for a missing tag, 0x0F privilege violation for a read-only one. Here the PLC is IoTCom's EnipAdapter (EnipPlcSimulator) and the HMI is EnipClient; implicit (cyclic I/O) messaging is not part of this demo.",
        "EtherNet/IP membawa CIP (Common Industrial Protocol) di atas Ethernet. Originator seperti HMI membuka TCP 44818, mendaftarkan sesi (RegisterSession mengembalikan session handle), dan mengirim permintaan CIP di dalam SendRRData: pesan unconnected yang dialamatkan ke objek dengan EPATH — class, instance, dan attribute, atau nama tag simbolik. ListIdentity, juga di UDP 44818 sebagai broadcast, memberi tahu siapa saja yang ada di jaringan.\n\nPermintaan CIP adalah kode layanan, path, dan data. Get_Attribute_Single (0x0E) pada class 1 instance 1 attribute 7 membaca nama produk device dari objek Identity. Pengendali Logix menambahkan Read Tag (0x4C) dan Write Tag (0x4D) pada path simbolik, dengan data bertipe: REAL 0xCA, DINT 0xC4, BOOL 0xC1. Multiple_Service_Packet (0x0A) mengumpulkan beberapa permintaan dalam satu perjalanan pulang-pergi, begitulah HMI ini membaca delapan tag sekaligus. Untuk mencapai pengendali di sebuah chassis, permintaan dibungkus Unconnected_Send dengan route path (port backplane 1, slot 0).\n\nJawabannya membawa general status: 0x05 path destination unknown untuk tag yang tidak ada, 0x0F privilege violation untuk tag hanya-baca. Di sini PLC-nya adalah EnipAdapter milik IoTCom (EnipPlcSimulator) dan HMI-nya adalah EnipClient; messaging implisit (I/O siklik) bukan bagian dari demo ini.");
    public override string Category => "Industrial";
    public override IReadOnlyList<string> Protocols => ["EtherNet/IP", "CIP", "Logix tags", "TCP 44818"];
    public override Difficulty Difficulty => Difficulty.Intermediate;
    public override string DocsPath => "docs/en/protocols/ethernetip.md";

    private static readonly string[] Polled = ["Line_Speed", "Motor_Run", "Count_Good", "Count_Reject", "Seal_Temp", "Seal_Setpoint", "Recipe", "Fault_Code"];
    private EnipPlcSimulator? _plc;
    private EnipClient? _hmi;
    private CancellationTokenSource? _cts;

    public ObservableCollection<LogixTagRow> Tags { get; } = [];

    [ObservableProperty] private bool _writesAllowed;
    [ObservableProperty] private double _speedTarget = 120;
    [ObservableProperty] private double _sealTarget = 172;
    [ObservableProperty] private double _speed;
    [ObservableProperty] private bool _running;
    [ObservableProperty] private double _sealTemp;
    [ObservableProperty] private double _good;
    [ObservableProperty] private double _rejects;
    [ObservableProperty] private string _identity = "";

    /// <summary>Raised on each poll (the line animation follows it).</summary>
    public event Action? Polled2;

    protected override async Task OnStartAsync()
    {
        _cts = new CancellationTokenSource();
        Tags.Clear();
        var listener = new InMemoryTransportListener("packline");
        _plc = EnipPlcSimulator.Create(o => o.ListenInMemory(listener));
        await _plc.StartAsync();
        _hmi = EnipClient.Create(o => { o.UseInMemory(listener); o.UseSlot(0); o.AllowWrites(); });   // the demo's write lock sits in front of the writes
        _hmi.AddTap(Tap);
        await _hmi.ConnectAsync();
        var id = await _hmi.ReadIdentityAsync();
        Identity = $"{id.ProductName} · rev {id.RevisionMajor}.{id.RevisionMinor:D3} · serial {id.SerialNumber:X8} · session 0x{_hmi.Session:X8}";
        foreach (var t in Polled.Append("Zone_Temps")) Tags.Add(new LogixTagRow(t, _plc.Adapter.Find(t)!.Writable ? "R/W" : "R"));
        AddLog($"RegisterSession → 0x{_hmi.Session:X8}; Get_Attributes_All Identity → {id.ProductName}");
        await PollAsync();
        _ = LoopAsync(_cts.Token);

        if (Environment.GetEnvironmentVariable("IOTCOM_GALLERY_SCREENSHOT") == "1")
        {
            WritesAllowed = true;
            SealTarget = 164;
            await SealAsync();
            for (var i = 0; i < 6; i++) _plc.Step(30);
            await PollAsync();
        }

        SetStatus(new Text("EtherNet/IP adapter \"IoTCom PackLine-1 PLC\" (EnipAdapter) · HMI EnipClient routed through backplane slot 0 · Read Tag ×8 in one Multiple_Service_Packet per second · 10 s of production per second.",
            "Adapter EtherNet/IP \"IoTCom PackLine-1 PLC\" (EnipAdapter) · HMI EnipClient lewat backplane slot 0 · Read Tag ×8 dalam satu Multiple_Service_Packet per detik · 10 detik produksi per detik."));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct);
                _plc?.Step(10);
                await PollAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task PollAsync()
    {
        if (_hmi is null) return;
        try
        {
            var results = await _hmi.ReadTagsAsync(Polled);
            var zones = await _hmi.ReadTagAsync("Zone_Temps", 4);
            Ui(() =>
            {
                foreach (var (tag, value, error) in results)
                {
                    var row = Tags.First(r => r.Name == tag);
                    row.Value = value?.ToString() ?? error!.Status.ToString();
                }

                Tags.First(r => r.Name == "Zone_Temps").Value = zones.ToString();
                double V(string n) => results.First(r => r.Tag == n).Value?.Value ?? double.NaN;
                (Speed, Running, SealTemp, Good, Rejects) = (V("Line_Speed"), V("Motor_Run") != 0, V("Seal_Temp"), V("Count_Good"), V("Count_Reject"));
                Polled2?.Invoke();
            });
        }
        catch (IoTComException ex)
        {
            AddLog($"poll: {ex.Message}");
        }
    }

    private async Task Write(string what, string tag, CipTagValue value)
    {
        if (_hmi is null) return;
        if (!WritesAllowed)
        {
            AddLog($"{what}: refused — the write lock is closed (read-only HMI)");
            return;
        }

        try
        {
            await _hmi.WriteTagAsync(tag, value);
            AddLog($"Write Tag {tag} = {value} ({what}) → success");
        }
        catch (Exception ex) when (ex is IoTComException)
        {
            AddLog($"{what}: {ex.Message}");
        }

        await PollAsync();
    }

    public Task SpeedAsync() => Write("line speed", "Line_Speed", CipTagValue.Of(CipDataType.Real, Math.Round(SpeedTarget)));

    public Task SealAsync() => Write("seal set point", "Seal_Setpoint", CipTagValue.Of(CipDataType.Real, Math.Round(SealTarget * 2) / 2));

    public Task MotorAsync(bool run) => Write(run ? "start conveyor" : "stop conveyor", "Motor_Run", CipTagValue.Of(CipDataType.Bool, run ? 1 : 0));

    public Task ResetCounterAsync() => Write("reset the good counter", "Count_Good", CipTagValue.Of(CipDataType.Dint, 0));

    protected override async Task OnStopAsync()
    {
        if (_cts is not null) await _cts.CancelAsync();
        if (_hmi is not null) await _hmi.DisposeAsync();
        if (_plc is not null) await _plc.DisposeAsync();
        (_hmi, _plc) = (null, null);
        _cts?.Dispose();
        _cts = null;
        WritesAllowed = false;
        Identity = "";
        Status = "";
    }
}
