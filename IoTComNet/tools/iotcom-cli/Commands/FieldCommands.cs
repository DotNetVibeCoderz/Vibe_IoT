using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using IoTCom.Net.Protocols.Astm;
using IoTCom.Net.Protocols.AtCommand;
using IoTCom.Net.Protocols.Nmea;
using IoTCom.Net.Transport.Serial;
using IoTCom.Net.Transports;
using Spectre.Console;
using Spectre.Console.Cli;

namespace IoTCom.Net.Cli.Commands;

// ---- AIS ---------------------------------------------------------------------------------------------------------

internal sealed class AisDecodeCommand : Command<AisDecodeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<sentences>"), Description("One or more !AIVDM sentences (multi-part messages in order).")]
        public string[] Sentences { get; init; } = [];
    }

    public override int Execute(CommandContext context, Settings s, CancellationToken ct)
    {
        var decoder = new AisDecoder();
        foreach (var line in s.Sentences)
        {
            if (decoder.Feed(line) is not { } m)
            {
                if (decoder.Dropped > 0) Ui.Error(Markup.Escape($"Not decodable: {line}"));
                continue;
            }

            AnsiConsole.MarkupLine($"[bold]Type {m.Type}[/] · MMSI {m.Mmsi} ({Markup.Escape(Ais.StationKind(m.Mmsi))})");
            var table = new Table().Border(TableBorder.Rounded).AddColumns("Field", "Value");
            foreach (var p in m.GetType().GetProperties().Where(p => p.Name is not ("Type" or "Mmsi" or "EqualityContract")))
                if (p.GetValue(m) is { } v) table.AddRow(p.Name, Markup.Escape(Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""));
            AnsiConsole.Write(table);
        }

        return 0;
    }
}

internal sealed class AisWatchCommand : AsyncCommand<AisWatchCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--sim"), Description("Simulated traffic in Jakarta Bay.")]
        public bool Simulate { get; init; }

        [CommandOption("--udp"), Description("Listen for !AIVDM sentences on this UDP port (AIS receivers and dispatchers).")]
        public int? Udp { get; init; }

        [CommandOption("-h|--host"), Description("TCP host of an AIS feed.")]
        public string? Host { get; init; }

        [CommandOption("-p|--port"), Description("TCP port (default 10110).")]
        public int Port { get; init; } = 10110;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        var decoder = new AisDecoder();
        var tracker = new AisTracker();
        void Line(string line)
        {
            if (decoder.Feed(line) is { } m) tracker.Apply(m);
        }

        Task source;
        if (s.Simulate)
        {
            var sim = new AisSimulator();
            source = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    foreach (var l in sim.Step(TimeSpan.FromSeconds(30))) Line(l);
                    await Task.Delay(1000, ct);
                }
            }, ct);
        }
        else if (s.Udp is { } port)
        {
            var udp = new UdpClient(port);
            source = Task.Run(async () =>
            {
                using var _ = udp;
                while (!ct.IsCancellationRequested)
                    foreach (var l in Encoding.ASCII.GetString((await udp.ReceiveAsync(ct)).Buffer).Split('\n')) Line(l.Trim());
            }, ct);
        }
        else
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(s.Host ?? throw new InvalidOperationException("Give --sim, --udp or --host."), s.Port, ct);
            source = Task.Run(async () =>
            {
                using var reader = new StreamReader(tcp.GetStream(), Encoding.ASCII);
                while (!ct.IsCancellationRequested && await reader.ReadLineAsync(ct) is { } l) Line(l);
            }, ct);
        }

        Table Render()
        {
            var t = new Table().Border(TableBorder.Rounded).Title($"AIS · {tracker.Vessels.Count} vessels · dropped {decoder.Dropped}")
                .AddColumns("MMSI", "Name", "Type", "Position", "SOG kn", "COG °", "Status", "Destination");
            foreach (var v in tracker.Vessels.OrderBy(v => v.Name, StringComparer.Ordinal))
                t.AddRow(v.Mmsi.ToString(CultureInfo.InvariantCulture), Markup.Escape(v.Name ?? "?"), Markup.Escape(Ais.ShipTypeName(v.ShipType)),
                    v.Latitude is { } lat && v.Longitude is { } lon ? $"{lat:0.0000}, {lon:0.0000}" : "",
                    v.Speed?.ToString("0.0", CultureInfo.InvariantCulture) ?? "", v.Course?.ToString("0", CultureInfo.InvariantCulture) ?? "",
                    v.ClassB ? "class B" : v.Status.ToString(), Markup.Escape(v.Destination ?? ""));
            return t;
        }

        try
        {
            await AnsiConsole.Live(Render()).StartAsync(async live =>
            {
                while (!ct.IsCancellationRequested && !source.IsCompleted)
                {
                    live.UpdateTarget(Render());
                    await Task.Delay(1000, ct);
                }
            });
        }
        catch (OperationCanceledException) { }
        return 0;
    }
}

