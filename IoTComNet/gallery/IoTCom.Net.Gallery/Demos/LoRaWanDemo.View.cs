using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using IoTCom.Net.Gallery.Infrastructure;
using IoTCom.Net.Protocols.LoRaWan;
using static IoTCom.Net.Gallery.Infrastructure.Loc;
using static IoTCom.Net.Gallery.Infrastructure.UiKit;

namespace IoTCom.Net.Gallery.Demos;

public sealed partial class LoRaWanDemo
{
    /// <summary>Spreading factor colours: green is fast and cheap, red is slow and expensive (IEC lamp colours at the ends).</summary>
    internal static IBrush SfBrush(int sf) => sf switch
    {
        7 => SfColors[0],
        8 => SfColors[1],
        9 => SfColors[2],
        10 => SfColors[3],
        11 => SfColors[4],
        12 => SfColors[5],
        _ => Palette.Grey,
    };

    internal static readonly IBrush[] SfColors =
    [
        new SolidColorBrush(Color.Parse("#2E9E5B")), new SolidColorBrush(Color.Parse("#7BAE3A")), new SolidColorBrush(Color.Parse("#C4B127")),
        new SolidColorBrush(Color.Parse("#F2A900")), new SolidColorBrush(Color.Parse("#E5711F")), new SolidColorBrush(Color.Parse("#D23B2F")),
    ];

