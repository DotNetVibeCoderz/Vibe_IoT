// UdsTester — read identification, live data and trouble codes from an engine ECU over CAN.
//
//   dotnet run                                   # built-in ECU simulator on a virtual bus (no hardware)
//   dotnet run -- socketcan:can0                 # Linux SocketCAN (ip link set can0 up type can bitrate 500000)
//   dotnet run -- slcan:COM5                     # CANable / CANtact / USBtin adapter
//
// The tester is read-only: it never clears codes or writes data. Only use diagnostic tools on vehicles you own
// or are authorised to service, with the engine off or the vehicle stationary.
// IoTCom.Net — built by Gravicode Studios, led by Kang Fadhil.
using IoTCom.Net;
using IoTCom.Net.Protocols.Uds;
using IoTCom.Net.Transport.Can;

var uri = args.FirstOrDefault() ?? "virtual:uds-tester";
await using var bus = await CanBus.OpenAsync(uri, o => o.Bitrate = 500_000);

// With the default URI, start a simulated engine ECU on the same virtual bus.
await using var ecu = uri.StartsWith("virtual:", StringComparison.Ordinal) ? EcuSimulator.Create(CanBus.Create(uri)) : null;
if (ecu is not null) await ecu.StartAsync();

await using var uds = UdsClient.Create(bus, o => o.AsReadOnly());
await using var obd = ObdClient.Create(bus, o => o.ReadOnly = true);
await uds.ConnectAsync();
await obd.ConnectAsync();

Console.WriteLine($"IoTCom.Net {IoTComInfo.Version} · UDS tester on {bus.Channel}\n");
Console.WriteLine($"VIN        {await uds.ReadVinAsync()}");
Console.WriteLine($"Part       {await uds.ReadStringAsync(UdsDid.SparePartNumber)}");
Console.WriteLine($"Software   {await uds.ReadStringAsync(UdsDid.SoftwareVersion)}\n");

foreach (var pid in new[] { ObdPids.EngineRpm, ObdPids.VehicleSpeed, ObdPids.CoolantTemperature, ObdPids.ModuleVoltage })
    Console.WriteLine($"{pid.Name,-26} {(await obd.ReadPidAsync(pid)).Value,8:0.0} {pid.Unit}");

Console.WriteLine();
var dtcs = await uds.ReadDtcsAsync();
if (dtcs.Count == 0) Console.WriteLine("No trouble codes stored.");
foreach (var d in dtcs) Console.WriteLine($"DTC {d,-8} status 0x{(byte)d.Status:X2}  {d.Status}");
