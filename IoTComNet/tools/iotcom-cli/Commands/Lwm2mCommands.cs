using System.ComponentModel;
using System.Globalization;
using System.Net;
using IoTCom.Net.Protocols.Lwm2m;
using IoTCom.Net.Transports;
using Spectre.Console;
using Spectre.Console.Cli;

namespace IoTCom.Net.Cli.Commands;

internal static class Lwm2mUi
{
    public static void Print(string endpoint, IEnumerable<Lwm2mValue> values)
    {
        var table = new Table().Border(TableBorder.Rounded).Title(Markup.Escape(endpoint)).AddColumns("Path", "Resource", "Value");
        foreach (var v in values)
        {
            var name = Lwm2mRegistry.Find(v.Path.ObjectId, v.Path.ResourceId ?? 0)?.Name ?? "";
            table.AddRow(v.Path.ToString(), Markup.Escape(name), Markup.Escape(Lwm2mValue.Format(v.Value)));
        }

        AnsiConsole.Write(table);
    }
}

internal sealed class Lwm2mServeCommand : AsyncCommand<Lwm2mServeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-p|--port"), Description("UDP port (default 5683).")]
        public int Port { get; init; } = 5683;

        [CommandOption("--observe"), Description("Observe this path on every client that registers (e.g. /3303/0/5700).")]
        public string? Observe { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        await using var server = Lwm2mServer.Create(o => o.UseUdp(s.Port));
        server.Registered += r => _ = OnRegisteredAsync(server, r, s.Observe, ct);
        server.Updated += r => AnsiConsole.MarkupLine($"[{Ui.Hex(Ui.Muted)}]{DateTime.Now:HH:mm:ss}[/] update from {Markup.Escape(r.Endpoint)}");
        server.Deregistered += (r, why) => Ui.Warn($"{Markup.Escape(r.Endpoint)}: {why}");
        await server.StartAsync(ct);
        Ui.Success($"LwM2M server on UDP {s.Port} (read-only: it reads and observes, never writes). Point a device or [bold]iotcom lwm2m client[/] at it. Ctrl+C to stop.");
        try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
        return 0;
    }

    private static async Task OnRegisteredAsync(Lwm2mServer server, Lwm2mRegistration r, string? observe, CancellationToken ct)
    {
        Ui.Success($"{Markup.Escape(r.Endpoint)} registered from {Markup.Escape(r.Address.ToString()!)}: LwM2M {Markup.Escape(r.Version)}, lifetime {r.Lifetime.TotalSeconds:0} s, {Markup.Escape(string.Join(" ", r.Objects))}");
        try
        {
            if (r.Objects.Any(o => o.ObjectId == 3)) Lwm2mUi.Print(r.Endpoint, await server.ReadAsync(r.Endpoint, Lwm2mPath.Parse("/3/0"), ct: ct));
            if (observe is not null)
                await server.ObserveAsync(r.Endpoint, Lwm2mPath.Parse(observe), values =>
                {
                    foreach (var v in values) AnsiConsole.MarkupLine($"[{Ui.Hex(Ui.Muted)}]{DateTime.Now:HH:mm:ss}[/] [{Ui.Hex(Ui.Amber)}]notify[/] {Markup.Escape(r.Endpoint)} {v.Path} = {Markup.Escape(Lwm2mValue.Format(v.Value))}");
                }, ct: ct);
        }
        catch (IoTComException ex)
        {
            Ui.Error(Markup.Escape(ex.Message));
        }
    }
}

