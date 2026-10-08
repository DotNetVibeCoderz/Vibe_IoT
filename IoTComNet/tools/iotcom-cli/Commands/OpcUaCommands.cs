using System.ComponentModel;
using IoTCom.Net.Adapters.OpcUa;
using Spectre.Console;
using Spectre.Console.Cli;

namespace IoTCom.Net.Cli.Commands;

internal class OpcUaSettings : CommandSettings
{
    [CommandOption("-e|--endpoint"), Description("Server endpoint, e.g. opc.tcp://plc.local:4840.")]
    public string Endpoint { get; init; } = "opc.tcp://localhost:4840";

    [CommandOption("--sim"), Description("Start the plant simulator in this process and connect to it.")]
    public bool Simulate { get; init; }

    [CommandOption("--no-security"), Description("Use SecurityPolicy None instead of the most secure endpoint.")]
    public bool NoSecurity { get; init; }

    [CommandOption("--accept-untrusted"), Description("Accept a server certificate that is not trusted yet (commissioning only).")]
    public bool AcceptUntrusted { get; init; }

    [CommandOption("--user")] public string? User { get; init; }

    [CommandOption("--password")] public string? Password { get; init; }

    /// <summary>Connects (starting the simulator when asked); dispose the returned pair.</summary>
    public async Task<(OpcUaClient Client, OpcUaPlantServer? Server)> ConnectAsync(bool readOnly, CancellationToken ct)
    {
        OpcUaPlantServer? server = null;
        var endpoint = Endpoint;
        if (Simulate)
        {
            server = OpcUaPlantServer.Create(o => o.Port = FreePort());
            await server.StartAsync(ct);
            endpoint = server.EndpointUrl;
        }

        var client = OpcUaClient.Create(o =>
        {
            o.UseEndpoint(endpoint);
            o.UseSecurity = !NoSecurity;
            o.AcceptUntrustedCertificates = AcceptUntrusted || Simulate;
            o.ReadOnly = readOnly;
            if (User is not null) o.WithCredentials(User, Password ?? "");
        });
        await client.ConnectAsync(ct);
        Ui.Success($"Connected to [bold]{Markup.Escape(endpoint)}[/] · {Markup.Escape(client.SecurityPolicy ?? "?")} / {Markup.Escape(client.SecurityMode ?? "?")}");
        return (client, server);
    }

    /// <summary>Plant node ids are written relative to the simulator ("Line1/Filler/Speed") when --sim is used.</summary>
    public string Resolve(string nodeId, OpcUaPlantServer? server) =>
        server is not null && !nodeId.Contains('=', StringComparison.Ordinal) ? server.NodeId(nodeId) : nodeId;

    private static int FreePort()
    {
        using var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        return ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
    }
}

internal sealed class OpcUaBrowseCommand : AsyncCommand<OpcUaBrowseCommand.Settings>
{
    public sealed class Settings : OpcUaSettings
    {
        [CommandArgument(0, "[node]"), Description("Node to start from (default: the Objects folder).")]
        public string? Node { get; init; }

        [CommandOption("-d|--depth"), Description("Levels to expand (default 5).")]
        public int Depth { get; init; } = 5;

        [CommandOption("--all"), Description("Also show the standard nodes of namespace 0 (Server, Aliases, Locations).")]
        public bool All { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        var (client, server) = await s.ConnectAsync(readOnly: true, ct);
        await using var _c = client;
        await using var _s = server;
        var tree = new Tree($"[bold]{Markup.Escape(s.Node ?? "Objects")}[/]");
        async Task Expand(IHasTreeNodes parent, string? nodeId, int depth)
        {
            foreach (var n in await client.BrowseAsync(nodeId is null ? null : s.Resolve(nodeId, server), ct))
            {
                if (!s.All && n.NodeId.StartsWith("i=", StringComparison.Ordinal)) continue;   // standard namespace-0 nodes
                var label = n.NodeClass switch
                {
                    "Variable" => $"[{Ui.Hex(Ui.CableBlue)}]{Markup.Escape(n.DisplayName)}[/] [{Ui.Hex(Ui.Muted)}]{Markup.Escape(n.NodeId)}[/] = {Markup.Escape((await client.ReadAsync(n.NodeId, ct)).Text)}",
                    "Method" => $"[{Ui.Hex(Ui.Amber)}]{Markup.Escape(n.DisplayName)}()[/] [{Ui.Hex(Ui.Muted)}]{Markup.Escape(n.NodeId)}[/]",
                    _ => $"[bold]{Markup.Escape(n.DisplayName)}[/] [{Ui.Hex(Ui.Muted)}]{Markup.Escape(n.NodeId)}[/]",
                };
                var node = parent.AddNode(label);
                if (n.IsContainer && depth > 1) await Expand(node, n.NodeId, depth - 1);
            }
        }

        await Expand(tree, s.Node, s.Depth);
        AnsiConsole.Write(tree);
        return 0;
    }
}

internal sealed class OpcUaReadCommand : AsyncCommand<OpcUaReadCommand.Settings>
{
    public sealed class Settings : OpcUaSettings
    {
        [CommandArgument(0, "<nodes>"), Description("Node ids, e.g. ns=2;s=Plant/Line1/Filler/Speed (with --sim: Line1/Filler/Speed).")]
        public string[] Nodes { get; init; } = [];
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        var (client, server) = await s.ConnectAsync(readOnly: true, ct);
        await using var _c = client;
        await using var _s = server;
        var table = new Table().Border(TableBorder.Rounded).AddColumns("Node", "Value", "Status", "Source time");
        foreach (var v in await client.ReadAsync([.. s.Nodes.Select(n => s.Resolve(n, server))], ct))
            table.AddRow(Markup.Escape(v.NodeId), $"[bold]{Markup.Escape(v.Text)}[/]", v.IsGood ? $"[{Ui.Hex(Ui.LampGreen)}]{v.Status}[/]" : $"[{Ui.Hex(Ui.Fault)}]{v.Status}[/]",
                v.SourceTimestamp?.LocalDateTime.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture) ?? "-");
        AnsiConsole.Write(table);
        return 0;
    }
}

