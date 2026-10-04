using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using IoTCom.Net.Gallery.Infrastructure;
using static IoTCom.Net.Gallery.Infrastructure.Loc;
using static IoTCom.Net.Gallery.Infrastructure.UiKit;

namespace IoTCom.Net.Gallery.Demos;

public sealed partial class WorkbenchDemo
{
    protected override Control CreateView()
    {
        var input = new TextBox { Classes = { "mono" }, FontSize = 18, AcceptsReturn = false, Watermark = "01 03 00 00 00 0A C5 CD" };
        input.Bind(TextBox.TextProperty, new Binding(nameof(Input)) { Mode = BindingMode.TwoWay });
        var mode = new ComboBox { ItemsSource = new[] { "Modbus TCP", "Modbus RTU" }, MinWidth = 150 };
        mode.Bind(SelectingItemsControl.SelectedIndexProperty, new Binding(nameof(ModeIndex)));
        var response = new CheckBox { Content = "Response" };
        response.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(IsResponse)));

        var samples = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var (label, hex, m, resp) in new[]
        {
            ("Read holding (RTU)", "11 03 00 6B 00 03 76 87", 1, false),
            ("Response (RTU)", "11 03 06 02 2B 00 00 00 64 05 7A", 1, true),
            ("Exception (RTU)", "01 83 02 C0 F1", 1, true),
            ("Write coil (TCP)", "12 34 00 00 00 06 FF 05 00 AC FF 00", 0, false),
        })
        {
            var b = new Button { Content = label, Classes = { "ghost" }, FontSize = 12 };
            b.Click += (_, _) => { ModeIndex = m; IsResponse = resp; Input = hex; };
            samples.Children.Add(b);
        }

        var lane = new ContentControl();
        lane.Bind(ContentControl.ContentProperty, new Binding(nameof(Lane)) { Converter = new Avalonia.Data.Converters.FuncValueConverter<IReadOnlyList<FieldTiles>, Control>(f => FrameLane(f ?? [], 16)) });
        var verdict = new TextBlock { Classes = { "value" }, FontSize = 20, TextWrapping = TextWrapping.Wrap };
        verdict.Bind(TextBlock.TextProperty, new Binding(nameof(Verdict)));
        verdict.Bind(TextBlock.ForegroundProperty, new Binding(nameof(Valid)) { Converter = new Avalonia.Data.Converters.FuncValueConverter<bool, IBrush>(v => v ? Palette.Green : Palette.Red) });

        var fields = new ItemsControl
        {
            ItemTemplate = new FuncDataTemplate<FrameField>((f, _) =>
            {
                var (bg, _) = Palette.ForKind(f.Kind);
                return Columns("16,140,*",
                    new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Background = bg, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = f.Name, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = f.Value ?? "", Classes = { "muted" } });
            }),
        };
        fields.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(Fields)));

        var crcs = new ItemsControl
        {
            ItemTemplate = new FuncDataTemplate<CrcRow>((c, _) => Columns("*,Auto,64",
                new TextBlock { Text = c?.Name },
                new TextBlock { Text = c?.Value, Classes = { "mono" }, Foreground = Palette.Green, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = c?.Width, Classes = { "muted" }, HorizontalAlignment = HorizontalAlignment.Right })),
        };
        crcs.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(Crcs)));

        Control Encoded(string label, string path)
        {
            var t = new SelectableTextBlock { Classes = { "mono" }, TextWrapping = TextWrapping.Wrap };
            t.Bind(TextBlock.TextProperty, new Binding(path));
            return Stack(3, Eyebrow(label), t);
        }

        var decode = Card(Stack(14,
            Eyebrow("Bytes (hex)"), input, Row(12, mode, response), samples,
            lane, verdict, fields));
        var framing = Card(Stack(12, Encoded("SLIP (RFC 1055)", nameof(Slip)), Encoded("COBS", nameof(Cobs)), Encoded("HDLC + FCS-16", nameof(Hdlc))));
        var crcCard = Card(Stack(8, Eyebrow(L("CRC catalogue", "Katalog CRC")), crcs));

        return new ScrollViewer
        {
            DataContext = this,
            Content = Columns("1.4*,16,*", Stack(16, decode, framing), new Border(), crcCard),
        };
    }
}
