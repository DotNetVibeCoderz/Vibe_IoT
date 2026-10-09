using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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

public sealed partial class ClockSyncDemo
{
    protected override Control CreateView()
    {
        var mono = (FontFamily)Application.Current!.FindResource("MonoFont")!;
        var traces = Card(new ErrorTraces(this) { Height = 330 }, L("Clock error per device, last 30 s (GPS time is the centre line)", "Galat jam per perangkat, 30 dtk terakhir (waktu GPS adalah garis tengah)"));

        var tiles = new ItemsControl
        {
            ItemsPanel = new FuncTemplate<Panel?>(() => new UniformGrid { Columns = 3 }),
            ItemTemplate = new FuncDataTemplate<SyncedDevice>((d, _) =>
            {
                if (d is null) return new Border();
                var reading = new TextBlock { FontFamily = mono, FontSize = 18, FontWeight = FontWeight.Bold };
                reading.Bind(TextBlock.TextProperty, new Binding(nameof(SyncedDevice.Reading)));
                var error = new TextBlock { FontFamily = mono, FontSize = 12 };
                error.Bind(TextBlock.TextProperty, new Binding(nameof(SyncedDevice.ErrorMs)) { StringFormat = "{0:+0;-0} ms" });
                var last = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap };
                last.Bind(TextBlock.TextProperty, new Binding(nameof(SyncedDevice.LastSync)));
                last.Bind(TextBlock.ForegroundProperty, new Binding(nameof(SyncedDevice.Failed)) { Converter = new FuncValueConverter<bool, IBrush>(f => f ? Palette.Red : Palette.Grey) });
                return new Border
                {
                    Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(12, 10), CornerRadius = new CornerRadius(8),
                    BorderThickness = new Thickness(4, 0, 0, 0), BorderBrush = new SolidColorBrush(Color.Parse(d.Colour)), Classes = { "card" },
                    Child = Stack(3, new TextBlock { Text = d.Name.ToUpperInvariant(), FontSize = 10.5, LetterSpacing = 1, Foreground = Palette.Grey }, reading, error, last),
                };
            }),
        };
        tiles.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(Devices)));

        Slider Knob(string path, double min, double max, double step)
        {
            var s = new Slider { Minimum = min, Maximum = max, TickFrequency = step, IsSnapToTickEnabled = true };
            s.Bind(RangeBase.ValueProperty, new Binding(path) { Mode = BindingMode.TwoWay });
            return s;
        }

        TextBlock Value(string path, string format)
        {
            var t = new TextBlock { FontFamily = mono, FontSize = 12 };
            t.Bind(TextBlock.TextProperty, new Binding(path) { StringFormat = format });
            return t;
        }

        var syncNow = new Button { Content = L("Sync all now", "Sinkronkan semua sekarang"), Classes = { "primary" } };
        syncNow.Click += async (_, _) => await SyncAllAsync();
        syncNow.Bind(InputElement.IsEnabledProperty, new Binding(nameof(IsRunning)));
        var gps = new ToggleSwitch { OnContent = L("GPS lost: server unsynchronised", "GPS hilang: server tidak tersinkron"), OffContent = L("GPS locked", "GPS terkunci") };
        gps.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(GpsLost)) { Mode = BindingMode.TwoWay });
        var controls = Card(Stack(10,
            Columns("*,auto", new TextBlock { Text = L("Sync interval", "Interval sinkronisasi"), FontSize = 12 }, Value(nameof(SyncInterval), "{0:0} s")), Knob(nameof(SyncInterval), 2, 20, 1),
            Columns("*,auto", new TextBlock { Text = L("Network round trip", "Pulang-pergi jaringan"), FontSize = 12 }, Value(nameof(LatencyMs), "{0:0} ms")), Knob(nameof(LatencyMs), 0, 400, 10),
            gps, syncNow), L("Network and server", "Jaringan dan server"));

        var exchange = new TextBlock { FontFamily = mono, FontSize = 11, TextWrapping = TextWrapping.Wrap };
        exchange.Bind(TextBlock.TextProperty, new Binding(nameof(LastExchange)));
        var math = Card(Stack(8, exchange,
            new TextBlock { Text = "θ = ((T2 − T1) + (T3 − T4)) / 2\nδ = (T4 − T1) − (T3 − T2)", FontFamily = mono, FontSize = 12, Foreground = Palette.Blue }),
            L("Last exchange", "Pertukaran terakhir"));

        var status = new TextBlock { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };
        status.Bind(TextBlock.TextProperty, new Binding(nameof(Status)));
        return new ScrollViewer
        {
            DataContext = this,
            Content = Stack(16, Columns("1.7*,16,*", traces, new Border(), Stack(16, controls, math)), tiles, status),
        };
    }
}

/// <summary>Time-error traces: one line per device on a ±1 s scale, newest at the right.</summary>
internal sealed class ErrorTraces : Control
{
    private static readonly IBrush Ink = new SolidColorBrush(Color.Parse("#2B3036"));
    private static readonly IPen Grid = new Pen(new SolidColorBrush(Color.Parse("#C9CBC4")), 1);
    private static readonly IPen Zero = new Pen(new SolidColorBrush(Color.Parse("#2B3036")), 1.5);
    private readonly ClockSyncDemo _demo;

    public ErrorTraces(ClockSyncDemo demo)
    {
        _demo = demo;
        demo.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(InvalidateVisual);
    }

    private static FormattedText Label(string text, IBrush brush) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("JetBrains Mono"), 10.5, brush);

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height, left = 54, right = w - 8, mid = h / 2;
        const double range = 1000;   // ms
        double Y(double ms) => mid - (Math.Clamp(ms, -range, range) / range * (h / 2 - 14));
        foreach (var ms in new[] { -1000, -500, 500, 1000 })
        {
            ctx.DrawLine(Grid, new Point(left, Y(ms)), new Point(right, Y(ms)));
            var label = Label($"{ms:+0;-0} ms", Brushes.Gray);
            ctx.DrawText(label, new Point(left - 8 - label.Width, Y(ms) - (label.Height / 2)));
        }

        ctx.DrawLine(Zero, new Point(left, mid), new Point(right, mid));
        var gps = Label("GPS", Ink);
        ctx.DrawText(gps, new Point(left - 8 - gps.Width, mid - (gps.Height / 2)));
        var now = _demo.Elapsed;
        double X(double t) => right - ((now - t) / 30 * (right - left));
        foreach (var d in _demo.Devices)
        {
            if (d.Trace.Count < 2) continue;
            var pen = new Pen(new SolidColorBrush(Color.Parse(d.Colour)), 2, lineCap: PenLineCap.Round);
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(X(d.Trace[0].T), Y(d.Trace[0].ErrorMs)), false);
                for (var i = 1; i < d.Trace.Count; i++) c.LineTo(new Point(X(d.Trace[i].T), Y(d.Trace[i].ErrorMs)));
                c.EndFigure(false);
            }

            ctx.DrawGeometry(null, pen, g);
        }
    }
}
