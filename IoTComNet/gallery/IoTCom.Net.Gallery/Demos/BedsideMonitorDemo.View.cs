using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using IoTCom.Net.Gallery.Infrastructure;
using IoTCom.Samples.Medical;
using static IoTCom.Net.Gallery.Infrastructure.Loc;
using static IoTCom.Net.Gallery.Infrastructure.UiKit;

namespace IoTCom.Net.Gallery.Demos;

public sealed partial class BedsideMonitorDemo
{
    // Bedside-monitor trace colours (the convention clinicians read at a glance).
    private static readonly IBrush MonitorBg = new SolidColorBrush(Color.Parse("#0B0E11"));
    private static readonly IBrush TraceHr = new SolidColorBrush(Color.Parse("#3DDC84"));
    private static readonly IBrush TraceSpO2 = new SolidColorBrush(Color.Parse("#3CC8F0"));
    private static readonly IBrush TraceBp = new SolidColorBrush(Color.Parse("#FF6B6B"));
    private static readonly IBrush TraceRr = new SolidColorBrush(Color.Parse("#F5D547"));
    private static readonly IBrush TraceTemp = new SolidColorBrush(Color.Parse("#E8E8E8"));
    private static readonly IBrush MonitorLabel = new SolidColorBrush(Color.Parse("#8C96A0"));

    internal static IBrush RiskBrush(RiskLevel r) => r switch
    {
        RiskLevel.High => Palette.Red,
        RiskLevel.Medium => new SolidColorBrush(Color.Parse("#E8772E")),
        RiskLevel.LowMedium => Palette.Amber,
        _ => Palette.Green,
    };

    internal static string RiskText(RiskLevel r) => r switch
    {
        RiskLevel.High => L("HIGH", "TINGGI"),
        RiskLevel.Medium => L("MEDIUM", "SEDANG"),
        RiskLevel.LowMedium => L("LOW–MED", "RENDAH–SDG"),
        _ => L("LOW", "RENDAH"),
    };

