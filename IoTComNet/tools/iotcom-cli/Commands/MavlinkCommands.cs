using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Net;
using IoTCom.Net.Protocols.Mavlink;
using IoTCom.Net.Protocols.Mavlink.Common;
using IoTCom.Net.Transport.Serial;
using IoTCom.Net.Transports;
using Spectre.Console;
using Spectre.Console.Cli;

namespace IoTCom.Net.Cli.Commands;

internal class MavlinkSettings : CommandSettings
{
    [CommandOption("-u|--udp"), Description("Listen on this UDP port like a ground station (default 14550).")]
    public int? Udp { get; init; }

    [CommandOption("-t|--tcp"), Description("Connect to host:port over TCP (e.g. SITL on 127.0.0.1:5760).")]
    public string? Tcp { get; init; }

    [CommandOption("-s|--serial"), Description("Serial port of a telemetry radio or flight controller.")]
    public string? Serial { get; init; }

    [CommandOption("--baud"), Description("Serial baud rate (default 57600).")]
    public int Baud { get; init; } = 57_600;

    [CommandOption("--target"), Description("Target system id (default 1).")]
    public byte Target { get; init; } = 1;

    public async Task<MavlinkConnection> ConnectAsync(CancellationToken ct)
    {
        var link = MavlinkConnection.Create(o =>
        {
            if (Tcp is { } hp)
            {
                var i = hp.LastIndexOf(':');
                o.UseTcp(hp[..i], int.Parse(hp[(i + 1)..], CultureInfo.InvariantCulture));
            }
            else if (Serial is { } port) o.UseSerial(port, Baud);
            else o.UseUdp(Udp ?? 14550);
        });
        await link.ConnectAsync(ct);
        return link;
    }

    public string Describe() => Tcp is not null ? $"tcp://{Tcp}" : Serial is not null ? $"{Serial}@{Baud}" : $"udp://0.0.0.0:{Udp ?? 14550}";
}

internal sealed class MavlinkListenCommand : AsyncCommand<MavlinkListenCommand.Settings>
{
    public sealed class Settings : MavlinkSettings
    {
        [CommandOption("-f|--filter"), Description("Only print these messages (comma-separated names, e.g. ATTITUDE,STATUSTEXT).")]
        public string? Filter { get; init; }

        [CommandOption("--stats"), Description("Live table of message rates and telemetry instead of a message log.")]
        public bool Stats { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        await using var link = await s.ConnectAsync(ct);
        Ui.Success($"MAVLink on {Markup.Escape(s.Describe())}. Ctrl+C to stop.");
        if (!s.Stats)
        {
            var names = s.Filter?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
            try
            {
                await foreach (var p in link.ReadAllAsync(ct))
                {
                    var name = p.Message?.Name ?? p.MessageId.ToString(CultureInfo.InvariantCulture);
                    if (names is not null && !names.Contains(name)) continue;
                    AnsiConsole.MarkupLine($"[{Ui.Hex(Ui.Muted)}]{p.Timestamp.ToLocalTime():HH:mm:ss.fff}[/] [{Ui.Hex(Ui.CableBlue)}]{p.SystemId}/{p.ComponentId}[/] [{Ui.Hex(Ui.Amber)}]{Markup.Escape(name)}[/] {Markup.Escape(p.Message?.ToString() ?? "")}");
                }
            }
            catch (OperationCanceledException) { }
            return 0;
        }

        var counts = new ConcurrentDictionary<string, int>();
        var state = new MavlinkGroundStation(link, s.Target);
        link.PacketReceived += p => counts.AddOrUpdate(p.Message?.Name ?? p.MessageId.ToString(CultureInfo.InvariantCulture), 1, (_, n) => n + 1);
        Table Render(double seconds)
        {
            var t = new Table().Border(TableBorder.Rounded).Title($"MAVLink · {Markup.Escape(s.Describe())}").AddColumn("Message").AddColumn(new TableColumn("Hz").RightAligned()).AddColumn(" ").AddColumn("Telemetry");
            var v = state.State;
            var telemetry = new[]
            {
                $"{(v.Armed ? $"[{Ui.Hex(Ui.Fault)}]ARMED[/]" : "disarmed")} · {MavlinkVehicleSimulator.ModeName(v.CustomMode)}",
                $"lat {v.Latitude:0.000000} lon {v.Longitude:0.000000}",
                $"alt {v.RelativeAltitude:0.0} m · climb {v.Climb:0.0} m/s",
                $"speed {v.GroundSpeed:0.0} m/s · hdg {v.Heading}°",
                $"roll {v.Roll * 57.3:0.0}° pitch {v.Pitch * 57.3:0.0}°",
                $"battery {v.BatteryVoltage:0.00} V {(v.BatteryRemaining >= 0 ? $"{v.BatteryRemaining}%" : "")}",
                $"GPS {v.GpsFix} · {v.Satellites} sats",
                $"lost {link.Statistics.PacketsLost} · crc errors {link.Parser.CrcErrors}",
            };
            var rows = counts.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
            for (var i = 0; i < Math.Max(rows.Count, telemetry.Length); i++)
            {
                t.AddRow(i < rows.Count ? $"[{Ui.Hex(Ui.Amber)}]{rows[i].Key}[/]" : "", i < rows.Count ? (rows[i].Value / Math.Max(1, seconds)).ToString("0.0", CultureInfo.InvariantCulture) : "",
                    "", i < telemetry.Length ? telemetry[i] : "");
            }
            return t;
        }
        var started = DateTime.UtcNow;
        await AnsiConsole.Live(Render(1)).StartAsync(async live =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(1000, ct);
                    live.UpdateTarget(Render((DateTime.UtcNow - started).TotalSeconds));
                }
            }
            catch (OperationCanceledException) { }
        });
        return 0;
    }
}