internal sealed class Lwm2mClientCommand : AsyncCommand<Lwm2mClientCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-s|--server"), Description("LwM2M server host (default 127.0.0.1).")]
        public string Server { get; init; } = "127.0.0.1";

        [CommandOption("-p|--port"), Description("Server UDP port (default 5683).")]
        public int Port { get; init; } = 5683;

        [CommandOption("-e|--endpoint"), Description("Endpoint client name (default urn:dev:light:SL60-000417).")]
        public string? Endpoint { get; init; }

        [CommandOption("--lifetime"), Description("Registration lifetime in seconds (default 300).")]
        public int Lifetime { get; init; } = 300;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        await using var light = Lwm2mStreetLightSimulator.Create(o =>
        {
            o.UseServer(s.Server, s.Port);
            o.Lifetime = TimeSpan.FromSeconds(s.Lifetime);
            if (s.Endpoint is not null) o.EndpointName = s.Endpoint;
        });
        light.Client.RequestHandled += r => AnsiConsole.MarkupLine($"[{Ui.Hex(Ui.Muted)}]{DateTime.Now:HH:mm:ss}[/] {r.Operation} {Markup.Escape(r.Path)} → {r.Result}");
        light.Rebooted += () => Ui.Warn("Reboot executed by the server.");
        await light.StartAsync(ct);
        Ui.Success($"Street light registered with {Markup.Escape(s.Server)}:{s.Port} as {Markup.Escape(light.Client.Instances.Count.ToString(CultureInfo.InvariantCulture))} object instances, registration {Markup.Escape(light.Client.RegistrationId ?? "")}. Ctrl+C to deregister.");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(2000, ct);
                light.Step(2);
            }
        }
        catch (OperationCanceledException)
        {
        }

        return 0;
    }
}

internal sealed class Lwm2mDemoCommand : AsyncCommand<Lwm2mDemoCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--allow-write"), Description("Also write the dimmer and execute Reboot.")]
        public bool AllowWrite { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        var net = new InMemoryDatagramNetwork();
        var serverAddress = new IPEndPoint(IPAddress.Loopback, 5683);
        await using var server = Lwm2mServer.Create(o => { o.UseInMemory(net, serverAddress); if (s.AllowWrite) o.AllowWrites(); });
        await server.StartAsync(ct);
        await using var light = Lwm2mStreetLightSimulator.Create(o => { o.UseInMemory(net); o.UseServer(serverAddress); });
        await light.StartAsync(ct);
        var r = server.Registrations.Single();
        Ui.Success($"{Markup.Escape(r.Endpoint)} registered: {Markup.Escape(string.Join(" ", r.Objects))}");
        Lwm2mUi.Print(r.Endpoint, await server.ReadAsync(r.Endpoint, Lwm2mPath.Parse("/3/0"), ct: ct));
        Lwm2mUi.Print(r.Endpoint + " (SenML JSON)", await server.ReadAsync(r.Endpoint, Lwm2mPath.Parse("/3311/0"), Lwm2mFormat.SenMLJson, ct));
        await server.WriteAttributesAsync(r.Endpoint, Lwm2mPath.Parse("/3303/0/5700"), pmin: 0, pmax: 2, ct: ct);
        await using (await server.ObserveAsync(r.Endpoint, Lwm2mPath.Parse("/3303/0/5700"), values =>
            AnsiConsole.MarkupLine($"  [{Ui.Hex(Ui.Amber)}]notify[/] driver temperature {Lwm2mValue.Format(values[0].Value)} °C"), ct: ct))
        {
            light.Light.Set(5850, true);
            for (var i = 0; i < 4; i++)
            {
                light.Step(60);
                await Task.Delay(400, ct);
            }
        }

        try
        {
            await server.WriteAsync(r.Endpoint, Lwm2mPath.Parse("/3311/0/5851"), 40L, ct);
            await server.ExecuteAsync(r.Endpoint, Lwm2mPath.Parse("/3/0/4"), ct: ct);
            Ui.Success($"Dimmer set to {light.Dimmer} %, reboots executed: {light.Reboots}.");
        }
        catch (ReadOnlyModeException ex)
        {
            Ui.Warn(Markup.Escape(ex.Message) + " Add --allow-write to try it.");
        }

        return 0;
    }
}

internal sealed class Lwm2mDecodeCommand : Command<Lwm2mDecodeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<path>"), Description("The path the payload belongs to (/3/0, /3303…).")]
        public string Path { get; init; } = "";

        [CommandArgument(1, "<hex>"), Description("TLV payload in hex.")]
        public string Hex { get; init; } = "";
    }

    public override int Execute(CommandContext context, Settings s, CancellationToken ct)
    {
        var bytes = Convert.FromHexString(new string(s.Hex.Where(Uri.IsHexDigit).ToArray()));
        AnsiConsole.Write(Ui.FrameLane(bytes, Lwm2mContent.DescribeTlv(bytes)));
        Lwm2mUi.Print(s.Path, Lwm2mContent.DecodeTlv(Lwm2mPath.Parse(s.Path), bytes));
        return 0;
    }
}
