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

public sealed partial class BacnetAhuDemo
{
    protected override Control CreateView()
    {
        var mono = (FontFamily)Application.Current!.FindResource("MonoFont")!;
        var schematic = new Border { CornerRadius = new CornerRadius(14), ClipToBounds = true, Child = new AhuSchematic(this) { Height = 280 } };

        Button Btn(string text, string cls, Func<Task> action)
        {
            var b = new Button { Content = text, Classes = { cls } };
            b.Click += async (_, _) => await action();
            b.Bind(InputElement.IsEnabledProperty, new Binding(nameof(IsRunning)));
            return b;
        }

        Control SliderRow(string label, string path, double min, double max, double tick, string format)
        {
            var slider = new Slider { Minimum = min, Maximum = max, TickFrequency = tick, IsSnapToTickEnabled = true };
            slider.Bind(RangeBase.ValueProperty, new Binding(path) { Mode = BindingMode.TwoWay });
            var text = new TextBlock { FontFamily = mono, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, MinWidth = 56 };
            text.Bind(TextBlock.TextProperty, new Binding(path) { StringFormat = format });
            return Columns("78,*,8,auto", new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center }, slider, new Border(), text);
        }

        var unlock = new ToggleSwitch { OnContent = L("Commands allowed", "Perintah diizinkan"), OffContent = L("Write lock closed (read-only)", "Kunci tulis tertutup (hanya-baca)") };
        unlock.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(WritesAllowed)) { Mode = BindingMode.TwoWay });
        var operatorCard = Card(Stack(10,
            unlock,
            new TextBlock { Text = L("Every command is written at priority 8 (manual operator).", "Setiap perintah ditulis pada prioritas 8 (operator manual)."), FontSize = 11, Classes = { "muted" }, TextWrapping = TextWrapping.Wrap },
            Row(8, Btn(L("Start fan", "Nyalakan kipas"), "primary", () => FanAsync(true)), Btn(L("Stop fan", "Matikan kipas"), "ghost", () => FanAsync(false)), Btn(L("Relinquish", "Relinquish"), "ghost", RelinquishFanAsync)),
            SliderRow(L("Set point", "Set point"), nameof(SetpointTarget), 10, 18, 0.5, "{0:0.0} °C"),
            Btn(L("Write set point", "Tulis set point"), "ghost", SetpointAsync),
            SliderRow(L("CHW valve", "Katup CHW"), nameof(ValveTarget), 0, 100, 5, "{0:0} %"),
            Row(8, Btn(L("Override valve", "Ambil alih katup"), "ghost", ValveOverrideAsync), Btn(L("Valve auto", "Katup otomatis"), "ghost", ValveAutoAsync)),
            Row(8, Btn(L("Occupied", "Terisi"), "ghost", () => OccupancyAsync(1)), Btn(L("Unoccupied", "Kosong"), "ghost", () => OccupancyAsync(2)))),
            L("BMS operator", "Operator BMS"));

        TextBlock Head(string t) => new() { Text = t, FontSize = 10, Classes = { "muted" }, FontFamily = mono };
        var header = Columns("52,8,*,8,110,8,52", Head("OBJECT"), new Border(), Head("NAME"), new Border(), Head("PRESENT VALUE"), new Border(), Head("CMD"));
        var rows = new ItemsControl
        {
            ItemTemplate = new FuncDataTemplate<BmsPoint>((p, _) =>
            {
                if (p is null) return new Border();
                var value = new TextBlock { FontFamily = mono, FontSize = 11 };
                value.Bind(TextBlock.TextProperty, new Binding(nameof(BmsPoint.Value)));
                var prio = new TextBlock { FontFamily = mono, FontSize = 11 };
                prio.Bind(TextBlock.TextProperty, new Binding(nameof(BmsPoint.Priority)));
                prio.Bind(TextBlock.ForegroundProperty, new Binding(nameof(BmsPoint.Overridden)) { Converter = new Avalonia.Data.Converters.FuncValueConverter<bool, IBrush>(o => o ? Palette.Amber : Palette.Grey) });
                return Columns("52,8,*,8,110,8,52",
                    new TextBlock { Text = p.Short, FontFamily = mono, FontSize = 11, Foreground = Palette.Blue }, new Border(),
                    new TextBlock { Text = p.Name, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis }, new Border(), value, new Border(), prio);
            }),
        };
        rows.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(Points)));
        var cov = new TextBlock { FontSize = 11, Classes = { "muted" } };
        cov.Bind(TextBlock.TextProperty, new Binding(nameof(CovCount)) { StringFormat = L("{0} COV notifications received", "{0} notifikasi COV diterima") });
        var pointsCard = Card(Stack(6, header, rows, cov), L("BMS points (device 2604)", "Titik BMS (device 2604)"));

        var log = new ItemsControl { ItemTemplate = new FuncDataTemplate<string>((t, _) => new TextBlock { Text = t, FontFamily = mono, FontSize = 10.5, TextWrapping = TextWrapping.Wrap }) };
        log.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(Log)));
        var logCard = Card(new ScrollViewer { MaxHeight = 170, Content = log }, L("BMS log", "Log BMS"));

        var status = new TextBlock { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };
        status.Bind(TextBlock.TextProperty, new Binding(nameof(Status)));
        return new ScrollViewer
        {
            DataContext = this,
            Content = Stack(16, Columns("1.6*,16,*", Stack(16, schematic, pointsCard), new Border(), Stack(16, operatorCard, logCard)), status),
        };
    }
}

