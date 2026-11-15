// EthercatRack — an EtherCAT master: scan the line, read identities and CoE objects, watch inputs in SAFE-OP, and
// (with --allow-write) go to OP and run a conveyor.
//
//   dotnet run                                   # the simulated I/O rack (coupler, DI8, DO8, AI4), read-only
//   dotnet run -- --allow-write                  # also OP: motor on, feedback, belt speed
//   sudo dotnet run -- eth1                      # a real line on a dedicated port (Linux: CAP_NET_RAW; Windows: Npcap + admin)
//
// Cycle timing on a desktop OS is best effort, not hard real-time.
// IoTCom.Net — built by Gravicode Studios, led by Kang Fadhil.
using System.Buffers.Binary;
using IoTCom.Net.Protocols.EtherCat;

var allowWrite = args.Contains("--allow-write");
var iface = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
var rack = iface is null ? new EthercatRackSimulator() : null;
await using var master = EthercatMaster.Create(o =>
{
    if (rack is not null) o.UseLink(rack.Segment);
    else o.UseInterface(iface!);
    if (allowWrite) o.AllowWrites();
});

foreach (var slave in await master.ScanAsync())
    Console.WriteLine(slave);

// SAFE-OP: inputs are live, outputs held safe. A read-only master stops here.
await master.RequestStateAsync(EcatState.SafeOp);
for (var i = 0; i < 50; i++)
{
    rack?.Step(0.01);
    await master.ExchangeAsync();
}

foreach (var slave in master.Slaves.Where(s => s.InputBytes > 0))
    Console.WriteLine($"{slave.Name,-14} inputs {Convert.ToHexString(master.InputsOf(slave))}");
var coe = master.Slaves.FirstOrDefault(s => s.HasCoe);
if (coe is not null)
    Console.WriteLine($"CoE 0x1008 of {coe.Name}: {System.Text.Encoding.ASCII.GetString(await master.SdoReadAsync(coe, 0x1008, 0))}");

if (!allowWrite || rack is null)
{
    Console.WriteLine(allowWrite ? "Real line: drive outputs from your own code." : "Read-only: add --allow-write to go to OP.");
    return;
}

await master.RequestStateAsync(EcatState.Op);
var outputs = master.Slaves.First(s => s.OutputBytes > 0);
master.SetOutputBit(outputs, 0, true);   // conveyor motor
using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
var simulation = Task.Run(async () =>
{
    while (!stop.IsCancellationRequested)
    {
        rack.Step(0.01);
        await Task.Delay(10);
    }
});
await master.RunAsync(TimeSpan.FromMilliseconds(4), stop.Token);   // 250 Hz, best effort
await simulation;
var analog = master.InputsOf(master.Slaves.Last());
Console.WriteLine($"belt {BinaryPrimitives.ReadInt16LittleEndian(analog[6..]) / 100.0:0.0} m/min, motor {BinaryPrimitives.ReadInt16LittleEndian(analog) / 100.0:0.00} A, {rack.Parts} parts");
var st = master.Statistics;
Console.WriteLine($"{st.Cycles} cycles, {st.WorkingCounterErrors} short working counters, max round trip {st.MaxRoundTrip.TotalMilliseconds:0.000} ms");
