using System.ComponentModel;
using System.Globalization;
using IoTCom.Net.Transport.Usb;
using Spectre.Console;
using Spectre.Console.Cli;

namespace IoTCom.Net.Cli.Commands;

internal class UsbSettings : CommandSettings
{
    [CommandOption("--sim"), Description("Use a virtual bus (a loopback device and a 4-channel HID relay board).")]
    public bool Simulate { get; init; }

    public IUsbBackend Backend()
    {
        if (!Simulate) return NativeUsbBackend.Instance;
        var bus = new VirtualUsbBus();
        bus.Add(new VirtualLoopbackDevice());
        bus.AddHid(new VirtualHidRelayBoard(4));
        return bus;
    }

    public static byte ParseByte(string s) => s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? byte.Parse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) : byte.Parse(s, CultureInfo.InvariantCulture);

    public static ushort ParseUShort(string s) => s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? ushort.Parse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) : ushort.Parse(s, CultureInfo.InvariantCulture);
}

internal sealed class UsbListCommand : Command<UsbSettings>
{
    public override int Execute(CommandContext context, UsbSettings s, CancellationToken ct)
    {
        var table = new Table().Border(TableBorder.Rounded).AddColumns("Id", "Device", "Interfaces");
        foreach (var d in UsbDevice.List(s.Backend()).OrderBy(d => d.Id, StringComparer.Ordinal))
            table.AddRow($"[bold]{Markup.Escape(d.Id)}[/]", Markup.Escape($"{d.Manufacturer ?? d.VendorName ?? "?"} · {d.Product ?? d.Kind}"),
                Markup.Escape(string.Join(", ", d.Interfaces.Select(i => $"{i.Number}:{i.ClassName}"))));
        AnsiConsole.Write(table);
        return 0;
    }
}

internal sealed class HidListCommand : Command<UsbSettings>
{
    public override int Execute(CommandContext context, UsbSettings s, CancellationToken ct)
    {
        var table = new Table().Border(TableBorder.Rounded).AddColumns("VID:PID", "Device", "Usage", "Path");
        foreach (var d in HidDevice.List(s.Backend()).OrderBy(d => d.VendorId).ThenBy(d => d.ProductId))
            table.AddRow($"{d.VendorId:x4}:{d.ProductId:x4}", Markup.Escape($"{d.Manufacturer} {d.Product}".Trim()), Markup.Escape(d.UsageName), $"[{Ui.Hex(Ui.Muted)}]{Markup.Escape(d.Path)}[/]");
        AnsiConsole.Write(table);
        return 0;
    }
}

