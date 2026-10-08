using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using IoTCom.Net.Gallery.Infrastructure;
using static IoTCom.Net.Gallery.Infrastructure.Loc;
using static IoTCom.Net.Gallery.Infrastructure.UiKit;

namespace IoTCom.Net.Gallery.Demos;

public sealed partial class J1939Demo
{
    private static readonly IBrush Dash = new SolidColorBrush(Color.Parse("#1B1F23"));
    private static readonly IBrush DashText = new SolidColorBrush(Color.Parse("#E4E5E0"));
    private static readonly IBrush AmberOff = new SolidColorBrush(Color.Parse("#3A3326"));

    protected override Control CreateView()
    {
        var mono = (FontFamily)Application.Current!.FindResource("MonoFont")!;

        Control Bar(string label, string path, string format, string unit, double max)
        {
            var value = new TextBlock { FontFamily = mono, FontSize = 15, FontWeight = FontWeight.Bold, Foreground = DashText };
            value.Bind(TextBlock.TextProperty, new Binding(path) { StringFormat = format + " " + unit.Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal) });
            var bar = new ProgressBar { Minimum = 0, Maximum = max, Height = 5, Foreground = Palette.Amber };
            bar.Bind(RangeBase.ValueProperty, new Binding(path));
            return Stack(3, new TextBlock { Text = label.ToUpperInvariant(), FontSize = 10, Foreground = Palette.Grey, LetterSpacing = 1 }, value, bar);
        }

        var lamp = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(17), Child = new TextBlock { Text = "⚠", FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Dash } };
        lamp.Bind(Border.BackgroundProperty, new Binding(nameof(Amber)) { Converter = new FuncValueConverter<bool, IBrush>(on => on ? Palette.Amber : AmberOff) });
        var dtcs = new ItemsControl { ItemTemplate = new FuncDataTemplate<string>((t, _) => new TextBlock { Text = t, FontFamily = mono, FontSize = 11.5, Foreground = Palette.Amber, TextWrapping = TextWrapping.Wrap }) };
        dtcs.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(Dtcs)));
        var vin = new TextBlock { FontFamily = mono, FontSize = 12, Foreground = DashText };
        vin.Bind(TextBlock.TextProperty, new Binding(nameof(Vin)) { StringFormat = "VIN {0}" });

        var cluster = new Border
        {
            Background = Dash, CornerRadius = new CornerRadius(18), Padding = new Thickness(22, 18),
            Child = Stack(14,
                Columns("*,*", new RoundGauge(this, true) { Height = 230 }, new RoundGauge(this, false) { Height = 230 }),
                Columns("*,16,*,16,*,16,*",
                    Bar(L("Coolant", "Pendingin"), nameof(Coolant), "{0:0}", "°C", 120), new Border(),
                    Bar(L("Oil pressure", "Tekanan oli"), nameof(OilPressure), "{0:0}", "kPa", 600), new Border(),
                    Bar(L("Fuel rate", "Konsumsi BBM"), nameof(FuelRate), "{0:0.0}", "l/h", 60), new Border(),
                    Bar(L("Battery", "Aki"), nameof(Battery), "{0:0.0}", "V", 32)),
                Columns("auto,12,*", lamp, new Border(), Stack(2, new TextBlock { Text = "DM1", FontFamily = mono, FontSize = 10, Foreground = Palette.Grey }, dtcs)),
                vin),
        };

        var throttle = new Slider { Minimum = 0, Maximum = 100 };
        throttle.Bind(RangeBase.ValueProperty, new Binding(nameof(Throttle)) { Mode = BindingMode.TwoWay });
        var reqVin = new Button { Content = L("Request VIN", "Minta VIN"), Classes = { "primary" } };
        reqVin.Click += async (_, _) => await RequestVinAsync();
        reqVin.Bind(InputElement.IsEnabledProperty, new Binding(nameof(IsRunning)));
        var leak = new Button { Content = L("Start an oil leak", "Mulai kebocoran oli"), Classes = { "ghost" } };
        leak.Click += (_, _) => Leak();
        var controls = Card(Stack(10,
            new TextBlock { Text = L("Accelerator pedal (simulated driver)", "Pedal gas (pengemudi simulasi)"), FontSize = 12 }, throttle,
            Row(8, reqVin, leak),
            new TextBlock { Text = L("The cluster is read-only: it listens and sends requests, never commands.", "Panel bersifat hanya-baca: ia mendengar dan mengirim permintaan, tidak pernah perintah."), FontSize = 11.5, Classes = { "muted" }, TextWrapping = TextWrapping.Wrap }),
            L("Driver and workshop", "Pengemudi dan bengkel"));
        var traffic = new ItemsControl { ItemTemplate = new FuncDataTemplate<string>((t, _) => new TextBlock { Text = t, FontFamily = mono, FontSize = 11 }) };
        traffic.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(Traffic)));
        var trafficCard = Card(traffic, L("Other PGNs (EEC1 and CCVS1 hidden)", "PGN lain (EEC1 dan CCVS1 disembunyikan)"));

        var status = new TextBlock { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };
        status.Bind(TextBlock.TextProperty, new Binding(nameof(Status)));
        return new ScrollViewer
        {
            DataContext = this,
            Content = Stack(16, Columns("1.6*,16,*", cluster, new Border(), Stack(16, controls, trafficCard)), status),
        };
    }
}

