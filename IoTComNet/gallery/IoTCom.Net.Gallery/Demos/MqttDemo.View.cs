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

public sealed partial class MqttDemo
{
    protected override Control CreateView()
    {
        TextBox Box(string path, string watermark)
        {
            var t = new TextBox { Watermark = watermark };
            t.Bind(TextBox.TextProperty, new Binding(path));
            return t;
        }

        var publish = new Button { Content = "PUBLISH", Classes = { "primary" } };
        publish.Click += async (_, _) => await PublishAsync();
        publish.Bind(InputElement.IsEnabledProperty, new Binding(nameof(IsRunning)));
        var retain = new CheckBox { Content = "Retain" };
        retain.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(Retain)));

        var subscribe = new Button { Content = "SUBSCRIBE", Classes = { "ghost" } };
        subscribe.Click += async (_, _) => await SubscribeAsync();
        subscribe.Bind(InputElement.IsEnabledProperty, new Binding(nameof(IsRunning)));

        var publisher = Card(Stack(10,
            Eyebrow(L("Publisher", "Penerbit")),
            Box(nameof(Topic), "Topic"),
            Box(nameof(Payload), "Payload"),
            Row(12, publish, retain)));
        var subscriber = Card(Stack(10,
            Eyebrow(L("Subscriber filter", "Filter subscriber")),
            Box(nameof(Filter), "plant/#"),
            new TextBlock { Text = L("+ matches one level · # matches the rest", "+ cocok satu level · # cocok sisanya"), Classes = { "muted" }, FontSize = 12 },
            subscribe));

        var log = new ItemsControl
        {
            ItemTemplate = new FuncDataTemplate<MqttLogItem>((m, _) =>
            {
                var row = new Border
                {
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Padding = new Thickness(0, 7),
                    Child = Stack(3,
                        Row(10, new TextBlock { Text = m?.Time, Classes = { "mono", "muted" } }, new TextBlock { Text = m?.Topic, Foreground = Palette.Blue, FontWeight = FontWeight.SemiBold }),
                        new TextBlock { Text = m?.Payload, Classes = { "mono" }, TextWrapping = TextWrapping.Wrap, MaxHeight = 60 }),
                };
                row.Bind(Border.BorderBrushProperty, row.GetResourceObservable("Rule"));
                return row;
            }),
        };
        log.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(Messages)));
        var status = new TextBlock { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };
        status.Bind(TextBlock.TextProperty, new Binding(nameof(Status)));

        var left = Stack(16, publisher, subscriber, status);
        var right = Card(Stack(10, Eyebrow(L("Messages received", "Pesan diterima")), new ScrollViewer { MaxHeight = 520, Content = log }));
        return new ScrollViewer { DataContext = this, Content = Columns("*,16,1.4*", left, new Border(), right) };
    }
}
