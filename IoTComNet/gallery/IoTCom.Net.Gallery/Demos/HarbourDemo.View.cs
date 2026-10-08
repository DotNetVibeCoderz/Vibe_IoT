using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using IoTCom.Net.Gallery.Infrastructure;
using static IoTCom.Net.Gallery.Infrastructure.Loc;
using static IoTCom.Net.Gallery.Infrastructure.UiKit;

namespace IoTCom.Net.Gallery.Demos;

public sealed partial class HarbourDemo
{
    /// <summary>Ship-type colours, as on most AIS displays: cargo green, tanker red, passenger blue, service cyan, fishing orange.</summary>
    internal static IBrush TypeBrush(int? type) => type switch
    {
        >= 70 and <= 79 => new SolidColorBrush(Color.Parse("#2E9E5B")),
        >= 80 and <= 89 => new SolidColorBrush(Color.Parse("#D23B2F")),
        >= 60 and <= 69 => new SolidColorBrush(Color.Parse("#2F6FD6")),
        30 => new SolidColorBrush(Color.Parse("#E5711F")),
        >= 50 and <= 59 => new SolidColorBrush(Color.Parse("#1AA3A8")),
        _ => new SolidColorBrush(Color.Parse("#8A9098")),
    };

    protected override Control CreateView()
    {
        var display = (FontFamily)Application.Current!.FindResource("DisplayFont")!;
        var mono = (FontFamily)Application.Current!.FindResource("MonoFont")!;

        var chart = new HarbourChart(this) { Height = 430, ClipToBounds = true };
        var chartFrame = new Border { CornerRadius = new CornerRadius(12), ClipToBounds = true, BorderBrush = new SolidColorBrush(Color.Parse("#9DB3C2")), BorderThickness = new Thickness(1), Child = chart };

        // ---- selected vessel ----------------------------------------------------------------------------------
        TextBlock Bound(string path, double size, FontFamily family, FontWeight weight = FontWeight.Normal, bool muted = false)
        {
            var t = new TextBlock { FontFamily = family, FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap };
            if (muted) t.Classes.Add("muted");
            t.Bind(TextBlock.TextProperty, new Binding($"{nameof(Selected)}.{path}"));
            return t;
        }

        var swatch = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center };
        swatch.Bind(Border.BackgroundProperty, new Binding($"{nameof(Selected)}.{nameof(VesselRow.ShipType)}") { Converter = new Avalonia.Data.Converters.FuncValueConverter<int?, IBrush>(TypeBrush) });
        var selected = Card(Stack(4,
            Row(8, swatch, Bound(nameof(VesselRow.Name), 22, display, FontWeight.Bold)),
            Bound(nameof(VesselRow.Kind), 13, display, FontWeight.SemiBold),
            Bound(nameof(VesselRow.Line), 18, mono, FontWeight.Bold),
            Bound(nameof(VesselRow.Status), 12, mono, muted: true),
            Row(6, new TextBlock { Text = "→", FontSize = 12 }, Bound(nameof(VesselRow.Destination), 12, mono)),
            Row(6, new TextBlock { Text = "MMSI", Classes = { "muted" }, FontSize = 11 }, Bound(nameof(VesselRow.Mmsi), 12, mono), new TextBlock { Text = "·", FontSize = 11 }, Bound(nameof(VesselRow.CallSign), 12, mono))),
            L("Selected vessel", "Kapal terpilih"));

