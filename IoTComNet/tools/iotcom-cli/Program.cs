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

IoTCom.Net.Transport.Can.Adapters.CanAdapters.Register();   // gsusb: and pcan: URIs
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
        n.AddBranch("ais", a =>
        {
            a.SetDescription("AIS (ship traffic) over NMEA: decode sentences, watch a feed.");
            a.AddCommand<AisDecodeCommand>("decode").WithExample("nmea", "ais", "decode", "!AIVDM,1,1,,B,15NG6V0P01G?cFhE`R2IU?wn28R>,0*05");
            a.AddCommand<AisWatchCommand>("watch").WithExample("nmea", "ais", "watch", "--sim").WithExample("nmea", "ais", "watch", "--udp", "10110");
        });
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
    c.AddBranch("canopen", o =>
    {
        o.SetDescription("CANopen (CiA 301): scan nodes, SDO read/write, NMT, monitor heartbeats, PDOs and emergencies.");
        o.AddCommand<CanOpenScanCommand>("scan").WithExample("canopen", "scan", "--can", "sim").WithExample("canopen", "scan", "--can", "gsusb:", "-b", "250000");
        o.AddCommand<CanOpenReadCommand>("read").WithExample("canopen", "read", "5", "1008", "-t", "str", "--can", "sim").WithExample("canopen", "read", "5", "6401:01", "-t", "i16", "--can", "sim");
        o.AddCommand<CanOpenWriteCommand>("write").WithDescription("SDO download (requires --allow-write).").WithExample("canopen", "write", "5", "6200:01", "1", "-t", "u8", "--can", "sim", "--allow-write");
        o.AddCommand<CanOpenNmtCommand>("nmt").WithDescription("NMT command (requires --allow-write).").WithExample("canopen", "nmt", "start", "5", "--can", "sim", "--allow-write");
        o.AddCommand<CanOpenMonitorCommand>("monitor").WithExample("canopen", "monitor", "--can", "sim");
    });
    c.AddBranch("j1939", j =>
    {
        j.SetDescription("SAE J1939 (trucks, buses, agriculture, marine): monitor decoded PGNs, request parameters, list address claims.");
        j.AddCommand<J1939MonitorCommand>("monitor").WithExample("j1939", "monitor", "--can", "sim").WithExample("j1939", "monitor", "--can", "socketcan:can0", "--pgn", "eec1", "--pgn", "dm1");
        j.AddCommand<J1939RequestCommand>("request").WithExample("j1939", "request", "vin", "--can", "sim").WithExample("j1939", "request", "hours", "--to", "00", "--can", "sim");
        j.AddCommand<J1939ClaimsCommand>("claims").WithExample("j1939", "claims", "--can", "sim");
    });
    c.AddBranch("iec104", i =>
    {
        i.SetDescription("IEC 60870-5-104 telecontrol (substations, RTUs, SCADA): interrogate, read, monitor, command, or serve a simulated feeder bay.");
        i.AddCommand<Iec104InterrogateCommand>("gi").WithDescription("General, group or counter interrogation.").WithExample("iec104", "gi", "--sim").WithExample("iec104", "gi", "-h", "10.0.0.5", "--counters");
        i.AddCommand<Iec104ReadCommand>("read").WithExample("iec104", "read", "2001", "--sim");
        i.AddCommand<Iec104MonitorCommand>("monitor").WithDescription("Interrogate, then print spontaneous changes (read-only).").WithExample("iec104", "monitor", "--sim");
        i.AddCommand<Iec104CommandCommand>("command").WithDescription("Single/double/step command or set point (requires --allow-write).").WithExample("iec104", "command", "double", "5001", "off", "--sbo", "--sim", "--allow-write");
        i.AddCommand<Iec104ServeCommand>("serve").WithDescription("Run the 20 kV feeder bay RTU simulator on TCP.").WithExample("iec104", "serve", "--port", "2404");
    });
    c.AddBranch("ntp", n =>
    {
        n.SetDescription("NTP/SNTP: measure this computer's clock against time servers, or serve time on the local network.");
        n.AddCommand<NtpQueryCommand>("query").WithDescription("Query servers and show offset and delay (never changes the system clock).").WithExample("ntp", "query").WithExample("ntp", "query", "time.cloudflare.com", "pool.ntp.org").WithExample("ntp", "query", "--sim", "--frames");
        n.AddCommand<NtpServeCommand>("serve").WithDescription("Serve this computer's time over UDP.").WithExample("ntp", "serve", "--port", "1123", "--ref", "LOCL", "--stratum", "10");
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
    c.AddBranch("at", a =>
    {
        a.SetDescription("AT commands for cellular/GNSS modules (3GPP 27.007): send, info, SMS; simulate a module.");
        a.AddCommand<AtSendCommand>("send").WithExample("at", "send", "--sim", "AT+CSQ", "AT+COPS?").WithExample("at", "send", "--serial", "COM7", "ATI");
        a.AddCommand<AtInfoCommand>("info").WithExample("at", "info", "--sim");
        a.AddCommand<AtSmsCommand>("sms").WithDescription("Send an SMS (requires --allow-write).").WithExample("at", "sms", "--sim", "+6281234567890", "hello", "--allow-write");
        a.AddCommand<AtSimulateCommand>("simulate").WithExample("at", "simulate", "--port", "2000");
    });
    c.AddBranch("astm", a =>
    {
        a.SetDescription("ASTM E1394 / LIS2-A2 lab analyzers: receive (LIS), send synthetic results, bridge to HL7.");
        a.AddCommand<AstmListenCommand>("listen").WithExample("astm", "listen", "--port", "5000", "--hl7");
        a.AddCommand<AstmSendCommand>("send").WithExample("astm", "send", "--port", "5000", "-n", "3");
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
    c.AddBranch("usb", u =>
    {
        u.SetDescription("USB and HID (Rust nusb/hidapi): list devices, control and bulk transfers, USB HID relay boards.");
        u.AddCommand<UsbListCommand>("list").WithExample("usb", "list").WithExample("usb", "list", "--sim");
        u.AddCommand<HidListCommand>("hid").WithDescription("List HID collections.").WithExample("usb", "hid");
        u.AddCommand<UsbControlCommand>("control").WithDescription("Control IN request (read-only).").WithExample("usb", "control", "--sim", "1209:0001", "0x01");
        u.AddCommand<UsbWriteReadCommand>("write").WithDescription("Write to a bulk OUT endpoint and read the answer (requires --allow-write).")
            .WithExample("usb", "write", "--sim", "1209:0001", "68656c6c6f", "--allow-write");
        u.AddCommand<UsbRelayCommand>("relay").WithDescription("Show or switch a USB HID relay board (switching requires --allow-write).")
            .WithExample("usb", "relay", "--sim").WithExample("usb", "relay", "--sim", "2", "on", "--allow-write");
    });
    c.AddBranch("ble", b =>
    {
        b.SetDescription("Bluetooth Low Energy central (Rust btleplug): scan, list services, watch notifications, write.");
        b.AddCommand<BleScanCommand>("scan").WithExample("ble", "scan").WithExample("ble", "scan", "--sim", "-t", "2");
        b.AddCommand<BleServicesCommand>("services").WithExample("ble", "services", "--sim", "E8:4F:25:10:7A:33");
        b.AddCommand<BleWatchCommand>("watch").WithExample("ble", "watch", "--sim", "C4:7C:8D:6A:21:0F", "2a37");
        b.AddCommand<BleWriteCommand>("write").WithDescription("Write a characteristic (requires --allow-write).")
            .WithExample("ble", "write", "--sim", "D0:8E:3A:55:10:C2", "6e400002-b5a3-f393-e0a9-e50e24dcca9e", "01", "--allow-write");
    });
    c.AddBranch("opcua", u =>
    {
        u.SetDescription("OPC UA (adapter over the OPC Foundation stack): browse, read, watch, write, call; simulate a plant server.");
        u.AddCommand<OpcUaBrowseCommand>("browse").WithExample("opcua", "browse", "--sim").WithExample("opcua", "browse", "-e", "opc.tcp://plc.local:4840", "--accept-untrusted");
        u.AddCommand<OpcUaReadCommand>("read").WithExample("opcua", "read", "--sim", "Line1/Filler/Speed", "Line1/Tank7/Level");
        u.AddCommand<OpcUaWatchCommand>("watch").WithExample("opcua", "watch", "--sim", "Line1/Tank7/Level", "-i", "250");
        u.AddCommand<OpcUaWriteCommand>("write").WithDescription("Write a variable (requires --allow-write).").WithExample("opcua", "write", "--sim", "Line1/Filler/Setpoint", "100", "--allow-write");
        u.AddCommand<OpcUaCallCommand>("call").WithDescription("Call a method (requires --allow-write).").WithExample("opcua", "call", "--sim", "Line1", "Line1/ResetCounter", "--allow-write");
        u.AddCommand<OpcUaSimulateCommand>("simulate").WithExample("opcua", "simulate", "--port", "4840");
    });
    c.AddBranch("sparkplug", p =>
    {
        p.SetDescription("Sparkplug B over MQTT: watch as a host application, simulate an edge node, write metrics.");
        p.AddCommand<SparkplugWatchCommand>("watch").WithExample("sparkplug", "watch", "--sim").WithExample("sparkplug", "watch", "--host", "broker.local");
        p.AddCommand<SparkplugSimulateCommand>("simulate").WithExample("sparkplug", "simulate", "--embedded-broker");
        p.AddCommand<SparkplugWriteCommand>("write").WithDescription("Write a metric with NCMD/DCMD (requires --allow-write).")
            .WithExample("sparkplug", "write", "Plant/Line1/Filler", "Running", "false", "--allow-write");
    });
    c.AddBranch("mdns", m =>
    {
        m.SetDescription("mDNS / DNS-SD: discover devices on the local network and advertise services.");
        m.AddCommand<MdnsBrowseCommand>("browse").WithExample("mdns", "browse").WithExample("mdns", "browse", "_modbus._tcp", "--watch").WithExample("mdns", "browse", "--sim");
        m.AddCommand<MdnsAdvertiseCommand>("advertise").WithExample("mdns", "advertise", "Line 1 gateway", "_modbus._tcp", "502", "--txt", "units=1-8");
    });
    c.AddCommand<PayloadDecodeCommand>("payload").WithDescription("Decode a payload: protobuf, msgpack, ber-tlv, tlv, sparkplug or dns.")
        .WithExample("payload", "protobuf", "08 96 01 12 02 68 69").WithExample("payload", "ber-tlv", "6F148407A0000000031010A5095004564953419F3800");
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
