using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Text;
using IoTCom.Net.Protocols.Ocpp;
using IoTCom.Net.Transports;
using Spectre.Console;
using Spectre.Console.Cli;

namespace IoTCom.Net.Cli.Commands;

internal static class OcppUi
{
    public static OcppVersion Version(string? v) => v?.Replace("j", "", StringComparison.OrdinalIgnoreCase) switch
    {
        null or "1.6" or "16" => OcppVersion.Ocpp16,
        "2.0.1" or "201" or "2" => OcppVersion.Ocpp201,
        _ => throw new ArgumentException($"Unknown OCPP version '{v}' (1.6 or 2.0.1)."),
    };

    public static void Log(OcppMessageEvent e)
    {
        var arrow = e.Direction == FrameDirection.Inbound ? $"[{Ui.Hex(Ui.CableBlue)}]←[/]" : $"[{Ui.Hex(Ui.Amber)}]→[/]";
        var color = e.Frame.Type == OcppMessageType.CallError ? Ui.Fault : Ui.Muted;
        AnsiConsole.MarkupLine($"[{Ui.Hex(Ui.Muted)}]{DateTime.Now:HH:mm:ss}[/] {arrow} {Markup.Escape(e.Identity)} [{Ui.Hex(color)}]{Markup.Escape(e.Frame.Describe())}[/] {Markup.Escape(Shorten(e.Frame.Payload is null ? "" : OcppFrame.Format(e.Frame.Payload)))}");
    }

    private static string Shorten(string s) => s.Length <= 110 ? s : s[..107] + "...";

    public static void PrintStations(OcppCsms csms)
    {
        var table = new Table().Border(TableBorder.Rounded).AddColumns("Station", "Version", "Vendor / model", "Connector", "Status", "Power", "Energy", "SoC", "Transaction");
        foreach (var st in csms.Stations.Values.OrderBy(s => s.Identity, StringComparer.Ordinal))
        {
            var first = true;
            foreach (var c in st.Connectors.Values.Where(c => c.Id > 0).OrderBy(c => c.Id))
            {
                table.AddRow(first ? Markup.Escape(st.Identity) + (st.IsOnline ? "" : " [grey](offline)[/]") : "", first ? st.Version.DisplayName() : "", first ? Markup.Escape($"{st.Vendor} {st.Model}") : "",
                    c.Id.ToString(CultureInfo.InvariantCulture), Markup.Escape(c.ChargingState ?? c.Status), c.PowerW is { } p ? $"{p / 1000:0.0} kW" : "",
                    c.Transaction is { } tx ? $"{tx.EnergyWh / 1000:0.00} kWh" : "", c.SoC is { } soc ? $"{soc:0} %" : "", Markup.Escape(c.Transaction?.Id ?? ""));
                first = false;
            }
        }

        AnsiConsole.Write(table);
    }
}

internal sealed class OcppCsmsCommand : AsyncCommand<OcppCsmsCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-p|--port"), Description("TCP port for ws:// (default 8887).")]
        public int Port { get; init; } = 8887;

        [CommandOption("--tags"), Description("Comma-separated id tags that may charge (default: any).")]
        public string? Tags { get; init; }

        [CommandOption("--heartbeat"), Description("Heartbeat interval given to stations, seconds (default 300).")]
        public int Heartbeat { get; init; } = 300;

        [CommandOption("--frames"), Description("Print every OCPP message.")]
        public bool Frames { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        await using var csms = OcppCsms.Create(o =>
        {
            o.UseTcp(IPAddress.Any, s.Port);
            o.HeartbeatInterval = TimeSpan.FromSeconds(s.Heartbeat);
            if (s.Tags is not null) o.AuthorizedTags = new HashSet<string>(s.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.Ordinal);
        });
        if (s.Frames) csms.Message += OcppUi.Log;
        csms.StationConnected += st => Ui.Success($"{Markup.Escape(st.Identity)} connected ({st.Version.DisplayName()}) from {Markup.Escape(st.RemoteAddress)}");
        csms.StationDisconnected += st => Ui.Warn($"{Markup.Escape(st.Identity)} disconnected");
        csms.TransactionStarted += (st, tx) => Ui.Success($"{Markup.Escape(st.Identity)}: transaction {Markup.Escape(tx.Id)} started on {tx.ConnectorId} by {Markup.Escape(tx.IdTag)}");
        csms.TransactionStopped += (st, tx) => Ui.Success($"{Markup.Escape(st.Identity)}: {Markup.Escape(tx.ToString())}");
        await csms.StartAsync(ct);
        Ui.Success($"CSMS listening on ws://0.0.0.0:{s.Port}/ocpp/{{identity}} (OCPP 2.0.1 and 1.6J; read-only: it answers stations but never sends commands). Try [bold]iotcom ocpp charger --csms ws://127.0.0.1:{s.Port}/ocpp/[/]. Ctrl+C to stop.");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                if (csms.Stations.Count > 0) OcppUi.PrintStations(csms);
            }
        }
        catch (OperationCanceledException)
        {
        }

        return 0;
    }
}

