using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using IoTCom.Net.Gallery.Infrastructure;
using IoTCom.Net.Gallery.ViewModels;

namespace IoTCom.Net.Gallery.Views;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.PropertyChanged += OnViewModelChanged;
                RenderCode(vm);
            }
        };
        // Tab headers are set from code: Fluent's tab strip keeps the old header width when a bound string changes.
        ApplyTabHeaders();
        Loc.Instance.LanguageChanged += ApplyTabHeaders;
        ActualThemeVariantChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm) RenderCode(vm);
        };
    }

    private void ApplyTabHeaders()
    {
        RunTab.Header = Loc.Instance["run"];
        CodeTab.Header = Loc.Instance["code"];
        DocsTab.Header = Loc.Instance["docs"];
        TrafficTab.Header = Loc.Instance["traffic"];
        foreach (var l in Tabs.GetVisualDescendants().OfType<Layoutable>()) l.InvalidateMeasure();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedDemo) && sender is MainViewModel vm) RenderCode(vm);
    }

    /// <summary>Shows the demo's own source with syntax colours from the current theme.</summary>
    private void RenderCode(MainViewModel vm)
    {
        if (vm.SelectedDemo is null) return;
        IBrush B(string key) => this.TryFindResource(key, ActualThemeVariant, out var r) && r is IBrush b ? b : Brushes.Gray;
        var code = CodeHighlighter.LoadSource(vm.SelectedDemo.SourceFile);
        CodeView.Inlines = CodeHighlighter.Highlight(code, B("CodeKeyword"), B("CodeString"), B("CodeComment"), B("CodeType"), B("Ink2"));
    }
}
