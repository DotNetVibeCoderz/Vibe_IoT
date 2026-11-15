using System.ComponentModel;
using System.Globalization;
using System.Net;
using IoTCom.Net.Protocols.Knx;
using IoTCom.Net.Transports;
using Spectre.Console;
using Spectre.Console.Cli;

namespace IoTCom.Net.Cli.Commands;

/// <summary>Shared options: a real interface (--gateway) or the in-process office floor (--sim).</summary>
internal class KnxSettings : CommandSettings
{
    [CommandOption("-g|--gateway"), Description("KNX IP interface or router (host[:port]).")]
    public string? Gateway { get; init; }

    [CommandOption("--sim"), Description("Use an in-process KNX IP router with a simulated office floor.")]
    public bool Sim { get; init; }

    [CommandOption("--routing"), Description("Use KNXnet/IP routing (multicast 224.0.23.12) instead of a tunnel.")]
    public bool Routing { get; init; }

    [CommandOption("--frames"), Description("Print every KNXnet/IP frame.")]
    public bool Frames { get; init; }
}

/// <summary>An in-process installation for --sim.</summary>
internal sealed class KnxSim : IAsyncDisposable
{
    public static readonly IPEndPoint Address = new(IPAddress.Parse("10.50.0.2"), KnxIp.Port);
    public InMemoryDatagramNetwork Net { get; } = new();
    public KnxBus Bus { get; } = new();
    public KnxBuildingSimulator Floor { get; }
    public KnxIpGateway Gateway { get; }
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public KnxSim()
    {
        Floor = new KnxBuildingSimulator(Bus);
        Gateway = KnxIpGateway.Create(o => { o.UseInMemory(Net, Address); o.Bus = Bus; });
    }

    public async Task StartAsync()
    {
        await Gateway.StartAsync();
        _loop = Task.Run(async () =>
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    await Task.Delay(1000, _cts.Token);
                    Floor.Step(30);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_loop is not null) await _loop;
        await Gateway.DisposeAsync();
        await Bus.DisposeAsync();
        _cts.Dispose();
    }
}

internal static class KnxUi
{
    public static IPEndPoint Endpoint(string text)
    {
        var parts = text.Split(':');
        var host = parts[0];
        var port = parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : KnxIp.Port;
        var ip = IPAddress.TryParse(host, out var a) ? a : Dns.GetHostAddresses(host).First(x => x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
        return new IPEndPoint(ip, port);
    }

    /// <summary>Connects a tunnel or a routing participant per the settings.</summary>
    public static async Task<(IKnxGroupClient Client, IAsyncDisposable Owner, KnxSim? Sim)> OpenAsync(KnxSettings s, bool writes, CancellationToken ct)
    {
        KnxSim? sim = null;
        if (s.Sim)
        {
            sim = new KnxSim();
            await sim.StartAsync();
        }
        else if (s.Gateway is null && !s.Routing)
        {
            throw new InvalidOperationException("Give --gateway <host>, --routing, or --sim.");
        }

        EndpointBase endpoint;
        if (s.Routing)
        {
            var router = KnxRouter.Create(o =>
            {
                if (sim is not null) o.UseInMemory(sim.Net, new IPEndPoint(IPAddress.Parse("10.50.0.9"), KnxIp.Port));
                if (writes) o.AllowWrites();
            });
            await router.ConnectAsync(ct);
            endpoint = router;
        }
        else
        {
            var tunnel = KnxTunnelClient.Create(o =>
            {
                if (sim is not null) o.UseInMemory(sim.Net).UseGateway(KnxSim.Address);
                else o.UseGateway(Endpoint(s.Gateway!));
                if (writes) o.AllowWrites();
            });
            await tunnel.ConnectAsync(ct);
            Ui.Success($"Tunnel open: channel {tunnel.Channel}, individual address {tunnel.Address}.");
            endpoint = tunnel;
        }

        if (s.Frames) endpoint.AddTap(new FrameTap());
        return ((IKnxGroupClient)endpoint, new Owner(endpoint, sim), sim);
    }

    private sealed class Owner(EndpointBase endpoint, KnxSim? sim) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await endpoint.DisposeAsync();
            if (sim is not null) await sim.DisposeAsync();
        }
    }

    private sealed class FrameTap : ITrafficTap
    {
        public void OnFrame(in TrafficFrame frame) =>
            AnsiConsole.MarkupLine($"[{Ui.Hex(Ui.Muted)}]{frame.Timestamp.ToLocalTime():HH:mm:ss.fff} {(frame.Direction == FrameDirection.Inbound ? "←" : "→")} {Markup.Escape(frame.Summary ?? "")}[/]");
    }

    public static string Format(KnxGroupEvent e, KnxBuildingSimulator? floor, string? dpt)
    {
        var info = floor?.Find(e.Destination);
        var type = dpt ?? info?.Dpt;
        var value = e.Service == KnxApci.GroupValueRead ? "" : type is null ? e.Value.ToString() : KnxDpt.Format(type, e.Value);
        var service = e.Service switch { KnxApci.GroupValueRead => "Read", KnxApci.GroupValueResponse => "Response", _ => "Write" };
        return $"{e.Source,-9} → {e.Destination,-8} {service,-8} {value,-14} {info?.Name}";
    }
}

