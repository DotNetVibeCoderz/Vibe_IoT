using IoTCom.Net.Adapters.Mqtt;
using IoTCom.Net.Protocols.Dmx;
using IoTCom.Net.Protocols.Hl7;
using IoTCom.Net.Protocols.Modbus;
using IoTCom.Net.Protocols.Nmea;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Hosting;

/// <summary>Protocol-specific registrations for <see cref="IoTComBuilder"/>.</summary>
/// <example>
/// <code>
/// builder.Services.AddIoTCom(iot => iot
///     .AddModbusClient("plc1", o => o.UseTcp("10.0.0.5", 502))
///     .AddMqtt("cloud", o => o.UseBroker("broker.local"))
///     .AddHealthChecks());
/// </code>
/// </example>
public static class IoTComBuilderExtensions
{
    /// <summary>Registers a Modbus master.</summary>
    public static IoTComBuilder AddModbusClient(this IoTComBuilder builder, string name, Action<ModbusClientOptions> configure)
        => builder.AddEndpoint(name, sp => ModbusClient.Create(o =>
        {
            o.Name = name;
            o.Logger = Logger(sp, "IoTCom.Modbus");
            configure(o);
        }));

    /// <summary>Registers a Modbus slave.</summary>
    public static IoTComBuilder AddModbusServer(this IoTComBuilder builder, string name, Action<ModbusServerOptions> configure)
        => builder.AddEndpoint(name, sp => ModbusServer.Create(o =>
        {
            o.Name = name;
            o.Logger = Logger(sp, "IoTCom.Modbus");
            configure(o);
        }));

    /// <summary>Registers an MQTT client.</summary>
    public static IoTComBuilder AddMqtt(this IoTComBuilder builder, string name, Action<MqttEndpointOptions> configure)
        => builder.AddEndpoint(name, sp => MqttEndpoint.Create(o =>
        {
            o.Name = name;
            o.Logger = Logger(sp, "IoTCom.Mqtt");
            configure(o);
        }));

    /// <summary>Registers an embedded MQTT broker.</summary>
    public static IoTComBuilder AddMqttBroker(this IoTComBuilder builder, string name, int port = 1883)
        => builder.AddEndpoint(name, sp => MqttBroker.Create(port, Logger(sp, "IoTCom.MqttBroker")));

    /// <summary>Registers an NMEA 0183 reader.</summary>
    public static IoTComBuilder AddNmeaReader(this IoTComBuilder builder, string name, Action<NmeaReaderOptions> configure)
        => builder.AddEndpoint(name, sp => NmeaReader.Create(o =>
        {
            o.Name = name;
            o.Logger = Logger(sp, "IoTCom.Nmea");
            configure(o);
        }));

    /// <summary>Registers an HL7 MLLP receiver (auto-acknowledges every message).</summary>
    public static IoTComBuilder AddHl7Server(this IoTComBuilder builder, string name, Action<Hl7MllpServerOptions> configure)
        => builder.AddEndpoint(name, sp => Hl7MllpServer.Create(o =>
        {
            o.Name = name;
            o.Logger = Logger(sp, "IoTCom.Hl7");
            configure(o);
        }));

    /// <summary>Registers an HL7 MLLP sender.</summary>
    public static IoTComBuilder AddHl7Client(this IoTComBuilder builder, string name, Action<Hl7MllpClientOptions> configure)
        => builder.AddEndpoint(name, sp => Hl7MllpClient.Create(o =>
        {
            o.Logger = Logger(sp, "IoTCom.Hl7");
            configure(o);
        }));

    /// <summary>Registers an Art-Net node.</summary>
    public static IoTComBuilder AddArtNet(this IoTComBuilder builder, string name, Action<DmxNodeOptions>? configure = null)
        => builder.AddEndpoint(name, sp => ArtNetNode.Create(o =>
        {
            o.Logger = Logger(sp, "IoTCom.ArtNet");
            configure?.Invoke(o);
        }));

    /// <summary>Registers an sACN node.</summary>
    public static IoTComBuilder AddSacn(this IoTComBuilder builder, string name, Action<DmxNodeOptions>? configure = null)
        => builder.AddEndpoint(name, sp => SacnNode.Create(o =>
        {
            o.Logger = Logger(sp, "IoTCom.Sacn");
            configure?.Invoke(o);
        }));

    private static ILogger? Logger(IServiceProvider sp, string category) => sp.GetService<ILoggerFactory>()?.CreateLogger(category);
}
