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
using IoTCom.Net.Protocols.Iec104;
using static IoTCom.Net.Gallery.Infrastructure.Loc;
using static IoTCom.Net.Gallery.Infrastructure.UiKit;

namespace IoTCom.Net.Gallery.Demos;

public sealed partial class SubstationDemo
{
    protected override Control CreateView()
    {
        var mono = (FontFamily)Application.Current!.FindResource("MonoFont")!;
        var mimic = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#E4E5E0")), CornerRadius = new CornerRadius(14), Padding = new Thickness(6),
            BorderBrush = new SolidColorBrush(Color.Parse("#C9CBC4")), BorderThickness = new Thickness(1),
            Child = new SingleLineDiagram(this) { Height = 470 },
        };

        Control Lamp(string label, string path, IBrush on)
        {
            var dot = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(7) };
            dot.Bind(Border.BackgroundProperty, new Binding(path) { Converter = new FuncValueConverter<bool, IBrush>(v => v ? on : new SolidColorBrush(Color.Parse("#C9CBC4"))) });
            return Row(8, dot, new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        }

        var unlock = new ToggleSwitch { OnContent = L("Control unlocked", "Kendali terbuka"), OffContent = L("Control locked (read-only)", "Kendali terkunci (hanya-baca)") };
        unlock.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(ControlUnlocked)) { Mode = BindingMode.TwoWay });
        var device = new ComboBox { ItemsSource = new[] { "Q0", "Q1", "Q8" }, MinWidth = 80 };
        device.Bind(SelectingItemsControl.SelectedItemProperty, new Binding(nameof(Device)) { Mode = BindingMode.TwoWay });
        Button Btn(string text, string cls, Func<Task> action)
        {
            var b = new Button { Content = text, Classes = { cls } };
            b.Click += async (_, _) => await action();
            b.Bind(InputElement.IsEnabledProperty, new Binding(nameof(IsRunning)));
            return b;
        }

        var execute = new Button { Content = L("Execute", "Eksekusi"), Classes = { "primary" } };
        execute.Click += async (_, _) => await ExecuteAsync();
        execute.Bind(InputElement.IsEnabledProperty, new Binding(nameof(Selected)));
        var selection = new TextBlock { FontFamily = mono, FontSize = 12, Foreground = Palette.Blue, VerticalAlignment = VerticalAlignment.Center };
        selection.Bind(TextBlock.TextProperty, new Binding(nameof(Selection)) { StringFormat = "▸ {0}" });
        selection.Bind(Visual.IsVisibleProperty, new Binding(nameof(Selected)));
        var q = new Slider { Minimum = -3, Maximum = 3, TickFrequency = 0.1, IsSnapToTickEnabled = true, MinWidth = 140 };
        q.Bind(RangeBase.ValueProperty, new Binding(nameof(ReactiveSetpoint)) { Mode = BindingMode.TwoWay });
        var qText = new TextBlock { FontFamily = mono, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        qText.Bind(TextBlock.TextProperty, new Binding(nameof(ReactiveSetpoint)) { StringFormat = "{0:0.0} Mvar" });

        var control = Card(Stack(12,
            unlock,
            new TextBlock { Text = L("Switching uses select-before-operate: select the device and the direction, wait for the confirmation, then execute.", "Pensaklaran memakai select-before-operate: pilih perangkat dan arahnya, tunggu konfirmasi, lalu eksekusi."), FontSize = 11.5, Classes = { "muted" }, TextWrapping = TextWrapping.Wrap },
            Row(8, device, Btn(L("Select open", "Pilih buka"), "ghost", () => SelectAsync(false)), Btn(L("Select close", "Pilih tutup"), "ghost", () => SelectAsync(true))),
            Row(10, execute, selection),
            Row(8, Btn(L("Tap ▲", "Tap ▲"), "ghost", () => StepTapAsync(true)), Btn(L("Tap ▼", "Tap ▼"), "ghost", () => StepTapAsync(false)), Btn(L("Reset protection", "Reset proteksi"), "ghost", ResetProtectionAsync)),
            Row(8, new TextBlock { Text = "Q", VerticalAlignment = VerticalAlignment.Center }, q, qText, Btn(L("Send", "Kirim"), "ghost", ApplySetpointAsync))),
            L("Operator", "Operator"));

        var fault = new Button { Content = L("Short circuit on the feeder", "Hubung singkat di penyulang"), Classes = { "ghost" } };
        fault.Click += (_, _) => ShortCircuit();
        var gas = new Button { Content = L("SF6 leak on/off", "Kebocoran SF6 nyala/mati"), Classes = { "ghost" } };
        gas.Click += (_, _) => ToggleGas();
        var alarms = Card(Stack(10,
            Row(18, Lamp(L("Protection trip", "Proteksi trip"), nameof(Tripped), Palette.Red), Lamp(L("SF6 low", "SF6 rendah"), nameof(GasLow), Palette.Amber)),
            Row(8, fault, gas)), L("Alarms and field events", "Alarm dan kejadian lapangan"));

        var events = new ItemsControl { ItemTemplate = new FuncDataTemplate<string>((t, _) => new TextBlock { Text = t, FontFamily = mono, FontSize = 11, TextWrapping = TextWrapping.NoWrap }) };
        events.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(Events)));
        var soe = Card(events, L("Sequence of events (RTU time tags)", "Urutan kejadian (tanda waktu RTU)"));

        var status = new TextBlock { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };
        status.Bind(TextBlock.TextProperty, new Binding(nameof(Status)));
        return new ScrollViewer
        {
            DataContext = this,
            Content = Stack(16, Columns("1.25*,16,*", Stack(16, mimic, soe), new Border(), Stack(16, control, alarms)), status),
        };
    }
}

