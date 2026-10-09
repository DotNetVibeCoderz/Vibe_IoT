using System.ComponentModel;
using System.Globalization;
using IoTCom.Net.Protocols.Iec104;
using IoTCom.Net.Transports;
using Spectre.Console;
using Spectre.Console.Cli;

namespace IoTCom.Net.Cli.Commands;

internal class Iec104Settings : CommandSettings
{
    [CommandOption("-h|--host"), Description("Station host (default 127.0.0.1).")]
    public string Host { get; init; } = "127.0.0.1";

    [CommandOption("-p|--port"), Description("TCP port (default 2404).")]
    public int Port { get; init; } = Iec104Apdu.DefaultPort;

    [CommandOption("--ca"), Description("Common address of ASDU (default 1).")]
    public ushort CommonAddress { get; init; } = 1;

    [CommandOption("--sim"), Description("Talk to an in-process 20 kV feeder bay simulator instead of a real station.")]
    public bool Sim { get; init; }

    public string Target => Sim ? "simulated feeder bay" : $"{Host}:{Port}";
}

/// <summary>A connected client, with an in-process simulator when <c>--sim</c> is given.</summary>
internal sealed class Iec104Target : IAsyncDisposable
{
    private readonly Iec104SubstationSimulator? _sim;
    private readonly CancellationTokenSource _stepper = new();

    private Iec104Target(Iec104Client client, Iec104SubstationSimulator? sim)
    {
        (Client, _sim) = (client, sim);
        if (sim is not null) _ = StepAsync(sim, _stepper.Token);
    }

    public Iec104Client Client { get; }

    private static async Task StepAsync(Iec104SubstationSimulator sim, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(500, ct);
                sim.Step(0.5);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public static async Task<Iec104Target> OpenAsync(Iec104Settings s, bool allowCommands, CancellationToken ct)
    {
        Iec104SubstationSimulator? sim = null;
        Iec104Client client;
        if (s.Sim)
        {
            var listener = new InMemoryTransportListener("iec104-sim");
            sim = Iec104SubstationSimulator.Create(o => { o.ListenInMemory(listener); o.CommonAddress = s.CommonAddress; });
            await sim.StartAsync(ct);
            client = Iec104Client.Create(o => { o.UseInMemory(listener); Configure(o); });
        }
        else
        {
            client = Iec104Client.Create(o => { o.UseTcp(s.Host, s.Port); Configure(o); });
        }

        void Configure(Iec104ClientOptions o)
        {
            o.CommonAddress = s.CommonAddress;
            if (allowCommands) o.AllowCommands();
        }

        await client.ConnectAsync(ct);
        return new Iec104Target(client, sim);
    }

    public static string Format(Iec104PointValue p)
    {
        var o = p.Object;
        return Iec104Types.WithoutTime(p.Type) switch
        {
            Iec104TypeId.SinglePoint => o.IsOn ? "ON" : "OFF",
            Iec104TypeId.DoublePoint => o.DoublePoint switch { Iec104DoublePoint.On => "CLOSED", Iec104DoublePoint.Off => "OPEN", Iec104DoublePoint.Intermediate => "moving", _ => "faulty" },
            Iec104TypeId.MeasuredFloat or Iec104TypeId.MeasuredNormalized or Iec104TypeId.MeasuredNormalizedNoQuality => o.Value.ToString("0.###", CultureInfo.InvariantCulture),
            _ => o.Value.ToString(CultureInfo.InvariantCulture),
        };
    }

    public static void Print(Iec104PointValue p)
    {
        var colour = p.Object.Quality.HasFlag(Iec104Quality.Invalid) ? Ui.Fault : p.Cause == Iec104Cause.Spontaneous ? Ui.Amber : Ui.CableBlue;
        var time = p.Object.Time is { } t ? t.Value.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) : DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        AnsiConsole.MarkupLine($"[{Ui.Hex(Ui.Muted)}]{time}[/] [{Ui.Hex(colour)}]{Iec104Types.Mnemonic(p.Type),-9}[/] IOA [bold]{p.Object.Address,6}[/] = {Markup.Escape(Format(p)),-10} " +
            $"[{Ui.Hex(Ui.Muted)}]{Markup.Escape(Iec104Asdu.CauseName(p.Cause))}{(p.Object.Quality != 0 ? " " + p.Object.Quality : "")}[/]");
    }

