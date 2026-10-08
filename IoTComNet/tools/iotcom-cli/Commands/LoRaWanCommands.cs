using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Text.Json;
using IoTCom.Net.Protocols.LoRaWan;
using IoTCom.Net.Transports;
using Spectre.Console;
using Spectre.Console.Cli;

namespace IoTCom.Net.Cli.Commands;

/// <summary>Prints LoRaWAN PHYPayloads with the frame lane.</summary>
internal sealed class LoRaWanFramePrinter : ITrafficTap
{
    public void OnFrame(in TrafficFrame frame)
    {
        AnsiConsole.MarkupLine($"[{Ui.Hex(frame.Direction == FrameDirection.Outbound ? Ui.Amber : Ui.CableBlue)}]{(frame.Direction == FrameDirection.Outbound ? "TX" : "RX")}[/] {Markup.Escape(frame.Summary ?? "")}");
        AnsiConsole.Write(Ui.FrameLane(frame.Data.Span, LoRaWanAnatomy.Describe(frame.Data.Span)));
    }
}

/// <summary>Device list files: <c>[{"name": "...", "devEui": "...", "appKey": "..."}]</c>.</summary>
internal static class LoRaWanDeviceFile
{
    public static List<(string Name, Eui64 DevEui, byte[] AppKey)> Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return [.. doc.RootElement.EnumerateArray().Select(e => (
            e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
            Eui64.Parse(e.GetProperty("devEui").GetString()!),
            LoRaWanKeys.Parse(e.GetProperty("appKey").GetString()!)))];
    }

    public static void Save(string path, IEnumerable<LoRaWanSimulatedDeviceOptions> devices)
    {
        using var stream = File.Create(path);
        using var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        w.WriteStartArray();
        foreach (var d in devices)
        {
            w.WriteStartObject();
            w.WriteString("name", d.Name);
            w.WriteString("devEui", d.DevEui.ToString());
            w.WriteString("appKey", Convert.ToHexString(d.AppKey));
            w.WriteString("sensor", d.Sensor.ToString());
            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    /// <summary>The demo deployment, with the AppKeys from <paramref name="path"/> when it exists (created otherwise).</summary>
    public static LoRaWanSimulatorOptions DemoOptions(EndPoint server, LoRaRegion region, string? path)
    {
        var options = LoRaWanSimulatorOptions.Demo(server, region);
        if (path is null) return options;
        if (File.Exists(path))
        {
            var saved = Load(path).ToDictionary(d => d.DevEui);
            for (var i = 0; i < options.Devices.Count; i++)
                if (saved.TryGetValue(options.Devices[i].DevEui, out var s)) options.Devices[i] = options.Devices[i] with { AppKey = s.AppKey };
        }
        else
        {
            Save(path, options.Devices);
            Ui.Warn($"Wrote the simulated devices (DevEUI and AppKey) to {Markup.Escape(path)}; register them in your network server.");
        }

        return options;
    }
}

internal static class LoRaWanUi
{
    public static void PrintUplink(LoRaWanUplink up)
    {
        var gw = string.Join(" ", up.Gateways.Select(g => $"{g.GatewayEui.ToString()[^4..]}:{g.Rssi:0}/{g.Snr:0.0}"));
        var mac = up.MacCommands.Count == 0 ? "" : $" [{Ui.Hex(Ui.Muted)}]{Markup.Escape(string.Join(", ", up.MacCommands))}[/]";
        AnsiConsole.MarkupLine(
            $"[{Ui.Hex(Ui.Muted)}]{up.Time.ToLocalTime():HH:mm:ss.fff}[/] [{Ui.Hex(Ui.CableBlue)}]▲[/] [bold]{Markup.Escape(up.Device.Name),-11}[/] " +
            $"FCnt {up.FCnt,-5} {up.DataRate,-9} {up.Frequency:0.0##} MHz {up.Airtime.TotalMilliseconds,6:0.0} ms  [{Ui.Hex(Ui.Muted)}]{Markup.Escape(gw)}[/]" +
            $"{(up.Confirmed ? $" [{Ui.Hex(Ui.Amber)}]CONF[/]" : "")}  {Markup.Escape(LoRaWanSimulator.DescribePayload(up.FPort, up.Payload))}{mac}");
    }

    public static void PrintRadio(LoRaWanRadioEvent e)
    {
        var arrow = e.Uplink ? $"[{Ui.Hex(Ui.CableBlue)}]▲[/]" : $"[{Ui.Hex(Ui.Amber)}]▼[/]";
        var heard = e.Uplink
            ? e.Receptions.Count == 0 ? $"[{Ui.Hex(Ui.Fault)}]lost[/]" : $"[{Ui.Hex(Ui.Muted)}]{Markup.Escape(string.Join(" ", e.Receptions.Select(r => $"{r.Gateway}:{r.Rssi:0}/{r.Snr:0.0}")))}[/]"
            : $"[{Ui.Hex(Ui.Muted)}]via {Markup.Escape(e.Gateway ?? "")}[/]";
        AnsiConsole.MarkupLine($"[{Ui.Hex(Ui.Muted)}]{e.Time:HH:mm:ss.fff}[/] {arrow} [bold]{Markup.Escape(e.Device ?? "?"),-11}[/] {e.DataRate,-9} {e.Airtime.TotalMilliseconds,6:0.0} ms {heard}  {Markup.Escape(e.Summary)}");
    }

    public static LoRaRegion Region(string name)
    {
        try
        {
            return LoRaRegion.Get(name);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
    }
}

internal sealed class LoRaWanDecodeCommand : Command<LoRaWanDecodeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<frame>"), Description("PHYPayload as hex or base64 (as in a Semtech rxpk).")]
        public string Frame { get; init; } = "";

        [CommandOption("--appkey"), Description("AppKey: verifies a Join-Request MIC or decrypts a Join-Accept.")]
        public string? AppKey { get; init; }

        [CommandOption("--nwkskey"), Description("NwkSKey: verifies a data frame MIC (and decrypts FPort 0).")]
        public string? NwkSKey { get; init; }

        [CommandOption("--appskey"), Description("AppSKey: decrypts the application payload.")]
        public string? AppSKey { get; init; }

        [CommandOption("--fcnt"), Description("Full 32-bit frame counter when it is above 65535.")]
        public uint? FCnt { get; init; }
    }

    public override int Execute(CommandContext context, Settings s, CancellationToken ct)
    {
        byte[] phy;
        var text = s.Frame.Replace(" ", "", StringComparison.Ordinal);
        try
        {
            phy = text.All(Uri.IsHexDigit) && text.Length % 2 == 0 ? Convert.FromHexString(text) : Convert.FromBase64String(text);
        }
        catch (FormatException)
        {
            Ui.Error("The frame is neither hex nor base64.");
            return 2;
        }

        AnsiConsole.Write(Ui.FrameLane(phy, LoRaWanAnatomy.Describe(phy)));
        if (!LoRaWanPacket.TryDecode(phy, out var p, out var error))
        {
            Ui.Error(Markup.Escape(error!));
            return 1;
        }

        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(p!.ToString())}[/]");
        if (p.FOpts.Length > 0) AnsiConsole.MarkupLine($"FOpts: {Markup.Escape(LoRaWanMacCommands.Summarize(p.FOpts.Span, p.IsUplink))}");
        var fcnt = s.FCnt ?? p.FCnt;
        if (p.MType == LoRaWanMType.JoinRequest && s.AppKey is { } jk)
            Verdict(p.VerifyMic(LoRaWanKeys.Parse(jk)), "MIC (AppKey)");
        if (p.MType == LoRaWanMType.JoinAccept && s.AppKey is { } ak)
        {
            if (LoRaWanJoinAccept.TryDecrypt(phy, LoRaWanKeys.Parse(ak), out var accept, out var e))
                Ui.Success(Markup.Escape($"Join-Accept: DevAddr {accept!.DevAddr}, NetID {accept.NetId:X6}, JoinNonce {accept.JoinNonce:X6}, RX1 delay {accept.Rx1Delay.TotalSeconds:0} s, RX2 DR{accept.Rx2DataRate}{(accept.CfList is null ? "" : ", CFList")}"));
            else Ui.Error(Markup.Escape(e!));
        }

        if (p.IsData && s.NwkSKey is { } nk)
        {
            var keys = new LoRaWanSessionKeys(LoRaWanKeys.Parse(nk), s.AppSKey is { } a ? LoRaWanKeys.Parse(a) : new byte[16]);
            Verdict(p.VerifyMic(keys.NwkSKey, fcnt), $"MIC (NwkSKey, FCnt {fcnt})");
            if (p.FPort == 0) AnsiConsole.MarkupLine($"MAC commands: {Markup.Escape(LoRaWanMacCommands.Summarize(p.DecryptPayload(keys, fcnt), p.IsUplink))}");
            else if (p.FPort is { } port && s.AppSKey is not null)
            {
                var plain = p.DecryptPayload(keys, fcnt);
                AnsiConsole.MarkupLine($"Payload (FPort {port}): [bold]{Convert.ToHexString(plain)}[/]");
                var described = LoRaWanSimulator.DescribePayload(port, plain);
                if (described != Convert.ToHexString(plain)) AnsiConsole.MarkupLine($"  {Markup.Escape(described)}");
            }
        }

        if (p.IsData && s.NwkSKey is null && s.AppSKey is null)
            Ui.Warn("Pass --nwkskey and --appskey to verify the MIC and decrypt the payload.");
        return 0;

        static void Verdict(bool ok, string what)
        {
            if (ok) Ui.Success($"{Markup.Escape(what)}: valid");
            else Ui.Error($"{Markup.Escape(what)}: mismatch");
        }
    }
}