/// <summary>
/// The bay's single-line diagram. Conductors are amber when live, grey when dead and green when earthed; a switching
/// device is filled when closed, hollow when open and dashed while moving. Click a device to pick it for switching.
/// </summary>
internal sealed class SingleLineDiagram : Control
{
    private static readonly Color LiveColor = Color.Parse("#F2A900"), DeadColor = Color.Parse("#8A9098"), EarthColor = Color.Parse("#2E9E5B");
    private static readonly IBrush Ink = new SolidColorBrush(Color.Parse("#2B3036"));
    private static readonly IBrush Paper = new SolidColorBrush(Color.Parse("#F4F4F1"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#6B7178"));
    private static readonly IBrush Select = new SolidColorBrush(Color.Parse("#2F6FD6"));
    private readonly SubstationDemo _demo;
    private Rect _q0, _q1, _q8;

    public SingleLineDiagram(SubstationDemo demo)
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
        if (_q0.Inflate(8).Contains(p)) _demo.Device = "Q0";
        else if (_q1.Inflate(8).Contains(p)) _demo.Device = "Q1";
        else if (_q8.Inflate(8).Contains(p)) _demo.Device = "Q8";
    }

    private static Pen Conductor(Color c, double width = 5) => new(new SolidColorBrush(c), width, lineCap: PenLineCap.Round);

    private static FormattedText Label(string text, double size, IBrush brush, bool bold = false, string face = "Barlow Condensed") =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(face, FontStyle.Normal, bold ? FontWeight.Bold : FontWeight.Normal), size, brush);

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var d = _demo;
        var x = Math.Round(w * 0.36);
        bool closed(Iec104DoublePoint s) => s == Iec104DoublePoint.On;
        var busLive = d.Voltage > 1;
        var upperLive = busLive && closed(d.Disconnector);
        var feederLive = upperLive && closed(d.Breaker);
        var feederEarthed = closed(d.Earthing);
        Color Section(bool live, bool earthed) => earthed ? EarthColor : live ? LiveColor : DeadColor;

