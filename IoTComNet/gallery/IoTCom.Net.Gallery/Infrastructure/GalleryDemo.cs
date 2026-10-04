using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace IoTCom.Net.Gallery.Infrastructure;

/// <summary>Difficulty shown on the demo header.</summary>
public enum Difficulty
{
    Beginner,
    Intermediate,
    Advanced,
}

/// <summary>
/// Contract of a Gallery demo (plugin architecture: add a class, it appears in the navigation).
/// Every demo runs against in-process simulators, so no hardware is required.
/// </summary>
public interface IGalleryDemo : IAsyncDisposable
{
    string Id { get; }
    Text Title { get; }
    Text Summary { get; }
    Text Docs { get; }
    string Category { get; }
    IReadOnlyList<string> Protocols { get; }
    Difficulty Difficulty { get; }
    /// <summary>Embedded resource holding the demo source (the Code tab).</summary>
    string SourceFile { get; }
    /// <summary>Docs page in the repository.</summary>
    string DocsPath { get; }
    bool IsRunning { get; }
    /// <summary>True for workbench demos that are always live (no start/stop).</summary>
    bool AlwaysOn { get; }
    Control View { get; }
    RecordingTap Tap { get; }
    Task StartAsync();
    Task StopAsync();
}

/// <summary>Base class: run state, a shared tap and UI-thread helpers.</summary>
public abstract partial class GalleryDemo : ObservableObject, IGalleryDemo
{
    private Control? _view;

    public abstract string Id { get; }
    public abstract Text Title { get; }
    public abstract Text Summary { get; }
    public abstract Text Docs { get; }
    public abstract string Category { get; }
    public abstract IReadOnlyList<string> Protocols { get; }
    public virtual Difficulty Difficulty => Difficulty.Beginner;
    public string SourceFile => GetType().Name + ".cs";
    public abstract string DocsPath { get; }
    public virtual bool AlwaysOn => false;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _status = "";

    public RecordingTap Tap { get; } = new(500);

    public Control View => _view ??= CreateView();

    /// <summary>Drops the cached view so it is rebuilt (e.g. after a language switch).</summary>
    public void ResetView()
    {
        _view = null;
        if (_statusText is { } t) Status = Loc.T(t);
        OnPropertyChanged(nameof(View));
    }

    private Text? _statusText;

    /// <summary>Sets a bilingual status line that follows language switches.</summary>
    protected void SetStatus(Text text)
    {
        _statusText = text;
        Status = Loc.T(text);
    }

    /// <summary>Activity log lines shown by some demos.</summary>
    public ObservableCollection<string> Log { get; } = [];

    protected abstract Control CreateView();

    public async Task StartAsync()
    {
        if (IsRunning) return;
        try
        {
            await OnStartAsync();
            IsRunning = true;
        }
        catch (Exception ex)
        {
            Status = ex.Message;
            await OnStopAsync();
        }
    }

    public async Task StopAsync()
    {
        if (!IsRunning) return;
        IsRunning = false;
        await OnStopAsync();
    }

    protected abstract Task OnStartAsync();

    protected abstract Task OnStopAsync();

    protected static void Ui(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    protected void AddLog(string line)
    {
        Ui(() =>
        {
            Log.Insert(0, $"{DateTime.Now:HH:mm:ss}  {line}");
            while (Log.Count > 60) Log.RemoveAt(Log.Count - 1);
        });
    }

    public virtual async ValueTask DisposeAsync()
    {
        await StopAsync();
        GC.SuppressFinalize(this);
    }
}
