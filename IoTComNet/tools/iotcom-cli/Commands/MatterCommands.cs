using System.ComponentModel;
using System.Globalization;
using System.Net;
using IoTCom.Net.Protocols.Matter;
using IoTCom.Net.Protocols.Mdns;
using IoTCom.Net.Transports;
using Spectre.Console;
using Spectre.Console.Cli;

namespace IoTCom.Net.Cli.Commands;

internal static class MatterUi
{
    public static string DefaultFabric => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IoTCom.Net", "matter", "fabric.json");

    public static IPEndPoint Parse(string host)
    {
        var parts = host.Split(':');
        var port = parts.Length > 1 ? int.Parse(parts[^1], CultureInfo.InvariantCulture) : 5540;
        var name = parts.Length > 1 ? string.Join(':', parts[..^1]) : host;
        return new IPEndPoint(IPAddress.TryParse(name, out var ip) ? ip : Dns.GetHostAddresses(name).First(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork), port);
    }

    public static ulong Number(string text) => text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? ulong.Parse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
        : ulong.Parse(text, CultureInfo.InvariantCulture);

    public static AttributePath Path(string text)
    {
        var p = text.Split('/');
        if (p.Length != 3) throw new ArgumentException("A path is endpoint/cluster/attribute, e.g. 1/0x0006/0 (* = any).");
        static ulong? N(string s) => s == "*" ? null : Number(s);
        return new AttributePath((ushort?)N(p[0]), (uint?)N(p[1]), (uint?)N(p[2]));
    }

    public static MatterController Controller(string? fabric, bool writes, bool trustTest)
    {
        var file = fabric ?? DefaultFabric;
        return MatterController.Create(o =>
        {
            o.UseUdp();
            o.FabricFile = file;
            if (writes) o.AllowWrites();
            if (trustTest) o.TrustTestDevices();
        });
    }

    public static string Value(TlvElement? v) => v is null ? "" : Markup.Escape(v.ToString());
}

internal class MatterNodeSettings : CommandSettings
{
    [CommandArgument(0, "<host>"), Description("Device address host[:port] (default port 5540).")]
    public string Host { get; init; } = "";

    [CommandArgument(1, "<node>"), Description("Node id on this controller's fabric (decimal or 0x hex).")]
    public string Node { get; init; } = "";

    [CommandOption("--fabric"), Description("Fabric file (default %APPDATA%/IoTCom.Net/matter/fabric.json; holds private keys).")]
    public string? Fabric { get; init; }
}

internal sealed class MatterCodeCommand : Command<MatterCodeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[code]"), Description("An MT: QR payload or a manual pairing code to decode. Without it, encode from the options.")]
        public string? Code { get; init; }

        [CommandOption("--discriminator"), Description("12-bit discriminator (default 3840).")]
        public ushort Discriminator { get; init; } = 3840;

        [CommandOption("--passcode"), Description("Setup passcode (default 20202021).")]
        public uint Passcode { get; init; } = 20202021;

        [CommandOption("--vid"), Description("Vendor id (default 0xFFF1).")]
        public string Vid { get; init; } = "0xFFF1";

        [CommandOption("--pid"), Description("Product id (default 0x8000).")]
        public string Pid { get; init; } = "0x8000";
    }

    public override int Execute(CommandContext context, Settings s, CancellationToken ct)
    {
        var payload = s.Code is { } code
            ? code.StartsWith("MT:", StringComparison.Ordinal) ? MatterSetupPayload.FromQrCode(code) : MatterSetupPayload.FromManualCode(code)
            : new MatterSetupPayload((ushort)MatterUi.Number(s.Vid), (ushort)MatterUi.Number(s.Pid), CommissioningFlow.Standard, DiscoveryCapabilities.OnNetwork, s.Discriminator, s.Passcode);
        var t = new Table().Border(TableBorder.Rounded).AddColumns("Field", "Value");
        t.AddRow("Vendor / product", $"0x{payload.VendorId:X4} / 0x{payload.ProductId:X4}");
        t.AddRow("Discriminator", $"{payload.Discriminator} (short {payload.ShortDiscriminator})");
        t.AddRow("Passcode", payload.Passcode.ToString(CultureInfo.InvariantCulture));
        t.AddRow("Flow / discovery", $"{payload.Flow} / {payload.Discovery}");
        if (s.Code is null || s.Code.StartsWith("MT:", StringComparison.Ordinal)) t.AddRow("QR payload", payload.ToQrCode());
        t.AddRow("Manual code", payload.ToManualCode());
        AnsiConsole.Write(t);
        return 0;
    }
}

