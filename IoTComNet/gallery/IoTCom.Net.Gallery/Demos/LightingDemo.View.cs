using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using IoTCom.Net.Gallery.Infrastructure;
using static IoTCom.Net.Gallery.Infrastructure.Loc;
using static IoTCom.Net.Gallery.Infrastructure.UiKit;

namespace IoTCom.Net.Gallery.Demos;

public sealed partial class LightingDemo
{
    private static readonly string[] Channels = ["R", "G", "B"];
    private static readonly IBrush[] ChannelBrushes = [new SolidColorBrush(Color.Parse("#D23B2F")), new SolidColorBrush(Color.Parse("#2E9E5B")), new SolidColorBrush(Color.Parse("#2F6FD6"))];

    protected override Control CreateView()
    {
        // Fixtures as seen by the RECEIVER (what came off the wire), glowing on a dark stage.
        var lamps = new Ellipse[Fixtures];
        var stage = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 36, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 22) };
        for (var f = 0; f < Fixtures; f++)
        {
            lamps[f] = new Ellipse { Width = 92, Height = 92, Fill = Brushes.Black, Stroke = new SolidColorBrush(Color.Parse("#4A5058")), StrokeThickness = 4 };
            stage.Children.Add(Stack(8, lamps[f], new TextBlock { Text = $"Fixture {f + 1} · ch {f * 3 + 1}–{f * 3 + 3}", Foreground = new SolidColorBrush(Color.Parse("#9AA1A8")), HorizontalAlignment = HorizontalAlignment.Center, FontSize = 12 }));
        }
        void Paint()
        {
            for (var f = 0; f < Fixtures; f++)
            {
                var c = Color.FromRgb(Received[f * 3], Received[f * 3 + 1], Received[f * 3 + 2]);
                lamps[f].Fill = new RadialGradientBrush
                {
                    GradientStops = { new GradientStop(Color.FromArgb(255, (byte)Math.Min(255, c.R + 60), (byte)Math.Min(255, c.G + 60), (byte)Math.Min(255, c.B + 60)), 0), new GradientStop(c, 0.55), new GradientStop(Color.FromRgb((byte)(c.R / 3), (byte)(c.G / 3), (byte)(c.B / 3)), 1) },
                };
            }
        }
        var pending = 0;
        ReceivedChanged += () =>
        {
            if (Interlocked.Exchange(ref pending, 1) == 1) return;
            Dispatcher.UIThread.Post(() => { pending = 0; Paint(); Packets = ReceivedPackets; }, DispatcherPriority.Render);
        };
        var stageCard = new Border { CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Color.Parse("#15181B")), Child = stage };

        // Console: a fader bank per fixture.
        var bank = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 22 };
        for (var f = 0; f < Fixtures; f++)
        {
            var faders = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            for (var c = 0; c < 3; c++)
            {
                var (fi, ci) = (f, c);
                var slider = new Slider { Orientation = Orientation.Vertical, Minimum = 0, Maximum = 255, Value = Levels[f * 3 + c], Height = 150, Foreground = ChannelBrushes[c] };
                slider.ValueChanged += (_, e) => SetLevel(fi, ci, (byte)e.NewValue);
                faders.Children.Add(Stack(4, slider, new TextBlock { Text = Channels[c], HorizontalAlignment = HorizontalAlignment.Center, Foreground = ChannelBrushes[c], FontWeight = FontWeight.Bold }));
            }
            bank.Children.Add(Stack(6, Eyebrow($"Fixture {f + 1}"), faders));
        }
        var master = new Slider { Orientation = Orientation.Vertical, Minimum = 0, Maximum = 1, Height = 150 };
        master.Bind(RangeBase.ValueProperty, new Binding(nameof(Master)));
        bank.Children.Add(Stack(6, Eyebrow("Master"), master));

        var chase = new ToggleSwitch { OnContent = "Chase", OffContent = "Chase" };
        chase.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(Chase)));
        var packets = new TextBlock { Classes = { "muted" } };
        packets.Bind(TextBlock.TextProperty, new Binding(nameof(Packets)) { StringFormat = L("ArtDmx packets received: {0:N0}", "Paket ArtDmx diterima: {0:N0}") });
        var status = new TextBlock { Classes = { "muted" } };
        status.Bind(TextBlock.TextProperty, new Binding(nameof(Status)));

        return new ScrollViewer
        {
            DataContext = this,
            Content = Stack(16, stageCard, Card(Stack(14, Row(24, Eyebrow(L("Console · universe 0", "Konsol · universe 0")), chase), new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = bank })), packets, status),
        };
    }
}
