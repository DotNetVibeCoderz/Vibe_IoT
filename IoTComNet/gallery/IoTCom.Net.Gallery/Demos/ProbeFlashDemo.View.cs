using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using IoTCom.Net.Debug;
using IoTCom.Net.Gallery.Infrastructure;
using static IoTCom.Net.Gallery.Infrastructure.Loc;
using static IoTCom.Net.Gallery.Infrastructure.UiKit;

namespace IoTCom.Net.Gallery.Demos;

public sealed partial class ProbeFlashDemo
{
    protected override Control CreateView()
    {
        var mono = (FontFamily)Application.Current!.FindResource("MonoFont")!;
        var bench = new Border { CornerRadius = new CornerRadius(14), ClipToBounds = true, Child = new BenchView(this) { Height = 300 } };

        Button Btn(string text, string cls, Func<Task> action)
        {
            var b = new Button { Content = new TextBlock { Text = text }, Classes = { cls } };
            b.Click += async (_, _) => await action();
            b.Bind(InputElement.IsEnabledProperty, new Binding(nameof(IsRunning)));
            return b;
        }

        ToggleSwitch Switch(string on, string off, string path)
        {
            var t = new ToggleSwitch { OnContent = on, OffContent = off };
            t.Bind(ToggleButton.IsCheckedProperty, new Binding(path) { Mode = BindingMode.TwoWay });
            t.Bind(InputElement.IsEnabledProperty, new Binding(nameof(IsRunning)));
            return t;
        }

        var versions = new ComboBox { ItemsSource = Versions.Select(v => $"firmware {v}").ToList(), MinWidth = 150 };
        versions.Bind(SelectingItemsControl.SelectedIndexProperty, new Binding(nameof(VersionIndex)) { Mode = BindingMode.TwoWay });
        var phase = new TextBlock { FontFamily = mono, FontSize = 11 };
        phase.Bind(TextBlock.TextProperty, new Binding(nameof(Phase)));
        var bar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 6 };
        bar.Bind(RangeBase.ValueProperty, new Binding(nameof(Progress)));
        var core = new TextBlock { FontFamily = mono, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        core.Bind(TextBlock.TextProperty, new Binding(nameof(Core)));
        var fw = new TextBlock { FontFamily = mono, FontSize = 12, Classes = { "muted" } };
        fw.Bind(TextBlock.TextProperty, new Binding(nameof(Firmware)));

        var boardCard = Card(Stack(10,
            core, fw,
            Switch(L("Writes allowed", "Penulisan diizinkan"), L("Write lock closed (read-only)", "Kunci tulis tertutup (hanya-baca)"), nameof(WritesAllowed)),
            Switch(L("BOOT0 set: USB DFU bootloader", "BOOT0 diset: bootloader USB DFU"), L("BOOT0 clear: run from flash (SWD)", "BOOT0 bersih: jalan dari flash (SWD)"), nameof(DfuMode)),
            Row(8, versions, Btn(L("Flash", "Flash"), "primary", FlashAsync)),
            phase, bar,
            Row(8, Btn("Halt", "ghost", () => ControlAsync("halt")), Btn("Run", "ghost", () => ControlAsync("run")), Btn("Reset", "ghost", () => ControlAsync("reset"))),
            Row(8, Btn(L("Erase all", "Hapus semua"), "ghost", EraseAsync), Btn(L("Leave DFU", "Keluar DFU"), "ghost", LeaveDfuAsync))),
            L("Board", "Board"));

        var addr = new TextBox { FontFamily = mono, Width = 130 };
        addr.Bind(TextBox.TextProperty, new Binding(nameof(MemoryAddress)) { Mode = BindingMode.TwoWay });
        var memory = new TextBlock { FontFamily = mono, FontSize = 11 };
        memory.Bind(TextBlock.TextProperty, new Binding(nameof(Memory)));
        var memoryCard = Card(Stack(8, Row(8, addr, Btn(L("Read 64 bytes", "Baca 64 byte"), "ghost", ReadMemoryAsync)), memory),
            L("Memory (read while running)", "Memori (dibaca saat berjalan)"));

        var terminal = new ItemsControl { ItemTemplate = new FuncDataTemplate<string>((t, _) => new TextBlock { Text = t, FontFamily = mono, FontSize = 11, Foreground = Palette.Data }) };
        terminal.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(Terminal)));
        var scroll = new ScrollViewer { Height = 150, Content = terminal };
        Terminal.CollectionChanged += (_, _) => scroll.ScrollToEnd();
        var cmd = new TextBox { FontFamily = mono, Width = 160 };
        cmd.Bind(TextBox.TextProperty, new Binding(nameof(Command)) { Mode = BindingMode.TwoWay });
        var terminalCard = Card(Stack(8,
            new Border { Background = Palette.Ink, CornerRadius = new CornerRadius(8), Padding = new Thickness(10), Child = scroll },
            Row(8, cmd, Btn(L("Send on down channel", "Kirim di kanal down"), "ghost", SendAsync))),
            L("RTT terminal (up 0 'Terminal')", "Terminal RTT (up 0 'Terminal')"));

        var log = new ItemsControl { ItemTemplate = new FuncDataTemplate<string>((t, _) => new TextBlock { Text = t, FontFamily = mono, FontSize = 10.5, TextWrapping = TextWrapping.Wrap }) };
        log.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(Log)));
        var logCard = Card(new ScrollViewer { MaxHeight = 150, Content = log }, L("Probe log", "Log probe"));

        var status = new TextBlock { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };
        status.Bind(TextBlock.TextProperty, new Binding(nameof(Status)));
        return new ScrollViewer
        {
            DataContext = this,
            Content = Stack(16, Columns("1.6*,16,*", Stack(16, bench, terminalCard, memoryCard), new Border(), Stack(16, boardCard, logCard)), status),
        };
    }
}

