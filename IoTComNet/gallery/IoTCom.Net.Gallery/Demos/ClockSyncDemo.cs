using System.Collections.ObjectModel;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using IoTCom.Net.Gallery.Infrastructure;
using IoTCom.Net.Protocols.Ntp;
using IoTCom.Net.Transports;

namespace IoTCom.Net.Gallery.Demos;

/// <summary>One field device: a drifting clock and its SNTP client.</summary>
public sealed partial class SyncedDevice : ObservableObject
{
    internal SyncedDevice(string name, string colour, DriftingClock clock, SntpClient client) => (Name, Colour, Clock, Client) = (name, colour, clock, client);

    public string Name { get; }
    public string Colour { get; }
    internal DriftingClock Clock { get; }
    internal SntpClient Client { get; }

    /// <summary>Recent (seconds since start, error in ms) samples.</summary>
    internal List<(double T, double ErrorMs)> Trace { get; } = [];

    [ObservableProperty] private string _reading = "";
    [ObservableProperty] private double _errorMs;
    [ObservableProperty] private string _lastSync = "–";
    [ObservableProperty] private bool _failed;
}

/// <summary>
/// Clock synchronisation of a small sensor fleet: every device has a cheap, drifting clock and an SNTP client; a
/// GPS-referenced NTP server answers over a network with adjustable latency. The error traces show each device drift
/// away and snap back at every synchronisation.
/// </summary>
public sealed partial class ClockSyncDemo : GalleryDemo
{
    public override string Id => "ntp-clock-sync";
    public override Text Title => new("Fleet clock sync over NTP", "Sinkronisasi jam armada lewat NTP");
    public override Text Summary => new(
        "Six field devices with cheap, drifting clocks ask a GPS-referenced NTP server for the time. Watch their errors grow and snap back at every synchronisation, add network latency, and see what happens when the server loses GPS.",
        "Enam perangkat lapangan dengan jam murah yang melenceng bertanya waktu ke server NTP berreferensi GPS. Lihat galatnya membesar lalu kembali ke nol di setiap sinkronisasi, tambahkan latensi jaringan, dan lihat apa yang terjadi saat server kehilangan GPS.");
    public override Text Docs => new(
        "NTP measures a clock against a server with four timestamps: T1 when the request leaves the client, T2 when it reaches the server, T3 when the answer leaves the server and T4 when it arrives back. The offset ((T2 − T1) + (T3 − T4)) / 2 is how far the client is behind; the delay (T4 − T1) − (T3 − T2) is the round trip through the network. The offset is exact when the path is symmetric; an asymmetric path adds half the difference as error.\n\nSNTP (RFC 4330) is the simple client: one request, a few checks (the answer echoes our transmit time, the server is synchronised, it is not a kiss-o'-death), and the application applies the offset. Real crystals drift by tens of parts per million — a few seconds a day — so devices re-synchronise periodically. Here the drift is exaggerated about a thousand times (several percent) so it shows within seconds.\n\nThe devices use IoTCom.Net's SntpClient and DriftingClock; the server is NtpServer on an in-memory network whose latency you control. The same client queries pool.ntp.org with UseServer(\"pool.ntp.org\").",
        "NTP mengukur jam terhadap server dengan empat cap waktu: T1 saat permintaan meninggalkan klien, T2 saat tiba di server, T3 saat jawaban meninggalkan server, dan T4 saat tiba kembali. Offset ((T2 − T1) + (T3 − T4)) / 2 adalah seberapa jauh klien tertinggal; delay (T4 − T1) − (T3 − T2) adalah perjalanan pulang-pergi lewat jaringan. Offset tepat bila jalurnya simetris; jalur yang tidak simetris menambah galat sebesar setengah selisihnya.\n\nSNTP (RFC 4330) adalah klien sederhana: satu permintaan, beberapa pemeriksaan (jawaban menggemakan waktu kirim kita, server tersinkron, bukan kiss-o'-death), lalu aplikasi menerapkan offset. Kristal sungguhan melenceng puluhan bagian per juta — beberapa detik sehari — sehingga perangkat bersinkron ulang secara berkala. Di sini lencengnya diperbesar sekitar seribu kali (beberapa persen) agar terlihat dalam hitungan detik.\n\nPerangkat memakai SntpClient dan DriftingClock milik IoTCom.Net; servernya NtpServer di jaringan dalam memori yang latensinya Anda atur. Klien yang sama bertanya ke pool.ntp.org dengan UseServer(\"pool.ntp.org\").");
    public override string Category => "Messaging";
    public override IReadOnlyList<string> Protocols => ["NTP v4", "SNTP", "UDP 123"];
    public override Difficulty Difficulty => Difficulty.Beginner;
    public override string DocsPath => "docs/en/protocols/ntp.md";

    private static readonly IPEndPoint ServerAddress = new(IPAddress.Parse("10.20.0.1"), 123);
    private static readonly (string Name, string Colour, double OffsetS, double Ppm)[] Fleet =
    [
        ("Weather mast", "#F2A900", -0.8, 42_000), ("Pump skid", "#2F6FD6", 0.5, -35_000), ("Gate sensor", "#2E9E5B", 0.9, 26_000),
        ("Cold room", "#D23B2F", -0.3, -52_000), ("Solar inverter", "#7A5BB5", 0.7, 61_000), ("Water meter", "#8A9098", -0.95, -18_000),
    ];

