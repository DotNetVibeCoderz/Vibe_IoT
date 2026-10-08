using System.ComponentModel;
using System.Globalization;
using System.Net;
using IoTCom.Net.Adapters.Mqtt;
using IoTCom.Net.Protocols.Mdns;
using IoTCom.Net.Protocols.Sparkplug;
using IoTCom.Net.Serialization.MessagePack;
using IoTCom.Net.Serialization.Protobuf;
using IoTCom.Net.Serialization.Tlv;
using Spectre.Console;
using Spectre.Console.Cli;

namespace IoTCom.Net.Cli.Commands;

// ---- mDNS / DNS-SD -----------------------------------------------------------------------------------------------

internal sealed class MdnsBrowseCommand : AsyncCommand<MdnsBrowseCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[type]"), Description("Service type, e.g. _modbus._tcp, _mqtt._tcp, _coap._udp. Omit to list every type.")]
        public string? Type { get; init; }

        [CommandOption("-t|--seconds"), Description("How long to listen (default 3).")]
        public double Seconds { get; init; } = 3;

        [CommandOption("--watch"), Description("Keep browsing and print services as they appear and leave.")]
        public bool Watch { get; init; }

        [CommandOption("--sim"), Description("Browse a simulated plant network instead of the real one.")]
        public bool Simulate { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        await using var plant = s.Simulate ? new MdnsSimulator() : null;
        if (plant is not null) await plant.StartAsync(ct);
        await using var browser = plant?.Browser() ?? MdnsBrowser.Create();
        await browser.StartAsync(ct);
        var duration = TimeSpan.FromSeconds(s.Seconds);
        var types = s.Type is { } t ? [t] : await browser.EnumerateTypesAsync(duration, ct);
        if (types.Count == 0)
        {
            Ui.Warn("No DNS-SD services answered. Check that UDP 5353 is not blocked by a firewall.");
            return 0;
        }

        var table = new Table().Border(TableBorder.Rounded).AddColumns("Service", "Type", "Host", "Address", "Port", "TXT");
        foreach (var type in types)
        {
            foreach (var svc in await browser.BrowseAsync(type.Replace(".local", "", StringComparison.Ordinal), duration, ct))
                table.AddRow($"[bold]{Markup.Escape(svc.Instance)}[/]", Markup.Escape(svc.Type), Markup.Escape(svc.Host), Markup.Escape(svc.Address?.ToString() ?? "-"),
                    svc.Port.ToString(CultureInfo.InvariantCulture), Markup.Escape(string.Join(" ", svc.Properties.Select(p => $"{p.Key}={p.Value}"))));
        }

        AnsiConsole.Write(table);
        if (!s.Watch) return 0;

        Ui.Success("Watching for changes. Ctrl+C to stop.");
        browser.ServiceChanged += (_, c) => AnsiConsole.MarkupLine(c.Lost
            ? $"[{Ui.Hex(Ui.Fault)}]○ left[/] {Markup.Escape(c.Service.Instance)} ({Markup.Escape(c.Service.Type)})"
            : $"[{Ui.Hex(Ui.LampGreen)}]● seen[/] {Markup.Escape(c.Service.ToString())}");
        try
        {
            await Task.WhenAll(types.Select(type => browser.BrowseContinuouslyAsync(type.Replace(".local", "", StringComparison.Ordinal), ct)));
        }
        catch (OperationCanceledException) { }
        return 0;
    }
}