/// <summary>
/// The bench: a dev board whose MCU carries a flash map — one cell per 2 KiB page, coloured by the firmware version in it,
/// grey when erased — the user LED the firmware drives, and the cable in use: a SWD ribbon to the probe, or USB with the
/// BOOT0 jumper set.
/// </summary>
internal sealed class BenchView : Control
{
    private static readonly IBrush Bg = new SolidColorBrush(Color.Parse("#2B3036"));
    private static readonly IBrush Pcb = new SolidColorBrush(Color.Parse("#1F4A3A"));
    private static readonly IBrush PcbEdge = new SolidColorBrush(Color.Parse("#2E6B53"));
    private static readonly IBrush Chip = new SolidColorBrush(Color.Parse("#16191C"));
    private static readonly IBrush Steel = new SolidColorBrush(Color.Parse("#8A9098"));
    private static readonly IBrush Panel = new SolidColorBrush(Color.Parse("#E4E5E0"));
    private static readonly IBrush Erased = new SolidColorBrush(Color.Parse("#3A4048"));
    private static readonly IBrush[] VersionBrush = [Palette.Blue, Palette.Amber, Palette.Green];
    private readonly ProbeFlashDemo _demo;
    private readonly Avalonia.Threading.DispatcherTimer _timer;
    private double _blink;

    public BenchView(ProbeFlashDemo demo)
    {
        _demo = demo;
        demo.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(InvalidateVisual);
        _timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _timer.Tick += (_, _) =>
        {
            _blink += 0.12;
            InvalidateVisual();
        };
        _timer.Start();
    }

