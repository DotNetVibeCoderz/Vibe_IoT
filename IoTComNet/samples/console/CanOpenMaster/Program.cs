// CanOpenMaster — scan a CANopen network, read identities over SDO, start the nodes and print their PDOs.
//
//   dotnet run                                  # two simulated I/O modules on a virtual bus
//   dotnet run -- socketcan:can0                # a real network (also slcan:COM5, gsusb:, pcan:usb1)
//   dotnet run -- gsusb: --start                # also send NMT start (changes what the devices do)
//
// IoTCom.Net — built by Gravicode Studios, led by Kang Fadhil.
using IoTCom.Net.Protocols.CanOpen;
using IoTCom.Net.Transport.Can;
using IoTCom.Net.Transport.Can.Adapters;

CanAdapters.Register();
var uri = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
var start = uri is null || args.Contains("--start");
var modules = new List<CanOpenIoModuleSimulator>();
ICanBus bus;
if (uri is null)
{
    var net = new VirtualCanNetwork("sample");
    foreach (byte id in new byte[] { 5, 6 })
    {
        var m = CanOpenIoModuleSimulator.Create(net.CreateNode(), id, heartbeatMs: 500, eventTimerMs: 250);
        await m.StartAsync();
        modules.Add(m);
    }

    bus = net.CreateNode();
}
else
{
    bus = await CanBus.OpenAsync(uri, o => o.Bitrate = 500_000);
}

await using var _bus = bus;
await using var master = CanOpenMaster.Create(bus, o => o.ReadOnly = !start);
await master.StartAsync();

Console.WriteLine("Scanning nodes 1–127…");
var nodes = await master.ScanAsync(1, 127, TimeSpan.FromMilliseconds(60));
foreach (var n in nodes)
    Console.WriteLine($"  node {n.Id,3}  {n.Name,-14} type 0x{n.DeviceType:X8}  vendor 0x{n.Identity?.Vendor:X8} product 0x{n.Identity?.Product:X8} serial {n.Identity?.Serial:X8}");
if (nodes.Count == 0) return;

var mappings = new Dictionary<byte, PdoMapping>();
foreach (var n in nodes) mappings[n.Id] = await master.ReadTpdoMappingAsync(n.Id, 1);
master.PdoReceived += (node, pdo, data) =>
{
    if (pdo != 1 || !mappings.TryGetValue(node, out var map)) return;
    var values = map.Unpack(data).Select(v => $"{v.Object.Index:X4}:{v.Object.SubIndex:X2}={Convert.ToHexString(v.Raw)}");
    Console.WriteLine($"  {DateTime.Now:HH:mm:ss.fff} TPDO1 node {node}: {string.Join("  ", values)}");
};

if (start)
{
    await master.NmtAsync(NmtCommand.Start, 0);
    Console.WriteLine("NMT start sent to all nodes.");
}

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(uri is null ? 3 : 30));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
try
{
    while (!cts.IsCancellationRequested)
    {
        foreach (var m in modules) m.Step();
        await Task.Delay(250, cts.Token);
    }
}
catch (OperationCanceledException)
{
}

foreach (var m in modules) await m.DisposeAsync();
