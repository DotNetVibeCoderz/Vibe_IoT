using System.Collections.Specialized;
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

public sealed partial class DroneDemo
{
    private static readonly IBrush Glass = new SolidColorBrush(Color.Parse("#14181C"));
    private static readonly IBrush GlassLine = new SolidColorBrush(Color.Parse("#2B3036"));
    private static readonly IBrush GlassLabel = new SolidColorBrush(Color.Parse("#8C96A0"));
    private static readonly IBrush GlassInk = new SolidColorBrush(Color.Parse("#F1F2EE"));

    protected override Control CreateView()
    {
        var display = (FontFamily)Application.Current!.FindResource("DisplayFont")!;
        var mono = (FontFamily)Application.Current!.FindResource("MonoFont")!;

        // ---- signature: the artificial horizon -----------------------------------------------------------
        var horizon = new AttitudeIndicator { Width = 300, Height = 300 };
        horizon.Bind(AttitudeIndicator.RollProperty, new Binding(nameof(Roll)));
        horizon.Bind(AttitudeIndicator.PitchProperty, new Binding(nameof(Pitch)));

        Control Gauge(string label, string path, string format, string unit)
        {
            var v = new TextBlock { FontFamily = display, FontWeight = FontWeight.Bold, FontSize = 30, Foreground = GlassInk };
            v.Bind(TextBlock.TextProperty, new Binding(path) { StringFormat = format });
            return Stack(0,
                new TextBlock { Text = label.ToUpperInvariant(), Foreground = GlassLabel, FontSize = 10, LetterSpacing = 1.4 },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { v, new TextBlock { Text = unit, Foreground = GlassLabel, FontSize = 13, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 5) } } });
        }

        var map = new TrackMap { Height = 300, MinWidth = 260, ClipToBounds = true };
        map.Bind(TrackMap.PositionProperty, new Binding(nameof(Position)));
        map.Bind(TrackMap.HeadingProperty, new Binding(nameof(Heading)));
        map.SetTrack(Track);

        var armedLamp = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(10, 3), HorizontalAlignment = HorizontalAlignment.Left };
        var armedText = new TextBlock { FontFamily = display, FontWeight = FontWeight.Bold, FontSize = 13, LetterSpacing = 1.6 };
        armedLamp.Child = armedText;
        armedLamp.Bind(Border.BackgroundProperty, new Binding(nameof(Armed)) { Converter = new Avalonia.Data.Converters.FuncValueConverter<bool, IBrush>(a => a ? Palette.Red : Brushes.Transparent) });
        armedLamp.Bind(Border.BorderBrushProperty, new Binding(nameof(Armed)) { Converter = new Avalonia.Data.Converters.FuncValueConverter<bool, IBrush>(a => a ? Palette.Red : GlassLine) });
        armedLamp.BorderThickness = new Thickness(1);
        armedText.Bind(TextBlock.TextProperty, new Binding(nameof(Armed)) { Converter = new Avalonia.Data.Converters.FuncValueConverter<bool, string>(a => a ? L("ARMED", "ARMED") : L("DISARMED", "DISARMED")) });
        armedText.Bind(TextBlock.ForegroundProperty, new Binding(nameof(Armed)) { Converter = new Avalonia.Data.Converters.FuncValueConverter<bool, IBrush>(a => a ? Brushes.White : GlassLabel) });
        var mode = new TextBlock { FontFamily = mono, FontSize = 13, Foreground = Palette.Amber };
        mode.Bind(TextBlock.TextProperty, new Binding(nameof(Mode)));

        var readouts = Stack(14,
            Row(10, armedLamp, mode),
            Gauge(L("Altitude", "Ketinggian"), nameof(Altitude), "{0:0.0}", "m"),
            Gauge(L("Ground speed", "Kecepatan"), nameof(GroundSpeed), "{0:0.0}", "m/s"),
            Gauge(L("Climb", "Laju naik"), nameof(Climb), "{0:+0.0;-0.0;0.0}", "m/s"),
            Gauge(L("Battery", "Baterai"), nameof(Voltage), "{0:0.00}", "V"));

        var screen = new Border
        {
            Background = Glass, CornerRadius = new CornerRadius(14), BorderBrush = GlassLine, BorderThickness = new Thickness(1), Padding = new Thickness(20, 16),
            Child = Columns("auto,22,170,22,*", horizon, new Border(), readouts, new Border(), map),
        };

        // ---- commands and status --------------------------------------------------------------------------
        var allow = new CheckBox { Content = L("Allow commands (arm, takeoff, land)", "Izinkan perintah (arm, lepas landas, mendarat)") };
        allow.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(AllowCommands)));
        var low = new CheckBox { Content = L("Simulate a low battery (pre-arm check)", "Simulasikan baterai lemah (pre-arm check)") };
        low.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(LowBattery)));
        Button Action(string text, Func<Task> act, string cls = "ghost")
        {
            var b = new Button { Content = text, Classes = { cls } };
            b.Click += async (_, _) => await act();
            b.Bind(InputElement.IsEnabledProperty, new Binding(nameof(IsRunning)));
            return b;
        }
        var buttons = new WrapPanel
        {
            ItemSpacing = 8, LineSpacing = 6,
            Children =
            {
                Action(L("Arm", "Arm"), () => ArmAsync(true), "primary"),
                Action(L("Take off 20 m", "Lepas landas 20 m"), TakeoffAsync),
                Action(L("Return to launch", "Kembali ke home"), ReturnAsync),
                Action(L("Land", "Mendarat"), LandAsync),
                Action(L("Disarm", "Disarm"), () => ArmAsync(false)),
            },
        };
        var texts = new ItemsControl
        {
            ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((t, _) =>
            {
                var line = new TextBlock { Text = t, FontFamily = mono, FontSize = 12, TextWrapping = TextWrapping.Wrap };
                if (t.Contains("PreArm", StringComparison.Ordinal) || t.Contains("denied", StringComparison.OrdinalIgnoreCase)) line.Foreground = Palette.Red;
                return line;
            }),
        };
        texts.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(Texts)));

        var link = Columns("*,*,*",
            Cell(L("Packets", "Paket"), nameof(Packets), "{0:N0}"),
            Cell(L("Lost (seq gaps)", "Hilang (celah urutan)"), nameof(Lost), "{0:N0}"),
            Cell("GPS", nameof(Satellites), "{0} sats"));

        var commandCard = Card(Stack(12, allow, low, buttons, link), L("Ground station (COMMAND_LONG → COMMAND_ACK)", "Ground station (COMMAND_LONG → COMMAND_ACK)"));
        var textCard = Card(texts, L("STATUSTEXT from the vehicle", "STATUSTEXT dari kendaraan"));

        var status = new TextBlock { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };
        status.Bind(TextBlock.TextProperty, new Binding(nameof(Status)));

        return new ScrollViewer
        {
            DataContext = this,
            Content = Stack(16, screen, Columns("1.3*,16,*", commandCard, new Border(), textCard), status),
        };
    }
}

