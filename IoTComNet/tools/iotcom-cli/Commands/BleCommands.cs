using System.ComponentModel;
using System.Globalization;
using IoTCom.Net.Transport.Ble;
using Spectre.Console;
using Spectre.Console.Cli;

namespace IoTCom.Net.Cli.Commands;

internal class BleSettings : CommandSettings
{
    [CommandOption("--sim"), Description("Use a virtual radio with simulated peripherals (heart-rate strap, greenhouse sensor, beacon, smart plug).")]
    public bool Simulate { get; init; }

    [CommandOption("-t|--seconds"), Description("Scan duration (default 5).")]
    public double Seconds { get; init; } = 5;

    /// <summary>Opens the radio; with --sim also starts the simulator loop.</summary>
    public async Task<(BleCentral Central, CancellationTokenSource? Sim)> OpenAsync(bool readOnly, CancellationToken ct)
    {
        CancellationTokenSource? sim = null;
        var central = BleCentral.Create(o =>
        {
            o.ReadOnly = readOnly;
            if (Simulate)
            {
                var net = new VirtualBleNetwork();
                net.AddHeartRateStrap();
                net.AddEnvironmentSensor();
                net.AddBeacon();
                net.AddSmartPlug();
                var driver = new VirtualBleSimulator(net);
                sim = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var token = sim.Token;
                _ = Task.Run(async () =>
                {
                    while (!token.IsCancellationRequested)
                    {
                        driver.Step();
                        try { await Task.Delay(1000, token); } catch (OperationCanceledException) { return; }
                    }
                }, CancellationToken.None);
                o.UseVirtual(net);
            }
            else
            {
                o.UseNative();
            }
        });
        await central.ConnectAsync(ct);
        Ui.Success($"Radio: [bold]{Markup.Escape(central.Adapter!.Description)}[/]");
        return (central, sim);
    }

    /// <summary>Scans until <paramref name="id"/> is seen (connect needs a recent advertisement).</summary>
    public async Task FindAsync(BleCentral central, string id, CancellationToken ct)
    {
        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        window.CancelAfter(TimeSpan.FromSeconds(Seconds));
        try
        {
            await foreach (var ad in central.WatchAsync(null, window.Token))
                if (string.Equals(ad.Id, id, StringComparison.OrdinalIgnoreCase) || string.Equals(ad.Address, id, StringComparison.OrdinalIgnoreCase)) return;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }

        throw new DeviceException($"{id} did not advertise within {Seconds} s.");
    }
}

internal sealed class BleScanCommand : AsyncCommand<BleScanCommand.Settings>
{
    public sealed class Settings : BleSettings
    {
        [CommandOption("--service"), Description("Only devices advertising this service (e.g. 180d).")]
        public string[] Services { get; init; } = [];
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        var (central, sim) = await s.OpenAsync(readOnly: true, ct);
        await using var _ = central;
        using var __ = sim;
        var found = await AnsiConsole.Status().StartAsync($"Scanning {s.Seconds} s…", _ => central.ScanAsync(TimeSpan.FromSeconds(s.Seconds), [.. s.Services.Select(BleUuid.Parse)], ct));
        var table = new Table().Border(TableBorder.Rounded).AddColumns("Name", "Id", "RSSI", "Services", "Details");
        foreach (var ad in found)
        {
            var details = ad.IBeacon is { } b ? $"iBeacon {b.ProximityUuid} {b.Major}/{b.Minor}"
                : ad.Eddystone is { } e ? $"Eddystone {e.Kind} {e.Description}"
                : string.Join(" ", ad.ManufacturerData.Select(m => $"mfr 0x{m.Key:X4}:{Convert.ToHexString(m.Value)}"));
            table.AddRow($"[bold]{Markup.Escape(ad.Name ?? "-")}[/]", Markup.Escape(ad.Id), $"{ad.Rssi?.ToString(CultureInfo.InvariantCulture) ?? "?"} dBm",
                Markup.Escape(string.Join(", ", ad.Services.Select(BleUuid.Name))), Markup.Escape(details));
        }

        AnsiConsole.Write(table);
        if (found.Count == 0) Ui.Warn("Nothing advertised. Check that Bluetooth is on and the device is advertising.");
        return 0;
    }
}

