// NtpClock — measure this computer's clock against NTP servers, then keep a device clock disciplined.
//
//   dotnet run                                  # an in-process GPS-referenced server and a drifting device clock
//   dotnet run -- pool.ntp.org time.cloudflare.com
//                                               # real servers (measures only; the system clock is never changed)
//
// IoTCom.Net — built by Gravicode Studios, led by Kang Fadhil.
using System.Net;
using IoTCom.Net;
using IoTCom.Net.Protocols.Ntp;
using IoTCom.Net.Transports;

if (args.Length > 0)
{
    await using var client = SntpClient.Create(o => { foreach (var host in args) o.UseServer(host); });
    var estimate = await client.SynchronizeAsync();
    foreach (var r in estimate.Accepted) Console.WriteLine(r);
    foreach (var (failed, reason) in estimate.Failed) Console.WriteLine($"{failed}: {reason}");
    Console.WriteLine($"This computer is {estimate.Offset.TotalMilliseconds:+0.000;-0.000} ms off the median server time.");
    return;
}

// A local network with 10 ms each way, a stratum-1 server, and a device whose crystal runs 2 % fast (exaggerated).
var net = new InMemoryDatagramNetwork { Latency = TimeSpan.FromMilliseconds(10) };
var serverAddress = new IPEndPoint(IPAddress.Parse("10.0.0.1"), 123);
await using var server = NtpServer.Create(o => o.UseInMemory(net, serverAddress).WithReference("GPS"));
await server.StartAsync();

var device = new DriftingClock(TimeSpan.FromSeconds(-3.2), driftPpm: 20_000);
await using var sntp = SntpClient.Create(o =>
{
    o.UseInMemory(net);
    o.UseServer(serverAddress);
    o.Clock = () => device.UtcNow;
    o.MinimumPollInterval = TimeSpan.Zero;    // our own server; keep ≥ 15 s for public ones
});

for (var i = 0; i < 5; i++)
{
    var before = device.Error;
    var r = await sntp.QueryAsync(serverAddress);
    device.Step(r.Offset);
    Console.WriteLine($"sync {i + 1}: error was {before.TotalMilliseconds,9:+0.0;-0.0} ms, offset {r.Offset.TotalMilliseconds,9:+0.0;-0.0} ms, delay {r.RoundTripDelay.TotalMilliseconds,5:0.0} ms → error now {device.Error.TotalMilliseconds,6:+0.0;-0.0} ms");
    await Task.Delay(1000);
}

server.Leap = NtpLeap.Unsynchronised;   // the server loses its GPS reference
try
{
    await sntp.QueryAsync(serverAddress);
}
catch (DeviceException e)
{
    Console.WriteLine("refused: " + e.Message);
}