internal sealed class LoRaWanServerCommand : AsyncCommand<LoRaWanServerCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-p|--port"), Description("Semtech UDP port (default 1700).")]
        public int Port { get; init; } = 1700;

        [CommandOption("--region"), Description("EU868, US915 or AS923 (AS923-2, Indonesia). Default AS923.")]
        public string Region { get; init; } = "AS923";

        [CommandOption("--devices"), Description("JSON device list [{name, devEui, appKey}] (OTAA).")]
        public string? Devices { get; init; }

        [CommandOption("--sim"), Description("Also run the simulated gateways and devices in this process.")]
        public bool Simulate { get; init; }

        [CommandOption("--frames"), Description("Print every LoRaWAN frame as a frame lane.")]
        public bool Frames { get; init; }

        [CommandOption("-w|--pcap"), Description("Write every LoRaWAN frame to this pcapng file (LoRaTap; Wireshark decodes LoRaWAN).")]
        public string? Pcap { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        var region = LoRaWanUi.Region(s.Region);
        await using var server = LoRaWanNetworkServer.Create(o =>
        {
            o.Port = s.Port;
            o.Region = region;
        });
        if (s.Frames) server.AddTap(new LoRaWanFramePrinter());
        using var pcap = s.Pcap is null ? null : PcapngTap.Create(s.Pcap);
        if (pcap is not null) server.AddTap(pcap);
        if (s.Devices is not null)
            foreach (var (name, devEui, appKey) in LoRaWanDeviceFile.Load(s.Devices))
                server.AddDevice(LoRaWanDeviceRegistration.Otaa(devEui, appKey, name.Length == 0 ? null : name));

        server.UplinkReceived += (_, up) => LoRaWanUi.PrintUplink(up);
        server.DeviceJoined += (_, d) => Ui.Success(Markup.Escape($"{d.Name} joined: DevAddr {d.DevAddr} (join #{d.JoinCount})"));
        server.FrameRejected += (_, reason) => Ui.Warn(Markup.Escape(reason));
        server.GatewayUpdated += (_, g) =>
        {
            if (g.Status is null) Ui.Success(Markup.Escape($"Gateway {g.Eui} connected from {g.PullEndPoint}"));
        };
        await server.StartAsync(ct);
        Ui.Success($"LoRaWAN network server ({region.Name}) on udp://0.0.0.0:{s.Port} (Semtech packet forwarder protocol), {server.Devices.Count} device(s). Ctrl+C to stop.");

        LoRaWanSimulator? sim = null;
        if (s.Simulate)
        {
            sim = new LoRaWanSimulator(LoRaWanSimulatorOptions.Demo(new IPEndPoint(IPAddress.Loopback, s.Port), region));
            foreach (var r in sim.Registrations) server.AddDevice(r);
            await sim.StartAsync(ct);
            Ui.Warn($"Simulating {sim.Gateways.Count} gateways and {sim.Devices.Count} devices; joins take 5 s (JOIN_ACCEPT_DELAY1).");
        }
        else
        {
            Ui.Warn($"Point a gateway's packet forwarder at this host, port {s.Port} (global_conf.json: server_address, serv_port_up/down).");
        }

        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException) { }
        if (sim is not null) await sim.DisposeAsync();
        Ui.Warn(Markup.Escape($"Stopped: {server.Devices.Sum(d => d.UplinkCount)} uplinks from {server.Devices.Count(d => d.IsActivated)} active device(s) via {server.Gateways.Count} gateway(s)."));
        return 0;
    }
}

