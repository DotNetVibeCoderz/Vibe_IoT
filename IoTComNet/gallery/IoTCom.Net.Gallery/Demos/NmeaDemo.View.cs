using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using IoTCom.Net.Gallery.Infrastructure;
using IoTCom.Net.Protocols.Nmea;
using static IoTCom.Net.Gallery.Infrastructure.Loc;
using static IoTCom.Net.Gallery.Infrastructure.UiKit;

namespace IoTCom.Net.Gallery.Demos;

public sealed partial class NmeaDemo
{
    protected override Control CreateView()
    {
        // Track plot: equirectangular projection around the track centre, drawn on a "chart table" grid.
        var line = new Polyline { Stroke = Palette.Blue, StrokeThickness = 3, StrokeJoin = PenLineJoin.Round };
        var dot = new Ellipse { Width = 16, Height = 16, Fill = Palette.Amber, Stroke = Brushes.White, StrokeThickness = 3 };
        var canvas = new Canvas { Height = 300, ClipToBounds = true, Children = { line, dot } };
        void Redraw()
        {
            var w = canvas.Bounds.Width;
            var h = canvas.Bounds.Height;
            if (w <= 0 || Track.Count == 0) return;
            double cLat = -6.9147, cLon = 107.6098, span = 0.0055; // fixed frame around the simulator's 400 m circle
            var kx = Math.Cos(cLat * Math.PI / 180);
            Point P((double Lat, double Lon) t) => new(w / 2 + (t.Lon - cLon) * kx / span * h / 2, h / 2 - (t.Lat - cLat) / span * h / 2);
            line.Points = Track.Select(P).ToList();
            var last = P(Track[^1]);
            Canvas.SetLeft(dot, last.X - 8);
            Canvas.SetTop(dot, last.Y - 8);
        }
        TrackChanged += Redraw;
        canvas.SizeChanged += (_, _) => Redraw();

        var map = new Border
        {
            Classes = { "card" },
            Padding = new Thickness(0),
            ClipToBounds = true,
            Child = new Panel { Children = { new GridLines(), canvas } },
        };

        var sats = new ItemsControl
        {
            ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 }),
            ItemTemplate = new FuncDataTemplate<SatelliteInfo>((s, _) =>
            {
                var snr = s.Snr ?? 0;
                return new StackPanel
                {
                    Spacing = 4,
                    Children =
                    {
                        new Border { Height = 70, Width = 22, VerticalAlignment = VerticalAlignment.Bottom, Child = new Border { Height = Math.Clamp(snr * 1.5, 4, 70), Background = snr >= 35 ? Palette.Green : Palette.Amber, VerticalAlignment = VerticalAlignment.Bottom, CornerRadius = new CornerRadius(3) } },
                        new TextBlock { Text = s.Prn.ToString("00", System.Globalization.CultureInfo.InvariantCulture), Classes = { "mono" }, HorizontalAlignment = HorizontalAlignment.Center },
                    },
                };
            }),
        };
        sats.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(Satellites)));

        var position = new TextBlock { Classes = { "value" } };
        position.Bind(TextBlock.TextProperty, new MultiBinding
        {
            Bindings = { new Binding(nameof(Latitude)), new Binding(nameof(Longitude)) },
            StringFormat = "{0:0.000000}, {1:0.000000}",
        });
        var last = new SelectableTextBlock { Classes = { "mono" }, TextWrapping = TextWrapping.Wrap };
        last.Bind(TextBlock.TextProperty, new Binding(nameof(LastSentence)));

        var fixCard = Card(Stack(12,
            Stack(2, Eyebrow(L("Position (WGS-84)", "Posisi (WGS-84)")), position),
            Columns("*,*", Cell(L("Speed", "Kecepatan"), nameof(Speed), "{0:0.0}", "km/h"), Cell(L("Course", "Haluan"), nameof(Course), "{0:0}", "°")),
            Columns("*,*", Cell(L("Satellites used", "Satelit dipakai"), nameof(SatellitesUsed), "{0}"), Cell("HDOP", nameof(Hdop), "{0:0.0}")),
            Cell("Fix", nameof(FixQuality), "{0}")));

        var top = Columns("1.6*,16,*", map, new Border(), fixCard);
        return new ScrollViewer
        {
            DataContext = this,
            Content = Stack(16, top, Card(Stack(10, Eyebrow(L("Signal strength (SNR dB-Hz)", "Kekuatan sinyal (SNR dB-Hz)")), sats)), Card(Stack(8, Eyebrow(L("Last sentence", "Kalimat terakhir")), last))),
        };
    }

    /// <summary>Faint chart-table grid behind the track.</summary>
    private sealed class GridLines : Control
    {
        public override void Render(DrawingContext context)
        {
            var pen = new Pen(new SolidColorBrush(Color.Parse("#808A9098")), 1);
            for (double x = 0; x < Bounds.Width; x += 40) context.DrawLine(pen, new Point(x, 0), new Point(x, Bounds.Height));
            for (double y = 0; y < Bounds.Height; y += 40) context.DrawLine(pen, new Point(0, y), new Point(Bounds.Width, y));
        }
    }
}