// ---- AT commands -------------------------------------------------------------------------------------------------

internal class AtSettings : CommandSettings
{
    [CommandOption("--serial"), Description("Serial port of the module (COM7, /dev/ttyUSB2).")]
    public string? Serial { get; init; }

    [CommandOption("--baud"), Description("Baud rate (default 115200).")]
    public int Baud { get; init; } = 115200;

    [CommandOption("-h|--host"), Description("TCP serial server instead.")]
    public string? Host { get; init; }

    [CommandOption("-p|--port"), Description("TCP port.")]
    public int Port { get; init; } = 2000;

    [CommandOption("--sim"), Description("A simulated LTE-M module in this process.")]
    public bool Simulate { get; init; }

    [CommandOption("--frames"), Description("Print every command and response.")]
    public bool Frames { get; init; }

    public async Task<(AtModem Modem, AtModemSimulator? Sim)> ConnectAsync(bool readOnly, CancellationToken ct)
    {
        AtModemSimulator? sim = null;
        InMemoryTransportListener? link = null;
        if (Simulate)
        {
            link = new InMemoryTransportListener("modem");
            sim = AtModemSimulator.Create(o => { o.ListenInMemory(link); o.RegistrationDelay = TimeSpan.FromMilliseconds(500); });
            await sim.StartAsync(ct);
        }

        var modem = AtModem.Create(o =>
        {
            if (link is not null) o.UseInMemory(link);
            else if (Serial is not null) o.UseSerial(Serial, Baud);
            else o.UseTcp(Host ?? throw new InvalidOperationException("Give --serial, --host or --sim."), Port);
            o.ReadOnly = readOnly;
        });
        if (Frames) modem.AddTap(new LinePrinter());
        await modem.ConnectAsync(ct);
        if (sim is not null) await Task.Delay(700, ct);
        return (modem, sim);
    }

    private sealed class LinePrinter : ITrafficTap
    {
        public void OnFrame(in TrafficFrame frame) =>
            AnsiConsole.MarkupLine($"[{Ui.Hex(frame.Direction == FrameDirection.Outbound ? Ui.Amber : Ui.CableBlue)}]{(frame.Direction == FrameDirection.Outbound ? "TX" : "RX")}[/] {Markup.Escape(frame.Summary ?? "")}");
    }
}

internal sealed class AtSendCommand : AsyncCommand<AtSendCommand.Settings>
{
    public sealed class Settings : AtSettings
    {
        [CommandArgument(0, "<commands>"), Description("AT commands, e.g. AT+CSQ \"AT+COPS?\".")]
        public string[] Commands { get; init; } = [];

        [CommandOption("--allow-write"), Description("Allow commands that change the module (AT+CFUN, dialling, SMS, …).")]
        public bool AllowWrite { get; init; }

        [CommandOption("--timeout"), Description("Seconds per command (default 5).")]
        public double Timeout { get; init; } = 5;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        var (modem, sim) = await s.ConnectAsync(!s.AllowWrite, ct);
        await using var _m = modem;
        await using var _s = sim;
        var failed = 0;
        foreach (var command in s.Commands)
        {
            var r = await modem.SendAsync(command, TimeSpan.FromSeconds(s.Timeout), ct);
            foreach (var line in r.Lines) AnsiConsole.WriteLine(line);
            var detail = r.ErrorCode is { } code ? $" {code} ({AtParser.CmeErrorName(code)})" : r.ErrorText is { } t ? $" ({t})" : "";
            AnsiConsole.MarkupLine($"[{Ui.Hex(r.IsSuccess ? Ui.LampGreen : Ui.Fault)}]{r.Result}{Markup.Escape(detail)}[/]");
            if (!r.IsSuccess) failed++;
        }

        return failed == 0 ? 0 : 1;
    }
}

internal sealed class AtInfoCommand : AsyncCommand<AtSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, AtSettings s, CancellationToken ct)
    {
        var (modem, sim) = await s.ConnectAsync(true, ct);
        await using var _m = modem;
        await using var _s = sim;
        var i = await modem.GetInfoAsync(ct);
        var table = new Table().Border(TableBorder.Rounded).AddColumns("Item", "Value");
        table.AddRow("Module", Markup.Escape($"{i.Manufacturer} {i.Model} ({i.Revision})"));
        table.AddRow("IMEI", Markup.Escape(i.Imei));
        table.AddRow("SIM", Markup.Escape($"{i.SimStatus}{(i.Iccid is { } id ? $" · ICCID {id}" : "")}"));
        table.AddRow("Network", Markup.Escape($"{i.Registration} · {i.Operator ?? "-"} · {i.AccessTechnology ?? "-"}"));
        table.AddRow("Signal", Markup.Escape(i.Signal.ToString()));
        AnsiConsole.Write(table);
        return 0;
    }
}

