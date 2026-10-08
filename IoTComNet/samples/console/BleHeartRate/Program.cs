// BleHeartRate — find a Bluetooth heart-rate strap, read its battery and print beats per minute as they arrive.
//
//   dotnet run            # virtual radio with a simulated strap
//   dotnet run -- --real  # your Bluetooth radio and any strap advertising the Heart Rate service (0x180D)
//
// IoTCom.Net — built by Gravicode Studios, led by Kang Fadhil.
using IoTCom.Net.Transport.Ble;

var real = args.Contains("--real");
var network = new VirtualBleNetwork();
network.AddHeartRateStrap();
var simulator = new VirtualBleSimulator(network);

await using var ble = BleCentral.Create(o =>
{
    o.ReadOnly = true;                                  // this sample only listens
    if (real) o.UseNative(); else o.UseVirtual(network);
});
await ble.ConnectAsync();
Console.WriteLine($"Radio: {ble.Adapter!.Description}");

var heartRate = BleUuid.FromShort(0x180D);
var found = await ble.ScanAsync(TimeSpan.FromSeconds(real ? 10 : 1), [heartRate]);
if (found.Count == 0)
{
    Console.WriteLine("No heart-rate strap is advertising. Wear it (straps sleep without skin contact) and try again.");
    return;
}

var strapAd = found[0];
Console.WriteLine($"Found {strapAd.Name} ({strapAd.Id}) at {strapAd.Rssi} dBm");
await using var strap = await ble.OpenAsync(strapAd.Id);
var battery = await strap.ReadAsync("2a19");
Console.WriteLine($"Battery {GattValue.Describe(BleUuid.Parse("2a19"), battery)}, sensor on the {GattValue.Describe(BleUuid.Parse("2a38"), await strap.ReadAsync("2a38"))}");

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(real ? 60 : 8));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
if (!real) _ = Task.Run(async () => { while (!cts.IsCancellationRequested) { simulator.Step(); await Task.Delay(1000); } });
try
{
    await foreach (var value in strap.SubscribeAsync(BleUuid.FromShort(0x2A37), cts.Token))
    {
        var hr = GattValue.ParseHeartRate(value);
        Console.WriteLine($"{DateTime.Now:HH:mm:ss}  {hr.BeatsPerMinute,3} bpm  {string.Join(" ", hr.RrIntervals.Select(r => $"RR {r:0.000} s"))}");
    }
}
catch (OperationCanceledException)
{
}
