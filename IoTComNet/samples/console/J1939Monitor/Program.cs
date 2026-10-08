// J1939Monitor — listen to a truck, bus or machine on J1939: decoded SPNs, DM1 trouble codes and the VIN.
//
//   dotnet run                                  # a simulated engine ECU on a virtual bus
//   dotnet run -- socketcan:can0                # a real 250 kbit/s network (also slcan:COM5, gsusb:, pcan:usb1)
//   dotnet run -- gsusb: --listen-only          # never transmit (no address claim, no VIN request)
//
// IoTCom.Net — built by Gravicode Studios, led by Kang Fadhil.
using System.Text;
using IoTCom.Net.Protocols.J1939;
using IoTCom.Net.Transport.Can;
using IoTCom.Net.Transport.Can.Adapters;

CanAdapters.Register();
var uri = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
var listenOnly = args.Contains("--listen-only");
J1939EngineSimulator? engine = null;
ICanBus bus;
if (uri is null)
{
    var net = new VirtualCanNetwork("sample");
    engine = J1939EngineSimulator.Create(net.CreateNode());
    engine.Throttle = 45;
    await engine.StartAsync();
    bus = net.CreateNode();
}
else
{
    bus = await CanBus.OpenAsync(uri, o => { o.Bitrate = 250_000; o.ListenOnly = listenOnly; });
}

await using var _bus = bus;
await using var node = J1939Node.Create(bus, o => { o.ReadOnly = true; o.ListenOnly = listenOnly; });
var seen = new HashSet<uint>();
node.MessageReceived += m =>
{
    if (m.Pgn == Pgn.Dm1)
    {
        var dm1 = J1939Dm1.Parse(m.Data);
        Console.WriteLine($"  {DateTime.Now:HH:mm:ss.fff} DM1 0x{m.Source:X2}: amber {dm1.AmberWarningLamp}, {(dm1.Dtcs.Count == 0 ? "no active DTCs" : string.Join("; ", dm1.Dtcs.Select(d => $"SPN {d.Spn} ({J1939Spn.Name(d.Spn)}) FMI {d.Fmi} {d.FailureMode}")))}");
    }
    else if (J1939Spn.Knows(m.Pgn) && seen.Add(m.Pgn * 256 + m.Source))   // first one of each PGN per source; the rest repeat
    {
        Console.WriteLine($"  {DateTime.Now:HH:mm:ss.fff} {m}");
    }
};
await node.StartAsync();

if (!listenOnly)
{
    try
    {
        var vin = await node.RequestAsync(Pgn.VehicleIdentification, uri is null ? (byte)0x00 : J1939Id.Global, TimeSpan.FromSeconds(2));
        Console.WriteLine($"VIN from 0x{vin.Source:X2}: {Encoding.ASCII.GetString(vin.Data).TrimEnd('*')}");
    }
    catch (IoTCom.Net.IoTComTimeoutException)
    {
        Console.WriteLine("No ECU answered the VIN request.");
    }
}

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(uri is null ? 4 : 60));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
try
{
    await Task.Delay(1500, cts.Token);
    engine?.OilLeak();
    while (!cts.IsCancellationRequested)
    {
        engine?.Step(5);   // accelerate the simulated leak
        await Task.Delay(500, cts.Token);
    }
}
catch (OperationCanceledException)
{
}

if (engine is not null) await engine.DisposeAsync();
