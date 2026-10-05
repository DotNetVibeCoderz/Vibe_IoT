using System.ComponentModel;
using System.Globalization;
using System.Text;
using IoTCom.Net.Framing;
using IoTCom.Net.Transport.Serial;
using Spectre.Console;
using Spectre.Console.Cli;

namespace IoTCom.Net.Cli.Commands;

internal sealed class InfoCommand : Command<InfoCommand.Settings>
{
    public sealed class Settings : CommandSettings;

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        Ui.Banner();
        AnsiConsole.WriteLine();
        var t = new Table().Border(TableBorder.Rounded).BorderColor(Ui.Muted)
            .AddColumn("[bold]Protocol[/]").AddColumn("[bold]Roles[/]").AddColumn("[bold]Package[/]").AddColumn("[bold]Engine[/]");
        t.AddRow("Modbus TCP / RTU / ASCII", "master · slave · simulator", "IoTCom.Net.Protocols.Modbus", "C# + Rust (Native.Modbus)");
        t.AddRow("NMEA 0183", "reader · server · simulator", "IoTCom.Net.Protocols.Nmea", "C#");
        t.AddRow("Art-Net 4 · sACN E1.31", "send · receive · discovery", "IoTCom.Net.Protocols.Dmx", "C#");
        t.AddRow("HL7 v2 / MLLP", "sender · receiver · monitor simulator", "IoTCom.Net.Protocols.Hl7", "C#");
        t.AddRow("DICOM C-STORE / C-ECHO", "SCU · SCP · synthetic imaging", "IoTCom.Net.Adapters.Dicom", "fo-dicom adapter");
        t.AddRow("MQTT 3.1.1 / 5.0", "publish · subscribe · broker", "IoTCom.Net.Adapters.Mqtt", "MQTTnet adapter");
        t.AddRow("SenML (RFC 8428)", "JSON · CBOR codec", "IoTCom.Net.Serialization.SenML", "C#");
        t.AddRow("CRC · SLIP · COBS · HDLC", "codec", "IoTCom.Net.Framing", "C#");
        t.AddRow("Serial RS-232/485", "transport", "IoTCom.Net.Transport.Serial", "System.IO.Ports");
        AnsiConsole.Write(t);
        AnsiConsole.MarkupLine($"[{Ui.Hex(Ui.Muted)}]{IoTComInfo.CreditId}[/]");
        AnsiConsole.MarkupLine($"[{Ui.Hex(Ui.Muted)}]Run [bold]iotcom --help[/] to see every command.[/]");
        return 0;
    }
}

internal sealed class PortsCommand : Command<PortsCommand.Settings>
{
    public sealed class Settings : CommandSettings;

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var ports = SerialTransport.GetPortNames();
        if (ports.Length == 0)
        {
            Ui.Warn("No serial ports found. Plug in a USB-serial adapter, or on Linux check that your user is in the 'dialout' group.");
            return 0;
        }
        foreach (var p in ports) Ui.Success(p);
        return 0;
    }
}

internal sealed class CrcCommand : Command<CrcCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[input]"), Description("Hex bytes, e.g. \"01 03 00 00 00 0A\".")]
        public string? Input { get; init; }

        [CommandOption("-t|--text"), Description("Treat input as ASCII text.")]
        public string? Text { get; init; }

        [CommandOption("-a|--algorithm"), Description("Preset name or alias (modbus, x25, mavlink, crc32c, ...).")]
        public string Algorithm { get; init; } = "modbus";

        [CommandOption("--all"), Description("Compute every preset.")]
        public bool All { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var data = settings.Text is not null ? Encoding.ASCII.GetBytes(settings.Text)
            : settings.Input is not null ? HexDump.Parse(settings.Input)
            : throw new ArgumentException("Give hex input or --text.");
        var algorithms = settings.All ? CrcCatalog.All
            : [CrcCatalog.Find(settings.Algorithm) ?? throw new ArgumentException($"Unknown CRC '{settings.Algorithm}'. Try --all to list presets.")];

        var t = new Table().Border(TableBorder.Rounded).BorderColor(Ui.Muted).AddColumn("[bold]Algorithm[/]").AddColumn("[bold]CRC[/]").AddColumn("[bold]Wire order (LE)[/]");
        foreach (var a in algorithms)
        {
            var crc = a.Compute(data);
            var digits = a.Width / 4;
            var le = HexDump.ToHex(BitConverter.GetBytes(crc).AsSpan(0, (a.Width + 7) / 8));
            t.AddRow(a.Parameters.Name, $"[{Ui.Hex(Ui.LampGreen)}]0x{crc.ToString("X" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)}[/]", $"[{Ui.Hex(Ui.Muted)}]{le}[/]");
        }
        AnsiConsole.MarkupLine($"[{Ui.Hex(Ui.Muted)}]{data.Length} bytes[/]");
        AnsiConsole.Write(t);
        return 0;
    }
}

internal class FrameSettings : CommandSettings
{
    [CommandArgument(0, "<codec>"), Description("slip, cobs or hdlc.")]
    public string Codec { get; init; } = "";

    [CommandArgument(1, "<hex>"), Description("Bytes in hex.")]
    public string Hex { get; init; } = "";

    public IFrameEncoder Encoder() => Create();
    public IFrameDecoder Decoder() => (IFrameDecoder)Create();

    private IFrameEncoder Create() => Codec.ToLowerInvariant() switch
    {
        "slip" => new Slip(),
        "cobs" => new Cobs(),
        "hdlc" => new Hdlc(),
        _ => throw new ArgumentException("Codec must be slip, cobs or hdlc."),
    };
}

internal sealed class FrameEncodeCommand : Command<FrameSettings>
{
    public override int Execute(CommandContext context, FrameSettings settings, CancellationToken cancellationToken)
    {
        var w = new System.Buffers.ArrayBufferWriter<byte>();
        settings.Encoder().Encode(HexDump.Parse(settings.Hex), w);
        AnsiConsole.MarkupLine($"[{Ui.Hex(Ui.Muted)}]{settings.Codec.ToUpperInvariant()} encoded, {w.WrittenCount} bytes[/]");
        AnsiConsole.MarkupLine($"[bold]{HexDump.ToHex(w.WrittenSpan)}[/]");
        return 0;
    }
}

internal sealed class FrameDecodeCommand : Command<FrameSettings>
{
    public override int Execute(CommandContext context, FrameSettings settings, CancellationToken cancellationToken)
    {
        var seq = new System.Buffers.ReadOnlySequence<byte>(HexDump.Parse(settings.Hex));
        var payload = new System.Buffers.ArrayBufferWriter<byte>();
        var frames = 0;
        var decoder = settings.Decoder();
        while (true)
        {
            payload.ResetWrittenCount();
            var status = decoder.TryDecode(ref seq, payload);
            if (status == FrameDecodeStatus.NeedMoreData) break;
            if (status == FrameDecodeStatus.Invalid) { Ui.Error("Invalid frame (bad escape or checksum) — skipped."); continue; }
            frames++;
            Ui.Success($"Frame {frames}: [bold]{HexDump.ToHex(payload.WrittenSpan)}[/] [{Ui.Hex(Ui.Muted)}]({payload.WrittenCount} bytes)[/]");
        }
        if (frames == 0) Ui.Warn("No complete frame found. Check that the input includes the closing delimiter.");
        return frames > 0 ? 0 : 2;
    }
}