internal sealed class OcppChargerCommand : AsyncCommand<OcppChargerCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--csms"), Description("CSMS URL without the identity (default ws://127.0.0.1:8887/ocpp/).")]
        public string Csms { get; init; } = "ws://127.0.0.1:8887/ocpp/";

        [CommandOption("-i|--identity"), Description("Charge point identity (default SPKLU-KM57-01).")]
        public string Identity { get; init; } = "SPKLU-KM57-01";

        [CommandOption("-v|--version"), Description("OCPP version: 1.6 (default) or 2.0.1.")]
        public string? Version { get; init; }

        [CommandOption("--password"), Description("Basic authentication password (security profile 1/2).")]
        public string? Password { get; init; }

        [CommandOption("--tag"), Description("Plug an EV into connector 1 and present this id tag after boot.")]
        public string? Tag { get; init; }

        [CommandOption("--speed"), Description("Simulated seconds per real second (default 60).")]
        public double Speed { get; init; } = 60;

        [CommandOption("--frames"), Description("Print every OCPP message.")]
        public bool Frames { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        await using var sim = OcppChargePointSimulator.Create(o =>
        {
            o.Identity = s.Identity;
            o.Version = OcppUi.Version(s.Version);
            o.AutoRun = true;
            o.TimeScale = s.Speed;
            o.UseCsms(s.Csms);
            if (s.Password is not null) o.ChargePoint += c => c.Password = s.Password;
        });
        if (s.Frames) sim.ChargePoint.Message += OcppUi.Log;
        sim.Reset += kind => Ui.Warn($"Reset ({kind}) executed by the CSMS.");
        await sim.StartAsync(ct);
        Ui.Success($"{Markup.Escape(sim.Identity)} ({sim.Version.DisplayName()}) connected to {Markup.Escape(s.Csms)}: {(sim.Registered ? "boot accepted" : "boot not accepted")}. Ctrl+C to stop.");
        if (s.Tag is not null)
        {
            await sim.PlugInAsync(1, socPercent: 25, batteryKWh: 60, evMaxKW: 11, ct: ct);
            Ui.Success(await sim.PresentCardAsync(1, s.Tag, ct) ? $"Tag {Markup.Escape(s.Tag)} accepted: charging on connector 1." : $"Tag {Markup.Escape(s.Tag)} refused by the CSMS.");
        }

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                foreach (var c in sim.Connectors)
                    AnsiConsole.MarkupLine($"[{Ui.Hex(Ui.Muted)}]{DateTime.Now:HH:mm:ss}[/] connector {c.Id}: {Markup.Escape(c.Status)}{(c.TransactionId is null ? "" : $", {c.PowerW / 1000:0.0} kW, SoC {c.SoC:0} %, {(c.MeterWh - c.TransactionStartWh) / 1000:0.00} kWh")}");
            }
        }
        catch (OperationCanceledException)
        {
        }

        return 0;
    }
}

