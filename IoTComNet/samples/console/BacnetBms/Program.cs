// BacnetBms — a small BMS front end: discover BACnet/IP devices, read their objects, subscribe to COV, command a point.
//
//   dotnet run                    # an in-process air handling unit (IoTCom's BacnetDevice) and the client adapter
//   dotnet run -- 192.168.1.255   # discover and list the devices on your network (read-only), broadcasting there
//
// IoTCom.Net — built by Gravicode Studios, led by Kang Fadhil.
using System.Net;
using IoTCom.Net;
using IoTCom.Net.Adapters.Bacnet;
using IoTCom.Net.Protocols.Bacnet;
using IoTCom.Net.Transports;

if (args.Length > 0)
{
    await using var bms = BacnetIpClient.Create(o => o.BroadcastAddress = new IPEndPoint(IPAddress.Parse(args[0]), BacnetDeviceOptions.DefaultPort));
    await bms.ConnectAsync();
    foreach (var d in await bms.DiscoverAsync(TimeSpan.FromSeconds(3)))
    {
        Console.WriteLine(d);
        foreach (var obj in (await bms.ReadObjectListAsync(d.Instance)).Take(20))
        {
            var r = await bms.ReadPropertiesAsync(d.Instance, obj, [BacnetPropertyId.ObjectName, BacnetPropertyId.PresentValue]);
            Console.WriteLine($"  {obj.ShortName,-8} {(r[0].Ok ? r[0].Values![0] : ""),-32} {(r[1].Ok ? r[1].Values![0] : "")}");
        }
    }

    return;
}

// An air handling unit on an in-memory network; broadcasts use a multicast group there (BACnet/IP Annex J allows it).
var net = new InMemoryDatagramNetwork();
var group = new IPEndPoint(IPAddress.Parse("239.255.186.192"), BacnetDeviceOptions.DefaultPort);
await using var ahu = BacnetAhuSimulator.Create(o => { o.UseInMemory(net, new IPEndPoint(IPAddress.Parse("10.60.0.2"), 47808)); o.BroadcastAddress = group; });
await ahu.StartAsync();

await using var client = BacnetIpClient.Create(o =>
{
    o.UseInMemory(net, new IPEndPoint(IPAddress.Parse("10.60.0.20"), 47808));
    o.BroadcastAddress = group;
    o.AllowWrites();
});
await client.ConnectAsync();
var device = (await client.DiscoverAsync(TimeSpan.FromMilliseconds(300))).Single();
Console.WriteLine($"found {device}");

foreach (var obj in (await client.ReadObjectListAsync(device.Instance)).Where(o => o.Type != BacnetObjectType.Device))
{
    var r = await client.ReadPropertiesAsync(device.Instance, obj, [BacnetPropertyId.ObjectName, BacnetPropertyId.PresentValue]);
    Console.WriteLine($"  {obj.ShortName,-5} {r[0].Values![0],-30} {r[1].Values![0]}");
}

await using (await client.SubscribeCovAsync(device.Instance, BacnetObjectId.Parse("AI1"), n => Console.WriteLine($"COV supply air {n.Values[0].Values[0]} °C")))
{
    // An operator stops the supply fan at priority 8 (manual operator), then hands it back.
    await client.WritePropertyAsync(device.Instance, BacnetObjectId.Parse("BO1"), BacnetPropertyId.PresentValue, BacnetValue.Enumerated(0), priority: 8);
    for (var i = 0; i < 6; i++)
    {
        ahu.Step(10);
        await Task.Delay(100);
    }

    Console.WriteLine($"fan status {ahu.FanStatus.FormatValue()}, static pressure {ahu.StaticPressure.FormatValue()}");
    await client.WritePropertyAsync(device.Instance, BacnetObjectId.Parse("BO1"), BacnetPropertyId.PresentValue, BacnetValue.Null, priority: 8);
    Console.WriteLine($"BO1 relinquished: command {ahu.FanCommand.FormatValue()} (priority {ahu.FanCommand.ActivePriority?.ToString() ?? "relinquish default"})");
}
