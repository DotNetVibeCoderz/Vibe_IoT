using System.ComponentModel;
using System.Globalization;
using System.Text;
using IoTCom.Net.Debug;
using IoTCom.Net.Transport.Usb;
using Spectre.Console;
using Spectre.Console.Cli;

namespace IoTCom.Net.Cli.Commands;

/// <summary>Shared options: a probe and a chip, or the simulated MCU (--sim).</summary>
internal class ProbeSettings : CommandSettings
{
    [CommandOption("-t|--target"), Description("probe-rs target name, e.g. STM32F411CEUx, nRF52840_xxAA, RP2040.")]
    public string? Target { get; init; }

    [CommandOption("-p|--probe"), Description("Probe selector vid:pid[:serial] (default: the only probe).")]
    public string? Probe { get; init; }

    [CommandOption("--jtag"), Description("Use JTAG instead of SWD.")]
    public bool Jtag { get; init; }

    [CommandOption("--speed"), Description("Clock in kHz (default 4000).")]
    public int Speed { get; init; } = 4000;

    [CommandOption("--sim"), Description("Use a simulated STM32F411 running IoTCom's demo firmware.")]
    public bool Sim { get; init; }
}

internal static class DebugUi
{
    public static async Task<(DebugProbe Probe, VirtualMcu? Mcu)> OpenAsync(ProbeSettings s, bool writes, CancellationToken ct)
    {
        VirtualMcu? mcu = null;
        if (s.Sim)
        {
            mcu = new VirtualMcu(firmware: VirtualMcu.BuildDemoFirmware("1.0.0"));
            mcu.Step(1.5);
        }
        else if (string.IsNullOrEmpty(s.Target)) throw new InvalidOperationException("Give --target <chip> (probe-rs name, e.g. STM32F411CEUx) or --sim.");

        var probe = DebugProbe.Create(o =>
        {
            if (mcu is not null) o.UseVirtual(new VirtualProbeBackend(mcu));
            o.UseTarget(s.Target ?? mcu!.Target);
            o.Selector = s.Probe;
            o.Protocol = s.Jtag ? ProbeProtocol.Jtag : ProbeProtocol.Swd;
            o.SpeedKhz = s.Speed;
            if (writes) o.AllowWrites();
        });
        await probe.ConnectAsync(ct);
        return (probe, mcu);
    }

    public static ulong ParseAddress(string text) => text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? ulong.Parse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
        : ulong.Parse(text, CultureInfo.InvariantCulture);

    public static void Progress(FirmwareImage image, Func<IProgress<FlashProgress>, Task> run) =>
        AnsiConsole.Progress().AutoClear(false).Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn()).StartAsync(async ctx =>
        {
            var tasks = new Dictionary<string, ProgressTask>();
            await run(new Reporter(p =>
            {
                if (!tasks.TryGetValue(p.Phase, out var t)) tasks[p.Phase] = t = ctx.AddTask(p.Phase, maxValue: 1);
                t.Value = p.Fraction;
            }));
            foreach (var t in tasks.Values) t.Value = 1;
        }).GetAwaiter().GetResult();

    private sealed class Reporter(Action<FlashProgress> report) : IProgress<FlashProgress>
    {
        public void Report(FlashProgress value) => report(value);
    }
}

internal sealed class ProbeListCommand : Command
{
    public override int Execute(CommandContext context, CancellationToken ct)
    {
        if (!NativeProbeBackend.IsSupported)
        {
            Ui.Warn("The native probe library (iotcom_probe) is not available for this platform; use --sim.");
            return 1;
        }

        var probes = DebugProbe.List();
        if (probes.Count == 0)
        {
            Ui.Warn("No debug probes found. Check the USB cable, drivers (WinUSB via Zadig on Windows) and udev rules (Linux).");
            return 1;
        }

        var t = new Table().Border(TableBorder.Rounded).AddColumns("Selector", "Name", "Kind", "Serial");
        foreach (var p in probes) t.AddRow(Markup.Escape(p.Selector), Markup.Escape(p.Name), Markup.Escape(p.Kind), Markup.Escape(p.Serial ?? ""));
        AnsiConsole.Write(t);
        return 0;
    }
}

