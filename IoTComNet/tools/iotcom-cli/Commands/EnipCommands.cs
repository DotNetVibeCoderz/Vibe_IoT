using System.ComponentModel;
using System.Globalization;
using System.Net;
using IoTCom.Net.Protocols.EtherNetIp;
using IoTCom.Net.Transports;
using Spectre.Console;
using Spectre.Console.Cli;

namespace IoTCom.Net.Cli.Commands;

/// <summary>Shared options: a controller (--host) or the in-process packaging-line PLC (--sim).</summary>
internal class EnipSettings : CommandSettings
{
    [CommandOption("-h|--host"), Description("Controller or adapter address (host[:port], default port 44818).")]
    public string? Host { get; init; }

    [CommandOption("--slot"), Description("Route through the backplane to this slot (ControlLogix CPU slot, usually 0).")]
    public byte? Slot { get; init; }

    [CommandOption("--sim"), Description("Use an in-process packaging-line PLC.")]
    public bool Sim { get; init; }

    [CommandOption("--frames"), Description("Print every encapsulation packet.")]
    public bool Frames { get; init; }
}

internal static class EnipUi
{
    public static async Task<(EnipClient Client, IAsyncDisposable Owner, EnipPlcSimulator? Plc)> OpenAsync(EnipSettings s, bool writes, CancellationToken ct)
    {
        EnipPlcSimulator? plc = null;
        InMemoryTransportListener? listener = null;
        if (s.Sim)
        {
            listener = new InMemoryTransportListener("plc");
            plc = EnipPlcSimulator.Create(o => o.ListenInMemory(listener));
            await plc.StartAsync(ct);
        }
        else if (s.Host is null)
        {
            throw new InvalidOperationException("Give --host <address> or --sim.");
        }

        var client = EnipClient.Create(o =>
        {
            if (listener is not null) o.UseInMemory(listener);
            else
            {
                var parts = s.Host!.Split(':');
                o.UseTcp(parts[0], parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : EnipPacket.Port);
            }

            if (s.Slot is { } slot) o.UseSlot(slot);
            if (writes) o.AllowWrites();
        });
        if (s.Frames) client.AddTap(new Printer());
        await client.ConnectAsync(ct);
        return (client, new Owner(client, plc), plc);
    }

    private sealed class Owner(EnipClient client, EnipPlcSimulator? plc) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await client.DisposeAsync();
            if (plc is not null) await plc.DisposeAsync();
        }
    }

    private sealed class Printer : ITrafficTap
    {
        public void OnFrame(in TrafficFrame frame) =>
            AnsiConsole.MarkupLine($"[{Ui.Hex(Ui.Muted)}]{frame.Timestamp.ToLocalTime():HH:mm:ss.fff} {(frame.Direction == FrameDirection.Inbound ? "←" : "→")} {Markup.Escape(frame.Summary ?? "")}[/]");
    }

    public static void Identity(EnipIdentity id)
    {
        var t = new Table().Border(TableBorder.Rounded).AddColumns("Attribute", "Value");
        t.AddRow("Product name", Markup.Escape(id.ProductName));
        t.AddRow("Vendor", id.VendorId.ToString(CultureInfo.InvariantCulture));
        t.AddRow("Device type", $"0x{id.DeviceType:X2}");
        t.AddRow("Product code", id.ProductCode.ToString(CultureInfo.InvariantCulture));
        t.AddRow("Revision", $"{id.RevisionMajor}.{id.RevisionMinor:D3}");
        t.AddRow("Serial number", $"{id.SerialNumber:X8}");
        t.AddRow("Status", $"0x{id.Status:X4}");
        if (id.Address is not null) t.AddRow("Address", id.Address.ToString());
        AnsiConsole.Write(t);
    }
}

