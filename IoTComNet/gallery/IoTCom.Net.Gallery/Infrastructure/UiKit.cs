using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;

namespace IoTCom.Net.Gallery.Infrastructure;

/// <summary>Builders for the Gallery's industrial-panel components (cards, readouts, lamps, frame lanes).</summary>
public static class UiKit
{
    private static readonly IBrush OffLamp = new SolidColorBrush(Color.Parse("#7C848C"));

    public static TextBlock Eyebrow(string text) => new() { Text = text.ToUpperInvariant(), Classes = { "eyebrow" } };

    public static TextBlock EyebrowLoc(string key)
    {
        var t = new TextBlock { Classes = { "eyebrow" } };
        t.Bind(TextBlock.TextProperty, new Binding($"[{key}]") { Source = Loc.Instance });
        return t;
    }

    public static Border Card(Control content, string? eyebrow = null, double padding = 18)
    {
        var body = eyebrow is null ? content : new StackPanel { Spacing = 10, Children = { Eyebrow(eyebrow), content } };
        return new Border { Classes = { "card" }, Padding = new Thickness(padding, padding - 2), Child = body };
    }

    /// <summary>Big numeral with unit, bound to <paramref name="path"/>.</summary>
    public static Control Readout(string label, string path, string unit, string format = "{0:0.0}", double size = 56)
    {
        var value = new TextBlock { Classes = { "readout" }, FontSize = size, VerticalAlignment = VerticalAlignment.Bottom };
        value.Bind(TextBlock.TextProperty, new Binding(path) { StringFormat = format });
        return new StackPanel
        {
            Spacing = 2,
            Children =
            {
                Eyebrow(label),
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 6,
                    Children = { value, new TextBlock { Text = unit, FontSize = Math.Max(14, size * 0.36), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, size * 0.12), Classes = { "muted" } } },
                },
            },
        };
    }

    /// <summary>Small labelled value cell.</summary>
    public static Control Cell(string label, string path, string format, string unit = "")
    {
        var value = new TextBlock { Classes = { "value" } };
        value.Bind(TextBlock.TextProperty, new Binding(path) { StringFormat = format + (unit.Length > 0 ? " " + unit : "") });
        return new StackPanel { Spacing = 2, Children = { Eyebrow(label), value } };
    }

    /// <summary>Signal lamp bound to a boolean (on = <paramref name="onClass"/>).</summary>
    public static Control Lamp(string label, string path, string onClass = "on")
    {
        var onBrush = onClass switch { "warn" => Palette.Amber, "fault" => Palette.Red, _ => Palette.Green };
        var lamp = new Ellipse { Classes = { "lamp" } };
        lamp.Bind(Shape.FillProperty, new Binding(path) { Converter = new FuncValueConverter<bool, IBrush>(on => on ? onBrush : OffLamp) });
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { lamp, new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center } } };
    }

    public static Control Chip(string text) => new Border { Classes = { "chip" }, Child = new TextBlock { Text = text } };

    /// <summary>The signature frame lane: tiles per byte, coloured per field.</summary>
    public static Control FrameLane(IReadOnlyList<FieldTiles> fields, double tileFont = 11.5)
    {
        var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var f in fields)
        {
            var group = new WrapPanel { Margin = new Thickness(0, 0, 4, 3) };
            ToolTip.SetTip(group, f.Tooltip);
            foreach (var b in f.Bytes)
            {
                group.Children.Add(new Border
                {
                    Classes = { "byte" },
                    Background = f.Background,
                    Child = new TextBlock { Text = b, Foreground = f.Foreground, FontSize = tileFont },
                });
            }
            wrap.Children.Add(group);
        }
        return wrap;
    }

    public static Grid Columns(string definition, params Control[] children)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions(definition) };
        for (var i = 0; i < children.Length; i++)
        {
            Grid.SetColumn(children[i], i);
            g.Children.Add(children[i]);
        }
        return g;
    }

    public static StackPanel Stack(double spacing, params Control[] children)
    {
        var s = new StackPanel { Spacing = spacing };
        foreach (var c in children) s.Children.Add(c);
        return s;
    }

    public static StackPanel Row(double spacing, params Control[] children)
    {
        var s = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing };
        foreach (var c in children) s.Children.Add(c);
        return s;
    }

    /// <summary>A sparkline that redraws whenever <paramref name="source"/> raises <c>Changed</c>.</summary>
    public static Control Sparkline(TrendBuffer source, IBrush stroke, double height = 90)
    {
        var line = new Polyline { Stroke = stroke, StrokeThickness = 2, StrokeJoin = PenLineJoin.Round };
        var canvas = new Canvas { Height = height, ClipToBounds = true, Children = { line } };
        void Redraw()
        {
            var values = source.Snapshot();
            var w = canvas.Bounds.Width;
            if (values.Length < 2 || w <= 0) return;
            var min = values.Min();
            var max = values.Max();
            if (max - min < source.MinimumSpan) { var mid = (max + min) / 2; min = mid - source.MinimumSpan / 2; max = mid + source.MinimumSpan / 2; }
            var pts = new List<Point>(values.Length);
            for (var i = 0; i < values.Length; i++)
                pts.Add(new Point(i * w / (source.Capacity - 1), height - 4 - (values[i] - min) / (max - min) * (height - 8)));
            line.Points = pts;
        }
        source.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(Redraw);
        canvas.SizeChanged += (_, _) => Redraw();
        return canvas;
    }
}

/// <summary>Fixed-size history used by sparklines.</summary>
public sealed class TrendBuffer(int capacity = 120, double minimumSpan = 1)
{
    private readonly Queue<double> _values = new();
    private readonly Lock _gate = new();

    public int Capacity { get; } = capacity;
    public double MinimumSpan { get; } = minimumSpan;
    public event Action? Changed;

    public void Add(double v)
    {
        lock (_gate)
        {
            _values.Enqueue(v);
            while (_values.Count > Capacity) _values.Dequeue();
        }
        Changed?.Invoke();
    }

    public double[] Snapshot()
    {
        lock (_gate) return [.. _values];
    }

    public void Clear()
    {
        lock (_gate) _values.Clear();
        Changed?.Invoke();
    }
}
