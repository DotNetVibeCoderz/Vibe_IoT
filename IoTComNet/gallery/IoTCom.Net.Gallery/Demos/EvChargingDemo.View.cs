using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using IoTCom.Net.Gallery.Infrastructure;
using static IoTCom.Net.Gallery.Infrastructure.Loc;
using static IoTCom.Net.Gallery.Infrastructure.UiKit;

namespace IoTCom.Net.Gallery.Demos;

public sealed partial class EvChargingDemo
{
    protected override Control CreateView()
    {
        var mono = (FontFamily)Application.Current!.FindResource("MonoFont")!;
        var forecourt = new Border { CornerRadius = new CornerRadius(14), ClipToBounds = true, Child = new Forecourt(this) { Height = 300 } };
        var loadBar = new SiteLoadBar(this) { Height = 46 };

        Button Btn(string text, string cls, Func<Task> action)
        {
            var b = new Button { Content = text, Classes = { cls } };
            b.Click += async (_, _) => await action();
            b.Bind(InputElement.IsEnabledProperty, new Binding(nameof(IsRunning)));
            return b;
        }

        var selected = new TextBlock { FontFamily = mono, FontSize = 13, TextWrapping = TextWrapping.Wrap };
        selected.Bind(TextBlock.TextProperty, new MultiBinding
        {
            Bindings = { new Binding("Selected.Label"), new Binding("Selected.Version"), new Binding("Selected.Status"), new Binding("Selected.TransactionId") },
            Converter = new Avalonia.Data.Converters.FuncMultiValueConverter<object?, string>(v =>
            {
                var a = v.ToArray();
                return a[0] is null ? "" : $"{a[0]}  ·  {a[1]}  ·  {a[2]}{(a[3] is string tx ? "  ·  " + tx : "")}";
            }),
        });

        var driverCard = Card(Stack(10,
            selected,
            new TextBlock { Text = L("Click a bay to select it.", "Klik bay untuk memilihnya."), FontSize = 11, Classes = { "muted" } },
            Row(8, Btn(L("Plug in a car", "Colokkan mobil"), "primary", PlugInAsync), Btn(L("Unplug", "Cabut"), "ghost", UnplugAsync)),
            Row(8, Btn(L("Tap card 04A1B2C3", "Tempel kartu 04A1B2C3"), "ghost", () => TapCardAsync("04A1B2C3")), Btn(L("Tap unknown card", "Tempel kartu asing"), "ghost", () => TapCardAsync("DEADBEEF"))),
            Row(8, Btn(L("Ground fault", "Gangguan tanah"), "ghost", FaultAsync), Btn(L("Drop cellular link", "Putus link seluler"), "ghost", DropLinkAsync))),
            L("Driver & field", "Pengemudi & lapangan"));

        var unlock = new ToggleSwitch { OnContent = L("Commands allowed", "Perintah diizinkan"), OffContent = L("Write lock closed (read-only)", "Kunci tulis tertutup (hanya-baca)") };
        unlock.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(CommandsAllowed)) { Mode = BindingMode.TwoWay });
        var limit = new Slider { Minimum = 1.4, Maximum = 60, TickFrequency = 0.2, IsSnapToTickEnabled = true };
        limit.Bind(RangeBase.ValueProperty, new Binding(nameof(LimitTarget)) { Mode = BindingMode.TwoWay });
        var limitText = new TextBlock { FontFamily = mono, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        limitText.Bind(TextBlock.TextProperty, new Binding(nameof(LimitTarget)) { StringFormat = "{0:0.0} kW" });
        var lm = new ToggleSwitch { OnContent = L("Load management: site ≤ 100 kW", "Manajemen beban: situs ≤ 100 kW"), OffContent = L("Load management off", "Manajemen beban mati") };
        lm.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(LoadManagement)) { Mode = BindingMode.TwoWay });

        var operatorCard = Card(Stack(10,
            unlock,
            Row(8, Btn(L("Remote start", "Mulai jarak jauh"), "primary", RemoteStartAsync), Btn(L("Remote stop", "Hentikan jarak jauh"), "ghost", RemoteStopAsync)),
            Columns("auto,8,*,8,auto", new TextBlock { Text = L("Limit", "Batas"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center }, new Border(), limit, new Border(), limitText),
            Row(8, Btn(L("Apply limit", "Terapkan batas"), "ghost", LimitAsync), Btn(L("Clear limit", "Hapus batas"), "ghost", ClearLimitAsync)),
            Row(8, Btn(L("Toggle availability", "Ubah ketersediaan"), "ghost", AvailabilityAsync), Btn(L("Reset charger", "Reset pengisi"), "ghost", ResetAsync)),
            lm), L("CSMS operator", "Operator CSMS"));

        var site = new TextBlock { FontFamily = mono, FontSize = 12 };
        site.Bind(TextBlock.TextProperty, new MultiBinding
        {
            Bindings = { new Binding(nameof(SiteKw)), new Binding(nameof(SessionKWh)) },
            Converter = new Avalonia.Data.Converters.FuncMultiValueConverter<object?, string>(v =>
            {
                var a = v.Select(x => x is double d ? d : 0).ToArray();
                return string.Create(CultureInfo.InvariantCulture, $"{a[0]:0.0} kW of {GridKw:0} kW grid connection  ·  {a[1]:0.00} kWh in running sessions");
            }),
        });
        var loadCard = Card(Stack(8, loadBar, site, Sparkline(SiteTrend, Palette.Blue, 60)), L("Site load", "Beban situs"));

        var log = new ItemsControl { ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((t, _) => new TextBlock { Text = t, FontFamily = mono, FontSize = 10.5, TextWrapping = TextWrapping.Wrap }) };
        log.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(Log)));
        var logCard = Card(new ScrollViewer { MaxHeight = 200, Content = log }, L("CSMS events", "Kejadian CSMS"));

        var status = new TextBlock { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };
        status.Bind(TextBlock.TextProperty, new Binding(nameof(Status)));
        return new ScrollViewer
        {
            DataContext = this,
            Content = Stack(16, Columns("1.6*,16,*", Stack(16, forecourt, loadCard, logCard), new Border(), Stack(16, driverCard, operatorCard)), status),
        };
    }
}

