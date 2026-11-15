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
using IoTCom.Net.Protocols.EtherNetIp;
using static IoTCom.Net.Gallery.Infrastructure.Loc;
using static IoTCom.Net.Gallery.Infrastructure.UiKit;

namespace IoTCom.Net.Gallery.Demos;

public sealed partial class EnipPackLineDemo
{
    protected override Control CreateView()
    {
        var mono = (FontFamily)Application.Current!.FindResource("MonoFont")!;
        var line = new Border { CornerRadius = new CornerRadius(14), ClipToBounds = true, Child = new PackLine(this) { Height = 230 } };

        Button Btn(string text, string cls, Func<Task> action)
        {
            var b = new Button { Content = new TextBlock { Text = text }, Classes = { cls } };   // a plain string would turn "_" into an access key
            b.Click += async (_, _) => await action();
            b.Bind(InputElement.IsEnabledProperty, new Binding(nameof(IsRunning)));
            return b;
        }

        Control SliderRow(string label, string path, double min, double max, double tick, string format)
        {
            var slider = new Slider { Minimum = min, Maximum = max, TickFrequency = tick, IsSnapToTickEnabled = true };
            slider.Bind(RangeBase.ValueProperty, new Binding(path) { Mode = BindingMode.TwoWay });
            var text = new TextBlock { FontFamily = mono, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, MinWidth = 70 };
            text.Bind(TextBlock.TextProperty, new Binding(path) { StringFormat = format });
            return Columns("80,*,8,auto", new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center }, slider, new Border(), text);
        }

        var unlock = new ToggleSwitch { OnContent = L("Writes allowed", "Penulisan diizinkan"), OffContent = L("Write lock closed (read-only)", "Kunci tulis tertutup (hanya-baca)") };
        unlock.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(WritesAllowed)) { Mode = BindingMode.TwoWay });
        var operatorCard = Card(Stack(10,
            unlock,
            Row(8, Btn(L("Start conveyor", "Jalankan konveyor"), "primary", () => MotorAsync(true)), Btn(L("Stop", "Hentikan"), "ghost", () => MotorAsync(false))),
            SliderRow(L("Speed", "Kecepatan"), nameof(SpeedTarget), 0, 200, 5, "{0:0} /min"),
            Btn(L("Write Line_Speed", "Tulis Line_Speed"), "ghost", SpeedAsync),
            SliderRow(L("Seal set", "Set seal"), nameof(SealTarget), 150, 190, 0.5, "{0:0.0} °C"),
            Btn(L("Write Seal_Setpoint", "Tulis Seal_Setpoint"), "ghost", SealAsync),
            Btn(L("Reset Count_Good (read-only tag)", "Reset Count_Good (tag hanya-baca)"), "ghost", ResetCounterAsync)),
            L("HMI operator", "Operator HMI"));

        TextBlock Head(string t) => new() { Text = t, FontSize = 10, Classes = { "muted" }, FontFamily = mono };
        var rows = new ItemsControl
        {
            ItemTemplate = new FuncDataTemplate<LogixTagRow>((r, _) =>
            {
                if (r is null) return new Border();
                var v = new TextBlock { FontFamily = mono, FontSize = 11 };
                v.Bind(TextBlock.TextProperty, new Binding(nameof(LogixTagRow.Value)));
                return Columns("130,8,40,8,*", new TextBlock { Text = r.Name, FontFamily = mono, FontSize = 11, Foreground = Palette.Blue }, new Border(),
                    new TextBlock { Text = r.Access, FontFamily = mono, FontSize = 11, Foreground = r.Access == "R/W" ? Palette.Amber : Palette.Grey }, new Border(), v);
            }),
        };
        rows.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(Tags)));
        var identity = new TextBlock { FontSize = 11, Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };
        identity.Bind(TextBlock.TextProperty, new Binding(nameof(Identity)));
        var tagsCard = Card(Stack(6, Columns("130,8,40,8,*", Head("TAG"), new Border(), Head("R/W"), new Border(), Head("VALUE")), rows, identity), L("Logix tags (Multiple_Service_Packet)", "Tag Logix (Multiple_Service_Packet)"));

        var log = new ItemsControl { ItemTemplate = new FuncDataTemplate<string>((t, _) => new TextBlock { Text = t, FontFamily = mono, FontSize = 10.5, TextWrapping = TextWrapping.Wrap }) };
        log.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(Log)));
        var logCard = Card(new ScrollViewer { MaxHeight = 170, Content = log }, L("HMI log", "Log HMI"));

        var status = new TextBlock { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };
        status.Bind(TextBlock.TextProperty, new Binding(nameof(Status)));
        return new ScrollViewer
        {
            DataContext = this,
            Content = Stack(16, Columns("1.6*,16,*", Stack(16, line, tagsCard), new Border(), Stack(16, operatorCard, logCard)), status),
        };
    }
}