    public async ValueTask DisposeAsync()
    {
        await _stepper.CancelAsync();
        await Client.DisposeAsync();
        if (_sim is not null) await _sim.DisposeAsync();
        _stepper.Dispose();
    }
}

internal sealed class Iec104InterrogateCommand : AsyncCommand<Iec104InterrogateCommand.Settings>
{
    public sealed class Settings : Iec104Settings
    {
        [CommandOption("--group"), Description("Interrogation group 1–16 (default: station interrogation).")]
        public int Group { get; init; }

        [CommandOption("--counters"), Description("Counter interrogation instead (integrated totals).")]
        public bool Counters { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        await using var target = await Iec104Target.OpenAsync(s, allowCommands: false, ct);
        var points = s.Counters ? await target.Client.CounterInterrogateAsync(ct: ct) : await target.Client.InterrogateAsync((byte)(20 + s.Group), ct: ct);
        var table = new Table().Border(TableBorder.Rounded).AddColumns("IOA", "Type", "Value", "Quality", "Cause");
        foreach (var p in points.OrderBy(p => p.Object.Address))
            table.AddRow(p.Object.Address.ToString(CultureInfo.InvariantCulture), Iec104Types.Mnemonic(p.Type), Markup.Escape(Iec104Target.Format(p)),
                p.Object.Quality == 0 ? "good" : p.Object.Quality.ToString(), Markup.Escape(Iec104Asdu.CauseName(p.Cause)));
        AnsiConsole.Write(table);
        Ui.Success($"{points.Count} value(s) from CA {s.CommonAddress} on {Markup.Escape(s.Target)}.");
        return 0;
    }
}

internal sealed class Iec104ReadCommand : AsyncCommand<Iec104ReadCommand.Settings>
{
    public sealed class Settings : Iec104Settings
    {
        [CommandArgument(0, "<ioa>"), Description("Information object address.")]
        public uint Address { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        await using var target = await Iec104Target.OpenAsync(s, allowCommands: false, ct);
        Iec104Target.Print(await target.Client.ReadAsync(s.Address, ct: ct));
        return 0;
    }
}

internal sealed class Iec104MonitorCommand : AsyncCommand<Iec104MonitorCommand.Settings>
{
    public sealed class Settings : Iec104Settings
    {
        [CommandOption("--no-gi"), Description("Skip the general interrogation at start.")]
        public bool NoInterrogation { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        await using var target = await Iec104Target.OpenAsync(s, allowCommands: false, ct);
        target.Client.PointReceived += p =>
        {
            if (p.Cause is not (Iec104Cause.InterrogatedByStation or Iec104Cause.Request)) Iec104Target.Print(p);
        };
        if (!s.NoInterrogation)
            foreach (var p in (await target.Client.InterrogateAsync(ct: ct)).OrderBy(p => p.Object.Address)) Iec104Target.Print(p);
        Ui.Success($"Data transfer started with {Markup.Escape(s.Target)} (read-only). Spontaneous changes follow; Ctrl+C to stop.");
        try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
        return 0;
    }
}

internal sealed class Iec104CommandCommand : AsyncCommand<Iec104CommandCommand.Settings>
{
    public sealed class Settings : Iec104Settings
    {
        [CommandArgument(0, "<kind>"), Description("single (C_SC), double (C_DC), step (C_RC) or setpoint (C_SE_NC).")]
        public string Kind { get; init; } = "";

        [CommandArgument(1, "<ioa>"), Description("Command object address.")]
        public uint Address { get; init; }

        [CommandArgument(2, "<value>"), Description("on/off (single, double), higher/lower (step), or a number (setpoint).")]
        public string Value { get; init; } = "";