internal static class BayLook
{
    public static readonly IBrush Asphalt = new SolidColorBrush(Color.Parse("#2B3036"));
    public static readonly IBrush Line = new SolidColorBrush(Color.Parse("#E4E5E0"), 0.55);
    public static readonly IBrush Panel = new SolidColorBrush(Color.Parse("#E4E5E0"));
    public static readonly IBrush Ink = new SolidColorBrush(Color.Parse("#2B3036"));
    public static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#8A9098"));
    public static readonly IBrush Body = new SolidColorBrush(Color.Parse("#5B626B"));
    public static readonly IBrush Glass = new SolidColorBrush(Color.Parse("#3A4048"));
    public static readonly IBrush Selection = new SolidColorBrush(Color.Parse("#2F6FD6"));

    /// <summary>IEC lamp colour of a status: green free, blue charging, amber waiting, red fault, grey out of service.</summary>
    public static IBrush Lamp(ChargeBay b) => b.Faulted || b.Status == "Faulted" ? Palette.Red
        : b.Status is "Offline" or "Unavailable" ? Palette.Grey
        : b.Status == "Available" ? Palette.Green
        : b.Status == "Charging" ? Palette.Blue
        : Palette.Amber;

    public static FormattedText Text(string s, double size, IBrush brush, string face = "JetBrains Mono", FontWeight weight = FontWeight.Normal) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(face, FontStyle.Normal, weight), size, brush);
}

/// <summary>Top-down forecourt: a charger pillar per bay, the car with its battery fill, a cable that glows while power flows.</summary>
internal sealed class Forecourt : Control
{
    private readonly EvChargingDemo _demo;

