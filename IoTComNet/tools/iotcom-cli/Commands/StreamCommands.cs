using System.ComponentModel;
using System.Globalization;
using System.Net;
using IoTCom.Net.Adapters.Mqtt;
using IoTCom.Net.Protocols.Dmx;
using IoTCom.Net.Protocols.Nmea;
using IoTCom.Net.Transport.Serial;
using Spectre.Console;
using Spectre.Console.Cli;

namespace IoTCom.Net.Cli.Commands;

internal sealed class NmeaListenCommand : AsyncCommand<NmeaListenCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-h|--host")] public string Host { get; init; } = "127.0.0.1";
        [CommandOption("-p|--port"), Description("NMEA-over-TCP port (default 10110).")] public int Port { get; init; } = 10110;
        [CommandOption("--serial"), Description("GPS receiver serial port.")] public string? Serial { get; init; }
        [CommandOption("--baud")] public int Baud { get; init; } = 9600;
        [CommandOption("--raw"), Description("Print every sentence instead of the live fix panel.")] public bool Raw { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        await using var reader = NmeaReader.Create(o =>
        {
            if (s.Serial is not null) o.UseSerial(s.Serial, s.Baud);
            else o.UseTcp(s.Host, s.Port);
        });
        await reader.ConnectAsync(ct);
        Ui.Success($"Listening to {(s.Serial ?? $"{s.Host}:{s.Port}")}. Ctrl+C to stop.");
        if (s.Raw)
        {
            await foreach (var m in reader.ReadAllAsync(ct))
                AnsiConsole.MarkupLine($"[{Ui.Hex(Ui.Amber)}]{m.Sentence.Type}[/] [{Ui.Hex(Ui.Muted)}]{Markup.Escape(m.Sentence.Raw)}[/]");
            return 0;
        }
        await AnsiConsole.Live(new Text("Waiting for a fix…")).StartAsync(async live =>
        {
            while (!ct.IsCancellationRequested)
            {
                var f = reader.Gnss.Current;
                var grid = new Grid().AddColumn().AddColumn();
                grid.AddRow("[bold]Fix[/]", f.HasFix ? $"[{Ui.Hex(Ui.LampGreen)}]● {f.Quality}[/]" : $"[{Ui.Hex(Ui.Amber)}]● searching[/]");
                grid.AddRow("Position", f.HasFix ? $"{f.Latitude:F6}, {f.Longitude:F6}" : "—");
                grid.AddRow("Altitude", f.AltitudeMeters is { } a ? $"{a:F1} m" : "—");
                grid.AddRow("Speed / course", f.SpeedKmh is { } v ? $"{v:F1} km/h · {f.CourseDegrees:F0}°" : "—");
                grid.AddRow("Satellites", $"{f.SatellitesUsed} used / {f.SatellitesInView.Count} in view · HDOP {f.Hdop:F1}");
                var bars = string.Join(" ", f.SatellitesInView.Take(16).Select(sat => $"{sat.Prn:00}[{Ui.Hex(Ui.CableBlue)}]{new string('▮', Math.Clamp((sat.Snr ?? 0) / 8, 0, 6))}[/]"));
                grid.AddRow("SNR", bars.Length > 0 ? bars : "—");
                live.UpdateTarget(new Panel(grid).Header("[bold] NMEA 0183 [/]").BorderColor(Ui.Muted));
                try { await Task.Delay(500, ct); } catch (OperationCanceledException) { }
            }
        });
        return 0;
    }
}

internal sealed class NmeaSimulateCommand : AsyncCommand<NmeaSimulateCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-p|--port"), Description("TCP port to serve (default 10110).")] public int Port { get; init; } = 10110;
        [CommandOption("--lat")] public double Latitude { get; init; } = -6.9147;
        [CommandOption("--lon")] public double Longitude { get; init; } = 107.6098;
        [CommandOption("--speed"), Description("km/h")] public double Speed { get; init; } = 36;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        await using var server = NmeaServer.Create(o => o.UseTcp(IPAddress.Any, s.Port));
        await server.StartAsync(ct);
        var sim = new NmeaSimulator(s.Latitude, s.Longitude, speedKmh: s.Speed);
        Ui.Success($"Simulated GPS on tcp://0.0.0.0:{s.Port} — connect with [bold]iotcom nmea listen --port {s.Port}[/]. Ctrl+C to stop.");
        await sim.RunAsync(server, ct: ct);
        return 0;
    }
}

internal sealed class ArtNetSendCommand : AsyncCommand<ArtNetSendCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-u|--universe"), Description("Port-address (0-32767).")] public int Universe { get; init; }
        [CommandOption("-v|--values"), Description("Comma separated channel levels starting at channel 1.")] public string Values { get; init; } = "";
        [CommandOption("--to"), Description("Unicast IP (default broadcast).")] public string? To { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        var values = s.Values.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(v => byte.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        await using var node = ArtNetNode.Create(o => { o.Port = 0; o.RespondToPoll = false; });
        await node.StartAsync(ct);
        var dest = s.To is null ? null : new IPEndPoint(IPAddress.Parse(s.To), ArtNetPacket.Port);
        await node.SendDmxAsync(s.Universe, values, dest, ct);
        Ui.Success($"ArtDmx universe {s.Universe}: {values.Length} channels sent to {(s.To ?? "broadcast")}.");
        return 0;
    }
}

