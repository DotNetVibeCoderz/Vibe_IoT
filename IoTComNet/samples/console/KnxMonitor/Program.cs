// KnxMonitor — a KNXnet/IP group monitor, plus an office floor to watch.
//
//   dotnet run                       # an in-process KNX IP router with an office floor: discover, tunnel, read, write
//   dotnet run -- 192.168.1.10       # monitor a real installation through its KNX IP interface (read-only)
//
// IoTCom.Net — built by Gravicode Studios, led by Kang Fadhil.
using System.Net;
using IoTCom.Net;
using IoTCom.Net.Protocols.Knx;
using IoTCom.Net.Transports;

if (args.Length > 0)
{
    await using var monitor = KnxTunnelClient.Create(o => o.UseGateway(args[0]));   // read-only by default
    monitor.GroupValueReceived += e => Console.WriteLine($"{e.Time.ToLocalTime():HH:mm:ss}  {e}");
    monitor.ConnectionLost += why => Console.WriteLine($"connection lost: {why}");
    await monitor.ConnectAsync();
    Console.WriteLine($"Tunnel open as {monitor.Address}. Ctrl+C to stop.");
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    try
    {
        await Task.Delay(Timeout.Infinite, stop.Token);
    }
    catch (OperationCanceledException)
    {
    }

    return;
}

// An office floor on a KNX line behind a KNX IP router, all in memory.
var net = new InMemoryDatagramNetwork();
var routerAddress = new IPEndPoint(IPAddress.Parse("10.50.0.2"), KnxIp.Port);
await using var bus = new KnxBus();
var floor = new KnxBuildingSimulator(bus);
await using var router = KnxIpGateway.Create(o => { o.UseInMemory(net, routerAddress); o.Bus = bus; });
await router.StartAsync();

foreach (var device in await KnxDiscovery.SearchAsync(net.Bind(), TimeSpan.FromMilliseconds(300)))
    Console.WriteLine($"found: {device}");

await using var tunnel = KnxTunnelClient.Create(o => o.UseInMemory(net).UseGateway(routerAddress).AllowWrites());
tunnel.GroupValueReceived += e =>
{
    var info = floor.Find(e.Destination);
    Console.WriteLine($"  {e.Source} → {e.Destination} {e.Service}: {(info is null || e.Service == KnxApci.GroupValueRead ? e.Value.ToString() : KnxDpt.Format(info.Dpt, e.Value))}  {info?.Name}");
};
await tunnel.ConnectAsync();
Console.WriteLine($"tunnel open as {tunnel.Address}");

Console.WriteLine($"Meeting room: {await tunnel.ReadAsync("3/0/1", "9.001")} °C, set point {await tunnel.ReadAsync("3/1/1", "9.001")} °C");
await tunnel.WriteAsync("1/0/1", "1.001", true);       // light on
await tunnel.WriteAsync("2/1/1", "5.001", 75);         // blind to 75 % closed
await tunnel.WriteAsync("3/1/1", "9.001", 23.0);       // warmer
for (var i = 0; i < 5; i++)
{
    floor.Step(5);
    await Task.Delay(200);
}

var room = floor.Rooms[0];
Console.WriteLine($"{room.Name}: light {(room.LightOn ? "on" : "off")}, blind {room.Blind:0} %, valve {room.Valve:0} %, {room.Temperature:0.0} °C");
