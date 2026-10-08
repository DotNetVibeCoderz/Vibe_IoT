using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using IoTCom.Net.Gallery.Infrastructure;
using static IoTCom.Net.Gallery.Infrastructure.Loc;
using static IoTCom.Net.Gallery.Infrastructure.UiKit;

namespace IoTCom.Net.Gallery.Demos;

public sealed partial class BleDemo
{
    internal static IBrush KindBrush(string kind) => kind switch
    {
        "Heart Rate" => Palette.Red,
        "Environmental Sensing" => Palette.Green,
        "iBeacon" => Palette.Violet,
        _ => Palette.Amber,
    };

    protected override Control CreateView()
    {
        var display = (FontFamily)Application.Current!.FindResource("DisplayFont")!;
        var mono = (FontFamily)Application.Current!.FindResource("MonoFont")!;

        var radar = new ProximityRadar(this) { Height = 300 };
        var list = new ItemsControl
        {
            ItemTemplate = new FuncDataTemplate<BleDeviceRow>((d, _) => Columns("14,*,auto",
                new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(5), Background = KindBrush(d.Kind), VerticalAlignment = VerticalAlignment.Center },
                Stack(0, new TextBlock { Text = d.Name, FontFamily = display, FontWeight = FontWeight.SemiBold, FontSize = 14 },
                    new TextBlock { Text = $"{d.Kind} · {d.Id}", FontFamily = mono, FontSize = 10.5, Classes = { "muted" } }),
                new TextBlock { Text = d.Line, FontFamily = mono, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center })),
        };
        list.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(Devices)));
        var radarCard = Card(Stack(10, radar, list), L("Advertisements · rings at −50, −65, −80 dBm", "Advertisement · cincin di −50, −65, −80 dBm"));

        // ---- heart-rate strap ---------------------------------------------------------------------------------
        var bpm = new TextBlock { FontFamily = display, FontWeight = FontWeight.Bold, FontSize = 64, Foreground = Palette.Red };
        bpm.Bind(TextBlock.TextProperty, new Binding(nameof(HeartRate)));
        var rr = new TextBlock { FontFamily = mono, FontSize = 12, Classes = { "muted" } };
        rr.Bind(TextBlock.TextProperty, new Binding(nameof(Rr)));
        var heartCard = Card(Stack(4,
            Row(8, bpm, new TextBlock { Text = "bpm", FontSize = 20, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 12), Classes = { "muted" } }),
            rr, Sparkline(HeartTrend, Palette.Red, 70)), L("HRM-Pro 2107 · notifications on 0x2A37", "HRM-Pro 2107 · notifikasi di 0x2A37"));

        // ---- greenhouse sensor --------------------------------------------------------------------------------
        Control Reading(string label, string path) => Stack(0,
            Eyebrow(label),
            new TextBlock { FontFamily = mono, FontSize = 20, FontWeight = FontWeight.Bold }.With(t => t.Bind(TextBlock.TextProperty, new Binding(path))));
        var envCard = Card(Columns("*,*,*", Reading(L("Temperature", "Suhu"), nameof(Temperature)), Reading(L("Humidity", "Kelembapan"), nameof(Humidity)), Reading(L("Pressure", "Tekanan"), nameof(Pressure))),
            L("Greenhouse ESS 3 · Environmental Sensing 0x181A", "Greenhouse ESS 3 · Environmental Sensing 0x181A"));

        // ---- smart plug, guarded ------------------------------------------------------------------------------
        var allow = new CheckBox { Content = L("Allow writes (a second central without read-only)", "Izinkan penulisan (central kedua tanpa read-only)") };
        allow.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(AllowWrite)));
        var toggle = new Button { Content = L("Switch the water pump plug", "Nyalakan/matikan plug pompa air"), Classes = { "primary" } };
        toggle.Click += async (_, _) => await TogglePlugAsync();
        toggle.Bind(InputElement.IsEnabledProperty, new Binding(nameof(IsRunning)));
        var watts = new TextBlock { FontFamily = mono, FontSize = 20, FontWeight = FontWeight.Bold };
        watts.Bind(TextBlock.TextProperty, new Binding(nameof(PlugWatts)) { StringFormat = "{0} W" });
        var last = new TextBlock { FontFamily = mono, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Classes = { "muted" } };
        last.Bind(TextBlock.TextProperty, new Binding(nameof(LastAction)));
        var plugCard = Card(Stack(10, Row(16, toggle, watts), allow, last), L("Plug Pompa Air · vendor service 6E400001…", "Plug Pompa Air · layanan vendor 6E400001…"));

        var status = new TextBlock { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };
        status.Bind(TextBlock.TextProperty, new Binding(nameof(Status)));

        return new ScrollViewer
        {
            DataContext = this,
            Content = Stack(16, Columns("1*,16,1.1*", radarCard, new Border(), Stack(16, heartCard, envCard, plugCard)), status),
        };
    }
}

/// <summary>Devices on concentric rings by RSSI; the angle comes from the device id so each keeps its place.</summary>
internal sealed class ProximityRadar : Control
{
    private static readonly IPen RingPen = new Pen(new SolidColorBrush(Color.Parse("#8A9098"), 0.45), 1, DashStyle.Dash);
    private static readonly IBrush Centre = new SolidColorBrush(Color.Parse("#2B3036"));
    private readonly BleDemo _demo;

    public ProximityRadar(BleDemo demo)
    {
        _demo = demo;
        demo.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(InvalidateVisual);
    }

    public override void Render(DrawingContext ctx)
    {
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var max = Math.Min(Bounds.Width, Bounds.Height) / 2 - 12;
        double Radius(int rssi) => Math.Clamp((-35 - rssi) / 55.0, 0.05, 1) * max;
        foreach (var ring in new[] { -50, -65, -80 })
        {
            var r = Radius(ring);
            ctx.DrawEllipse(null, RingPen, c, r, r);
            ctx.DrawText(new FormattedText($"{ring}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("JetBrains Mono"), 9.5, Palette.Grey), new Point(c.X + 3, c.Y - r - 12));
        }

        ctx.DrawEllipse(Centre, null, c, 6, 6);
        foreach (var d in _demo.Devices.ToList())
        {
            var angle = (uint)d.Id.GetHashCode(StringComparison.Ordinal) % 360 * Math.PI / 180;
            var r = Radius(d.Rssi);
            var p = new Point(c.X + (Math.Cos(angle) * r), c.Y + (Math.Sin(angle) * r));
            ctx.DrawEllipse(BleDemo.KindBrush(d.Kind), null, p, 7, 7);
            ctx.DrawText(new FormattedText(d.Name, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Barlow"), 11.5, Palette.Ink), new Point(p.X + 10, p.Y - 8));
        }
    }
}
