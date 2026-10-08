// MBusScanner — find every slave on a wired M-Bus segment and print its data records.
//
//   dotnet run                    # a simulated segment (heat, water and electricity meters) in this process
//   dotnet run -- COM4            # a real bus through a level converter (2400 baud, 8E1)
//   dotnet run -- 10.0.0.40:10001 # an M-Bus/TCP gateway
//
// Reading never changes a meter: the scan only sends SND_NKE and REQ_UD2.
// IoTCom.Net — built by Gravicode Studios, led by Kang Fadhil.
using System.IO.Ports;
using IoTCom.Net;
using IoTCom.Net.Protocols.MBus;
using IoTCom.Net.Transport.Serial;
using IoTCom.Net.Transports;

var target = args.FirstOrDefault();
var listener = target is null ? new InMemoryTransportListener("mbus") : null;
await using var segment = listener is null ? null : MBusSlaveSimulator.Create(o => o.ListenInMemory(listener)).AddDefaultDevices();
if (segment is not null) await segment.StartAsync();

await using var bus = MBusMaster.Create(o =>
{
    if (listener is not null) o.UseInMemory(listener);
    else if (target!.Contains(':', StringComparison.Ordinal)) o.UseTcp(target.Split(':')[0], int.Parse(target.Split(':')[1], System.Globalization.CultureInfo.InvariantCulture));
    else o.UseSerial(target, 2400, Parity.Even);
    o.ResponseTimeout = TimeSpan.FromMilliseconds(listener is null ? 600 : 100);
});
await bus.ConnectAsync();
Console.WriteLine($"IoTCom.Net {IoTComInfo.Version} · M-Bus scan of primary addresses 0–250…");

foreach (var address in await bus.ScanAsync(0, listener is null ? (byte)250 : (byte)10))
{
    var t = await bus.ReadAsync(address);
    Console.WriteLine($"\n[{address}] {t.SecondaryAddress}  {t.MediumName}, access #{t.AccessNumber}");
    foreach (var r in t.Records) Console.WriteLine($"    {r}");
}

Console.WriteLine($"\n{IoTComInfo.CreditEn}");