internal sealed class MavlinkSimulateCommand : AsyncCommand<MavlinkSimulateCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--to"), Description("Ground station address (default 127.0.0.1:14550).")]
        public string To { get; init; } = "127.0.0.1:14550";

        [CommandOption("--port"), Description("Local UDP port of the vehicle (default 14555).")]
        public int Port { get; init; } = 14555;

        [CommandOption("--sysid"), Description("Vehicle system id (default 1).")]
        public byte SystemId { get; init; } = 1;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        var i = s.To.LastIndexOf(':');
        var gcs = new IPEndPoint(IPAddress.Parse(s.To[..i]), int.Parse(s.To[(i + 1)..], CultureInfo.InvariantCulture));
        await using var link = MavlinkConnection.Create(o =>
        {
            o.UseUdp(s.Port).SendTo(gcs);
            (o.SystemId, o.ComponentId) = (s.SystemId, 1);
        });
        await using var sim = new MavlinkVehicleSimulator(link);
        await link.ConnectAsync(ct);
        sim.Start();
        Ui.Success($"Simulated quadcopter (system {s.SystemId}) on udp://0.0.0.0:{s.Port} → {gcs}. Home: Bandung. Ctrl+C to stop.");
        Ui.Warn($"Try: iotcom mavlink listen --stats   ·   iotcom mavlink cmd arm --allow-write   ·   QGroundControl / Mission Planner on UDP {gcs.Port}");
        var last = sim.Phase;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(250, ct);
                if (sim.Phase == last) continue;
                last = sim.Phase;
                AnsiConsole.MarkupLine($"[{Ui.Hex(Ui.Muted)}]{DateTime.Now:HH:mm:ss}[/] phase [{Ui.Hex(Ui.Amber)}]{last}[/] · battery {sim.BatteryVoltage:0.00} V");
            }
        }
        catch (OperationCanceledException) { }
        return 0;
    }
}

internal sealed class MavlinkCommandCommand : AsyncCommand<MavlinkCommandCommand.Settings>
{
    public sealed class Settings : MavlinkSettings
    {
        [CommandArgument(0, "<action>"), Description("arm, disarm, takeoff, land or rtl.")]
        public string Action { get; init; } = "";

        [CommandArgument(1, "[value]"), Description("Takeoff altitude in metres (default 10).")]
        public float? Value { get; init; }

        [CommandOption("--allow-write"), Description("Required: commands move real vehicles.")]
        public bool AllowWrite { get; init; }