internal sealed class MdnsAdvertiseCommand : AsyncCommand<MdnsAdvertiseCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<instance>"), Description("Instance name, e.g. \"Line 1 gateway\".")]
        public string Instance { get; init; } = "";

        [CommandArgument(1, "<type>"), Description("Service type, e.g. _modbus._tcp.")]
        public string Type { get; init; } = "";

        [CommandArgument(2, "<port>"), Description("Port the service listens on.")]
        public ushort Port { get; init; }

        [CommandOption("--txt"), Description("TXT properties as key=value (repeatable).")]
        public string[] Txt { get; init; } = [];
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        await using var responder = MdnsResponder.Create();
        await responder.StartAsync(ct);
        var service = new MdnsService
        {
            Instance = s.Instance,
            Type = s.Type,
            Port = s.Port,
            Addresses = MdnsAddresses.LocalAddresses(),
            Properties = s.Txt.Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : ""),
        };
        await responder.RegisterAsync(service, ct);
        Ui.Success($"Advertising [bold]{Markup.Escape(service.FullName)}[/] on {Markup.Escape(string.Join(", ", service.Addresses))}:{s.Port}. Ctrl+C to stop (sends a goodbye).");
        try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
        await responder.UnregisterAsync(service.FullName, CancellationToken.None);
        return 0;
    }
}

// ---- Sparkplug B ---------------------------------------------------------------------------------------------------

internal sealed class SparkplugWatchCommand : AsyncCommand<SparkplugWatchCommand.Settings>
{
    public sealed class Settings : MqttSettings
    {
        [CommandOption("--host-id"), Description("Host application id published on spBv1.0/STATE/<id> (default iotcom-cli).")]
        public string HostId { get; init; } = "iotcom-cli";

        [CommandOption("--sim"), Description("Start an embedded broker and a simulated bottling line, then watch them.")]
        public bool Simulate { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        await using var sim = s.Simulate ? await SparkplugSim.StartAsync(s.Port, ct) : null;
        await using var host = SparkplugHost.Create(o =>
        {
            o.HostId = s.HostId;
            o.Mqtt = m =>
            {
                m.UseBroker(s.Simulate ? "127.0.0.1" : s.Host, s.Port).WithTls(s.Tls);
                if (s.User is not null) m.WithCredentials(s.User, s.Password ?? "");
            };
        });
        host.MessageReceived += (_, e) =>
        {
            var colour = e.Topic.Type switch
            {
                SparkplugMessageType.NBirth or SparkplugMessageType.DBirth => Ui.LampGreen,
                SparkplugMessageType.NDeath or SparkplugMessageType.DDeath => Ui.Fault,
                SparkplugMessageType.NCmd or SparkplugMessageType.DCmd => Ui.Amber,
                _ => Ui.CableBlue,
            };
            AnsiConsole.MarkupLine($"[{Ui.Hex(Ui.Muted)}]{DateTime.Now:HH:mm:ss.fff}[/] [{Ui.Hex(colour)}]{e.Topic.Type.ToString().ToUpperInvariant(),-6}[/] {Markup.Escape(e.Topic.ToString())} {Markup.Escape(e.Payload?.ToString() ?? "")}");
        };
        await host.StartAsync(ct);
        Ui.Success($"Sparkplug host [bold]{Markup.Escape(s.HostId)}[/] online on {(s.Simulate ? "embedded broker" : $"{Markup.Escape(s.Host)}:{s.Port}")}. Ctrl+C to stop.");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct);
                if (sim is not null) await sim.Line.Step(ct);
            }
        }
        catch (OperationCanceledException) { }

        var table = new Table().Border(TableBorder.Rounded).AddColumns("Node / device", "State", "Metrics");
        foreach (var v in host.Views.OrderBy(v => v.Key, StringComparer.Ordinal))
            table.AddRow(Markup.Escape(v.Key), v.Online ? $"[{Ui.Hex(Ui.LampGreen)}]online[/]" : $"[{Ui.Hex(Ui.Fault)}]offline[/]",
                Markup.Escape(string.Join(", ", v.Metrics.Values.Where(m => !m.Name!.StartsWith("Node Control", StringComparison.Ordinal)).Select(m => m.ToString()))));
        AnsiConsole.Write(table);
        return 0;
    }
}

internal sealed class SparkplugSimulateCommand : AsyncCommand<SparkplugSimulateCommand.Settings>
{
    public sealed class Settings : MqttSettings
    {
        [CommandOption("--group"), Description("Group id (default Plant).")]
        public string Group { get; init; } = "Plant";

        [CommandOption("--node"), Description("Edge node id (default Line1).")]
        public string Node { get; init; } = "Line1";

