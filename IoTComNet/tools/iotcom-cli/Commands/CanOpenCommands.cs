using System.ComponentModel;
using System.Globalization;
using IoTCom.Net.Protocols.CanOpen;
using IoTCom.Net.Transport.Can;
using Spectre.Console;
using Spectre.Console.Cli;

namespace IoTCom.Net.Cli.Commands;

/// <summary>Opens a CANopen master on <c>--can</c>; <c>sim</c> starts two simulated I/O modules (nodes 5 and 6).</summary>
internal sealed class CanOpenTarget : IAsyncDisposable
{
    private readonly ICanBus _bus;
    private readonly List<CanOpenIoModuleSimulator> _modules;
    private readonly CancellationTokenSource _cts = new();

    private CanOpenTarget(ICanBus bus, CanOpenMaster master, List<CanOpenIoModuleSimulator> modules)
    {
        (_bus, Master, _modules) = (bus, master, modules);
        if (modules.Count > 0)
            _ = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    foreach (var m in _modules) m.Step();
                    try { await Task.Delay(500, _cts.Token); } catch (OperationCanceledException) { return; }
                }
            });
    }

    public CanOpenMaster Master { get; }

    public static async Task<CanOpenTarget> OpenAsync(CanSettings s, bool readOnly, CancellationToken ct)
    {
        var modules = new List<CanOpenIoModuleSimulator>();
        ICanBus bus;
        if (s.Can.Equals("sim", StringComparison.OrdinalIgnoreCase))
        {
            var net = new VirtualCanNetwork("canopen-sim");
            foreach (byte id in new byte[] { 5, 6 })
            {
                var m = CanOpenIoModuleSimulator.Create(net.CreateNode(), id, heartbeatMs: 1000, eventTimerMs: 500);
                await m.StartAsync(ct);
                modules.Add(m);
            }

            bus = net.CreateNode();
        }
        else
        {
            bus = await CanBus.OpenAsync(s.Can, o => o.Bitrate = s.Bitrate, ct);
        }

        var master = CanOpenMaster.Create(bus, o => o.ReadOnly = readOnly);
        await master.StartAsync(ct);
        return new CanOpenTarget(bus, master, modules);
    }

    /// <summary>Puts the simulated modules into operational (no effect on real buses).</summary>
    public async Task StartSimulatedAsync(CancellationToken ct)
    {
        if (_modules.Count > 0) await _bus.SendAsync(CanOpenCodec.Nmt(NmtCommand.Start, 0), ct);
    }

    public static byte Node(string s) => byte.Parse(s, CultureInfo.InvariantCulture);

    public static (ushort Index, byte Sub) Address(string s)
    {
        var parts = s.Split(':');
        var index = ushort.Parse(parts[0].Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var sub = parts.Length > 1 ? byte.Parse(parts[1].Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber, CultureInfo.InvariantCulture) : (byte)0;
        return (index, sub);
    }

    public static CanOpenDataType Type(string s) => s.ToLowerInvariant() switch
    {
        "u8" => CanOpenDataType.Unsigned8, "u16" => CanOpenDataType.Unsigned16, "u32" => CanOpenDataType.Unsigned32, "u64" => CanOpenDataType.Unsigned64,
        "i8" => CanOpenDataType.Integer8, "i16" => CanOpenDataType.Integer16, "i32" => CanOpenDataType.Integer32, "i64" => CanOpenDataType.Integer64,
        "f32" or "real32" => CanOpenDataType.Real32, "str" or "string" => CanOpenDataType.VisibleString, "bool" => CanOpenDataType.Boolean,
        _ => CanOpenDataType.Domain,
    };

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        await Master.DisposeAsync();
        foreach (var m in _modules) await m.DisposeAsync();
        await _bus.DisposeAsync();
        _cts.Dispose();
    }
}

internal sealed class CanOpenScanCommand : AsyncCommand<CanOpenScanCommand.Settings>
{
    public sealed class Settings : CanSettings
    {
        [CommandOption("--from")] public byte From { get; init; } = 1;
        [CommandOption("--to")] public byte To { get; init; } = 127;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        await using var target = await CanOpenTarget.OpenAsync(s, readOnly: true, ct);
        var nodes = await AnsiConsole.Status().StartAsync($"Reading 0x1000 on nodes {s.From}–{s.To}…", _ => target.Master.ScanAsync(s.From, s.To, ct: ct));
        var table = new Table().Border(TableBorder.Rounded).AddColumns("Node", "Name", "Device type", "Vendor", "Product", "Revision", "Serial", "State");
        foreach (var n in nodes)
            table.AddRow($"[bold]{n.Id}[/]", Markup.Escape(n.Name ?? "-"), $"0x{n.DeviceType:X8}", n.Identity is { } i ? $"0x{i.Vendor:X8}" : "-", n.Identity is { } p ? $"0x{p.Product:X8}" : "-",
                n.Identity is { } r ? $"0x{r.Revision:X8}" : "-", n.Identity is { } sn ? $"{sn.Serial:X8}" : "-", n.State?.ToString() ?? "?");
        AnsiConsole.Write(table);
        if (nodes.Count == 0) Ui.Warn("No node answered. Check the bit rate, termination and that the nodes are not stopped.");
        return 0;
    }
}