    private static FormattedText T(string s, double size, IBrush brush, string face = "JetBrains Mono", FontWeight weight = FontWeight.Normal) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(face, FontStyle.Normal, weight), size, brush);

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        ctx.DrawRectangle(Bg, null, new Rect(0, 0, w, h));
        var mcu = _demo.Mcu;
        ctx.DrawText(T(_demo.DfuMode ? "USB DFU · DFUSE BOOTLOADER 0483:DF11" : "SWD · VIRTUAL CMSIS-DAP · RTT", 10, Steel, "Barlow Condensed", FontWeight.SemiBold), new Point(18, 14));
        if (mcu is null)
        {
            ctx.DrawText(T("Start the demo to put the board on the bench.", 12, Steel), new Point(18, 40));
            return;
        }

        // The board.
        var board = new Rect(w * 0.30, 40, Math.Min(w * 0.62, 470), h - 70);
        ctx.DrawRectangle(Pcb, new Pen(PcbEdge, 2), board, 10, 10);
        for (var i = 0; i < 4; i++) ctx.DrawEllipse(Bg, new Pen(PcbEdge, 1), new Point(i % 2 == 0 ? board.X + 12 : board.Right - 12, i < 2 ? board.Y + 12 : board.Bottom - 12), 5, 5);
        ctx.DrawText(T("BLACKPILL-STYLE DEV BOARD · STM32F411CEU6", 9, Panel, "Barlow Condensed", FontWeight.SemiBold), new Point(board.X + 26, board.Y + 8));

        // MCU with the flash map.
        var chip = new Rect(board.X + 40, board.Y + 34, board.Width - 150, board.Height - 70);
        ctx.DrawRectangle(Chip, null, chip, 6, 6);
        for (var i = 0; i < 12; i++)
        {
            var x = chip.X + 10 + (i * (chip.Width - 20) / 12);
            ctx.DrawRectangle(Steel, null, new Rect(x, chip.Y - 6, 6, 6));
            ctx.DrawRectangle(Steel, null, new Rect(x, chip.Bottom, 6, 6));
        }

        ctx.DrawText(T("FLASH 0x08000000 · 128 × 2 KiB", 9, Steel, "Barlow Condensed", FontWeight.SemiBold), new Point(chip.X + 10, chip.Y + 8));
        var pages = _demo.PageVersions;
        const int Cols = 16;
        var cell = Math.Min((chip.Width - 20) / Cols, (chip.Height - 60) / 8);
        for (var p = 0; p < pages.Count; p++)
        {
            var r = new Rect(chip.X + 10 + (p % Cols * cell), chip.Y + 24 + (p / Cols * cell), cell - 2, cell - 2);
            ctx.DrawRectangle(pages[p] < 0 ? Erased : VersionBrush[pages[p] % VersionBrush.Length], null, r, 2, 2);
        }

        var version = mcu.FirmwareVersion ?? "— erased —";
        ctx.DrawText(T(version, 15, mcu.FirmwareVersion is null ? Palette.Red : Panel, "JetBrains Mono", FontWeight.SemiBold), new Point(chip.X + 10, chip.Bottom - 26));

        // User LED and the BOOT0 jumper.
        var ledOn = mcu.Led && !_demo.DfuMode;
        var led = new Point(board.Right - 50, board.Y + 60);
        if (ledOn) ctx.DrawEllipse(new SolidColorBrush(Color.FromArgb(70, 0x2E, 0x9E, 0x5B)), null, led, 16, 16);
        ctx.DrawEllipse(ledOn ? Palette.Green : Erased, new Pen(Steel, 1), led, 7, 7);
        ctx.DrawText(T("LED PC13", 9, Panel, "Barlow Condensed", FontWeight.SemiBold), new Point(led.X - 18, led.Y + 12));
        var boot = new Rect(board.Right - 70, board.Y + 110, 40, 16);
        ctx.DrawRectangle(Chip, null, boot, 2, 2);
        ctx.DrawRectangle(_demo.DfuMode ? Palette.Amber : Steel, null, new Rect(_demo.DfuMode ? boot.X + 20 : boot.X + 2, boot.Y + 2, 18, 12), 2, 2);
        ctx.DrawText(T(_demo.DfuMode ? "BOOT0 = 1" : "BOOT0 = 0", 9, _demo.DfuMode ? Palette.Amber : Panel, "Barlow Condensed", FontWeight.SemiBold), new Point(boot.X - 2, boot.Bottom + 2));

        // Cable to the host.
        var probe = new Rect(18, h * 0.38, w * 0.30 - 60, 70);
        if (_demo.DfuMode)
        {
            ctx.DrawRectangle(Chip, new Pen(Steel, 1), probe, 8, 8);
            ctx.DrawText(T("HOST USB", 12, Panel, "Barlow Condensed", FontWeight.SemiBold), new Point(probe.X + 10, probe.Y + 10));
            ctx.DrawText(T("DFU · control EP0", 9, Steel), new Point(probe.X + 10, probe.Y + 32));
            ctx.DrawLine(new Pen(Palette.Blue, 5), new Point(probe.Right, probe.Center.Y), new Point(board.X, board.Center.Y));
            ctx.DrawRectangle(Steel, null, new Rect(board.X - 6, board.Center.Y - 8, 14, 16), 2, 2);
        }
        else
        {
            ctx.DrawRectangle(Chip, new Pen(Steel, 1), probe, 8, 8);
            ctx.DrawText(T("CMSIS-DAP", 12, Panel, "Barlow Condensed", FontWeight.SemiBold), new Point(probe.X + 10, probe.Y + 10));
            ctx.DrawText(T("SWD 4 MHz", 9, Steel), new Point(probe.X + 10, probe.Y + 32));
            var active = _demo.ProbeActive || _blink % 1 < 0.3;
            ctx.DrawEllipse(active ? Palette.Amber : Erased, null, new Point(probe.Right - 14, probe.Y + 14), 4, 4);
            string[] wires = ["3V3", "SWDIO", "SWCLK", "GND"];
            for (var i = 0; i < wires.Length; i++)
            {
                var y = probe.Y + 14 + (i * 14);
                IBrush colour = i switch { 0 => Palette.Red, 1 => Palette.Amber, 2 => Palette.Blue, _ => Steel };
                ctx.DrawLine(new Pen(colour, 2), new Point(probe.Right, y), new Point(board.X + 6, board.Bottom - 80 + (i * 12)));
            }

            ctx.DrawText(T("SWD", 9, Panel, "Barlow Condensed", FontWeight.SemiBold), new Point(board.X + 4, board.Bottom - 96));
        }

        // Legend and phase.
        var lx = 18.0;
        var ly = h - 24;
        for (var i = 0; i < ProbeFlashDemo.Versions.Length; i++)
        {
            ctx.DrawRectangle(VersionBrush[i], null, new Rect(lx, ly, 10, 10), 2, 2);
            ctx.DrawText(T(ProbeFlashDemo.Versions[i], 10, Steel), new Point(lx + 14, ly - 2));
            lx += 70;
        }

        ctx.DrawRectangle(Erased, null, new Rect(lx, ly, 10, 10), 2, 2);
        ctx.DrawText(T("erased", 10, Steel), new Point(lx + 14, ly - 2));
        if (_demo.Phase.Length > 0) ctx.DrawText(T($"{_demo.Phase.ToUpperInvariant()} {_demo.Progress:P0}", 11, Palette.Amber, "JetBrains Mono", FontWeight.SemiBold), new Point(18, 40));
    }
}
