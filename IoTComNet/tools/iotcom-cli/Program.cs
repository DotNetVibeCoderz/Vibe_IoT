using IoTCom.Net.Cli;
using IoTCom.Net.Cli.Commands;
using Spectre.Console;
using Spectre.Console.Cli;

Console.OutputEncoding = System.Text.Encoding.UTF8;
if (Environment.GetEnvironmentVariable("IOTCOM_FORCE_ANSI") == "1")
{
    // Keep colours when output is redirected (docs screenshots, CI logs).
    AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings { Ansi = AnsiSupport.Yes, ColorSystem = ColorSystemSupport.TrueColor });
    AnsiConsole.Profile.Width = 100;
}

var app = new CommandApp<InfoCommand>();
app.Configure(c =>
{
    c.SetApplicationName("iotcom");
    c.SetApplicationVersion(IoTCom.Net.IoTComInfo.Version);
    c.AddCommand<InfoCommand>("info").WithDescription("Show version, credits and supported protocols.");
    c.AddCommand<PortsCommand>("ports").WithDescription("List serial ports on this machine.");
    c.AddCommand<RpcCommand>("rpc").WithDescription("JSON-RPC 2.0 over stdio for editors (used by the VS Code extension).").IsHidden();
    c.AddCommand<CrcCommand>("crc").WithDescription("Compute CRCs (20+ presets) of hex or text input.")
        .WithExample("crc", "01 03 00 00 00 0A", "--algorithm", "modbus")
        .WithExample("crc", "--text", "123456789", "--all");
    c.AddBranch("frame", f =>
    {
        f.SetDescription("Encode/decode SLIP, COBS and HDLC frames.");
        f.AddCommand<FrameEncodeCommand>("encode").WithExample("frame", "encode", "cobs", "11 22 00 33");
        f.AddCommand<FrameDecodeCommand>("decode").WithExample("frame", "decode", "slip", "C0 DB DC 01 C0");
    });
    c.AddBranch("sniff", n =>
    {
        n.SetDescription("Watch live traffic: a transparent TCP/UDP relay or a passive CAN capture, decoded and written to pcapng.");
        n.AddCommand<SniffTcpCommand>("tcp").WithDescription("Proxy a TCP device (Modbus/TCP, HL7/MLLP or raw) and decode both directions.")
            .WithExample("sniff", "tcp", "--listen", "1502", "--target", "192.168.1.10:502", "--pcap", "plc.pcapng");
        n.AddCommand<SniffUdpCommand>("udp").WithDescription("Relay a UDP device (CoAP, MAVLink or raw) and decode both directions.")
            .WithExample("sniff", "udp", "--listen", "15683", "--target", "192.168.1.40:5683", "--lanes");
        n.AddCommand<SniffCanCommand>("can").WithDescription("Capture a CAN bus (listen only).")
            .WithExample("sniff", "can", "--can", "socketcan:can0", "--pcap", "can.pcapng");
    });
    c.AddBranch("modbus", m =>
    {
        m.SetDescription("Modbus TCP / RTU / ASCII master, slave simulator and frame decoder.");
        m.AddCommand<ModbusReadCommand>("read").WithDescription("Read coils, inputs or registers.")
            .WithExample("modbus", "read", "--host", "127.0.0.1", "--port", "1502", "--table", "input", "--count", "8")
            .WithExample("modbus", "read", "--serial", "COM3", "--baud", "19200", "--rtu", "--unit", "2");
        m.AddCommand<ModbusWriteCommand>("write").WithDescription("Write coils or holding registers (requires --allow-write).")
            .WithExample("modbus", "write", "--host", "127.0.0.1", "--port", "1502", "--address", "0", "--values", "260", "--allow-write");
        m.AddCommand<ModbusServeCommand>("serve").WithDescription("Run a Modbus slave, optionally as a live virtual PLC.")
            .WithExample("modbus", "serve", "--port", "1502", "--simulate");
        m.AddCommand<ModbusDecodeCommand>("decode").WithDescription("Decode a frame field by field.")
            .WithExample("modbus", "decode", "01 03 00 00 00 0A C5 CD", "--mode", "rtu");
    });
    c.AddBranch("nmea", n =>
    {
        n.SetDescription("NMEA 0183 GPS/GNSS tools.");
        n.AddCommand<NmeaListenCommand>("listen").WithExample("nmea", "listen", "--host", "127.0.0.1", "--port", "10110");
        n.AddCommand<NmeaSimulateCommand>("simulate").WithExample("nmea", "simulate", "--port", "10110");
    });
    c.AddBranch("mavlink", m =>
    {
        m.SetDescription("MAVLink v1/v2: listen, simulate a quadcopter, send commands, read/write parameters, decode frames.");
        m.AddCommand<MavlinkListenCommand>("listen").WithExample("mavlink", "listen", "--stats")
            .WithExample("mavlink", "listen", "--serial", "COM7", "--baud", "57600", "--filter", "STATUSTEXT,HEARTBEAT");
        m.AddCommand<MavlinkSimulateCommand>("simulate").WithExample("mavlink", "simulate", "--to", "127.0.0.1:14550");
        m.AddCommand<MavlinkCommandCommand>("cmd").WithDescription("arm, disarm, takeoff, land, rtl (requires --allow-write).")
            .WithExample("mavlink", "cmd", "takeoff", "15", "--allow-write");
        m.AddCommand<MavlinkParamsCommand>("params").WithExample("mavlink", "params")
            .WithExample("mavlink", "params", "RTL_ALT", "2000", "--allow-write");
        m.AddCommand<MavlinkDecodeCommand>("decode").WithExample("mavlink", "decode", "FE09000101000000000002035104037DDD");
    });
    c.AddBranch("artnet", a =>
    {
        a.SetDescription("Art-Net 4 lighting control.");
        a.AddCommand<ArtNetSendCommand>("send").WithExample("artnet", "send", "--universe", "0", "--values", "255,128,0");
        a.AddCommand<ArtNetPollCommand>("poll");
        a.AddCommand<ArtNetMonitorCommand>("monitor");
    });
    c.AddBranch("can", b =>
    {
        b.SetDescription("CAN / CAN FD: list interfaces, dump, send, and an ECU simulator behind an slcan-over-TCP adapter.");
        b.AddCommand<CanListCommand>("list");
        b.AddCommand<CanDumpCommand>("dump").WithExample("can", "dump", "--can", "socketcan:can0", "--filter", "7E8:7F8")
            .WithExample("can", "dump", "--can", "sim");
        b.AddCommand<CanSendCommand>("send").WithExample("can", "send", "--can", "slcan:COM5", "7DF#02010C");
        b.AddCommand<CanSimulateCommand>("simulate").WithExample("can", "simulate", "--port", "20100");
    });
    c.AddBranch("uds", u =>
    {
        u.SetDescription("UDS (ISO 14229) diagnostics over ISO-TP: identification, DTCs, raw requests.");
        u.AddCommand<UdsReadCommand>("read").WithExample("uds", "read", "--can", "sim")
            .WithExample("uds", "read", "--can", "socketcan:can0", "--did", "F190", "--did", "F18C");
        u.AddCommand<UdsDtcCommand>("dtc").WithExample("uds", "dtc", "--can", "sim")
            .WithExample("uds", "dtc", "--can", "slcan:COM5", "--clear", "--allow-write");
        u.AddCommand<UdsRawCommand>("raw").WithExample("uds", "raw", "--can", "sim", "1003");
    });
    c.AddBranch("obd", o =>
    {
        o.SetDescription("OBD-II scan tool (SAE J1979 over CAN): live data, VIN, DTCs.");
        o.AddCommand<ObdLiveCommand>("live").WithExample("obd", "live", "--can", "sim", "--watch", "500");
        o.AddCommand<ObdVinCommand>("vin").WithExample("obd", "vin", "--can", "slcan:COM5");
        o.AddCommand<ObdDtcCommand>("dtc").WithExample("obd", "dtc", "--can", "sim");
    });
    c.AddBranch("coap", q =>
    {
        q.SetDescription("CoAP (RFC 7252) client and a simulated greenhouse device: get, put, observe, discover, ping, serve.");
        q.AddCommand<CoapGetCommand>("get").WithExample("coap", "get", "coap://127.0.0.1/sensors/temperature", "--accept", "senml");
        q.AddCommand<CoapWriteCommand>("put").WithDescription("PUT/POST/DELETE (requires --allow-write).")
            .WithExample("coap", "put", "coap://127.0.0.1/actuators/fan", "on", "--allow-write")
            .WithExample("coap", "put", "coap://127.0.0.1/firmware", "@image.bin", "--format", "octet", "--allow-write");
        q.AddCommand<CoapObserveCommand>("observe").WithExample("coap", "observe", "coap://127.0.0.1/sensors/temperature");
        q.AddCommand<CoapDiscoverCommand>("discover").WithExample("coap", "discover", "coap://127.0.0.1/", "--query", "rt=temperature*");
        q.AddCommand<CoapPingCommand>("ping").WithExample("coap", "ping", "coap://127.0.0.1/");
        q.AddCommand<CoapServeCommand>("serve").WithExample("coap", "serve", "--port", "5683", "--frames");
    });
    c.AddBranch("lorawan", l =>
    {
        l.SetDescription("LoRaWAN 1.0.x: decode frames, run a light network server (Semtech UDP), simulate gateways and devices, airtime.");
        l.AddCommand<LoRaWanDecodeCommand>("decode")
            .WithExample("lorawan", "decode", "40F17DBE4900020001954378762B11FF0D", "--nwkskey", "44024241ED4CE9A68C6A8BC055233FD3", "--appskey", "EC925802AE430CA77FD3DD73CB2CC588");
        l.AddCommand<LoRaWanServerCommand>("server").WithExample("lorawan", "server", "--sim", "--region", "AS923")
            .WithExample("lorawan", "server", "--port", "1700", "--devices", "devices.json", "--pcap", "lora.pcapng");
        l.AddCommand<LoRaWanSimulateCommand>("simulate").WithExample("lorawan", "simulate", "--server", "chirpstack.local:1700", "--region", "EU868");
        l.AddCommand<LoRaWanAirtimeCommand>("airtime").WithExample("lorawan", "airtime", "12", "--region", "AS923");
    });
    c.AddBranch("dlms", d =>
    {
        d.SetDescription("DLMS/COSEM smart meters (IEC 62056): read registers, objects, load profile, relay; simulate a meter.");
        d.AddCommand<DlmsReadCommand>("read").WithExample("dlms", "read", "--sim").WithExample("dlms", "read", "--serial", "COM3", "1.0.1.8.0.255");
        d.AddCommand<DlmsObjectsCommand>("objects").WithExample("dlms", "objects", "-h", "10.0.0.30");
        d.AddCommand<DlmsProfileCommand>("profile").WithExample("dlms", "profile", "--sim", "--hours", "3");
        d.AddCommand<DlmsRelayCommand>("relay").WithDescription("Disconnect control (requires --allow-write and a management client).")
            .WithExample("dlms", "relay", "off", "--sim", "--password", "12345678", "--allow-write");
        d.AddCommand<DlmsSimulateCommand>("simulate").WithExample("dlms", "simulate", "--port", "4059");
    });
    c.AddBranch("mbus", m =>
    {
        m.SetDescription("Wired M-Bus (EN 13757): scan, read (primary or secondary address), decode; simulate a segment.");
        m.AddCommand<MBusScanCommand>("scan").WithExample("mbus", "scan", "--sim", "--to", "10").WithExample("mbus", "scan", "--serial", "COM4");
        m.AddCommand<MBusReadCommand>("read").WithExample("mbus", "read", "1", "--sim").WithExample("mbus", "read", "26200002", "--sim");
        m.AddCommand<MBusDecodeCommand>("decode").WithExample("mbus", "decode", "681F1F680802727856341224400107550000000313153100DA023B13018B60043718021816");
        m.AddCommand<MBusSimulateCommand>("simulate").WithExample("mbus", "simulate", "--port", "10001");
    });
    c.AddBranch("hl7", h =>
    {
        h.SetDescription("HL7 v2 over MLLP: receive (auto-ACK), send, and simulate a bedside monitor.");
        h.AddCommand<Hl7ListenCommand>("listen").WithExample("hl7", "listen", "--port", "2575");
        h.AddCommand<Hl7SendCommand>("send").WithExample("hl7", "send", "message.hl7", "--host", "10.0.0.20");
        h.AddCommand<Hl7SimulateCommand>("simulate").WithExample("hl7", "simulate", "--scenario", "sepsis", "--interval", "2");
    });
    c.AddBranch("dicom", d =>
    {
        d.SetDescription("DICOM networking (fo-dicom adapter): C-ECHO, C-STORE send, Storage SCP.");
        d.AddCommand<DicomEchoCommand>("echo").WithExample("dicom", "echo", "--host", "pacs.local", "--port", "104", "--aec", "PACS");
        d.AddCommand<DicomSendCommand>("send").WithExample("dicom", "send", "--synthetic", "ct", "--finding", "Pneumothorax");
        d.AddCommand<DicomListenCommand>("listen").WithExample("dicom", "listen", "--port", "11112", "--output", "received");
    });
    c.AddBranch("mqtt", q =>
    {
        q.SetDescription("MQTT publish, subscribe and embedded broker.");
        q.AddCommand<MqttPublishCommand>("pub").WithExample("mqtt", "pub", "plant/line1/temp", "23.5");
        q.AddCommand<MqttSubscribeCommand>("sub").WithExample("mqtt", "sub", "plant/#");
        q.AddCommand<MqttBrokerCommand>("broker").WithExample("mqtt", "broker", "--port", "1883");
    });
});

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};
try
{
    return await app.RunAsync(args, cts.Token);
}
catch (Exception ex)
{
    Ui.Error(Markup.Escape(ex.Message));
    return 1;
}
