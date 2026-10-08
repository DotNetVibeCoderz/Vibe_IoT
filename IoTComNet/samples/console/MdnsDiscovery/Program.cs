// MdnsDiscovery — find devices on the local network with mDNS / DNS-SD, and advertise one.
//
//   dotnet run                  # a simulated plant segment, no network traffic
//   dotnet run -- --lan         # the real network: list every service type, then the services of each
//   dotnet run -- --advertise   # advertise "IoTCom gateway" as _modbus._tcp on port 502 until Ctrl+C
//
// IoTCom.Net — built by Gravicode Studios, led by Kang Fadhil.
using IoTCom.Net.Protocols.Mdns;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

if (args.Contains("--advertise"))
{
    await using var responder = MdnsResponder.Create();
    await responder.StartAsync();
    var service = new MdnsService
    {
        Instance = "IoTCom gateway",
        Type = "_modbus._tcp",
        Port = 502,
        Addresses = MdnsAddresses.LocalAddresses(),
        Properties = new Dictionary<string, string> { ["vendor"] = "IoTCom", ["units"] = "1-8" },
    };
    await responder.RegisterAsync(service);
    Console.WriteLine($"Advertising {service.FullName} at {string.Join(", ", service.Addresses)}:502. Ctrl+C sends a goodbye.");
    try { await Task.Delay(Timeout.Infinite, cts.Token); } catch (OperationCanceledException) { }
    return;
}

await using var plant = args.Contains("--lan") ? null : new MdnsSimulator();
if (plant is not null) await plant.StartAsync();
await using var browser = plant?.Browser() ?? MdnsBrowser.Create();
await browser.StartAsync();

var types = await browser.EnumerateTypesAsync(TimeSpan.FromSeconds(2), cts.Token);
Console.WriteLine($"{types.Count} service types on the {(plant is null ? "local network" : "simulated segment")}:");
foreach (var type in types)
{
    var services = await browser.BrowseAsync(type.Replace(".local", "", StringComparison.Ordinal), TimeSpan.FromSeconds(1), cts.Token);
    foreach (var s in services)
        Console.WriteLine($"  {s.Type,-14} {s.Instance,-26} {s.Address}:{s.Port}  {string.Join(" ", s.Properties.Select(p => $"{p.Key}={p.Value}"))}");
}

if (plant is not null)
{
    // Devices leave with a goodbye (TTL 0); browsers drop them within a second.
    browser.ServiceChanged += (_, c) => Console.WriteLine($"  {(c.Lost ? "left" : "joined")}: {c.Service.Instance}");
    var watching = browser.BrowseContinuouslyAsync("_ipp._tcp", cts.Token);
    await plant.UnplugAsync("Label printer");
    await Task.Delay(1500);
    await cts.CancelAsync();
    await watching;
}
