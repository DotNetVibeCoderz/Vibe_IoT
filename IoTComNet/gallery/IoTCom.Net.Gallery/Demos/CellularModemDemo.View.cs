using System.Globalization;
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

public sealed partial class CellularModemDemo
{
    protected override Control CreateView()
    {
        var mono = (FontFamily)Application.Current!.FindResource("MonoFont")!;

        Button Btn(string text, string cls, Action action)
        {
            var b = new Button { Content = new TextBlock { Text = text }, Classes = { cls } };
            b.Click += (_, _) => action();
            b.Bind(InputElement.IsEnabledProperty, new Binding(nameof(IsRunning)));
            return b;
        }

        Button BtnAsync(string text, string cls, Func<Task> action)
        {
            var b = new Button { Content = new TextBlock { Text = text }, Classes = { cls } };
            b.Click += async (_, _) => await action();
            b.Bind(InputElement.IsEnabledProperty, new Binding(nameof(IsRunning)));
            return b;
        }

        var status = new Border { CornerRadius = new CornerRadius(14), ClipToBounds = true, Child = new ModuleStatus(this) { Height = 150 } };

        var terminal = new ItemsControl { ItemTemplate = new FuncDataTemplate<string>((t, _) => new TextBlock
        {
            Text = t, FontFamily = mono, FontSize = 11.5,
            Foreground = t.StartsWith('>') ? Palette.Amber : t.Contains("ERROR", StringComparison.Ordinal) || t.Contains('!', StringComparison.Ordinal) ? Palette.Red : new SolidColorBrush(Color.Parse("#E4E5E0")),
        }) };
        terminal.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(Terminal)));
        var scroller = new ScrollViewer { Height = 230, Content = terminal };
        Terminal.CollectionChanged += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => scroller.ScrollToEnd());
        var input = new TextBox { FontFamily = mono, FontSize = 12, Watermark = "AT+CSQ" };
        input.Bind(TextBox.TextProperty, new Binding(nameof(Command)) { Mode = BindingMode.TwoWay });
        input.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter) await RunTypedAsync();
        };
        var presets = new WrapPanel();
        foreach (var c in new[] { "ATI", "AT+CSQ", "AT+CEREG?", "AT+COPS?", "AT+CGSN", "AT+CMGL=\"ALL\"", "AT+CFUN?" })
        {
            var b = new Button { Content = new TextBlock { Text = c, FontFamily = mono, FontSize = 11 }, Classes = { "ghost" }, Margin = new Thickness(0, 0, 6, 6) };
            b.Click += async (_, _) => await RunAsync(c);
            b.Bind(InputElement.IsEnabledProperty, new Binding(nameof(IsRunning)));
            presets.Children.Add(b);
        }

        var terminalCard = Card(Stack(8,
            new Border { Background = new SolidColorBrush(Color.Parse("#1E2227")), CornerRadius = new CornerRadius(8), Padding = new Thickness(10), Child = scroller },
            Columns("*,8,auto", input, new Border(), BtnAsync(L("Send", "Kirim"), "primary", RunTypedAsync)),
            presets), L("AT terminal (UART1, 115200 8N1)", "Terminal AT (UART1, 115200 8N1)"));

        var urcs = new ItemsControl { ItemTemplate = new FuncDataTemplate<string>((t, _) => new TextBlock { Text = t, FontFamily = mono, FontSize = 10.5, TextWrapping = TextWrapping.Wrap }) };
        urcs.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(Urcs)));
        var urcCard = Card(new ScrollViewer { MaxHeight = 150, Content = urcs }, L("Unsolicited result codes", "Unsolicited result code"));

        var unlock = new ToggleSwitch { OnContent = L("Sending SMS allowed", "Kirim SMS diizinkan"), OffContent = L("Write lock closed", "Kunci tulis tertutup") };
        unlock.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(WritesAllowed)) { Mode = BindingMode.TwoWay });
        var reply = new TextBox { FontSize = 12 };
        reply.Bind(TextBox.TextProperty, new Binding(nameof(Reply)) { Mode = BindingMode.TwoWay });
        var roadCard = Card(Stack(10,
            Row(8, Btn(L("Enter tunnel", "Masuk terowongan"), "ghost", EnterTunnel), Btn(L("Leave tunnel", "Keluar terowongan"), "ghost", LeaveTunnel)),
            Row(8, Btn(L("Cross the border", "Lintas batas"), "ghost", CrossBorder), Btn(L("Dispatcher SMS", "SMS dispatcher"), "ghost", ReceiveSms)),
            unlock, reply, BtnAsync(L("Reply by SMS", "Balas lewat SMS"), "primary", SendReplyAsync)), L("On the road", "Di jalan"));

        var log = new ItemsControl { ItemTemplate = new FuncDataTemplate<string>((t, _) => new TextBlock { Text = t, FontFamily = mono, FontSize = 10.5, TextWrapping = TextWrapping.Wrap }) };
        log.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(Log)));
        var logCard = Card(new ScrollViewer { MaxHeight = 150, Content = log }, L("Telematics log", "Log telematika"));

        var statusLine = new TextBlock { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };
        statusLine.Bind(TextBlock.TextProperty, new Binding(nameof(Status)));
        return new ScrollViewer
        {
            DataContext = this,
            Content = Stack(16, Columns("1.5*,16,*", Stack(16, status, terminalCard), new Border(), Stack(16, roadCard, urcCard, logCard)), statusLine),
        };
    }
}