internal sealed class ProbeInfoCommand : AsyncCommand<ProbeSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, ProbeSettings s, CancellationToken ct)
    {
        var (probe, _) = await DebugUi.OpenAsync(s, false, ct);
        await using (probe)
        {
            var (state, pc) = await probe.CoreAsync(ct);
            Ui.Success($"{Markup.Escape(probe.Target)} through {Markup.Escape(probe.ProbeName ?? "")}: core {state}{(pc != 0 ? $", PC 0x{pc:X8}" : "")}");
            var vectors = await probe.ReadMemoryAsync(0x08000000, 8, ct);
            Ui.Success($"vector table: SP 0x{BitConverter.ToUInt32(vectors, 0):X8}, reset 0x{BitConverter.ToUInt32(vectors, 4):X8}");
        }

        return 0;
    }
}

internal sealed class ProbeReadCommand : AsyncCommand<ProbeReadCommand.Settings>
{
    public sealed class Settings : ProbeSettings
    {
        [CommandArgument(0, "<address>"), Description("Address (0x-prefixed hex or decimal).")]
        public string Address { get; init; } = "";

        [CommandArgument(1, "[length]"), Description("Bytes to read (default 64).")]
        public int Length { get; init; } = 64;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        var (probe, _) = await DebugUi.OpenAsync(s, false, ct);
        await using (probe)
        {
            var address = DebugUi.ParseAddress(s.Address);
            var data = await probe.ReadMemoryAsync(address, s.Length, ct);
            for (var i = 0; i < data.Length; i += 16)
            {
                var line = data.AsSpan(i, Math.Min(16, data.Length - i));
                var ascii = new string([.. line.ToArray().Select(b => b is >= 0x20 and < 0x7F ? (char)b : '.')]);
                AnsiConsole.MarkupLine($"[{Ui.Hex(Ui.Muted)}]{address + (ulong)i:X8}[/]  {HexDump.ToHex(line),-47}  [{Ui.Hex(Ui.Muted)}]{Markup.Escape(ascii)}[/]");
            }
        }

        return 0;
    }
}

internal sealed class ProbeFlashCommand : AsyncCommand<ProbeFlashCommand.Settings>
{
    public sealed class Settings : ProbeSettings
    {
        [CommandArgument(0, "[file]"), Description("Firmware: .elf, .hex, .dfu, or .bin (with --base). With --sim and no file, the demo firmware.")]
        public string? File { get; init; }

        [CommandOption("--base"), Description("Load address for raw .bin files (default 0x08000000).")]
        public string Base { get; init; } = "0x08000000";

        [CommandOption("--version"), Description("With --sim and no file: build the demo firmware with this version (default 1.1.0).")]
        public string Version { get; init; } = "1.1.0";

        [CommandOption("--allow-write"), Description("Required: flashing replaces the firmware.")]
        public bool AllowWrite { get; init; }

        [CommandOption("-y|--yes"), Description("Skip the confirmation.")]
        public bool Yes { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        if (!s.AllowWrite)
        {
            Ui.Error("Flashing replaces the firmware. Add --allow-write.");
            return 2;
        }

        var image = s.File is not null ? FirmwareImage.Load(s.File, (uint)DebugUi.ParseAddress(s.Base))
            : s.Sim ? VirtualMcu.BuildDemoFirmware(s.Version) : throw new InvalidOperationException("Give a firmware file.");
        var (probe, mcu) = await DebugUi.OpenAsync(s, true, ct);
        await using (probe)
        {
            if (!s.Yes && !s.Sim && !AnsiConsole.Confirm($"Flash {Markup.Escape(image.ToString())} to {Markup.Escape(probe.Target)}?", false)) return 1;
            DebugUi.Progress(image, p => probe.FlashAsync(image, p, reset: true, ct));
            Ui.Success($"Flashed {Markup.Escape(image.ToString())} and reset {Markup.Escape(probe.Target)}.");
            if (mcu is not null)
            {
                mcu.Step(0.3);
                var rtt = await probe.AttachRttAsync(VirtualMcu.RamBase, VirtualMcu.RamSize, ct: ct);
                Ui.Success($"RTT: {Markup.Escape((await rtt.ReadTextAsync(0, ct)).Split('\n')[0])}");
            }
        }

        return 0;
    }
}

internal sealed class ProbeRttCommand : AsyncCommand<ProbeRttCommand.Settings>
{
    public sealed class Settings : ProbeSettings
    {
        [CommandOption("--ram"), Description("RAM range to scan for the control block: start:length (default 0x20000000:0x20000).")]
        public string Ram { get; init; } = "0x20000000:0x20000";

