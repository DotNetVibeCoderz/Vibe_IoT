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

public sealed partial class StreetLightDemo
{
    protected override Control CreateView()
    {
        var mono = (FontFamily)Application.Current!.FindResource("MonoFont")!;
        var street = new Border { CornerRadius = new CornerRadius(14), ClipToBounds = true, Child = new NightStreet(this) { Height = 300 } };

        var picker = new ComboBox { MinWidth = 220, ItemTemplate = new FuncDataTemplate<StreetPole>((p, _) => new TextBlock { Text = p?.Endpoint ?? "", FontFamily = mono, FontSize = 12 }) };
        picker.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(Poles)));
        picker.Bind(SelectingItemsControl.SelectedItemProperty, new Binding(nameof(Selected)) { Mode = BindingMode.TwoWay });
        var unlock = new ToggleSwitch { OnContent = L("Writes allowed", "Penulisan diizinkan"), OffContent = L("Write lock closed (read-only)", "Kunci tulis tertutup (hanya-baca)") };
        unlock.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(WritesAllowed)) { Mode = BindingMode.TwoWay });
        var dimmer = new Slider { Minimum = 0, Maximum = 100, TickFrequency = 5, IsSnapToTickEnabled = true };
        dimmer.Bind(RangeBase.ValueProperty, new Binding(nameof(DimmerTarget)) { Mode = BindingMode.TwoWay });
        var dimText = new TextBlock { FontFamily = mono, FontSize = 12 };
        dimText.Bind(TextBlock.TextProperty, new Binding(nameof(DimmerTarget)) { StringFormat = "{0:0} %" });
        Button Btn(string text, string cls, Func<Task> action)
        {
            var b = new Button { Content = text, Classes = { cls } };
            b.Click += async (_, _) => await action();
            b.Bind(InputElement.IsEnabledProperty, new Binding(nameof(IsRunning)));
            return b;
        }

        var deviceInfo = new ItemsControl { ItemTemplate = new FuncDataTemplate<string>((t, _) => new TextBlock { Text = t, FontFamily = mono, FontSize = 11 }) };
        deviceInfo.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(DeviceInfo)));
        var operatorCard = Card(Stack(10,
            picker, unlock,
            Columns("auto,8,*,8,auto", new TextBlock { Text = L("Dimmer", "Dimmer"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center }, new Border(), dimmer, new Border(), dimText),
            Row(8, Btn(L("Switch on", "Nyalakan"), "primary", () => SwitchAsync(true)), Btn(L("Switch off", "Matikan"), "ghost", () => SwitchAsync(false)), Btn(L("Reboot", "Reboot"), "ghost", RebootAsync)),
            Row(8, Btn(L("Cut power", "Putus listrik"), "ghost", PowerCutAsync), Btn(L("Restore power", "Pulihkan listrik"), "ghost", PowerOnSelectedAsync)),
            new TextBlock { Text = L("Device object /3/0, read on selection", "Objek Device /3/0, dibaca saat dipilih"), FontSize = 11, Classes = { "muted" } },
            deviceInfo), L("Operator", "Operator"));

        var log = new ItemsControl { ItemTemplate = new FuncDataTemplate<string>((t, _) => new TextBlock { Text = t, FontFamily = mono, FontSize = 10.5, TextWrapping = TextWrapping.Wrap }) };
        log.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(Log)));
        var logCard = Card(new ScrollViewer { MaxHeight = 220, Content = log }, L("LwM2M operations", "Operasi LwM2M"));

        var status = new TextBlock { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };
        status.Bind(TextBlock.TextProperty, new Binding(nameof(Status)));
        return new ScrollViewer
        {
            DataContext = this,
            Content = Stack(16, Columns("1.5*,16,*", Stack(16, street, logCard), new Border(), operatorCard), status),
        };
    }
}

