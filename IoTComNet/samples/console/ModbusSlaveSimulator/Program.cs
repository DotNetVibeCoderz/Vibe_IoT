// ModbusSlaveSimulator — a Modbus TCP slave that behaves like a small production line.
// Point SCADA, an HMI, Node-RED or `iotcom modbus read` at it to develop without hardware.
//
//   dotnet run                    # listens on 0.0.0.0:1502 (502 needs admin/root)
//   dotnet run -- --port 502 --unit 1
//
// IoTCom.Net — built by Gravicode Studios, led by Kang Fadhil.
using System.Net;
using IoTCom.Net;
using IoTCom.Net.Protocols.Modbus;

var port = args.Length > 1 && args[0] == "--port" ? int.Parse(args[1]) : 1502;
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var store = new ModbusDataStore();
await using var simulator = ModbusSimulator.CreateVirtualPlc(store, TimeSpan.FromMilliseconds(200));

// Add your own signals on top of the built-in map: a slow sawtooth on holding register 100.
simulator.AddSignal(ModbusTable.HoldingRegisters, 100, t => t % 60, ModbusValueType.UInt16);

await using var server = ModbusServer.Create(o => o
    .UseTcp(IPAddress.Any, port)
    .WithStore(store)
    .WithIdentity("Gravicode Studios", "IoTCom.Net Line Simulator"));

// React to writes from the master, exactly like PLC logic would.
store.Coils.Changed += (_, e) =>
{
    if (e.FromRemote && e.Address == 0) Console.WriteLine($"  ↳ master turned the motor {(store.Coils[0] ? "ON" : "OFF")}");
};
store.HoldingRegisters.Changed += (_, e) =>
{
    if (e.FromRemote && e.Address == 0) Console.WriteLine($"  ↳ master set the setpoint to {store.HoldingRegisters[0] / 10.0:0.0} °C");
};
server.RequestHandled += (_, e) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {e.Peer,-22} {e.Summary}{(e.IsException ? "  ✗" : "")}");

await server.StartAsync(cts.Token);
simulator.Start();

Console.WriteLine($"IoTCom.Net {IoTComInfo.Version} · Modbus slave on tcp://0.0.0.0:{port}");
Console.WriteLine("Register map:");
Console.WriteLine("  IR0 temperature ×10 °C   IR1 humidity ×10 %   IR2 pressure hPa   IR3 motor rpm");
Console.WriteLine("  IR4-5 power kW (float32) IR6-7 energy kWh (float32)");
Console.WriteLine("  HR0 setpoint ×10 °C (rw) HR1 production counter  HR2 alarm word  HR10-21 device name  HR100 sawtooth");
Console.WriteLine("  Coil0 motor run (rw)     Coil1 cooling pump      DI0 door  DI1 e-stop  DI2 high temp");
Console.WriteLine("Try: iotcom modbus read --port " + port + " --table input --count 8\n");

try { await Task.Delay(Timeout.Infinite, cts.Token); } catch (OperationCanceledException) { }
Console.WriteLine($"Stopped after {server.RequestCount} requests.");
