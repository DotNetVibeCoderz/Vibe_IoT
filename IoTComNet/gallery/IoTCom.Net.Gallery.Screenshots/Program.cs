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
// Optional: further arguments name the screenshots to render (e.g. gallery-can-uds.png); default renders all.
var only = args.Skip(1).ToHashSet(StringComparer.OrdinalIgnoreCase);
bool Want(params string[] names) => only.Count == 0 || names.Any(only.Contains);
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

if (Want("gallery-modbus.png", "gallery-traffic.png", "gallery-code.png"))
{
    Show("modbus-factory", seconds: 6);
    Shot("gallery-modbus.png");
    Tabs().SelectedIndex = 3;
    Pump(TimeSpan.FromSeconds(1));
    Shot("gallery-traffic.png");
    Tabs().SelectedIndex = 1;
    Shot("gallery-code.png");
}

if (Want("gallery-can-uds.png", "gallery-can-traffic.png"))
{
    // Vehicle diagnostics: unlock, inject a cooling fault, let the coolant climb past 105 °C.
    Show("can-uds", seconds: 3);
    var vehicle = (VehicleDiagnosticsDemo)vm.SelectedDemo!;
    Await(vehicle.UnlockAsync());
    vehicle.InjectOverheating(true);
    Pump(TimeSpan.FromSeconds(15));
    Await(vehicle.ReadDtcsAsync());
    Shot("gallery-can-uds.png");
    Tabs().SelectedIndex = 3;
    Pump(TimeSpan.FromSeconds(1));
    Shot("gallery-can-traffic.png");
    Tabs().SelectedIndex = 0;
}

if (Want("gallery-mavlink.png"))
{
    // MAVLink drone: arm, take off, let it fly part of the survey circuit.
    Show("mavlink-drone", seconds: 2);
    var drone = (DroneDemo)vm.SelectedDemo!;
    drone.AllowCommands = true;
    Await(drone.ArmAsync(true));
    Await(drone.TakeoffAsync());
    Pump(TimeSpan.FromSeconds(24));
    Shot("gallery-mavlink.png");
}

if (Want("gallery-coap.png"))
{
    // CoAP greenhouse: 20 % loss so the chart shows dropped datagrams and retransmissions.
    Show("coap-greenhouse", seconds: 2);
    var greenhouse = (GreenhouseDemo)vm.SelectedDemo!;
    greenhouse.LossPercent = 20;
    Await(greenhouse.SetFanAsync(true));
    Await(greenhouse.SetValveAsync(60));
    Await(greenhouse.FetchLogAsync());
    Pump(TimeSpan.FromSeconds(6));
    Shot("gallery-coap.png");
}

if (Want("gallery-lorawan.png"))
{
    // LoRaWAN: joins take 5 s; the water meter is moved to the edge so it climbs to a slow spreading factor.
    Show("lorawan-network", seconds: 9);
    var lorawan = (LoRaWanDemo)vm.SelectedDemo!;
    lorawan.MoveDevice("water-12", 7.2, 3.6);
    lorawan.SelectedDevice = "water-12";
    lorawan.SetInterval(10);
    Pump(TimeSpan.FromSeconds(16));
    Shot("gallery-lorawan.png");
}

if (Want("gallery-metering.png"))
{
    // Smart metering: wait for the first register, profile and M-Bus reads, then catch the LCD mid-cycle.
    Show("smart-metering", seconds: 8);
    Shot("gallery-metering.png");
}

if (Want("gallery-ais.png"))
{
    Show("ais-harbour", seconds: 9);
    Shot("gallery-ais.png");
}

if (Want("gallery-nfc.png"))
{
    Environment.SetEnvironmentVariable("IOTCOM_GALLERY_SCREENSHOT", "1");
    Show("nfc-asset-tags", seconds: 3);
    Shot("gallery-nfc.png");
}

if (Want("gallery-ntp.png"))
{
    Environment.SetEnvironmentVariable("IOTCOM_GALLERY_SCREENSHOT", "1");
    Show("ntp-clock-sync", seconds: 16);
    Shot("gallery-ntp.png");
}

if (Want("gallery-iec104.png"))
{
    Environment.SetEnvironmentVariable("IOTCOM_GALLERY_SCREENSHOT", "1");
    Show("iec104-substation", seconds: 5);
    Shot("gallery-iec104.png");
}

if (Want("gallery-j1939.png"))
{
    Environment.SetEnvironmentVariable("IOTCOM_GALLERY_SCREENSHOT", "1");
    Show("j1939-truck", seconds: 5);
    Shot("gallery-j1939.png");
}

if (Want("gallery-canopen.png"))
{
    Environment.SetEnvironmentVariable("IOTCOM_GALLERY_SCREENSHOT", "1");
    Show("canopen-io", seconds: 4);
    Shot("gallery-canopen.png");
}

if (Want("gallery-usb.png"))
{
    Environment.SetEnvironmentVariable("IOTCOM_GALLERY_SAMPLE_USB", "1");
    Show("usb-bench", seconds: 4);
    Shot("gallery-usb.png");
}

if (Want("gallery-ble.png"))
{
    Show("ble-nearby", seconds: 7);
    Shot("gallery-ble.png");
}

if (Want("gallery-opcua.png"))
{
    Show("opcua-tags", seconds: 9);
    Shot("gallery-opcua.png");
}

if (Want("gallery-sparkplug.png"))
{
    Show("plant-network", seconds: 8);
    Shot("gallery-sparkplug.png");
}

if (Want("gallery-nmea.png"))
{
    Show("nmea-tracker", seconds: 22);
    Shot("gallery-nmea.png");
}

if (Want("gallery-lighting.png"))
{
    Show("artnet-stage", seconds: 2);
    Shot("gallery-lighting.png");
}

if (Want("gallery-mqtt.png"))
{
    Show("mqtt-pubsub", seconds: 3.5);
    Await(((MqttDemo)vm.SelectedDemo!).PublishAsync());
    Pump(TimeSpan.FromSeconds(1));
    Shot("gallery-mqtt.png");
}

if (Want("gallery-workbench.png"))
{
    Show("workbench", seconds: 0.5);
    Shot("gallery-workbench.png");
}

// Medical demos (use the AI configured through IOTCOM_AI_* when present).
if (Want("gallery-hl7-icu.png"))
{
    Show("hl7-icu", seconds: 40);
    var icu = (BedsideMonitorDemo)vm.SelectedDemo!;
    Await(icu.SummarizeAsync());
    Pump(TimeSpan.FromSeconds(1));
    Shot("gallery-hl7-icu.png");
}

if (Want("gallery-dicom-ai.png", "gallery-dicom-ai-mri.png"))
{
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
}

if (Want("gallery-dark-id.png"))
{
    vm.ToggleThemeCommand.Execute(null);
    Loc.Instance.Language = "id";
    Show("modbus-factory", seconds: 1.5, start: false);
    Shot("gallery-dark-id.png");
}

Await(vm.DisposeAsync().AsTask());
Console.WriteLine("done");
