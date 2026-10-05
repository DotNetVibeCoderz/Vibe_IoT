using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using CommunityToolkit.Mvvm.ComponentModel;
using FellowOakDicom;
using IoTCom.Net.Adapters.Dicom;
using IoTCom.Net.Gallery.Infrastructure;
using IoTCom.Samples.Medical;

namespace IoTCom.Net.Gallery.Demos;

/// <summary>A study on the worklist.</summary>
public sealed partial class StudyItem : ObservableObject
{
    public StudyItem(DicomReceived received, SyntheticFinding groundTruth, string groundTruthText)
    {
        Received = received;
        GroundTruth = groundTruth;
        GroundTruthText = groundTruthText;
    }

    public DicomReceived Received { get; }
    public SyntheticFinding GroundTruth { get; }
    public string GroundTruthText { get; }
    public string Modality => Received.Modality;
    public string Description => Received.StudyDescription;
    public string Time => Received.ReceivedAt.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    [ObservableProperty] private ImagingReport? _report;
    [ObservableProperty] private bool _analyzing;
}

/// <summary>
/// Imaging pipeline: a simulated modality acquires a CT, MRI or X-ray study and pushes it with DICOM C-STORE to a
/// receiving node (the "PACS"). The viewer renders it with a VOI window, and a vision model pre-reads the image.
/// Each synthetic image has a planted finding, so the AI's answer is checked against ground truth.
/// </summary>
public sealed partial class ImagingDemo : GalleryDemo
{
    public override string Id => "dicom-ai";
    public override Text Title => new("Imaging AI pre-read (DICOM)", "Pra-baca gambar dengan AI (DICOM)");
    public override Text Summary => new(
        "A simulated modality sends CT, MRI and X-ray studies over DICOM C-STORE. View them with clinical window presets and let a vision model pre-read each image — checked against the finding planted in the synthetic phantom.",
        "Modalitas simulasi mengirim studi CT, MRI dan X-ray lewat DICOM C-STORE. Lihat dengan preset window klinis dan biarkan model vision pra-membaca setiap gambar — dicek terhadap temuan yang ditanam pada phantom sintetis.");
    public override Text Docs => new(
        "DICOM is both a file format and a network protocol. Modalities (CT, MR, DX…) send images to a PACS with C-STORE over an association between two Application Entities (AE titles); C-ECHO verifies connectivity.\n\nIoTCom.Net does not re-implement DICOM: IoTCom.Net.Adapters.Dicom wraps fo-dicom. DicomStoreServer is the receiving SCP (an IoTCom server endpoint with an async stream of received objects); DicomStoreClient is the sending SCU. DicomRenderer applies the modality rescale and the VOI window (center/width) to produce a grayscale image, and SyntheticImaging generates schematic phantoms with known findings (nodule, pneumothorax, consolidation, cardiomegaly, effusion, brain mass, infarct).\n\n'AI pre-read' sends the rendered PNG to the configured vision model and asks for structured findings. The planted finding is the ground truth, so you can see when the model is right and when it is not. Phantoms are schematic: this demonstrates the pipeline, not diagnostic accuracy.",
        "DICOM adalah format file sekaligus protokol jaringan. Modalitas (CT, MR, DX…) mengirim gambar ke PACS dengan C-STORE lewat asosiasi antara dua Application Entity (AE title); C-ECHO memverifikasi konektivitas.\n\nIoTCom.Net tidak menulis ulang DICOM: IoTCom.Net.Adapters.Dicom membungkus fo-dicom. DicomStoreServer adalah SCP penerima (endpoint server IoTCom dengan stream objek yang diterima); DicomStoreClient adalah SCU pengirim. DicomRenderer menerapkan rescale modalitas dan window VOI (center/width) untuk menghasilkan gambar grayscale, dan SyntheticImaging membuat phantom skematis dengan temuan yang diketahui (nodul, pneumotoraks, konsolidasi, kardiomegali, efusi, massa otak, infark).\n\n'Pra-baca AI' mengirim PNG hasil render ke model vision yang dikonfigurasi dan meminta temuan terstruktur. Temuan yang ditanam adalah ground truth, sehingga Anda bisa melihat kapan model benar dan kapan tidak. Phantom bersifat skematis: ini mendemonstrasikan alur kerjanya, bukan akurasi diagnostik.");
    public override string Category => "Medical";
    public override IReadOnlyList<string> Protocols => ["DICOM C-STORE", "fo-dicom", "Vision LLM"];
    public override Difficulty Difficulty => Difficulty.Advanced;
    public override string DocsPath => "docs/en/protocols/dicom.md";

