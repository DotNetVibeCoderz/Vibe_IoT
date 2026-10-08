using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using IoTCom.Net.Gallery.Infrastructure;
using IoTCom.Net.Protocols.Nmea;

namespace IoTCom.Net.Gallery.Demos;

/// <summary>
/// Harbour traffic over AIS: simulated vessels in the approaches to Tanjung Priok (Jakarta Bay) broadcast class A
/// and class B reports as NMEA !AIVDM sentences; the demo reassembles fragments, decodes them and keeps a vessel table.
/// </summary>
public sealed partial class HarbourDemo : GalleryDemo
{
    public override string Id => "ais-harbour";
    public override Text Title => new("Harbour traffic (AIS)", "Lalu lintas pelabuhan (AIS)");
    public override Text Summary => new(
        "Ships in the approaches to Tanjung Priok broadcast AIS: positions every few seconds, names and destinations every few minutes. Watch the sentences arrive, the fragments join, and the chart fill up.",
        "Kapal di alur masuk Tanjung Priok memancarkan AIS: posisi setiap beberapa detik, nama dan tujuan setiap beberapa menit. Lihat kalimat datang, fragmen tersambung, dan peta terisi.");
    public override Text Docs => new(
        "AIS (Automatic Identification System) is the radio beacon every large ship carries. On the wire it arrives as NMEA sentences that start with !AIVDM: the binary message is packed six bits per character (\"armoured\"), and long messages are split over several sentences that share a sequence number.\n\nPosition reports (types 1–3 for class A ships, 18 for small class B craft) carry the MMSI, navigational status, speed and course over ground, heading and position. Static and voyage data (type 5, two sentences) adds the name, call sign, ship type, dimensions, draught and destination; class B boats send their name in type 24.\n\nA receiver therefore needs three steps: reassemble fragments, de-armour the bits, and decode fields by bit offset. Vessels appear on the chart as soon as a position arrives; their names follow when the static message comes round.",
        "AIS (Automatic Identification System) adalah suar radio yang dibawa setiap kapal besar. Di jalur, AIS datang sebagai kalimat NMEA yang diawali !AIVDM: pesan biner dipadatkan enam bit per karakter (\"armoured\"), dan pesan panjang dipecah ke beberapa kalimat yang berbagi nomor urut.\n\nLaporan posisi (tipe 1–3 untuk kapal kelas A, 18 untuk kapal kecil kelas B) membawa MMSI, status navigasi, kecepatan dan haluan terhadap dasar laut, heading, serta posisi. Data statis dan pelayaran (tipe 5, dua kalimat) menambahkan nama, call sign, tipe kapal, dimensi, draught, dan tujuan; kapal kelas B mengirim namanya di tipe 24.\n\nJadi penerima butuh tiga langkah: menyambung fragmen, membuka armour bit, dan mengurai field berdasarkan offset bit. Kapal muncul di peta begitu posisi datang; namanya menyusul ketika pesan statis tiba.");
    public override string Category => "Navigation";
    public override IReadOnlyList<string> Protocols => ["AIS", "NMEA 0183", "!AIVDM"];
    public override Difficulty Difficulty => Difficulty.Intermediate;
    public override string DocsPath => "docs/en/protocols/nmea.md";

    private CancellationTokenSource? _cts;

    /// <summary>The vessel table.</summary>
    public AisTracker Tracker { get; private set; } = new();

    /// <summary>Raw sentences, newest first.</summary>
    public ObservableCollection<string> Sentences { get; } = [];

    /// <summary>Vessel rows for the list.</summary>
    public ObservableCollection<VesselRow> Vessels { get; } = [];

    [ObservableProperty] private uint? _selectedMmsi;
    [ObservableProperty] private long _sentenceCount;
    [ObservableProperty] private long _messageCount;
    [ObservableProperty] private long _multiPart;

    /// <summary>Raised when the chart should redraw.</summary>
    public event Action? Changed;

    protected override Task OnStartAsync()
    {
        Tracker = new AisTracker();
        var sim = new AisSimulator();
        var decoder = new AisDecoder();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    foreach (var line in sim.Step(TimeSpan.FromSeconds(20)))
                    {
                        var parts = line.Split(',');
                        var message = decoder.Feed(line);
                        if (message is not null) Tracker.Apply(message);
                        Ui(() =>
                        {
                            SentenceCount++;
                            if (parts.Length > 1 && parts[1] != "1") MultiPart++;
                            if (message is not null) MessageCount++;
                            Sentences.Insert(0, line);
                            while (Sentences.Count > 9) Sentences.RemoveAt(Sentences.Count - 1);
                        });
                        await Task.Delay(60, ct);
                    }

                    Ui(Refresh);
                    await Task.Delay(400, ct);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, ct);
        SetStatus(new Text("Simulated AIS receiver · Jakarta Bay · 7 vessels (6 class A, 1 class B) · time runs 20× faster.",
            "Penerima AIS simulasi · Teluk Jakarta · 7 kapal (6 kelas A, 1 kelas B) · waktu berjalan 20× lebih cepat."));
        return Task.CompletedTask;
    }

    private void Refresh()
    {
        Vessels.Clear();
        foreach (var v in Tracker.Vessels.OrderBy(v => v.Name ?? "~", StringComparer.Ordinal)) Vessels.Add(new VesselRow(v));
        SelectedMmsi ??= Vessels.FirstOrDefault()?.Mmsi;
        OnPropertyChanged(nameof(Selected));
        Changed?.Invoke();
    }

    /// <summary>The selected vessel.</summary>
    public VesselRow? Selected => Vessels.FirstOrDefault(v => v.Mmsi == SelectedMmsi);

    partial void OnSelectedMmsiChanged(uint? value)
    {
        OnPropertyChanged(nameof(Selected));
        Changed?.Invoke();
    }

    protected override async Task OnStopAsync()
    {
        if (_cts is not null) await _cts.CancelAsync();
        _cts?.Dispose();
        _cts = null;
        Status = "";
    }
}

/// <summary>A vessel snapshot for the UI.</summary>
public sealed record VesselRow(uint Mmsi, string Name, string Kind, int? ShipType, double? Lat, double? Lon, double? Speed, double? Course, int? Heading, string Status, string Destination, string CallSign, int? Length, bool ClassB)
{
    public VesselRow(AisVessel v) : this(v.Mmsi, v.Name ?? $"MMSI {v.Mmsi}", Ais.ShipTypeName(v.ShipType), v.ShipType, v.Latitude, v.Longitude, v.Speed, v.Course, v.Heading,
        v.ClassB ? "class B" : StatusText(v.Status), v.Destination ?? "", v.CallSign ?? "", v.Length, v.ClassB)
    {
    }

    public string Line => $"{Speed?.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) ?? "–"} kn · {Course?.ToString("000", System.Globalization.CultureInfo.InvariantCulture) ?? "–"}°";

    private static string StatusText(AisNavigationStatus s) => s switch
    {
        AisNavigationStatus.UnderWayUsingEngine => Loc.L("under way", "berlayar"),
        AisNavigationStatus.AtAnchor => Loc.L("at anchor", "berlabuh jangkar"),
        AisNavigationStatus.Moored => Loc.L("moored", "tambat"),
        AisNavigationStatus.Fishing => Loc.L("fishing", "menangkap ikan"),
        _ => s.ToString(),
    };
}