internal sealed class OpcUaWatchCommand : AsyncCommand<OpcUaWatchCommand.Settings>
{
    public sealed class Settings : OpcUaSettings
    {
        [CommandArgument(0, "<nodes>")] public string[] Nodes { get; init; } = [];

        [CommandOption("-i|--interval"), Description("Publishing interval in ms (default 500).")]
        public int Interval { get; init; } = 500;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        var (client, server) = await s.ConnectAsync(readOnly: true, ct);
        await using var _c = client;
        await using var _s = server;
        Ui.Success("Subscribed. Ctrl+C to stop.");
        try
        {
            await foreach (var v in client.SubscribeAsync([.. s.Nodes.Select(n => s.Resolve(n, server))], TimeSpan.FromMilliseconds(s.Interval), ct))
                AnsiConsole.MarkupLine($"[{Ui.Hex(Ui.Muted)}]{(v.SourceTimestamp ?? DateTimeOffset.Now).LocalDateTime:HH:mm:ss.fff}[/] [{Ui.Hex(Ui.CableBlue)}]{Markup.Escape(v.NodeId)}[/] = [bold]{Markup.Escape(v.Text)}[/]{(v.IsGood ? "" : $" [{Ui.Hex(Ui.Fault)}]{v.Status}[/]")}");
        }
        catch (OperationCanceledException) { }
        return 0;
    }
}

internal sealed class OpcUaWriteCommand : AsyncCommand<OpcUaWriteCommand.Settings>
{
    public sealed class Settings : OpcUaSettings
    {
        [CommandArgument(0, "<node>")] public string Node { get; init; } = "";

        [CommandArgument(1, "<value>"), Description("Converted to the variable's data type.")]
        public string Value { get; init; } = "";

        [CommandOption("--allow-write"), Description("Required: writing changes a running device.")]
        public bool AllowWrite { get; init; }

        [CommandOption("-y|--yes"), Description("Skip the confirmation prompt.")]
        public bool Yes { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        if (!s.AllowWrite)
        {
            Ui.Warn("Writing changes a running device. Re-run with [bold]--allow-write[/].");
            return 2;
        }

        if (!s.Simulate && !s.Yes && !AnsiConsole.Confirm($"Write {Markup.Escape(s.Value)} to {Markup.Escape(s.Node)}?", false)) return 1;
        var (client, server) = await s.ConnectAsync(readOnly: false, ct);
        await using var _c = client;
        await using var _s = server;
        var node = s.Resolve(s.Node, server);
        var before = await client.ReadAsync(node, ct);
        await client.WriteAsync(node, s.Value, ct);
        var after = await client.ReadAsync(node, ct);
        Ui.Success($"{Markup.Escape(node)}: {Markup.Escape(before.Text)} → [bold]{Markup.Escape(after.Text)}[/]");
        return 0;
    }
}

internal sealed class OpcUaCallCommand : AsyncCommand<OpcUaCallCommand.Settings>
{
    public sealed class Settings : OpcUaSettings
    {
        [CommandArgument(0, "<object>")] public string Object { get; init; } = "";

        [CommandArgument(1, "<method>")] public string Method { get; init; } = "";

        [CommandArgument(2, "[args]"), Description("Input arguments (strings; the server converts them when it can).")]
        public string[] Args { get; init; } = [];

        [CommandOption("--allow-write"), Description("Required: methods usually change device state.")]
        public bool AllowWrite { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        if (!s.AllowWrite)
        {
            Ui.Warn("Methods usually change device state. Re-run with [bold]--allow-write[/].");
            return 2;
        }

        var (client, server) = await s.ConnectAsync(readOnly: false, ct);
        await using var _c = client;
        await using var _s = server;
        var outputs = await client.CallAsync(s.Resolve(s.Object, server), s.Resolve(s.Method, server), [.. s.Args], ct);
        Ui.Success($"{Markup.Escape(s.Method)} returned {Markup.Escape(string.Join(", ", outputs.Select(o => Convert.ToString(o, System.Globalization.CultureInfo.InvariantCulture))))}");
        return 0;
    }
}

internal sealed class OpcUaSimulateCommand : AsyncCommand<OpcUaSimulateCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-p|--port"), Description("TCP port (default 4840).")]
        public int Port { get; init; } = 4840;

        [CommandOption("--host"), Description("Host name in the endpoint URL (default localhost).")]
        public string Host { get; init; } = "localhost";
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        await using var server = OpcUaPlantServer.Create(o => { o.Port = s.Port; o.Host = s.Host; });
        await server.StartAsync(ct);
        Ui.Success($"OPC UA plant simulator on [bold]{Markup.Escape(server.EndpointUrl)}[/] (None and Basic256Sha256). Nodes under {Markup.Escape(server.NodeId("Line1"))}. Ctrl+C to stop.");
        try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
        return 0;
    }
}
