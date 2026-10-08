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

public sealed partial class OpcUaDemo
{
    private static readonly IBrush Selection = new SolidColorBrush(Color.Parse("#F2A900"), 0.22);
    private static readonly IBrush AlarmOn = new SolidColorBrush(Color.Parse("#D23B2F"));
    private static readonly IBrush AlarmOff = new SolidColorBrush(Color.Parse("#7C848C"));

    protected override Control CreateView()
    {
        var display = (FontFamily)Application.Current!.FindResource("DisplayFont")!;
        var mono = (FontFamily)Application.Current!.FindResource("MonoFont")!;

        // ---- address space: an indented tag list, the way engineering tools show it ---------------------------------
        var tree = new ItemsControl
        {
            ItemTemplate = new FuncDataTemplate<TagRow>((t, _) =>
            {
                var value = new TextBlock { FontFamily = mono, FontSize = 12, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Right };
                value.Bind(TextBlock.TextProperty, new Binding(nameof(TagRow.Value)) { Source = t });
                var row = new Border
                {
                    Padding = new Thickness(6 + (t.Depth * 16), 3, 6, 3), CornerRadius = new CornerRadius(3), Cursor = new Cursor(StandardCursorType.Hand), Background = Brushes.Transparent,
                    Child = Columns("16,*,auto",
                        new TextBlock { Text = t.Glyph, FontSize = 11, Foreground = t.NodeClass switch { "Variable" => Palette.Blue, "Method" => Palette.Amber, _ => Palette.Grey }, VerticalAlignment = VerticalAlignment.Center },
                        new TextBlock { Text = t.Name, FontFamily = t.NodeClass == "Object" ? display : mono, FontWeight = t.NodeClass == "Object" ? FontWeight.SemiBold : FontWeight.Normal, FontSize = t.NodeClass == "Object" ? 14 : 12, VerticalAlignment = VerticalAlignment.Center },
                        t.NodeClass == "Variable" ? value : new Border()),
                };
                row.Bind(Border.BackgroundProperty, new Binding(nameof(Selected)) { Source = this, Converter = new FuncValueConverter<TagRow?, IBrush>(s => ReferenceEquals(s, t) ? Selection : Brushes.Transparent) });
                row.PointerPressed += (_, _) => { if (t.NodeClass == "Variable") Selected = t; };
                return row;
            }),
        };
        tree.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(Tags)));
        var notifications = new TextBlock { FontFamily = mono, FontSize = 11, Classes = { "muted" } };
        notifications.Bind(TextBlock.TextProperty, new Binding(nameof(Notifications)) { StringFormat = L("{0} data-change notifications", "{0} notifikasi perubahan data") });
        var treeCard = Card(Stack(8, tree, notifications), L("Address space · Objects / Plant", "Address space · Objects / Plant"));

        // ---- the selected tag ---------------------------------------------------------------------------------
        TextBlock Bound(string path, double size, FontFamily family, FontWeight weight = FontWeight.Normal, bool muted = false)
        {
            var t = new TextBlock { FontFamily = family, FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap };
            if (muted) t.Classes.Add("muted");
            t.Bind(TextBlock.TextProperty, new Binding($"{nameof(Selected)}.{path}"));
            return t;
        }

        var trendHost = new ContentControl { Height = 110 };
        void Retrend() => trendHost.Content = Sparkline(SelectedTrend, Palette.Blue, 110);
        Retrend();
        TrendChanged += () => Ui(Retrend);
        var tagCard = Card(Stack(6,
            Bound(nameof(TagRow.Name), 30, display, FontWeight.Bold),
            Bound(nameof(TagRow.NodeId), 11.5, mono, muted: true),
            Bound(nameof(TagRow.Value), 52, display, FontWeight.Bold),
            Row(10, Bound(nameof(TagRow.Status), 12, mono), Bound(nameof(TagRow.Time), 12, mono, muted: true)),
            trendHost), L("Selected tag · live from the subscription", "Tag terpilih · langsung dari subscription"));

        // ---- writes behind a guard ------------------------------------------------------------------------------
        var allow = new CheckBox { Content = L("Allow writes (opens a second, writable session)", "Izinkan penulisan (membuka sesi kedua yang bisa menulis)") };
        allow.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(AllowWrite)));
        Button Act(string text, Func<Task> act, string cls = "ghost")
        {
            var b = new Button { Content = text, Classes = { cls } };
            b.Click += async (_, _) => await act();
            b.Bind(InputElement.IsEnabledProperty, new Binding(nameof(IsRunning)));
            return b;
        }

        var alarmLamp = new Ellipse { Width = 16, Height = 16 };
        alarmLamp.Bind(Shape.FillProperty, new Binding(nameof(HighLevel)) { Converter = new FuncValueConverter<bool, IBrush>(on => on ? AlarmOn : AlarmOff) });
        var lastAction = new TextBlock { FontFamily = mono, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Classes = { "muted" } };
        lastAction.Bind(TextBlock.TextProperty, new Binding(nameof(LastAction)));
        var session = new TextBlock { FontFamily = mono, FontSize = 11.5, TextWrapping = TextWrapping.Wrap };
        session.Bind(TextBlock.TextProperty, new Binding(nameof(Session)));
        var controlCard = Card(Stack(10,
            session,
            allow,
            new WrapPanel
            {
                ItemSpacing = 8, LineSpacing = 6,
                Children =
                {
                    Act(L("Stop / start filler", "Hentikan / jalankan filler"), ToggleFillerAsync, "primary"),
                    Act(L("Setpoint −10", "Setpoint −10"), () => NudgeSetpointAsync(-10)),
                    Act(L("Setpoint +10", "Setpoint +10"), () => NudgeSetpointAsync(10)),
                    Act(L("Open / close inlet valve", "Buka / tutup katup masuk"), ToggleValveAsync),
                    Act(L("Call ResetCounter()", "Panggil ResetCounter()"), ResetCounterAsync),
                },
            },
            Row(8, alarmLamp, new TextBlock { Text = L("Tank 7 high-level alarm (> 90 %)", "Alarm level tinggi Tangki 7 (> 90 %)"), VerticalAlignment = VerticalAlignment.Center }),
            lastAction), L("Session and writes", "Sesi dan penulisan"));

        var status = new TextBlock { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };
        status.Bind(TextBlock.TextProperty, new Binding(nameof(Status)));

        return new ScrollViewer
        {
            DataContext = this,
            Content = Stack(16,
                Columns("1*,16,1.2*", treeCard, new Border(), Stack(16, tagCard, controlCard)),
                status),
        };
    }
}