internal sealed class UsbControlCommand : AsyncCommand<UsbControlCommand.Settings>
{
    public sealed class Settings : UsbSettings
    {
        [CommandArgument(0, "<id>"), Description("vvvv:pppp[:serial].")] public string Id { get; init; } = "";
        [CommandArgument(1, "<request>"), Description("bRequest (e.g. 0x01).")] public string Request { get; init; } = "";
        [CommandOption("--type"), Description("vendor (default), class or standard.")] public string Type { get; init; } = "vendor";
        [CommandOption("--recipient"), Description("device (default) or interface.")] public string Recipient { get; init; } = "device";
        [CommandOption("--value")] public string Value { get; init; } = "0";
        [CommandOption("--index")] public string Index { get; init; } = "0";
        [CommandOption("-n|--length"), Description("Bytes to read (control IN).")] public int Length { get; init; } = 64;
        [CommandOption("--interface")] public byte Interface { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        var type = (byte)((s.Type.ToLowerInvariant() switch { "standard" => 0, "class" => 1, _ => 2 }) << 5 | (s.Recipient.StartsWith("i", StringComparison.OrdinalIgnoreCase) ? 1 : 0));
        await using var usb = UsbDevice.Create(o =>
        {
            o.Backend = s.Backend();
            o.DeviceId = s.Id;
            o.Interfaces.Clear();
            o.Interfaces.Add(s.Interface);
            o.ReadOnly = true;
        });
        await usb.ConnectAsync(ct);
        var data = await usb.ControlInAsync(new UsbSetup(type, UsbSettings.ParseByte(s.Request), UsbSettings.ParseUShort(s.Value), UsbSettings.ParseUShort(s.Index)), s.Length, ct);
        AnsiConsole.MarkupLine($"[bold]{data.Length} B[/] {Convert.ToHexString(data)}");
        if (data.Length > 0 && data.All(b => b is >= 0x20 and < 0x7F)) AnsiConsole.MarkupLine($"[{Ui.Hex(Ui.Muted)}]\"{Markup.Escape(System.Text.Encoding.ASCII.GetString(data))}\"[/]");
        return 0;
    }
}

internal sealed class UsbWriteReadCommand : AsyncCommand<UsbWriteReadCommand.Settings>
{
    public sealed class Settings : UsbSettings
    {
        [CommandArgument(0, "<id>")] public string Id { get; init; } = "";
        [CommandArgument(1, "<hex>"), Description("Bytes to write to the OUT endpoint.")] public string Hex { get; init; } = "";
        [CommandOption("--out"), Description("OUT endpoint (default 0x01).")] public string Out { get; init; } = "0x01";
        [CommandOption("--in"), Description("IN endpoint to read the answer from (default 0x81; 'none' to skip).")] public string In { get; init; } = "0x81";
        [CommandOption("--interface")] public byte Interface { get; init; }
        [CommandOption("--allow-write"), Description("Required: this sends data to the device.")] public bool AllowWrite { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        if (!s.AllowWrite)
        {
            Ui.Warn("Writing sends data to the device. Re-run with [bold]--allow-write[/].");
            return 2;
        }

        await using var usb = UsbDevice.Create(o =>
        {
            o.Backend = s.Backend();
            o.DeviceId = s.Id;
            o.Interfaces.Clear();
            o.Interfaces.Add(s.Interface);
        });
        await usb.ConnectAsync(ct);
        var sent = await usb.WriteAsync(UsbSettings.ParseByte(s.Out), Convert.FromHexString(new string(s.Hex.Where(Uri.IsHexDigit).ToArray())), ct: ct);
        Ui.Success($"Wrote {sent} B to {Markup.Escape(s.Out)}.");
        if (s.In.Equals("none", StringComparison.OrdinalIgnoreCase)) return 0;
        var reply = await usb.ReadAsync(UsbSettings.ParseByte(s.In), 512, TimeSpan.FromSeconds(2), ct: ct);
        AnsiConsole.MarkupLine(reply is null ? $"[{Ui.Hex(Ui.Muted)}]no answer within 2 s[/]" : $"[bold]{reply.Length} B[/] {Convert.ToHexString(reply)}");
        return 0;
    }
}

internal sealed class UsbRelayCommand : AsyncCommand<UsbRelayCommand.Settings>
{
    public sealed class Settings : UsbSettings
    {
        [CommandArgument(0, "[relay]"), Description("Relay number (1-based) or 'all'. Omit to show the states.")] public string? Relay { get; init; }
        [CommandArgument(1, "[state]"), Description("on or off.")] public string? State { get; init; }
        [CommandOption("--allow-write"), Description("Required to switch relays.")] public bool AllowWrite { get; init; }
        [CommandOption("-y|--yes")] public bool Yes { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        var change = s.Relay is not null;
        if (change && !s.AllowWrite)
        {
            Ui.Warn("Switching a relay changes what is wired to it. Re-run with [bold]--allow-write[/].");
            return 2;
        }

        if (change && !s.Simulate && !s.Yes && !AnsiConsole.Confirm($"Switch relay {Markup.Escape(s.Relay!)} {Markup.Escape(s.State ?? "")}?", false)) return 1;
        await using var hid = HidDevice.Create(o =>
        {
            o.Backend = s.Backend();
            o.UseDevice(HidRelayBoard.VendorId, HidRelayBoard.ProductId);
            o.ReadOnly = !change;
        });
        await hid.ConnectAsync(ct);
        var board = new HidRelayBoard(hid);
        if (change)
        {
            var on = string.Equals(s.State, "on", StringComparison.OrdinalIgnoreCase);
            if (string.Equals(s.Relay, "all", StringComparison.OrdinalIgnoreCase)) await board.SetAllAsync(on, ct);
            else await board.SetAsync(int.Parse(s.Relay!, CultureInfo.InvariantCulture), on, ct);
        }

        var states = await board.GetStatesAsync(ct);
        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(hid.Info!.Product ?? "relay board")}[/] serial {Markup.Escape(await board.GetSerialAsync(ct))}");
        AnsiConsole.MarkupLine(string.Join("  ", states.Select((on, i) => on ? $"[{Ui.Hex(Ui.LampGreen)}]● {i + 1} ON[/]" : $"[{Ui.Hex(Ui.Muted)}]○ {i + 1} off[/]")));
        return 0;
    }
}