        // Busbar.
        ctx.DrawLine(Conductor(busLive ? LiveColor : DeadColor, 7), new Point(30, 56), new Point(w - 30, 56));
        ctx.DrawText(Label("20 kV  BUSBAR A", 15, Ink, bold: true), new Point(30, 24));
        ctx.DrawText(Label($"{d.Voltage:0.00} kV  ·  {d.Frequency:0.00} Hz", 12, Muted, face: "JetBrains Mono"), new Point(w - 190, 30));

        // Q1 disconnector: fixed contact at 92, pivot at 132.
        var q1Colour = Section(upperLive, feederEarthed && closed(d.Disconnector) && closed(d.Breaker));
        ctx.DrawLine(Conductor(busLive ? LiveColor : DeadColor), new Point(x, 56), new Point(x, 92));
        var q1Blade = closed(d.Disconnector) ? new Point(x, 92) : d.Disconnector == Iec104DoublePoint.Off ? new Point(x + 22, 98) : new Point(x + 12, 94);
        ctx.DrawLine(Conductor(q1Colour, 4), new Point(x, 132), q1Blade);
        ctx.DrawLine(new Pen(Ink, 2), new Point(x - 9, 92), new Point(x + 9, 92));
        ctx.DrawEllipse(Ink, null, new Point(x, 132), 3.5, 3.5);
        _q1 = new Rect(x - 16, 86, 46, 50);
        Device(ctx, "Q1", "disconnector", d.Disconnector, _q1, new Point(x - 165, 100));

        // Q0 circuit breaker.
        ctx.DrawLine(Conductor(Section(upperLive, false)), new Point(x, 132), new Point(x, 168));
        _q0 = new Rect(x - 20, 168, 40, 40);
        var q0Brush = closed(d.Breaker) ? new SolidColorBrush(Section(feederLive, feederEarthed)) : (IBrush)Paper;
        var q0Pen = d.Breaker == Iec104DoublePoint.Intermediate ? new Pen(Ink, 2.5, new DashStyle([3, 2], 0)) : new Pen(Ink, 2.5);
        ctx.DrawRectangle(q0Brush, q0Pen, _q0, 3, 3);
        if (!closed(d.Breaker)) ctx.DrawLine(new Pen(Ink, 2), _q0.TopLeft + new Point(8, 8), _q0.BottomRight - new Point(8, 8));
        Device(ctx, "Q0", "circuit breaker", d.Breaker, _q0, new Point(x - 165, 176));

        // Feeder section with CT, earthing branch and transformer.
        var feeder = Conductor(Section(feederLive, feederEarthed));
        ctx.DrawLine(feeder, new Point(x, 208), new Point(x, 330));
        ctx.DrawEllipse(null, new Pen(Ink, 2), new Point(x, 236), 11, 11);
        ctx.DrawText(Label("CT 400/1 A", 11, Muted, face: "JetBrains Mono"), new Point(x - 86, 229));

        // Q8 earthing switch: branch at 270 to the right, blade down to earth.
        var ex = x + 78;
        ctx.DrawLine(feeder, new Point(x, 270), new Point(ex, 270));
        ctx.DrawEllipse(Ink, null, new Point(x, 270), 4, 4);
        var q8Blade = closed(d.Earthing) ? new Point(ex, 270) : d.Earthing == Iec104DoublePoint.Off ? new Point(ex + 20, 276) : new Point(ex + 10, 272);
        ctx.DrawLine(Conductor(feederEarthed ? EarthColor : DeadColor, 4), new Point(ex, 310), q8Blade);
        ctx.DrawEllipse(Ink, null, new Point(ex, 310), 3.5, 3.5);
        for (var i = 0; i < 3; i++) ctx.DrawLine(new Pen(Ink, 2), new Point(ex - 12 + (i * 4), 318 + (i * 5)), new Point(ex + 12 - (i * 4), 318 + (i * 5)));
        _q8 = new Rect(ex - 14, 262, 46, 54);
        Device(ctx, "Q8", "earthing switch", d.Earthing, _q8, new Point(ex + 38, 278));