        [CommandOption("--embedded-broker"), Description("Also run an MQTT broker on --port.")]
        public bool EmbeddedBroker { get; init; }

        [CommandOption("--read-only"), Description("Ignore write commands from hosts (rebirth still works).")]
        public bool ReadOnly { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        await using var broker = s.EmbeddedBroker ? MqttBroker.Create(s.Port) : null;
        if (broker is not null) await broker.StartAsync(ct);
        await using var node = SparkplugEdgeNode.Create(o =>
        {
            o.Group = s.Group;
            o.EdgeNode = s.Node;
            o.AcceptWrites = !s.ReadOnly;
            o.Mqtt = m =>
            {
                m.UseBroker(s.EmbeddedBroker ? "127.0.0.1" : s.Host, s.Port).WithTls(s.Tls);
                if (s.User is not null) m.WithCredentials(s.User, s.Password ?? "");
            };
        });
        var line = new SparkplugLineSimulator(node).Define();
        node.CommandReceived += (_, c) => AnsiConsole.MarkupLine($"[{Ui.Hex(Ui.Amber)}]● write[/] {Markup.Escape(c.Device ?? s.Node)}/{Markup.Escape(c.Metric)} = {Markup.Escape(Convert.ToString(c.Value, CultureInfo.InvariantCulture) ?? "null")}");
        node.Published += (_, e) =>
        {
            if (e.Topic.Type is SparkplugMessageType.NBirth or SparkplugMessageType.DBirth)
                AnsiConsole.MarkupLine($"[{Ui.Hex(Ui.LampGreen)}]● {e.Topic.Type.ToString().ToUpperInvariant()}[/] {Markup.Escape(e.Topic.ToString())} ({e.Payload.Metrics.Count} metrics)");
        };
        await node.StartAsync(ct);
        Ui.Success($"Edge node [bold]{Markup.Escape(s.Group)}/{Markup.Escape(s.Node)}[/] (bdSeq {node.BdSeq}) publishing every second{(s.ReadOnly ? ", read-only" : "")}. Ctrl+C to stop (sends DDEATH/NDEATH).");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await line.Step(ct);
                await Task.Delay(1000, ct);
            }
        }
        catch (OperationCanceledException) { }
        return 0;
    }
}

internal sealed class SparkplugWriteCommand : AsyncCommand<SparkplugWriteCommand.Settings>
{
    public sealed class Settings : MqttSettings
    {
        [CommandArgument(0, "<target>"), Description("group/node[/device], e.g. Plant/Line1/Filler.")]
        public string Target { get; init; } = "";

        [CommandArgument(1, "<metric>")] public string Metric { get; init; } = "";

        [CommandArgument(2, "<value>")] public string Value { get; init; } = "";

        [CommandOption("--allow-write"), Description("Required: this sends an NCMD/DCMD that changes a device.")]
        public bool AllowWrite { get; init; }

