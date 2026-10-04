using IoTCom.Net;
using IoTCom.Net.Transports;
#if (isModbus)
using IoTCom.Net.Protocols.Modbus;
#elif (isNmea)
using IoTCom.Net.Protocols.Nmea;
#else
using IoTCom.Net.Adapters.Mqtt;
#endif

#if (isId)
// Dibuat dengan template IoTCom.Net. Jalankan: dotnet run  (memakai simulator bawaan, tanpa perangkat keras)
// Ganti UseInMemory(...) dengan UseTcp(...) atau UseSerial(...) untuk terhubung ke perangkat sungguhan.
#else
// Created from the IoTCom.Net template. Run: dotnet run  (uses the built-in simulator, no hardware needed)
// Replace UseInMemory(...) with UseTcp(...) or UseSerial(...) to talk to a real device.
#endif
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
#if (isModbus)

// Simulated device: a Modbus slave animated as a virtual PLC.
var link = new InMemoryTransportListener();
var store = new ModbusDataStore();
await using var device = ModbusServer.Create(o => o.ListenInMemory(link).WithStore(store));
await using var simulator = ModbusSimulator.CreateVirtualPlc(store);
await device.StartAsync();
simulator.Start();

// Your application: a Modbus master. For a real PLC: o.UseTcp("192.168.1.10", 502)
await using var plc = ModbusClient.Create(o => o.UseInMemory(link).WithUnitId(1).WithTimeout(TimeSpan.FromSeconds(1)));
while (!cts.IsCancellationRequested)
{
    var registers = await plc.ReadInputRegistersAsync(0, 4);
    Console.WriteLine($"temperature {registers[0] / 10.0:0.0} °C · motor {registers[3]} rpm");
    try { await Task.Delay(1000, cts.Token); } catch (OperationCanceledException) { }
}
#elif (isNmea)

// Simulated device: a GPS receiver publishing NMEA 0183.
var link = new InMemoryTransportListener();
await using var receiver = NmeaServer.Create(o => o.ListenInMemory(link));
await receiver.StartAsync();
_ = new NmeaSimulator().RunAsync(receiver, ct: cts.Token);

// Your application: an NMEA reader. For a real GPS: o.UseSerial("COM4", 9600)
await using var gps = NmeaReader.Create(o => o.UseInMemory(link));
await gps.ConnectAsync();
try
{
    await foreach (var gga in gps.ReadAsync<GgaMessage>(cts.Token))
        Console.WriteLine($"{gga.Time} · {gga.Latitude:0.00000}, {gga.Longitude:0.00000} · {gga.Satellites} satellites");
}
catch (OperationCanceledException) { }
#else

// Embedded broker for local development. For a real broker: o.UseBroker("broker.example.com")
await using var broker = MqttBroker.Create(1883);
await broker.StartAsync();
await using var mqtt = MqttEndpoint.Create(o => o.UseBroker("127.0.0.1", 1883));
await mqtt.ConnectAsync();

_ = Task.Run(async () =>
{
    await foreach (var message in mqtt.SubscribeStringAsync("demo/#", cts.Token))
        Console.WriteLine($"received {message.Topic}: {message.Payload}");
});
while (!cts.IsCancellationRequested)
{
    await mqtt.PublishStringAsync("demo/hello", $"Hello from IoTCom.Net at {DateTime.Now:T}");
    try { await Task.Delay(2000, cts.Token); } catch (OperationCanceledException) { }
}
#endif