    private InMemoryDatagramNetwork? _net;
    private NtpServer? _server;
    private CancellationTokenSource? _cts;
    private DateTime _started;

    /// <summary>The devices.</summary>
    public ObservableCollection<SyncedDevice> Devices { get; } = [];

    [ObservableProperty] private double _syncInterval = 8;
    [ObservableProperty] private double _latencyMs = 20;
    [ObservableProperty] private bool _gpsLost;
    [ObservableProperty] private string _lastExchange = "";

    /// <summary>Raised when the traces should redraw.</summary>
    public event Action? Changed;

    /// <summary>Seconds since the demo started.</summary>
    public double Elapsed => (DateTime.UtcNow - _started).TotalSeconds;

    protected override async Task OnStartAsync()
    {
        _cts = new CancellationTokenSource();
        _started = DateTime.UtcNow;
        Devices.Clear();
        _net = new InMemoryDatagramNetwork { Latency = TimeSpan.FromMilliseconds(LatencyMs / 2), Jitter = TimeSpan.FromMilliseconds(2) };
        _server = NtpServer.Create(o => o.UseInMemory(_net, ServerAddress).WithReference("GPS"));
        _server.AddTap(Tap);
        await _server.StartAsync();
        foreach (var (name, colour, offset, ppm) in Fleet)
        {
            var clock = new DriftingClock(TimeSpan.FromSeconds(offset), ppm);
            var client = SntpClient.Create(o =>
            {
                o.UseInMemory(_net!);
                o.UseServer(ServerAddress);
                o.Clock = () => clock.UtcNow;
                o.MinimumPollInterval = TimeSpan.Zero;   // a private server on our own network
                o.Timeout = TimeSpan.FromSeconds(1);
            });
            Devices.Add(new SyncedDevice(name, colour, clock, client));
        }

        _ = SampleAsync(_cts.Token);
        _ = SyncLoopAsync(_cts.Token);
        if (Environment.GetEnvironmentVariable("IOTCOM_GALLERY_SCREENSHOT") == "1")
        {
            SyncInterval = 4;
            await Task.Delay(1500);
            await SyncAllAsync();
        }

        SetStatus(new Text("NTP server 10.20.0.1:123, stratum 1, reference GPS · six SNTP clients · drift exaggerated ~1000×.",
            "Server NTP 10.20.0.1:123, stratum 1, referensi GPS · enam klien SNTP · lenceng diperbesar ~1000×."));
    }

    partial void OnLatencyMsChanged(double value)
    {
        if (_net is not null) _net.Latency = TimeSpan.FromMilliseconds(value / 2);
    }

    partial void OnGpsLostChanged(bool value)
    {
        if (_server is not null) _server.Leap = value ? NtpLeap.Unsynchronised : NtpLeap.None;
    }

    private async Task SampleAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(100, ct);
                var t = Elapsed;
                Ui(() =>
                {
                    foreach (var d in Devices)
                    {
                        var err = d.Clock.Error.TotalMilliseconds;
                        d.ErrorMs = err;
                        d.Reading = d.Clock.UtcNow.ToLocalTime().ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
                        d.Trace.Add((t, err));
                        d.Trace.RemoveAll(p => p.T < t - 30);
                    }

                    Changed?.Invoke();
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SyncLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(SyncInterval), ct);
                await SyncAllAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Synchronises every device once, one after another.</summary>
    public async Task SyncAllAsync()
    {
        foreach (var d in Devices.ToList()) await SyncAsync(d);
    }

    private async Task SyncAsync(SyncedDevice d)
    {
        try
        {
            var r = await d.Client.QueryAsync(ServerAddress);
            d.Clock.Step(r.Offset);
            Ui(() =>
            {
                d.Failed = false;
                d.LastSync = $"{r.Offset.TotalMilliseconds:+0;-0} ms · δ {r.RoundTripDelay.TotalMilliseconds:0} ms";
                LastExchange = $"{d.Name}\nT1 {Hms(r.T1)}  (device)\nT2 {Hms(r.T2)}  (server)\nT3 {Hms(r.T3)}  (server)\nT4 {Hms(r.T4)}  (device)\nθ = {r.Offset.TotalMilliseconds:+0.0;-0.0} ms   δ = {r.RoundTripDelay.TotalMilliseconds:0.0} ms";
            });
        }
        catch (IoTComException ex)
        {
            Ui(() =>
            {
                d.Failed = true;
                d.LastSync = ex is DeviceException ? "server unsynchronised — kept own time" : "no answer";
            });
        }
    }

    private static string Hms(NtpTimestamp t) => t.ToDateTime().ToLocalTime().ToString("HH:mm:ss.ffffff", System.Globalization.CultureInfo.InvariantCulture);

    protected override async Task OnStopAsync()
    {
        if (_cts is not null) await _cts.CancelAsync();
        foreach (var d in Devices) await d.Client.DisposeAsync();
        if (_server is not null) await _server.DisposeAsync();
        (_server, _net) = (null, null);
        _cts?.Dispose();
        _cts = null;
        GpsLost = false;
        Status = "";
    }
}