    public Forecourt(EvChargingDemo demo)
    {
        _demo = demo;
        demo.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(InvalidateVisual);
        demo.PropertyChanged += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(InvalidateVisual);
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_demo.Bays.Count == 0) return;
        var i = (int)(e.GetPosition(this).X / (Bounds.Width / _demo.Bays.Count));
        _demo.Selected = _demo.Bays[Math.Clamp(i, 0, _demo.Bays.Count - 1)];
    }

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        ctx.DrawRectangle(BayLook.Asphalt, null, new Rect(0, 0, w, h));
        var bays = _demo.Bays.ToList();
        if (bays.Count == 0)
        {
            ctx.DrawText(BayLook.Text("Start the demo to open the hub.", 13, BayLook.Muted, "Barlow"), new Point(18, h / 2 - 8));
            return;
        }

        var bw = w / bays.Count;
        var canopyH = 58.0;
        ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#1E2227")), null, new Rect(0, 0, w, canopyH));
        ctx.DrawText(BayLook.Text("SPKLU KM 57 · REST AREA", 11, BayLook.Muted, "Barlow Condensed", FontWeight.SemiBold), new Point(12, h - 20));
        var linePen = new Pen(BayLook.Line, 2, new DashStyle([6, 4], 0));

        foreach (var b in bays)
        {
            var x0 = b.Index * bw;
            var cx = x0 + bw / 2;
            if (b.Index > 0) ctx.DrawLine(linePen, new Point(x0, canopyH + 8), new Point(x0, h - 34));

            // Charger pillar under the canopy, one per two bays for AC, one per bay label.
            var pillar = new Rect(cx - 22, 10, 44, 40);
            ctx.DrawRectangle(BayLook.Panel, null, pillar, 6, 6);
            ctx.DrawEllipse(BayLook.Lamp(b), new Pen(BayLook.Ink, 1), new Point(cx, 22), 6, 6);
            ctx.DrawText(BayLook.Text(b.Label, 9.5, BayLook.Ink), new Point(cx - 19, 33));

            // The car with its battery as the roof.
            var car = new Rect(cx - Math.Min(42, bw * 0.34), canopyH + 40, Math.Min(84, bw * 0.68), 116);
            if (b.Car is not null)
            {
                var plugY = car.Top + 18;
                var flowing = b.PowerKw > 0.05;
                var cable = new Pen(flowing ? Palette.Amber : BayLook.Muted, flowing ? 4 : 2.5) { LineCap = PenLineCap.Round };
                ctx.DrawLine(cable, new Point(cx, pillar.Bottom), new Point(cx, plugY));
                ctx.DrawRectangle(BayLook.Body, null, car, 18, 18);
                var battery = car.Deflate(new Thickness(12, 26, 12, 30));
                ctx.DrawRectangle(BayLook.Glass, null, battery, 6, 6);
                var fill = battery.Height * Math.Clamp(b.SoC / 100, 0, 1);
                ctx.DrawRectangle(b.SoC >= 80 ? Palette.Amber : Palette.Green, null, new Rect(battery.X, battery.Bottom - fill, battery.Width, fill), 6, 6);
                var soc = BayLook.Text($"{b.SoC:0}%", 13, Brushes.White, "Barlow Condensed", FontWeight.SemiBold);
                ctx.DrawText(soc, new Point(cx - soc.Width / 2, battery.Center.Y - soc.Height / 2));
                var name = BayLook.Text(b.Car, 9, BayLook.Panel, "Barlow");
                ctx.DrawText(name, new Point(cx - Math.Min(name.Width, car.Width) / 2, car.Bottom - 22));
            }
            else
            {
                var free = BayLook.Text(b.Status == "Available" ? "FREE" : b.Status.ToUpperInvariant(), 11, BayLook.Muted, "Barlow Condensed", FontWeight.SemiBold);
                ctx.DrawText(free, new Point(cx - free.Width / 2, car.Center.Y - 8));
            }

            // Readout under the bay.
            var line1 = BayLook.Text(b.TransactionId is null ? (b.Car is null ? "" : b.Status) : $"{b.PowerKw:0.0} kW", 12, Brushes.White, "JetBrains Mono", FontWeight.SemiBold);
            ctx.DrawText(line1, new Point(cx - line1.Width / 2, car.Bottom + 6));
            var line2 = BayLook.Text(b.TransactionId is null ? "" : $"{b.EnergyKWh:0.00} kWh" + (b.LimitKw is { } l ? $" ≤{l:0.#}" : ""), 10, BayLook.Muted);
            ctx.DrawText(line2, new Point(cx - line2.Width / 2, car.Bottom + 22));

            if (ReferenceEquals(b, _demo.Selected))
                ctx.DrawRectangle(null, new Pen(BayLook.Selection, 2.5), new Rect(x0 + 4, canopyH + 6, bw - 8, h - canopyH - 38), 10, 10);
        }
    }
}

/// <summary>The site load: each bay's power stacked against the grid connection, with the load-management limit.</summary>
internal sealed class SiteLoadBar : Control
{
    private readonly EvChargingDemo _demo;

    public SiteLoadBar(EvChargingDemo demo)
    {
        _demo = demo;
        demo.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(InvalidateVisual);
        demo.PropertyChanged += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(InvalidateVisual);
    }

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, barH = 22;
        var scale = w / EvChargingDemo.GridKw;
        ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#8A9098"), 0.25), null, new Rect(0, 0, w, barH), 4, 4);
        var x = 0.0;
        IBrush[] shades = [Palette.Blue, new SolidColorBrush(Color.Parse("#4F8CE6")), new SolidColorBrush(Color.Parse("#7BA7EC"))];
        foreach (var b in _demo.Bays.Where(b => b.PowerKw > 0.05))
        {
            var seg = b.PowerKw * scale;
            ctx.DrawRectangle(shades[b.Index % shades.Length], new Pen(Brushes.White, 1), new Rect(x, 0, seg, barH));
            if (seg > 26) ctx.DrawText(BayLook.Text($"{b.PowerKw:0}", 9, Brushes.White), new Point(x + 4, 5));
            x += seg;
        }

        if (_demo.LoadManagement)
        {
            var lx = _demo.SiteLimitKw * scale;
            ctx.DrawLine(new Pen(Palette.Red, 2.5), new Point(lx, -2), new Point(lx, barH + 4));
            ctx.DrawText(BayLook.Text($"limit {_demo.SiteLimitKw:0} kW", 9.5, Palette.Red), new Point(Math.Min(lx + 5, w - 70), 5));
        }

        for (var kw = 0; kw <= EvChargingDemo.GridKw; kw += 50)
            ctx.DrawText(BayLook.Text($"{kw}", 9, BayLook.Muted), new Point(Math.Min(kw * scale, w - 18), barH + 6));
    }
}
