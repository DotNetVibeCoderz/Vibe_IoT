using IoTCom.Net;
using IoTCom.Net.Adapters.Mqtt;
using IoTCom.Net.Hosting;
using IoTCom.Net.Protocols.Modbus;
using IoTCom.Net.Serialization.SenML;

namespace IoTComWorker;

/// <summary>Gateway settings (appsettings.json → "Gateway").</summary>
public sealed class GatewayOptions
{
    public bool Simulate { get; set; } = true;
    public string DeviceHost { get; set; } = "127.0.0.1";
    public int DevicePort { get; set; } = 1502;
    public byte UnitId { get; set; } = 1;
    public bool EmbeddedBroker { get; set; } = true;
    public string MqttHost { get; set; } = "127.0.0.1";
    public int MqttPort { get; set; } = 1883;
    public string ClientId { get; set; } = "iotcom-worker";
    public string Topic { get; set; } = "site/line1/telemetry";
    public string BaseName { get; set; } = "urn:dev:iotcom:line1:";
    public int IntervalSeconds { get; set; } = 5;
}

/// <summary>Polls the device and publishes SenML telemetry.</summary>
public sealed class Worker(IoTComEndpoints endpoints, GatewayOptions options, ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var device = endpoints.GetRequired<ModbusClient>("device");
        var uplink = endpoints.GetRequired<MqttEndpoint>("uplink");
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.IntervalSeconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                // Adjust the register map to your device.
                var ir = await device.ReadInputRegistersAsync(0, 4, ct: stoppingToken);
                var pack = new SenMLPackBuilder(options.BaseName).At(DateTimeOffset.UtcNow)
                    .Add("temperature", ir[0] / 10.0, "Cel")
                    .Add("humidity", ir[1] / 10.0, "%RH")
                    .Add("pressure", ir[2] * 100, "Pa")
                    .Build();
                await uplink.PublishAsync(options.Topic, SenMLCodec.ToJson(pack), new PublishOptions { ContentType = SenMLCodec.JsonContentType }, stoppingToken);
                logger.LogInformation("Published {Count} measurements to {Topic}", pack.Count, options.Topic);
            }
            catch (IoTComException ex)
            {
                logger.LogWarning("Telemetry cycle failed: {Message}", ex.Message);
            }
        }
    }
}
