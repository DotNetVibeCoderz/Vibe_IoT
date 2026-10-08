using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using IoTCom.Net.Gallery.Infrastructure;
using IoTCom.Net.Protocols.CanOpen;
using static IoTCom.Net.Gallery.Infrastructure.Loc;
using static IoTCom.Net.Gallery.Infrastructure.UiKit;

namespace IoTCom.Net.Gallery.Demos;

public sealed partial class CanOpenDemo
{
    private static readonly IBrush Housing = new SolidColorBrush(Color.Parse("#E9EAE5"));
    private static readonly IBrush HousingEdge = new SolidColorBrush(Color.Parse("#9AA0A6"));
    private static readonly IBrush LedOff = new SolidColorBrush(Color.Parse("#C9CBC4"));
    private static readonly IBrush Rail = new SolidColorBrush(Color.Parse("#A9AEB3"));

    private static IBrush StateBrush(string state) => state switch
    {
        nameof(NmtState.Operational) => Palette.Green,
        nameof(NmtState.PreOperational) => Palette.Amber,
        nameof(NmtState.Stopped) => Palette.Red,
        _ => Palette.Grey,
    };

    protected override Control CreateView()
    {
        var display = (FontFamily)Application.Current!.FindResource("DisplayFont")!;
        var mono = (FontFamily)Application.Current!.FindResource("MonoFont")!;

        Control LedRow(IoModuleView m, string path, IBrush on, string label)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            for (var i = 0; i < 8; i++)
            {
                var bit = i;
                var led = new Ellipse { Width = 11, Height = 11, Stroke = HousingEdge, StrokeThickness = 0.6 };
                led.Bind(Shape.FillProperty, new Binding(path) { Source = m, Converter = new FuncValueConverter<byte, IBrush>(v => (v & (1 << bit)) != 0 ? on : LedOff) });
                row.Children.Add(led);
            }

            return Stack(2, new TextBlock { Text = label, FontFamily = mono, FontSize = 9.5, Classes = { "muted" } }, row);
        }