/// <summary>
/// Attitude indicator: sky and ground rotate with roll and shift with pitch (10° per ladder step) inside a round bezel;
/// the aircraft symbol and the roll pointer stay fixed — the instrument every pilot scans first.
/// </summary>
internal sealed class AttitudeIndicator : Control
{
    public static readonly StyledProperty<double> RollProperty = AvaloniaProperty.Register<AttitudeIndicator, double>(nameof(Roll));
    public static readonly StyledProperty<double> PitchProperty = AvaloniaProperty.Register<AttitudeIndicator, double>(nameof(Pitch));
    private static readonly IBrush Sky = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse("#1F5C99"), 0), new GradientStop(Color.Parse("#4E95D0"), 1) },
    };
    private static readonly IBrush Ground = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse("#7A5230"), 0), new GradientStop(Color.Parse("#4A3019"), 1) },
    };
    private static readonly IPen White = new Pen(Brushes.White, 2);
    private static readonly IPen Thin = new Pen(new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), 1.4);
    private static readonly IPen Amber = new Pen(new SolidColorBrush(Color.Parse("#F2A900")), 5, lineCap: PenLineCap.Round);
    private static readonly IPen Bezel = new Pen(new SolidColorBrush(Color.Parse("#2B3036")), 10);
    private static readonly Typeface Face = new("avares://IoTCom.Net.Gallery/Assets/Fonts#JetBrains Mono");

    static AttitudeIndicator() => AffectsRender<AttitudeIndicator>(RollProperty, PitchProperty);

    public double Roll { get => GetValue(RollProperty); set => SetValue(RollProperty, value); }

    public double Pitch { get => GetValue(PitchProperty); set => SetValue(PitchProperty, value); }

    public override void Render(DrawingContext ctx)
    {
        var r = Math.Min(Bounds.Width, Bounds.Height) / 2 - 6;
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var pxPerDeg = r / 45;
        var pitchDeg = Pitch * 180 / Math.PI;
        var circle = new EllipseGeometry(new Rect(c.X - r, c.Y - r, 2 * r, 2 * r));
        using (ctx.PushGeometryClip(circle))
        {
            // Rotate the world opposite to the roll, then slide it with pitch.
            using (ctx.PushTransform(Matrix.CreateTranslation(-c.X, -c.Y) * Matrix.CreateRotation(-Roll) * Matrix.CreateTranslation(c.X, c.Y)))
            {
                var shift = pitchDeg * pxPerDeg;
                ctx.FillRectangle(Sky, new Rect(c.X - 2 * r, c.Y - 3 * r + shift, 4 * r, 3 * r));
                ctx.FillRectangle(Ground, new Rect(c.X - 2 * r, c.Y + shift, 4 * r, 3 * r));
                ctx.DrawLine(White, new Point(c.X - 2 * r, c.Y + shift), new Point(c.X + 2 * r, c.Y + shift));
                for (var deg = -30; deg <= 30; deg += 5)
                {
                    if (deg == 0) continue;
                    var y = c.Y + shift - deg * pxPerDeg;
                    var half = deg % 10 == 0 ? r * 0.28 : r * 0.12;
                    ctx.DrawLine(Thin, new Point(c.X - half, y), new Point(c.X + half, y));
                    if (deg % 10 == 0)
                    {
                        var label = new FormattedText(Math.Abs(deg).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 10, Brushes.White);
                        ctx.DrawText(label, new Point(c.X + half + 4, y - label.Height / 2));
                        ctx.DrawText(label, new Point(c.X - half - 4 - label.Width, y - label.Height / 2));
                    }
                }
            }
        }
        // Roll scale (fixed) and pointer (rotates with the world).
        foreach (var deg in new[] { -60, -45, -30, -20, -10, 0, 10, 20, 30, 45, 60 })
        {
            var a = (deg - 90) * Math.PI / 180;
            var len = deg % 30 == 0 ? 12 : 7;
            ctx.DrawLine(Thin, new Point(c.X + (r - 2) * Math.Cos(a), c.Y + (r - 2) * Math.Sin(a)), new Point(c.X + (r - 2 - len) * Math.Cos(a), c.Y + (r - 2 - len) * Math.Sin(a)));
        }
        var p = -Roll - Math.PI / 2;
        var tip = new Point(c.X + (r - 16) * Math.Cos(p), c.Y + (r - 16) * Math.Sin(p));
        var pointer = new StreamGeometry();
        using (var g = pointer.Open())
        {
            g.BeginFigure(tip, true);
            g.LineTo(new Point(tip.X + 8 * Math.Cos(p + 2.6), tip.Y + 8 * Math.Sin(p + 2.6)));
            g.LineTo(new Point(tip.X + 8 * Math.Cos(p - 2.6), tip.Y + 8 * Math.Sin(p - 2.6)));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(Brushes.White, null, pointer);
        // Fixed aircraft symbol.
        ctx.DrawLine(Amber, new Point(c.X - r * 0.42, c.Y), new Point(c.X - r * 0.14, c.Y));
        ctx.DrawLine(Amber, new Point(c.X - r * 0.14, c.Y), new Point(c.X - r * 0.07, c.Y + 9));
        ctx.DrawLine(Amber, new Point(c.X + r * 0.42, c.Y), new Point(c.X + r * 0.14, c.Y));
        ctx.DrawLine(Amber, new Point(c.X + r * 0.14, c.Y), new Point(c.X + r * 0.07, c.Y + 9));
        ctx.DrawEllipse(new SolidColorBrush(Color.Parse("#F2A900")), null, c, 4, 4);
        ctx.DrawGeometry(null, Bezel, circle);
    }
}