/// <summary>The module's status as a dashboard strip: signal bars, registration lamp, road position, identity.</summary>
internal sealed class ModuleStatus : Control
{
    private static readonly IBrush Bg = new SolidColorBrush(Color.Parse("#2B3036"));
    private static readonly IBrush Off = new SolidColorBrush(Color.Parse("#4A5058"));
    private static readonly IBrush Text = new SolidColorBrush(Color.Parse("#E4E5E0"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#8A9098"));
    private readonly CellularModemDemo _demo;

    public ModuleStatus(CellularModemDemo demo)
    {
        _demo = demo;
        demo.PropertyChanged += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(InvalidateVisual);
    }

    private static FormattedText T(string s, double size, IBrush brush, string face = "JetBrains Mono", FontWeight weight = FontWeight.Normal) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(face, FontStyle.Normal, weight), size, brush);

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        ctx.DrawRectangle(Bg, null, new Rect(0, 0, w, h));
        if (!_demo.IsRunning)
        {
            ctx.DrawText(T("Start the demo to power the module.", 13, Muted, "Barlow"), new Point(18, h / 2 - 8));
            return;
        }

        // Signal bars.
        var x0 = 22.0;
        for (var i = 0; i < 4; i++)
        {
            var bh = 14 + (i * 12);
            ctx.DrawRectangle(i < _demo.Bars ? (_demo.Bars <= 1 ? Palette.Red : Palette.Green) : Off, null, new Rect(x0 + (i * 14), 78 - bh, 10, bh), 2, 2);
        }

        ctx.DrawText(T(_demo.Signal, 11, Muted), new Point(x0, 86));

        // Registration lamp.
        var reg = _demo.Registration;
        var lamp = reg switch { "Home" => Palette.Green, "Roaming" => Palette.Amber, "Searching" => Palette.Amber, "" => Off, _ => Palette.Red };
        ctx.DrawEllipse(lamp, null, new Point(118, 36), 8, 8);
        ctx.DrawText(T(reg switch { "Home" => "REGISTERED · HOME", "Roaming" => "REGISTERED · ROAMING", "Searching" => "SEARCHING", "" => "…", _ => reg.ToUpperInvariant() }, 15, Text, "Barlow Condensed", FontWeight.SemiBold), new Point(132, 26));
        ctx.DrawText(T("LTE-M · " + _demo.Place, 12, Muted, "Barlow"), new Point(132, 50));
        var id = T(_demo.Identity, 10, Muted);
        id.MaxTextWidth = w - 40;
        ctx.DrawText(id, new Point(22, h - 40));
    }
}
