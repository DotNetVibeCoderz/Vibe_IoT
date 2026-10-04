using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using IoTCom.Net.Gallery.ViewModels;
using IoTCom.Net.Gallery.Views;

namespace IoTCom.Net.Gallery;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainViewModel();
            desktop.MainWindow = new MainWindow { DataContext = vm };
            desktop.ShutdownRequested += async (_, _) => await vm.DisposeAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