internal sealed class MatterDeviceCommand : AsyncCommand<MatterDeviceCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-p|--port"), Description("UDP port (default 5540).")]
        public int Port { get; init; } = 5540;

        [CommandOption("--bridge"), Description("Run the smart-home bridge (4 lights, 2 sensors, a dimmable lamp) instead of a single light.")]
        public bool Bridge { get; init; }

        [CommandOption("--mdns"), Description("Advertise over DNS-SD (_matterc._udp, then _matter._tcp).")]
        public bool Mdns { get; init; }

        [CommandOption("--passcode"), Description("Setup passcode (default 20202021).")]
        public uint Passcode { get; init; } = 20202021;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        MdnsResponder? mdns = null;
        if (s.Mdns)
        {
            mdns = MdnsResponder.Create();
            await mdns.StartAsync(ct);
        }

        await using var device = MatterDevice.Create(o =>
        {
            o.UseUdp(s.Port);
            o.Passcode = s.Passcode;
            o.ProductName = s.Bridge ? "IoTCom smart home bridge" : "IoTCom light";
            o.Mdns = mdns;
            foreach (var a in Dns.GetHostAddresses(Dns.GetHostName()).Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)) o.AdvertisedAddresses.Add(a);
        });
        MatterBridgeSimulator? sim = null;
        MatterClusters.OnOffCluster? light = null;
        if (s.Bridge) sim = new MatterBridgeSimulator(device);
        else light = device.AddEndpoint(new MatterEndpoint(1, (MatterIds.OnOffLight, 3))).Add(new MatterClusters.OnOffCluster());
        device.Commissioned += f => Ui.Success($"Commissioned: fabric 0x{f.FabricId:X}, node 0x{f.NodeId:X}.");
        if (light is not null) light.Changed += (c, _) => Ui.Warn($"Light is now {(light.On ? "ON" : "off")}.");
        await device.StartAsync(ct);
        var p = device.SetupPayload;
        Ui.Success($"{Markup.Escape(device.ToString())} on UDP {s.Port}{(s.Mdns ? " (DNS-SD)" : "")}. Ctrl+C to stop.");
        Ui.Success($"QR: {p.ToQrCode()} · manual code: {p.ToManualCode()}");
        Ui.Warn("Simulated device with IoTCom's test attestation: commission it with `iotcom matter commission <host> <code> --node 1 --allow-write --trust-test`.");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct);
                sim?.Step(60);
            }
        }
        catch (OperationCanceledException) { }

        if (mdns is not null) await mdns.DisposeAsync();
        return 0;
    }
}

internal sealed class MatterDiscoverCommand : AsyncCommand<MatterDiscoverCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--seconds"), Description("Browse time (default 3).")]
        public double Seconds { get; init; } = 3;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        await using var browser = MdnsBrowser.Create();
        await browser.StartAsync(ct);
        var t = new Table().Border(TableBorder.Rounded).AddColumns("Kind", "Instance", "Address", "Details");
        foreach (var d in await MatterDiscovery.FindCommissionableAsync(browser, TimeSpan.FromSeconds(s.Seconds), ct: ct))
            t.AddRow("commissionable", Markup.Escape(d.Instance), d.Address.ToString(), Markup.Escape($"D={d.Discriminator} VP={d.VendorId:X4}+{d.ProductId:X4} {d.Name}"));
        foreach (var o in await browser.BrowseAsync(MatterDiscovery.OperationalType, TimeSpan.FromSeconds(s.Seconds), ct))
            t.AddRow("operational", Markup.Escape(o.Instance), $"{o.Address}:{o.Port}", "fabric-node");
        AnsiConsole.Write(t);
        return 0;
    }
}

