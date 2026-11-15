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
using IoTCom.Net.Protocols.Knx;
using static IoTCom.Net.Gallery.Infrastructure.Loc;
using static IoTCom.Net.Gallery.Infrastructure.UiKit;

namespace IoTCom.Net.Gallery.Demos;

public sealed partial class KnxOfficeDemo
{
    protected override Control CreateView()
    {
        var mono = (FontFamily)Application.Current!.FindResource("MonoFont")!;
        var plan = new Border { CornerRadius = new CornerRadius(14), ClipToBounds = true, Child = new FloorPlan(this) { Height = 300 } };

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
            return Columns("70,*,8,auto", new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center }, slider, new Border(), text);
        }

        var unlock = new ToggleSwitch { OnContent = L("Writes allowed", "Penulisan diizinkan"), OffContent = L("Write lock closed (read-only)", "Kunci tulis tertutup (hanya-baca)") };
        unlock.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(WritesAllowed)) { Mode = BindingMode.TwoWay });
        var room = new TextBlock { FontFamily = mono, FontSize = 12 };
        room.Bind(TextBlock.TextProperty, new Binding(nameof(SelectedRoom)) { Converter = new Avalonia.Data.Converters.FuncValueConverter<int, string>(n =>
            Floor?.Rooms.FirstOrDefault(r => r.Number == n) is { } r ? $"{r.Name} · 1/x/{n} · 2/x/{n} · 3/x/{n}" : $"room {n}") });
        var tunnel = new TextBlock { FontSize = 11, Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };
        tunnel.Bind(TextBlock.TextProperty, new Binding(nameof(TunnelInfo)));

        var operatorCard = Card(Stack(10,
            room, unlock,
            Row(8, Btn(L("Light on", "Lampu nyala"), "primary", () => LightAsync(true)), Btn(L("Light off", "Lampu mati"), "ghost", () => LightAsync(false))),
            SliderRow(L("Dim", "Redup"), nameof(DimTarget), 0, 100, 5, "{0:0} %"),
            Btn(L("Send dim value", "Kirim nilai redup"), "ghost", DimAsync),
            SliderRow(L("Blind", "Tirai"), nameof(BlindTarget), 0, 100, 5, "{0:0} %"),
            Row(8, Btn(L("Move blind", "Gerakkan tirai"), "ghost", BlindAsync), Btn(L("Up", "Naik"), "ghost", () => BlindUpDownAsync(false)), Btn(L("Down", "Turun"), "ghost", () => BlindUpDownAsync(true))),
            SliderRow(L("Set point", "Set point"), nameof(SetpointTarget), 16, 28, 0.5, "{0:0.0} °C"),
            Row(8, Btn(L("Send set point", "Kirim set point"), "ghost", SetpointAsync), Btn(L("Economy mode", "Mode hemat"), "ghost", EconomyAsync)),
            tunnel), L("Visualisation", "Visualisasi"));

        var header = Columns("64,8,62,8,58,8,70,8,110,8,*",
            Head("TIME"), new Border(), Head("SOURCE"), new Border(), Head("GROUP"), new Border(), Head("SERVICE"), new Border(), Head("VALUE"), new Border(), Head("NAME"));
        TextBlock Head(string t) => new() { Text = t, FontSize = 10, Classes = { "muted" }, FontFamily = mono };
        var rows = new ItemsControl
        {
            ItemTemplate = new FuncDataTemplate<KnxMonitorRow>((r, _) => r is null ? new Border() : Columns("64,8,62,8,58,8,70,8,110,8,*",
                Cell(r.Time), new Border(), Cell(r.Source), new Border(), Cell(r.Destination, Palette.Blue), new Border(), Cell(r.Service, r.Service == "Write" ? Palette.Amber : null),
                new Border(), Cell(r.Value), new Border(), Cell(r.Name))),
        };
        TextBlock Cell(string t, IBrush? brush = null)
        {
            var tb = new TextBlock { Text = t, FontFamily = mono, FontSize = 10.5, TextTrimming = TextTrimming.CharacterEllipsis };
            if (brush is not null) tb.Foreground = brush;
            return tb;
        }

        rows.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(Monitor)));
        var count = new TextBlock { FontSize = 11, Classes = { "muted" } };
        count.Bind(TextBlock.TextProperty, new Binding(nameof(Telegrams)) { StringFormat = L("{0} telegrams on line 1.1", "{0} telegram di jalur 1.1") });
        var monitorCard = Card(Stack(6, header, new ScrollViewer { MaxHeight = 230, Content = rows }, count), L("Group monitor", "Group monitor"));

        var log = new ItemsControl { ItemTemplate = new FuncDataTemplate<string>((t, _) => new TextBlock { Text = t, FontFamily = mono, FontSize = 10.5, TextWrapping = TextWrapping.Wrap }) };
        log.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(Log)));
        var logCard = Card(new ScrollViewer { MaxHeight = 150, Content = log }, L("Visualisation log", "Log visualisasi"));

        var status = new TextBlock { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };
        status.Bind(TextBlock.TextProperty, new Binding(nameof(Status)));
        return new ScrollViewer
        {
            DataContext = this,
            Content = Stack(16, Columns("1.6*,16,*", Stack(16, plan, monitorCard), new Border(), Stack(16, operatorCard, logCard)), status),
        };
    }
}

