using Avalonia;

namespace IoTCom.Net.Gallery;

internal static class Program
{
    // IoTCom.Net Gallery — learn every protocol by running it. Built by Gravicode Studios, led by Kang Fadhil.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