internal sealed class CanOpenReadCommand : AsyncCommand<CanOpenReadCommand.Settings>
{
    public sealed class Settings : CanSettings
    {
        [CommandArgument(0, "<node>")] public string Node { get; init; } = "";
        [CommandArgument(1, "<index[:sub]>"), Description("e.g. 1008, 1018:02, 0x6401:01.")] public string Address { get; init; } = "";
        [CommandOption("-t|--type"), Description("u8, u16, u32, i16, i32, f32, str… (default: raw hex).")] public string Type { get; init; } = "raw";
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        await using var target = await CanOpenTarget.OpenAsync(s, readOnly: true, ct);
        var (index, sub) = CanOpenTarget.Address(s.Address);
        var raw = await target.Master.UploadAsync(CanOpenTarget.Node(s.Node), index, sub, ct);
        var type = CanOpenTarget.Type(s.Type);
        var value = type == CanOpenDataType.Domain ? Convert.ToHexString(raw) : Convert.ToString(CanOpenValue.Decode(type, raw), CultureInfo.InvariantCulture);
        AnsiConsole.MarkupLine($"[{Ui.Hex(Ui.CableBlue)}]{index:X4}:{sub:X2}[/] = [bold]{Markup.Escape(value ?? "")}[/] [{Ui.Hex(Ui.Muted)}]({raw.Length} B {Convert.ToHexString(raw)})[/]");
        return 0;
    }
}

internal sealed class CanOpenWriteCommand : AsyncCommand<CanOpenWriteCommand.Settings>
{
    public sealed class Settings : CanSettings
    {
        [CommandArgument(0, "<node>")] public string Node { get; init; } = "";
        [CommandArgument(1, "<index[:sub]>")] public string Address { get; init; } = "";
        [CommandArgument(2, "<value>")] public string Value { get; init; } = "";
        [CommandOption("-t|--type"), Description("u8, u16, u32, i16, i32, f32, str, or raw (hex).")] public string Type { get; init; } = "u8";
        [CommandOption("--allow-write"), Description("Required: this changes the device.")] public bool AllowWrite { get; init; }
        [CommandOption("-y|--yes")] public bool Yes { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        if (!s.AllowWrite)
        {
            Ui.Warn("Writing changes the device. Re-run with [bold]--allow-write[/].");
            return 2;
        }

        if (!s.Can.Equals("sim", StringComparison.OrdinalIgnoreCase) && !s.Yes && !AnsiConsole.Confirm($"Write {Markup.Escape(s.Value)} to node {Markup.Escape(s.Node)} {Markup.Escape(s.Address)}?", false)) return 1;
        await using var target = await CanOpenTarget.OpenAsync(s, readOnly: false, ct);
        var (index, sub) = CanOpenTarget.Address(s.Address);
        var type = CanOpenTarget.Type(s.Type);
        var data = type == CanOpenDataType.Domain ? Convert.FromHexString(s.Value) : CanOpenValue.Encode(type, type == CanOpenDataType.VisibleString ? s.Value : (object)decimal.Parse(s.Value, CultureInfo.InvariantCulture));
        await target.Master.DownloadAsync(CanOpenTarget.Node(s.Node), index, sub, data, ct);
        Ui.Success($"Wrote {index:X4}:{sub:X2} = {Markup.Escape(s.Value)} ({data.Length} B).");
        return 0;
    }
}

internal sealed class CanOpenNmtCommand : AsyncCommand<CanOpenNmtCommand.Settings>
{
    public sealed class Settings : CanSettings
    {
        [CommandArgument(0, "<command>"), Description("start, stop, preop, reset or reset-comm.")] public string Command { get; init; } = "";
        [CommandArgument(1, "[node]"), Description("Node id (default 0 = all nodes).")] public string Node { get; init; } = "0";
        [CommandOption("--allow-write"), Description("Required: NMT changes what devices do.")] public bool AllowWrite { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        if (!s.AllowWrite)
        {
            Ui.Warn("NMT commands start, stop and reset devices. Re-run with [bold]--allow-write[/].");
            return 2;
        }

        var command = s.Command.ToLowerInvariant() switch
        {
            "start" => NmtCommand.Start, "stop" => NmtCommand.Stop, "preop" or "pre-operational" => NmtCommand.EnterPreOperational,
            "reset" => NmtCommand.ResetNode, "reset-comm" => NmtCommand.ResetCommunication,
            _ => throw new ArgumentException("Use start, stop, preop, reset or reset-comm."),
        };
        await using var target = await CanOpenTarget.OpenAsync(s, readOnly: false, ct);
        await target.Master.NmtAsync(command, CanOpenTarget.Node(s.Node), ct);
        await Task.Delay(300, ct);
        Ui.Success($"NMT {command} sent to {(s.Node == "0" ? "all nodes" : "node " + Markup.Escape(s.Node))}.");
        foreach (var n in target.Master.Nodes) AnsiConsole.MarkupLine($"  node {n.Id}: {n.State}");
        return 0;
    }
}

internal sealed class CanOpenMonitorCommand : AsyncCommand<CanSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, CanSettings s, CancellationToken ct)
    {
        await using var target = await CanOpenTarget.OpenAsync(s, readOnly: true, ct);
        await target.StartSimulatedAsync(ct);   // with --can sim the modules are switched to operational so PDOs flow
        target.Master.NodeStateChanged += n => AnsiConsole.MarkupLine($"[{Ui.Hex(Ui.LampGreen)}]● node {n.Id}[/] {n.State}");
        target.Master.EmergencyReceived += (n, e) => AnsiConsole.MarkupLine($"[{Ui.Hex(Ui.Fault)}]● node {n} {Markup.Escape(e.ToString())}[/]");
        target.Master.PdoReceived += (n, p, d) => AnsiConsole.MarkupLine($"[{Ui.Hex(Ui.Muted)}]{DateTime.Now:HH:mm:ss.fff}[/] [{Ui.Hex(Ui.CableBlue)}]TPDO{p}[/] node {n} {Convert.ToHexString(d)}");
        Ui.Success("Listening for heartbeats, PDOs and emergencies. Ctrl+C to stop.");
        try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
        return 0;
    }
}