internal sealed class LoRaWanSimulateCommand : AsyncCommand<LoRaWanSimulateCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--server"), Description("Network server host:port (Semtech UDP), e.g. a ChirpStack gateway bridge. Default 127.0.0.1:1700.")]
        public string Server { get; init; } = "127.0.0.1:1700";

        [CommandOption("--region"), Description("EU868, US915 or AS923 (default AS923).")]
        public string Region { get; init; } = "AS923";

        [CommandOption("--devices"), Description("Device key file: read when it exists, written with fresh AppKeys otherwise.")]
        public string Devices { get; init; } = "lorawan-sim-devices.json";
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        var colon = s.Server.LastIndexOf(':');
        var host = colon > 0 ? s.Server[..colon] : s.Server;
        var port = colon > 0 ? int.Parse(s.Server[(colon + 1)..], CultureInfo.InvariantCulture) : 1700;
        var address = IPAddress.TryParse(host, out var ip) ? ip : (await Dns.GetHostAddressesAsync(host, ct)).First(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
        var options = LoRaWanDeviceFile.DemoOptions(new IPEndPoint(address, port), LoRaWanUi.Region(s.Region), s.Devices);
        await using var sim = new LoRaWanSimulator(options);
        sim.RadioActivity += (_, e) => LoRaWanUi.PrintRadio(e);
        await sim.StartAsync(ct);
        var table = new Table().Border(TableBorder.Rounded).AddColumns("Gateway", "EUI", "Position (km)");
        foreach (var (g, _) in sim.Gateways) table.AddRow(g.Name, g.Eui.ToString(), $"{g.X:0.0}, {g.Y:0.0}");
        AnsiConsole.Write(table);
        Ui.Success(Markup.Escape($"{sim.Devices.Count} devices sending to {s.Server} ({options.Region.Name}); keys in {s.Devices}. Ctrl+C to stop."));
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException) { }
        Ui.Warn(Markup.Escape($"Stopped: {sim.Devices.Sum(d => d.Uplinks)} uplinks, {sim.Devices.Sum(d => d.Lost)} lost, {sim.Devices.Sum(d => d.Downlinks)} downlinks."));
        return 0;
    }
}