internal sealed class KnxDiscoverCommand : AsyncCommand<KnxDiscoverCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--sim"), Description("Search an in-process network with a simulated router.")]
        public bool Sim { get; init; }

        [CommandOption("-t|--timeout"), Description("Seconds to collect answers (default 3).")]
        public int Timeout { get; init; } = 3;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        KnxSim? sim = null;
        if (s.Sim)
        {
            sim = new KnxSim();
            await sim.StartAsync();
        }

        try
        {
            var found = await KnxDiscovery.SearchAsync(sim?.Net.Bind(), TimeSpan.FromSeconds(s.Timeout), ct);
            if (found.Count == 0)
            {
                Ui.Warn("No KNX IP interface answered. Check that multicast reaches this network (224.0.23.12:3671).");
                return 1;
            }

            var table = new Table().Border(TableBorder.Rounded).AddColumns("Name", "Address", "Control endpoint", "Serial", "Services");
            foreach (var d in found)
                table.AddRow(Markup.Escape(d.FriendlyName), d.Address.ToString(), Markup.Escape(d.ControlEndpoint?.ToString() ?? ""), Convert.ToHexString(d.SerialNumber),
                    string.Join(", ", d.Services.Select(x => x.Family switch { 2 => "core", 3 => "device management", 4 => "tunnelling", 5 => "routing", _ => $"0x{x.Family:X2}" })));
            AnsiConsole.Write(table);
            return 0;
        }
        finally
        {
            if (sim is not null) await sim.DisposeAsync();
        }
    }
}

internal sealed class KnxMonitorCommand : AsyncCommand<KnxMonitorCommand.Settings>
{
    public sealed class Settings : KnxSettings
    {
        [CommandOption("--seconds"), Description("Stop after this many seconds (default: until Ctrl+C).")]
        public int? Seconds { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        var (client, owner, sim) = await KnxUi.OpenAsync(s, writes: false, ct);
        await using (owner)
        {
            client.GroupValueReceived += e => AnsiConsole.MarkupLine($"[{Ui.Hex(Ui.Muted)}]{e.Time.ToLocalTime():HH:mm:ss}[/] {Markup.Escape(KnxUi.Format(e, sim?.Floor, null))}");
            Ui.Success($"Group monitor (read-only){(sim is null ? "" : " on the simulated office floor (30 s per second)")}. Ctrl+C to stop.");
            try
            {
                await Task.Delay(s.Seconds is { } sec ? TimeSpan.FromSeconds(sec) : Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
            }
        }

        return 0;
    }
}

internal sealed class KnxReadCommand : AsyncCommand<KnxReadCommand.Settings>
{
    public sealed class Settings : KnxSettings
    {
        [CommandArgument(0, "<group-address>"), Description("Group address, e.g. 3/0/1.")]
        public string Address { get; init; } = "";