        [CommandOption("-y|--yes")]
        public bool Yes { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        if (!s.AllowWrite)
        {
            Ui.Warn("Commands move real vehicles. Props off, area clear — then re-run with [bold]--allow-write[/].");
            return 2;
        }
        if (!s.Yes && !AnsiConsole.Confirm($"Send [bold]{Markup.Escape(s.Action)}[/] to system {s.Target}?", false)) return 1;
        await using var link = await s.ConnectAsync(ct);
        using var gcs = new MavlinkGroundStation(link, s.Target);
        gcs.StatusText += (sev, text) => AnsiConsole.MarkupLine($"[{Ui.Hex(sev <= MavSeverity.Warning ? Ui.Fault : Ui.Muted)}]{Markup.Escape(text)}[/]");
        if (!await gcs.WaitForHeartbeatAsync(TimeSpan.FromSeconds(5), ct))
        {
            Ui.Error($"No heartbeat from system {s.Target} on {Markup.Escape(s.Describe())}.");
            return 3;
        }
        var result = s.Action.ToLowerInvariant() switch
        {
            "arm" => await gcs.ArmAsync(true, ct: ct),
            "disarm" => await gcs.ArmAsync(false, ct: ct),
            "takeoff" => await gcs.TakeoffAsync(s.Value ?? 10, ct),
            "land" => await gcs.LandAsync(ct),
            "rtl" => await gcs.ReturnToLaunchAsync(ct),
            _ => throw new ArgumentException("Action must be arm, disarm, takeoff, land or rtl."),
        };
        await Task.Delay(300, ct); // let the status text arrive
        (result == MavResult.Accepted ? (Action<string>)Ui.Success : Ui.Error)($"{Markup.Escape(s.Action)} → {result}");
        return result == MavResult.Accepted ? 0 : 2;
    }
}

internal sealed class MavlinkParamsCommand : AsyncCommand<MavlinkParamsCommand.Settings>
{
    public sealed class Settings : MavlinkSettings
    {
        [CommandArgument(0, "[name]"), Description("Parameter to set (with a value and --allow-write).")]
        public string? Name { get; init; }

        [CommandArgument(1, "[value]")]
        public float? Value { get; init; }

        [CommandOption("--allow-write")]
        public bool AllowWrite { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        await using var link = await s.ConnectAsync(ct);
        using var gcs = new MavlinkGroundStation(link, s.Target) { ReadOnly = !s.AllowWrite };
        if (!await gcs.WaitForHeartbeatAsync(TimeSpan.FromSeconds(5), ct))
        {
            Ui.Error($"No heartbeat from system {s.Target}.");
            return 3;
        }
        if (s.Name is { } name && s.Value is { } value)
        {
            if (!s.AllowWrite)
            {
                Ui.Warn("Changing parameters alters vehicle behaviour. Re-run with [bold]--allow-write[/].");
                return 2;
            }
            Ui.Success($"{Markup.Escape(name)} = {await gcs.SetParameterAsync(name, value, ct: ct)}");
            return 0;
        }
        var all = await gcs.ReadParametersAsync(ct);
        var t = new Table().Border(TableBorder.Rounded).AddColumn("Parameter").AddColumn(new TableColumn("Value").RightAligned());
        foreach (var (k, v) in all) t.AddRow(k, v.ToString("0.###", CultureInfo.InvariantCulture));
        AnsiConsole.Write(t);
        return 0;
    }
}

internal sealed class MavlinkDecodeCommand : Command<MavlinkDecodeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<hex>"), Description("A frame in hex, e.g. FD09000007010100000000000000020351040315A9.")]
        public string Hex { get; init; } = "";
    }

    public override int Execute(CommandContext context, Settings s, CancellationToken ct)
    {
        var bytes = Convert.FromHexString(s.Hex.Replace(" ", "", StringComparison.Ordinal));
        AnsiConsole.Write(Ui.FrameLane(bytes, MavlinkAnatomy.Describe(bytes, CommonDialect.Instance)));
        var parser = new MavlinkParser(CommonDialect.Instance);
        parser.Feed(bytes);
        if (parser.TryRead(out var p)) Ui.Success(Markup.Escape(p.ToString()));
        else Ui.Error($"Not a valid frame (CRC errors {parser.CrcErrors}, unknown ids {parser.UnknownMessages}).");
        return 0;
    }
}
