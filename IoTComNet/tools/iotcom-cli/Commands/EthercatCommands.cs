using System.Buffers.Binary;
using System.ComponentModel;
using System.Globalization;
using IoTCom.Net.Protocols.EtherCat;
using Spectre.Console;
using Spectre.Console.Cli;

namespace IoTCom.Net.Cli.Commands;

/// <summary>Shared options: a raw interface (--if) or the in-process I/O rack (--sim).</summary>
internal class EthercatSettings : CommandSettings
{
    [CommandOption("-i|--if"), Description("Network interface dedicated to EtherCAT (see `iotcom ethercat interfaces`). Needs CAP_NET_RAW / administrator rights.")]
    public string? Interface { get; init; }

    [CommandOption("--sim"), Description("Use an in-process I/O rack (coupler, DI8, DO8, AI4 on a conveyor).")]
    public bool Sim { get; init; }

    [CommandOption("--frames"), Description("Print every frame.")]
    public bool Frames { get; init; }
}

internal static class EthercatUi
{
    public static async Task<(EthercatMaster Master, EthercatRackSimulator? Rack)> OpenAsync(EthercatSettings s, bool writes, CancellationToken ct)
    {
        EthercatRackSimulator? rack = null;
        if (s.Sim) rack = new EthercatRackSimulator();
        else if (s.Interface is null) throw new InvalidOperationException("Give --if <interface> or --sim.");
        var master = EthercatMaster.Create(o =>
        {
            if (rack is not null) o.UseLink(rack.Segment);
            else o.UseInterface(s.Interface!);
            if (writes) o.AllowWrites();
        });
        if (s.Frames) master.AddTap(new Printer());
        await master.ConnectAsync(ct);
        await master.ScanAsync(ct);
        return (master, rack);
    }

    private sealed class Printer : ITrafficTap
    {
        public void OnFrame(in TrafficFrame frame) =>
            AnsiConsole.MarkupLine($"[{Ui.Hex(Ui.Muted)}]{frame.Timestamp.ToLocalTime():HH:mm:ss.fff} {(frame.Direction == FrameDirection.Inbound ? "←" : "→")} {Markup.Escape(frame.Summary ?? "")}[/]");
    }

    public static Table Slaves(EthercatMaster master)
    {
        var t = new Table().Border(TableBorder.Rounded).AddColumns("#", "Station", "Name", "Vendor", "Product", "Serial", "State", "In", "Out", "CoE");
        foreach (var s in master.Slaves)
        {
            var colour = s.StatusCode != 0 ? Ui.Fault : s.State == EcatState.Op ? Ui.LampGreen : s.State >= EcatState.PreOp ? Ui.Amber : Ui.Muted;
            t.AddRow(s.Position.ToString(CultureInfo.InvariantCulture), $"0x{s.Station:X4}", Markup.Escape(s.Name), $"0x{s.Vendor:X8}", $"0x{s.Product:X8}",
                s.Serial.ToString(CultureInfo.InvariantCulture), $"[{Ui.Hex(colour)}]{EcatAlStatus.Name(s.State)}{(s.StatusCode != 0 ? $" 0x{s.StatusCode:X4}" : "")}[/]",
                $"{s.InputBytes} B", $"{s.OutputBytes} B", s.HasCoe ? "yes" : "");
        }

        return t;
    }

    public static string Bits(ReadOnlySpan<byte> b) => string.Join(' ', b.ToArray().Select(x => Convert.ToString(x, 2).PadLeft(8, '0')));

    public static EthercatSlave Slave(EthercatMaster master, int position) => position >= 0 && position < master.Slaves.Count
        ? master.Slaves[position]
        : throw new ArgumentException($"No slave at position {position}; the line has {master.Slaves.Count}.");