        var list = new ItemsControl
        {
            ItemTemplate = new FuncDataTemplate<VesselRow>((v, _) =>
            {
                var row = new Border
                {
                    Padding = new Thickness(6, 4), CornerRadius = new CornerRadius(4), Cursor = new Cursor(StandardCursorType.Hand), Background = Brushes.Transparent,
                    Child = Columns("14,*,auto",
                        new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(2), Background = TypeBrush(v.ShipType), VerticalAlignment = VerticalAlignment.Center },
                        new TextBlock { Text = v.Name, FontFamily = mono, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis },
                        new TextBlock { Text = v.Line, FontFamily = mono, FontSize = 11, Classes = { "muted" } }),
                };
                row.PointerPressed += (_, _) => SelectedMmsi = v.Mmsi;
                return row;
            }),
        };
        list.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(Vessels)));

        // ---- the radio side: sentences as they arrive -----------------------------------------------------------
        var sentences = new ItemsControl
        {
            ItemTemplate = new FuncDataTemplate<string>((s, _) =>
            {
                var multi = s.Split(',') is { Length: > 2 } p && p[1] != "1";
                return new TextBlock { Text = s, FontFamily = mono, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = multi ? Palette.Amber : null };
            }),
        };
        sentences.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(Sentences)));
        Control Stat(string label, string path)
        {
            var v = new TextBlock { FontFamily = display, FontWeight = FontWeight.Bold, FontSize = 22 };
            v.Bind(TextBlock.TextProperty, new Binding(path) { StringFormat = "{0:N0}" });
            return Stack(0, Eyebrow(label), v);
        }

        var radio = Card(Stack(10,
            Columns("*,*,*", Stat(L("Sentences", "Kalimat"), nameof(SentenceCount)), Stat(L("Messages", "Pesan"), nameof(MessageCount)), Stat(L("Multi-part", "Multi-bagian"), nameof(MultiPart))),
            sentences,
            new TextBlock { Text = L("Amber lines are fragments of a multi-sentence message (type 5).", "Baris amber adalah fragmen pesan multi-kalimat (tipe 5)."), Classes = { "muted" }, FontSize = 11.5 }),
            L("!AIVDM as received", "!AIVDM seperti diterima"));

        var status = new TextBlock { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };
        status.Bind(TextBlock.TextProperty, new Binding(nameof(Status)));

        return new ScrollViewer
        {
            DataContext = this,
            Content = Stack(16,
                Columns("1.7*,16,*", chartFrame, new Border(), Stack(12, selected, Card(list, L("Vessels", "Kapal")))),
                radio,
                status),
        };
    }
}

