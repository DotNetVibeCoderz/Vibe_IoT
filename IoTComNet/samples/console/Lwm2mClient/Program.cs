// Lwm2mClient — a smart street light as an OMA LwM2M client, plus a server that manages it.
//
//   dotnet run                                  # server and light in-process: register, read, observe, write
//   dotnet run -- leshan.local 5683             # only the light, registering with an LwM2M server you run
//
// IoTCom.Net — built by Gravicode Studios, led by Kang Fadhil.
using System.Globalization;
using System.Net;
using IoTCom.Net;
using IoTCom.Net.Protocols.Lwm2m;
using IoTCom.Net.Transports;

if (args.Length > 0)
{
    var port = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 5683;
    await using var remoteLight = Lwm2mStreetLightSimulator.Create(o => o.UseServer(args[0], port));
    remoteLight.Client.RequestHandled += r => Console.WriteLine($"{r.Operation} {r.Path} -> {r.Result}");
    await remoteLight.StartAsync();
    Console.WriteLine($"Registered as {remoteLight.Client.RegistrationId}. Ctrl+C to deregister.");
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    try
    {
        while (true)
        {
            await Task.Delay(5000, stop.Token);
            remoteLight.Step(5);
        }
    }
    catch (OperationCanceledException)
    {
    }

    return;
}

var net = new InMemoryDatagramNetwork();
var serverAddress = new IPEndPoint(IPAddress.Loopback, 5683);
await using var server = Lwm2mServer.Create(o => o.UseInMemory(net, serverAddress).AllowWrites());
server.Registered += r => Console.WriteLine($"registered: {r}");
await server.StartAsync();

await using var light = Lwm2mStreetLightSimulator.Create(o => { o.UseInMemory(net); o.UseServer(serverAddress); });
await light.StartAsync();
const string endpoint = "urn:dev:light:SL60-000417";   // the simulator's default endpoint client name

foreach (var v in await server.ReadAsync(endpoint, Lwm2mPath.Parse("/3/0")))
    Console.WriteLine($"  {v}");

await server.WriteAttributesAsync(endpoint, Lwm2mPath.Parse("/3303/0/5700"), pmin: 0, pmax: 10);
await using (await server.ObserveAsync(endpoint, Lwm2mPath.Parse("/3303/0/5700"), values => Console.WriteLine($"  notify: driver at {values[0].Value} °C")))
{
    await server.WriteAsync(endpoint, Lwm2mPath.Parse("/3311/0/5850"), true);
    for (var i = 0; i < 3; i++)
    {
        light.Step(60);
        await Task.Delay(300);
    }
}

try
{
    await server.WriteAsync(endpoint, Lwm2mPath.Parse("/3311/0/5851"), 150L);
}
catch (Lwm2mException e)
{
    Console.WriteLine($"dimmer 150 %: {e.Status}");   // 4.00: the light refuses values outside 0–100
}

await server.ExecuteAsync(endpoint, Lwm2mPath.Parse("/3/0/4"));
Console.WriteLine($"reboots: {light.Reboots}");