internal sealed class MatterCommissionCommand : AsyncCommand<MatterCommissionCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<host>"), Description("Device address host[:port].")]
        public string Host { get; init; } = "";

        [CommandArgument(1, "<code>"), Description("MT: QR payload or manual pairing code.")]
        public string Code { get; init; } = "";

        [CommandOption("--node"), Description("Node id to give the device (default 1).")]
        public string Node { get; init; } = "1";

        [CommandOption("--fabric"), Description("Fabric file (holds private keys).")]
        public string? Fabric { get; init; }

        [CommandOption("--trust-test"), Description("Trust IoTCom's test attestation (simulated devices).")]
        public bool TrustTest { get; init; }

        [CommandOption("--allow-write"), Description("Required: commissioning adds this fabric to the device.")]
        public bool AllowWrite { get; init; }

        [CommandOption("-y|--yes"), Description("Skip the confirmation.")]
        public bool Yes { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        if (!s.AllowWrite)
        {
            Ui.Error("Commissioning adds a fabric to the device. Add --allow-write.");
            return 2;
        }

        var payload = s.Code.StartsWith("MT:", StringComparison.Ordinal) ? MatterSetupPayload.FromQrCode(s.Code) : MatterSetupPayload.FromManualCode(s.Code);
        var address = MatterUi.Parse(s.Host);
        if (!s.Yes && !AnsiConsole.Confirm($"Commission {address} as node {s.Node}?", true)) return 1;
        await using var controller = MatterUi.Controller(s.Fabric, true, s.TrustTest);
        var node = await controller.CommissionAsync(address, payload, MatterUi.Number(s.Node), ct);
        var name = await node.ReadAttributeAsync(0, MatterIds.BasicInformation, 3, ct);
        Ui.Success($"Commissioned {MatterUi.Value(name)} at {address} as node 0x{node.NodeId:X} on fabric 0x{controller.FabricId:X} (fabric file {Markup.Escape(s.Fabric ?? MatterUi.DefaultFabric)}).");
        return 0;
    }
}

internal sealed class MatterReadCommand : AsyncCommand<MatterReadCommand.Settings>
{
    public sealed class Settings : MatterNodeSettings
    {
        [CommandArgument(2, "<path>"), Description("endpoint/cluster/attribute, * for any (e.g. 1/*/*, 0/0x0028/*).")]
        public string Path { get; init; } = "";
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        await using var controller = MatterUi.Controller(s.Fabric, false, false);
        var node = await controller.ConnectAsync(MatterUi.Parse(s.Host), MatterUi.Number(s.Node), ct);
        var t = new Table().Border(TableBorder.Rounded).AddColumns("Path", "Value");
        foreach (var r in await node.ReadAsync([MatterUi.Path(s.Path)], ct))
            t.AddRow(r.Path.ToString(), r.Value is null ? $"[{Ui.Hex(Ui.Fault)}]{r.Status}[/]" : MatterUi.Value(r.Value));
        AnsiConsole.Write(t);
        return 0;
    }
}

internal sealed class MatterOnOffCommand : AsyncCommand<MatterOnOffCommand.Settings>
{
    public sealed class Settings : MatterNodeSettings
    {
        [CommandArgument(2, "<endpoint>"), Description("Endpoint with an On/Off cluster.")]
        public ushort Endpoint { get; init; }

        [CommandArgument(3, "<on|off|toggle>"), Description("Command.")]
        public string Action { get; init; } = "";

        [CommandOption("--allow-write"), Description("Required: commands change the device.")]
        public bool AllowWrite { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        if (!s.AllowWrite)
        {
            Ui.Error("Commands change the device. Add --allow-write.");
            return 2;
        }

        var command = s.Action.ToLowerInvariant() switch { "off" => 0u, "on" => 1u, "toggle" => 2u, _ => throw new ArgumentException("on, off or toggle.") };
        await using var controller = MatterUi.Controller(s.Fabric, true, false);
        var node = await controller.ConnectAsync(MatterUi.Parse(s.Host), MatterUi.Number(s.Node), ct);
        var (_, status) = await node.InvokeAsync(s.Endpoint, MatterIds.OnOff, command, ct: ct);
        var now = await node.ReadAttributeAsync(s.Endpoint, MatterIds.OnOff, 0, ct);
        Ui.Success($"{s.Action}: {status}; endpoint {s.Endpoint} is now {(now.AsBool() ? "on" : "off")}.");
        return status == MatterStatus.Success ? 0 : 1;
    }
}

internal sealed class MatterSubscribeCommand : AsyncCommand<MatterSubscribeCommand.Settings>
{
    public sealed class Settings : MatterNodeSettings
    {
        [CommandArgument(2, "<path>"), Description("endpoint/cluster/attribute, * for any.")]
        public string Path { get; init; } = "";

        [CommandOption("--max"), Description("Maximum report interval in seconds (default 60).")]
        public ushort Max { get; init; } = 60;

