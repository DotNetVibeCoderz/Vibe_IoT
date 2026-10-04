using Avalonia;
using Avalonia.Controls;
using IoTCom.Net.Gallery.Infrastructure;

namespace IoTCom.Net.Gallery.Controls;

/// <summary>Renders a frame as byte tiles coloured by field (the IoTCom.Net "frame lane").</summary>
public sealed class FrameLaneView : Decorator
{
    public static readonly StyledProperty<IReadOnlyList<FieldTiles>?> FieldsProperty =
        AvaloniaProperty.Register<FrameLaneView, IReadOnlyList<FieldTiles>?>(nameof(Fields));

    public static readonly StyledProperty<double> TileFontSizeProperty =
        AvaloniaProperty.Register<FrameLaneView, double>(nameof(TileFontSize), 11.5);

    public IReadOnlyList<FieldTiles>? Fields
    {
        get => GetValue(FieldsProperty);
        set => SetValue(FieldsProperty, value);
    }

    public double TileFontSize
    {
        get => GetValue(TileFontSizeProperty);
        set => SetValue(TileFontSizeProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FieldsProperty || change.Property == TileFontSizeProperty)
            Child = Fields is { Count: > 0 } f ? UiKit.FrameLane(f, TileFontSize) : null;
    }
}