        [CommandOption("-y|--yes"), Description("Skip the confirmation prompt.")]
        public bool Yes { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        var parts = s.Target.Split('/');
        if (parts.Length is < 2 or > 3)
        {
            Ui.Error("Target must be group/node or group/node/device.");
            return 1;
        }

        if (!s.AllowWrite)
        {
            Ui.Warn("Writing changes a running device. Re-run with [bold]--allow-write[/].");
            return 2;
        }

        if (!s.Yes && !AnsiConsole.Confirm($"Write {Markup.Escape(s.Metric)} = {Markup.Escape(s.Value)} on {Markup.Escape(s.Target)}?", false)) return 1;
        await using var host = SparkplugHost.Create(o =>
        {
            o.HostId = "iotcom-cli-write";
            o.RequestRebirthOnGap = false;
            o.Mqtt = m =>
            {
                m.UseBroker(s.Host, s.Port).WithTls(s.Tls);
                if (s.User is not null) m.WithCredentials(s.User, s.Password ?? "");
            };
        });
        await host.StartAsync(ct);
        await host.RequestRebirthAsync(parts[0], parts[1], ct);
        var device = parts.Length == 3 ? parts[2] : null;
        for (var i = 0; i < 50 && host.Find(parts[0], parts[1], device)?.Metrics.ContainsKey(s.Metric) != true; i++) await Task.Delay(100, ct);
        var view = host.Find(parts[0], parts[1], device);
        if (view?.Metrics.TryGetValue(s.Metric, out var known) != true)
        {
            Ui.Error($"{Markup.Escape(s.Target)} did not announce {Markup.Escape(s.Metric)} in its birth certificate.");
            return 1;
        }

        object value = known!.DataType switch
        {
            SparkplugDataType.Boolean => bool.Parse(s.Value),
            SparkplugDataType.Float or SparkplugDataType.Double => double.Parse(s.Value, CultureInfo.InvariantCulture),
            SparkplugDataType.String or SparkplugDataType.Text or SparkplugDataType.Uuid => s.Value,
            _ => long.Parse(s.Value, CultureInfo.InvariantCulture),
        };
        await host.WriteAsync(parts[0], parts[1], device, s.Metric, value, ct);
        Ui.Success($"Sent {(device is null ? "NCMD" : "DCMD")} {Markup.Escape(s.Metric)} = {Markup.Escape(s.Value)} ({known.DataType}).");
        return 0;
    }
}

/// <summary>An embedded broker plus a simulated bottling line for <c>--sim</c>.</summary>
internal sealed class SparkplugSim : IAsyncDisposable
{
    private SparkplugSim(MqttBroker broker, SparkplugEdgeNode node, SparkplugLineSimulator line) => (Broker, Node, Line) = (broker, node, line);

    public MqttBroker Broker { get; }

    public SparkplugEdgeNode Node { get; }

    public SparkplugLineSimulator Line { get; }

    public static async Task<SparkplugSim> StartAsync(int port, CancellationToken ct)
    {
        var broker = MqttBroker.Create(port);
        await broker.StartAsync(ct);
        var node = SparkplugEdgeNode.Create(o => { o.Group = "Plant"; o.EdgeNode = "Line1"; o.Mqtt = m => m.UseBroker("127.0.0.1", port); });
        var line = new SparkplugLineSimulator(node).Define();
        var sim = new SparkplugSim(broker, node, line);
        // The host starts after this; it asks for a rebirth when it sees data without a birth.
        await node.StartAsync(ct);
        return sim;
    }

    public async ValueTask DisposeAsync()
    {
        await Node.DisposeAsync();
        await Broker.DisposeAsync();
    }
}

// ---- payload decoding ----------------------------------------------------------------------------------------------

internal sealed class PayloadDecodeCommand : Command<PayloadDecodeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<format>"), Description("protobuf, msgpack, ber-tlv, tlv, sparkplug or dns.")]
        public string Format { get; init; } = "";