internal sealed class ArtNetPollCommand : AsyncCommand<ArtNetPollCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--wait"), Description("Seconds to collect replies (default 3).")] public int Wait { get; init; } = 3;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        await using var node = ArtNetNode.Create(o => o.RespondToPoll = false);
        await node.StartAsync(ct);
        await node.PollAsync(ct: ct);
        await AnsiConsole.Status().StartAsync("Polling the network…", _ => Task.Delay(TimeSpan.FromSeconds(s.Wait), ct));
        var t = new Table().Border(TableBorder.Rounded).BorderColor(Ui.Muted).AddColumn("[bold]Node[/]").AddColumn("[bold]Address[/]").AddColumn("[bold]Ports[/]").AddColumn("[bold]Report[/]");
        foreach (var n in node.Nodes) t.AddRow(Markup.Escape(n.ShortName), n.Address.ToString(), n.NumPorts.ToString(CultureInfo.InvariantCulture), Markup.Escape(n.NodeReport));
        if (node.Nodes.Count == 0) Ui.Warn("No Art-Net nodes answered. Check that nodes are on this subnet and UDP 6454 is allowed.");
        else AnsiConsole.Write(t);
        return 0;
    }
}

internal sealed class ArtNetMonitorCommand : AsyncCommand<ArtNetMonitorCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-u|--universe"), Description("Only show this universe.")] public int? Universe { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        await using var node = ArtNetNode.Create(o => o.WithName("iotcom monitor"));
        await node.StartAsync(ct);
        Ui.Success("Monitoring Art-Net on UDP 6454. Ctrl+C to stop.");
        await foreach (var f in node.ReceiveAsync(s.Universe, ct))
        {
            var levels = string.Join("", f.Data.ToArray().Take(48).Select(v => " ▁▂▃▄▅▆▇█"[v * 8 / 255]));
            AnsiConsole.MarkupLine($"[{Ui.Hex(Ui.CableBlue)}]U{f.Universe,-5}[/] [{Ui.Hex(Ui.Amber)}]{Markup.Escape(levels)}[/] [{Ui.Hex(Ui.Muted)}]seq {f.Sequence} · {Markup.Escape(f.Source)}[/]");
        }
        return 0;
    }
}

internal class MqttSettings : CommandSettings
{
    [CommandOption("-h|--host")] public string Host { get; init; } = "127.0.0.1";
    [CommandOption("-p|--port")] public int Port { get; init; } = 1883;
    [CommandOption("--user")] public string? User { get; init; }
    [CommandOption("--password")] public string? Password { get; init; }
    [CommandOption("--tls")] public bool Tls { get; init; }

    public MqttEndpoint Create() => MqttEndpoint.Create(o =>
    {
        o.UseBroker(Host, Port).WithTls(Tls).WithReconnect(ReconnectPolicy.None);
        if (User is not null) o.WithCredentials(User, Password ?? "");
    });
}

internal sealed class MqttPublishCommand : AsyncCommand<MqttPublishCommand.Settings>
{
    public sealed class Settings : MqttSettings
    {
        [CommandArgument(0, "<topic>")] public string Topic { get; init; } = "";
        [CommandArgument(1, "<message>")] public string Message { get; init; } = "";
        [CommandOption("-q|--qos")] public int Qos { get; init; }
        [CommandOption("-r|--retain")] public bool Retain { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        await using var mqtt = s.Create();
        await mqtt.ConnectAsync(ct);
        await mqtt.PublishStringAsync(s.Topic, s.Message, new PublishOptions { QualityOfService = (QualityOfService)s.Qos, Retain = s.Retain }, ct);
        Ui.Success($"Published to [bold]{Markup.Escape(s.Topic)}[/].");
        return 0;
    }
}

internal sealed class MqttSubscribeCommand : AsyncCommand<MqttSubscribeCommand.Settings>
{
    public sealed class Settings : MqttSettings
    {
        [CommandArgument(0, "<filter>")] public string Filter { get; init; } = "#";
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        await using var mqtt = s.Create();
        await mqtt.ConnectAsync(ct);
        Ui.Success($"Subscribed to [bold]{Markup.Escape(s.Filter)}[/] on {s.Host}:{s.Port}. Ctrl+C to stop.");
        try
        {
            await foreach (var m in mqtt.SubscribeStringAsync(s.Filter, ct))
                AnsiConsole.MarkupLine($"[{Ui.Hex(Ui.Muted)}]{m.Timestamp.LocalDateTime:HH:mm:ss.fff}[/] [{Ui.Hex(Ui.CableBlue)}]{Markup.Escape(m.Topic)}[/] {Markup.Escape(m.Payload)}");
        }
        catch (OperationCanceledException) { }
        return 0;
    }
}

internal sealed class MqttBrokerCommand : AsyncCommand<MqttBrokerCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-p|--port")] public int Port { get; init; } = 1883;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        await using var broker = MqttBroker.Create(s.Port);
        broker.ClientConnected += id => AnsiConsole.MarkupLine($"[{Ui.Hex(Ui.LampGreen)}]●[/] connected [bold]{Markup.Escape(id)}[/]");
        broker.ClientDisconnected += id => AnsiConsole.MarkupLine($"[{Ui.Hex(Ui.Muted)}]○ disconnected {Markup.Escape(id)}[/]");
        await broker.StartAsync(ct);
        Ui.Success($"MQTT broker on tcp://0.0.0.0:{s.Port}. Ctrl+C to stop.");
        try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
        Ui.Warn($"Stopped after routing {broker.MessagesRouted} messages.");
        return 0;
    }
}