        [CommandOption("--address"), Description("Control block address (skips the scan; from the ELF symbol _SEGGER_RTT).")]
        public string? Address { get; init; }

        [CommandOption("--channel"), Description("Up channel (default 0).")]
        public int Channel { get; init; }

        [CommandOption("--send"), Description("Send this line on down channel 0 first (requires --allow-write).")]
        public string? Send { get; init; }

        [CommandOption("--allow-write"), Description("Allow writes to the down channel.")]
        public bool AllowWrite { get; init; }

        [CommandOption("--seconds"), Description("Stop after this many seconds (default: until Ctrl+C).")]
        public double? Seconds { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        if (s.Send is not null && !s.AllowWrite)
        {
            Ui.Error("Sending on a down channel feeds input to the firmware. Add --allow-write.");
            return 2;
        }

        var (probe, mcu) = await DebugUi.OpenAsync(s, s.AllowWrite, ct);
        await using (probe)
        {
            var parts = s.Ram.Split(':');
            var rtt = await probe.AttachRttAsync(DebugUi.ParseAddress(parts[0]), (int)DebugUi.ParseAddress(parts[1]), s.Address is null ? null : DebugUi.ParseAddress(s.Address), ct);
            Ui.Success($"RTT control block at 0x{rtt.Address:X8}: up {string.Join(", ", rtt.UpChannels.Select(c => $"{c.Index} '{Markup.Escape(c.Name)}' {c.Size} B"))}; down {string.Join(", ", rtt.DownChannels.Select(c => $"{c.Index} '{Markup.Escape(c.Name)}'"))}. Ctrl+C to stop.");
            if (s.Send is not null) await rtt.WriteAsync(Encoding.UTF8.GetBytes(s.Send + "\n"), 0, ct);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (s.Seconds is { } secs) stop.CancelAfter(TimeSpan.FromSeconds(secs));
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    mcu?.Step(0.1);
                    var text = await rtt.ReadTextAsync(s.Channel, stop.Token);
                    if (text.Length > 0) AnsiConsole.Write(new Text(text));
                    await Task.Delay(100, stop.Token);
                }
            }
            catch (OperationCanceledException) { }
        }

        return 0;
    }
}

internal sealed class ProbeControlCommand : AsyncCommand<ProbeControlCommand.Settings>
{
    public sealed class Settings : ProbeSettings
    {
        [CommandArgument(0, "<action>"), Description("halt, run, reset, reset-halt or erase-all.")]
        public string Action { get; init; } = "";

        [CommandOption("--allow-write"), Description("Required: these change what the target does.")]
        public bool AllowWrite { get; init; }

        [CommandOption("-y|--yes"), Description("Skip the confirmation for erase-all.")]
        public bool Yes { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        if (!s.AllowWrite)
        {
            Ui.Error($"{Markup.Escape(s.Action)} changes what the target does. Add --allow-write.");
            return 2;
        }

        var (probe, _) = await DebugUi.OpenAsync(s, true, ct);
        await using (probe)
        {
            switch (s.Action.ToLowerInvariant())
            {
                case "halt": await probe.HaltAsync(ct); break;
                case "run": await probe.RunAsync(ct); break;
                case "reset": await probe.ResetAsync(false, ct); break;
                case "reset-halt": await probe.ResetAsync(true, ct); break;
                case "erase-all":
                    if (!s.Yes && !s.Sim && !AnsiConsole.Confirm($"Erase all flash of {Markup.Escape(probe.Target)}?", false)) return 1;
                    await probe.EraseAllAsync(ct);
                    break;
                default:
                    Ui.Error("Action is halt, run, reset, reset-halt or erase-all.");
                    return 2;
            }

            var (state, pc) = await probe.CoreAsync(ct);
            Ui.Success($"{Markup.Escape(s.Action)} done: core {state}{(pc != 0 ? $", PC 0x{pc:X8}" : "")}");
        }

        return 0;
    }
}

internal sealed class FirmwareInfoCommand : Command<FirmwareInfoCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<file>"), Description("Firmware file (.elf, .hex, .dfu, .bin).")]
        public string File { get; init; } = "";