        [CommandOption("--sbo"), Description("Select before operate.")]
        public bool SelectBeforeOperate { get; init; }

        [CommandOption("--allow-write"), Description("Required: confirms you intend to operate equipment.")]
        public bool AllowWrite { get; init; }

        [CommandOption("-y|--yes"), Description("Skip the interactive confirmation.")]
        public bool Yes { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        if (!s.AllowWrite)
        {
            Ui.Warn("IEC 104 commands operate breakers, switches and set points. Re-run with [bold]--allow-write[/] to confirm you intend to.");
            return 3;
        }

        var on = s.Value.ToLowerInvariant() is "on" or "close" or "closed" or "1" or "true" or "higher" or "raise" or "up";
        if (!s.Sim && !s.Yes && !AnsiConsole.Confirm($"Send [bold]{Markup.Escape(s.Kind)} {Markup.Escape(s.Value)}[/] to IOA {s.Address} at CA {s.CommonAddress} on {Markup.Escape(s.Target)}?", false))
            return 4;
        await using var target = await Iec104Target.OpenAsync(s, allowCommands: true, ct);
        var c = target.Client;
        var result = s.Kind.ToLowerInvariant() switch
        {
            "single" or "sc" => await c.SingleCommandAsync(s.Address, on, s.SelectBeforeOperate, ct: ct),
            "double" or "dc" => await c.DoubleCommandAsync(s.Address, on, s.SelectBeforeOperate, ct: ct),
            "step" or "rc" => await c.RegulatingStepAsync(s.Address, on, s.SelectBeforeOperate, ct: ct),
            "setpoint" or "se" => await c.SetpointAsync(s.Address, double.Parse(s.Value, CultureInfo.InvariantCulture), selectBeforeOperate: s.SelectBeforeOperate, ct: ct),
            _ => throw new ArgumentException("Kind must be single, double, step or setpoint."),
        };
        Ui.Success($"{Iec104Types.Mnemonic(result.Type)} IOA {s.Address}: {Markup.Escape(Iec104Asdu.CauseName(result.Cause))}.");
        await Task.Delay(300, ct);   // let return information arrive
        foreach (var p in c.Points.Values.Where(p => p.Cause == Iec104Cause.ReturnRemote)) Iec104Target.Print(p);
        return 0;
    }
}

internal sealed class Iec104ServeCommand : AsyncCommand<Iec104ServeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-p|--port"), Description("TCP port (default 2404).")]
        public int Port { get; init; } = Iec104Apdu.DefaultPort;

        [CommandOption("--ca"), Description("Common address (default 1).")]
        public ushort CommonAddress { get; init; } = 1;

        [CommandOption("--sbo"), Description("Require select-before-operate for every command.")]
        public bool RequireSelect { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        await using var sim = Iec104SubstationSimulator.Create(o =>
        {
            o.UseTcp(s.Port);
            o.CommonAddress = s.CommonAddress;
            o.RequireSelectBeforeOperate = s.RequireSelect;
        });
        sim.Server.ConnectionChanged += (peer, up) => (up ? (Action<string>)Ui.Success : Ui.Warn)($"{Markup.Escape(peer)} {(up ? "connected" : "disconnected")}");
        sim.Server.CommandHandled += r => (r.Accepted ? (Action<string>)Ui.Success : Ui.Error)(
            $"{Iec104Types.Mnemonic(r.Command.Type)} IOA {r.Command.Command.Address} {(r.Command.IsSelect ? "select" : "execute")} value {r.Command.Command.Value.ToString(CultureInfo.InvariantCulture)} from {Markup.Escape(r.Command.Peer)}: {(r.Accepted ? "accepted" : Markup.Escape(r.Reason ?? "refused"))}");
        await sim.StartAsync(ct);
        Ui.Success($"20 kV feeder bay RTU on TCP {s.Port}, CA {s.CommonAddress}. Points 1001–3001, commands 5001–6001. Ctrl+C to stop.");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(500, ct);
                sim.Step(0.5);
            }
        }
        catch (OperationCanceledException)
        {
        }

        return 0;
    }
}
