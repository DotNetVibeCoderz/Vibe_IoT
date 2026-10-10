using System.ComponentModel;
using System.Globalization;
using System.Text;
using IoTCom.Net.Adapters.Zenoh;
using Spectre.Console;
using Spectre.Console.Cli;

namespace IoTCom.Net.Cli.Commands;

internal class ZenohSettings : CommandSettings
{
    [CommandOption("--sim"), Description("Use an in-process Zenoh network with a simulated plant (publishes plant/<line>/temp|pressure every 500 ms, answers plant/*/info).")]
    public bool Simulate { get; init; }

    [CommandOption("-m|--mode"), Description("Session mode: peer (default) or client.")]
    public string Mode { get; init; } = "peer";

    [CommandOption("-e|--connect"), Description("Endpoint to connect to, e.g. tcp/192.168.1.10:7447 (repeatable).")]
    public string[] Connect { get; init; } = [];

    [CommandOption("-l|--listen"), Description("Endpoint to listen on, e.g. tcp/0.0.0.0:7447 (repeatable).")]
    public string[] Listen { get; init; } = [];

    [CommandOption("--no-scouting"), Description("Switch multicast scouting off (use with explicit --connect/--listen).")]
    public bool NoScouting { get; init; }

    /// <summary>Opens a session; with --sim also starts the simulated plant on the same virtual network.</summary>
    public async Task<(ZenohSession Session, IAsyncDisposable? Sim)> OpenAsync(bool readOnly, CancellationToken ct)
    {
        IAsyncDisposable? sim = null;
        VirtualZenohNetwork? net = null;
        if (Simulate)
        {
            net = new VirtualZenohNetwork();
            sim = await ZenohPlantSimulator.StartAsync(net, ct);
        }

        var session = ZenohSession.Create(o =>
        {
            o.ReadOnly = readOnly;
            o.Mode = Mode.Equals("client", StringComparison.OrdinalIgnoreCase) ? ZenohMode.Client
                : Mode.Equals("peer", StringComparison.OrdinalIgnoreCase) ? ZenohMode.Peer
                : throw new ArgumentException("--mode must be 'peer' or 'client'.");
            o.MulticastScouting = !NoScouting;
            foreach (var e in Connect) o.Connect(e);
            foreach (var e in Listen) o.Listen(e);
            if (net is not null) o.UseVirtual(net);
        });
        try
        {
            await session.ConnectAsync(ct);
        }
        catch
        {
            await session.DisposeAsync();
            if (sim is not null) await sim.DisposeAsync();
            throw;
        }

        Ui.Success($"Zenoh session [bold]{Markup.Escape(session.Zid)}[/] [{Ui.Hex(Ui.Muted)}]{Markup.Escape(session.Backend!.Description)}[/]");
        return (session, sim);
    }

    public static void PrintSample(ZenohSample s)
    {
        var colour = s.Kind == ZenohSampleKind.Put ? Ui.LampGreen : Ui.Amber;
        var body = s.Kind == ZenohSampleKind.Delete ? "(deleted)" : Render(s.Payload, s.Encoding);
        AnsiConsole.MarkupLine($"[{Ui.Hex(Ui.Muted)}]{s.Timestamp.LocalDateTime:HH:mm:ss.fff}[/] [{Ui.Hex(colour)}]{(s.Kind == ZenohSampleKind.Put ? "PUT" : "DEL")}[/] [bold]{Markup.Escape(s.Key)}[/] {Markup.Escape(body)}"
            + (s.Encoding is null ? "" : $" [{Ui.Hex(Ui.Muted)}]{Markup.Escape(s.Encoding)}[/]"));
    }

    /// <summary>Shows a payload as text when it is printable UTF-8, otherwise as hex.</summary>
    public static string Render(byte[] payload, string? encoding)
    {
        if (payload.Length == 0) return "";
        var text = Encoding.UTF8.GetString(payload);
        var printable = !text.Contains('�', StringComparison.Ordinal) && text.All(c => !char.IsControl(c) || c is '\n' or '\r' or '\t');
        return printable ? text : Convert.ToHexString(payload);
    }
}

/// <summary>A small simulated plant for <c>--sim</c>: periodic samples and an info queryable.</summary>
internal sealed class ZenohPlantSimulator : IAsyncDisposable
{
    private readonly ZenohSession _session;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly IDisposable _queryable;

    private ZenohPlantSimulator(ZenohSession session)
    {
        _session = session;
        _queryable = session.DeclareQueryable("plant/*/info", async q =>
        {
            foreach (var line in new[] { "line1", "line2" })
            {
                var key = $"plant/{line}/info";
                if (ZenohKeyExpr.Intersects(key, q.Key))
                    await q.ReplyAsync(key, $"{{\"line\":\"{line}\",\"state\":\"running\",\"query\":\"{q.Parameters}\"}}");
            }
        });
        _loop = Task.Run(LoopAsync);
    }

    public static async Task<ZenohPlantSimulator> StartAsync(VirtualZenohNetwork net, CancellationToken ct)
    {
        var session = ZenohSession.Create(o =>
        {
            o.UseVirtual(net);
            o.Name = "plant-sim";
        });
        await session.ConnectAsync(ct);
        return new ZenohPlantSimulator(session);
    }