/// <summary>A small nautical chart of Jakarta Bay: sea, coastline with the Tanjung Priok breakwaters, depth contour, vessels.</summary>
internal sealed class HarbourChart : Control
{
    private const double MinLat = -6.125, MaxLat = -5.87, MinLon = 106.70, MaxLon = 107.02;
    private static readonly IBrush Sea = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse("#B9D3E3"), 0), new GradientStop(Color.Parse("#D3E4EE"), 1) },
    };
    private static readonly IBrush Land = new SolidColorBrush(Color.Parse("#EDE6CF"));
    private static readonly IPen Coast = new Pen(new SolidColorBrush(Color.Parse("#7D7356")), 1.4);
    private static readonly IPen Contour = new Pen(new SolidColorBrush(Color.FromArgb(120, 70, 110, 140)), 1, new DashStyle([4, 4], 0));
    private static readonly IPen Grid = new Pen(new SolidColorBrush(Color.FromArgb(60, 60, 90, 110)), 1);
    private static readonly IBrush Ink = new SolidColorBrush(Color.Parse("#2B3036"));
    private static readonly IBrush Label = new SolidColorBrush(Color.Parse("#4F6372"));
    private static readonly Typeface Face = new("avares://IoTCom.Net.Gallery/Assets/Fonts#JetBrains Mono");
    private static readonly (double Lat, double Lon)[] Shore =
    [
        (-6.098, 106.70), (-6.102, 106.74), (-6.112, 106.78), (-6.108, 106.82), (-6.102, 106.85), (-6.098, 106.872),
        (-6.090, 106.876), (-6.094, 106.884), (-6.099, 106.886), (-6.096, 106.905), (-6.091, 106.93), (-6.085, 106.96), (-6.079, 106.99), (-6.072, 107.02),
    ];
    private readonly HarbourDemo _demo;

    public HarbourChart(HarbourDemo demo)
    {
        _demo = demo;
        demo.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(InvalidateVisual);
    }

    private Point Px(double lat, double lon) => new((lon - MinLon) / (MaxLon - MinLon) * Bounds.Width, (MaxLat - lat) / (MaxLat - MinLat) * Bounds.Height);

    private static FormattedText Text(string s, double size, IBrush brush) => new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, brush);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetPosition(this);
        var hit = _demo.Vessels.Where(v => v.Lat is not null && v.Lon is not null)
            .Select(v => (v, d: Point.Distance(Px(v.Lat!.Value, v.Lon!.Value), p))).Where(x => x.d < 18).OrderBy(x => x.d).FirstOrDefault();
        if (hit.v is not null) _demo.SelectedMmsi = hit.v.Mmsi;
    }

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        ctx.FillRectangle(Sea, new Rect(Bounds.Size));
        for (var lon = 106.75; lon < MaxLon; lon += 0.05) ctx.DrawLine(Grid, Px(MaxLat, lon), Px(MinLat, lon));
        for (var lat = -6.1; lat < MaxLat; lat += 0.05) ctx.DrawLine(Grid, Px(lat, MinLon), Px(lat, MaxLon));

        // 10 m contour, roughly parallel to the shore.
        var contour = new StreamGeometry();
        using (var s = contour.Open())
        {
            s.BeginFigure(Px(Shore[0].Lat + 0.03, Shore[0].Lon), false);
            foreach (var (lat, lon) in Shore.Skip(1)) s.LineTo(Px(lat + 0.03, lon));
        }

        ctx.DrawGeometry(null, Contour, contour);
        var land = new StreamGeometry();
        using (var s = land.Open())
        {
            s.BeginFigure(Px(Shore[0].Lat, Shore[0].Lon), true);
            foreach (var (lat, lon) in Shore.Skip(1)) s.LineTo(Px(lat, lon));
            s.LineTo(new Point(w, h));
            s.LineTo(new Point(0, h));
            s.EndFigure(true);
        }

        ctx.DrawGeometry(Land, Coast, land);
        var priok = Px(-6.103, 106.885);
        var name = Text("TANJUNG PRIOK", 10.5, Label);
        ctx.DrawText(name, new Point(priok.X - name.Width / 2, priok.Y + 6));
        ctx.DrawText(Text("JAKARTA", 12, Label), Px(-6.115, 106.80));
        ctx.DrawText(Text("TELUK JAKARTA · 10 m", 10, Label), new Point(10, 8));

        foreach (var v in _demo.Vessels)
        {
            if (v.Lat is not { } lat || v.Lon is not { } lon) continue;
            var c = Px(lat, lon);
            var brush = HarbourDemo.TypeBrush(v.ShipType);
            var selected = v.Mmsi == _demo.SelectedMmsi;
            var size = Math.Clamp((v.Length ?? 40) / 25.0, 5, 11);
            if (v.Speed is < 0.5)
            {
                // Stationary (at anchor, moored): a ring.
                ctx.DrawEllipse(Brushes.White, new Pen(brush, 2.4), c, size * 0.7, size * 0.7);
            }
            else
            {
                var a = ((v.Heading ?? (int)(v.Course ?? 0)) - 90) * Math.PI / 180;
                Point R(double fwd, double side) => new(c.X + (fwd * Math.Cos(a)) - (side * Math.Sin(a)), c.Y + (fwd * Math.Sin(a)) + (side * Math.Cos(a)));
                var hull = new StreamGeometry();
                using (var s = hull.Open())
                {
                    s.BeginFigure(R(size * 1.6, 0), true);
                    s.LineTo(R(-size, size * 0.6));
                    s.LineTo(R(-size, -size * 0.6));
                    s.EndFigure(true);
                }

                ctx.DrawGeometry(brush, new Pen(Ink, 1), hull);
                // Course vector: where the ship will be in six minutes.
                if (v.Course is { } cog && v.Speed is { } sog)
                {
                    var minutes = 6.0;
                    var dLat = sog * minutes / 60 / 60 * Math.Cos(cog * Math.PI / 180);
                    var dLon = sog * minutes / 60 / 60 * Math.Sin(cog * Math.PI / 180) / Math.Cos(lat * Math.PI / 180);
                    ctx.DrawLine(new Pen(brush, 1.4), c, Px(lat + dLat, lon + dLon));
                }
            }

            if (selected) ctx.DrawEllipse(null, new Pen(Ink, 1.6, new DashStyle([3, 3], 0)), c, size * 2.2, size * 2.2);
            var label = Text(v.Name, selected ? 11 : 9.5, selected ? Ink : Label);
            ctx.DrawText(label, new Point(c.X + size + 6, c.Y - label.Height / 2));
        }
    }
}
