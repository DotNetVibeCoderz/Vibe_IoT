using System.Globalization;
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
using static IoTCom.Net.Gallery.Infrastructure.Loc;
using static IoTCom.Net.Gallery.Infrastructure.UiKit;

namespace IoTCom.Net.Gallery.Demos;

public sealed partial class MatterHomeDemo
{
    protected override Control CreateView()
    {
        var mono = (FontFamily)Application.Current!.FindResource("MonoFont")!;
        var house = new Border { CornerRadius = new CornerRadius(14), ClipToBounds = true, Child = new HouseView(this) { Height = 300 } };

        Button Btn(string text, string cls, Func<Task> action)
        {
            var b = new Button { Content = new TextBlock { Text = text }, Classes = { cls } };
            b.Click += async (_, _) => await action();
            b.Bind(InputElement.IsEnabledProperty, new Binding(nameof(IsRunning)));
            return b;
        }

        // The commissioning timeline: eight steps, each a lamp that turns green.
        var timeline = new ItemsControl
        {
            ItemTemplate = new FuncDataTemplate<CommissioningStepRow>((r, _) =>
            {
                if (r is null) return new Border();
                var lamp = new Ellipse { Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Center };
                lamp.Bind(Shape.FillProperty, new Binding(nameof(CommissioningStepRow.Done)) { Converter = new FuncValueConverter<bool, IBrush>(d => d ? Palette.Green : Palette.Grey) });
                return Columns("20,90,*", lamp, new TextBlock { Text = r.Title.ToString(), FontWeight = FontWeight.SemiBold, FontSize = 12 },
                    new TextBlock { Text = r.Detail.ToString(), FontSize = 11, Classes = { "muted" }, FontFamily = mono });
            }),
        };
        timeline.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(Steps)));

        var unlock = new ToggleSwitch { OnContent = L("Writes allowed", "Penulisan diizinkan"), OffContent = L("Write lock closed (read-only)", "Kunci tulis tertutup (hanya-baca)") };
        unlock.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(WritesAllowed)) { Mode = BindingMode.TwoWay });
        var manual = new TextBlock { FontFamily = mono, FontSize = 20, FontWeight = FontWeight.SemiBold };
        manual.Bind(TextBlock.TextProperty, new Binding(nameof(Manual)));
        var qr = new TextBlock { FontFamily = mono, FontSize = 11, Classes = { "muted" } };
        qr.Bind(TextBlock.TextProperty, new Binding(nameof(Qr)));
        var fabric = new TextBlock { FontFamily = mono, FontSize = 11, TextWrapping = TextWrapping.Wrap };
        fabric.Bind(TextBlock.TextProperty, new Binding(nameof(Fabric)));
        var commissionCard = Card(Stack(10,
            new TextBlock { Text = L("Pairing code on the bridge", "Kode pairing di bridge"), FontSize = 11, Classes = { "muted" } }, manual, qr,
            unlock, Btn(L("Commission bridge", "Commission bridge"), "primary", CommissionAsync), timeline, fabric),
            L("Controller", "Controller"));

        var lights = new StackPanel { Spacing = 6 };
        foreach (var i in Enumerable.Range(0, 4))
        {
            var ep = (ushort)(2 + i);
            lights.Children.Add(Row(8,
                Btn(L($"Toggle {RoomName(i)}", $"Saklar {RoomName(i)}"), "ghost", () => ToggleAsync(ep)),
                Btn(L("Unplug / plug", "Cabut / colok"), "ghost", () => { TogglePlug(ep); return Task.CompletedTask; })));
        }

        var lightsCard = Card(lights, L("Lights (Toggle behind the write lock)", "Lampu (Toggle di balik kunci tulis)"));
        var log = new ItemsControl { ItemTemplate = new FuncDataTemplate<string>((t, _) => new TextBlock { Text = t, FontFamily = mono, FontSize = 10.5, TextWrapping = TextWrapping.Wrap }) };
        log.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(Log)));
        var logCard = Card(new ScrollViewer { MaxHeight = 170, Content = log }, L("Controller log", "Log controller"));
        var status = new TextBlock { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };
        status.Bind(TextBlock.TextProperty, new Binding(nameof(Status)));
        return new ScrollViewer
        {
            DataContext = this,
            Content = Stack(16, Columns("1.5*,16,*", Stack(16, house, lightsCard, logCard), new Border(), Stack(16, commissionCard)), status),
        };
    }

    private static string RoomName(int i) => i switch { 0 => "living room", 1 => "kitchen", 2 => "terrace", _ => "bedroom" };
}