    protected override Control CreateView()
    {
        var display = (FontFamily)Application.Current!.FindResource("DisplayFont")!;
        var mono = (FontFamily)Application.Current!.FindResource("MonoFont")!;

        // ---- signature: the radio map ----------------------------------------------------------------------
        var map = new RadioMap(this) { Height = 440, ClipToBounds = true };
        var mapScreen = new Border
        {
            Background = RadioMap.Glass, CornerRadius = new CornerRadius(14), BorderBrush = RadioMap.GlassLine, BorderThickness = new Thickness(1),
            Padding = new Thickness(4), Child = map,
        };

        // ---- devices as the network server sees them ---------------------------------------------------------
        var devices = new ItemsControl
        {
            ItemTemplate = new FuncDataTemplate<DeviceRow>((d, _) =>
            {
                var chip = new Border { CornerRadius = new CornerRadius(3), Padding = new Thickness(6, 1), VerticalAlignment = VerticalAlignment.Center };
                chip.Bind(Border.BackgroundProperty, new Binding(nameof(DeviceRow.SpreadingFactor)) { Converter = new Avalonia.Data.Converters.FuncValueConverter<int, IBrush>(SfBrush) });
                var chipText = new TextBlock { FontFamily = mono, FontSize = 10.5, FontWeight = FontWeight.Bold, Foreground = Brushes.White };
                chipText.Bind(TextBlock.TextProperty, new Binding(nameof(DeviceRow.DataRate)));
                chip.Child = chipText;
                TextBlock Line(string path, double size = 11.5, bool muted = true)
                {
                    var t = new TextBlock { FontFamily = mono, FontSize = size, TextWrapping = TextWrapping.Wrap };
                    if (muted) t.Classes.Add("muted");
                    t.Bind(TextBlock.TextProperty, new Binding(path));
                    return t;
                }

                var name = new TextBlock { Text = d.Name, FontFamily = display, FontWeight = FontWeight.Bold, FontSize = 17, VerticalAlignment = VerticalAlignment.Center };
                var kind = new TextBlock { Text = SensorLabel(d.Sensor), Classes = { "muted" }, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
                var state = Line(nameof(DeviceRow.State), 11);
                state.HorizontalAlignment = HorizontalAlignment.Right;
                var head = Columns("auto,8,auto,8,auto,*", name, new Border(), chip, new Border(), kind, state);
                var battery = Line(nameof(DeviceRow.Battery), 11);
                var card = new Border
                {
                    Classes = { "device" }, Padding = new Thickness(12, 9), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
                    Cursor = new Cursor(StandardCursorType.Hand),
                    Child = Stack(3, head, Line(nameof(DeviceRow.Reading), 12.5, muted: false), Line(nameof(DeviceRow.Link)), Columns("*,auto", Line(nameof(DeviceRow.Counters), 11), battery)),
                };
                card.Bind(Border.BorderBrushProperty, new Binding(nameof(SelectedDevice))
                {
                    Source = this,
                    Converter = new Avalonia.Data.Converters.FuncValueConverter<string?, IBrush>(sel => sel == d.Name ? Palette.Amber : RadioMap.CardLine),
                });
                card.PointerPressed += (_, _) => SelectedDevice = d.Name;
                return card;
            }),
            ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Spacing = 8 }),
        };
        devices.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(DeviceRows)));
        var devicesCard = Card(devices, L("Devices · network server view", "Perangkat · tampilan network server"));

        // ---- actions on the selected device ------------------------------------------------------------------
        Button Action(string text, Action act, string cls = "ghost")
        {
            var b = new Button { Content = text, Classes = { cls } };
            b.Click += (_, _) => act();
            b.Bind(InputElement.IsEnabledProperty, new Binding(nameof(IsRunning)));
            return b;
        }

        var selected = new TextBlock { FontFamily = display, FontWeight = FontWeight.Bold, FontSize = 20 };
        selected.Bind(TextBlock.TextProperty, new Binding(nameof(SelectedDevice)) { TargetNullValue = L("Select a sensor", "Pilih sensor") });
        var lastAction = new TextBlock { FontFamily = mono, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Classes = { "muted" } };
        lastAction.Bind(TextBlock.TextProperty, new Binding(nameof(LastAction)));
        var actions = Card(Stack(10,
            selected,
            new TextBlock { Text = L("Drag it on the map to change its link. Downlinks wait for the next uplink (Class A).", "Seret di peta untuk mengubah link-nya. Downlink menunggu uplink berikutnya (Class A)."), Classes = { "muted" }, TextWrapping = TextWrapping.Wrap, FontSize = 12 },
            new WrapPanel
            {
                ItemSpacing = 8, LineSpacing = 6,
                Children =
                {
                    Action(L("Report now", "Lapor sekarang"), ReportNow, "primary"),
                    Action(L("Every 10 s (downlink)", "Tiap 10 dtk (downlink)"), () => SetInterval(10)),
                    Action(L("Every 60 s (downlink)", "Tiap 60 dtk (downlink)"), () => SetInterval(60)),
                    Action(L("Ask battery (DevStatusReq)", "Tanya baterai (DevStatusReq)"), AskStatus),
                },
            },
            lastAction), L("Selected sensor", "Sensor terpilih"));

        Control Stat(string label, string path, string format, IBrush? colour = null)
        {
            var v = new TextBlock { FontFamily = display, FontWeight = FontWeight.Bold, FontSize = 24 };
            if (colour is not null) v.Foreground = colour;
            v.Bind(TextBlock.TextProperty, new Binding(path) { StringFormat = format });
            return Stack(0, Eyebrow(label), v);
        }

        var stats = Card(Columns("*,*,*,*,*",
            Stat(L("Uplinks", "Uplink"), nameof(Uplinks), "{0:N0}"),
            Stat(L("Lost", "Hilang"), nameof(LostUplinks), "{0:N0}", Palette.Red),
            Stat(L("Downlinks", "Downlink"), nameof(Downlinks), "{0:N0}", Palette.Amber),
            Stat(L("Copies merged", "Salinan digabung"), nameof(DuplicatesDropped), "{0:N0}", Palette.Blue),
            Stat(L("Time on air", "Waktu di udara"), nameof(AirtimeSeconds), "{0:0.0} s")), L("Network", "Jaringan"));

        // ---- on the air: one row per transmission, the bar drawn to scale with its time on air ---------------
        var air = new ItemsControl
        {
            ItemTemplate = new FuncDataTemplate<AirRow>((a, _) =>
            {
                var arrow = new TextBlock { Text = a.Uplink ? "▲" : "▼", Foreground = a.Lost ? Palette.Red : a.Uplink ? Palette.Blue : Palette.Amber, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
                var bar = new Border
                {
                    Height = 8, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
                    Width = Math.Max(3, a.AirtimeMs / 1500 * 150), Background = a.Lost ? Palette.Red : SfBrush(a.SpreadingFactor), Opacity = a.Uplink ? 1 : 0.65,
                };
                ToolTip.SetTip(bar, $"{a.DataRate} · {a.AirtimeMs:0.0} ms");
                var airText = new TextBlock { Text = $"{a.AirtimeMs:0} ms", FontFamily = mono, FontSize = 10.5, Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center };
                var summary = new TextBlock
                {
                    Text = a.Lost ? L("lost — no gateway in range", "hilang — tak ada gateway dalam jangkauan") + " · " + a.Summary : a.Summary,
                    FontFamily = mono, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
                };
                if (a.Lost) summary.Foreground = Palette.Red;
                var heard = new TextBlock { Text = a.Heard, FontFamily = mono, FontSize = 10.5, Classes = { "muted" }, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                return new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("64,18,84,156,44,1.2*,10,*"), Margin = new Thickness(0, 2),
                    Children =
                    {
                        At(new TextBlock { Text = a.Time, FontFamily = mono, FontSize = 10.5, Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center }, 0),
                        At(arrow, 1),
                        At(new TextBlock { Text = a.Device, FontFamily = mono, FontSize = 11.5, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center }, 2),
                        At(bar, 3), At(airText, 4), At(summary, 5), At(heard, 7),
                    },
                };
            }),
        };
        air.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(Air)));
        var airCard = Card(air, L("On the air · bar length = time on air", "Di udara · panjang bar = waktu di udara"));

        var status = new TextBlock { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };
        status.Bind(TextBlock.TextProperty, new Binding(nameof(Status)));

        return new ScrollViewer
        {
            DataContext = this,
            Content = Stack(16,
                Columns("1.6*,16,*", mapScreen, new Border(), devicesCard),
                Columns("*,16,1.6*", actions, new Border(), stats),
                airCard,
                status),
        };
    }

    private static string SensorLabel(LoRaWanSensorKind kind) => kind switch
    {
        LoRaWanSensorKind.Environment => L("weather", "cuaca"),
        LoRaWanSensorKind.SoilMoisture => L("soil", "tanah"),
        LoRaWanSensorKind.WaterMeter => L("water meter", "meter air"),
        _ => L("GPS tracker", "pelacak GPS"),
    };

    private static Control At(Control c, int column)
    {
        Grid.SetColumn(c, column);
        return c;
    }
}

