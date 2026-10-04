using System.Net;
using IoTCom.Gateway;
using IoTCom.Net;
using IoTCom.Net.Hosting;
using IoTCom.Net.Protocols.Modbus;
using Microsoft.Extensions.Options;

// IoTCom.Gateway — bridges a Modbus PLC to MQTT (SenML) and serves a live HMI dashboard.
// Built by Gravicode Studios, led by Kang Fadhil.

var builder = WebApplication.CreateSlimBuilder(args);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, GatewayJson.Default));
builder.Services.Configure<GatewayOptions>(builder.Configuration.GetSection("Gateway"));
var gw = builder.Configuration.GetSection("Gateway").Get<GatewayOptions>() ?? new GatewayOptions();

// One shared store so the simulator animates exactly what the Modbus slave serves.
var simStore = new ModbusDataStore();
builder.Services.AddIoTCom(iot =>
{
    if (gw.Simulate)
        iot.AddModbusServer("plc-sim", o => o.UseTcp(IPAddress.Loopback, gw.Plc.Port).WithStore(simStore).WithIdentity("Gravicode Studios", "Virtual PLC"));
    iot.AddModbusClient("plc", o => o.UseTcp(gw.Plc.Host, gw.Plc.Port).WithUnitId(gw.Plc.UnitId).WithTimeout(TimeSpan.FromSeconds(1))
        .WithReconnect(ReconnectPolicy.Default with { MaxDelay = TimeSpan.FromSeconds(5) }));
    if (gw.Mqtt.EmbeddedBroker) iot.AddMqttBroker("broker", gw.Mqtt.Port);
    iot.AddMqtt("uplink", o => o.UseBroker(gw.Mqtt.Host, gw.Mqtt.Port).WithClientId($"iotcom-gateway-{Environment.MachineName}".ToLowerInvariant()));
    iot.AddHealthChecks();
});
builder.Services.AddSingleton<PlantState>();
builder.Services.AddHostedService<GatewayWorker>();
if (gw.Simulate) builder.Services.AddHostedService(_ => new SimulatorService(simStore));

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapHealthChecks("/health");

var api = app.MapGroup("/api");
api.MapGet("/info", (IOptions<GatewayOptions> o) => new InfoDto(o.Value.LineName, IoTComInfo.Product, IoTComInfo.Version, IoTComInfo.CreditEn, IoTComInfo.CreditId,
    $"{o.Value.Plc.Host}:{o.Value.Plc.Port}", o.Value.Mqtt.Topic, o.Value.AllowWrites, o.Value.Simulate));
api.MapGet("/snapshot", (PlantState p) => p.Current is { } s ? Results.Ok(s) : Results.NoContent());
api.MapGet("/history", (PlantState p) => p.History());
api.MapGet("/frames", (IoTComEndpoints e) => (IReadOnlyList<FrameDto>)e.Tap.Snapshot()
    .Where(f => f.Endpoint is "plc" || f.Protocol == "mqtt").TakeLast(40).Select(PlantState.ToDto).ToArray());
api.MapGet("/endpoints", (IoTComEndpoints e) => (IReadOnlyList<EndpointDto>)e.Names.Select(n => new EndpointDto(n, e.Get(n).Protocol, e.Get(n).State.ToString())).ToArray());
api.MapGet("/stream", (PlantState p, CancellationToken ct) => TypedResults.ServerSentEvents(p.Subscribe(ct)));

api.MapPost("/motor", async (MotorRequest req, IoTComEndpoints e, IOptions<GatewayOptions> o, CancellationToken ct) =>
{
    if (!o.Value.AllowWrites) return Results.Json(new ProblemDto("Writes disabled", "Set Gateway:AllowWrites to true to control the motor."), GatewayJson.Default.ProblemDto, statusCode: 403);
    await e.GetRequired<ModbusClient>("plc").WriteSingleCoilAsync(0, req.Run, ct: ct);
    return Results.NoContent();
});
api.MapPost("/setpoint", async (SetpointRequest req, IoTComEndpoints e, IOptions<GatewayOptions> o, CancellationToken ct) =>
{
    if (!o.Value.AllowWrites) return Results.Json(new ProblemDto("Writes disabled", "Set Gateway:AllowWrites to true to change the setpoint."), GatewayJson.Default.ProblemDto, statusCode: 403);
    if (req.Celsius is < 5 or > 60) return Results.Json(new ProblemDto("Setpoint out of range", "Choose a temperature between 5 and 60 °C."), GatewayJson.Default.ProblemDto, statusCode: 400);
    await e.GetRequired<ModbusClient>("plc").WriteSingleRegisterAsync(0, (ushort)Math.Round(req.Celsius * 10), ct: ct);
    return Results.NoContent();
});

app.Run();

/// <summary>Runs the virtual PLC animation while the gateway is in simulation mode.</summary>
internal sealed class SimulatorService(ModbusDataStore store) : IHostedService, IAsyncDisposable
{
    private readonly ModbusSimulator _sim = ModbusSimulator.CreateVirtualPlc(store, TimeSpan.FromMilliseconds(200));

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sim.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => _sim.StopAsync().AsTask();

    public ValueTask DisposeAsync() => _sim.DisposeAsync();
}