internal sealed class AtSmsCommand : AsyncCommand<AtSmsCommand.Settings>
{
    public sealed class Settings : AtSettings
    {
        [CommandArgument(0, "<number>"), Description("Destination (international format).")]
        public string Number { get; init; } = "";

        [CommandArgument(1, "<text>"), Description("Message text.")]
        public string Text { get; init; } = "";

        [CommandOption("--allow-write"), Description("Required: sending an SMS costs money and reaches a person.")]
        public bool AllowWrite { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        if (!s.AllowWrite)
        {
            Ui.Warn("Sending an SMS reaches a real person and may cost money. Re-run with [bold]--allow-write[/].");
            return 2;
        }

        var (modem, sim) = await s.ConnectAsync(false, ct);
        await using var _m = modem;
        await using var _s = sim;
        var reference = await modem.SendSmsAsync(s.Number, s.Text, ct);
        Ui.Success($"SMS sent (message reference {reference}).");
        return 0;
    }
}

internal sealed class AtSimulateCommand : AsyncCommand<AtSimulateCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-p|--port"), Description("TCP port of the simulated module (default 2000).")]
        public int Port { get; init; } = 2000;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        await using var sim = AtModemSimulator.Create(o => o.UseTcp(IPAddress.Any, s.Port));
        await sim.StartAsync(ct);
        Ui.Success($"Simulated LTE-M module on tcp://0.0.0.0:{s.Port}. Ctrl+C to stop.");
        Ui.Warn($"Try: iotcom at info -h 127.0.0.1 -p {s.Port}   ·   iotcom at send -h 127.0.0.1 -p {s.Port} AT+CSQ \"AT+COPS?\"");
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException) { }
        return 0;
    }
}

// ---- ASTM --------------------------------------------------------------------------------------------------------

internal sealed class AstmListenCommand : AsyncCommand<AstmListenCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-p|--port"), Description("TCP port (default 5000).")]
        public int Port { get; init; } = 5000;

        [CommandOption("--serial"), Description("Serial port of the analyzer instead (9600 8N1).")]
        public string? Serial { get; init; }

        [CommandOption("--hl7"), Description("Also print each message as HL7 ORU^R01.")]
        public bool Hl7 { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        await using var lis = AstmReceiver.Create(o =>
        {
            if (s.Serial is not null) o.ServeSerial(s.Serial, 9600);
            else o.UseTcp(IPAddress.Any, s.Port);
        });
        lis.MessageReceived += (_, m) =>
        {
            AnsiConsole.MarkupLine($"[bold]{Markup.Escape(m.Sender)}[/] · patient {Markup.Escape(m.PatientId ?? "?")} · specimen {Markup.Escape(m.SpecimenId ?? "?")}");
            var table = new Table().Border(TableBorder.Rounded).AddColumns("Test", "Value", "Units", "Reference", "Flag");
            foreach (var r in m.Results)
                table.AddRow(r.TestCode, $"[bold]{Markup.Escape(r.Value)}[/]", Markup.Escape(r.Units), Markup.Escape(r.ReferenceRange),
                    r.Flag is "N" or "" ? "" : $"[{Ui.Hex(Ui.Fault)}]{Markup.Escape(r.Flag)}[/]");
            AnsiConsole.Write(table);
            if (s.Hl7) AnsiConsole.WriteLine(AstmToHl7.ToOru(m).Encode().Replace("\r", Environment.NewLine, StringComparison.Ordinal));
        };
        await lis.StartAsync(ct);
        Ui.Success($"ASTM receiver (LIS) on {(s.Serial ?? $"tcp://0.0.0.0:{s.Port}")}. Synthetic data only; not a medical device. Ctrl+C to stop.");
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException) { }
        Ui.Warn($"{lis.MessagesReceived} message(s), {lis.NakCount} frame(s) rejected.");
        return 0;
    }
}

internal sealed class AstmSendCommand : AsyncCommand<AstmSendCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-h|--host"), Description("LIS host (default 127.0.0.1).")]
        public string Host { get; init; } = "127.0.0.1";

        [CommandOption("-p|--port"), Description("LIS port (default 5000).")]
        public int Port { get; init; } = 5000;

        [CommandOption("-n|--count"), Description("Messages to send (default 1).")]
        public int Count { get; init; } = 1;

        [CommandOption("--file"), Description("Send this ASTM file instead of synthetic results.")]
        public string? File { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        await using var analyzer = AstmSender.Create(o => o.UseTcp(s.Host, s.Port));
        await analyzer.ConnectAsync(ct);
        var sim = new AnalyzerSimulator();
        for (var i = 0; i < s.Count; i++)
        {
            var message = s.File is not null ? AstmMessage.Parse(await System.IO.File.ReadAllTextAsync(s.File, ct)) : sim.NextResult();
            await analyzer.SendAsync(message, ct);
            Ui.Success(Markup.Escape($"Sent {message} (retransmissions {analyzer.Retransmissions})."));
        }

        return 0;
    }
}
