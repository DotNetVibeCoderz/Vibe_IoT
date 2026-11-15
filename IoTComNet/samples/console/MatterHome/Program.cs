// MatterHome — a Matter smart-home bridge and a controller: commission, read, switch a light, subscribe.
//
//   dotnet run                                   # bridge and controller in-process (test attestation)
//   dotnet run -- device                         # only the bridge, on UDP 5540 with DNS-SD; commission it from another machine
//
// Matter devices sold to consumers need CSA certification and a production attestation chain; this is for labs and tools.
// IoTCom.Net — built by Gravicode Studios, led by Kang Fadhil.
using System.Net;
using IoTCom.Net.Protocols.Matter;
using IoTCom.Net.Protocols.Mdns;
using IoTCom.Net.Transports;

if (args.FirstOrDefault() == "device")
{
    await using var mdns = MdnsResponder.Create();
    await mdns.StartAsync();
    await using var bridge = MatterDevice.Create(o => { o.UseUdp(5540); o.Mdns = mdns; o.ProductName = "IoTCom smart home bridge"; });
    var simulator = new MatterBridgeSimulator(bridge);
    await bridge.StartAsync();
    Console.WriteLine($"Bridge on UDP 5540 — QR {bridge.SetupPayload.ToQrCode()}, manual code {bridge.SetupPayload.ToManualCode()}. Ctrl+C to stop.");
    await Task.Delay(Timeout.Infinite);
}

var net = new InMemoryDatagramNetwork();
var at = new IPEndPoint(IPAddress.Parse("10.0.0.70"), 5540);
await using var device = MatterDevice.Create(o => { o.UseInMemory(net, at); o.ProductName = "IoTCom smart home bridge"; });
var sim = new MatterBridgeSimulator(device);
await device.StartAsync();
Console.WriteLine($"Pairing code {device.SetupPayload.ToManualCode()} ({device.SetupPayload.ToQrCode()})");

await using var controller = MatterController.Create(o => o.UseInMemory(net).AllowWrites().TrustTestDevices());
var node = await controller.CommissionAsync(at, MatterSetupPayload.FromManualCode(device.SetupPayload.ToManualCode()), nodeId: 0x42,
    new Progress<MatterCommissioningStep>(step => Console.WriteLine($"  {step}")));
Console.WriteLine($"Commissioned as node 0x{node.NodeId:X} on fabric 0x{controller.FabricId:X}");

Console.WriteLine($"Bridge: {(await node.ReadAttributeAsync(0, MatterIds.BasicInformation, 3)).AsString()}");
foreach (var r in await node.ReadAsync([new AttributePath(null, MatterIds.BridgedDeviceBasicInformation, 5)]))
    Console.WriteLine($"  endpoint {r.Path.Endpoint}: {r.Value!.AsString()}");

var sub = await node.SubscribeAsync([new AttributePath(null, MatterIds.OnOff, 0)], 0, 60, r => Console.WriteLine($"  report {r.Path} = {r.Value}"));
Console.WriteLine($"Subscription {sub.Id}; switching the terrace light on:");
await node.InvokeAsync(sim.Lights[2].Endpoint, MatterIds.OnOff, 1);
await Task.Delay(500);
Console.WriteLine($"Terrace light is {(sim.Lights[2].OnOff.On ? "on" : "off")}");
