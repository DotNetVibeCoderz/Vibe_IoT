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
using IoTCom.Net.Protocols.Nfc;
using static IoTCom.Net.Gallery.Infrastructure.Loc;
using static IoTCom.Net.Gallery.Infrastructure.UiKit;

namespace IoTCom.Net.Gallery.Demos;

public sealed partial class NfcDemo
{
    protected override Control CreateView()
    {
        var mono = (FontFamily)Application.Current!.FindResource("MonoFont")!;

        var tags = new ItemsControl
        {
            ItemTemplate = new FuncDataTemplate<VirtualType2Tag>((tag, _) =>
            {
                if (tag is null) return new Border();
                var b = new Button
                {
                    Classes = { "ghost" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8),
                    Content = Stack(2, new TextBlock { Text = tag.Label, FontWeight = FontWeight.SemiBold },
                        new TextBlock { Text = $"NTAG213 · UID {Convert.ToHexString(tag.Uid)}", FontFamily = mono, FontSize = 10.5, Classes = { "muted" } }),
                };
                b.Click += async (_, _) => await TapAsync(tag);
                return b;
            }),
        };
        tags.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(Tags)));

        var unlock = new ToggleSwitch { OnContent = L("Writing allowed", "Penulisan diizinkan"), OffContent = L("Writing off (read-only)", "Penulisan mati (hanya-baca)") };
        unlock.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(WritesAllowed)) { Mode = BindingMode.TwoWay });
        var service = new Button { Content = L("Log a service visit on the tag", "Catat kunjungan servis di tag"), Classes = { "primary" } };
        service.Click += async (_, _) => await LogServiceAsync();
        service.Bind(InputElement.IsEnabledProperty, new Binding(nameof(IsRunning)));
        var log = new ItemsControl { ItemTemplate = new FuncDataTemplate<string>((t, _) => new TextBlock { Text = t, FontFamily = mono, FontSize = 10.5, TextWrapping = TextWrapping.Wrap }) };
        log.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(Log)));
        var round = Card(Stack(10, tags, unlock, service, log), L("Maintenance round — tap a tag", "Ronde perawatan — tempelkan tag"));

        TextBlock Bound(string path, double size, FontFamily? font = null)
        {
            var t = new TextBlock { FontSize = size, TextWrapping = TextWrapping.Wrap };
            if (font is not null) t.FontFamily = font;
            t.Bind(TextBlock.TextProperty, new Binding(path));
            return t;
        }

        var records = new ItemsControl { ItemTemplate = new FuncDataTemplate<string>((t, _) => new Border { Padding = new Thickness(0, 4), BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = Palette.Data, Child = new TextBlock { Text = t, TextWrapping = TextWrapping.Wrap, FontSize = 12.5 } }) };
        records.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(Records)));
        var ndefLane = new ContentControl();
        Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(() => ndefLane.Content = Ndef.Length == 0 ? null : FrameLane(FrameRow.Tiles(Ndef, NdefMessage.Describe(Ndef), ascii: false), 10.5));
        var onReader = Card(Stack(8,
            Row(8, new TextBlock { Text = "UID", Classes = { "muted" }, FontSize = 11 }, Bound(nameof(Uid), 13, mono)),
            Bound(nameof(Capability), 11, mono), Bound(nameof(Usage), 11, mono),
            records, ndefLane), L("On the reader", "Di pembaca"));

        var map = Card(new PageMap(this) { Height = 470 }, L("NTAG213 memory, page by page", "Memori NTAG213, halaman demi halaman"));
        var status = new TextBlock { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };
        status.Bind(TextBlock.TextProperty, new Binding(nameof(Status)));
        return new ScrollViewer
        {
            DataContext = this,
            Content = Stack(16, Columns("*,16,1.25*,16,1.1*", round, new Border(), onReader, new Border(), map), status),
        };
    }
}

/// <summary>The tag's memory as rows of four bytes, coloured by region (the frame-lane palette).</summary>
internal sealed class PageMap : Control
{
    private static readonly IBrush Free = new SolidColorBrush(Color.Parse("#F4F4F1"));
    private readonly NfcDemo _demo;

    public PageMap(NfcDemo demo)
    {
        _demo = demo;
        demo.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(InvalidateVisual);
    }

    private static (IBrush Fill, IBrush Ink) Colours(Type2Region r) => r switch
    {
        Type2Region.Uid => (Palette.Blue, Palette.White),
        Type2Region.Lock => (Palette.Red, Palette.White),
        Type2Region.CapabilityContainer => (Palette.Violet, Palette.White),
        Type2Region.TlvHeader => (Palette.Amber, Palette.Ink),
        Type2Region.Ndef => (Palette.Data, Palette.Ink),
        Type2Region.Terminator => (Palette.Green, Palette.White),
        Type2Region.Configuration => (Palette.Grey, Palette.White),
        _ => (Free, Palette.Grey),
    };

    private static FormattedText Text(string s, double size, IBrush brush) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("JetBrains Mono"), size, brush);

    public override void Render(DrawingContext ctx)
    {
        if (_demo.Memory is not { } m) return;
        var (memory, map) = m;
        var pages = memory.Length / 4;
        const int rows = 23;
        var columns = (pages + rows - 1) / rows;
        var colWidth = Bounds.Width / columns;
        var cell = Math.Min(24, (colWidth - 30) / 4);
        var rowHeight = Math.Min(17, (Bounds.Height - 70) / rows);
        for (var page = 0; page < pages; page++)
        {
            double x = (page / rows) * colWidth, y = (page % rows) * rowHeight;
            ctx.DrawText(Text(page.ToString(CultureInfo.InvariantCulture), 9.5, Palette.Grey), new Point(x, y + 2));
            for (var i = 0; i < 4; i++)
            {
                var index = (page * 4) + i;
                var (fill, ink) = Colours(map[index]);
                var r = new Rect(x + 24 + (i * cell), y, cell - 2, rowHeight - 2);
                ctx.DrawRectangle(fill, null, r, 2, 2);
                var hex = Text(memory[index].ToString("X2", CultureInfo.InvariantCulture), 9, ink);
                ctx.DrawText(hex, new Point(r.X + ((r.Width - hex.Width) / 2), r.Y + ((r.Height - hex.Height) / 2)));
            }
        }

        // Legend.
        var legend = new[] { (Type2Region.Uid, "UID"), (Type2Region.Lock, "lock"), (Type2Region.CapabilityContainer, "CC"), (Type2Region.TlvHeader, "TLV"),
            (Type2Region.Ndef, "NDEF"), (Type2Region.Terminator, "end"), (Type2Region.Free, "free"), (Type2Region.Configuration, "config") };
        double lx = 0, ly = (rows * rowHeight) + 14;
        foreach (var (region, label) in legend)
        {
            var t = Text(label, 10, Palette.Grey);
            if (lx + 16 + t.Width > Bounds.Width)
            {
                lx = 0;
                ly += 18;
            }

            ctx.DrawRectangle(Colours(region).Fill, null, new Rect(lx, ly, 12, 12), 2, 2);
            ctx.DrawText(t, new Point(lx + 16, ly - 1));
            lx += 26 + t.Width;
        }
    }
}