/// <summary>
/// The radio map: gateways with the reach of each spreading factor (6 dB margin rings), sensors coloured by their
/// spreading factor, and every transmission as it happens — an uplink ripples out and links to each gateway that heard
/// it (coloured by SNR margin), a downlink travels from the gateway to the sensor. Sensors can be dragged.
/// </summary>
internal sealed class RadioMap : Control
{
    internal static readonly IBrush Glass = new SolidColorBrush(Color.Parse("#14181C"));
    internal static readonly IBrush GlassLine = new SolidColorBrush(Color.Parse("#2B3036"));
    internal static readonly IBrush CardLine = new SolidColorBrush(Color.FromArgb(60, 138, 144, 152));
    private static readonly IPen Grid = new Pen(new SolidColorBrush(Color.Parse("#20262C")), 1);
    private static readonly IBrush Label = new SolidColorBrush(Color.Parse("#8C96A0"));
    private static readonly IBrush Ink = new SolidColorBrush(Color.Parse("#F1F2EE"));
    private static readonly Typeface Face = new("avares://IoTCom.Net.Gallery/Assets/Fonts#JetBrains Mono");
    private static readonly Typeface Display = new("avares://IoTCom.Net.Gallery/Assets/Fonts#Barlow Condensed", FontStyle.Normal, FontWeight.Bold);
    private const double MinX = -3, MaxX = 8, MinY = -4, MaxY = 5;
    private readonly LoRaWanDemo _demo;
    private readonly DispatcherTimer _timer;
    private string? _dragging;

