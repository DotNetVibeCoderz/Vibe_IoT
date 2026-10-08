// SparkplugEdgeNode — publish a bottling line as a Sparkplug B edge node and watch it from a host application.
//
//   dotnet run                        # embedded broker + edge node + host application, all in this process
//   dotnet run -- broker.local 1883   # edge node on a real broker (Ignition, HiveMQ, EMQX, Mosquitto…)
//
// IoTCom.Net — built by Gravicode Studios, led by Kang Fadhil.
using System.Net;
using System.Net.Sockets;
using IoTCom.Net.Adapters.Mqtt;
using IoTCom.Net.Protocols.Sparkplug;

var demo = args.Length == 0;
var host = demo ? "127.0.0.1" : args[0];
var port = args.Length > 1 ? int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : demo ? FreePort() : 1883;
using var cts = new CancellationTokenSource(demo ? TimeSpan.FromSeconds(12) : Timeout.InfiniteTimeSpan);
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

await using var broker = demo ? MqttBroker.Create(port) : null;
if (broker is not null) await broker.StartAsync();

// The edge node: NDEATH is registered as the MQTT will, NBIRTH/DBIRTH list every metric with an alias.
await using var node = SparkplugEdgeNode.Create(o =>
{
    o.Group = "Plant";
    o.EdgeNode = "Line1";
    o.Mqtt = m => m.UseBroker(host, port);
});
var line = new SparkplugLineSimulator(node).Define();
node.CommandReceived += (_, c) => Console.WriteLine($"  edge ← write {c.Device}/{c.Metric} = {c.Value}");

// In the demo a host application (SCADA side) follows the namespace and sends one command.
await using var scada = demo ? SparkplugHost.Create(o => { o.HostId = "scada"; o.Mqtt = m => m.UseBroker(host, port); }) : null;
if (scada is not null)
{
    scada.StateChanged += (_, v) => Console.WriteLine($"  host: {v.Key} is {(v.Online ? "online" : "offline")}");
    scada.MetricUpdated += (_, e) =>
    {
        if (e.View.Device == "Tank7" && e.Metric.Name == "Level") Console.WriteLine($"  host: Tank7 level {e.Metric.Value} %");
    };
    await scada.StartAsync();
}

await node.StartAsync();
Console.WriteLine($"Edge node Plant/Line1 online (bdSeq {node.BdSeq}) on {host}:{port}. Ctrl+C to stop.");
var tick = 0;
try
{
    while (!cts.IsCancellationRequested)
    {
        await line.Step(cts.Token);
        if (scada is not null && ++tick == 4) await scada.WriteAsync("Plant", "Line1", "Tank7", "InletValve", true);
        await Task.Delay(1000, cts.Token);
    }
}
catch (OperationCanceledException)
{
}

// Disposing publishes DDEATH for each device and NDEATH; a lost connection would make the broker send the will instead.
Console.WriteLine("Stopping: DDEATH, NDEATH.");

static int FreePort()
{
    using var l = new TcpListener(IPAddress.Loopback, 0);
    l.Start();
    return ((IPEndPoint)l.LocalEndpoint).Port;
}