/// <summary>The line: packs travel the conveyor under the seal jaws; leaky packs drop into the reject bin.</summary>
internal sealed class PackLine : Control
{
    private static readonly IBrush Bg = new SolidColorBrush(Color.Parse("#2B3036"));
    private static readonly IBrush Belt = new SolidColorBrush(Color.Parse("#1E2227"));
    private static readonly IBrush Steel = new SolidColorBrush(Color.Parse("#8A9098"));
    private static readonly IBrush Pack = new SolidColorBrush(Color.Parse("#E4E5E0"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#8A9098"));
    private readonly EnipPackLineDemo _demo;
    private readonly DispatcherTimerHandle _timer;
    private double _offset;

    public PackLine(EnipPackLineDemo demo)
    {
        _demo = demo;
        demo.Polled2 += () => Avalonia.Threading.Dispatcher.UIThread.Post(InvalidateVisual);
        _timer = new DispatcherTimerHandle(TimeSpan.FromMilliseconds(50), () =>
        {
            if (_demo.IsRunning && _demo.Running) _offset += _demo.Speed / 60 * 3;
            InvalidateVisual();
        });
    }

    private static FormattedText T(string s, double size, IBrush brush, string face = "JetBrains Mono", FontWeight weight = FontWeight.Normal) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(face, FontStyle.Normal, weight), size, brush);

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        ctx.DrawRectangle(Bg, null, new Rect(0, 0, w, h));
        var beltY = h * 0.58;
        ctx.DrawRectangle(Belt, new Pen(Steel, 1.5), new Rect(16, beltY, w - 32, 16), 8, 8);
        for (var x = 24 - (_offset % 24); x < w - 24; x += 24) ctx.DrawLine(new Pen(Steel, 1), new Point(x, beltY + 3), new Point(x, beltY + 13));

        // Seal jaws: amber when inside the film's window, red when cold or hot.
        var jawX = w * 0.55;
        var ok = Math.Abs(_demo.SealTemp - EnipPlcSimulator.NominalSealTemp) <= 4;
        var jaw = ok ? Palette.Amber : Palette.Red;
        ctx.DrawRectangle(jaw, null, new Rect(jawX - 18, beltY - 62, 36, 14), 3, 3);
        ctx.DrawRectangle(Steel, null, new Rect(jawX - 3, 18, 6, beltY - 80));
        ctx.DrawText(T($"SEAL {_demo.SealTemp:0.0} °C", 12, jaw, "JetBrains Mono", FontWeight.SemiBold), new Point(jawX + 24, beltY - 64));

        // Packs.
        var spacing = 46.0;
        for (var x = 20 + (_offset % spacing); x < w - 40; x += spacing)
        {
            var sealed_ = x > jawX;
            var bad = sealed_ && !ok && ((int)((x - _offset) / spacing) % 3 == 0);
            ctx.DrawRectangle(bad ? Palette.Red : Pack, null, new Rect(x, beltY - 24, 30, 24), 3, 3);
            if (sealed_) ctx.DrawLine(new Pen(Steel, 2), new Point(x + 2, beltY - 20), new Point(x + 28, beltY - 20));
        }

        // Counters.
        ctx.DrawText(T($"{_demo.Good:0}", 26, Palette.Green, "Barlow Condensed", FontWeight.SemiBold), new Point(w - 150, 18));
        ctx.DrawText(T("GOOD", 10, Muted, "Barlow Condensed", FontWeight.SemiBold), new Point(w - 150, 50));
        ctx.DrawText(T($"{_demo.Rejects:0}", 26, Palette.Red, "Barlow Condensed", FontWeight.SemiBold), new Point(w - 70, 18));
        ctx.DrawText(T("REJECT", 10, Muted, "Barlow Condensed", FontWeight.SemiBold), new Point(w - 70, 50));
        ctx.DrawText(T(_demo.Running ? $"CONVEYOR RUNNING · {_demo.Speed:0} PACKS/MIN" : "CONVEYOR STOPPED", 11, _demo.Running ? Palette.Green : Muted, "Barlow Condensed", FontWeight.SemiBold), new Point(18, 18));
        ctx.DrawText(T("PACKLINE-1 · ETHERNET/IP 44818 · SLOT 0", 10, Muted, "Barlow Condensed", FontWeight.SemiBold), new Point(18, h - 20));
    }

    /// <summary>A DispatcherTimer that keeps animating while the control lives.</summary>
    private sealed class DispatcherTimerHandle
    {
        private readonly Avalonia.Threading.DispatcherTimer _t;

        public DispatcherTimerHandle(TimeSpan interval, Action tick)
        {
            _t = new Avalonia.Threading.DispatcherTimer { Interval = interval };
            _t.Tick += (_, _) => tick();
            _t.Start();
        }
    }
}