internal sealed class LoRaWanAirtimeCommand : Command<LoRaWanAirtimeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<bytes>"), Description("Application payload size in bytes (13 bytes of LoRaWAN overhead are added).")]
        public int Bytes { get; init; }

        [CommandOption("--region"), Description("Region for the data rate table (default EU868).")]
        public string Region { get; init; } = "EU868";
    }

    public override int Execute(CommandContext context, Settings s, CancellationToken ct)
    {
        var region = LoRaWanUi.Region(s.Region);
        var phy = s.Bytes + 13;
        var table = new Table().Border(TableBorder.Rounded).Title($"Time on air · {s.Bytes} B payload ({phy} B PHYPayload) · {region.Name}")
            .AddColumns("DR", "Data rate", "Max payload", "Time on air", "1 % duty cycle: wait");
        for (var dr = 0; dr < region.DataRates.Count; dr++)
        {
            var rate = region.DataRates[dr];
            if (rate.SpreadingFactor == 0) continue;
            var t = LoRaAirtime.Compute(phy, rate.SpreadingFactor, rate.BandwidthKHz);
            var fits = s.Bytes <= rate.MaxPayload;
            table.AddRow($"DR{dr}", rate.Datr, fits ? rate.MaxPayload.ToString(CultureInfo.InvariantCulture) : $"[{Ui.Hex(Ui.Fault)}]{rate.MaxPayload} (too big)[/]",
                $"{t.TotalMilliseconds:0.0} ms", $"{t.TotalSeconds * 99:0.0} s");
        }

        AnsiConsole.Write(table);
        return 0;
    }
}
