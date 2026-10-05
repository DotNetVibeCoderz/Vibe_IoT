using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using IoTCom.Net.Gallery.Infrastructure;
using IoTCom.Net.Protocols.Hl7;
using IoTCom.Net.Transports;
using IoTCom.Samples.Medical;

namespace IoTCom.Net.Gallery.Demos;

/// <summary>One monitored bed as shown on the ward board.</summary>
public sealed partial class BedState : ObservableObject
{
    public BedState(Hl7Patient patient, string bed, PatientScenario scenario)
    {
        Patient = patient;
        Bed = bed;
        Scenario = scenario;
    }

    public Hl7Patient Patient { get; }
    public string Bed { get; }
    public PatientScenario Scenario { get; }
    public VitalsAnalyzer Analyzer { get; } = new();
    public TrendBuffer HeartRate { get; } = new(180, 10);
    public TrendBuffer SpO2 { get; } = new(180, 4);
    public TrendBuffer Systolic { get; } = new(180, 10);
    public string Title => $"{Bed} · {Patient.DisplayName}";
    public string Demographics => $"{Patient.Sex} · {Patient.AgeOn(DateOnly.FromDateTime(DateTime.Today))} y · {Patient.Id}";

    [ObservableProperty] private VitalsSample _latest;
    [ObservableProperty] private ClinicalSnapshot? _snapshot;
    [ObservableProperty] private int _news2;
    [ObservableProperty] private RiskLevel _risk;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _summarizing;
}

/// <summary>
/// ICU ward: bedside monitors (simulated) send HL7 v2 ORU^R01 vitals over MLLP to a receiver; the dashboard computes
/// NEWS2, trends and anomalies per patient, and an LLM writes an SBAR note for the doctor on request.
/// Patients and data are synthetic — decision-support illustration only.
/// </summary>
public sealed partial class BedsideMonitorDemo : GalleryDemo
{
    public override string Id => "hl7-icu";
    public override Text Title => new("ICU bedside monitors (HL7)", "Monitor pasien ICU (HL7)");
    public override Text Summary => new(
        "Four simulated bedside monitors stream HL7 ORU^R01 vitals over MLLP. The board scores NEWS2, tracks trends and anomalies per patient, and an AI model drafts an SBAR note for the doctor. Synthetic patients — not for clinical use.",
        "Empat monitor pasien simulasi mengirim tanda vital HL7 ORU^R01 lewat MLLP. Papan menghitung NEWS2, memantau tren dan anomali per pasien, dan model AI menyusun catatan SBAR untuk dokter. Pasien sintetis — bukan untuk penggunaan klinis.");
    public override Text Docs => new(
        "HL7 v2 is how hospital devices and systems exchange events: ADT for admissions, ORU for results. A bedside monitor sends an ORU^R01 with one OBX segment per measurement (LOINC-coded: 8867-4 heart rate, 59408-5 SpO2, 8480-6 systolic BP…), framed by MLLP (0x0B … 0x1C 0x0D) over TCP. The receiver answers every message with an ACK (MSA AA).\n\nHl7MllpServer receives and auto-acknowledges; Hl7MllpClient sends and waits for the matching ACK; PatientMonitorSimulator produces plausible vitals for stable, sepsis, hypoxia and hypertension courses.\n\nThe analysis layer (sample code) computes NEWS2 (RCP 2017; room air and an alert patient assumed), least-squares trends with a 15-minute projection, and EWMA z-score anomalies against each patient's own baseline. 'Ask AI' sends a compact, de-identified summary of those numbers to the configured model (Azure OpenAI, Hugging Face or DeepSeek, via IOTCOM_AI_* variables) and shows an SBAR note. Without AI it falls back to a rule-based note.",
        "HL7 v2 adalah cara perangkat dan sistem rumah sakit bertukar kejadian: ADT untuk penerimaan pasien, ORU untuk hasil. Monitor pasien mengirim ORU^R01 dengan satu segmen OBX per pengukuran (berkode LOINC: 8867-4 denyut jantung, 59408-5 SpO2, 8480-6 tekanan sistolik…), dibingkai MLLP (0x0B … 0x1C 0x0D) lewat TCP. Penerima menjawab setiap pesan dengan ACK (MSA AA).\n\nHl7MllpServer menerima dan otomatis mengakui; Hl7MllpClient mengirim dan menunggu ACK yang cocok; PatientMonitorSimulator menghasilkan tanda vital yang masuk akal untuk kondisi stabil, sepsis, hipoksia, dan hipertensi.\n\nLapisan analisis (kode sampel) menghitung NEWS2 (RCP 2017; diasumsikan udara ruangan dan pasien sadar), tren kuadrat terkecil dengan proyeksi 15 menit, dan anomali z-score EWMA terhadap baseline masing-masing pasien. 'Tanya AI' mengirim ringkasan angka yang ringkas ke model yang dikonfigurasi (Azure OpenAI, Hugging Face, atau DeepSeek, lewat variabel IOTCOM_AI_*) lalu menampilkan catatan SBAR. Tanpa AI, digunakan catatan berbasis aturan.");
    public override string Category => "Medical";
    public override IReadOnlyList<string> Protocols => ["HL7 v2.5.1", "MLLP", "NEWS2", "LLM"];
    public override Difficulty Difficulty => Difficulty.Intermediate;
    public override string DocsPath => "docs/en/protocols/hl7.md";