        [CommandOption("--to-hex"), Description("Also write the image as Intel HEX to this path.")]
        public string? ToHex { get; init; }
    }

    public override int Execute(CommandContext context, Settings s, CancellationToken ct)
    {
        var image = FirmwareImage.Load(s.File);
        var t = new Table().Border(TableBorder.Rounded).AddColumns("Start", "End", "Bytes", "CRC-32");
        foreach (var seg in image.Segments) t.AddRow($"0x{seg.Address:X8}", $"0x{seg.End:X8}", seg.Data.Length.ToString(CultureInfo.InvariantCulture), $"{Crc32.Compute(seg.Data):X8}");
        AnsiConsole.Write(t);
        Ui.Success($"{Markup.Escape(image.ToString())}{(image.EntryPoint is { } e ? $", entry 0x{e:X8}" : "")}{(image.FindTag("FWVER:") is { } v ? $", version {Markup.Escape(v)}" : "")}");
        if (s.File.EndsWith(".dfu", StringComparison.OrdinalIgnoreCase))
        {
            var dfu = DfuFile.Parse(File.ReadAllBytes(s.File));
            Ui.Success($"DFU suffix: {dfu.VendorId:X4}:{dfu.ProductId:X4}, bcdDFU {dfu.DfuVersion:X4}{(dfu.IsDfuSe ? $", DfuSe targets {Markup.Escape(string.Join(", ", dfu.Targets.Select(x => $"{x.Alternate} {x.Name}")))}" : "")}");
        }

        if (s.ToHex is not null)
        {
            File.WriteAllText(s.ToHex, IntelHex.Format(image));
            Ui.Success($"Wrote {Markup.Escape(s.ToHex)}.");
        }

        return 0;
    }
}

/// <summary>Shared DFU options: a real device or the simulated STM32 bootloader (--sim).</summary>
internal class DfuSettings : CommandSettings
{
    [CommandOption("-d|--device"), Description("vid:pid[:serial] (default 0483:df11, the STM32 system bootloader).")]
    public string Device { get; init; } = "0483:df11";

    [CommandOption("--sim"), Description("Use a simulated STM32 bootloader in DFU mode.")]
    public bool Sim { get; init; }
}

internal static class DfuUi
{
    public static (DfuClient Client, VirtualMcu? Mcu) Open(DfuSettings s, bool writes)
    {
        VirtualMcu? mcu = null;
        VirtualUsbBus? bus = null;
        if (s.Sim)
        {
            mcu = new VirtualMcu(firmware: VirtualMcu.BuildDemoFirmware("1.0.0"));
            bus = new VirtualUsbBus();
            bus.Add(new VirtualDfuDevice(mcu));
        }

        var client = DfuClient.Create(o =>
        {
            if (bus is not null) o.UseVirtual(bus);
            o.DeviceId = s.Device;
            if (writes) o.AllowWrites();
        });
        return (client, mcu);
    }
}

internal sealed class DfuListCommand : Command
{
    public override int Execute(CommandContext context, CancellationToken ct)
    {
        var devices = NativeUsbBackend.Instance.List().Where(d => d.Interfaces.Any(i => i.Class == 0xFE && i.Subclass == 0x01)).ToList();
        if (devices.Count == 0)
        {
            Ui.Warn("No DFU devices. STM32: hold BOOT0 high and reset; other boards: see their bootloader docs.");
            return 1;
        }

        var t = new Table().Border(TableBorder.Rounded).AddColumns("Device", "Product", "Interface", "Memory");
        foreach (var d in devices)
            foreach (var i in d.Interfaces.Where(i => i.Class == 0xFE))
                t.AddRow(Markup.Escape(d.Id), Markup.Escape(d.Product ?? ""), i.Number.ToString(CultureInfo.InvariantCulture), Markup.Escape(DfuSeLayout.Parse(i.Name)?.Describe() ?? i.Name ?? ""));
        AnsiConsole.Write(t);
        return 0;
    }
}