/// <summary>A round gauge: tachometer (0–3000 rpm, red above 2200) or speedometer (0–120 km/h).</summary>
internal sealed class RoundGauge : Control
{
    private static readonly IPen Scale = new Pen(new SolidColorBrush(Color.Parse("#8A9098")), 2);
    private static readonly IPen Red = new Pen(new SolidColorBrush(Color.Parse("#D23B2F")), 4);
    private static readonly IPen Needle = new Pen(new SolidColorBrush(Color.Parse("#F2A900")), 3.5, lineCap: PenLineCap.Round);
    private static readonly IBrush Text = new SolidColorBrush(Color.Parse("#E4E5E0"));
    private readonly J1939Demo _demo;
    private readonly bool _tacho;

    public RoundGauge(J1939Demo demo, bool tacho)
    {
        (_demo, _tacho) = (demo, tacho);
        demo.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(InvalidateVisual);
    }

    public override void Render(DrawingContext ctx)
    {
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2 + 8);
        var r = Math.Min(Bounds.Width, Bounds.Height) / 2 - 14;
        double max = _tacho ? 3000 : 120, value = _tacho ? _demo.Rpm : _demo.Speed;
        const double start = 225, sweep = 270;
        Point At(double v, double radius)
        {
            var a = (start - (v / max * sweep)) * Math.PI / 180;
            return new Point(c.X + (Math.Cos(a) * radius), c.Y - (Math.Sin(a) * radius));
        }

        var step = _tacho ? 250 : 10;
        for (var v = 0.0; v <= max + 0.1; v += step)
        {
            var major = v % (step * 2) == 0;
            ctx.DrawLine(_tacho && v >= 2200 ? Red : Scale, At(v, r), At(v, r - (major ? 14 : 7)));
            if (major)
            {
                var label = new FormattedText((_tacho ? v / 1000 : v).ToString(_tacho ? "0.#" : "0", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("JetBrains Mono"), 10.5, Text);
                var p = At(v, r - 28);
                ctx.DrawText(label, new Point(p.X - (label.Width / 2), p.Y - (label.Height / 2)));
            }
        }

        ctx.DrawLine(Needle, c, At(Math.Clamp(value, 0, max), r - 10));
        ctx.DrawEllipse(Text, null, c, 6, 6);
        var big = new FormattedText(value.ToString("0", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Barlow Condensed", FontStyle.Normal, FontWeight.Bold), 34, Text);
        ctx.DrawText(big, new Point(c.X - (big.Width / 2), c.Y + (r * 0.35)));
        var unit = new FormattedText(_tacho ? "rpm  · SPN 190" : "km/h · SPN 84", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("JetBrains Mono"), 10, Brushes.Gray);
        ctx.DrawText(unit, new Point(c.X - (unit.Width / 2), c.Y + (r * 0.35) + 40));
    }
}