    public static (ushort Index, byte Sub) ParseObject(string text)
    {
        var parts = text.Split(':');
        var index = ushort.Parse(parts[0].Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var sub = parts.Length > 1 ? byte.Parse(parts[1].Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber, CultureInfo.InvariantCulture) : (byte)0;
        return (index, sub);
    }
}

internal sealed class EthercatInterfacesCommand : Command
{
    public override int Execute(CommandContext context, CancellationToken ct)
    {
        var list = RawEthernetLink.List();
        if (list.Count == 0)
        {
            Ui.Warn(OperatingSystem.IsWindows() ? "No interfaces: install Npcap (https://npcap.com) in WinPcap-compatible mode." : "No interfaces found.");
            return 1;
        }

        var t = new Table().Border(TableBorder.Rounded).AddColumns("Name", "Description");
        foreach (var i in list) t.AddRow(Markup.Escape(i.Name), Markup.Escape(i.Description));
        AnsiConsole.Write(t);
        Ui.Warn("Use a port dedicated to EtherCAT; the master sends broadcast frames with EtherType 0x88A4.");
        return 0;
    }
}

internal sealed class EthercatScanCommand : AsyncCommand<EthercatSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, EthercatSettings s, CancellationToken ct)
    {
        var (master, _) = await EthercatUi.OpenAsync(s, false, ct);
        await using (master)
        {
            if (master.Slaves.Count == 0)
            {
                Ui.Warn($"No slaves answered on {Markup.Escape(master.Interface ?? "")}.");
                return 1;
            }

            AnsiConsole.Write(EthercatUi.Slaves(master));
            Ui.Success($"{master.Slaves.Count} slave(s) on {Markup.Escape(master.Interface ?? "")}; process image {master.Slaves.Sum(x => x.InputBytes)} B in, {master.Slaves.Sum(x => x.OutputBytes)} B out.");
        }

        return 0;
    }
}

internal sealed class EthercatWatchCommand : AsyncCommand<EthercatWatchCommand.Settings>
{
    public sealed class Settings : EthercatSettings
    {
        [CommandOption("--period"), Description("Cycle period in milliseconds (default 10).")]
        public int Period { get; init; } = 10;

        [CommandOption("--seconds"), Description("Stop after this many seconds (default: until Ctrl+C).")]
        public double? Seconds { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        var (master, rack) = await EthercatUi.OpenAsync(s, false, ct);
        await using (master)
        {
            await master.RequestStateAsync(EcatState.SafeOp, ct);
            Ui.Success($"SAFE-OP (read-only: inputs only, outputs held safe). Cycle {s.Period} ms. Ctrl+C to stop.");
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (s.Seconds is { } secs) stop.CancelAfter(TimeSpan.FromSeconds(secs));
            var sim = rack is null ? Task.CompletedTask : Task.Run(async () =>
            {
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        rack.Step(0.05);
                        await Task.Delay(50, stop.Token);
                    }
                }
                catch (OperationCanceledException) { }
            }, CancellationToken.None);
            var run = master.RunAsync(TimeSpan.FromMilliseconds(s.Period), stop.Token);
            string Caption()
            {
                var st = master.Statistics;
                return string.Create(CultureInfo.InvariantCulture, $"cycles {st.Cycles} · wkc {st.LastWorkingCounter}/{st.ExpectedWorkingCounter} · wkc errors {st.WorkingCounterErrors} · lost {st.LostFrames} · rtt {st.LastRoundTrip.TotalMilliseconds:0.000} ms (max {st.MaxRoundTrip.TotalMilliseconds:0.000})");
            }

            string Inputs(EthercatSlave slave) => slave.InputBytes <= 2 ? EthercatUi.Bits(master.InputsOf(slave)) : HexDump.ToHex(master.InputsOf(slave));
            async Task Pause(int ms)
            {
                try
                {
                    await Task.Delay(ms, stop.Token);
                }
                catch (OperationCanceledException) { }
            }

            if (Console.IsOutputRedirected || !AnsiConsole.Profile.Capabilities.Interactive)
            {
                while (!stop.IsCancellationRequested)
                {
                    await Pause(1000);
                    AnsiConsole.WriteLine(string.Join(" | ", master.Slaves.Where(x => x.InputBytes > 0).Select(x => $"#{x.Position} {Inputs(x)}")) + " · " + Caption());
                }
            }
            else
            {
                await AnsiConsole.Live(new Markup("")).StartAsync(async live =>
                {
                    while (!stop.IsCancellationRequested)
                    {
                        var t = new Table().Border(TableBorder.Rounded).AddColumns("#", "Name", "Inputs");
                        foreach (var slave in master.Slaves.Where(x => x.InputBytes > 0))
                            t.AddRow(slave.Position.ToString(CultureInfo.InvariantCulture), Markup.Escape(slave.Name), Inputs(slave));
                        t.Caption(Caption());
                        live.UpdateTarget(t);
                        await Pause(200);
                    }
                });
            }
            await run;
            await sim;
        }

