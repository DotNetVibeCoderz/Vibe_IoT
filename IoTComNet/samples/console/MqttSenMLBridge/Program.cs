// MqttSenMLBridge — the classic edge pattern: poll a Modbus device, publish SenML to MQTT,
// and accept commands back from MQTT. Runs fully self-contained with --simulate.
//
//   dotnet run -- --simulate                    # virtual PLC + embedded broker on port 1883
//   dotnet run -- --plc 10.0.0.5:502 --broker mqtt.local:1883
//
// Then: iotcom mqtt sub "factory/#"   and   iotcom mqtt pub factory/line1/cmd/motor off
// IoTCom.Net — built by Gravicode Studios, led by Kang Fadhil.
using System.Net;
using IoTCom.Net;
using IoTCom.Net.Adapters.Mqtt;
using IoTCom.Net.Protocols.Modbus;
using IoTCom.Net.Serialization.SenML;

string? Get(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
var simulate = args.Contains("--simulate") || args.Length == 0;
var (plcHost, plcPort) = Split(Get("--plc") ?? "127.0.0.1:1502");
var (brokerHost, brokerPort) = Split(Get("--broker") ?? "127.0.0.1:1883");
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// --simulate: a virtual PLC on TCP 1502 and an embedded MQTT broker.
var store = new ModbusDataStore();
await using var plcSim = simulate ? ModbusServer.Create(o => o.UseTcp(IPAddress.Loopback, plcPort).WithStore(store)) : null;
await using var sim = simulate ? ModbusSimulator.CreateVirtualPlc(store) : null;
await using var broker = simulate ? MqttBroker.Create(brokerPort) : null;
if (plcSim is not null) { await plcSim.StartAsync(); sim!.Start(); await broker!.StartAsync(); }

await using var plc = ModbusClient.Create(o => o.UseTcp(plcHost, plcPort).WithTimeout(TimeSpan.FromSeconds(1)));
await using var mqtt = MqttEndpoint.Create(o => o.UseBroker(brokerHost, brokerPort).WithClientId("iotcom-bridge").WithWill("factory/line1/status", "offline"));
await mqtt.ConnectAsync(cts.Token);
await mqtt.PublishStringAsync("factory/line1/status", "online", new PublishOptions { Retain = true });
Console.WriteLine($"IoTCom.Net {IoTComInfo.Version} · bridge modbus://{plcHost}:{plcPort} → mqtt://{brokerHost}:{brokerPort}. Ctrl+C to stop.");

// Commands: factory/line1/cmd/motor  on|off   ·   factory/line1/cmd/setpoint  <°C>
_ = Task.Run(async () =>
{
    await foreach (var cmd in mqtt.SubscribeStringAsync("factory/line1/cmd/+", cts.Token))
    {
        try
        {
            switch (cmd.Topic.Split('/')[^1])
            {
                case "motor": await plc.WriteSingleCoilAsync(0, cmd.Payload.Trim() is "on" or "1" or "true"); break;
                case "setpoint": await plc.WriteSingleRegisterAsync(0, (ushort)(double.Parse(cmd.Payload, System.Globalization.CultureInfo.InvariantCulture) * 10)); break;
                default: continue;
            }
            Console.WriteLine($"  ⇠ command {cmd.Topic} = {cmd.Payload} applied");
        }
        catch (Exception ex) when (ex is IoTComException or FormatException)
        {
            Console.WriteLine($"  ⇠ command {cmd.Topic} rejected: {ex.Message}");
        }
    }
}, cts.Token);

using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
try
{
    while (await timer.WaitForNextTickAsync(cts.Token))
    {
        try
        {
            var ir = await plc.ReadInputRegistersAsync(0, 8, ct: cts.Token);
            var pack = new SenMLPackBuilder("urn:dev:iotcom:line1:").At(DateTimeOffset.UtcNow)
                .Add("temperature", ir[0] / 10.0, "Cel")
                .Add("humidity", ir[1] / 10.0, "%RH")
                .Add("speed", ir[3], "1/min")
                .Add("power", Math.Round(ModbusConvert.ToSingle(ir.AsSpan(4, 2)) * 1000), "W")
                .Build();
            await mqtt.PublishAsync("factory/line1/telemetry", SenMLCodec.ToJson(pack), new PublishOptions { ContentType = SenMLCodec.JsonContentType }, cts.Token);
            Console.WriteLine($"  ⇢ telemetry  {ir[0] / 10.0:0.0} °C  {ir[3]} rpm");
        }
        catch (IoTComException ex)
        {
            Console.WriteLine($"  PLC unreachable: {ex.Message}");
        }
    }
}
catch (OperationCanceledException) { }
await mqtt.PublishStringAsync("factory/line1/status", "offline", new PublishOptions { Retain = true });

static (string Host, int Port) Split(string hostPort)
{
    var i = hostPort.LastIndexOf(':');
    return (hostPort[..i], int.Parse(hostPort[(i + 1)..]));
}
