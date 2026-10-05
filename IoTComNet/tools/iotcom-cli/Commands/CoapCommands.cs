using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Text;
using IoTCom.Net.Protocols.Coap;
using IoTCom.Net.Transports;
using Spectre.Console;
using Spectre.Console.Cli;

namespace IoTCom.Net.Cli.Commands;

internal class CoapSettings : CommandSettings
{
    [CommandArgument(0, "<uri>"), Description("coap://host[:port]/path[?query]")]
    public string Uri { get; init; } = "";

    [CommandOption("--non"), Description("Send non-confirmable requests.")]
    public bool NonConfirmable { get; init; }

    [CommandOption("--accept"), Description("Accept content format: text, json, link, senml, senml-cbor or a number.")]
    public string? Accept { get; init; }

    [CommandOption("--frames"), Description("Print every datagram as a frame lane.")]
    public bool Frames { get; init; }

    public (string Host, int Port, string Path) Parse()
    {
        if (!System.Uri.TryCreate(Uri, UriKind.Absolute, out var u) || u.Scheme is not ("coap" or "coap+udp"))
            throw new ArgumentException("Expected coap://host[:port]/path, e.g. coap://127.0.0.1/sensors/temperature");
        return (u.Host.Trim('[', ']'), u.IsDefaultPort || u.Port < 0 ? 5683 : u.Port, u.PathAndQuery);
    }

    public ushort? AcceptFormat => Accept?.ToLowerInvariant() switch
    {
        null => null,
        "text" => CoapContentFormat.TextPlain,
        "json" => CoapContentFormat.Json,
        "link" => CoapContentFormat.LinkFormat,
        "senml" => CoapContentFormat.SenMLJson,
        "senml-cbor" => CoapContentFormat.SenMLCbor,
        var n => ushort.Parse(n, NumberStyles.None, CultureInfo.InvariantCulture),
    };

    public async Task<CoapClient> ConnectAsync(bool allowWrite, CancellationToken ct)
    {
        var (host, port, _) = Parse();
        var client = CoapClient.Create(o =>
        {
            o.UseServer(host, port);
            o.Confirmable = !NonConfirmable;
            o.ReadOnly = !allowWrite;
        });
        if (Frames) client.AddTap(new CoapFramePrinter());
        await client.ConnectAsync(ct);
        return client;
    }

    public static void Print(CoapResponse r)
    {
        var colour = r.IsSuccess ? Ui.LampGreen : Ui.Fault;
        AnsiConsole.MarkupLine($"[{Ui.Hex(colour)}]{Markup.Escape(r.Code.ToString())}[/] [{Ui.Hex(Ui.Muted)}]{Markup.Escape(r.ContentFormat is { } cf ? CoapContentFormat.Name(cf) : "")} {r.Payload.Length} B{(r.ObserveSequence is { } s ? $" · obs {s}" : "")}[/]");
        if (r.Payload.IsEmpty) return;
        var text = r.ContentFormat is CoapContentFormat.Cbor or CoapContentFormat.SenMLCbor or CoapContentFormat.OctetStream
            ? IoTCom.Net.HexDump.Format(r.Payload.Span)
            : r.PayloadText;
        AnsiConsole.WriteLine(text);
    }
}

/// <summary>Prints datagrams with the CoAP frame lane.</summary>
internal sealed class CoapFramePrinter : ITrafficTap
{
    public void OnFrame(in TrafficFrame frame)
    {
        AnsiConsole.MarkupLine($"[{Ui.Hex(frame.Direction == FrameDirection.Outbound ? Ui.Amber : Ui.CableBlue)}]{(frame.Direction == FrameDirection.Outbound ? "TX" : "RX")}[/] {Markup.Escape(frame.Summary ?? "")}");
        AnsiConsole.Write(Ui.FrameLane(frame.Data.Span, CoapAnatomy.Describe(frame.Data.Span)));
    }
}

internal sealed class CoapGetCommand : AsyncCommand<CoapSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, CoapSettings s, CancellationToken ct)
    {
        await using var client = await s.ConnectAsync(false, ct);
        var r = await client.GetAsync(s.Parse().Path, s.AcceptFormat, ct);
        CoapSettings.Print(r);
        return r.IsSuccess ? 0 : 2;
    }
}

internal sealed class CoapWriteCommand : AsyncCommand<CoapWriteCommand.Settings>
{
    public sealed class Settings : CoapSettings
    {
        [CommandArgument(1, "[payload]"), Description("Text payload (or @file for a binary file).")]
        public string? Payload { get; init; }

        [CommandOption("-m|--method"), Description("PUT (default), POST or DELETE.")]
        public string Method { get; init; } = "PUT";

        [CommandOption("--format"), Description("Content format of the payload: text (default), json, octet or a number.")]
        public string Format { get; init; } = "text";