        [CommandOption("-d|--dpt"), Description("Datapoint type to decode with, e.g. 9.001.")]
        public string? Dpt { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        var (client, owner, sim) = await KnxUi.OpenAsync(s, writes: false, ct);
        await using (owner)
        {
            var ga = KnxGroupAddress.Parse(s.Address);
            var value = await client.ReadAsync(ga, ct: ct);
            var dpt = s.Dpt ?? sim?.Floor.Find(ga)?.Dpt;
            Ui.Success($"{ga}: {Markup.Escape(dpt is null ? value.ToString() : KnxDpt.Format(dpt, value))}{(dpt is null ? " (give --dpt to decode)" : $"  [{Ui.Hex(Ui.Muted)}]DPT {dpt}, raw {value}[/]")}");
        }

        return 0;
    }
}

internal sealed class KnxWriteCommand : AsyncCommand<KnxWriteCommand.Settings>
{
    public sealed class Settings : KnxSettings
    {
        [CommandArgument(0, "<group-address>"), Description("Group address, e.g. 1/0/1.")]
        public string Address { get; init; } = "";

        [CommandArgument(1, "<value>"), Description("Value: on/off/true/false, a number, hh:mm:ss, yyyy-mm-dd, or text.")]
        public string Value { get; init; } = "";

        [CommandOption("-d|--dpt"), Description("Datapoint type, e.g. 1.001, 5.001, 9.001.")]
        public string? Dpt { get; init; }

        [CommandOption("--allow-write"), Description("Required: group writes switch lights, move blinds and change set points.")]
        public bool AllowWrite { get; init; }

        [CommandOption("-y|--yes"), Description("Skip the confirmation.")]
        public bool Yes { get; init; }
    }

    private static object Parse(string dpt, string text) => dpt.Split('.')[0] switch
    {
        "1" => text.ToLowerInvariant() is "1" or "on" or "true" or "down" or "close" or "start",
        "10" => TimeOnly.Parse(text, CultureInfo.InvariantCulture),
        "11" => DateOnly.Parse(text, CultureInfo.InvariantCulture),
        "16" => text,
        _ => double.Parse(text, CultureInfo.InvariantCulture),
    };

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        if (!s.AllowWrite)
        {
            Ui.Error("Group writes change the building (lights, blinds, heating). Add --allow-write.");
            return 2;
        }

        var (client, owner, sim) = await KnxUi.OpenAsync(s, writes: true, ct);
        await using (owner)
        {
            var ga = KnxGroupAddress.Parse(s.Address);
            var dpt = s.Dpt ?? sim?.Floor.Find(ga)?.Dpt ?? throw new InvalidOperationException("Give --dpt (for example 1.001 or 9.001).");
            var value = KnxDpt.Encode(dpt, Parse(dpt, s.Value));
            if (!s.Yes && !s.Sim && !AnsiConsole.Confirm($"Write {Markup.Escape(KnxDpt.Format(dpt, value))} to {ga}?", false)) return 1;
            await client.WriteAsync(ga, value, ct);
            Ui.Success($"Written: {ga} = {Markup.Escape(KnxDpt.Format(dpt, value))} (DPT {dpt}, raw {value}).");
            if (sim is not null)
            {
                await Task.Delay(200, ct);
                var room = sim.Floor.Rooms.FirstOrDefault(r => r.Number == ga.Sub);
                if (room is not null) Ui.Success($"{Markup.Escape(room.Name)}: light {(room.LightOn ? "on" : "off")} {room.Dim:0} %, blind → {room.BlindTarget:0} %, set point {room.Setpoint:0.0} °C");
            }
        }

        return 0;
    }
}

internal sealed class KnxDemoCommand : AsyncCommand<KnxDemoCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--allow-write"), Description("Also switch, dim, move blinds and change a set point.")]
        public bool AllowWrite { get; init; }