/// <summary>The house: four rooms with a ceiling light each (glowing when on, hatched when unplugged), two thermometers and the bridge on the hallway wall.</summary>
internal sealed class HouseView : Control
{
    private static readonly IBrush Bg = new SolidColorBrush(Color.Parse("#2B3036"));
    private static readonly IBrush Floor = new SolidColorBrush(Color.Parse("#E4E5E0"));
    private static readonly IBrush Wall = new SolidColorBrush(Color.Parse("#2B3036"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#8A9098"));
    private static readonly IBrush Glow = new SolidColorBrush(Color.FromArgb(90, 0xF2, 0xA9, 0x00));
    private readonly MatterHomeDemo _demo;

    public HouseView(MatterHomeDemo demo)
    {
        _demo = demo;
        demo.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(InvalidateVisual);
        var t = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        t.Tick += (_, _) => InvalidateVisual();
        t.Start();
    }

    private static FormattedText T(string s, double size, IBrush brush, string face = "Barlow Condensed", FontWeight weight = FontWeight.SemiBold) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(face, FontStyle.Normal, weight), size, brush);

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        ctx.DrawRectangle(Bg, null, new Rect(0, 0, w, h));
        ctx.DrawText(T("HOUSE · MATTER BRIDGE 10.0.0.70:5540", 10, Muted), new Point(18, 12));
        var sim = _demo.Sim;
        var plan = new Rect(18, 32, w - 36, h - 50);
        ctx.DrawRectangle(Floor, new Pen(Wall, 4), plan, 6, 6);
        var midX = plan.X + (plan.Width / 2);
        var midY = plan.Y + (plan.Height / 2);
        ctx.DrawLine(new Pen(Wall, 3), new Point(midX, plan.Y), new Point(midX, plan.Bottom));
        ctx.DrawLine(new Pen(Wall, 3), new Point(plan.X, midY), new Point(plan.Right, midY));
        Rect[] rooms = [new(plan.X, plan.Y, midX - plan.X, midY - plan.Y), new(midX, plan.Y, plan.Right - midX, midY - plan.Y),
            new(plan.X, midY, midX - plan.X, plan.Bottom - midY), new(midX, midY, plan.Right - midX, plan.Bottom - midY)];
        if (sim is null)
        {
            ctx.DrawText(T("Start the demo.", 13, Muted), new Point(plan.X + 12, plan.Y + 12));
            return;
        }

        for (var i = 0; i < 4; i++)
        {
            var (label, endpoint, onOff, info) = sim.Lights[i];
            var room = rooms[i];
            var center = new Point(room.Center.X, room.Y + (room.Height * 0.45));
            if (onOff.On && info.Reachable) ctx.DrawEllipse(Glow, null, center, room.Width * 0.32, room.Height * 0.38);
            ctx.DrawEllipse(!info.Reachable ? Muted : onOff.On ? Palette.Amber : Floor, new Pen(Wall, 2), center, 14, 14);
            if (!info.Reachable)
            {
                ctx.DrawLine(new Pen(Palette.Red, 2), new Point(center.X - 12, center.Y - 12), new Point(center.X + 12, center.Y + 12));
                ctx.DrawText(T("UNREACHABLE", 10, Palette.Red), new Point(center.X - 28, center.Y + 18));
            }

            ctx.DrawText(T(label.ToUpperInvariant(), 12, Wall), new Point(room.X + 10, room.Y + 8));
            ctx.DrawText(T($"EP {endpoint}", 10, Muted, "JetBrains Mono", FontWeight.Normal), new Point(room.Right - 42, room.Y + 9));
        }

        var temps = new[] { (sim.Sensors[0].Temperature.Celsius, rooms[0]), (sim.Sensors[1].Temperature.Celsius, rooms[2]) };
        foreach (var (c, room) in temps)
            ctx.DrawText(T($"{c:0.0} °C", 16, Palette.Blue, "JetBrains Mono", FontWeight.SemiBold), new Point(room.X + 10, room.Bottom - 28));

        var bridge = new Rect(midX - 34, midY - 14, 68, 28);
        ctx.DrawRectangle(Wall, null, bridge, 6, 6);
        ctx.DrawEllipse(_demo.Commissioned ? Palette.Green : Palette.Amber, null, new Point(bridge.X + 12, bridge.Center.Y), 4, 4);
        ctx.DrawText(T("BRIDGE", 10, Floor), new Point(bridge.X + 20, bridge.Y + 7));
    }
}