    public RadioMap(LoRaWanDemo demo)
    {
        _demo = demo;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => InvalidateVisual());
        demo.Scene.Changed += () => Dispatcher.UIThread.Post(InvalidateVisual);
        AttachedToVisualTree += (_, _) => _timer.Start();
        DetachedFromVisualTree += (_, _) => _timer.Stop();
        Cursor = new Cursor(StandardCursorType.Arrow);
    }

    /// <summary>Distance (km) up to which a spreading factor keeps a 6 dB margin in the simulator's path-loss model.</summary>
    internal static double Reach(int sf) => Math.Pow(10, (7 - LoRaWanNetworkServer.DemodulationFloor(sf)) / 35);

    private (double Scale, Point Origin) Frame()
    {
        var scale = Math.Min(Bounds.Width / (MaxX - MinX), Bounds.Height / (MaxY - MinY));
        var origin = new Point((Bounds.Width - ((MaxX - MinX) * scale)) / 2 - (MinX * scale), (Bounds.Height + ((MaxY - MinY) * scale)) / 2 + (MinY * scale));
        return (scale, origin);
    }

    private Point Px(double x, double y)
    {
        var (s, o) = Frame();
        return new Point(o.X + (x * s), o.Y - (y * s));
    }

    private (double X, double Y) Km(Point p)
    {
        var (s, o) = Frame();
        return ((p.X - o.X) / s, (o.Y - p.Y) / s);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetPosition(this);
        var (_, devices, _) = _demo.Scene.Snapshot();
        var hit = devices.Select(d => (d.Name, Dist: Distance(Px(d.X, d.Y), p))).Where(d => d.Dist < 16).OrderBy(d => d.Dist).FirstOrDefault();
        if (hit.Name is null) return;
        _demo.SelectedDevice = hit.Name;
        _dragging = hit.Name;
        e.Pointer.Capture(this);
        Cursor = new Cursor(StandardCursorType.SizeAll);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging is null) return;
        var (x, y) = Km(e.GetPosition(this));
        _demo.MoveDevice(_dragging, Math.Clamp(x, MinX, MaxX), Math.Clamp(y, MinY, MaxY));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = null;
        e.Pointer.Capture(null);
        Cursor = new Cursor(StandardCursorType.Arrow);
    }

    private static double Distance(Point a, Point b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    private static FormattedText Text(string text, double size, IBrush brush, Typeface? face = null) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face ?? Face, size, brush);

    private static IBrush Alpha(IBrush brush, double opacity) =>
        brush is ISolidColorBrush s ? new SolidColorBrush(s.Color, opacity) : brush;

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        ctx.FillRectangle(Glass, new Rect(Bounds.Size));
        var (scale, _) = Frame();
        var (gateways, devices, pulses) = _demo.Scene.Snapshot();
        var now = DateTime.UtcNow;

        // 1 km grid over the whole screen.
        var (left, top) = Km(default);
        var (right, bottom) = Km(new Point(w, h));
        for (var x = Math.Ceiling(left); x <= right; x++) ctx.DrawLine(Grid, Px(x, top), Px(x, bottom));
        for (var y = Math.Ceiling(bottom); y <= top; y++) ctx.DrawLine(Grid, Px(left, y), Px(right, y));

        // Reach of each spreading factor around every gateway: SF12 outermost, SF7 innermost.
        foreach (var g in gateways)
        {
            var c = Px(g.X, g.Y);
            for (var sf = 12; sf >= 7; sf--)
            {
                var r = Reach(sf) * scale;
                var brush = LoRaWanDemo.SfBrush(sf);
                ctx.DrawEllipse(sf == 7 ? Alpha(brush, 0.06) : null, new Pen(Alpha(brush, 0.4), 1, new DashStyle([2, 5], 0)), c, r, r);
            }
        }

        if (gateways.Count > 0)
        {
            // Label the rings once, along the rooftop gateway's north-east diagonal.
            var g0 = gateways[0];
            for (var sf = 7; sf <= 12; sf++)
            {
                var r = Reach(sf) * scale;
                var at = Px(g0.X, g0.Y) + new Point(r * 0.7071, -r * 0.7071);
                var t = Text($"SF{sf}", 9.5, Alpha(LoRaWanDemo.SfBrush(sf), 0.9));
                ctx.DrawText(t, new Point(at.X + 3, at.Y - t.Height));
            }
        }

        // Transmissions.
        foreach (var p in pulses)
        {
            var age = (now - p.Start).TotalSeconds;
            var e = p.Event;
            var device = Px(p.X, p.Y);
            var sf = e.DataRate.Length > 2 ? LoRaDataRate.ParseDatr(e.DataRate).Sf : 7;
            if (e.Uplink)
            {
                var fade = Math.Clamp(1 - (age / 3.0), 0, 1);
                foreach (var (gwName, rssi, snr) in e.Receptions)
                {
                    var g = gateways.FirstOrDefault(x => x.Name == gwName);
                    if (g is null) continue;
                    var margin = snr - LoRaWanNetworkServer.DemodulationFloor(sf);
                    var colour = margin >= 6 ? Palette.Green : margin >= 0 ? Palette.Amber : Palette.Red;
                    var gp = Px(g.X, g.Y);
                    ctx.DrawLine(new Pen(Alpha(colour, 0.85 * fade), 1.6), device, gp);
                    if (fade > 0.15 && e.Device == _demo.Scene.Selected)
                    {
                        var mid = new Point((device.X + gp.X) / 2, (device.Y + gp.Y) / 2);
                        var label = Text($"{rssi:0} dBm  {snr:+0.0;-0.0} dB", 9.5, Alpha(Ink, fade));
                        ctx.FillRectangle(Alpha(Glass, 0.75 * fade), new Rect(mid.X - 2, mid.Y - label.Height / 2 - 1, label.Width + 4, label.Height + 2));
                        ctx.DrawText(label, new Point(mid.X, mid.Y - label.Height / 2));
                    }
                }

                if (age < 1.4)
                {
                    var k = age / 1.4;
                    var lost = e.Receptions.Count == 0;
                    var ring = lost ? Palette.Red : LoRaWanDemo.SfBrush(sf);
                    ctx.DrawEllipse(null, new Pen(Alpha(ring, 1 - k), 2.2), device, 8 + (k * 60), 8 + (k * 60));
                    if (lost)
                    {
                        var x = Text("×", 22, Alpha(Palette.Red, 1 - k), Display);
                        ctx.DrawText(x, new Point(device.X + 10, device.Y - x.Height));
                    }
                }
            }
            else if (age < 2.5 && e.Gateway is { } gwName)
            {
                var g = gateways.FirstOrDefault(x => x.Name == gwName);
                if (g is null) continue;
                var gp = Px(g.X, g.Y);
                var fade = Math.Clamp(1 - (age / 2.5), 0, 1);
                ctx.DrawLine(new Pen(Alpha(Palette.Amber, 0.9 * fade), 1.6, new DashStyle([6, 4], -age * 40)), gp, device);
                var k = Math.Clamp(age / 0.6, 0, 1);
                var dot = new Point(gp.X + ((device.X - gp.X) * k), gp.Y + ((device.Y - gp.Y) * k));
                ctx.DrawEllipse(Palette.Amber, null, dot, 4, 4);
                var tag = Text(e.Summary.StartsWith("Join-Accept", StringComparison.Ordinal) ? "Join-Accept" : "RX1", 10, Alpha(Palette.Amber, fade));
                ctx.DrawText(tag, new Point(device.X + 12, device.Y + 4));
            }
        }

        // Gateways: a mast with the name.
        foreach (var g in gateways)
        {
            var c = Px(g.X, g.Y);
            var mast = new StreamGeometry();
            using (var s = mast.Open())
            {
                s.BeginFigure(new Point(c.X, c.Y - 14), true);
                s.LineTo(new Point(c.X + 7, c.Y + 7));
                s.LineTo(new Point(c.X - 7, c.Y + 7));
                s.EndFigure(true);
            }

            ctx.DrawGeometry(Ink, new Pen(Glass, 1.5), mast);
            ctx.DrawEllipse(null, new Pen(Alpha(Ink, 0.6), 1.2), new Point(c.X, c.Y - 14), 5, 5);
            ctx.DrawEllipse(null, new Pen(Alpha(Ink, 0.3), 1.2), new Point(c.X, c.Y - 14), 10, 10);
            var name = Text(g.Name, 10.5, Label);
            ctx.DrawText(name, new Point(c.X - name.Width / 2, c.Y + 10));
        }

        // Sensors: coloured by spreading factor; grey until joined.
        foreach (var d in devices)
        {
            var c = Px(d.X, d.Y);
            var fill = d.Joined ? LoRaWanDemo.SfBrush(d.Sf) : Palette.Grey;
            var selected = d.Name == _demo.Scene.Selected;
            if (selected) ctx.DrawEllipse(null, new Pen(Ink, 1.6), c, 12, 12);
            ctx.DrawEllipse(fill, new Pen(Glass, 2), c, 7, 7);
            var label = Text(d.Name, selected ? 11.5 : 10.5, selected ? Ink : Label);
            ctx.DrawText(label, new Point(c.X + 13, c.Y - label.Height / 2));
            if (d.Joined)
            {
                var sf = Text($"SF{d.Sf}", 9.5, Alpha(LoRaWanDemo.SfBrush(d.Sf), 0.95));
                ctx.DrawText(sf, new Point(c.X + 13, c.Y + label.Height / 2 - 2));
            }
        }

        // Legend: the colour of a spreading factor and what it costs for 12 bytes.
        var lx = 12.0;
        var ly = h - 30;
        var title = Text("SPREADING FACTOR · TIME ON AIR FOR 12 B", 9, Label);
        ctx.DrawText(title, new Point(lx, ly - 14));
        for (var sf = 7; sf <= 12; sf++)
        {
            var ms = LoRaAirtime.Compute(25, sf).TotalMilliseconds;
            ctx.FillRectangle(LoRaWanDemo.SfBrush(sf), new Rect(lx, ly + 2, 10, 10), 2);
            var t = Text(ms >= 1000 ? $"SF{sf} {ms / 1000:0.0} s" : $"SF{sf} {ms:0} ms", 10, Ink);
            ctx.DrawText(t, new Point(lx + 14, ly));
            lx += 14 + t.Width + 12;
        }

        // Scale bar and north.
        var bar = scale;
        var bx = w - bar - 16;
        var by = h - 18;
        ctx.DrawLine(new Pen(Label, 2), new Point(bx, by), new Point(bx + bar, by));
        ctx.DrawLine(new Pen(Label, 2), new Point(bx, by - 4), new Point(bx, by + 2));
        ctx.DrawLine(new Pen(Label, 2), new Point(bx + bar, by - 4), new Point(bx + bar, by + 2));
        var km = Text("1 km", 10, Label);
        ctx.DrawText(km, new Point(bx + (bar - km.Width) / 2, by - km.Height - 3));
        var north = Text("N ↑  AS923-2 · JAKARTA", 10, Label);
        ctx.DrawText(north, new Point(w - north.Width - 14, 10));
        var hint = Text(Loc.L("drag a sensor to move it", "seret sensor untuk memindahkannya"), 10, Alpha(Label, 0.8));
        ctx.DrawText(hint, new Point(12, 10));
    }
}
