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

public sealed partial class PlantNetworkDemo
{
    private static readonly IBrush DeadLamp = new SolidColorBrush(Color.Parse("#D23B2F"));
    private static readonly IBrush LiveLamp = new SolidColorBrush(Color.Parse("#2E9E5B"));
    private static readonly IBrush Rail = new SolidColorBrush(Color.Parse("#9AA0A6"));

    /// <summary>Message-type colours: births green, deaths red, commands amber, data blue.</summary>
    internal static IBrush KindBrush(string kind) => kind switch
    {
        "NBIRTH" or "DBIRTH" => Palette.Green,
        "NDEATH" or "DDEATH" => Palette.Red,
        "NCMD" or "DCMD" => Palette.Amber,
        _ => Palette.Blue,
    };

    private static IBrush TypeBrush(string type) => type switch
    {
        "_modbus._tcp" => Palette.Amber,
        "_mqtt._tcp" => Palette.Green,
        "_coap._udp" => Palette.Violet,
        "_http._tcp" => Palette.Blue,
        _ => Palette.Grey,
    };

    protected override Control CreateView()
    {
        var display = (FontFamily)Application.Current!.FindResource("DisplayFont")!;
        var mono = (FontFamily)Application.Current!.FindResource("MonoFont")!;

        // ---- on the network (mDNS) ---------------------------------------------------------------------------
        var discovered = new ItemsControl
        {
            ItemTemplate = new FuncDataTemplate<DiscoveredRow>((d, _) => new Border
            {
                Padding = new Thickness(0, 6), BorderBrush = Rail, BorderThickness = new Thickness(0, 0, 0, 1),
                Child = Columns("auto,10,*",
                    new Border { Padding = new Thickness(6, 2), CornerRadius = new CornerRadius(3), Background = TypeBrush(d.Type), VerticalAlignment = VerticalAlignment.Top,
                        Child = new TextBlock { Text = d.Type, FontFamily = mono, FontSize = 10.5, Foreground = d.Type == "_modbus._tcp" ? Palette.Ink : Brushes.White } },
                    new Border(),
                    Stack(1,
                        new TextBlock { Text = d.Instance, FontFamily = display, FontWeight = FontWeight.SemiBold, FontSize = 15 },
                        new TextBlock { Text = d.Endpoint, FontFamily = mono, FontSize = 11 },
                        new TextBlock { Text = d.Txt, FontFamily = mono, FontSize = 10.5, Classes = { "muted" }, TextTrimming = TextTrimming.CharacterEllipsis })),
            }),
        };
        discovered.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(Discovered)));
        Button Act(string text, Func<Task> act, string cls = "ghost")
        {
            var b = new Button { Content = text, Classes = { cls } };
            b.Click += async (_, _) => await act();
            b.Bind(InputElement.IsEnabledProperty, new Binding(nameof(IsRunning)));
            return b;
        }

        var networkCard = Card(Stack(10, discovered,
            Act(L("Unplug / plug the label printer", "Cabut / pasang printer label"), TogglePrinterAsync),
            new TextBlock { Text = L("Browsing _modbus._tcp, _mqtt._tcp, _coap._udp, _http._tcp and _ipp._tcp on 224.0.0.251:5353.", "Menjelajah _modbus._tcp, _mqtt._tcp, _coap._udp, _http._tcp, dan _ipp._tcp di 224.0.0.251:5353."), Classes = { "muted" }, FontSize = 11.5, TextWrapping = TextWrapping.Wrap }),
            L("On the network · mDNS / DNS-SD", "Di jaringan · mDNS / DNS-SD"));

        // ---- the Unified Namespace: one terminal strip per node / device ------------------------------------------
        var uns = new ItemsControl
        {
            ItemTemplate = new FuncDataTemplate<UnsRow>((r, _) =>
            {
                var terminals = new WrapPanel { ItemSpacing = 6, LineSpacing = 6 };
                foreach (var m in r.Metrics)
                {
                    terminals.Children.Add(new Border
                    {
                        MinWidth = 92, Padding = new Thickness(8, 4), CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1), BorderBrush = Rail,
                        Opacity = r.Online ? 1 : 0.45,
                        Child = Stack(0,
                            new TextBlock { Text = m.Name, FontSize = 10.5, Classes = { "muted" }, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 150 },
                            new TextBlock { Text = m.Value, FontFamily = mono, FontWeight = FontWeight.Bold, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 150 }),
                    }.With(b => ToolTip.SetTip(b, m.Type)));
                }

                return new Border
                {
                    Margin = new Thickness(r.IsDevice ? 26 : 0, 0, 0, 8), Padding = new Thickness(10, 8), CornerRadius = new CornerRadius(6),
                    BorderThickness = new Thickness(4, 1, 1, 1), BorderBrush = r.Online ? LiveLamp : DeadLamp,
                    Child = Columns("auto,10,130,10,*",
                        new Ellipse { Width = 14, Height = 14, Fill = r.Online ? LiveLamp : DeadLamp, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 0, 0) },
                        new Border(),
                        Stack(0,
                            new TextBlock { Text = r.Name, FontFamily = display, FontWeight = FontWeight.Bold, FontSize = 17 },
                            new TextBlock { Text = r.Online ? (r.IsDevice ? "DBIRTH" : "NBIRTH") : (r.IsDevice ? "DDEATH" : "NDEATH"), FontFamily = mono, FontSize = 10.5, Foreground = r.Online ? LiveLamp : DeadLamp }),
                        new Border(),
                        terminals),
                };
            }),
        };
        uns.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(Namespace)));

        var cable = new Button { Classes = { "primary" } };
        cable.Bind(ContentControl.ContentProperty, new Binding(nameof(CableIn)) { Converter = new Avalonia.Data.Converters.FuncValueConverter<bool, string>(on => on ? L("Pull the network cable", "Cabut kabel jaringan") : L("Plug the cable back in", "Pasang lagi kabelnya")) });
        cable.Click += async (_, _) => await ToggleCableAsync();
        cable.Bind(InputElement.IsEnabledProperty, new Binding(nameof(IsRunning)));
        var writes = new CheckBox { Content = L("Edge node accepts writes", "Edge node menerima penulisan") };
        writes.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(AcceptWrites)));
        var lastAction = new TextBlock { FontFamily = mono, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Classes = { "muted" } };
        lastAction.Bind(TextBlock.TextProperty, new Binding(nameof(LastAction)));
        var session = new TextBlock { FontFamily = mono, FontSize = 11.5, Classes = { "muted" } };
        session.Bind(TextBlock.TextProperty, new MultiBinding
        {
            Bindings = { new Binding(nameof(BdSeq)), new Binding(nameof(Rebirths)) },
            Converter = new Avalonia.Data.Converters.FuncMultiValueConverter<object?, string>(v => { var a = v.ToArray(); return $"bdSeq {a[0]} · {L("rebirth requests", "permintaan rebirth")} {a[1]}"; }),
        });

        var unsCard = Card(Stack(12,
            new TextBlock { Text = "spBv1.0 / Plant / Line1", FontFamily = mono, FontSize = 12, Classes = { "muted" } },
            uns,
            new WrapPanel
            {
                ItemSpacing = 8, LineSpacing = 6,
                Children =
                {
                    cable,
                    Act(L("Stop / start the filler", "Hentikan / jalankan filler"), () => WriteAsync("Filler", "Running", !Current("Filler", "Running"))),
                    Act(L("Open / close the inlet valve", "Buka / tutup katup masuk"), () => WriteAsync("Tank7", "InletValve", !Current("Tank7", "InletValve"))),
                    Act(L("Ask for rebirth", "Minta rebirth"), RebirthAsync),
                },
            },
            Row(16, writes, session),
            lastAction), L("Unified Namespace · Sparkplug B host application", "Unified Namespace · host application Sparkplug B"));

        // ---- the wire: Sparkplug messages ---------------------------------------------------------------------
        var log = new ItemsControl
        {
            ItemTemplate = new FuncDataTemplate<SparkplugLogRow>((l, _) => Columns("64,70,150,*",
                new TextBlock { Text = l.Time, FontFamily = mono, FontSize = 11, Classes = { "muted" } },
                new Border { Padding = new Thickness(4, 1), CornerRadius = new CornerRadius(2), Background = KindBrush(l.Kind), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 1),
                    Child = new TextBlock { Text = l.Kind, FontFamily = mono, FontSize = 10.5, FontWeight = FontWeight.Bold, Foreground = l.Kind is "NCMD" or "DCMD" ? Palette.Ink : Brushes.White } },
                new TextBlock { Text = l.Topic, FontFamily = mono, FontSize = 11 },
                new TextBlock { Text = l.Detail, FontFamily = mono, FontSize = 11, Classes = { "muted" }, TextTrimming = TextTrimming.CharacterEllipsis })),
        };
        log.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(WireLog)));
        var logCard = Card(log, L("On the wire · spBv1.0/# (Protobuf payloads)", "Di jalur · spBv1.0/# (payload Protobuf)"));

        var status = new TextBlock { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };
        status.Bind(TextBlock.TextProperty, new Binding(nameof(Status)));

        return new ScrollViewer
        {
            DataContext = this,
            Content = Stack(16,
                Columns("0.8*,16,1.6*", networkCard, new Border(), unsCard),
                logCard,
                status),
        };
    }
}