/// <summary>Top-down flight track around home, auto-scaled, with a heading-oriented vehicle marker.</summary>
internal sealed class TrackMap : Control
{
    public static readonly StyledProperty<Point> PositionProperty = AvaloniaProperty.Register<TrackMap, Point>(nameof(Position));
    public static readonly StyledProperty<double> HeadingProperty = AvaloniaProperty.Register<TrackMap, double>(nameof(Heading));
    private static readonly IPen Grid = new Pen(new SolidColorBrush(Color.Parse("#22282E")), 1);
    private static readonly IPen TrackPen = new Pen(new SolidColorBrush(Color.Parse("#4E95D0")), 2, lineJoin: PenLineJoin.Round);
    private static readonly IBrush Label = new SolidColorBrush(Color.Parse("#8C96A0"));
    private static readonly Typeface Face = new("avares://IoTCom.Net.Gallery/Assets/Fonts#JetBrains Mono");
    private IReadOnlyList<Point> _track = [];

    static TrackMap() => AffectsRender<TrackMap>(PositionProperty, HeadingProperty);

    public Point Position { get => GetValue(PositionProperty); set => SetValue(PositionProperty, value); }

    public double Heading { get => GetValue(HeadingProperty); set => SetValue(HeadingProperty, value); }

    public void SetTrack(System.Collections.ObjectModel.ObservableCollection<Point> track)
    {
        _track = track;
        track.CollectionChanged += (_, _) => InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        ctx.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        var extent = 40.0;
        foreach (var p in _track) extent = Math.Max(extent, Math.Max(Math.Abs(p.X), Math.Abs(p.Y)) * 1.15);
        extent = Math.Max(extent, Math.Max(Math.Abs(Position.X), Math.Abs(Position.Y)) * 1.15);
        var scale = Math.Min(w, h) / 2 / extent;
        var c = new Point(w / 2, h / 2);
        Point Px(Point en) => new(c.X + en.X * scale, c.Y - en.Y * scale);

        // 20 m grid.
        var step = extent > 150 ? 50 : 20;
        for (var m = -Math.Ceiling(extent / step) * step; m <= extent; m += step)
        {
            ctx.DrawLine(Grid, Px(new Point(m, -extent * 2)), Px(new Point(m, extent * 2)));
            ctx.DrawLine(Grid, Px(new Point(-extent * 2, m)), Px(new Point(extent * 2, m)));
        }
        if (_track.Count > 1)
        {
            var g = new StreamGeometry();
            using (var s = g.Open())
            {
                s.BeginFigure(Px(_track[0]), false);
                for (var i = 1; i < _track.Count; i++) s.LineTo(Px(_track[i]));
            }
            ctx.DrawGeometry(null, TrackPen, g);
        }
        // Home.
        var home = Px(default);
        ctx.DrawEllipse(null, new Pen(new SolidColorBrush(Color.Parse("#2E9E5B")), 2), home, 9, 9);
        var hText = new FormattedText("H", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 11, new SolidColorBrush(Color.Parse("#2E9E5B")));
        ctx.DrawText(hText, new Point(home.X - hText.Width / 2, home.Y - hText.Height / 2));
        // Vehicle arrow, pointing along the heading (0 = north).
        var v = Px(Position);
        var a = Heading;
        Point Rot(double dx, double dy) => new(v.X + dx * Math.Cos(a) - dy * Math.Sin(a), v.Y + dx * Math.Sin(a) + dy * Math.Cos(a));
        var arrow = new StreamGeometry();
        using (var s = arrow.Open())
        {
            s.BeginFigure(Rot(0, -11), true);
            s.LineTo(Rot(7, 8));
            s.LineTo(Rot(0, 4));
            s.LineTo(Rot(-7, 8));
            s.EndFigure(true);
        }
        ctx.DrawGeometry(new SolidColorBrush(Color.Parse("#F2A900")), new Pen(Brushes.Black, 1), arrow);
        var scaleText = new FormattedText($"N ↑   grid {step} m", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 10, Label);
        ctx.DrawText(scaleText, new Point(8, h - scaleText.Height - 6));
    }
}
