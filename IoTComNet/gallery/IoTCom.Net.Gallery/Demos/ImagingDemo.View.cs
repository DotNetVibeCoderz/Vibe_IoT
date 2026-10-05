using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using IoTCom.Net.Adapters.Dicom;
using IoTCom.Net.Gallery.Infrastructure;
using IoTCom.Samples.Medical;
using static IoTCom.Net.Gallery.Infrastructure.Loc;
using static IoTCom.Net.Gallery.Infrastructure.UiKit;

namespace IoTCom.Net.Gallery.Demos;

public sealed partial class ImagingDemo
{
    private static readonly IBrush ViewerBg = new SolidColorBrush(Color.Parse("#050607"));
    private static readonly IBrush ViewerText = new SolidColorBrush(Color.Parse("#B8C0C8"));

    protected override Control CreateView()
    {
        // ---- acquisition (the simulated modality) ---------------------------------------------------
        Button Acquire(string label, SyntheticModality m)
        {
            var b = new Button { Content = label, Classes = { "ghost" }, HorizontalAlignment = HorizontalAlignment.Stretch };
            b.Click += async (_, _) => await AcquireAsync(m);
            b.Bind(InputElement.IsEnabledProperty, new Binding(nameof(IsRunning)));
            return b;
        }
        var acquire = Card(Stack(8,
            Eyebrow(L("Modality", "Modalitas")),
            Acquire(L("CT chest", "CT dada"), SyntheticModality.ChestCt),
            Acquire(L("MRI brain", "MRI otak"), SyntheticModality.BrainMr),
            Acquire(L("X-ray chest", "Rontgen dada"), SyntheticModality.ChestXray),
            new TextBlock { Text = L("Each study gets a random planted finding (or none).", "Setiap studi mendapat temuan acak (atau tanpa temuan)."), Classes = { "muted" }, FontSize = 11.5, TextWrapping = TextWrapping.Wrap }));

        var worklist = new ListBox { MaxHeight = 360, Background = Brushes.Transparent };
        worklist.ItemTemplate = new FuncDataTemplate<StudyItem>((s, _) => Stack(1,
            Row(8, new Border { Classes = { "chip" }, Child = new TextBlock { Text = s?.Modality } }, new TextBlock { Text = s?.Time, Classes = { "mono", "muted" }, VerticalAlignment = VerticalAlignment.Center }),
            new TextBlock { Text = s?.Description, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis }));
        worklist.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(Studies)));
        worklist.Bind(SelectingItemsControl.SelectedItemProperty, new Binding(nameof(Selected)) { Mode = BindingMode.TwoWay });
        var left = Stack(14, acquire, Card(Stack(6, Eyebrow(L("PACS worklist", "Daftar kerja PACS")), worklist)));

        // ---- viewer -------------------------------------------------------------------------------------
        var image = new Image { Stretch = Stretch.Uniform };
        image.Bind(Image.SourceProperty, new Binding(nameof(Png)) { Converter = new FuncValueConverter<byte[]?, Bitmap?>(b => b is null ? null : new Bitmap(new MemoryStream(b))) });
        var overlay = new TextBlock { Foreground = ViewerText, FontSize = 11.5, Margin = new Thickness(10), FontFamily = (FontFamily)Application.Current!.FindResource("MonoFont")! };
        overlay.Bind(TextBlock.TextProperty, new Binding(nameof(Selected)) { Converter = new FuncValueConverter<StudyItem?, string>(s => s is null ? "" :
            $"{s.Received.PatientName}\n{s.Description}\n{s.Modality} · {s.Received.File.Dataset.GetSingleValueOrDefault(FellowOakDicom.DicomTag.Rows, (ushort)0)}×{s.Received.File.Dataset.GetSingleValueOrDefault(FellowOakDicom.DicomTag.Columns, (ushort)0)} · from {s.Received.CallingAe}") });
        var empty = new TextBlock { Text = L("Acquire a study to view it here.", "Akuisisi studi untuk melihatnya di sini."), Foreground = ViewerText, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        empty.Bind(Visual.IsVisibleProperty, new Binding(nameof(Png)) { Converter = ObjectConverters.IsNull });
        var viewer = new Border
        {
            Background = ViewerBg,
            CornerRadius = new CornerRadius(12),
            Height = 520,
            ClipToBounds = true,
            Child = new Panel { Children = { empty, image, overlay } },
        };
        var presets = new ComboBox { ItemsSource = new[] { "Default" }.Concat(DicomRenderer.Presets.Keys.Where(k => k.StartsWith("CT", StringComparison.Ordinal))).ToArray(), MinWidth = 170 };
        presets.Bind(SelectingItemsControl.SelectedItemProperty, new Binding(nameof(WindowPreset)) { Mode = BindingMode.TwoWay });
        var center = Stack(10, viewer, Row(10, Eyebrow(L("Window (CT)", "Window (CT)")), presets));

        // ---- AI pre-read ------------------------------------------------------------------------------------
        var reportHost = new ContentControl();
        void RebuildReport()
        {
            var s = Selected;
            reportHost.Content = s is null ? null : Report(s);
        }
        PropertyChanged += (_, e) => { if (e.PropertyName is nameof(Selected) or nameof(ShowTruth)) RebuildReport(); };
        var truthToggle = new CheckBox { Content = L("Reveal planted finding (ground truth)", "Tampilkan temuan yang ditanam (ground truth)") };
        truthToggle.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(ShowTruth)));
        var right = Card(Stack(10,
            Eyebrow(L("AI pre-read", "Pra-baca AI")),
            new TextBlock { Text = L("Engine: ", "Mesin: ") + AiLabel, Classes = { "muted" }, FontSize = 12, TextWrapping = TextWrapping.Wrap },
            reportHost,
            truthToggle,
            new TextBlock { Text = "⚠ " + ClinicalAssistant.Disclaimer(Instance.Language) + " " + L("Phantoms are schematic, not real anatomy.", "Phantom bersifat skematis, bukan anatomi nyata."), Foreground = Palette.Amber, FontSize = 11.5, TextWrapping = TextWrapping.Wrap }));

        return new ScrollViewer
        {
            DataContext = this,
            Content = Columns("230,14,1.25*,14,*", left, new Border(), center, new Border(), right),
        };
    }

    private Control Report(StudyItem s)
    {
        var analyze = new Button { Content = L("RUN AI PRE-READ", "JALANKAN PRA-BACA AI"), Classes = { "primary" } };
        analyze.Click += async (_, _) => await AnalyzeAsync();
        analyze.Bind(InputElement.IsEnabledProperty, new Binding(nameof(StudyItem.Analyzing)) { Converter = new FuncValueConverter<bool, bool>(b => !b) });
        var busy = new TextBlock { Text = L("Reading the image…", "Membaca gambar…"), Classes = { "muted" } };
        busy.Bind(Visual.IsVisibleProperty, new Binding(nameof(StudyItem.Analyzing)));

        var body = new ContentControl();
        void Fill()
        {
            var r = s.Report;
            if (r is null) { body.Content = null; return; }
            var findings = new StackPanel { Spacing = 4 };
            foreach (var f in r.Findings) findings.Children.Add(new TextBlock { Text = "• " + f, TextWrapping = TextWrapping.Wrap });
            var badges = Row(6,
                Chip(L("confidence ", "keyakinan ") + r.Confidence),
                r.Urgent ? new Border { Classes = { "chip" }, BorderBrush = Palette.Red, Child = new TextBlock { Text = L("URGENT", "MENDESAK"), Foreground = Palette.Red } } : new Border());
            var verdict = r.MatchesGroundTruth is { } m && ShowTruth
                ? new Border
                {
                    Classes = { "chip" },
                    BorderBrush = m ? Palette.Green : Palette.Red,
                    Child = new TextBlock { Text = m ? L("✓ matches planted finding", "✓ sesuai temuan yang ditanam") : L("✗ missed planted finding", "✗ melewatkan temuan yang ditanam"), Foreground = m ? Palette.Green : Palette.Red },
                }
                : (Control)new Border();
            body.Content = Stack(8,
                findings,
                new TextBlock { Text = r.Impression, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                badges,
                verdict);
        }
        s.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(StudyItem.Report)) Fill(); };
        Fill();

        var truth = new TextBlock { Text = L("Planted: ", "Ditanam: ") + s.GroundTruthText, TextWrapping = TextWrapping.Wrap, Foreground = Palette.Blue, FontSize = 12.5, IsVisible = ShowTruth };
        return new ContentControl { DataContext = s, Content = Stack(10, analyze, busy, body, truth) };
    }
}