    protected override Control CreateView()
    {
        var riskBrush = new FuncValueConverter<RiskLevel, IBrush>(r => RiskBrush(r));

        // ---- ward board ------------------------------------------------------------------------------
        var board = new ItemsControl
        {
            ItemsPanel = new FuncTemplate<Panel?>(() => new UniformGrid { Columns = 4 }),
            ItemTemplate = new FuncDataTemplate<BedState>((bed, _) =>
            {
                var news = new TextBlock { Classes = { "readout" }, FontSize = 40, LineHeight = 42 };
                news.Bind(TextBlock.TextProperty, new Binding(nameof(BedState.News2)));
                news.Bind(TextBlock.ForegroundProperty, new Binding(nameof(BedState.Risk)) { Converter = riskBrush });
                var risk = new TextBlock { Classes = { "eyebrow" } };
                risk.Bind(TextBlock.TextProperty, new Binding(nameof(BedState.Risk)) { Converter = new FuncValueConverter<RiskLevel, string>(r => "NEWS2 · " + RiskText(r)) });
                var mini = new TextBlock { Classes = { "mono" }, FontSize = 11 };
                mini.Bind(TextBlock.TextProperty, new Binding(nameof(BedState.Latest))
                {
                    Converter = new FuncValueConverter<Net.Protocols.Hl7.VitalsSample, string>(v => v.HeartRate > 0
                        ? $"HR {v.HeartRate:0} · SpO₂ {v.SpO2:0} · BP {v.Systolic:0}/{v.Diastolic:0}"
                        : "—"),
                });
                var strip = new Border { Width = 6, CornerRadius = new CornerRadius(3) };
                strip.Bind(Border.BackgroundProperty, new Binding(nameof(BedState.Risk)) { Converter = riskBrush });
                var content = Stack(4,
                    new TextBlock { Text = bed?.Title, FontWeight = FontWeight.SemiBold, FontSize = 15, TextTrimming = TextTrimming.CharacterEllipsis },
                    new TextBlock { Text = bed?.Demographics, Classes = { "muted" }, FontSize = 12 },
                    Row(10, news, new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { risk } }),
                    mini);
                var tile = new Border
                {
                    Classes = { "card" },
                    Margin = new Thickness(0, 0, 12, 0),
                    Padding = new Thickness(14, 12),
                    Cursor = new Cursor(StandardCursorType.Hand),
                    Child = Columns("Auto,12,*", strip, new Border(), content),
                };
                tile.PointerPressed += (_, _) => { if (bed is not null) Selected = bed; };
                return tile;
            }),
        };
        board.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(Beds)));

        // ---- selected patient ------------------------------------------------------------------------
        var detailHost = new ContentControl();
        void Rebuild() => detailHost.Content = Selected is { } s ? Detail(s) : new TextBlock { Text = Loc.Instance["idle"], Classes = { "muted" } };
        PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Selected)) Rebuild(); };
        Rebuild();

        var engine = new TextBlock { Classes = { "muted" }, FontSize = 12, Text = L("AI engine: ", "Mesin AI: ") + AiLabel };
        var count = new TextBlock { Classes = { "muted" }, FontSize = 12 };
        count.Bind(TextBlock.TextProperty, new Binding(nameof(Messages)) { StringFormat = L("HL7 messages received and acknowledged: {0:N0}", "Pesan HL7 diterima dan di-ACK: {0:N0}") });
        var disclaimer = new TextBlock { Text = "⚠ " + ClinicalAssistant.Disclaimer(Instance.Language), Foreground = Palette.Amber, FontSize = 12, TextWrapping = TextWrapping.Wrap };

        return new ScrollViewer
        {
            DataContext = this,
            Content = Stack(14, board, detailHost, Row(24, count, engine), disclaimer),
        };
    }

    private Control Detail(BedState bed)
    {
        // Monitor screen
        Control Channel(string label, string unit, IBrush brush, string path, string format, TrendBuffer? trend)
        {
            var value = new TextBlock { FontFamily = (FontFamily)Application.Current!.FindResource("DisplayFont")!, FontSize = 46, FontWeight = FontWeight.SemiBold, Foreground = brush, LineHeight = 48 };
            value.Bind(TextBlock.TextProperty, new Binding($"{nameof(BedState.Latest)}.{path}") { StringFormat = format });
            var left = Stack(0,
                new TextBlock { Text = label, Foreground = brush, FontFamily = (FontFamily)Application.Current!.FindResource("DisplayFont")!, FontWeight = FontWeight.Bold, FontSize = 13, LetterSpacing = 1.5 },
                Row(6, value, new TextBlock { Text = unit, Foreground = MonitorLabel, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 8) }));
            var spark = trend is null ? (Control)new Border() : Sparkline(trend, brush, 52);
            return new Border
            {
                BorderBrush = new SolidColorBrush(Color.Parse("#1E252B")),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(0, 6),
                Child = Columns("170,*", left, spark),
            };
        }
        var bp = new TextBlock { FontFamily = (FontFamily)Application.Current!.FindResource("DisplayFont")!, FontSize = 46, FontWeight = FontWeight.SemiBold, Foreground = TraceBp, LineHeight = 48 };
        bp.Bind(TextBlock.TextProperty, new MultiBinding
        {
            Bindings = { new Binding($"{nameof(BedState.Latest)}.Systolic"), new Binding($"{nameof(BedState.Latest)}.Diastolic") },
            StringFormat = "{0:0}/{1:0}",
        });
        var bpRow = new Border
        {
            BorderBrush = new SolidColorBrush(Color.Parse("#1E252B")), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 6),
            Child = Columns("170,*",
                Stack(0, new TextBlock { Text = "NIBP", Foreground = TraceBp, FontWeight = FontWeight.Bold, FontSize = 13, LetterSpacing = 1.5 }, Row(6, bp, new TextBlock { Text = "mmHg", Foreground = MonitorLabel, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 8) })),
                Sparkline(bed.Systolic, TraceBp, 52)),
        };
        var screen = new Border
        {
            Background = MonitorBg,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(18, 10),
            Child = Stack(0,
                new TextBlock { Text = bed.Title.ToUpperInvariant(), Foreground = MonitorLabel, FontSize = 12, LetterSpacing = 1.2, Margin = new Thickness(0, 4, 0, 4) },
                Channel("HR", "bpm", TraceHr, "HeartRate", "{0:0}", bed.HeartRate),
                Channel("SpO₂", "%", TraceSpO2, "SpO2", "{0:0}", bed.SpO2),
                bpRow,
                Channel("RR", "rpm", TraceRr, "RespiratoryRate", "{0:0}", null),
                Channel("TEMP", "°C", TraceTemp, "Temperature", "{0:0.0}", null)),
        };

        // NEWS2 + alerts
        var news = new TextBlock { Classes = { "readout" } };
        news.Bind(TextBlock.TextProperty, new Binding(nameof(BedState.News2)));
        news.Bind(TextBlock.ForegroundProperty, new Binding(nameof(BedState.Risk)) { Converter = new FuncValueConverter<RiskLevel, IBrush>(r => RiskBrush(r)) });
        var riskText = new TextBlock { Classes = { "value" } };
        riskText.Bind(TextBlock.TextProperty, new Binding(nameof(BedState.Risk)) { Converter = new FuncValueConverter<RiskLevel, string>(r => RiskText(r)) });
        var parts = new TextBlock { Classes = { "muted" }, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        parts.Bind(TextBlock.TextProperty, new Binding(nameof(BedState.Snapshot)) { Converter = new FuncValueConverter<ClinicalSnapshot?, string>(s => s is null ? "" :
            string.Join(" · ", s.News2.Parts.Where(p => p.Value > 0).Select(p => $"{p.Key} {p.Value}")) + "\n" + L("Projected in 15 min: ", "Proyeksi 15 menit: ") + s.ProjectedNews2.Total) });
        var alerts = new ItemsControl
        {
            ItemTemplate = new FuncDataTemplate<string>((a, _) => Columns("16,*",
                new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = Palette.Red, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 0, 0) },
                new TextBlock { Text = a, TextWrapping = TextWrapping.Wrap })),
        };
        alerts.Bind(ItemsControl.ItemsSourceProperty, new Binding($"{nameof(BedState.Snapshot)}.{nameof(ClinicalSnapshot.Alerts)}"));
        var newsCard = Card(Stack(8,
            Eyebrow("NEWS2"),
            Row(14, news, new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { riskText } }),
            parts,
            Eyebrow(L("Alerts", "Peringatan")),
            alerts));

        // AI summary
        var ask = new Button { Content = L("ASK AI FOR SBAR NOTE", "MINTA CATATAN SBAR KE AI"), Classes = { "primary" } };
        ask.Click += async (_, _) => await SummarizeAsync();
        ask.Bind(InputElement.IsEnabledProperty, new Binding(nameof(BedState.Summarizing)) { Converter = new FuncValueConverter<bool, bool>(b => !b) });
        var note = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13.5, LineHeight = 20 };
        void RenderNote() => note.Inlines = MarkdownInlines(bed.Summary);
        bed.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(BedState.Summary)) RenderNote(); };
        RenderNote();
        var aiCard = Card(Stack(10,
            Eyebrow(L("AI clinical summary", "Ringkasan klinis AI")),
            ask,
            note,
            new TextBlock { Text = ClinicalAssistant.Disclaimer(Instance.Language), Foreground = Palette.Amber, FontSize = 11.5, TextWrapping = TextWrapping.Wrap }));

        return new ContentControl
        {
            DataContext = bed,
            Content = Columns("1.25*,16,*", screen, new Border(), Stack(14, newsCard, aiCard)),
        };
    }

    /// <summary>Tiny Markdown subset for model output: **bold**, headings and bullets.</summary>
    internal static InlineCollection MarkdownInlines(string text)
    {
        var inlines = new InlineCollection();
        foreach (var rawLine in text.Replace("\r", "", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.TrimEnd();
            var heading = line.StartsWith('#');
            if (heading) line = line.TrimStart('#', ' ');
            if (line.Length > 2 && line[0] == '_' && line[^1] == '_')
            {
                inlines.Add(new Run(line[1..^1]) { FontStyle = FontStyle.Italic, Foreground = Brushes.Gray });
                inlines.Add(new LineBreak());
                continue;
            }
            if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal)) line = "• " + line[2..];
            var parts = line.Split("**");
            for (var i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length == 0) continue;
                inlines.Add(new Run(parts[i].Replace("_(", "(", StringComparison.Ordinal).Replace(")_", ")", StringComparison.Ordinal)) { FontWeight = heading || i % 2 == 1 ? FontWeight.Bold : FontWeight.Normal });
            }
            inlines.Add(new LineBreak());
        }
        return inlines;
    }
}