        [CommandOption("--seconds"), Description("Stop after this many seconds (default: until Ctrl+C).")]
        public double? Seconds { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        await using var controller = MatterUi.Controller(s.Fabric, false, false);
        var node = await controller.ConnectAsync(MatterUi.Parse(s.Host), MatterUi.Number(s.Node), ct);
        var sub = await node.SubscribeAsync([MatterUi.Path(s.Path)], 0, s.Max, r =>
            AnsiConsole.MarkupLine($"[{Ui.Hex(Ui.Muted)}]{DateTime.Now:HH:mm:ss}[/] {r.Path} = {MatterUi.Value(r.Value)}"), ct);
        Ui.Success($"Subscription {sub.Id} (max interval {sub.MaxInterval} s). Ctrl+C to stop.");
        try
        {
            await Task.Delay(s.Seconds is { } secs ? TimeSpan.FromSeconds(secs) : Timeout.InfiniteTimeSpan, ct);
        }
        catch (OperationCanceledException) { }

        return 0;
    }
}

internal sealed class MatterDemoCommand : AsyncCommand<MatterDemoCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--allow-write"), Description("Commission and switch lights (a read-only controller can only show the codes).")]
        public bool AllowWrite { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        var net = new InMemoryDatagramNetwork();
        var at = new IPEndPoint(IPAddress.Parse("10.0.0.70"), 5540);
        await using var device = MatterDevice.Create(o => { o.UseInMemory(net, at); o.ProductName = "IoTCom smart home bridge"; });
        var sim = new MatterBridgeSimulator(device);
        await device.StartAsync(ct);
        Ui.Success($"Bridge at {at}: QR {device.SetupPayload.ToQrCode()} · manual {device.SetupPayload.ToManualCode()}");
        if (!s.AllowWrite)
        {
            Ui.Warn("Read-only controller: commissioning adds a fabric to the device. Add --allow-write.");
            return 0;
        }

        await using var controller = MatterController.Create(o => o.UseInMemory(net).AllowWrites().TrustTestDevices());
        var node = await controller.CommissionAsync(at, device.SetupPayload, 0x42, ct);
        Ui.Success($"Commissioned over PASE (SPAKE2+), attestation, CSR, NOC; CASE session to node 0x42 on fabric 0x{controller.FabricId:X}.");
        var t = new Table().Border(TableBorder.Rounded).AddColumns("Endpoint", "Label", "State");
        foreach (var l in sim.Lights)
            t.AddRow(l.Endpoint.ToString(CultureInfo.InvariantCulture), Markup.Escape((await node.ReadAttributeAsync(l.Endpoint, MatterIds.BridgedDeviceBasicInformation, 5, ct)).AsString()),
                (await node.ReadAttributeAsync(l.Endpoint, MatterIds.OnOff, 0, ct)).AsBool() ? "on" : "off");
        foreach (var x in sim.Sensors)
            t.AddRow(x.Endpoint.ToString(CultureInfo.InvariantCulture), Markup.Escape(x.Label), $"{(await node.ReadAttributeAsync(x.Endpoint, MatterIds.TemperatureMeasurement, 0, ct)).AsInt() / 100.0:0.00} °C");
        AnsiConsole.Write(t);
        await node.InvokeAsync(sim.Lights[2].Endpoint, MatterIds.OnOff, 1, ct: ct);
        Ui.Success($"Invoke On on endpoint {sim.Lights[2].Endpoint}: the terrace light is {(sim.Lights[2].OnOff.On ? "on" : "off")}.");
        return 0;
    }
}

internal sealed class MatterDecodeCommand : Command<MatterDecodeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<hex>"), Description("An unsecured Matter message, or a TLV element.")]
        public string Hex { get; init; } = "";
    }

    public override int Execute(CommandContext context, Settings s, CancellationToken ct)
    {
        var bytes = Convert.FromHexString(new string(s.Hex.Where(Uri.IsHexDigit).ToArray()));
        try
        {
            var m = MatterMessage.Decode(bytes);
            AnsiConsole.Write(Ui.FrameLane(bytes, MatterMessage.DescribeFields(bytes)));
            Ui.Success(Markup.Escape(m.Describe()));
            try
            {
                Ui.Success($"payload: {Markup.Escape(Tlv.Decode(m.Payload).ToString())}");
            }
            catch (ProtocolException) { }

            return 0;
        }
        catch (ProtocolException)
        {
            Ui.Success(Markup.Escape(Tlv.Decode(bytes).ToString()));
            return 0;
        }
    }
}