        return 0;
    }
}

internal sealed class EthercatSdoCommand : AsyncCommand<EthercatSdoCommand.Settings>
{
    public sealed class Settings : EthercatSettings
    {
        [CommandArgument(0, "<position>"), Description("Slave position (0 = first).")]
        public int Position { get; init; }

        [CommandArgument(1, "<object>"), Description("Index:sub-index in hex, e.g. 1008:00 or 8000:15.")]
        public string Object { get; init; } = "";

        [CommandOption("--write"), Description("Write this value (hex bytes, little-endian) instead of reading.")]
        public string? Write { get; init; }

        [CommandOption("--allow-write"), Description("Required with --write: objects change how the device behaves.")]
        public bool AllowWrite { get; init; }

        [CommandOption("-y|--yes"), Description("Skip the confirmation.")]
        public bool Yes { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        if (s.Write is not null && !s.AllowWrite)
        {
            Ui.Error("Object writes change how the device behaves. Add --allow-write.");
            return 2;
        }

        var (index, sub) = EthercatUi.ParseObject(s.Object);
        var (master, _) = await EthercatUi.OpenAsync(s, s.Write is not null, ct);
        await using (master)
        {
            var slave = EthercatUi.Slave(master, s.Position);
            await master.RequestStateAsync(EcatState.PreOp, ct);
            if (s.Write is { } hex)
            {
                var value = Convert.FromHexString(new string(hex.Where(Uri.IsHexDigit).ToArray()));
                if (!s.Yes && !s.Sim && !AnsiConsole.Confirm($"Write {Convert.ToHexString(value)} to 0x{index:X4}:{sub:X2} on {Markup.Escape(slave.Name)}?", false)) return 1;
                await master.SdoWriteAsync(slave, index, sub, value, ct);
                Ui.Success($"0x{index:X4}:{sub:X2} written.");
            }

            var data = await master.SdoReadAsync(slave, index, sub, ct);
            var printable = data.Length > 4 && data.All(b => b is >= 0x20 and < 0x7F);
            Ui.Success($"{Markup.Escape(slave.Name)} 0x{index:X4}:{sub:X2} = {Convert.ToHexString(data)}" + (printable
                ? $" \"{Markup.Escape(System.Text.Encoding.ASCII.GetString(data))}\""
                : data.Length is 1 or 2 or 4 ? $" ({(data.Length == 1 ? data[0] : data.Length == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(data) : BinaryPrimitives.ReadUInt32LittleEndian(data))})" : ""));
        }

        return 0;
    }
}

internal sealed class EthercatSetCommand : AsyncCommand<EthercatSetCommand.Settings>
{
    public sealed class Settings : EthercatSettings
    {
        [CommandArgument(0, "<position>"), Description("Position of the output terminal.")]
        public int Position { get; init; }

        [CommandArgument(1, "<bit>"), Description("Output bit (0 = first channel).")]
        public int Bit { get; init; }

        [CommandArgument(2, "<on|off>"), Description("State.")]
        public string Value { get; init; } = "";

        [CommandOption("--seconds"), Description("Hold the output this long in OP, then go back to SAFE-OP (default 3).")]
        public double Seconds { get; init; } = 3;

        [CommandOption("--allow-write"), Description("Required: outputs move machines.")]
        public bool AllowWrite { get; init; }

