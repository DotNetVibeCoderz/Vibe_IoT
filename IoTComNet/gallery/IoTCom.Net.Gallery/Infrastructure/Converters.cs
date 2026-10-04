using Avalonia.Data.Converters;
using Avalonia.Media;

namespace IoTCom.Net.Gallery.Infrastructure;

/// <summary>Value converters used from XAML.</summary>
public static class Converters
{
    private static readonly IBrush Off = new SolidColorBrush(Color.Parse("#7C848C"));

    /// <summary>Running → green lamp, idle → grey lamp.</summary>
    public static readonly IValueConverter RunLamp = new FuncValueConverter<bool, IBrush>(on => on ? Palette.Green : Off);
}
