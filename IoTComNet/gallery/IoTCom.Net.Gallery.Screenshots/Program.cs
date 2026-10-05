// Renders the real Gallery window off-screen (Avalonia.Headless + Skia) and saves PNGs for README/docs.
// Usage: dotnet run --project gallery/IoTCom.Net.Gallery.Screenshots -- <output-dir>
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using IoTCom.Net.Gallery;
using IoTCom.Net.Gallery.Demos;
using IoTCom.Net.Gallery.Infrastructure;
using IoTCom.Net.Gallery.ViewModels;
using IoTCom.Net.Gallery.Views;

var outDir = Path.GetFullPath(args.Length > 0 ? args[0] : "docs/images");
Directory.CreateDirectory(outDir);

AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .SetupWithoutStarting();

var vm = new MainViewModel();
var window = new MainWindow { DataContext = vm, Width = 1400, Height = 880 };
window.Show();
Pump(TimeSpan.FromMilliseconds(300));

void Pump(TimeSpan duration)
{
    var until = DateTime.UtcNow + duration;
    while (DateTime.UtcNow < until)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Thread.Sleep(15);
    }
}

void Await(Task task)
{
    while (!task.IsCompleted) Pump(TimeSpan.FromMilliseconds(20));
    task.GetAwaiter().GetResult();
}

TabControl Tabs() => window.GetVisualDescendants().OfType<TabControl>().First();

void Shot(string name)
{
    Pump(TimeSpan.FromMilliseconds(400));
    using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame rendered.");
    var path = Path.Combine(outDir, name);
    frame.Save(path);
    Console.WriteLine($"saved {path}");
}

void Show(string id, int tab = 0, double seconds = 3, bool start = true)
{
    vm.Select(id);
    Pump(TimeSpan.FromMilliseconds(200));
    Tabs().SelectedIndex = tab;
    if (start && vm.SelectedDemo is { AlwaysOn: false, IsRunning: false }) Await(vm.ToggleRunCommand.ExecuteAsync(null));
    Pump(TimeSpan.FromSeconds(seconds));
}

Show("modbus-factory", seconds: 6);
Shot("gallery-modbus.png");
Tabs().SelectedIndex = 3;
Pump(TimeSpan.FromSeconds(1));
Shot("gallery-traffic.png");
Tabs().SelectedIndex = 1;
Shot("gallery-code.png");

Show("nmea-tracker", seconds: 22);
Shot("gallery-nmea.png");

Show("artnet-stage", seconds: 2);
Shot("gallery-lighting.png");

Show("mqtt-pubsub", seconds: 3.5);
Await(((MqttDemo)vm.SelectedDemo!).PublishAsync());
Pump(TimeSpan.FromSeconds(1));
Shot("gallery-mqtt.png");

Show("workbench", seconds: 0.5);
Shot("gallery-workbench.png");

// Medical demos (use the AI configured through IOTCOM_AI_* when present).
Show("hl7-icu", seconds: 40);
var icu = (BedsideMonitorDemo)vm.SelectedDemo!;
Await(icu.SummarizeAsync());
Pump(TimeSpan.FromSeconds(1));
Shot("gallery-hl7-icu.png");

Show("dicom-ai", seconds: 1);
var imaging = (ImagingDemo)vm.SelectedDemo!;
Await(imaging.AcquireAsync(IoTCom.Net.Adapters.Dicom.SyntheticModality.ChestXray, IoTCom.Net.Adapters.Dicom.SyntheticFinding.LungNodule));
Pump(TimeSpan.FromSeconds(1.5));
Await(imaging.AnalyzeAsync());
imaging.ShowTruth = true;
Pump(TimeSpan.FromSeconds(1));
Shot("gallery-dicom-ai.png");
Await(imaging.AcquireAsync(IoTCom.Net.Adapters.Dicom.SyntheticModality.BrainMr, IoTCom.Net.Adapters.Dicom.SyntheticFinding.Infarct));
Pump(TimeSpan.FromSeconds(1.5));
Await(imaging.AnalyzeAsync());
Pump(TimeSpan.FromSeconds(1));
Shot("gallery-dicom-ai-mri.png");

vm.ToggleThemeCommand.Execute(null);
Loc.Instance.Language = "id";
Show("modbus-factory", seconds: 1.5, start: false);
Shot("gallery-dark-id.png");

Await(vm.DisposeAsync().AsTask());
Console.WriteLine("done");