internal sealed class BleServicesCommand : AsyncCommand<BleServicesCommand.Settings>
{
    public sealed class Settings : BleSettings
    {
        [CommandArgument(0, "<id>"), Description("Device id or address from `iotcom ble scan`.")]
        public string Id { get; init; } = "";
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        var (central, sim) = await s.OpenAsync(readOnly: true, ct);
        await using var _ = central;
        using var __ = sim;
        await s.FindAsync(central, s.Id, ct);
        await using var p = await central.OpenAsync(s.Id, ct);
        var tree = new Tree($"[bold]{Markup.Escape(p.Name ?? p.Id)}[/]");
        foreach (var svc in p.Services)
        {
            var node = tree.AddNode($"[{Ui.Hex(Ui.Amber)}]{Markup.Escape(svc.Name)}[/] [{Ui.Hex(Ui.Muted)}]{BleUuid.Short(svc.Uuid)}[/]");
            foreach (var c in svc.Characteristics)
            {
                var value = "";
                if ((c.Properties & GattProperties.Read) != 0)
                {
                    try { value = " = " + GattValue.Describe(c.Uuid, await p.ReadAsync(c.Uuid, ct)); }
                    catch (Exception ex) when (ex is IoTComException or TimeoutException) { value = " (read failed)"; }
                }

                node.AddNode($"[{Ui.Hex(Ui.CableBlue)}]{Markup.Escape(c.Name)}[/] [{Ui.Hex(Ui.Muted)}]{BleUuid.Short(c.Uuid)} {c.Properties}[/]{Markup.Escape(value)}");
            }
        }

        AnsiConsole.Write(tree);
        return 0;
    }
}

internal sealed class BleWatchCommand : AsyncCommand<BleWatchCommand.Settings>
{
    public sealed class Settings : BleSettings
    {
        [CommandArgument(0, "<id>")] public string Id { get; init; } = "";

        [CommandArgument(1, "<characteristic>"), Description("UUID, e.g. 2a37 for heart rate.")]
        public string Characteristic { get; init; } = "";
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        var (central, sim) = await s.OpenAsync(readOnly: true, ct);
        await using var _ = central;
        using var __ = sim;
        await s.FindAsync(central, s.Id, ct);
        await using var p = await central.OpenAsync(s.Id, ct);
        var uuid = BleUuid.Parse(s.Characteristic);
        Ui.Success($"Notifications from {Markup.Escape(BleUuid.Name(uuid))} on {Markup.Escape(p.Name ?? p.Id)}. Ctrl+C to stop.");
        try
        {
            await foreach (var v in p.SubscribeAsync(uuid, ct))
                AnsiConsole.MarkupLine($"[{Ui.Hex(Ui.Muted)}]{DateTime.Now:HH:mm:ss.fff}[/] [bold]{Markup.Escape(GattValue.Describe(uuid, v))}[/] [{Ui.Hex(Ui.Muted)}]{Convert.ToHexString(v)}[/]");
        }
        catch (OperationCanceledException) { }
        return 0;
    }
}

internal sealed class BleWriteCommand : AsyncCommand<BleWriteCommand.Settings>
{
    public sealed class Settings : BleSettings
    {
        [CommandArgument(0, "<id>")] public string Id { get; init; } = "";

        [CommandArgument(1, "<characteristic>")] public string Characteristic { get; init; } = "";

        [CommandArgument(2, "<hex>"), Description("Value in hex, e.g. 01.")]
        public string Hex { get; init; } = "";

        [CommandOption("--allow-write"), Description("Required: writing changes the device.")]
        public bool AllowWrite { get; init; }

        [CommandOption("-y|--yes")] public bool Yes { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        if (!s.AllowWrite)
        {
            Ui.Warn("Writing changes the device. Re-run with [bold]--allow-write[/].");
            return 2;
        }

        if (!s.Simulate && !s.Yes && !AnsiConsole.Confirm($"Write {Markup.Escape(s.Hex)} to {Markup.Escape(s.Characteristic)} on {Markup.Escape(s.Id)}?", false)) return 1;
        var (central, sim) = await s.OpenAsync(readOnly: false, ct);
        await using var _ = central;
        using var __ = sim;
        await s.FindAsync(central, s.Id, ct);
        await using var p = await central.OpenAsync(s.Id, ct);
        await p.WriteAsync(BleUuid.Parse(s.Characteristic), Convert.FromHexString(new string(s.Hex.Where(Uri.IsHexDigit).ToArray())), ct: ct);
        Ui.Success($"Wrote {Markup.Escape(s.Hex)} to {Markup.Escape(BleUuid.Name(BleUuid.Parse(s.Characteristic)))}.");
        return 0;
    }
}
