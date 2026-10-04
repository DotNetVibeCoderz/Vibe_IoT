using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using IoTCom.Net.Gallery.Infrastructure;
using IoTCom.Net.Protocols.Nmea;
using IoTCom.Net.Transports;

namespace IoTCom.Net.Gallery.Demos;

/// <summary>
/// GNSS tracker: a simulated GPS receiver publishes NMEA 0183 sentences; an NmeaReader decodes them
/// into a consolidated fix (position, speed, satellites) that the UI plots as a live track.
/// </summary>
public sealed partial class NmeaDemo : GalleryDemo
{
    public override string Id => "nmea-tracker";
    public override Text Title => new("GNSS vehicle tracker", "Pelacak kendaraan GNSS");
    public override Text Summary => new(
        "A simulated GPS receiver drives around Bandung. Watch GGA, RMC, GSA and GSV sentences decode into a position, a track, speed and satellite signal strength.",
        "Penerima GPS simulasi berkeliling Bandung. Lihat kalimat GGA, RMC, GSA dan GSV diurai menjadi posisi, lintasan, kecepatan dan kekuatan sinyal satelit.");
    public override Text Docs => new(
        "NMEA 0183 is line-based ASCII: $TTSSS,field,field*CS. The talker (GP = GPS, GN = multi-constellation) and sentence type are followed by comma-separated fields and an XOR checksum.\n\nNmeaReader validates checksums, decodes typed messages (GgaMessage, RmcMessage, ...) and keeps a GnssState with the latest fix. NmeaServer broadcasts sentences to any number of clients — the same API works over a serial GPS (UseSerial) or an NMEA-over-TCP multiplexer (UseTcp).",
        "NMEA 0183 berbasis baris ASCII: $TTSSS,field,field*CS. Talker (GP = GPS, GN = multi-konstelasi) dan tipe kalimat diikuti field yang dipisah koma serta checksum XOR.\n\nNmeaReader memvalidasi checksum, mengurai pesan bertipe (GgaMessage, RmcMessage, ...) dan menyimpan GnssState berisi fix terbaru. NmeaServer menyiarkan kalimat ke banyak klien — API yang sama bekerja lewat GPS serial (UseSerial) atau multiplexer NMEA-over-TCP (UseTcp).");
    public override string Category => "Navigation";
    public override IReadOnlyList<string> Protocols => ["NMEA 0183"];
    public override string DocsPath => "docs/en/protocols/nmea.md";

    private InMemoryTransportListener? _listener;
    private NmeaServer? _server;
    private NmeaReader? _reader;
    private CancellationTokenSource? _cts;

    /// <summary>Track points (lat, lon) for the plot.</summary>
    public List<(double Lat, double Lon)> Track { get; } = [];
    public ObservableCollection<SatelliteInfo> Satellites { get; } = [];
    public event Action? TrackChanged;

    [ObservableProperty] private double _latitude;
    [ObservableProperty] private double _longitude;
    [ObservableProperty] private double _speed;
    [ObservableProperty] private double _course;
    [ObservableProperty] private int _satellitesUsed;
    [ObservableProperty] private double _hdop;
    [ObservableProperty] private string _fixQuality = "—";
    [ObservableProperty] private string _lastSentence = "";

    protected override async Task OnStartAsync()
    {
        _listener = new InMemoryTransportListener("gps");
        _server = NmeaServer.Create(o => o.ListenInMemory(_listener));
        await _server.StartAsync();

        _reader = NmeaReader.Create(o => o.UseInMemory(_listener));
        _reader.AddTap(Tap);
        _reader.MessageReceived += m => Ui(() => LastSentence = m.Sentence.Raw);
        _reader.Gnss.Updated += OnFix;
        await _reader.ConnectAsync();

        // Simulated receiver: one epoch every 500 ms, running at 4x speed so the track grows quickly.
        var simulator = new NmeaSimulator(speedKmh: 54);
        _cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            var start = DateTimeOffset.UtcNow;
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
            while (await timer.WaitForNextTickAsync(_cts.Token))
            {
                var now = DateTimeOffset.UtcNow;
                foreach (var sentence in simulator.GenerateEpoch(now, (now - start) * 4)) await _server.BroadcastAsync(sentence, _cts.Token);
            }
        });
        SetStatus(new Text("Receiving NMEA from the simulated receiver at 2 Hz.", "Menerima NMEA dari penerima simulasi pada 2 Hz."));
    }

    private void OnFix(GnssFix fix)
    {
        if (!fix.HasFix) return;
        Ui(() =>
        {
            Latitude = fix.Latitude!.Value;
            Longitude = fix.Longitude!.Value;
            Speed = fix.SpeedKmh ?? 0;
            Course = fix.CourseDegrees ?? 0;
            SatellitesUsed = fix.SatellitesUsed;
            Hdop = fix.Hdop ?? 0;
            FixQuality = fix.Quality.ToString();
            if (Track.Count == 0 || Track[^1] != (Latitude, Longitude))
            {
                Track.Add((Latitude, Longitude));
                if (Track.Count > 2000) Track.RemoveAt(0);
                TrackChanged?.Invoke();
            }
            if (fix.SatellitesInView.Count > 0 && (Satellites.Count != fix.SatellitesInView.Count || !Satellites.SequenceEqual(fix.SatellitesInView)))
            {
                Satellites.Clear();
                foreach (var s in fix.SatellitesInView) Satellites.Add(s);
            }
        });
    }

    protected override async Task OnStopAsync()
    {
        if (_cts is not null) await _cts.CancelAsync();
        if (_reader is not null) await _reader.DisposeAsync();
        if (_server is not null) await _server.DisposeAsync();
        _cts?.Dispose();
        (_reader, _server, _cts) = (null, null, null);
        Status = "";
    }
}
