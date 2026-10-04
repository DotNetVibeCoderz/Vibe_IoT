using System.Net;
using IoTComWorker;
using IoTCom.Net;
using IoTCom.Net.Hosting;
using IoTCom.Net.Protocols.Modbus;

// Edge gateway: Modbus device → MQTT (SenML). Configure everything in appsettings.json.
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSystemd();
builder.Services.AddWindowsService(o => o.ServiceName = "IoTComWorker");

var gateway = builder.Configuration.GetSection("Gateway").Get<GatewayOptions>() ?? new GatewayOptions();
builder.Services.AddSingleton(gateway);

var simulatedStore = new ModbusDataStore();
builder.Services.AddIoTCom(iot =>
{
    if (gateway.Simulate)
        iot.AddModbusServer("device-sim", o => o.UseTcp(IPAddress.Loopback, gateway.DevicePort).WithStore(simulatedStore));
    iot.AddModbusClient("device", o => o.UseTcp(gateway.DeviceHost, gateway.DevicePort).WithUnitId(gateway.UnitId).AsReadOnly());
    if (gateway.EmbeddedBroker) iot.AddMqttBroker("broker", gateway.MqttPort);
    iot.AddMqtt("uplink", o => o.UseBroker(gateway.MqttHost, gateway.MqttPort).WithClientId(gateway.ClientId));
    iot.AddHealthChecks();
});
if (gateway.Simulate) builder.Services.AddSingleton(_ => ModbusSimulator.CreateVirtualPlc(simulatedStore));
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
if (gateway.Simulate) host.Services.GetRequiredService<ModbusSimulator>().Start();
host.Run();