/// <summary>The unit as a duct schematic: outdoor-air damper, return air, filter, chilled-water coil, supply fan, sensors.</summary>
internal sealed class AhuSchematic : Control
{
    private static readonly IBrush Bg = new SolidColorBrush(Color.Parse("#2B3036"));
    private static readonly IBrush Duct = new SolidColorBrush(Color.Parse("#3A4048"));
    private static readonly IBrush Edge = new SolidColorBrush(Color.Parse("#8A9098"));
    private static readonly IBrush Panel = new SolidColorBrush(Color.Parse("#E4E5E0"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#8A9098"));
    private readonly BacnetAhuDemo _demo;
    private double _angle;

    public AhuSchematic(BacnetAhuDemo demo)
    {
        _demo = demo;
        demo.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(InvalidateVisual);
        demo.PropertyChanged += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(InvalidateVisual);
    }

    private static FormattedText T(string s, double size, IBrush brush, string face = "JetBrains Mono", FontWeight weight = FontWeight.Normal) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(face, FontStyle.Normal, weight), size, brush);

    private static void Tag(DrawingContext ctx, Point at, string label, string value, bool alarm = false)
    {
        var v = T(value, 12, alarm ? Palette.Red : Brushes.White, "JetBrains Mono", FontWeight.SemiBold);
        var l = T(label, 9, Muted, "Barlow Condensed", FontWeight.SemiBold);
        var w = Math.Max(v.Width, l.Width) + 14;
        ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#1E2227")), new Pen(alarm ? Palette.Red : Edge, 1), new Rect(at.X - (w / 2), at.Y, w, 34), 5, 5);
        ctx.DrawText(l, new Point(at.X - (l.Width / 2), at.Y + 3));
        ctx.DrawText(v, new Point(at.X - (v.Width / 2), at.Y + 15));
    }

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        ctx.DrawRectangle(Bg, null, new Rect(0, 0, w, h));
        if (_demo.Ahu is not { } a)
        {
            ctx.DrawText(T("Start the demo to bring device 2604 online.", 13, Muted, "Barlow"), new Point(18, h / 2 - 8));
            return;
        }

        var y = h * 0.52;
        var dh = 56.0;
        var duct = new Rect(w * 0.08, y - (dh / 2), w * 0.84, dh);
        ctx.DrawRectangle(Duct, new Pen(Edge, 2), duct);
        // Return air comes down from above into the mixing box.
        var ret = new Rect(w * 0.26, 34, dh * 0.8, y - (dh / 2) - 34);
        ctx.DrawRectangle(Duct, new Pen(Edge, 2), ret);
        ctx.DrawRectangle(Duct, null, new Rect(ret.X + 2, ret.Bottom - 3, ret.Width - 4, 6));

        // Outdoor-air damper: blades close to horizontal as it closes.
        var dx = w * 0.13;
        var open = a.Damper.Value / 100;
        for (var i = 0; i < 4; i++)
        {
            var cy = duct.Top + 8 + (i * 13);
            var ang = (1 - open) * Math.PI / 2.2;
            var len = 11.0;
            ctx.DrawLine(new Pen(Panel, 3) { LineCap = PenLineCap.Round }, new Point(dx - (Math.Cos(ang) * 0), cy - (Math.Sin(Math.PI / 2 - ang) * len / 2)), new Point(dx + (Math.Sin(ang) * len / 2), cy + (Math.Cos(ang) * len / 2)));
        }

        // Filter.
        var fx = w * 0.40;
        ctx.DrawRectangle(a.FilterAlarm.Value == 1 ? Palette.Red : new SolidColorBrush(Color.Parse("#7C848C")), null, new Rect(fx, duct.Top + 4, 8, dh - 8));
        for (var i = 0; i < 6; i++) ctx.DrawLine(new Pen(Bg, 1), new Point(fx, duct.Top + 8 + (i * 8)), new Point(fx + 8, duct.Top + 12 + (i * 8)));

        // Chilled-water coil: blue fills with the valve.
        var cx = w * 0.50;
        var coil = new Rect(cx, duct.Top + 4, 26, dh - 8);
        ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb((byte)(40 + (a.CoolingValve.Value * 2)), 0x2F, 0x6F, 0xD6)), new Pen(Palette.Blue, 1.5), coil);
        for (var i = 1; i < 5; i++) ctx.DrawLine(new Pen(Palette.Blue, 1), new Point(coil.X + (i * coil.Width / 5), coil.Top), new Point(coil.X + (i * coil.Width / 5), coil.Bottom));

        // Supply fan: a wheel that turns while running.
        var fan = new Point(w * 0.66, y);
        var running = a.FanStatus.Value == 1;
        if (running) _angle += 0.6;
        ctx.DrawEllipse(Duct, new Pen(running ? Palette.Green : Edge, 2.5), fan, 22, 22);
        for (var i = 0; i < 5; i++)
        {
            var t = _angle + (i * Math.PI * 2 / 5);
            ctx.DrawLine(new Pen(running ? Palette.Green : Edge, 2), fan, new Point(fan.X + (Math.Cos(t) * 18), fan.Y + (Math.Sin(t) * 18)));
        }

        // Flow arrow on the supply side.
        var ax = w * 0.86;
        if (running)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(ax - 10, y - 9), true);
                c.LineTo(new Point(ax + 6, y));
                c.LineTo(new Point(ax - 10, y + 9));
                c.EndFigure(true);
            }

            ctx.DrawGeometry(Palette.Amber, null, g);
        }

        // Sensor and actuator tags.
        Tag(ctx, new Point(w * 0.10, duct.Bottom + 12), "AI3 OUTDOOR", $"{a.OutdoorTemp.Value:0.0}°C");
        Tag(ctx, new Point(w * 0.15, duct.Top - 44), "AO2 OA DAMPER", $"{a.Damper.Value:0}%");
        Tag(ctx, new Point(ret.Center.X + 62, 40), "AI2 RETURN", $"{a.ReturnTemp.Value:0.0}°C");
        Tag(ctx, new Point(ret.Center.X + 62, 82), "AI4 CO2", $"{a.Co2.Value:0} ppm", a.Co2.Value > a.Co2Setpoint.Value + 200);
        Tag(ctx, new Point(fx + 4, duct.Bottom + 12), "BI2 FILTER", a.FilterAlarm.FormatValue(), a.FilterAlarm.Value == 1);
        Tag(ctx, new Point(cx + 13, duct.Top - 44), "AO1 CHW VALVE", $"{a.CoolingValve.Value:0}%" + (a.CoolingValve.ActivePriority is { } p ? $" @{p}" : ""), a.CoolingValve.ActivePriority is not null);
        Tag(ctx, new Point(fan.X, duct.Bottom + 12), "BI1 FAN", a.FanStatus.FormatValue() + (a.FanCommand.ActivePriority is { } fp ? $" @{fp}" : ""), a.FanCommand.ActivePriority is not null);
        Tag(ctx, new Point(w * 0.82, duct.Top - 44), "AI1 SUPPLY", $"{a.SupplyTemp.Value:0.0}°C");
        Tag(ctx, new Point(w * 0.82, duct.Bottom + 12), "AI5 STATIC", $"{a.StaticPressure.Value:0} Pa");
        ctx.DrawText(T($"AV1 SET {a.SupplySetpoint.Value:0.0}°C  ·  MSV1 {a.Occupancy.FormatValue().ToUpperInvariant()}  ·  DEVICE 2604 AHU-1 LANTAI 3", 10, Muted, "Barlow Condensed", FontWeight.SemiBold), new Point(14, h - 20));
    }
}