        [CommandOption("--frames"), Description("Print every KNXnet/IP frame.")]
        public bool Frames { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        await using var sim = new KnxSim();
        await sim.StartAsync();
        var found = await KnxDiscovery.SearchAsync(sim.Net.Bind(), TimeSpan.FromMilliseconds(300), ct);
        Ui.Success($"Discovered: {Markup.Escape(found[0].ToString())}");
        await using var tunnel = KnxTunnelClient.Create(o => { o.UseInMemory(sim.Net).UseGateway(KnxSim.Address); if (s.AllowWrite) o.AllowWrites(); });
        if (s.Frames) tunnel.AddTap(new FramePrinter());
        tunnel.GroupValueReceived += e => AnsiConsole.MarkupLine($"  [{Ui.Hex(Ui.Muted)}]{Markup.Escape(KnxUi.Format(e, sim.Floor, null))}[/]");
        await tunnel.ConnectAsync(ct);
        Ui.Success($"Tunnel open as {tunnel.Address}.");

        var table = new Table().Border(TableBorder.Rounded).AddColumns("Room", "Temperature", "Set point", "Mode", "Light", "Blind");
        foreach (var r in sim.Floor.Rooms)
        {
            var t = (double)await tunnel.ReadAsync($"3/0/{r.Number}", "9.001", ct: ct);
            var sp = (double)await tunnel.ReadAsync($"3/1/{r.Number}", "9.001", ct: ct);
            var mode = KnxDpt.Format("20.102", await tunnel.ReadAsync(KnxGroupAddress.Parse($"3/2/{r.Number}"), ct: ct));
            var light = (bool)await tunnel.ReadAsync($"1/1/{r.Number}", "1.001", ct: ct);
            var blind = (double)await tunnel.ReadAsync($"2/2/{r.Number}", "5.001", ct: ct);
            table.AddRow(Markup.Escape(r.Name), $"{t:0.0} °C", $"{sp:0.0} °C", mode, light ? "on" : "off", $"{blind:0} %");
        }

        AnsiConsole.Write(table);
        try
        {
            await tunnel.WriteAsync("1/2/1", "5.001", 70, ct);
            await tunnel.WriteAsync("2/1/2", "5.001", 80, ct);
            await tunnel.WriteAsync("3/1/3", "9.001", 20.5, ct);
            await Task.Delay(2500, ct);
            Ui.Success($"Meeting room light {sim.Floor.Rooms[0].Dim:0} %, open-office blind at {sim.Floor.Rooms[1].Blind:0} %, director's set point {sim.Floor.Rooms[2].Setpoint:0.0} °C.");
        }
        catch (ReadOnlyModeException ex)
        {
            Ui.Warn(Markup.Escape(ex.Message) + " Add --allow-write to try it.");
        }

        return 0;
    }

    private sealed class FramePrinter : ITrafficTap
    {
        public void OnFrame(in TrafficFrame frame) =>
            AnsiConsole.MarkupLine($"[{Ui.Hex(Ui.Muted)}]{(frame.Direction == FrameDirection.Inbound ? "←" : "→")} {Markup.Escape(frame.Summary ?? "")}[/]");
    }
}

internal sealed class KnxDecodeCommand : Command<KnxDecodeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<hex>"), Description("A KNXnet/IP frame (06 10 …) or a cEMI frame (11/29/2E …) in hex.")]
        public string Hex { get; init; } = "";

        [CommandOption("-d|--dpt"), Description("Decode the value with this datapoint type.")]
        public string? Dpt { get; init; }
    }

    public override int Execute(CommandContext context, Settings s, CancellationToken ct)
    {
        var bytes = Convert.FromHexString(new string(s.Hex.Where(Uri.IsHexDigit).ToArray()));
        var knxip = bytes.Length >= 2 && bytes[0] == 0x06 && bytes[1] == 0x10;
        AnsiConsole.Write(Ui.FrameLane(bytes, knxip ? KnxIp.DescribeFields(bytes) : KnxCemiFrame.Describe(bytes)));
        if (knxip)
        {
            Ui.Success(Markup.Escape(KnxIp.Describe(bytes)));
            return 0;
        }

        var f = KnxCemiFrame.Decode(bytes);
        Ui.Success(Markup.Escape(f.ToString()) + (s.Dpt is null || f.Apci == KnxApci.GroupValueRead ? "" : $" = {Markup.Escape(KnxDpt.Format(s.Dpt, f.Value))}"));
        return 0;
    }
}
