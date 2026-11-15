// EnipTags — read and write Logix tags over EtherNet/IP explicit messaging.
//
//   dotnet run                          # an in-process packaging-line PLC (IoTCom's EnipAdapter)
//   dotnet run -- 192.168.1.20 0        # a real controller: identity and a few tags, read-only (CPU in slot 0)
//
// IoTCom.Net — built by Gravicode Studios, led by Kang Fadhil.
using System.Globalization;
using IoTCom.Net;
using IoTCom.Net.Protocols.EtherNetIp;
using IoTCom.Net.Transports;

if (args.Length > 0)
{
    await using var plc = EnipClient.Create(o =>
    {
        o.UseTcp(args[0], EnipPacket.Port);
        if (args.Length > 1) o.UseSlot(byte.Parse(args[1], CultureInfo.InvariantCulture));
    });
    await plc.ConnectAsync();
    Console.WriteLine(await plc.ReadIdentityAsync());
    foreach (var tag in args.Skip(2))
        Console.WriteLine($"{tag} = {await plc.ReadTagAsync(tag)}");
    return;
}

var listener = new InMemoryTransportListener("packline");
await using var sim = EnipPlcSimulator.Create(o => o.ListenInMemory(listener));
await sim.StartAsync();

await using var client = EnipClient.Create(o => o.UseInMemory(listener).AllowWrites());
await client.ConnectAsync();
Console.WriteLine($"session 0x{client.Session:X8}: {await client.ReadIdentityAsync()}");

foreach (var (tag, value, error) in await client.ReadTagsAsync(["Line_Speed", "Motor_Run", "Count_Good", "Count_Reject", "Seal_Temp", "Recipe"]))
    Console.WriteLine($"  {tag,-14} {value?.ToString() ?? error!.Message}");
Console.WriteLine($"  Zone_Temps     {await client.ReadTagAsync("Zone_Temps", 4)}");

// A cold seal set point makes packs leak: watch the reject counter climb.
await client.WriteTagAsync("Seal_Setpoint", CipTagValue.Of(CipDataType.Real, 160));
for (var i = 0; i < 10; i++) sim.Step(30);
Console.WriteLine($"after 5 minutes at 160 °C: good {(await client.ReadTagAsync("Count_Good")).Value}, rejected {(await client.ReadTagAsync("Count_Reject")).Value}");

try
{
    await client.WriteTagAsync("Count_Good", CipTagValue.Of(CipDataType.Dint, 0));
}
catch (CipException ex)
{
    Console.WriteLine($"Count_Good is read-only: {ex.Status}");
}