/// <summary>Top-down plan of the floor: light glow by dimming value, blinds as slats on the façade, temperatures and valves.</summary>
internal sealed class FloorPlan : Control
{
    private static readonly IBrush Floor = new SolidColorBrush(Color.Parse("#2B3036"));
    private static readonly IBrush RoomFill = new SolidColorBrush(Color.Parse("#353B42"));
    private static readonly IBrush Wall = new SolidColorBrush(Color.Parse("#E4E5E0"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#8A9098"));
    private static readonly IBrush Glass = new SolidColorBrush(Color.Parse("#7FB3E6"), 0.55);
    private static readonly IBrush Slat = new SolidColorBrush(Color.Parse("#C9CBC4"));
    private readonly KnxOfficeDemo _demo;
    private readonly List<(Rect Box, int Number)> _boxes = [];

    public FloorPlan(KnxOfficeDemo demo)
    {
        _demo = demo;
        demo.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(InvalidateVisual);
        demo.PropertyChanged += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(InvalidateVisual);
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetPosition(this);
        foreach (var (box, number) in _boxes)
            if (box.Contains(p)) _demo.SelectedRoom = number;
    }

    private static FormattedText T(string s, double size, IBrush brush, string face = "JetBrains Mono", FontWeight weight = FontWeight.Normal) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(face, FontStyle.Normal, weight), size, brush);

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        ctx.DrawRectangle(Floor, null, new Rect(0, 0, w, h));
        _boxes.Clear();
        if (_demo.Floor is not { } floor)
        {
            ctx.DrawText(T("Start the demo to power line 1.1.", 13, Muted, "Barlow"), new Point(18, h / 2 - 8));
            return;
        }

        var corridor = 34.0;
        var rw = (w - 30) / 2;
        var rh = (h - 30 - corridor) / 2;
        var wallPen = new Pen(Wall, 3);
        for (var i = 0; i < floor.Rooms.Count; i++)
        {
            var r = floor.Rooms[i];
            var col = i % 2;
            var row = i / 2;
            var box = new Rect(10 + (col * (rw + 10)), 10 + (row * (rh + corridor)), rw, rh);
            _boxes.Add((box, r.Number));
            ctx.DrawRectangle(RoomFill, wallPen, box);

            // Light: a warm glow sized and tinted by the dimming value.
            if (r.LightOn && r.Dim > 0)
            {
                var glow = new RadialGradientBrush
                {
                    GradientStops = { new GradientStop(Color.FromArgb((byte)(60 + r.Dim * 1.6), 0xF2, 0xA9, 0x00), 0), new GradientStop(Color.FromArgb(0, 0xF2, 0xA9, 0x00), 1) },
                };
                var radius = Math.Min(rw, rh) * (0.25 + r.Dim / 250);
                ctx.DrawEllipse(glow, null, box.Center, radius, radius);
            }

            // Façade with blind slats: outer wall is the top wall for row 0 and the bottom wall for row 1.
            var windowY = row == 0 ? box.Top : box.Bottom;
            var window = new Rect(box.Left + 20, windowY - 4, rw - 40, 8);
            ctx.DrawRectangle(Glass, null, window);
            var slats = (int)Math.Round(r.Blind / 100 * 12);
            for (var s = 0; s < slats; s++)
            {
                var sx = window.Left + (s * window.Width / 12);
                ctx.DrawRectangle(Slat, null, new Rect(sx + 1, window.Top, window.Width / 12 - 2, window.Height));
            }

            // Text: name, temperature, set point, valve, presence.
            var inner = box.Deflate(14);
            var top = row == 0 ? inner.Top + 10 : inner.Top;
            ctx.DrawText(T(r.Name.ToUpperInvariant(), 11, Muted, "Barlow Condensed", FontWeight.SemiBold), new Point(inner.Left, top));
            ctx.DrawText(T($"{r.Temperature:0.0}°", 30, Wall, "Barlow Condensed", FontWeight.SemiBold), new Point(inner.Left, top + 14));
            ctx.DrawText(T($"set {r.Setpoint:0.0} · {(r.Mode switch { 1 => "comfort", 2 => "standby", 3 => "economy", _ => "protect" })}", 10, Muted), new Point(inner.Left, top + 52));
            var valveW = 60.0;
            ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#8A9098"), 0.3), null, new Rect(inner.Left, top + 70, valveW, 5), 2, 2);
            ctx.DrawRectangle(Palette.Red, null, new Rect(inner.Left, top + 70, valveW * r.Valve / 100, 5), 2, 2);
            ctx.DrawText(T($"valve {r.Valve:0}%", 9, Muted), new Point(inner.Left + valveW + 6, top + 66));
            ctx.DrawText(T(r.LightOn ? $"light {r.Dim:0}%" : "light off", 10, r.LightOn ? Palette.Amber : Muted), new Point(inner.Right - 70, top + 4));
            ctx.DrawText(T($"blind {r.Blind:0}%", 10, Muted), new Point(inner.Right - 70, top + 20));
            ctx.DrawEllipse(r.Presence ? Palette.Green : new SolidColorBrush(Color.Parse("#5B626B")), null, new Point(inner.Right - 6, top + 44), 5, 5);
            ctx.DrawText(T(r.Presence ? "occupied" : "empty", 9, Muted), new Point(inner.Right - 62, top + 38));

            if (r.Number == _demo.SelectedRoom) ctx.DrawRectangle(null, new Pen(Palette.Blue, 3), box.Inflate(2), 4, 4);
        }

        ctx.DrawText(T($"OUTDOOR {floor.OutdoorTemperature:0.0} °C · {floor.OutdoorLux / 1000:0} klux · CORRIDOR", 10, Muted, "Barlow Condensed", FontWeight.SemiBold), new Point(16, 10 + rh + (corridor / 2) - 6));
    }
}
