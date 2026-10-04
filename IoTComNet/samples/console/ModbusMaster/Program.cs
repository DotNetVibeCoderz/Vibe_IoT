// ModbusMaster — read a PLC over Modbus TCP (or RTU on a serial port) and print live values.
//
//   dotnet run -- --simulate                       # in-process virtual PLC, no hardware
//   dotnet run -- --host 192.168.1.10 --port 502   # a real PLC / gateway
//   dotnet run -- --serial COM3 --baud 19200       # Modbus RTU on RS-485
//
// IoTCom.Net — built by Gravicode Studios, led by Kang Fadhil.
using IoTCom.Net;
using IoTCom.Net.Protocols.Modbus;
using IoTCom.Net.Transport.Serial;
using IoTCom.Net.Transports;

var options = Args.Parse(args);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// --simulate: start a virtual PLC in the same process and connect to it through an in-memory link.
await using var simulation = options.Simulate ? await VirtualPlc.StartAsync() : null;

await using var plc = ModbusClient.Create(o =>
{
    if (simulation is not null) o.UseInMemory(simulation.Listener);
    else if (options.Serial is not null) o.UseSerial(options.Serial, options.Baud, System.IO.Ports.Parity.Even).UseRtuFraming();
    else o.UseTcp(options.Host, options.Port);
    o.WithUnitId(options.Unit)
     .WithTimeout(TimeSpan.FromSeconds(1))
     .WithRetries(1)
     .AsReadOnly(); // this sample never writes: safe to point at real equipment
});

Console.WriteLine($"IoTCom.Net {IoTComInfo.Version} · Modbus master → {(simulation is not null ? "virtual PLC" : options.Serial ?? $"{options.Host}:{options.Port}")}");
var identity = await TryIdentify(plc);
if (identity is not null) Console.WriteLine($"Device: {identity.VendorName} · {identity.ProductCode} · {identity.Revision}");
Console.WriteLine("Ctrl+C to stop.\n");

while (!cts.IsCancellationRequested)
{
    try
    {
        // Input registers 0..7 of the IoTCom.Net virtual PLC map (see ModbusSimulator.CreateVirtualPlc).
        var ir = await plc.ReadInputRegistersAsync(0, 8, ct: cts.Token);
        var counter = (await plc.ReadHoldingRegistersAsync(1, 1, ct: cts.Token))[0];
        var motor = (await plc.ReadCoilsAsync(0, 1, ct: cts.Token))[0];
        Console.WriteLine(
            $"{DateTime.Now:HH:mm:ss}  temp {ir[0] / 10.0,5:0.0} °C  rpm {ir[3],5}  power {ModbusConvert.ToSingle(ir.AsSpan(4, 2)),6:0.00} kW  " +
            $"energy {ModbusConvert.ToSingle(ir.AsSpan(6, 2)),7:0.000} kWh  produced {counter,6}  motor {(motor ? "RUN" : "STOP")}");
    }
    catch (ModbusException ex)
    {
        Console.WriteLine($"Device refused the request: {ex.ExceptionCode} ({ex.Message})");
    }
    catch (IoTComException ex) when (!cts.IsCancellationRequested)
    {
        Console.WriteLine($"Link problem: {ex.Message} — retrying");
    }

    try { await Task.Delay(1000, cts.Token); } catch (OperationCanceledException) { }
}

static async Task<ModbusDeviceIdentification?> TryIdentify(IModbusClient plc)
{
    try { return await plc.ReadDeviceIdentificationAsync(); }
    catch (IoTComException) { return null; } // many devices do not implement 0x2B/0x0E
}

internal sealed record Args(string Host, int Port, string? Serial, int Baud, byte Unit, bool Simulate)
{
    public static Args Parse(string[] a)
    {
        string? Get(string name) => Array.IndexOf(a, name) is var i and >= 0 && i + 1 < a.Length ? a[i + 1] : null;
        return new Args(Get("--host") ?? "127.0.0.1", int.Parse(Get("--port") ?? "502"), Get("--serial"),
            int.Parse(Get("--baud") ?? "9600"), byte.Parse(Get("--unit") ?? "1"), a.Contains("--simulate"));
    }
}

/// <summary>A Modbus slave + simulator living in this process (for --simulate).</summary>
internal sealed class VirtualPlc : IAsyncDisposable
{
    private readonly ModbusServer _server;
    private readonly ModbusSimulator _simulator;

    private VirtualPlc(InMemoryTransportListener listener, ModbusServer server, ModbusSimulator simulator)
        => (Listener, _server, _simulator) = (listener, server, simulator);

    public InMemoryTransportListener Listener { get; }

    public static async Task<VirtualPlc> StartAsync()
    {
        var listener = new InMemoryTransportListener();
        var store = new ModbusDataStore();
        var server = ModbusServer.Create(o => o.ListenInMemory(listener).WithStore(store));
        var simulator = ModbusSimulator.CreateVirtualPlc(store);
        await server.StartAsync();
        simulator.Start();
        return new VirtualPlc(listener, server, simulator);
    }

    public async ValueTask DisposeAsync()
    {
        await _simulator.DisposeAsync();
        await _server.DisposeAsync();
    }
}