        var modules = new ItemsControl
        {
            ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 }),
            ItemTemplate = new FuncDataTemplate<IoModuleView>((m, _) =>
            {
                var stateLamp = new Ellipse { Width = 14, Height = 14 };
                stateLamp.Bind(Shape.FillProperty, new Binding(nameof(IoModuleView.State)) { Source = m, Converter = new FuncValueConverter<string, IBrush>(s => StateBrush(s ?? "")) });
                var state = new TextBlock { FontFamily = mono, FontSize = 11 };
                state.Bind(TextBlock.TextProperty, new Binding(nameof(IoModuleView.State)) { Source = m });
                var name = new TextBlock { FontFamily = display, FontWeight = FontWeight.Bold, FontSize = 16 };
                name.Bind(TextBlock.TextProperty, new Binding(nameof(IoModuleView.Name)) { Source = m });
                var location = new TextBlock { FontSize = 11, Classes = { "muted" }, TextWrapping = TextWrapping.Wrap, MaxWidth = 210 };
                location.Bind(TextBlock.TextProperty, new Binding(nameof(IoModuleView.Location)) { Source = m });
                var pressure = new TextBlock { FontFamily = mono, FontSize = 13, FontWeight = FontWeight.Bold };
                pressure.Bind(TextBlock.TextProperty, new Binding(nameof(IoModuleView.Pressure)) { Source = m, StringFormat = "{0} mbar" });
                var flow = new TextBlock { FontFamily = mono, FontSize = 13, FontWeight = FontWeight.Bold };
                flow.Bind(TextBlock.TextProperty, new Binding(nameof(IoModuleView.Flow)) { Source = m, StringFormat = "{0:0.0} l/min" });
                var flowBar = new ProgressBar { Minimum = 0, Maximum = 60, Height = 6 };
                flowBar.Bind(RangeBase.ValueProperty, new Binding(nameof(IoModuleView.Flow)) { Source = m });

                Button Act(string text, Func<Task> act)
                {
                    var b = new Button { Content = text, Classes = { "ghost" }, FontSize = 11.5, Padding = new Thickness(8, 3) };
                    b.Click += async (_, _) => await act();
                    return b;
                }

                return new Border
                {
                    Width = 250, Background = Housing, BorderBrush = HousingEdge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
                    Child = Stack(0,
                        new Border { Height = 8, Background = Rail },
                        new Border
                        {
                            Padding = new Thickness(14, 10),
                            Child = Stack(8,
                                Row(8, stateLamp, new TextBlock { Text = $"NODE {m.Node}", FontFamily = mono, FontSize = 11, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center }, state),
                                name, location,
                                LedRow(m, nameof(IoModuleView.Inputs), Palette.Green, "DI 1–8  0x6000:01"),
                                LedRow(m, nameof(IoModuleView.Outputs), Palette.Amber, "DO 1–8  0x6200:01"),
                                Stack(2, new TextBlock { Text = "AI 1  0x6401:01", FontFamily = mono, FontSize = 9.5, Classes = { "muted" } }, pressure),
                                Stack(2, new TextBlock { Text = "AI 2  0x6401:02", FontFamily = mono, FontSize = 9.5, Classes = { "muted" } }, flow, flowBar),
                                new WrapPanel
                                {
                                    ItemSpacing = 4, LineSpacing = 4,
                                    Children =
                                    {
                                        Act("Start", () => NmtAsync(m.Node, NmtCommand.Start)),
                                        Act("Pre-op", () => NmtAsync(m.Node, NmtCommand.EnterPreOperational)),
                                        Act("Stop", () => NmtAsync(m.Node, NmtCommand.Stop)),
                                        Act(L("Pump DO1", "Pompa DO1"), () => TogglePumpAsync(m.Node)),
                                    },
                                }),
                        },
                        new Border { Height = 8, Background = Rail }),
                };
            }),
        };
        modules.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(Modules)));

        var allow = new CheckBox { Content = L("Allow writes (NMT and SDO downloads from a second master)", "Izinkan penulisan (NMT dan unduh SDO dari master kedua)") };
        allow.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(AllowWrite)));
        var last = new TextBlock { FontFamily = mono, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Classes = { "muted" } };
        last.Bind(TextBlock.TextProperty, new Binding(nameof(LastAction)));
        var railCard = Card(Stack(12, modules, allow, last), L("DIN rail · CANopen bus 500 kbit/s", "Rel DIN · bus CANopen 500 kbit/s"));

        var address = new TextBox { FontFamily = mono, Width = 110 };
        address.Bind(TextBox.TextProperty, new Binding(nameof(SdoAddress)) { Mode = BindingMode.TwoWay });
        var read5 = new Button { Content = L("Read node 5", "Baca node 5"), Classes = { "primary" } };
        read5.Click += async (_, _) => await ReadSdoAsync(5);
        var read6 = new Button { Content = L("Read node 6", "Baca node 6"), Classes = { "ghost" } };
        read6.Click += async (_, _) => await ReadSdoAsync(6);
        var result = new TextBlock { FontFamily = mono, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        result.Bind(TextBlock.TextProperty, new Binding(nameof(SdoResult)));
        var sdoCard = Card(Stack(8,
            Row(8, address, read5, read6),
            new TextBlock { Text = L("Try 1008, 1018:01, 2100 (segmented), 6401:01, 1A00:01, 9999 (abort).", "Coba 1008, 1018:01, 2100 (bersegmen), 6401:01, 1A00:01, 9999 (abort)."), FontSize = 11.5, Classes = { "muted" }, TextWrapping = TextWrapping.Wrap },
            result), L("SDO · object dictionary", "SDO · object dictionary"));

        var log = new ItemsControl { ItemTemplate = new FuncDataTemplate<string>((l, _) => new TextBlock { Text = l, FontFamily = mono, FontSize = 11 }) };
        log.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(BusLog)));
        var logCard = Card(log, L("Heartbeats, PDOs and emergencies", "Heartbeat, PDO, dan emergency"));

        var status = new TextBlock { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };
        status.Bind(TextBlock.TextProperty, new Binding(nameof(Status)));
        return new ScrollViewer
        {
            DataContext = this,
            Content = Stack(16, Columns("auto,16,*", railCard, new Border(), Stack(16, sdoCard, logCard)), status),
        };
    }
}