internal sealed class EnipDiscoverCommand : AsyncCommand<EnipDiscoverCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--broadcast"), Description("Broadcast address (default 255.255.255.255).")]
        public string? Broadcast { get; init; }

        [CommandOption("-t|--timeout"), Description("Seconds to collect answers (default 2).")]
        public int Timeout { get; init; } = 2;

        [CommandOption("--sim"), Description("Ask an in-process PLC.")]
        public bool Sim { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        IReadOnlyList<EnipIdentity> found;
        if (s.Sim)
        {
            var net = new InMemoryDatagramNetwork();
            var address = new IPEndPoint(IPAddress.Parse("10.70.0.2"), EnipPacket.Port);
            await using var plc = EnipPlcSimulator.Create(o => { o.ListenInMemory(new InMemoryTransportListener("plc")); o.UseInMemory(net, address); });
            await plc.StartAsync(ct);
            found = await EnipDiscovery.ListIdentityAsync(net.Bind(), address, TimeSpan.FromMilliseconds(300), ct);
        }
        else
        {
            found = await EnipDiscovery.ListIdentityAsync(null, new IPEndPoint(s.Broadcast is null ? IPAddress.Broadcast : IPAddress.Parse(s.Broadcast), EnipPacket.Port), TimeSpan.FromSeconds(s.Timeout), ct);
        }

        if (found.Count == 0)
        {
            Ui.Warn("No EtherNet/IP device answered ListIdentity on UDP 44818.");
            return 1;
        }

        var t = new Table().Border(TableBorder.Rounded).AddColumns("Address", "Product", "Vendor", "Type", "Revision", "Serial");
        foreach (var id in found)
            t.AddRow(Markup.Escape(id.Address?.ToString() ?? ""), Markup.Escape(id.ProductName), id.VendorId.ToString(CultureInfo.InvariantCulture), $"0x{id.DeviceType:X2}", $"{id.RevisionMajor}.{id.RevisionMinor:D3}", $"{id.SerialNumber:X8}");
        AnsiConsole.Write(t);
        return 0;
    }
}

internal sealed class EnipIdentityCommand : AsyncCommand<EnipSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, EnipSettings s, CancellationToken ct)
    {
        var (client, owner, _) = await EnipUi.OpenAsync(s, false, ct);
        await using (owner) EnipUi.Identity(await client.ReadIdentityAsync(ct));
        return 0;
    }
}

internal sealed class EnipReadCommand : AsyncCommand<EnipReadCommand.Settings>
{
    public sealed class Settings : EnipSettings
    {
        [CommandArgument(0, "<tags>"), Description("One tag, or several separated by commas (read in one Multiple_Service_Packet).")]
        public string Tags { get; init; } = "";

        [CommandOption("-c|--count"), Description("Elements to read (arrays).")]
        public ushort Count { get; init; } = 1;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        var (client, owner, _) = await EnipUi.OpenAsync(s, false, ct);
        await using (owner)
        {
            var tags = s.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tags.Length == 1)
            {
                Ui.Success($"{Markup.Escape(tags[0])} = {Markup.Escape((await client.ReadTagAsync(tags[0], s.Count, ct)).ToString())}");
                return 0;
            }

            var t = new Table().Border(TableBorder.Rounded).AddColumns("Tag", "Value");
            foreach (var (tag, value, error) in await client.ReadTagsAsync(tags, ct))
                t.AddRow(Markup.Escape(tag), Markup.Escape(value?.ToString() ?? error!.Message));
            AnsiConsole.Write(t);
        }

        return 0;
    }
}

internal sealed class EnipWriteCommand : AsyncCommand<EnipWriteCommand.Settings>
{
    public sealed class Settings : EnipSettings
    {
        [CommandArgument(0, "<tag>"), Description("Tag name.")]
        public string Tag { get; init; } = "";

        [CommandArgument(1, "<type>"), Description("BOOL, SINT, INT, DINT, LINT, USINT, UINT, UDINT, REAL or LREAL.")]
        public string Type { get; init; } = "";

        [CommandArgument(2, "<value>"), Description("Value (comma-separated for several elements; true/false for BOOL).")]
        public string Value { get; init; } = "";

        [CommandOption("--allow-write"), Description("Required: tag writes change what the machine does.")]
        public bool AllowWrite { get; init; }

        [CommandOption("-y|--yes"), Description("Skip the confirmation.")]
        public bool Yes { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        if (!s.AllowWrite)
        {
            Ui.Error("Tag writes change what the machine does. Add --allow-write.");
            return 2;
        }

        var type = CipTagValue.ParseType(s.Type);
        var values = s.Value.Split(',').Select(v => v.Trim().ToLowerInvariant() switch { "true" or "on" => 1.0, "false" or "off" => 0.0, var x => double.Parse(x, CultureInfo.InvariantCulture) }).ToArray();
        var value = CipTagValue.Of(type, values);
        var (client, owner, _) = await EnipUi.OpenAsync(s, true, ct);
        await using (owner)
        {
            if (!s.Yes && !s.Sim && !AnsiConsole.Confirm($"Write {Markup.Escape(value.ToString())} to {Markup.Escape(s.Tag)} on {Markup.Escape(s.Host ?? "")}?", false)) return 1;
            await client.WriteTagAsync(s.Tag, value, ct);
            Ui.Success($"{Markup.Escape(s.Tag)} now reads {Markup.Escape((await client.ReadTagAsync(s.Tag, (ushort)values.Length, ct)).ToString())}.");
        }

        return 0;
    }
}

internal sealed class EnipAttributeCommand : AsyncCommand<EnipAttributeCommand.Settings>
{
    public sealed class Settings : EnipSettings
    {
        [CommandArgument(0, "<class>"), Description("Class (decimal or 0x…).")]
        public string Class { get; init; } = "";

