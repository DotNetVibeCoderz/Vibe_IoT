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
using IoTCom.Net.Protocols.EtherCat;
using static IoTCom.Net.Gallery.Infrastructure.Loc;
using static IoTCom.Net.Gallery.Infrastructure.UiKit;

namespace IoTCom.Net.Gallery.Demos;

public sealed partial class EthercatRackDemo
{
    protected override Control CreateView()
    {
        var mono = (FontFamily)Application.Current!.FindResource("MonoFont")!;
        var rail = new Border { CornerRadius = new CornerRadius(14), ClipToBounds = true, Child = new EthercatRail(this) { Height = 330 } };

        Button Btn(string text, string cls, Func<Task> action)
        {
            var b = new Button { Content = new TextBlock { Text = text }, Classes = { cls } };   // a plain string would turn "_" into an access key
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

        var state = new TextBlock { FontFamily = mono, FontSize = 22, FontWeight = FontWeight.SemiBold };
        state.Bind(TextBlock.TextProperty, new Binding(nameof(LineState)));
        var lineCard = Card(Stack(10,
            state,
            Switch(L("Writes allowed", "Penulisan diizinkan"), L("Write lock closed (read-only)", "Kunci tulis tertutup (hanya-baca)"), nameof(WritesAllowed)),
            Row(8,
                Btn("PRE-OP", "ghost", () => RequestAsync(EcatState.PreOp)),
                Btn("SAFE-OP", "ghost", () => RequestAsync(EcatState.SafeOp)),
                Btn("OP", "primary", () => RequestAsync(EcatState.Op))),
            Btn(L("Acknowledge errors", "Akui error"), "ghost", AcknowledgeAsync)),
            L("Line state", "Status jalur"));

        var outputs = new StackPanel { Spacing = 6 };
        foreach (var (bit, name) in OutputChannels)
        {
            var t = new ToggleSwitch { OnContent = $"DO{bit} · {name}", OffContent = $"DO{bit} · {name}" };
            t.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(OutputBits)) { Mode = BindingMode.OneWay, Converter = new Avalonia.Data.Converters.FuncValueConverter<byte, bool?>(b => (b & (1 << bit)) != 0) });
            t.IsCheckedChanged += (_, _) => SetOutput(bit, t.IsChecked == true);
            t.Bind(InputElement.IsEnabledProperty, new Binding(nameof(WritesAllowed)));
            outputs.Children.Add(t);
        }

        var outputsCard = Card(outputs, L("Outputs (DO8, behind the write lock)", "Output (DO8, di balik kunci tulis)"));
        var plantCard = Card(Stack(6,
            Switch(L("Emergency stop pressed", "Tombol darurat ditekan"), L("Emergency stop released", "Tombol darurat dilepas"), nameof(EmergencyStop)),
            Switch(L("Guard door open", "Pintu pengaman terbuka"), L("Guard door closed", "Pintu pengaman tertutup"), nameof(GuardOpen)),
            Switch(L("Cable cut after DI8", "Kabel putus setelah DI8"), L("Cable intact", "Kabel utuh"), nameof(CableCut)),
            Switch(L("20 % frames lost", "20 % frame hilang"), L("Clean link", "Link bersih"), nameof(Lossy))),
            L("Plant", "Pabrik"));

        var coe = new TextBlock { FontFamily = mono, FontSize = 11, TextWrapping = TextWrapping.Wrap };
        coe.Bind(TextBlock.TextProperty, new Binding(nameof(Coe)));
        var coeCard = Card(Stack(8, Row(8, Btn(L("Read 0x1008 / 0x8000:15", "Baca 0x1008 / 0x8000:15"), "ghost", ReadNameAsync), Btn(L("Next filter", "Filter berikutnya"), "ghost", CycleFilterAsync)), coe),
            L("CoE mailbox (AI4)", "Mailbox CoE (AI4)"));

        var counters = new TextBlock { FontFamily = mono, FontSize = 11.5, TextWrapping = TextWrapping.Wrap };
        counters.Bind(TextBlock.TextProperty, new Binding(nameof(Counters)));
        counters.Bind(TextBlock.ForegroundProperty, new Binding(nameof(WkcOk)) { Converter = new Avalonia.Data.Converters.FuncValueConverter<bool, IBrush>(ok => ok ? Palette.Green : Palette.Red) });

