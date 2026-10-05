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

public sealed partial class GreenhouseDemo
{
    private static IBrush KindBrush(string kind) => kind switch
    {
        "CON" => Palette.Amber,
        "NON" => Palette.Blue,
        "ACK" => Palette.Green,
        _ => Palette.Red,
    };

    protected override Control CreateView()
    {
        var display = (FontFamily)Application.Current!.FindResource("DisplayFont")!;
        var mono = (FontFamily)Application.Current!.FindResource("MonoFont")!;

        // ---- the greenhouse: observed sensors -------------------------------------------------------------
        Control Sensor(string label, string path, string unit, string format, IBrush stroke, TrendBuffer trend) =>
            Card(Stack(6, Readout(label, path, unit, format, 40), Sparkline(trend, stroke, 44)));
        var sensors = Columns("*,12,*,12,*",
            Sensor(L("Air temperature", "Suhu udara"), nameof(Temperature), "°C", "{0:0.0}", Palette.Amber, TemperatureTrend), new Border(),
            Sensor(L("Humidity", "Kelembapan"), nameof(Humidity), "%RH", "{0:0}", Palette.Blue, HumidityTrend), new Border(),
            Sensor(L("Soil moisture", "Kelembapan tanah"), nameof(Soil), "%", "{0:0}", Palette.Green, SoilTrend));

        // ---- actuators and exchanges ---------------------------------------------------------------------
        var fan = new ToggleSwitch { OnContent = L("FAN ON", "KIPAS NYALA"), OffContent = L("FAN OFF", "KIPAS MATI"), FontFamily = display, FontWeight = FontWeight.Bold };
        fan.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(FanOn)) { Mode = BindingMode.OneWay });
        fan.IsCheckedChanged += async (_, _) =>
        {
            if (fan.IsChecked != FanOn) await SetFanAsync(fan.IsChecked == true);
        };
        var valve = new Slider { Minimum = 0, Maximum = 100, TickFrequency = 10, IsSnapToTickEnabled = true, MinWidth = 160 };
        valve.Bind(RangeBase.ValueProperty, new Binding(nameof(Valve)) { Mode = BindingMode.OneWay });
        valve.ValueChanged += async (_, e) =>
        {
            if ((int)e.NewValue != Valve) await SetValveAsync((int)e.NewValue);
        };
        var valveText = new TextBlock { Classes = { "value" }, MinWidth = 48, VerticalAlignment = VerticalAlignment.Center };
        valveText.Bind(TextBlock.TextProperty, new Binding(nameof(Valve)) { StringFormat = "{0} %" });
        var log = new Button { Content = L("Fetch event log (Block2)", "Ambil log kejadian (Block2)"), Classes = { "ghost" } };
        log.Click += async (_, _) => await FetchLogAsync();
        var calibrate = new Button { Content = L("Calibrate (separate response)", "Kalibrasi (separate response)"), Classes = { "ghost" } };
        calibrate.Click += async (_, _) => await CalibrateAsync();
        foreach (var c in new Control[] { fan, valve, log, calibrate }) c.Bind(InputElement.IsEnabledProperty, new Binding(nameof(IsRunning)));
        var lastAction = new TextBlock { FontFamily = mono, FontSize = 12, TextWrapping = TextWrapping.Wrap, Classes = { "muted" } };
        lastAction.Bind(TextBlock.TextProperty, new Binding(nameof(LastAction)));

        var resources = new ItemsControl
        {
            ItemsPanel = new FuncTemplate<Panel?>(() => new WrapPanel { ItemSpacing = 6, LineSpacing = 6 }),
            ItemTemplate = new FuncDataTemplate<string>((r, _) => Chip(r)),
        };
        resources.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(Resources)));

        var controls = Card(Stack(12,
            Row(16, fan, Row(8, Eyebrow(L("Valve", "Katup")), valve, valveText)),
            new WrapPanel { ItemSpacing = 8, LineSpacing = 6, Children = { log, calibrate } },
            lastAction,
            Eyebrow(L("/.well-known/core  ·  ◉ observable", "/.well-known/core  ·  ◉ dapat diamati")),
            resources), L("Actuators & resources", "Aktuator & resource"));

        // ---- signature: the message sequence chart with a lossy link ---------------------------------------
        var loss = new Slider { Minimum = 0, Maximum = 30, TickFrequency = 5, IsSnapToTickEnabled = true, MinWidth = 180 };
        loss.Bind(RangeBase.ValueProperty, new Binding(nameof(LossPercent)));
        var lossText = new TextBlock { FontFamily = display, FontWeight = FontWeight.Bold, FontSize = 22, MinWidth = 64, VerticalAlignment = VerticalAlignment.Center };
        lossText.Bind(TextBlock.TextProperty, new Binding(nameof(LossPercent)) { StringFormat = "{0:0} %" });

        var clientLane = new TextBlock { FontFamily = mono, FontSize = 11, Classes = { "muted" } };
        clientLane.Bind(TextBlock.TextProperty, new Binding(nameof(ClientLabel)) { StringFormat = L("Gallery client {0}", "Klien Galeri {0}") });
        var lanes = Columns("*,*",
            Stack(2, new TextBlock { Text = L("CLIENT", "KLIEN"), Classes = { "eyebrow" } }, clientLane),
            new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, Spacing = 2, Children =
            {
                new TextBlock { Text = "GREENHOUSE-NODE-01", Classes = { "eyebrow" }, HorizontalAlignment = HorizontalAlignment.Right },
                new TextBlock { Text = "coap://10.0.0.40:5683", FontFamily = mono, FontSize = 11, Classes = { "muted" }, HorizontalAlignment = HorizontalAlignment.Right },
            } });

        var chart = new ItemsControl
        {
            ItemTemplate = new FuncDataTemplate<WireRow>((w, _) =>
            {
                var colour = w.Delivered ? KindBrush(w.Kind) : Palette.Red;
                var kind = new Border
                {
                    Background = colour, CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 0), VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = w.Kind, FontFamily = mono, FontSize = 10, FontWeight = FontWeight.Bold, Foreground = w.Kind == "CON" && w.Delivered ? Palette.Ink : Brushes.White },
                };
                var label = new TextBlock
                {
                    Text = w.Label, FontFamily = mono, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0),
                    TextDecorations = w.Delivered ? null : TextDecorations.Strikethrough, Opacity = w.Delivered ? 1 : 0.75,
                };
                var head = new Row(4, kind, label) { HorizontalAlignment = w.FromClient ? HorizontalAlignment.Left : HorizontalAlignment.Right };

                // The arrow: a full shaft when delivered; a shaft broken off with ✕ when the network dropped it.
                var shaft = new Border { Height = 2, Background = colour, VerticalAlignment = VerticalAlignment.Center, Opacity = w.Delivered ? 1 : 0.6 };
                var tip = new TextBlock { Text = w.Delivered ? (w.FromClient ? "▶" : "◀") : "✕", Foreground = colour, FontSize = w.Delivered ? 11 : 13, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
                Grid arrow;
                if (w.FromClient)
                    arrow = w.Delivered ? Columns("*,auto", shaft, tip) : Columns("3*,auto,2*", shaft, tip, new Border());
                else
                    arrow = w.Delivered ? Columns("auto,*", tip, shaft) : Columns("2*,auto,3*", new Border(), tip, shaft);

                var time = new TextBlock { Text = w.Time, FontFamily = mono, FontSize = 10, Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center };
                return new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("78,*"), Margin = new Thickness(0, 3),
                    Children = { time, SetColumn(Stack(1, head, arrow), 1) },
                };
            }),
        };
        chart.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(Wire)));

        Control Stat(string label, string path, IBrush? colour = null)
        {
            var v = new TextBlock { FontFamily = display, FontWeight = FontWeight.Bold, FontSize = 22 };
            if (colour is not null) v.Foreground = colour;
            v.Bind(TextBlock.TextProperty, new Binding(path) { StringFormat = "{0:N0}" });
            return Stack(0, Eyebrow(label), v);
        }
        var stats = Columns("*,*,*,*,*",
            Stat(L("Datagrams", "Datagram"), nameof(Sent)),
            Stat(L("Lost", "Hilang"), nameof(Lost), Palette.Red),
            Stat(L("Retransmitted", "Dikirim ulang"), nameof(Retransmissions), Palette.Amber),
            Stat(L("Duplicates dropped", "Duplikat dibuang"), nameof(Duplicates), Palette.Blue),
            Stat(L("Notifications", "Notifikasi"), nameof(Notifications), Palette.Green));

        var wire = Card(Stack(12,
            Row(12, Eyebrow(L("Packet loss on the link", "Kehilangan paket di jaringan")), loss, lossText),
            lanes,
            new Border { Height = 1, Background = Palette.Grey, Opacity = 0.4 },
            chart,
            stats), L("On the wire — message sequence chart", "Di jalur — diagram urutan pesan"));

        var status = new TextBlock { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };
        status.Bind(TextBlock.TextProperty, new Binding(nameof(Status)));

        return new ScrollViewer
        {
            DataContext = this,
            Content = Stack(16, sensors, Columns("*,16,1.35*", controls, new Border(), wire), status),
        };
    }

    private static Control SetColumn(Control c, int column)
    {
        Grid.SetColumn(c, column);
        return c;
    }

    private sealed class Row : StackPanel
    {
        public Row(double spacing, params Control[] children)
        {
            Orientation = Orientation.Horizontal;
            Spacing = spacing;
            foreach (var c in children) Children.Add(c);
        }
    }
}
