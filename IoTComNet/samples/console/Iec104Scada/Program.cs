// Iec104Scada — a small IEC 60870-5-104 master: general interrogation, counters, then spontaneous changes.
//
//   dotnet run                                  # against the built-in 20 kV feeder bay simulator
//   dotnet run -- 10.0.0.5                      # a real RTU on port 2404 (read-only: no commands are sent)
//   dotnet run -- 10.0.0.5 2404 7               # host, port, common address
//
// IoTCom.Net — built by Gravicode Studios, led by Kang Fadhil.
using System.Globalization;
using IoTCom.Net;
using IoTCom.Net.Protocols.Iec104;
using IoTCom.Net.Transports;

var host = args.ElementAtOrDefault(0);
var port = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : Iec104Apdu.DefaultPort;
var ca = args.Length > 2 ? ushort.Parse(args[2], CultureInfo.InvariantCulture) : (ushort)1;

Iec104SubstationSimulator? rtu = null;
Iec104Client scada;
if (host is null)
{
    var listener = new InMemoryTransportListener("sample-rtu");
    rtu = Iec104SubstationSimulator.Create(o => o.ListenInMemory(listener));
    await rtu.StartAsync();
    scada = Iec104Client.Create(o => o.UseInMemory(listener));
}
else
{
    scada = Iec104Client.Create(o => { o.UseTcp(host, port); o.CommonAddress = ca; });
}

await using var _scada = scada;
await scada.ConnectAsync();
Console.WriteLine($"Connected to {host ?? "the simulated feeder bay"}, data transfer started.");

Console.WriteLine("General interrogation:");
foreach (var p in (await scada.InterrogateAsync()).OrderBy(p => p.Object.Address))
    Console.WriteLine($"  {Iec104Types.Mnemonic(p.Type),-9} IOA {p.Object.Address,6} = {p.Object.Value.ToString("0.###", CultureInfo.InvariantCulture),10}{(p.Object.Quality != 0 ? $"  [{p.Object.Quality}]" : "")}");
foreach (var c in await scada.CounterInterrogateAsync())
    Console.WriteLine($"  counter IOA {c.Object.Address} = {c.Object.Value} (sequence {c.Object.Qualifier})");

scada.PointReceived += p =>
{
    if (p.Cause is Iec104Cause.Spontaneous or Iec104Cause.ReturnRemote)
        Console.WriteLine($"  {p.Object.Time?.Value:HH:mm:ss.fff} {Iec104Types.Mnemonic(p.Type),-9} IOA {p.Object.Address,6} = {p.Object.Value.ToString("0.###", CultureInfo.InvariantCulture)} ({Iec104Asdu.CauseName(p.Cause)})");
};

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(rtu is null ? 60 : 4));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
Console.WriteLine("Spontaneous changes:");
try
{
    var n = 0;
    while (!cts.IsCancellationRequested)
    {
        await Task.Delay(500, cts.Token);
        rtu?.Step(30);                        // half a simulated hour per tick
        if (++n == 4) rtu?.Trip();            // and a short circuit on the feeder
    }
}
catch (OperationCanceledException)
{
}

if (rtu is not null) await rtu.DisposeAsync();
