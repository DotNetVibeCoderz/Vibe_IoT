using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Text;
using IoTCom.Net.Protocols.Coap;
using IoTCom.Net.Transport.Dtls;
using IoTCom.Net.Transports;
using Spectre.Console;
using Spectre.Console.Cli;

namespace IoTCom.Net.Cli.Commands;

internal class CoapSettings : CommandSettings
{
    [CommandArgument(0, "<uri>"), Description("coap://host[:port]/path[?query], or coaps:// for DTLS (port 5684)")]
    public string Uri { get; init; } = "";

    [CommandOption("--non"), Description("Send non-confirmable requests.")]
    public bool NonConfirmable { get; init; }

    [CommandOption("--accept"), Description("Accept content format: text, json, link, senml, senml-cbor or a number.")]
    public string? Accept { get; init; }

    [CommandOption("--frames"), Description("Print every datagram as a frame lane.")]
    public bool Frames { get; init; }

    [CommandOption("--psk"), Description("coaps: pre-shared key as identity:hex (prefer the IOTCOM_DTLS_PSK environment variable; keys in shell history leak).")]
    public string? Psk { get; init; }

    [CommandOption("--pin"), Description("coaps: accept the server certificate with this SHA-256 fingerprint (hex).")]
    public string? Pin { get; init; }

    public bool Secure => System.Uri.TryCreate(Uri, UriKind.Absolute, out var u) && u.Scheme == "coaps";

    public (string Host, int Port, string Path) Parse()
    {
        if (!System.Uri.TryCreate(Uri, UriKind.Absolute, out var u) || u.Scheme is not ("coap" or "coap+udp" or "coaps"))
            throw new ArgumentException("Expected coap://host[:port]/path or coaps://host[:port]/path, e.g. coap://127.0.0.1/sensors/temperature");
        return (u.Host.Trim('[', ']'), u.IsDefaultPort || u.Port < 0 ? (u.Scheme == "coaps" ? 5684 : 5683) : u.Port, u.PathAndQuery);
    }

    /// <summary>DTLS client options from --psk / IOTCOM_DTLS_PSK / --pin.</summary>
    public DtlsClientOptions DtlsOptions()
    {
        var options = new DtlsClientOptions();
        var psk = Psk ?? Environment.GetEnvironmentVariable("IOTCOM_DTLS_PSK");
        if (!string.IsNullOrEmpty(psk)) options.Psk = DtlsCli.ParsePsk(psk);
        if (!string.IsNullOrEmpty(Pin)) options.PinnedFingerprints.Add(Pin.Replace(":", "", StringComparison.Ordinal));
        if (options.Psk is null && options.PinnedFingerprints.Count == 0)
            throw new ArgumentException("coaps:// needs --psk identity:hex (or IOTCOM_DTLS_PSK) or --pin <sha256>.");
        return options;
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
            if (Secure)
            {
                var server = DtlsCli.Resolve(host, port);
                o.UseDtlsClient(() => new UdpDatagramTransport(new IPEndPoint(server.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0)), server, DtlsOptions());
                o.UseServer(server);
            }
            else o.UseServer(host, port);
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
        [CommandOption("-p|--port"), Description("UDP port (default 5683, or 5684 with DTLS).")]
        public int? Port { get; init; }

        [CommandOption("--frames"), Description("Print every datagram as a frame lane.")]
        public bool Frames { get; init; }

        [CommandOption("--psk"), Description("Serve coaps:// with this pre-shared key (identity:hex; or set IOTCOM_DTLS_PSK).")]
        public string? Psk { get; init; }

        [CommandOption("--cert"), Description("Serve coaps:// with a fresh self-signed ECDSA certificate (its fingerprint is printed for --pin).")]
        public bool Certificate { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        var pskText = s.Psk ?? Environment.GetEnvironmentVariable("IOTCOM_DTLS_PSK");
        DtlsServerOptions? dtls = null;
        if (!string.IsNullOrEmpty(pskText) || s.Certificate)
        {
            dtls = new DtlsServerOptions();
            if (!string.IsNullOrEmpty(pskText)) dtls.UsePsk(DtlsCli.ParsePsk(pskText));
            if (s.Certificate) dtls.Certificate = DtlsCertificates.CreateSelfSigned("iotcom-coap-demo");
        }

        var port = s.Port ?? (dtls is null ? 5683 : 5684);
        await using var server = CoapServer.Create(o =>
        {
            if (dtls is null) o.UseUdp(port);
            else o.UseDtlsServer(() => new UdpDatagramTransport(new IPEndPoint(IPAddress.Any, port)), dtls);
        });
        if (s.Frames) server.AddTap(new CoapFramePrinter());
        await using var device = new CoapDeviceSimulator(server);
        await server.StartAsync(ct);
        device.Start();
        var scheme = dtls is null ? "coap" : "coaps";
        Ui.Success($"Greenhouse node (simulated) on {scheme}://0.0.0.0:{port} — {server.Resources.Count} resources. Ctrl+C to stop.");
        if (dtls?.Certificate is { } cert) Ui.Warn($"Certificate fingerprint (SHA-256): {DtlsCertificates.Fingerprint(cert)}");
        var auth = dtls is null ? "" : dtls.Certificate is { } c2 ? $" --pin {DtlsCertificates.Fingerprint(c2)}" : " --psk <identity:hex>";
        Ui.Warn($"Try: iotcom coap discover {scheme}://127.0.0.1:{port}/{auth}   ·   iotcom coap observe {scheme}://127.0.0.1:{port}/sensors/temperature{auth}");
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException) { }
        Ui.Warn($"Stopped after {server.RequestCount} requests ({server.Statistics.RetransmissionCount} retransmissions, {server.Statistics.DuplicateCount} duplicates).");
        return 0;
    }
}

/// <summary>DTLS helpers shared by the CoAP and LwM2M commands.</summary>
internal static class DtlsCli
{
    /// <summary>Parses identity:hex.</summary>
    public static DtlsPsk ParsePsk(string text)
    {
        var colon = text.LastIndexOf(':');
        if (colon <= 0 || colon == text.Length - 1) throw new ArgumentException("A PSK is identity:hex, e.g. node-01:000102030405060708090A0B0C0D0E0F.");
        return DtlsPsk.FromHex(text[..colon], text[(colon + 1)..]);
    }

    /// <summary>Resolves a host name to an endpoint (DTLS needs a concrete peer address).</summary>
    public static IPEndPoint Resolve(string host, int port)
    {
        if (IPAddress.TryParse(host, out var ip)) return new IPEndPoint(ip, port);
        var addresses = Dns.GetHostAddresses(host);
        var pick = addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? addresses.FirstOrDefault()
            ?? throw new ArgumentException($"Cannot resolve {host}.");
        return new IPEndPoint(pick, port);
    }
}
