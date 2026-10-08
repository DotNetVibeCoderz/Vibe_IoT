using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using IoTCom.Net.Gallery.Infrastructure;
using static IoTCom.Net.Gallery.Infrastructure.Loc;
using static IoTCom.Net.Gallery.Infrastructure.UiKit;

namespace IoTCom.Net.Gallery.Demos;

public sealed partial class UsbDemo
{
    private static readonly IBrush BoardGreen = new SolidColorBrush(Color.Parse("#1F5F3A"));
    private static readonly IBrush Copper = new SolidColorBrush(Color.Parse("#C98B3B"));
    private static readonly IBrush LampOff = new SolidColorBrush(Color.Parse("#3A4A3F"));
    private static readonly IBrush LampOn = new SolidColorBrush(Color.Parse("#D23B2F"));

    protected override Control CreateView()
    {
        var display = (FontFamily)Application.Current!.FindResource("DisplayFont")!;
        var mono = (FontFamily)Application.Current!.FindResource("MonoFont")!;

        // ---- this computer -------------------------------------------------------------------------------------
        var local = new ItemsControl
        {
            ItemTemplate = new FuncDataTemplate<UsbRow>((d, _) => Stack(0,
                new TextBlock { Text = d.Name, FontFamily = display, FontWeight = FontWeight.SemiBold, FontSize = 14 },
                new TextBlock { Text = $"{d.Id} · {d.Kind}", FontFamily = mono, FontSize = 10.5, Classes = { "muted" }, TextTrimming = TextTrimming.CharacterEllipsis })
                .With(s => s.Margin = new Thickness(0, 0, 0, 6))),
        };
        local.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(Local)));
        var note = new TextBlock { FontFamily = mono, FontSize = 11, Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };
        note.Bind(TextBlock.TextProperty, new Binding(nameof(LocalNote)));
        var localCard = Card(Stack(8, note, local), L("This computer · enumeration", "Komputer ini · enumerasi"));

        // ---- the relay board: a green PCB with four relays and their red LEDs --------------------------------------
        var relays = new ItemsControl
        {
            ItemsPanel = new FuncTemplate<Panel?>(() => new UniformGrid { Columns = 4 }),
            ItemTemplate = new FuncDataTemplate<RelayLamp>((r, _) =>
            {
                var b = new Button
                {
                    Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = new Cursor(StandardCursorType.Hand), HorizontalAlignment = HorizontalAlignment.Center,
                    Content = Stack(6,
                        new Border { Width = 54, Height = 40, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.Parse("#2E5FA8")),
                            Child = new TextBlock { Text = "SRD-05VDC", FontFamily = mono, FontSize = 7.5, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } },
                        Row(5, new Ellipse { Width = 10, Height = 10, Fill = r.On ? LampOn : LampOff }, new TextBlock { Text = $"K{r.Number}", FontFamily = mono, FontSize = 11, Foreground = Brushes.White })),
                };
                b.Click += async (_, _) => await ToggleRelayAsync(r.Number);
                return b;
            }),
        };
        relays.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(Relays)));
        var pcb = new Border
        {
            Background = BoardGreen, CornerRadius = new CornerRadius(6), Padding = new Thickness(16, 12), BorderBrush = Copper, BorderThickness = new Thickness(2),
            Child = Stack(10,
                Row(8, new TextBlock { Text = "USBRelay4", FontFamily = display, FontWeight = FontWeight.Bold, FontSize = 18, Foreground = Brushes.White },
                    new TextBlock { Text = "16c0:05df · HID · feature report 0", FontFamily = mono, FontSize = 10.5, Foreground = Copper, VerticalAlignment = VerticalAlignment.Center }),
                relays),
        };
        var allow = new CheckBox { Content = L("Allow writes (opens a second, writable HID handle)", "Izinkan penulisan (membuka handle HID kedua yang bisa menulis)") };
        allow.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(AllowWrite)));
        var last = new TextBlock { FontFamily = mono, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Classes = { "muted" } };
        last.Bind(TextBlock.TextProperty, new Binding(nameof(LastAction)));
        var relayCard = Card(Stack(10, pcb, allow, last), L("USB HID relay board · click a relay", "Papan relay USB HID · klik sebuah relay"));

        // ---- loopback ------------------------------------------------------------------------------------------
        var input = new TextBox { FontFamily = mono };
        input.Bind(TextBox.TextProperty, new Binding(nameof(Message)) { Mode = BindingMode.TwoWay });
        var upper = new CheckBox { Content = L("Upper-case mode (vendor request 0x02)", "Mode huruf besar (vendor request 0x02)") };
        upper.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(Upper)));
        var send = new Button { Content = L("Send over bulk", "Kirim lewat bulk"), Classes = { "primary" } };
        send.Click += async (_, _) => await SendAsync();
        send.Bind(InputElement.IsEnabledProperty, new Binding(nameof(IsRunning)));
        var console = new ItemsControl { ItemTemplate = new FuncDataTemplate<string>((l, _) => new TextBlock { Text = l, FontFamily = mono, FontSize = 11.5, TextWrapping = TextWrapping.Wrap }) };
        console.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(Console)));
        var loopCard = Card(Stack(8, Columns("*,10,auto", input, new Border(), send), upper, console), L("Loopback device 1209:0001 · control + bulk", "Perangkat loopback 1209:0001 · control + bulk"));

        var status = new TextBlock { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };
        status.Bind(TextBlock.TextProperty, new Binding(nameof(Status)));
        return new ScrollViewer
        {
            DataContext = this,
            Content = Stack(16, Columns("0.9*,16,1.3*", localCard, new Border(), Stack(16, relayCard, loopCard)), status),
        };
    }
}