        // Transformer T1 and the outgoing feeder.
        var tPen = new Pen(new SolidColorBrush(Section(feederLive, feederEarthed)), 3);
        ctx.DrawEllipse(null, tPen, new Point(x, 350), 22, 22);
        ctx.DrawEllipse(null, new Pen(new SolidColorBrush(feederLive && !feederEarthed ? LiveColor : DeadColor), 3), new Point(x, 380), 22, 22);
        ctx.DrawText(Label($"T1  20/0.4 kV  1 MVA   tap {d.TapPosition:+0;-0;0}", 12, Ink, face: "JetBrains Mono"), new Point(x + 36, 348));
        ctx.DrawText(Label($"oil {d.OilTemperature:0} °C", 11, Muted, face: "JetBrains Mono"), new Point(x + 36, 366));
        ctx.DrawLine(Conductor(feederLive && !feederEarthed ? LiveColor : DeadColor), new Point(x, 402), new Point(x, 440));
        var arrow = new StreamGeometry();
        using (var g = arrow.Open())
        {
            g.BeginFigure(new Point(x - 9, 436), true);
            g.LineTo(new Point(x + 9, 436));
            g.LineTo(new Point(x, 452));
            g.EndFigure(true);
        }

        ctx.DrawGeometry(new SolidColorBrush(feederLive && !feederEarthed ? LiveColor : DeadColor), null, arrow);
        ctx.DrawText(Label("FEEDER 7 · CIKARANG INDUSTRIAL ESTATE", 13, Ink, bold: true), new Point(x + 20, 432));

        // Measurements panel on the right.
        var mx = Math.Max(x + 200, w - 190);
        var rows = new (string Label, string Value)[]
        {
            ("I", $"{d.Current:0.0} A"), ("P", $"{d.ActivePower:0.00} MW"), ("Q", $"{d.ReactivePower:0.00} Mvar"),
            ("E", $"{d.Energy:#,0} kWh"),
        };
        for (var i = 0; i < rows.Length; i++)
        {
            ctx.DrawText(Label(rows[i].Label, 13, Muted, bold: true), new Point(mx, 120 + (i * 34)));
            ctx.DrawText(Label(rows[i].Value, 19, Ink, face: "JetBrains Mono"), new Point(mx + 22, 116 + (i * 34)));
        }

        // Legend, bottom left.
        var ly = 396.0;
        foreach (var (c, text) in new[] { (LiveColor, "live"), (DeadColor, "dead"), (EarthColor, "earthed") })
        {
            ctx.DrawLine(Conductor(c, 5), new Point(30, ly + 8), new Point(52, ly + 8));
            ctx.DrawText(Label(text, 12, Muted), new Point(60, ly));
            ly += 20;
        }
    }

    private void Device(DrawingContext ctx, string tag, string name, Iec104DoublePoint state, Rect area, Point labelAt)
    {
        var picked = _demo.Device == tag;
        var selected = picked && _demo.Selected;
        if (picked)
            ctx.DrawRectangle(null, selected ? new Pen(Select, 2.5) : new Pen(Select, 1.5, new DashStyle([4, 3], 0)), area.Inflate(6), 6, 6);
        ctx.DrawText(Label(tag, 18, Ink, bold: true), labelAt);
        var stateText = state switch { Iec104DoublePoint.On => "CLOSED", Iec104DoublePoint.Off => "OPEN", Iec104DoublePoint.Intermediate => "MOVING", _ => "FAULTY" };
        ctx.DrawText(Label($"{name} · {stateText}{(selected ? " · SELECTED" : "")}", 11, selected ? Select : Muted), labelAt + new Point(0, 22));
    }
}
