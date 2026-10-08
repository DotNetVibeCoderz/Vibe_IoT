using System.ComponentModel;
using System.Globalization;
using System.Text;
using IoTCom.Net.Protocols.J1939;
using IoTCom.Net.Transport.Can;
using Spectre.Console;
using Spectre.Console.Cli;

namespace IoTCom.Net.Cli.Commands;

/// <summary>A J1939 node on <c>--can</c>; <c>sim</c> starts an engine ECU (address 0x00) with the throttle at 40 %.</summary>
internal sealed class J1939Target : IAsyncDisposable
{
    private readonly ICanBus _bus;
    private readonly J1939EngineSimulator? _engine;

    private J1939Target(ICanBus bus, J1939Node node, J1939EngineSimulator? engine) => (_bus, Node, _engine) = (bus, node, engine);

    public J1939Node Node { get; }

    public J1939EngineSimulator? Engine => _engine;

    public static async Task<J1939Target> OpenAsync(CanSettings s, bool listenOnly, CancellationToken ct)
    {
        J1939EngineSimulator? engine = null;
        ICanBus bus;
        if (s.Can.Equals("sim", StringComparison.OrdinalIgnoreCase))
        {
            var net = new VirtualCanNetwork("j1939-sim");
            engine = J1939EngineSimulator.Create(net.CreateNode());
            engine.Throttle = 40;
            await engine.StartAsync(ct);
            bus = net.CreateNode();
        }
        else
        {
            bus = await CanBus.OpenAsync(s.Can, o => { o.Bitrate = s.Bitrate == 500_000 ? 250_000 : s.Bitrate; o.ListenOnly = listenOnly; }, ct);
        }

        var node = J1939Node.Create(bus, o => { o.ListenOnly = listenOnly; o.ReadOnly = true; });
        await node.StartAsync(ct);
        return new J1939Target(bus, node, engine);
    }

    public static uint ParsePgn(string s) => s.ToLowerInvariant() switch
    {
        "vin" or "vi" => Pgn.VehicleIdentification, "ci" or "component" => Pgn.ComponentIdentification, "hours" => Pgn.Hours, "dm1" => Pgn.Dm1, "dm2" => Pgn.Dm2,
        "eec1" => Pgn.Eec1, "ccvs1" => Pgn.Ccvs1, "et1" => Pgn.Et1,
        var x when x.StartsWith("0x", StringComparison.Ordinal) => uint.Parse(x.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        var x => uint.Parse(x, CultureInfo.InvariantCulture),
    };

    public static void Print(J1939Message m)
    {
        var colour = m.Pgn is Pgn.Dm1 or Pgn.Dm2 ? Ui.Fault : J1939Spn.Knows(m.Pgn) ? Ui.CableBlue : Ui.Muted;
        var body = m.Pgn switch
        {
            Pgn.Dm1 or Pgn.Dm2 => J1939Dm1.Parse(m.Data) is var dm ? $"lamps MIL {dm.MalfunctionLamp} RSL {dm.RedStopLamp} AWL {dm.AmberWarningLamp} PL {dm.ProtectLamp}  {(dm.Dtcs.Count == 0 ? "no active DTCs" : string.Join("; ", dm.Dtcs))}" : "",
            Pgn.VehicleIdentification or Pgn.ComponentIdentification => Encoding.ASCII.GetString(m.Data),
            Pgn.AddressClaimed => J1939Name.Parse(m.Data) is var n ? $"function {n.Function} industry group {n.IndustryGroup} manufacturer {n.ManufacturerCode} identity {n.IdentityNumber}" : "",
            _ when J1939Spn.Knows(m.Pgn) => string.Join(", ", m.Values),
            _ => Convert.ToHexString(m.Data),
        };
        AnsiConsole.MarkupLine($"[{Ui.Hex(Ui.Muted)}]{DateTime.Now:HH:mm:ss.fff}[/] [{Ui.Hex(colour)}]{Markup.Escape(Pgn.Name(m.Pgn)),-8}[/] [{Ui.Hex(Ui.Muted)}]0x{m.Source:X2}[/] {Markup.Escape(body)}");
    }

    public async ValueTask DisposeAsync()
    {
        await Node.DisposeAsync();
        if (_engine is not null) await _engine.DisposeAsync();
        await _bus.DisposeAsync();
    }
}

internal sealed class J1939MonitorCommand : AsyncCommand<J1939MonitorCommand.Settings>
{
    public sealed class Settings : CanSettings
    {
        [CommandOption("--pgn"), Description("Only these PGNs (names like eec1, dm1 or numbers; repeatable).")]
        public string[] Pgns { get; init; } = [];
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        await using var target = await J1939Target.OpenAsync(s, listenOnly: true, ct);
        var filter = s.Pgns.Select(J1939Target.ParsePgn).ToHashSet();
        target.Node.MessageReceived += m =>
        {
            if (filter.Count == 0 || filter.Contains(m.Pgn)) J1939Target.Print(m);
        };
        Ui.Success("Listening (no address claimed, nothing transmitted). Ctrl+C to stop.");
        try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
        return 0;
    }
}

internal sealed class J1939RequestCommand : AsyncCommand<J1939RequestCommand.Settings>
{
    public sealed class Settings : CanSettings
    {
        [CommandArgument(0, "<pgn>"), Description("vin, ci, hours, dm1, dm2, eec1… or a number (65260, 0xFEEC).")]
        public string Pgn { get; init; } = "";

        [CommandOption("--to"), Description("Destination address in hex (default FF = everyone).")]
        public string To { get; init; } = "FF";
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        await using var target = await J1939Target.OpenAsync(s, listenOnly: false, ct);
        var answer = await target.Node.RequestAsync(J1939Target.ParsePgn(s.Pgn), byte.Parse(s.To, NumberStyles.HexNumber, CultureInfo.InvariantCulture), TimeSpan.FromSeconds(3), ct);
        J1939Target.Print(answer);
        return 0;
    }
}

internal sealed class J1939ClaimsCommand : AsyncCommand<CanSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, CanSettings s, CancellationToken ct)
    {
        await using var target = await J1939Target.OpenAsync(s, listenOnly: false, ct);
        try
        {
            await target.Node.RequestAsync(Pgn.AddressClaimed, timeout: TimeSpan.FromSeconds(1), ct: ct);
        }
        catch (IoTComTimeoutException)
        {
        }

        await Task.Delay(500, ct);
        var table = new Table().Border(TableBorder.Rounded).AddColumns("Address", "Function", "Industry group", "Manufacturer", "Identity", "Arbitrary");
        foreach (var (address, name) in target.Node.Claims.OrderBy(c => c.Key))
            table.AddRow($"0x{address:X2}", name.Function.ToString(CultureInfo.InvariantCulture), name.IndustryGroup.ToString(CultureInfo.InvariantCulture),
                name.ManufacturerCode.ToString(CultureInfo.InvariantCulture), name.IdentityNumber.ToString(CultureInfo.InvariantCulture), name.ArbitraryAddressCapable ? "yes" : "no");
        AnsiConsole.Write(table);
        return 0;
    }
}