        [CommandOption("--allow-write"), Description("Required: confirms you intend to change the device.")]
        public bool AllowWrite { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        if (!s.AllowWrite)
        {
            Ui.Warn($"{Markup.Escape(s.Method.ToUpperInvariant())} changes device state. Re-run with [bold]--allow-write[/].");
            return 2;
        }
        await using var client = await s.ConnectAsync(true, ct);
        var path = s.Parse().Path;
        byte[] body = s.Payload is { } p && p.StartsWith('@') ? await File.ReadAllBytesAsync(p[1..], ct) : Encoding.UTF8.GetBytes(s.Payload ?? "");
        var format = s.Format.ToLowerInvariant() switch
        {
            "text" => CoapContentFormat.TextPlain,
            "json" => CoapContentFormat.Json,
            "octet" => CoapContentFormat.OctetStream,
            var n => ushort.Parse(n, NumberStyles.None, CultureInfo.InvariantCulture),
        };
        var r = s.Method.ToUpperInvariant() switch
        {
            "PUT" => await client.PutAsync(path, body, format, ct),
            "POST" => await client.PostAsync(path, body, format, ct),
            "DELETE" => await client.DeleteAsync(path, ct),
            _ => throw new ArgumentException("Method must be PUT, POST or DELETE."),
        };
        CoapSettings.Print(r);
        return r.IsSuccess ? 0 : 2;
    }
}

internal sealed class CoapObserveCommand : AsyncCommand<CoapSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, CoapSettings s, CancellationToken ct)
    {
        await using var client = await s.ConnectAsync(false, ct);
        Ui.Success($"Observing {Markup.Escape(s.Uri)}. Ctrl+C to stop (the server is told to stop notifying).");
        try
        {
            await foreach (var n in client.ObserveAsync(s.Parse().Path, s.AcceptFormat, ct))
            {
                AnsiConsole.Markup($"[{Ui.Hex(Ui.Muted)}]{DateTime.Now:HH:mm:ss}[/] ");
                CoapSettings.Print(n);
            }
        }
        catch (OperationCanceledException) { }
        return 0;
    }
}

internal sealed class CoapDiscoverCommand : AsyncCommand<CoapDiscoverCommand.Settings>
{
    public sealed class Settings : CoapSettings
    {
        [CommandOption("-q|--query"), Description("Filter, e.g. rt=temperature* or if=sensor.")]
        public string? Query { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        await using var client = await s.ConnectAsync(false, ct);
        var links = await client.DiscoverAsync(s.Query, ct);
        var table = new Table().Border(TableBorder.Rounded).AddColumn("Resource").AddColumn("rt").AddColumn("Title").AddColumn("ct").AddColumn("obs");
        foreach (var l in links)
            table.AddRow($"[{Ui.Hex(Ui.CableBlue)}]{Markup.Escape(l.Path)}[/]", Markup.Escape(l.ResourceType ?? ""), Markup.Escape(l.Title ?? ""),
                l.ContentFormat is { } cf ? Markup.Escape(CoapContentFormat.Name(cf)) : "", l.Observable ? $"[{Ui.Hex(Ui.LampGreen)}]●[/]" : "");
        AnsiConsole.Write(table);
        return 0;
    }
}

internal sealed class CoapPingCommand : AsyncCommand<CoapSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, CoapSettings s, CancellationToken ct)
    {
        await using var client = await s.ConnectAsync(false, ct);
        var rtt = await client.PingAsync(ct);
        Ui.Success($"pong from {Markup.Escape(client.Server?.ToString() ?? "")} in {rtt.TotalMilliseconds:0.0} ms");
        return 0;
    }
}

internal sealed class CoapServeCommand : AsyncCommand<CoapServeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-p|--port"), Description("UDP port (default 5683).")]
        public int Port { get; init; } = 5683;

        [CommandOption("--frames"), Description("Print every datagram as a frame lane.")]
        public bool Frames { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        await using var server = CoapServer.Create(o => o.UseUdp(s.Port));
        if (s.Frames) server.AddTap(new CoapFramePrinter());
        await using var device = new CoapDeviceSimulator(server);
        await server.StartAsync(ct);
        device.Start();
        Ui.Success($"Greenhouse node (simulated) on coap://0.0.0.0:{s.Port} — {server.Resources.Count} resources. Ctrl+C to stop.");
        Ui.Warn($"Try: iotcom coap discover coap://127.0.0.1:{s.Port}/   ·   iotcom coap observe coap://127.0.0.1:{s.Port}/sensors/temperature");
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException) { }
        Ui.Warn($"Stopped after {server.RequestCount} requests ({server.Statistics.RetransmissionCount} retransmissions, {server.Statistics.DuplicateCount} duplicates).");
        return 0;
    }
}