        [CommandArgument(1, "<hex>"), Description("The payload in hex (spaces allowed).")]
        public string Hex { get; init; } = "";
    }

    public override int Execute(CommandContext context, Settings s, CancellationToken ct)
    {
        var bytes = Convert.FromHexString(new string(s.Hex.Where(Uri.IsHexDigit).ToArray()));
        switch (s.Format.ToLowerInvariant())
        {
            case "protobuf":
                AnsiConsole.Write(Ui.FrameLane(bytes, ProtobufWire.Describe(bytes)));
                if (!ProtobufWire.TryInspect(bytes, out var fields)) return Fail("Not valid Protobuf wire format.");
                var tree = new Tree("[bold]message[/]");
                void Add(IHasTreeNodes parent, IReadOnlyList<ProtobufField> list)
                {
                    foreach (var f in list)
                    {
                        var n = parent.AddNode($"[{Ui.Hex(Ui.Amber)}]#{f.Number}[/] [{Ui.Hex(Ui.Muted)}]{WireName(f.WireType)}[/] {Markup.Escape(f.Value)}");
                        Add(n, f.Nested);
                    }
                }

                Add(tree, fields);
                AnsiConsole.Write(tree);
                return 0;
            case "msgpack":
                AnsiConsole.Write(Ui.FrameLane(bytes, MessagePackView.Describe(bytes)));
                return MessagePackView.ToJson(bytes) is { } json ? Print(json) : Fail("Not valid MessagePack.");
            case "ber-tlv":
                AnsiConsole.Write(Ui.FrameLane(bytes, BerTlv.Describe(bytes)));
                return Tlv(() => BerTlv.Decode(bytes));
            case "tlv":
                return Tlv(() => TlvFormat.Simple.Decode(bytes));
            case "sparkplug":
                AnsiConsole.Write(Ui.FrameLane(bytes, SparkplugPayload.Describe(bytes)));
                try
                {
                    var p = SparkplugPayload.Decode(bytes);
                    var table = new Table().Border(TableBorder.Rounded).AddColumns("Metric", "Alias", "Type", "Value");
                    foreach (var m in p.Metrics)
                        table.AddRow(Markup.Escape(m.Name ?? "-"), m.Alias?.ToString(CultureInfo.InvariantCulture) ?? "-", m.DataType.ToString(), Markup.Escape(m.IsNull ? "null" : Convert.ToString(m.Value, CultureInfo.InvariantCulture) ?? ""));
                    AnsiConsole.MarkupLine($"seq {p.Seq?.ToString(CultureInfo.InvariantCulture) ?? "-"} · timestamp {(p.Timestamp is { } ts ? DateTimeOffset.FromUnixTimeMilliseconds((long)ts).ToString("O", CultureInfo.InvariantCulture) : "-")}");
                    AnsiConsole.Write(table);
                    return 0;
                }
                catch (ProtocolException ex)
                {
                    return Fail(ex.Message);
                }

            case "dns":
                AnsiConsole.Write(Ui.FrameLane(bytes, DnsMessage.Describe(bytes)));
                if (!DnsMessage.TryDecode(bytes, out var dns, out var error)) return Fail(error!);
                foreach (var q in dns!.Questions) AnsiConsole.MarkupLine($"[{Ui.Hex(Ui.Amber)}]?[/] {Markup.Escape(q.Name)} {q.Type}{(q.UnicastResponse ? " (QU)" : "")}");
                foreach (var r in dns.AllRecords) AnsiConsole.MarkupLine($"[{Ui.Hex(Ui.LampGreen)}]=[/] {Markup.Escape(r.ToString())}");
                return 0;
            default:
                return Fail("Format must be protobuf, msgpack, ber-tlv, tlv, sparkplug or dns.");
        }
    }

    private static string WireName(int wire) => wire switch { 0 => "varint", 1 => "i64", 2 => "len", 5 => "i32", _ => "?" };

    private static int Print(string text)
    {
        AnsiConsole.WriteLine(text);
        return 0;
    }

    private static int Fail(string message)
    {
        Ui.Error(Markup.Escape(message));
        return 1;
    }

    private static int Tlv(Func<IReadOnlyList<TlvItem>> decode)
    {
        try
        {
            var tree = new Tree("[bold]TLV[/]");
            void Add(IHasTreeNodes parent, IEnumerable<TlvItem> items)
            {
                foreach (var i in items)
                {
                    var label = i.Children.Count > 0
                        ? $"[{Ui.Hex(Ui.Amber)}]{i.Tag:X}[/] [{Ui.Hex(Ui.Muted)}]constructed, {i.Value.Length} B[/]"
                        : $"[{Ui.Hex(Ui.CableBlue)}]{i.Tag:X}[/] {Convert.ToHexString(i.Value)}{(i.Value.Length > 0 && i.Value.All(b => b is >= 0x20 and < 0x7F) ? $" [{Ui.Hex(Ui.Muted)}]\"{Markup.Escape(System.Text.Encoding.ASCII.GetString(i.Value))}\"[/]" : "")}";
                    Add(parent.AddNode(label), i.Children);
                }
            }

            Add(tree, decode());
            AnsiConsole.Write(tree);
            return 0;
        }
        catch (FormatException ex)
        {
            return Fail(ex.Message);
        }
    }
}