    private DicomStoreServer? _pacs;
    private DicomStoreClient? _modality;
    private readonly ClinicalAssistant _assistant = new(new AiClient(AiSettings.Load()));
    private readonly Dictionary<string, (SyntheticFinding Finding, string Text)> _truth = [];
    private int _seed = 100;

    public ObservableCollection<StudyItem> Studies { get; } = [];

    [ObservableProperty] private StudyItem? _selected;
    [ObservableProperty] private byte[]? _png;
    [ObservableProperty] private string _windowPreset = "Default";
    [ObservableProperty] private bool _showTruth;
    [ObservableProperty] private int _port;

    public string AiLabel => _assistant.EngineLabel;

    protected override async Task OnStartAsync()
    {
        Port = FreePort();
        _pacs = DicomStoreServer.Create(o => { o.Port = Port; o.AeTitle = "IOTCOM-PACS"; });
        _pacs.AddTap(Tap);
        _pacs.ImageReceived += OnReceived;
        await _pacs.StartAsync();
        _modality = DicomStoreClient.Create(o => { o.Port = Port; o.CallingAe = "SIM-MODALITY"; o.CalledAe = "IOTCOM-PACS"; });
        _modality.AddTap(Tap);
        await _modality.ConnectAsync(); // C-ECHO
        SetStatus(new Text($"PACS (SCP) listening on DICOM port {Port} · C-ECHO OK.", $"PACS (SCP) mendengarkan di port DICOM {Port} · C-ECHO OK."));
    }

    /// <summary>Acquires a synthetic study and sends it with C-STORE.</summary>
    public async Task AcquireAsync(SyntheticModality modality, SyntheticFinding? finding = null)
    {
        if (_modality is null) return;
        var options = SyntheticImaging.FindingsFor(modality);
        var f = finding ?? options[Random.Shared.Next(options.Count)];
        var study = SyntheticImaging.Generate(modality, f, $"SYNTH^CASE{_seed}", $"SYN-{_seed}", _seed++);
        lock (_truth) _truth[study.File.Dataset.GetString(DicomTag.SOPInstanceUID)] = (f, study.FindingDescription);
        try
        {
            await _modality.StoreAsync(study.File);
        }
        catch (IoTComException ex)
        {
            Status = ex.Message;
        }
    }

    private void OnReceived(DicomReceived r)
    {
        (SyntheticFinding Finding, string Text) truth;
        lock (_truth) truth = _truth.TryGetValue(r.SopInstanceUid, out var t) ? t : (SyntheticFinding.None, "");
        Ui(() =>
        {
            var item = new StudyItem(r, truth.Finding, truth.Text);
            Studies.Insert(0, item);
            Selected = item;
        });
    }

    partial void OnSelectedChanged(StudyItem? value) => Render();

    partial void OnWindowPresetChanged(string value) => Render();

    private void Render()
    {
        if (Selected is null) { Png = null; return; }
        (double, double)? window = DicomRenderer.Presets.TryGetValue(WindowPreset, out var w) && Selected.Modality == "CT" ? w : null;
        Png = DicomRenderer.Render(Selected.Received.File.Dataset, window).ToPng();
    }

    /// <summary>Sends the current image to the vision model.</summary>
    public async Task AnalyzeAsync()
    {
        var item = Selected;
        if (item is null || item.Analyzing) return;
        item.Analyzing = true;
        try
        {
            // Always pre-read the default rendering (the window the modality chose), independent of the viewer preset.
            var png = DicomRenderer.Render(item.Received.File.Dataset).ToPng();
            item.Report = await _assistant.AnalyzeImageAsync(png, item.Modality, item.Description, item.GroundTruth, Loc.Instance.Language);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            item.Report = new ImagingReport([], Loc.L("AI request failed: ", "Permintaan AI gagal: ") + ex.Message, "—", false, AiLabel, null);
        }
        finally
        {
            item.Analyzing = false;
        }
    }

    protected override async Task OnStopAsync()
    {
        if (_modality is not null) await _modality.DisposeAsync();
        if (_pacs is not null) await _pacs.DisposeAsync();
        (_modality, _pacs) = (null, null);
        Status = "";
    }

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }
}