    private async Task LoopAsync()
    {
        var step = 0;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                foreach (var line in new[] { "line1", "line2" })
                {
                    var phase = step * 0.3 + (line == "line1" ? 0 : 1.7);
                    await _session.PutAsync($"plant/{line}/temp", (21.5 + 2 * Math.Sin(phase)).ToString("F1", CultureInfo.InvariantCulture), _cts.Token);
                    await _session.PutAsync($"plant/{line}/pressure", (101.3 + 0.8 * Math.Cos(phase)).ToString("F1", CultureInfo.InvariantCulture), _cts.Token);
                }

                step++;
                await Task.Delay(500, _cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        await _loop;
        _queryable.Dispose();
        await _session.DisposeAsync();
        _cts.Dispose();
    }
}

internal sealed class ZenohSubCommand : AsyncCommand<ZenohSubCommand.Settings>
{
    public sealed class Settings : ZenohSettings
    {
        [CommandArgument(0, "<keyexpr>"), Description("Key expression to subscribe to, e.g. 'plant/**' or 'plant/*/temp'.")]
        public string KeyExpr { get; init; } = "";

        [CommandOption("-n|--count"), Description("Stop after this many samples (default: run until Ctrl+C).")]
        public int Count { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        var (session, sim) = await s.OpenAsync(readOnly: true, ct);
        await using var session_ = session;
        await using var sim_ = sim;
        Ui.Success($"Subscribed to [bold]{Markup.Escape(s.KeyExpr)}[/]. Ctrl+C to stop.");
        var seen = 0;
        try
        {
            await foreach (var sample in session.WatchAsync(s.KeyExpr, ct))
            {
                ZenohSettings.PrintSample(sample);
                if (s.Count > 0 && ++seen >= s.Count) break;
            }
        }
        catch (OperationCanceledException)
        {
        }

        return 0;
    }
}

internal sealed class ZenohGetCommand : AsyncCommand<ZenohGetCommand.Settings>
{
    public sealed class Settings : ZenohSettings
    {
        [CommandArgument(0, "<selector>"), Description("Key expression with optional parameters, e.g. 'plant/*/info?detail=1'.")]
        public string Selector { get; init; } = "";

        [CommandOption("--body"), Description("Optional request payload (text).")]
        public string? Body { get; init; }

        [CommandOption("-t|--timeout"), Description("Seconds to wait for replies (default 5).")]
        public double Timeout { get; init; } = 5;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        var (session, sim) = await s.OpenAsync(readOnly: true, ct);
        await using var session_ = session;
        await using var sim_ = sim;
        var replies = await session.GetAsync(s.Selector, Encoding.UTF8.GetBytes(s.Body ?? ""), TimeSpan.FromSeconds(s.Timeout), ct);
        if (replies.Count == 0)
        {
            Ui.Warn($"No queryable answered {Markup.Escape(s.Selector)} within {s.Timeout.ToString(CultureInfo.InvariantCulture)} s.");
            return 1;
        }

        foreach (var r in replies)
        {
            if (r.Sample is { } sample)
                AnsiConsole.MarkupLine($"[{Ui.Hex(Ui.LampGreen)}]REPLY[/] [bold]{Markup.Escape(sample.Key)}[/] {Markup.Escape(ZenohSettings.Render(sample.Payload, sample.Encoding))}");
            else
                AnsiConsole.MarkupLine($"[{Ui.Hex(Ui.Fault)}]ERROR[/] {Markup.Escape(r.ErrorText)}");
        }

        return replies.Any(r => r.IsError) ? 2 : 0;
    }
}

internal sealed class ZenohPubCommand : AsyncCommand<ZenohPubCommand.Settings>
{
    public sealed class Settings : ZenohSettings
    {
        [CommandArgument(0, "<key>"), Description("Concrete key to publish on, e.g. plant/line1/setpoint.")]
        public string Key { get; init; } = "";

        [CommandArgument(1, "<value>"), Description("Text payload, or 'hex:0A0B' for raw bytes.")]
        public string Value { get; init; } = "";

        [CommandOption("--delete"), Description("Delete the key instead of putting a value.")]
        public bool Delete { get; init; }

        [CommandOption("-n|--count"), Description("Publish this many times (default 1).")]
        public int Count { get; init; } = 1;

        [CommandOption("-i|--interval"), Description("Milliseconds between repeats (default 1000).")]
        public int Interval { get; init; } = 1000;

        [CommandOption("--allow-write"), Description("Required: publishing changes what other nodes see.")]
        public bool AllowWrite { get; init; }

        [CommandOption("-y|--yes")] public bool Yes { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        if (!s.AllowWrite)
        {
            Ui.Warn("Publishing changes what other nodes see. Re-run with [bold]--allow-write[/].");
            return 2;
        }

        if (!s.Simulate && !s.Yes && !AnsiConsole.Confirm($"{(s.Delete ? "Delete" : "Put on")} {Markup.Escape(s.Key)}?", false)) return 1;
        var payload = s.Value.StartsWith("hex:", StringComparison.OrdinalIgnoreCase)
            ? Convert.FromHexString(s.Value[4..])
            : Encoding.UTF8.GetBytes(s.Value);
        var encoding = s.Value.StartsWith("hex:", StringComparison.OrdinalIgnoreCase) ? null : "text/plain";
        var (session, sim) = await s.OpenAsync(readOnly: false, ct);
        await using var session_ = session;
        await using var sim_ = sim;
        if (s.Simulate)
        {
            // Show what a subscriber on the virtual network receives.
            session.Subscribe(s.Key, ZenohSettings.PrintSample);
        }

        for (var i = 0; i < Math.Max(1, s.Count); i++)
        {
            if (i > 0) await Task.Delay(s.Interval, ct);
            if (s.Delete) await session.DeleteAsync(s.Key, ct);
            else await session.PutAsync(s.Key, payload, encoding, ct);
            Ui.Success($"{(s.Delete ? "Deleted" : "Put")} [bold]{Markup.Escape(s.Key)}[/]" + (s.Delete ? "" : $" ({payload.Length} B)"));
        }

        // Give a loopback subscriber (and the network) a moment to deliver before the session closes.
        await Task.Delay(s.Simulate ? 200 : 100, ct);
        return 0;
    }
}
