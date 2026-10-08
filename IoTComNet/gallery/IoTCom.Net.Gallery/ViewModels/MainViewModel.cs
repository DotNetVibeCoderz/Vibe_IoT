using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IoTCom.Net.Gallery.Demos;
using IoTCom.Net.Gallery.Infrastructure;

namespace IoTCom.Net.Gallery.ViewModels;

/// <summary>Navigation entry: either a category header or a demo.</summary>
public abstract record NavItem;

/// <summary>Category header in the rail.</summary>
public sealed record CategoryHeader(string Label) : NavItem;

/// <summary>Demo entry in the rail.</summary>
public sealed record DemoItem(IGalleryDemo Demo, string Title, string Tags) : NavItem;

public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly string[] CategoryOrder = ["Industrial", "Automotive", "Medical", "Navigation", "Building", "Lpwan", "Messaging", "Workbench"];
    private readonly List<IGalleryDemo> _demos;
    private readonly List<TrafficFrame> _incoming = [];
    private readonly Lock _gate = new();
    private readonly DispatcherTimer _flush;
    private IDisposable? _tapSubscription;

    public MainViewModel()
    {
        _demos = [new ModbusDemo(), new VehicleDiagnosticsDemo(), new BedsideMonitorDemo(), new ImagingDemo(), new NmeaDemo(), new DroneDemo(), new GreenhouseDemo(), new LightingDemo(), new LoRaWanDemo(), new MqttDemo(), new WorkbenchDemo()];
        foreach (var d in _demos.OfType<INotifyPropertyChanged>()) d.PropertyChanged += OnDemoPropertyChanged;
        Loc.Instance.LanguageChanged += OnLanguageChanged;
        _flush = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => FlushFrames());
        _flush.Start();
        RebuildNav();
        SelectedNav = Nav.OfType<DemoItem>().First();
    }

    public Loc.LocTable L => Loc.Instance.Table;

    public ObservableCollection<NavItem> Nav { get; } = [];

    public ObservableCollection<FrameRow> Frames { get; } = [];

    [ObservableProperty] private NavItem? _selectedNav;
    [ObservableProperty] private IGalleryDemo? _selectedDemo;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private FrameRow? _selectedFrame;
    [ObservableProperty] private FrameRow? _lastFrame;
    [ObservableProperty] private long _frameCount;
    [ObservableProperty] private bool _isDark;
    [ObservableProperty] private string _captureMessage = "";

    public string Title => SelectedDemo is null ? "" : Loc.T(SelectedDemo.Title);
    public string Summary => SelectedDemo is null ? "" : Loc.T(SelectedDemo.Summary);
    public string DocsText => SelectedDemo is null ? "" : Loc.T(SelectedDemo.Docs);
    public string DocsLink => SelectedDemo is null ? "" : SelectedDemo.DocsPath.Replace("docs/en/", $"docs/{Loc.Instance.Language}/", StringComparison.Ordinal);
    public string CategoryLabel => SelectedDemo is null ? "" : Loc.Instance[SelectedDemo.Category];
    public IReadOnlyList<string> Protocols => SelectedDemo?.Protocols ?? [];
    public string DifficultyLabel => SelectedDemo is null ? "" : Loc.Instance[SelectedDemo.Difficulty.ToString().ToLowerInvariant()];
    public bool IsRunning => SelectedDemo?.IsRunning ?? false;
    public bool CanToggle => SelectedDemo is { AlwaysOn: false };
    public string RunLabel => Loc.Instance[IsRunning ? "stop" : "start"];
    public string StatusText => SelectedDemo is GalleryDemo g && !string.IsNullOrEmpty(g.Status) ? g.Status
        : SelectedDemo is { AlwaysOn: true } ? Loc.T(SelectedDemo.Summary) : Loc.Instance["idle"];
    public string LanguageLabel => Loc.Instance["language"];
    public string ThemeLabel => Loc.Instance[IsDark ? "themeLight" : "theme"];
    public string Credit => Loc.Instance["credit"];
    public string Version => $"IoTCom.Net {IoTComInfo.Version}";
    public bool HasFrames => Frames.Count > 0;

    partial void OnSearchChanged(string value) => RebuildNav();

    partial void OnSelectedNavChanged(NavItem? value)
    {
        if (value is DemoItem d) SelectedDemo = d.Demo;
        else if (value is CategoryHeader) SelectedNav = Nav.OfType<DemoItem>().FirstOrDefault(i => i.Demo == SelectedDemo);
    }

    partial void OnSelectedDemoChanged(IGalleryDemo? value)
    {
        _tapSubscription?.Dispose();
        Frames.Clear();
        lock (_gate) _incoming.Clear();
        LastFrame = null;
        SelectedFrame = null;
        FrameCount = 0;
        if (value is not null)
        {
            void OnFrame(TrafficFrame f)
            {
                lock (_gate) _incoming.Add(f);
            }
            value.Tap.FrameCaptured += OnFrame;
            _tapSubscription = new Unsubscribe(() => value.Tap.FrameCaptured -= OnFrame);
            foreach (var f in value.Tap.Snapshot().TakeLast(100)) Frames.Insert(0, FrameRow.From(f));
            FrameCount = value.Tap.TotalFrames;
            LastFrame = Frames.FirstOrDefault();
        }
        RaiseDemoProperties();
    }

    [RelayCommand]
    private async Task ToggleRunAsync()
    {
        if (SelectedDemo is null || SelectedDemo.AlwaysOn) return;
        if (SelectedDemo.IsRunning) await SelectedDemo.StopAsync();
        else await SelectedDemo.StartAsync();
        RaiseDemoProperties();
    }

    [RelayCommand]
    private void ToggleLanguage() => Loc.Instance.Language = Loc.Instance.Language == "en" ? "id" : "en";

    [RelayCommand]
    private void ToggleTheme()
    {
        IsDark = !IsDark;
        if (Application.Current is { } app) app.RequestedThemeVariant = IsDark ? ThemeVariant.Dark : ThemeVariant.Light;
        OnPropertyChanged(nameof(ThemeLabel));
    }

    [RelayCommand]
    private void ClearFrames()
    {
        SelectedDemo?.Tap.Clear();
        Frames.Clear();
        LastFrame = null;
        FrameCount = 0;
        OnPropertyChanged(nameof(HasFrames));
    }

    /// <summary>Writes the current demo's captured frames to a pcapng file Wireshark can open.</summary>
    [RelayCommand]
    private void SaveCapture()
    {
        if (SelectedDemo is not { } demo) return;
        var frames = demo.Tap.Snapshot();
        if (frames.Count == 0)
        {
            CaptureMessage = Loc.L("Nothing captured yet — start the demo first.", "Belum ada yang ditangkap — jalankan demo dulu.");
            return;
        }
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "IoTCom.Net", "captures");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{demo.Id}-{DateTime.Now:yyyyMMdd-HHmmss}.pcapng");
        using (var pcap = PcapngTap.Create(path))
            foreach (var f in frames) pcap.OnFrame(f);
        CaptureMessage = $"{frames.Count} {Loc.L("frames saved to", "frame disimpan ke")} {path}";
    }

    /// <summary>Selects a demo by id (used by the screenshot tool).</summary>
    public void Select(string id) => SelectedNav = Nav.OfType<DemoItem>().FirstOrDefault(d => d.Demo.Id == id) ?? SelectedNav;

    private void FlushFrames()
    {
        List<TrafficFrame> batch;
        lock (_gate)
        {
            if (_incoming.Count == 0) return;
            batch = [.. _incoming.TakeLast(40)];
            _incoming.Clear();
        }
        foreach (var f in batch) Frames.Insert(0, FrameRow.From(f));
        while (Frames.Count > 150) Frames.RemoveAt(Frames.Count - 1);
        LastFrame = Frames[0];
        FrameCount = SelectedDemo?.Tap.TotalFrames ?? FrameCount;
        OnPropertyChanged(nameof(HasFrames));
    }

    private void RebuildNav()
    {
        var keep = SelectedDemo;
        Nav.Clear();
        var q = Search.Trim();
        foreach (var category in CategoryOrder)
        {
            var items = _demos.Where(d => d.Category == category).Where(d => q.Length == 0
                || Loc.T(d.Title).Contains(q, StringComparison.OrdinalIgnoreCase)
                || d.Protocols.Any(p => p.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();
            if (items.Count == 0) continue;
            Nav.Add(new CategoryHeader(Loc.Instance[category]));
            foreach (var d in items) Nav.Add(new DemoItem(d, Loc.T(d.Title), string.Join(" · ", d.Protocols)));
        }
        if (keep is not null) SelectedNav = Nav.OfType<DemoItem>().FirstOrDefault(i => i.Demo == keep);
    }

    private void OnLanguageChanged()
    {
        foreach (var d in _demos.OfType<GalleryDemo>()) d.ResetView();
        OnPropertyChanged(nameof(L));
        RebuildNav();
        RaiseDemoProperties();
        OnPropertyChanged(nameof(LanguageLabel));
        OnPropertyChanged(nameof(ThemeLabel));
        OnPropertyChanged(nameof(Credit));
    }

    private void OnDemoPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender != SelectedDemo) return;
        if (e.PropertyName is nameof(GalleryDemo.IsRunning) or nameof(GalleryDemo.Status))
            Dispatcher.UIThread.Post(RaiseDemoProperties);
    }

    private void RaiseDemoProperties()
    {
        foreach (var p in new[] { nameof(Title), nameof(Summary), nameof(DocsText), nameof(DocsLink), nameof(CategoryLabel), nameof(Protocols), nameof(DifficultyLabel), nameof(IsRunning), nameof(CanToggle), nameof(RunLabel), nameof(StatusText), nameof(HasFrames) })
            OnPropertyChanged(p);
    }

    public async ValueTask DisposeAsync()
    {
        _flush.Stop();
        foreach (var d in _demos) await d.DisposeAsync();
    }

    private sealed class Unsubscribe(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
