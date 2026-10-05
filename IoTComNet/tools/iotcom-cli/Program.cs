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
    c.AddCommand<CrcCommand>("crc").WithDescription("Compute CRCs (20+ presets) of hex or text input.")
        .WithExample("crc", "01 03 00 00 00 0A", "--algorithm", "modbus")
        .WithExample("crc", "--text", "123456789", "--all");
    c.AddBranch("frame", f =>
    {
        f.SetDescription("Encode/decode SLIP, COBS and HDLC frames.");
        f.AddCommand<FrameEncodeCommand>("encode").WithExample("frame", "encode", "cobs", "11 22 00 33");
        f.AddCommand<FrameDecodeCommand>("decode").WithExample("frame", "decode", "slip", "C0 DB DC 01 C0");
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
    c.AddBranch("artnet", a =>
    {
        a.SetDescription("Art-Net 4 lighting control.");
        a.AddCommand<ArtNetSendCommand>("send").WithExample("artnet", "send", "--universe", "0", "--values", "255,128,0");
        a.AddCommand<ArtNetPollCommand>("poll");
        a.AddCommand<ArtNetMonitorCommand>("monitor");
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