        [CommandOption("-y|--yes"), Description("Skip the confirmation.")]
        public bool Yes { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        if (!s.AllowWrite)
        {
            Ui.Error("Outputs move machines. Add --allow-write.");
            return 2;
        }

        var on = s.Value.ToLowerInvariant() is "on" or "1" or "true";
        var (master, rack) = await EthercatUi.OpenAsync(s, true, ct);
        await using (master)
        {
            var slave = EthercatUi.Slave(master, s.Position);
            if (!s.Yes && !s.Sim && !AnsiConsole.Confirm($"Bring the line to OP and switch output {s.Bit} of {Markup.Escape(slave.Name)} {(on ? "on" : "off")} for {s.Seconds:0.#} s?", false)) return 1;
            await master.RequestStateAsync(EcatState.Op, ct);
            master.SetOutputBit(slave, s.Bit, on);
            var until = DateTime.UtcNow.AddSeconds(s.Seconds);
            while (DateTime.UtcNow < until && !ct.IsCancellationRequested)
            {
                rack?.Step(0.01);
                await master.ExchangeAsync(ct);
                await Task.Delay(10, ct);
            }

            var inputs = master.Slaves.Where(x => x.InputBytes is > 0 and <= 2).Select(x => $"{x.Name}: {EthercatUi.Bits(master.InputsOf(x))}");
            Ui.Success($"Output {s.Bit} of {Markup.Escape(slave.Name)} was {(on ? "on" : "off")} for {s.Seconds:0.#} s. {Markup.Escape(string.Join(" · ", inputs))}");
            master.SetOutputBit(slave, s.Bit, false);
            await master.ExchangeAsync(ct);
            await master.RequestStateAsync(EcatState.SafeOp, ct);
            Ui.Warn("Outputs cleared; the line is back in SAFE-OP.");
        }

        return 0;
    }
}

internal sealed class EthercatDemoCommand : AsyncCommand<EthercatDemoCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--allow-write"), Description("Also go to OP and run the simulated conveyor.")]
        public bool AllowWrite { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        var (master, rack) = await EthercatUi.OpenAsync(new EthercatSettings { Sim = true }, s.AllowWrite, ct);
        await using (master)
        {
            AnsiConsole.Write(EthercatUi.Slaves(master));
            await master.RequestStateAsync(EcatState.SafeOp, ct);
            rack!.Step(0.1);
            await master.ExchangeAsync(ct);
            Ui.Success($"SAFE-OP: DI8 {EthercatUi.Bits(master.InputsOf(master.Slaves[1]))} · AI4 {HexDump.ToHex(master.InputsOf(master.Slaves[3]))} · wkc {master.Statistics.LastWorkingCounter}");
            var name = await master.SdoReadAsync(master.Slaves[3], 0x1008, 0, ct);
            Ui.Success($"CoE 0x1008 of slave 3: \"{Markup.Escape(System.Text.Encoding.ASCII.GetString(name))}\"");
            if (!s.AllowWrite)
            {
                Ui.Warn("Read-only: the line stays in SAFE-OP. Add --allow-write to go to OP and start the conveyor.");
                return 0;
            }

            await master.RequestStateAsync(EcatState.Op, ct);
            master.SetOutputBit(master.Slaves[2], 0, true);
            master.SetOutputBit(master.Slaves[2], 2, true);
            for (var i = 0; i < 300; i++)
            {
                rack.Step(0.01);
                await master.ExchangeAsync(ct);
            }

            var ai = master.InputsOf(master.Slaves[3]);
            Ui.Success(string.Create(CultureInfo.InvariantCulture, $"OP: motor on · feedback {(master.InputsOf(master.Slaves[1])[0] & 1) == 1} · belt {BinaryPrimitives.ReadInt16LittleEndian(ai[6..]) / 100.0:0.0} m/min · current {BinaryPrimitives.ReadInt16LittleEndian(ai) / 100.0:0.00} A · {rack.Parts} part(s)"));
            Ui.Success($"{master.Statistics.Cycles} cycles, {master.Statistics.WorkingCounterErrors} working-counter errors.");
        }

        return 0;
    }
}

internal sealed class EthercatDecodeCommand : Command<EthercatDecodeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<hex>"), Description("An Ethernet frame with EtherType 0x88A4, or the EtherCAT payload alone.")]
        public string Hex { get; init; } = "";
    }

    public override int Execute(CommandContext context, Settings s, CancellationToken ct)
    {
        var bytes = Convert.FromHexString(new string(s.Hex.Where(Uri.IsHexDigit).ToArray()));
        AnsiConsole.Write(Ui.FrameLane(bytes, EcatFrame.DescribeFields(bytes)));
        var ok = bytes.Length >= 14 && BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(12)) == EcatFrame.EtherType
            ? EcatFrame.TryDecode(bytes, out var datagrams, out var error)
            : EcatFrame.TryDecodePayload(bytes, out datagrams, out error);
        if (!ok)
        {
            Ui.Error(Markup.Escape(error ?? "invalid"));
            return 1;
        }

        foreach (var d in datagrams) Ui.Success(Markup.Escape(d.ToString()));
        return 0;
    }
}