        var log = new ItemsControl { ItemTemplate = new FuncDataTemplate<string>((t, _) => new TextBlock { Text = t, FontFamily = mono, FontSize = 10.5, TextWrapping = TextWrapping.Wrap }) };
        log.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(Log)));
        var logCard = Card(new ScrollViewer { MaxHeight = 190, Content = log }, L("Master log", "Log master"));

        var status = new TextBlock { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };
        status.Bind(TextBlock.TextProperty, new Binding(nameof(Status)));
        return new ScrollViewer
        {
            DataContext = this,
            Content = Stack(16,
                Columns("1.7*,16,*", Stack(16, rail, Card(counters, L("Cyclic exchange (LRW every 20 ms)", "Pertukaran siklik (LRW setiap 20 ms)")), logCard), new Border(),
                    Stack(16, lineCard, outputsCard, plantCard, coeCard)),
                status),
        };
    }
}

/// <summary>
/// The DIN rail: the master's cable into the coupler, one slim terminal per slave with its AL-state lamp and channel LEDs,
/// and one frame travelling through every terminal and back, collecting the working counter. Below it the conveyor the
/// rack drives.
/// </summary>
internal sealed class EthercatRail : Control
{
    private static readonly IBrush Bg = new SolidColorBrush(Color.Parse("#2B3036"));
    private static readonly IBrush Panel = new SolidColorBrush(Color.Parse("#E4E5E0"));
    private static readonly IBrush PanelDark = new SolidColorBrush(Color.Parse("#C9CBC4"));
    private static readonly IBrush Steel = new SolidColorBrush(Color.Parse("#8A9098"));
    private static readonly IBrush Off = new SolidColorBrush(Color.Parse("#4A5058"));
    private static readonly IBrush Belt = new SolidColorBrush(Color.Parse("#1E2227"));
    private static readonly IBrush Ink = new SolidColorBrush(Color.Parse("#2B3036"));
    private readonly EthercatRackDemo _demo;
    private readonly Avalonia.Threading.DispatcherTimer _timer;
    private double _phase, _belt, _partPos;

    public EthercatRail(EthercatRackDemo demo)
    {
        _demo = demo;
        demo.Refreshed += InvalidateVisual;
        _timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _timer.Tick += (_, _) =>
        {
            if (!_demo.IsRunning) return;
            _phase = (_phase + 0.025) % 1;
            var speed = _demo.Analog(3);
            _belt += speed / 60 * 0.04 * 60;   // px: 1 m/min ≈ 1 px per tick
            _partPos += speed / 60 * 0.04 * 60;
            InvalidateVisual();
        };
        _timer.Start();
    }

