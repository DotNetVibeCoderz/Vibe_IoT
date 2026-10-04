using IoTCom.Net;
using IoTCom.Net.Adapters.Mqtt;
using IoTCom.Net.Hosting;
using IoTCom.Net.Protocols.Modbus;
using IoTCom.Net.Serialization.SenML;
using Microsoft.Extensions.Options;

namespace IoTCom.Gateway;

/// <summary>
/// The bridge: polls the PLC over Modbus, publishes SenML to MQTT, and feeds the dashboard (SSE).
/// </summary>
public sealed class GatewayWorker(IoTComEndpoints endpoints, PlantState plant, IOptions<GatewayOptions> options, ILogger<GatewayWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        var plc = endpoints.GetRequired<ModbusClient>("plc");
        var mqtt = endpoints.GetRequired<MqttEndpoint>("uplink");
        var publishEvery = Math.Max(1, 1000 / Math.Max(50, o.PollIntervalMs));
        var tick = 0;

        // Stream decoded client-side frames (PLC + MQTT) into the dashboard's frame lane.
        endpoints.Tap.FrameCaptured += f =>
        {
            if (f.Endpoint is "plc" || f.Protocol == "mqtt") plant.PublishFrame(PlantState.ToDto(f));
        };

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(o.PollIntervalMs));
        do
        {
            PlantSnapshot snapshot;
            try
            {
                var ir = await plc.ReadInputRegistersAsync(0, 8, ct: stoppingToken);
                var hr = await plc.ReadHoldingRegistersAsync(0, 3, ct: stoppingToken);
                var coils = await plc.ReadCoilsAsync(0, 2, ct: stoppingToken);
                var di = await plc.ReadDiscreteInputsAsync(0, 3, ct: stoppingToken);
                snapshot = new PlantSnapshot(DateTimeOffset.UtcNow, true,
                    ir[0] / 10.0, hr[0] / 10.0, ir[1] / 10.0, ir[2], ir[3],
                    Math.Round(ModbusConvert.ToSingle(ir.AsSpan(4, 2)), 3), Math.Round(ModbusConvert.ToSingle(ir.AsSpan(6, 2)), 4),
                    hr[1], coils[0], coils[1], di[0], di[1], di[2], null);
            }
            catch (IoTComException ex)
            {
                snapshot = (plant.Current ?? Empty()) with { Time = DateTimeOffset.UtcNow, Online = false, Error = ex.Message };
            }
            plant.Publish(snapshot);

            if (snapshot.Online && tick++ % publishEvery == 0 && mqtt.IsConnected)
            {
                var pack = new SenMLPackBuilder($"urn:dev:iotcom:{o.LineName.Replace(' ', '-').ToLowerInvariant()}:plc:")
                    .At(snapshot.Time)
                    .Add("temperature", snapshot.TemperatureC, "Cel")
                    .Add("humidity", snapshot.HumidityPct, "%RH")
                    .Add("pressure", snapshot.PressureHpa * 100, "Pa")
                    .Add("speed", snapshot.MotorRpm, "1/min")
                    .Add("power", snapshot.PowerKw * 1000, "W")
                    .Add("energy", snapshot.EnergyKwh * 3_600_000, "J")
                    .Add("counter", snapshot.Counter, "count")
                    .Add("motor", snapshot.MotorRun)
                    .Build();
                try
                {
                    await mqtt.PublishAsync(o.Mqtt.Topic, SenMLCodec.ToJson(pack), new PublishOptions { ContentType = SenMLCodec.JsonContentType }, stoppingToken);
                }
                catch (IoTComException ex)
                {
                    logger.LogDebug(ex, "MQTT uplink publish failed");
                }
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private static PlantSnapshot Empty() => new(DateTimeOffset.UtcNow, false, 0, 0, 0, 0, 0, 0, 0, 0, false, false, false, false, false, null);
}
