// CoapObserve — discover a CoAP device's resources and observe its sensors (RFC 7641).
//
//   dotnet run                                  # starts a simulated greenhouse node on UDP 5683 and observes it
//   dotnet run -- 192.168.1.40                  # a real device (any RFC 7252 server)
//
// IoTCom.Net — built by Gravicode Studios, led by Kang Fadhil.
using System.Net;
using IoTCom.Net;
using IoTCom.Net.Protocols.Coap;
using IoTCom.Net.Transports;

var host = args.FirstOrDefault() ?? "127.0.0.1";
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// Without arguments, host the simulated device ourselves.
await using var server = args.Length == 0 ? CoapServer.Create(o => o.UseUdp(5683, IPAddress.Loopback)) : null;
await using var device = server is null ? null : new CoapDeviceSimulator(server);
if (server is not null)
{
    await server.StartAsync();
    device!.Start();
}

await using var coap = CoapClient.Create(o => o.UseServer(host).ReadOnly = true);
await coap.ConnectAsync();
Console.WriteLine($"IoTCom.Net {IoTComInfo.Version} · CoAP client → coap://{host}:5683 (ping {(await coap.PingAsync()).TotalMilliseconds:0.0} ms)\n");

var links = await coap.DiscoverAsync();
foreach (var l in links) Console.WriteLine($"  {l.Path,-24} {l.ResourceType,-16} {(l.Observable ? "observable" : "")}");

// Observe every observable resource concurrently; Ctrl+C cancels (the device is told to stop).
var observed = links.Where(l => l.Observable).Select(l => Task.Run(async () =>
{
    try
    {
        await foreach (var n in coap.ObserveAsync(l.Path, ct: cts.Token))
            Console.WriteLine($"{DateTime.Now:HH:mm:ss}  {l.Path,-24} {n.PayloadText,8}   (obs {n.ObserveSequence})");
    }
    catch (OperationCanceledException) { }
})).ToArray();

Console.WriteLine("\nObserving… Ctrl+C to stop.\n");
await Task.WhenAll(observed);
Console.WriteLine($"Retransmissions: {coap.Statistics.RetransmissionCount}, duplicates suppressed: {coap.Statistics.DuplicateCount}");