    private static FormattedText T(string s, double size, IBrush brush, string face = "JetBrains Mono", FontWeight weight = FontWeight.Normal) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(face, FontStyle.Normal, weight), size, brush);

    private static IBrush StateBrush(EthercatSlave s) => s.StatusCode != 0 ? Palette.Red : s.State switch
    {
        EcatState.Op => Palette.Green,
        EcatState.SafeOp => Palette.Blue,
        EcatState.PreOp => Palette.Amber,
        _ => Steel,
    };

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        ctx.DrawRectangle(Bg, null, new Rect(0, 0, w, h));
        var master = _demo.Master;
        var rack = _demo.Rack;
        ctx.DrawText(T("ETHERCAT · ETHERTYPE 0x88A4 · ONE FRAME THROUGH EVERY TERMINAL", 10, Steel, "Barlow Condensed", FontWeight.SemiBold), new Point(18, 14));
        if (master is null || rack is null || master.Slaves.Count == 0)
        {
            ctx.DrawText(T("Start the demo to scan the line.", 12, Steel), new Point(18, 40));
            return;
        }

        // Master box and DIN rail.
        var top = 44.0;
        var termH = 168.0;
        var masterRect = new Rect(18, top + 40, 92, 70);
        ctx.DrawRectangle(Belt, new Pen(Steel, 1.2), masterRect, 8, 8);
        ctx.DrawText(T("MASTER", 12, Panel, "Barlow Condensed", FontWeight.SemiBold), new Point(masterRect.X + 10, masterRect.Y + 10));
        ctx.DrawText(T(master.Interface ?? "", 10, Steel), new Point(masterRect.X + 10, masterRect.Y + 30));
        ctx.DrawText(T("LRW 20 ms", 10, Palette.Amber), new Point(masterRect.X + 10, masterRect.Y + 46));

        var x0 = 150.0;
        var widths = master.Slaves.Select(s => s.Position == 0 ? 92.0 : 58.0).ToArray();
        var railRight = x0 + widths.Sum() + (6 * widths.Length) + 10;
        ctx.DrawRectangle(Steel, null, new Rect(x0 - 10, top + (termH / 2) - 6, railRight - x0 + 20, 12), 2, 2);   // the rail
        var cut = rack.Segment.BreakAfter;

        // Cable from the master to the coupler (out on top, back below).
        var cableOut = top + 58;
        var cableBack = top + 96;
        ctx.DrawLine(new Pen(Palette.Blue, 3), new Point(masterRect.Right, cableOut), new Point(x0, cableOut));
        ctx.DrawLine(new Pen(Palette.Blue, 3), new Point(masterRect.Right, cableBack), new Point(x0, cableBack));

        var xs = new double[widths.Length];
        var x = x0;
        for (var i = 0; i < master.Slaves.Count; i++)
        {
            xs[i] = x;
            var s = master.Slaves[i];
            var reachable = cut is null || i < cut;
            var body = new Rect(x, top, widths[i], termH);
            ctx.DrawRectangle(reachable ? Panel : PanelDark, new Pen(Steel, 1), body, 4, 4);
            ctx.DrawRectangle(Ink, null, new Rect(x, top, widths[i], 26), 4, 4);
            var label = s.Position == 0 ? "COUPLER" : s.Name.Replace("Sim ", "", StringComparison.Ordinal).Split(' ')[0];
            ctx.DrawText(T(label, 11, Panel, "Barlow Condensed", FontWeight.SemiBold), new Point(x + 6, top + 5));
            ctx.DrawEllipse(StateBrush(s), null, new Point(x + widths[i] - 10, top + 13), 5, 5);
            ctx.DrawText(T(EcatAlStatus.Name(s.State), 9, Ink, "Barlow Condensed", FontWeight.SemiBold), new Point(x + 6, top + 32));
            ctx.DrawText(T($"0x{s.Station:X4}", 9, Steel), new Point(x + 6, top + 46));
            if (s.StatusCode != 0) ctx.DrawText(T($"0x{s.StatusCode:X4}", 9, Palette.Red), new Point(x + 6, top + 58));

            // Channel LEDs.
            var ledY = top + 78;
            if (s.InputBytes == 1 || s.OutputBytes == 1)
            {
                var value = s.InputBytes == 1 ? (_demo.InputsOf(i) is { Length: 1 } b ? b[0] : (byte)0) : s.State == EcatState.Op ? _demo.CommandedOutputs : (byte)0;
                var on = s.InputBytes == 1 ? Palette.Green : Palette.Amber;
                for (var c = 0; c < 8; c++)
                {
                    var lit = reachable && (value & (1 << c)) != 0;
                    ctx.DrawEllipse(lit ? on : Off, null, new Point(x + 14 + ((c % 2) * 26), ledY + ((c / 2) * 18)), 5, 5);
                    ctx.DrawText(T(c.ToString(CultureInfo.InvariantCulture), 8, Steel), new Point(x + 22 + ((c % 2) * 26), ledY - 6 + ((c / 2) * 18)));
                }
            }
            else if (s.InputBytes == 8)
            {
                double[] max = [6, 80, 10, 25];
                for (var c = 0; c < 4; c++)
                {
                    var v = reachable ? Math.Clamp(_demo.Analog(c) / max[c], 0, 1) : 0;
                    var bar = new Rect(x + 8 + (c * 12), ledY - 4, 8, 72);
                    ctx.DrawRectangle(Off, null, bar, 2, 2);
                    ctx.DrawRectangle(Palette.Blue, null, new Rect(bar.X, bar.Bottom - (bar.Height * v), bar.Width, bar.Height * v), 2, 2);
                }
            }
            else
            {
                ctx.DrawEllipse(reachable ? Palette.Green : Off, null, new Point(x + 16, ledY + 4), 5, 5);
                ctx.DrawText(T("RUN", 9, Steel, "Barlow Condensed", FontWeight.SemiBold), new Point(x + 26, ledY - 2));
                ctx.DrawEllipse(reachable ? Palette.Amber : Off, null, new Point(x + 16, ledY + 24), 5, 5);
                ctx.DrawText(T("LINK", 9, Steel, "Barlow Condensed", FontWeight.SemiBold), new Point(x + 26, ledY + 18));
            }

            x += widths[i] + 6;
        }

        // The frame: out along the top lane to the last reachable terminal, back along the bottom lane.
        var last = Math.Min(cut ?? master.Slaves.Count, master.Slaves.Count) - 1;
        var turn = xs[last] + widths[last] - 8;
        ctx.DrawLine(new Pen(Palette.Amber, 1, new DashStyle([3, 3], 0)), new Point(x0, cableOut), new Point(turn, cableOut));
        ctx.DrawLine(new Pen(Palette.Amber, 1, new DashStyle([3, 3], 0)), new Point(turn, cableOut), new Point(turn, cableBack));
        ctx.DrawLine(new Pen(Palette.Amber, 1, new DashStyle([3, 3], 0)), new Point(turn, cableBack), new Point(x0, cableBack));
        if (cut is { } c2 && c2 < master.Slaves.Count)
        {
            var gap = xs[c2] - 3;
            ctx.DrawLine(new Pen(Palette.Red, 3), new Point(gap - 6, top - 6), new Point(gap + 6, top + termH + 6));
            ctx.DrawText(T("CABLE CUT", 10, Palette.Red, "Barlow Condensed", FontWeight.SemiBold), new Point(gap + 8, top + termH + 2));
        }

        var stats = master.Statistics;
        var outLen = turn - masterRect.Right;
        var trip = (2 * outLen) + (cableBack - cableOut);
        var at = _phase * trip;
        Point pos;
        var wkc = 0;
        if (at < outLen)
        {
            pos = new Point(masterRect.Right + at, cableOut);
            for (var i = 0; i <= last; i++)
                if (pos.X > xs[i] + (widths[i] / 2)) wkc += (master.Slaves[i].OutputBytes > 0 ? 2 : 0) + (master.Slaves[i].InputBytes > 0 ? 1 : 0);
        }
        else if (at < outLen + (cableBack - cableOut))
        {
            pos = new Point(turn, cableOut + (at - outLen));
            wkc = stats.LastWorkingCounter;
        }
        else
        {
            pos = new Point(turn - (at - outLen - (cableBack - cableOut)), cableBack);
            wkc = stats.LastWorkingCounter;
        }

        if (master.Image.Length == 0) wkc = 0;
        var short_ = wkc < stats.ExpectedWorkingCounter && at >= outLen;
        var pill = new Rect(pos.X - 26, pos.Y - 9, 52, 18);
        ctx.DrawRectangle(short_ ? Palette.Red : Palette.Amber, null, pill, 9, 9);
        ctx.DrawText(T($"WKC {wkc}", 10, Ink, "JetBrains Mono", FontWeight.SemiBold), new Point(pill.X + 6, pill.Y + 2));

        var verdict = master.Image.Length == 0 ? "no process image below SAFE-OP"
            : stats.LastWorkingCounter == stats.ExpectedWorkingCounter ? $"back with wkc {stats.LastWorkingCounter} = expected" : $"back with wkc {stats.LastWorkingCounter} of {stats.ExpectedWorkingCounter}: a terminal did not see the frame";
        ctx.DrawText(T(verdict, 11, stats.LastWorkingCounter == stats.ExpectedWorkingCounter ? Palette.Green : Palette.Red), new Point(x0 - 10, top + termH + 18));

        // Conveyor.
        var beltY = h - 58;
        ctx.DrawRectangle(Belt, new Pen(Steel, 1.5), new Rect(18, beltY, w - 36, 16), 8, 8);
        for (var bx = 26 - (_belt % 24); bx < w - 26; bx += 24) ctx.DrawLine(new Pen(Steel, 1), new Point(bx, beltY + 3), new Point(bx, beltY + 13));
        var sensorX = w * 0.62;
        var di = _demo.InputsOf(1) is { Length: 1 } d ? d[0] : (byte)0;
        ctx.DrawLine(new Pen((di & 2) != 0 ? Palette.Green : Off, 2), new Point(sensorX, beltY - 34), new Point(sensorX, beltY - 2));
        for (var px = 30 + (_partPos % 80); px < w - 50; px += 80) ctx.DrawRectangle(Panel, null, new Rect(px, beltY - 22, 34, 22), 3, 3);
        var running = (di & 1) != 0;
        ctx.DrawText(T(running ? $"CONVEYOR RUNNING · {_demo.Analog(3):0.0} M/MIN · {_demo.Analog(0):0.00} A" : "CONVEYOR STOPPED", 11, running ? Palette.Green : Steel, "Barlow Condensed", FontWeight.SemiBold), new Point(18, h - 30));
        ctx.DrawText(T($"{rack.Parts} PARTS · GEARBOX {_demo.Analog(1):0.0} °C · AIR {_demo.Analog(2):0.00} BAR", 11, Steel, "Barlow Condensed", FontWeight.SemiBold), new Point(w * 0.45, h - 30));
        if ((di & 4) == 0) ctx.DrawText(T("E-STOP", 13, Palette.Red, "Barlow Condensed", FontWeight.Bold), new Point(w - 80, beltY - 30));
        else if ((di & 8) == 0) ctx.DrawText(T("GUARD OPEN", 13, Palette.Amber, "Barlow Condensed", FontWeight.Bold), new Point(w - 110, beltY - 30));
    }
}