/// <summary>The street at night: poles along the road, each lamp's glow sized by its dimmer; unregistered lights go dark.</summary>
internal sealed class NightStreet : Control
{
    private static readonly IBrush Sky = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse("#141A24"), 0), new GradientStop(Color.Parse("#2B3036"), 1) },
    };
    private static readonly IBrush Road = new SolidColorBrush(Color.Parse("#3A4048"));
    private static readonly IBrush Kerb = new SolidColorBrush(Color.Parse("#8A9098"));
    private static readonly IBrush Pole = new SolidColorBrush(Color.Parse("#5B626B"));
    private static readonly IBrush Text = new SolidColorBrush(Color.Parse("#E4E5E0"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#8A9098"));
    private static readonly IBrush Fault = new SolidColorBrush(Color.Parse("#D23B2F"));
    private static readonly IBrush Selection = new SolidColorBrush(Color.Parse("#2F6FD6"));
    private readonly StreetLightDemo _demo;

    public NightStreet(StreetLightDemo demo)
    {
        _demo = demo;
        demo.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(InvalidateVisual);
        demo.PropertyChanged += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(InvalidateVisual);
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var x = e.GetPosition(this).X / Bounds.Width;
        _demo.Selected = _demo.Poles.OrderBy(p => Math.Abs(p.X - x)).FirstOrDefault();
    }

    private static FormattedText Label(string s, double size, IBrush brush, string face = "JetBrains Mono") =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(face), size, brush);

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height, roadTop = h * 0.72;
        ctx.DrawRectangle(Sky, null, new Rect(0, 0, w, h));
        ctx.DrawRectangle(Road, null, new Rect(0, roadTop, w, h - roadTop));
        ctx.DrawRectangle(Kerb, null, new Rect(0, roadTop - 4, w, 4));
        for (var x = 10.0; x < w; x += 46) ctx.DrawRectangle(Kerb, null, new Rect(x, roadTop + ((h - roadTop) / 2), 24, 3));
        ctx.DrawText(Label("JL. ASIA AFRIKA · BANDUNG", 11, Muted, "Barlow Condensed"), new Point(14, h - 20));

        foreach (var p in _demo.Poles)
        {
            var x = p.X * w;
            var head = new Point(x + 26, h * 0.22);
            var lit = p.Registered && p.On && p.Dimmer > 0;
            if (lit)
            {
                // Glow on the road and around the lamp, sized and brightened by the dimmer.
                var level = p.Dimmer / 100.0;
                var cone = new StreamGeometry();
                using (var g = cone.Open())
                {
                    g.BeginFigure(head + new Point(-6, 6), true);
                    g.LineTo(new Point(head.X - (40 + (60 * level)), roadTop + 30));
                    g.LineTo(new Point(head.X + 40 + (60 * level), roadTop + 30));
                    g.LineTo(head + new Point(6, 6));
                    g.EndFigure(true);
                }

                ctx.DrawGeometry(new SolidColorBrush(Color.FromArgb((byte)(25 + (55 * level)), 0xF2, 0xA9, 0x00)), null, cone);
                ctx.DrawEllipse(new RadialGradientBrush
                {
                    GradientStops = { new GradientStop(Color.FromArgb((byte)(150 + (100 * level)), 0xFF, 0xD2, 0x6A), 0), new GradientStop(Color.FromArgb(0, 0xF2, 0xA9, 0x00), 1) },
                }, null, head, 24 + (26 * level), 24 + (26 * level));
            }

            ctx.DrawRectangle(Pole, null, new Rect(x - 2, h * 0.2, 4, roadTop - (h * 0.2)));
            ctx.DrawLine(new Pen(Pole, 3), new Point(x, h * 0.2), head + new Point(-4, 0));
            ctx.DrawEllipse(lit ? new SolidColorBrush(Color.Parse("#FFE7A6")) : p.Registered ? Muted : Pole, null, head, 6, 4);
            if (_demo.Selected == p) ctx.DrawRectangle(null, new Pen(Selection, 1.5, new DashStyle([4, 3], 0)), new Rect(x - 64, h * 0.12, 126, roadTop - (h * 0.12) + 8), 6, 6);

            var state = !p.Registered ? "offline" : lit ? string.Create(CultureInfo.InvariantCulture, $"{p.Dimmer} %") : "off";
            ctx.DrawText(Label(p.Serial[^3..], 12, Text, "Barlow Condensed"), new Point(x - 58, roadTop - 54));
            ctx.DrawText(Label(state, 11, !p.Registered ? Fault : Text), new Point(x - 58, roadTop - 40));
            ctx.DrawText(Label(p.Registered ? string.Create(CultureInfo.InvariantCulture, $"{p.Temperature:0.0} °C") : p.Expires, 10, Muted), new Point(x - 58, roadTop - 26));
        }
    }
}
