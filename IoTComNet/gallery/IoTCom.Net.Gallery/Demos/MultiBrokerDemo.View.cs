using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using IoTCom.Net.Gallery.Infrastructure;
using static IoTCom.Net.Gallery.Infrastructure.Loc;
using static IoTCom.Net.Gallery.Infrastructure.UiKit;

namespace IoTCom.Net.Gallery.Demos;

public sealed partial class MultiBrokerDemo
{
    protected override Control CreateView()
    {
        var mono = (FontFamily)Application.Current!.FindResource("MonoFont")!;
        var lanes = new Border { CornerRadius = new CornerRadius(14), ClipToBounds = true, Child = new BrokerLanes(this) { Height = 220 } };

        Control LaneCard(BrokerLane lane, IBrush color)
        {
            TextBlock Num(string path, string format) { var t = new TextBlock { FontFamily = mono, FontSize = 12 }; t.Bind(TextBlock.TextProperty, new Binding(path) { Source = lane, StringFormat = format }); return t; }
            return Card(Stack(6,
                new TextBlock { Text = lane.Address, FontSize = 11, Classes = { "muted" } },
                Num(nameof(BrokerLane.P50), "p50  {0:0.00} ms"),
                Num(nameof(BrokerLane.P95), "p95  {0:0.00} ms"),
                Num(nameof(BrokerLane.Max), "max  {0:0.0} ms"),
                Num(nameof(BrokerLane.Received), "recv {0}"),
                Num(nameof(BrokerLane.Lost), "gaps {0}"),
                Sparkline(lane.Trend, color, 46)), lane.Name);
        }

        var rate = new Slider { Minimum = 10, Maximum = 500, TickFrequency = 10, IsSnapToTickEnabled = true };
        rate.Bind(RangeBase.ValueProperty, new Binding(nameof(Rate)) { Mode = BindingMode.TwoWay });
        var rateText = new TextBlock { FontFamily = mono, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, MinWidth = 80 };
        rateText.Bind(TextBlock.TextProperty, new Binding(nameof(Rate)) { StringFormat = "{0} msg/s" });
        var bridge = new ToggleSwitch { OnContent = L("Bridge: MQTT → AMQP + Zenoh", "Jembatan: MQTT → AMQP + Zenoh"), OffContent = L("Direct: publish to all three", "Langsung: publikasi ke ketiganya") };
        bridge.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(BridgeMode)) { Mode = BindingMode.TwoWay });
        var published = new TextBlock { FontFamily = mono, FontSize = 12 };
        published.Bind(TextBlock.TextProperty, new Binding(nameof(Published)) { StringFormat = L("{0} published by truck42", "{0} dipublikasikan truck42") });
        var controls = Card(Stack(10, Columns("60,*,8,auto", new TextBlock { Text = L("Rate", "Laju"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center }, rate, new Border(), rateText), bridge, published), L("Producer", "Produsen"));

        var log = new ItemsControl { ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((t, _) => new TextBlock { Text = t, FontFamily = mono, FontSize = 10.5, TextWrapping = TextWrapping.Wrap }) };
        log.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(Log)));
        var logCard = Card(new ScrollViewer { MaxHeight = 160, Content = log }, L("Log", "Log"));

        var status = new TextBlock { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };
        status.Bind(TextBlock.TextProperty, new Binding(nameof(Status)));
        return new ScrollViewer
        {
            DataContext = this,
            Content = Stack(16,
                Columns("1.6*,16,*", Stack(16, lanes, Columns("*,12,*,12,*", LaneCard(Mqtt, BrokerLanes.Colors[0]), new Border(), LaneCard(Amqp, BrokerLanes.Colors[1]), new Border(), LaneCard(Zenoh, BrokerLanes.Colors[2]))), new Border(), Stack(16, controls, logCard)),
                status),
        };
    }
}

/// <summary>Three lanes from the truck to the dashboard; every delivered message is a dot that crosses its lane.</summary>
internal sealed class BrokerLanes : Control
{
    public static readonly IBrush[] Colors = [new SolidColorBrush(Color.Parse("#2E9E5B")), new SolidColorBrush(Color.Parse("#F2A900")), new SolidColorBrush(Color.Parse("#2F6FD6"))];
    private static readonly IBrush Bg = new SolidColorBrush(Color.Parse("#2B3036"));
    private static readonly IBrush Track = new SolidColorBrush(Color.Parse("#3A4048"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#8A9098"));
    private static readonly IBrush Text = new SolidColorBrush(Color.Parse("#E4E5E0"));
    private readonly MultiBrokerDemo _demo;

    public BrokerLanes(MultiBrokerDemo demo)
    {
        _demo = demo;
        var t = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        t.Tick += (_, _) => InvalidateVisual();
        t.Start();
    }

    private static FormattedText T(string s, double size, IBrush brush, string face = "Barlow Condensed", FontWeight weight = FontWeight.SemiBold) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(face, FontStyle.Normal, weight), size, brush);

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        ctx.DrawRectangle(Bg, null, new Rect(0, 0, w, h));
        var left = 96.0;
        var right = w - 110;
        ctx.DrawText(T("TRUCK42", 12, Text), new Point(14, h / 2 - 8));
        ctx.DrawText(T("DASHBOARD", 12, Text), new Point(right + 18, 12));
        var lanes = _demo.Lanes;
        var now = Stopwatch.GetTimestamp();
        for (var i = 0; i < lanes.Count; i++)
        {
            var y = 40 + (i * ((h - 70) / (lanes.Count - 1)));
            var lane = lanes[i];
            var bridged = _demo.BridgeMode && i > 0;
            ctx.DrawRectangle(Track, null, new Rect(left, y - 3, right - left, 6), 3, 3);
            if (bridged) ctx.DrawLine(new Pen(Muted, 1.5, new DashStyle([4, 4], 0)), new Point(left + ((right - left) * 0.5), 40), new Point(left + ((right - left) * 0.5), y));
            ctx.DrawText(T(lane.Name.ToUpperInvariant() + (bridged ? "  (VIA BRIDGE)" : ""), 11, Muted), new Point(left, y - 22));
            ctx.DrawText(T($"{lane.P50:0.00} ms", 12, Colors[i], "JetBrains Mono"), new Point(right + 18, y - 8));
            foreach (var (ticks, _) in lane.Recent)
            {
                var age = Stopwatch.GetElapsedTime(ticks, now).TotalMilliseconds;
                if (age > 700) continue;
                var x = left + ((right - left) * Math.Min(1, age / 700));
                ctx.DrawEllipse(Colors[i], null, new Point(x, y), 4, 4);
            }
        }

        ctx.DrawText(T("EACH DOT IS ONE DELIVERED MESSAGE · LATENCY IS SEND → RECEIVE IN THIS PROCESS", 10, Muted), new Point(14, h - 18));
    }
}