internal sealed class DfuInfoCommand : AsyncCommand<DfuSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, DfuSettings s, CancellationToken ct)
    {
        var (dfu, _) = DfuUi.Open(s, false);
        await using (dfu)
        {
            await dfu.ConnectAsync(ct);
            var status = await dfu.GetStatusAsync(ct);
            Ui.Success($"{Markup.Escape(dfu.Device!.Id)} {Markup.Escape(dfu.Device.Product ?? "")}: state {status.State}, status {status.StatusText}");
            if (dfu.Layout is { } l) Ui.Success($"memory: {Markup.Escape(l.Describe())}");
        }

        return 0;
    }
}

internal sealed class DfuUploadCommand : AsyncCommand<DfuUploadCommand.Settings>
{
    public sealed class Settings : DfuSettings
    {
        [CommandArgument(0, "<file>"), Description("Output file (.bin, or .hex for Intel HEX).")]
        public string File { get; init; } = "";

        [CommandOption("--address"), Description("Start address (default 0x08000000).")]
        public string Address { get; init; } = "0x08000000";

        [CommandOption("--length"), Description("Bytes to read (default 16384).")]
        public int Length { get; init; } = 16384;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        var (dfu, _) = DfuUi.Open(s, false);
        await using (dfu)
        {
            var address = (uint)DebugUi.ParseAddress(s.Address);
            var data = await dfu.UploadAsync(s.Length, address, ct);
            if (s.File.EndsWith(".hex", StringComparison.OrdinalIgnoreCase)) File.WriteAllText(s.File, IntelHex.Format(FirmwareImage.FromBinary(data, address)));
            else File.WriteAllBytes(s.File, data);
            Ui.Success($"Read {data.Length} bytes from 0x{address:X8} into {Markup.Escape(s.File)} (CRC-32 {Crc32.Compute(data):X8}).");
        }

        return 0;
    }
}

internal sealed class DfuDownloadCommand : AsyncCommand<DfuDownloadCommand.Settings>
{
    public sealed class Settings : DfuSettings
    {
        [CommandArgument(0, "[file]"), Description("Firmware (.dfu, .hex, .elf, .bin with --base). With --sim and no file, the demo firmware.")]
        public string? File { get; init; }

        [CommandOption("--base"), Description("Load address for raw .bin files (default 0x08000000).")]
        public string Base { get; init; } = "0x08000000";

        [CommandOption("--leave"), Description("Start the application afterwards.")]
        public bool Leave { get; init; }

        [CommandOption("--allow-write"), Description("Required: downloading replaces the firmware.")]
        public bool AllowWrite { get; init; }

        [CommandOption("-y|--yes"), Description("Skip the confirmation.")]
        public bool Yes { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        if (!s.AllowWrite)
        {
            Ui.Error("Downloading replaces the firmware. Add --allow-write.");
            return 2;
        }

        var image = s.File is not null ? FirmwareImage.Load(s.File, (uint)DebugUi.ParseAddress(s.Base))
            : s.Sim ? VirtualMcu.BuildDemoFirmware("2.0.0") : throw new InvalidOperationException("Give a firmware file.");
        var (dfu, mcu) = DfuUi.Open(s, true);
        await using (dfu)
        {
            await dfu.ConnectAsync(ct);
            if (!s.Yes && !s.Sim && !AnsiConsole.Confirm($"Download {Markup.Escape(image.ToString())} to {Markup.Escape(dfu.Device!.Id)}?", false)) return 1;
            DebugUi.Progress(image, p => dfu.DownloadAsync(image, p, ct));
            Ui.Success($"Downloaded {Markup.Escape(image.ToString())}.");
            if (s.Leave)
            {
                await dfu.LeaveAsync(image.Start, ct);
                Ui.Success(mcu is not null ? $"Left DFU mode: the MCU boots firmware {Markup.Escape(mcu.FirmwareVersion ?? "?")}." : "Left DFU mode: the device starts the application.");
            }
        }

        return 0;
    }
}
