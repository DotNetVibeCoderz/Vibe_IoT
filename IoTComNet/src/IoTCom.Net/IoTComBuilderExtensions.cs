using IoTCom.Net.Adapters.Mqtt;
using IoTCom.Net.Protocols.Coap;
using IoTCom.Net.Protocols.Dmx;
using IoTCom.Net.Protocols.Hl7;
using IoTCom.Net.Protocols.Astm;
using IoTCom.Net.Protocols.AtCommand;
using IoTCom.Net.Protocols.Dlms;
using IoTCom.Net.Protocols.LoRaWan;
using IoTCom.Net.Protocols.MBus;
using IoTCom.Net.Protocols.Mdns;
using IoTCom.Net.Protocols.Sparkplug;
using IoTCom.Net.Protocols.Mavlink;
using IoTCom.Net.Protocols.Modbus;
using IoTCom.Net.Protocols.Nmea;
using IoTCom.Net.Transport.Ble;
using IoTCom.Net.Transport.Can;
using IoTCom.Net.Transport.Usb;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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

    /// <summary>Registers a CoAP client.</summary>
    public static IoTComBuilder AddCoapClient(this IoTComBuilder builder, string name, Action<CoapClientOptions> configure)
        => builder.AddEndpoint(name, sp => CoapClient.Create(o =>
        {
            o.Name = name;
            o.Logger = Logger(sp, "IoTCom.Coap");
            configure(o);
        }));

    /// <summary>Registers a CoAP server; map resources in <paramref name="resources"/>.</summary>
    public static IoTComBuilder AddCoapServer(this IoTComBuilder builder, string name, Action<CoapServerOptions>? configure = null, Action<CoapServer>? resources = null)
        => builder.AddEndpoint(name, sp =>
        {
            var server = CoapServer.Create(o =>
            {
                o.Name = name;
                o.Logger = Logger(sp, "IoTCom.Coap");
                configure?.Invoke(o);
            });
            resources?.Invoke(server);
            return server;
        });

    /// <summary>Registers a MAVLink connection (e.g. <c>o =&gt; o.UseUdp(14550)</c> for a ground station).</summary>
    public static IoTComBuilder AddMavlink(this IoTComBuilder builder, string name, Action<MavlinkConnectionOptions> configure)
        => builder.AddEndpoint(name, sp => MavlinkConnection.Create(o =>
        {
            o.Name = name;
            o.Logger = Logger(sp, "IoTCom.Mavlink");
            configure(o);
        }));

    /// <summary>
    /// Registers a light LoRaWAN network server (Semtech UDP, default port 1700); register devices in
    /// <paramref name="devices"/> and subscribe to <see cref="LoRaWanNetworkServer.UplinkReceived"/>.
    /// </summary>
    public static IoTComBuilder AddLoRaWanNetworkServer(this IoTComBuilder builder, string name, Action<LoRaWanNetworkServerOptions>? configure = null, Action<LoRaWanNetworkServer>? devices = null)
        => builder.AddEndpoint(name, sp =>
        {
            var server = LoRaWanNetworkServer.Create(o =>
            {
                o.Name = name;
                o.Logger = Logger(sp, "IoTCom.LoRaWan");
                configure?.Invoke(o);
            });
            devices?.Invoke(server);
            return server;
        });

    /// <summary>Registers a DLMS/COSEM client (a meter reader; read-only unless the options say otherwise).</summary>
    public static IoTComBuilder AddDlmsClient(this IoTComBuilder builder, string name, Action<DlmsClientOptions> configure)
        => builder.AddEndpoint(name, sp => DlmsClient.Create(o =>
        {
            o.Name = name;
            o.Logger = Logger(sp, "IoTCom.Dlms");
            configure(o);
        }));

    /// <summary>Registers an M-Bus master.</summary>
    public static IoTComBuilder AddMBusMaster(this IoTComBuilder builder, string name, Action<MBusMasterOptions> configure)
        => builder.AddEndpoint(name, sp => MBusMaster.Create(o =>
        {
            o.Name = name;
            o.Logger = Logger(sp, "IoTCom.MBus");
            configure(o);
        }));

    /// <summary>Registers an AT-command modem (cellular / GNSS module).</summary>
    public static IoTComBuilder AddAtModem(this IoTComBuilder builder, string name, Action<AtModemOptions> configure)
        => builder.AddEndpoint(name, sp => AtModem.Create(o =>
        {
            o.Name = name;
            o.Logger = Logger(sp, "IoTCom.At");
            configure(o);
        }));

    /// <summary>Registers an ASTM receiver (LIS side) for lab analyzers.</summary>
    public static IoTComBuilder AddAstmReceiver(this IoTComBuilder builder, string name, Action<AstmReceiverOptions> configure)
        => builder.AddEndpoint(name, sp => AstmReceiver.Create(o =>
        {
            o.Name = name;
            o.Logger = Logger(sp, "IoTCom.Astm");
            configure(o);
        }));

    /// <summary>Registers an mDNS responder; services registered on it are advertised on the local network.</summary>
    public static IoTComBuilder AddMdnsResponder(this IoTComBuilder builder, string name, Action<MdnsOptions>? configure = null)
        => builder.AddEndpoint(name, sp => MdnsResponder.Create(o =>
        {
            o.Name = name;
            o.Logger = Logger(sp, "IoTCom.Mdns");
            configure?.Invoke(o);
        }));

    /// <summary>Registers an mDNS / DNS-SD browser.</summary>
    public static IoTComBuilder AddMdnsBrowser(this IoTComBuilder builder, string name, Action<MdnsOptions>? configure = null)
        => builder.AddEndpoint(name, sp => MdnsBrowser.Create(o =>
        {
            o.Name = name;
            o.Logger = Logger(sp, "IoTCom.Mdns");
            configure?.Invoke(o);
        }));

    /// <summary>
    /// Registers a Sparkplug B edge node as a singleton, started (births) with the host and stopped (deaths) with it.
    /// Define devices and metrics in <paramref name="define"/>.
    /// </summary>
    public static IoTComBuilder AddSparkplugEdgeNode(this IoTComBuilder builder, Action<SparkplugEdgeNodeOptions> configure, Action<SparkplugEdgeNode>? define = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddSingleton(sp =>
        {
            var node = SparkplugEdgeNode.Create(o =>
            {
                o.Logger = Logger(sp, "IoTCom.Sparkplug");
                configure(o);
            });
            define?.Invoke(node);
            return node;
        });
        builder.Services.AddHostedService(sp => new SparkplugLifetime(sp.GetRequiredService<SparkplugEdgeNode>().StartAsync, ct => sp.GetRequiredService<SparkplugEdgeNode>().StopAsync(ct)));
        return builder;
    }

    /// <summary>Registers a Sparkplug B host application as a singleton, started and stopped with the host.</summary>
    public static IoTComBuilder AddSparkplugHost(this IoTComBuilder builder, Action<SparkplugHostOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddSingleton(sp => SparkplugHost.Create(o =>
        {
            o.Logger = Logger(sp, "IoTCom.Sparkplug");
            configure(o);
        }));
        builder.Services.AddHostedService(sp => new SparkplugLifetime(sp.GetRequiredService<SparkplugHost>().StartAsync, _ => sp.GetRequiredService<SparkplugHost>().StopAsync()));
        return builder;
    }

    private sealed class SparkplugLifetime(Func<CancellationToken, Task> start, Func<CancellationToken, Task> stop) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => start(cancellationToken);

        public Task StopAsync(CancellationToken cancellationToken) => stop(cancellationToken);
    }

    /// <summary>Registers a Bluetooth LE central (native radio unless <paramref name="configure"/> chooses a virtual one).</summary>
    public static IoTComBuilder AddBleCentral(this IoTComBuilder builder, string name, Action<BleCentralOptions>? configure = null)
        => builder.AddEndpoint(name, sp => BleCentral.Create(o =>
        {
            o.Name = name;
            o.Logger = Logger(sp, "IoTCom.Ble");
            configure?.Invoke(o);
        }));

    /// <summary>Registers a raw USB device (control, bulk and interrupt transfers).</summary>
    public static IoTComBuilder AddUsbDevice(this IoTComBuilder builder, string name, Action<UsbDeviceOptions> configure)
        => builder.AddEndpoint(name, sp => UsbDevice.Create(o =>
        {
            o.Name = name;
            o.Logger = Logger(sp, "IoTCom.Usb");
            configure(o);
        }));

    /// <summary>Registers a HID device.</summary>
    public static IoTComBuilder AddHidDevice(this IoTComBuilder builder, string name, Action<HidDeviceOptions> configure)
        => builder.AddEndpoint(name, sp => HidDevice.Create(o =>
        {
            o.Name = name;
            o.Logger = Logger(sp, "IoTCom.Hid");
            configure(o);
        }));

    /// <summary>Registers a CAN bus by URI (<c>socketcan:can0</c>, <c>slcan:COM5</c>, <c>virtual:demo</c>).</summary>
    public static IoTComBuilder AddCanBus(this IoTComBuilder builder, string name, string uri, Action<CanBusOptions>? configure = null)
        => builder.AddEndpoint(name, sp => CanBus.Create(uri, o =>
        {
            o.Name = name;
            o.Logger = Logger(sp, "IoTCom.Can");
            configure?.Invoke(o);
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