internal sealed class OcppDemoCommand : AsyncCommand<OcppDemoCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--allow-write"), Description("Also send commands: remote start, a power limit and remote stop.")]
        public bool AllowWrite { get; init; }

        [CommandOption("--frames"), Description("Print every OCPP message.")]
        public bool Frames { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        var listener = new InMemoryTransportListener("csms");
        await using var csms = OcppCsms.Create(o =>
        {
            o.ListenInMemory(listener);
            o.AuthorizedTags = new HashSet<string>(["04A1B2C3", "APP-7731"], StringComparer.Ordinal);
            if (s.AllowWrite) o.AllowCommands();
        });
        if (s.Frames) csms.Message += OcppUi.Log;
        await csms.StartAsync(ct);
        await using var ac = OcppChargePointSimulator.Create(o => { o.Identity = "SPKLU-KM57-AC1"; o.UseTransport(listener.Connect); });
        await using var dc = OcppChargePointSimulator.Create(o =>
        {
            o.Identity = "SPKLU-KM57-DC1";
            o.Version = OcppVersion.Ocpp201;
            o.Connectors = 1;
            o.MaxPowerW = 60_000;
            o.Model = "IoTCom DC-60";
            o.UseTransport(listener.Connect);
        });
        await ac.StartAsync(ct);
        await dc.StartAsync(ct);
        Ui.Success("Two chargers booted: an AC wallbox on OCPP 1.6J and a DC fast charger on OCPP 2.0.1.");

        await ac.PlugInAsync(1, socPercent: 30, batteryKWh: 60, evMaxKW: 11, ct: ct);
        await ac.PresentCardAsync(1, "04A1B2C3", ct);
        await dc.PlugInAsync(1, socPercent: 18, batteryKWh: 77, evMaxKW: 150, ct: ct);
        Ui.Success(await dc.PresentCardAsync(1, "DEADBEEF", ct) ? "Unknown card accepted?" : "Card DEADBEEF refused by the CSMS (not on the list).");
        try
        {
            Ui.Success($"Remote start on the DC charger: {(await csms.RemoteStartAsync("SPKLU-KM57-DC1", 1, "APP-7731", ct) ? "accepted" : "rejected")}");
        }
        catch (ReadOnlyModeException ex)
        {
            Ui.Warn(Markup.Escape(ex.Message) + " Add --allow-write to try it.");
        }

        await Task.Delay(200, ct);
        for (var minute = 0; minute < 30; minute += 10)
        {
            await ac.StepAsync(TimeSpan.FromMinutes(10), ct);
            await dc.StepAsync(TimeSpan.FromMinutes(10), ct);
        }

        await Task.Delay(200, ct);
        OcppUi.PrintStations(csms);
        if (s.AllowWrite)
        {
            Ui.Success($"Limit the DC charger to 30 kW: {await csms.SetPowerLimitAsync("SPKLU-KM57-DC1", 1, 30_000, ct)}");
            await dc.StepAsync(TimeSpan.FromMinutes(1), ct);
            var tx = csms.Stations["SPKLU-KM57-DC1"].Connectors[1].Transaction;
            if (tx is not null) Ui.Success($"Remote stop {Markup.Escape(tx.Id)}: {(await csms.RemoteStopAsync("SPKLU-KM57-DC1", tx.Id, ct) ? "accepted" : "rejected")}");
        }

        await ac.UnplugAsync(1, ct);
        await Task.Delay(200, ct);
        OcppUi.PrintStations(csms);
        return 0;
    }
}

internal sealed class OcppDecodeCommand : Command<OcppDecodeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<json>"), Description("One OCPP-J message, e.g. [2,\"1\",\"Heartbeat\",{}].")]
        public string Json { get; init; } = "";

        [CommandOption("-v|--version"), Description("Validate the payload against this version's schema: 1.6 or 2.0.1 (default 1.6).")]
        public string? Version { get; init; }
    }

    public override int Execute(CommandContext context, Settings s, CancellationToken ct)
    {
        var version = OcppUi.Version(s.Version);
        AnsiConsole.Write(Ui.FrameLane(Encoding.UTF8.GetBytes(s.Json), OcppFrame.DescribeFields(s.Json), asciiFrame: true));
        if (!OcppFrame.TryParse(s.Json, version, out var frame, out var error))
        {
            Ui.Error($"Invalid: {Markup.Escape(error.Message)} → a receiver answers {Markup.Escape(error.ErrorCode)}{(error.MessageId is null ? " (no answer possible: the id is unreadable)" : "")}.");
            return 1;
        }

        Ui.Success(Markup.Escape(frame.Describe()));
        if (frame.Type == OcppMessageType.Call)
        {
            try
            {
                OcppCatalog.Normalize(version, frame.Action!, false, frame.Payload!);
                Ui.Success($"Payload matches the {version.DisplayName()} {Markup.Escape(frame.Action!)} schema.");
            }
            catch (System.Text.Json.JsonException ex)
            {
                Ui.Warn($"{version.DisplayName()} schema: {Markup.Escape(ex.Message)}");
            }
        }

        return 0;
    }
}
