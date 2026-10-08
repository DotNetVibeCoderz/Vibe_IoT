// LoRaWanGatewayMonitor — a light LoRaWAN network server that shows what your gateways hear.
//
//   dotnet run                          # network server on UDP 1700 plus simulated gateways and devices (AS923-2)
//   dotnet run -- devices.json          # real gateways: point their packet forwarder at this machine, port 1700
//
// devices.json: [{ "name": "node-1", "devEui": "70B3D57ED0000001", "appKey": "<32 hex digits>" }]
// IoTCom.Net — built by Gravicode Studios, led by Kang Fadhil.
using System.Net;
using System.Text.Json;
using IoTCom.Net;
using IoTCom.Net.Protocols.LoRaWan;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

await using var server = LoRaWanNetworkServer.Create(o => o.Region = LoRaRegion.AS923Group2);
server.DeviceJoined += (_, d) => Console.WriteLine($"  + {d.Name} joined as {d.DevAddr}");
server.FrameRejected += (_, reason) => Console.WriteLine($"  ! {reason}");
server.GatewayUpdated += (_, g) =>
{
    if (g.Status is { } s) Console.WriteLine($"  · gateway {g.Eui}: {s.RxReceived} received, {s.RxForwarded} forwarded, ack {s.AckRatio:0}%");
    else Console.WriteLine($"  · gateway {g.Eui} connected from {g.PullEndPoint}");
};
server.UplinkReceived += (_, up) =>
{
    var best = up.Gateways[0];
    Console.WriteLine($"{up.Time.ToLocalTime():HH:mm:ss} {up.Device.Name,-11} FCnt {up.FCnt,-4} {up.DataRate,-9} " +
        $"RSSI {best.Rssi,4:0} SNR {best.Snr,5:0.0} via {up.Gateways.Count} gw  {up.Airtime.TotalMilliseconds,5:0} ms  " +
        LoRaWanSimulator.DescribePayload(up.FPort, up.Payload));

    // Every tenth uplink of a sensor, ask it to report every 20 s (FPort 10, as the simulated sensors understand).
    if (up.FCnt > 0 && up.FCnt % 10 == 0) server.EnqueueDownlink(up.Device.DevEui, 10, [0x00, 20]);
};

LoRaWanSimulator? simulator = null;
if (args.Length > 0)
{
    using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(args[0]));
    foreach (var d in doc.RootElement.EnumerateArray())
        server.AddDevice(LoRaWanDeviceRegistration.Otaa(Eui64.Parse(d.GetProperty("devEui").GetString()!),
            LoRaWanKeys.Parse(d.GetProperty("appKey").GetString()!), d.GetProperty("name").GetString()));
}
else
{
    // No device list: simulate two gateways and four sensors that speak Semtech UDP to the server on loopback.
    simulator = new LoRaWanSimulator(LoRaWanSimulatorOptions.Demo(new IPEndPoint(IPAddress.Loopback, 1700)));
    foreach (var r in simulator.Registrations) server.AddDevice(r);
}

await server.StartAsync();
Console.WriteLine($"IoTCom.Net {IoTComInfo.Version} · LoRaWAN network server ({server.Options.Region.Name}) on UDP 1700, {server.Devices.Count} devices");
Console.WriteLine(simulator is null ? "Waiting for gateways (packet forwarder → this host:1700)…" : "Simulated gateways and devices; OTAA joins take 5 s.");
if (simulator is not null) await simulator.StartAsync();

try
{
    await Task.Delay(Timeout.Infinite, cts.Token);
}
catch (OperationCanceledException)
{
}

if (simulator is not null) await simulator.DisposeAsync();
foreach (var d in server.Devices.OrderBy(d => d.Name))
    Console.WriteLine($"{d.Name,-11} {(d.IsActivated ? d.DevAddr.ToString() : "not joined"),-10} {d.UplinkCount,4} up {d.DownlinkCount,4} down  battery {(d.Battery is { } b ? $"{b * 100 / 254}%" : "?")}");
Console.WriteLine(IoTComInfo.CreditEn);
