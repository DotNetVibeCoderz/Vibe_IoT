using System.ComponentModel;
using System.Globalization;
using System.Net;
using IoTCom.Net.Protocols.Ntp;
using IoTCom.Net.Transports;
using Spectre.Console;
using Spectre.Console.Cli;

namespace IoTCom.Net.Cli.Commands;

internal sealed class NtpQueryCommand : AsyncCommand<NtpQueryCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[servers]"), Description("Servers to query (default pool.ntp.org).")]
        public string[] Servers { get; init; } = [];

        [CommandOption("-p|--port"), Description("UDP port (default 123).")]
        public int Port { get; init; } = NtpPacket.DefaultPort;

        [CommandOption("--sim"), Description("Query three in-process servers instead (one of them 30 s wrong) over a 15 ms network.")]
        public bool Sim { get; init; }

        [CommandOption("--frames"), Description("Show the frame lane of each answer.")]
        public bool Frames { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        var servers = new List<NtpServer>();
        SntpClient client;
        if (s.Sim)
        {
            var net = new InMemoryDatagramNetwork { Latency = TimeSpan.FromMilliseconds(15), Jitter = TimeSpan.FromMilliseconds(3) };
            var offsets = new[] { 1.234, 1.236, 31.2 };
            for (var i = 0; i < offsets.Length; i++)
            {
                var skew = offsets[i];
                var server = NtpServer.Create(o => { o.UseInMemory(net, new IPEndPoint(IPAddress.Parse($"10.0.0.{i + 1}"), 123)); o.Clock = () => DateTime.UtcNow.AddSeconds(skew); });
                await server.StartAsync(ct);
                servers.Add(server);
            }

            client = SntpClient.Create(o =>
            {
                o.UseInMemory(net);
                for (var i = 1; i <= offsets.Length; i++) o.UseServer(new IPEndPoint(IPAddress.Parse($"10.0.0.{i}"), 123));
                o.MinimumPollInterval = TimeSpan.Zero;
            });
        }
        else
        {
            client = SntpClient.Create(o => { foreach (var h in s.Servers.Length > 0 ? s.Servers : ["pool.ntp.org"]) o.UseServer(h, s.Port); });
        }

        await using (client)
        {
            if (s.Frames) client.AddTap(new FrameLaneTap());
            var estimate = await client.SynchronizeAsync(ct);
            var table = new Table().Border(TableBorder.Rounded).AddColumns("Server", "Stratum", "Reference", "Offset", "Delay");
            foreach (var r in estimate.Accepted)
                table.AddRow(Markup.Escape(r.Server.ToString()!), r.Stratum.ToString(CultureInfo.InvariantCulture), Markup.Escape(r.Response.ReferenceIdText),
                    $"{r.Offset.TotalMilliseconds:+0.000;-0.000} ms", $"{r.RoundTripDelay.TotalMilliseconds:0.000} ms");
            foreach (var (server, reason) in estimate.Failed)
                table.AddRow(Markup.Escape(server.ToString()!), "–", $"[{Ui.Hex(Ui.Fault)}]{Markup.Escape(reason)}[/]", "", "");
            AnsiConsole.Write(table);
            Ui.Success($"This computer's clock is {Math.Abs(estimate.Offset.TotalMilliseconds):0.000} ms {(estimate.Offset >= TimeSpan.Zero ? "behind" : "ahead of")} the median of {estimate.Accepted.Count} server(s). The system clock was not changed.");
        }

        foreach (var server in servers) await server.DisposeAsync();
        return 0;
    }

    private sealed class FrameLaneTap : ITrafficTap
    {
        public void OnFrame(in TrafficFrame frame)
        {
            if (frame.Direction != FrameDirection.Inbound) return;
            var data = frame.Data.ToArray();
            AnsiConsole.Write(Ui.FrameLane(data, NtpPacket.Describe(data)));
        }
    }
}

internal sealed class NtpServeCommand : AsyncCommand<NtpServeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-p|--port"), Description("UDP port (default 123; ports below 1024 may need admin rights).")]
        public int Port { get; init; } = NtpPacket.DefaultPort;

        [CommandOption("--stratum"), Description("Stratum to advertise (default 1).")]
        public byte Stratum { get; init; } = 1;

        [CommandOption("--ref"), Description("Reference identifier (default GPS; LOCL for an undisciplined local clock).")]
        public string Reference { get; init; } = "GPS";

        [CommandOption("--rate-limit"), Description("Answer each client address at most once per this many seconds (kiss-o'-death RATE otherwise).")]
        public double RateLimit { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        await using var server = NtpServer.Create(o =>
        {
            o.UseUdp(s.Port);
            o.WithReference(s.Reference, s.Stratum);
            o.RateLimit = TimeSpan.FromSeconds(s.RateLimit);
        });
        server.Served += (client, kiss) => (kiss ? (Action<string>)Ui.Warn : Ui.Success)($"{Markup.Escape(client.ToString()!)} {(kiss ? "rate-limited (RATE)" : "answered")}");
        await server.StartAsync(ct);
        Ui.Success($"NTP server on UDP {s.Port}, stratum {s.Stratum}, reference {Markup.Escape(s.Reference)}, telling this computer's clock. Ctrl+C to stop.");
        try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
        return 0;
    }
}