        [CommandArgument(1, "<instance>"), Description("Instance.")]
        public uint Instance { get; init; }

        [CommandArgument(2, "[attribute]"), Description("Attribute (omit for Get_Attributes_All).")]
        public ushort? Attribute { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        var cls = s.Class.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? ushort.Parse(s.Class[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture) : ushort.Parse(s.Class, CultureInfo.InvariantCulture);
        var (client, owner, _) = await EnipUi.OpenAsync(s, false, ct);
        await using (owner)
        {
            var data = s.Attribute is { } a ? await client.GetAttributeSingleAsync(cls, s.Instance, a, ct) : await client.GetAttributesAllAsync(cls, s.Instance, ct);
            AnsiConsole.Write(new Panel(Markup.Escape(HexDump.Format(data))).Header($"class 0x{cls:X2} / instance {s.Instance}{(s.Attribute is { } x ? $" / attribute {x}" : "")}"));
        }

        return 0;
    }
}

internal sealed class EnipServeCommand : AsyncCommand<EnipServeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-p|--port"), Description("TCP and UDP port (default 44818).")]
        public int Port { get; init; } = EnipPacket.Port;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        await using var plc = EnipPlcSimulator.Create(o =>
        {
            o.UseTcp(IPAddress.Any, s.Port);
            o.UseUdp(s.Port);
        });
        plc.Adapter.TagWritten += t => Ui.Warn($"{Markup.Escape(t.Name)} written: {Markup.Escape(t.Value.ToString())}");
        await plc.StartAsync(ct);
        Ui.Success($"EtherNet/IP adapter \"{Markup.Escape(plc.Adapter.Identity.ProductName)}\" on TCP/UDP {s.Port} with {plc.Adapter.Tags.Count} tags (Line_Speed, Motor_Run, Count_Good…). Ctrl+C to stop.");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct);
                plc.Step(1);
            }
        }
        catch (OperationCanceledException)
        {
        }

        return 0;
    }
}

internal sealed class EnipDemoCommand : AsyncCommand<EnipDemoCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--allow-write"), Description("Also change the line speed and seal set point.")]
        public bool AllowWrite { get; init; }

        [CommandOption("--frames"), Description("Print every encapsulation packet.")]
        public bool Frames { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        var (client, owner, plc) = await EnipUi.OpenAsync(new EnipSettings { Sim = true, Frames = s.Frames }, s.AllowWrite, ct);
        await using (owner)
        {
            Ui.Success($"Session 0x{client.Session:X8} registered.");
            EnipUi.Identity(await client.ReadIdentityAsync(ct));
            string[] tags = ["Line_Speed", "Motor_Run", "Count_Good", "Count_Reject", "Seal_Temp", "Seal_Setpoint", "Recipe", "Fault_Code"];
            var t = new Table().Border(TableBorder.Rounded).AddColumns("Tag", "Value");
            foreach (var (tag, value, _) in await client.ReadTagsAsync(tags, ct)) t.AddRow(tag, Markup.Escape(value?.ToString() ?? "?"));
            AnsiConsole.Write(t);
            Ui.Success($"Zone_Temps = {Markup.Escape((await client.ReadTagAsync("Zone_Temps", 4, ct)).ToString())}");
            try
            {
                await client.WriteTagAsync("Line_Speed", CipTagValue.Of(CipDataType.Real, 150), ct);
                plc!.Step(60);
                Ui.Success($"Line_Speed set to 150 packs/min; after a minute Count_Good = {(await client.ReadTagAsync("Count_Good", ct: ct)).Value}.");
            }
            catch (ReadOnlyModeException ex)
            {
                Ui.Warn(Markup.Escape(ex.Message) + " Add --allow-write to try it.");
            }
        }

        return 0;
    }
}

internal sealed class EnipDecodeCommand : Command<EnipDecodeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<hex>"), Description("An encapsulation packet, or a bare CIP request/reply, in hex.")]
        public string Hex { get; init; } = "";
    }

    public override int Execute(CommandContext context, Settings s, CancellationToken ct)
    {
        var bytes = Convert.FromHexString(new string(s.Hex.Where(Uri.IsHexDigit).ToArray()));
        if (bytes.Length >= EnipPacket.HeaderSize && EnipPacket.FrameLength(bytes) == bytes.Length)
        {
            AnsiConsole.Write(Ui.FrameLane(bytes, EnipPacket.DescribeFields(bytes)));
            Ui.Success(Markup.Escape(EnipPacket.Decode(bytes).Describe()));
            return 0;
        }

        Ui.Success(Markup.Escape((bytes[0] & 0x80) != 0 ? CipResponse.Decode(bytes).ToString() : CipRequest.Decode(bytes).ToString()));
        return 0;
    }
}