    private InMemoryTransportListener? _link;
    private Hl7MllpServer? _receiver;
    private readonly List<Hl7MllpClient> _monitors = [];
    private CancellationTokenSource? _cts;
    private readonly ClinicalAssistant _assistant = new(new AiClient(AiSettings.Load()));

    public ObservableCollection<BedState> Beds { get; } = [];

    [ObservableProperty] private BedState? _selected;
    [ObservableProperty] private long _messages;

    public string AiLabel => _assistant.EngineLabel;

    protected override async Task OnStartAsync()
    {
        _link = new InMemoryTransportListener("hl7-icu");
        _receiver = Hl7MllpServer.Create(o => o.ListenInMemory(_link));
        _receiver.AddTap(Tap);
        _receiver.MessageReceived += OnMessage;
        await _receiver.StartAsync();

        Beds.Clear();
        foreach (var (patient, scenario, bed) in PatientMonitorSimulator.DemoWard) Beds.Add(new BedState(patient, bed, scenario));
        Selected = Beds[0];

        // Each monitor is its own MLLP connection, like real devices on the network. Time runs 10× faster.
        _cts = new CancellationTokenSource();
        var start = DateTimeOffset.Now;
        var seed = 11;
        foreach (var b in Beds)
        {
            var monitor = Hl7MllpClient.Create(o => o.UseInMemory(_link));
            _monitors.Add(monitor);
            var sim = new PatientMonitorSimulator(b.Patient, b.Scenario, b.Bed, seed: seed++, onset: TimeSpan.FromMinutes(1.5));
            _ = Task.Run(() => RunMonitorAsync(monitor, sim, start, _cts.Token));
        }
        SetStatus(new Text("4 monitors streaming ORU^R01 over MLLP · simulated time runs 10× faster.",
            "4 monitor mengirim ORU^R01 lewat MLLP · waktu simulasi 10× lebih cepat."));
    }

    private static async Task RunMonitorAsync(Hl7MllpClient monitor, PatientMonitorSimulator sim, DateTimeOffset start, CancellationToken ct)
    {
        try
        {
            await monitor.SendAsync(sim.Admission(start), ct: ct);
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
            var simTime = start;
            while (await timer.WaitForNextTickAsync(ct))
            {
                simTime = simTime.AddSeconds(5);
                await monitor.SendAsync(sim.ToOru(sim.Next(TimeSpan.FromSeconds(5), simTime)), ct: ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (IoTComException) { }
    }

    private void OnMessage(object? sender, Hl7MessageReceivedEventArgs e)
    {
        if (e.Message.MessageType != "ORU^R01") return;
        var id = e.Message.GetPatient()?.Id;
        var vitals = PatientMonitorSimulator.FromOru(e.Message);
        Ui(() =>
        {
            Messages = _receiver?.MessagesReceived ?? 0;
            var bed = Beds.FirstOrDefault(b => b.Patient.Id == id);
            if (bed is null) return;
            bed.Analyzer.Add(vitals);
            bed.HeartRate.Add(vitals.HeartRate);
            bed.SpO2.Add(vitals.SpO2);
            bed.Systolic.Add(vitals.Systolic);
            var snap = bed.Analyzer.Analyze(bed.Patient, bed.Bed);
            bed.Latest = vitals;
            bed.Snapshot = snap;
            bed.News2 = snap.News2.Total;
            bed.Risk = snap.News2.Risk;
        });
    }

    /// <summary>Asks the model for an SBAR note on the selected patient.</summary>
    public async Task SummarizeAsync()
    {
        var bed = Selected;
        if (bed?.Snapshot is null || bed.Summarizing) return;
        bed.Summarizing = true;
        bed.Summary = Loc.L("Asking the model…", "Menghubungi model…");
        try
        {
            var snapshot = bed.Snapshot;
            var note = await _assistant.SummarizeAsync(snapshot, bed.Analyzer.History, Loc.Instance.Language);
            bed.Summary = Loc.L($"_Generated {DateTime.Now:HH:mm:ss} from NEWS2 {snapshot.News2.Total} · {_assistant.EngineLabel}_", $"_Dibuat {DateTime.Now:HH:mm:ss} dari NEWS2 {snapshot.News2.Total} · {_assistant.EngineLabel}_") + Environment.NewLine + note;
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            bed.Summary = Loc.L("AI request failed: ", "Permintaan AI gagal: ") + ex.Message + "\n\n" + ClinicalAssistant.Template(bed.Snapshot, Loc.Instance.Language);
        }
        finally
        {
            bed.Summarizing = false;
        }
    }

    protected override async Task OnStopAsync()
    {
        if (_cts is not null) await _cts.CancelAsync();
        foreach (var m in _monitors) await m.DisposeAsync();
        _monitors.Clear();
        if (_receiver is not null) await _receiver.DisposeAsync();
        _cts?.Dispose();
        (_receiver, _cts) = (null, null);
        Status = "";
    }
}
