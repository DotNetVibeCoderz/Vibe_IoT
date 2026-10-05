using System.ComponentModel;
using System.Globalization;
using System.Net;
using IoTCom.Net.Protocols.Uds;
using IoTCom.Net.Transport.Can;
using IoTCom.Net.Transports;
using Spectre.Console;
using Spectre.Console.Cli;

namespace IoTCom.Net.Cli.Commands;

/// <summary>Opens <c>--can</c> targets; <c>sim</c> starts an in-process ECU simulator on a private virtual bus.</summary>
internal sealed class CanTarget : IAsyncDisposable
{
    private CanTarget(ICanBus bus, EcuSimulator? simulator)
    {
        Bus = bus;
        Simulator = simulator;
    }

    public ICanBus Bus { get; }

    public EcuSimulator? Simulator { get; }

    public static async Task<CanTarget> OpenAsync(string uri, int bitrate, bool fd, CancellationToken ct)
    {
        if (uri.Equals("sim", StringComparison.OrdinalIgnoreCase))
        {
            var net = new VirtualCanNetwork("sim");
            var ecu = EcuSimulator.Create(net.CreateNode(o => o.Fd = fd));
            await ecu.StartAsync(ct);
            var tester = net.CreateNode(o => o.Fd = fd);
            await tester.ConnectAsync(ct);
            return new CanTarget(tester, ecu);
        }
        var bus = await CanBus.OpenAsync(uri, o =>
        {
            o.Bitrate = bitrate;
            o.Fd = fd;
        }, ct);
        return new CanTarget(bus, null);
    }

    public async ValueTask DisposeAsync()
    {
        if (Simulator is not null) await Simulator.DisposeAsync();
        await Bus.DisposeAsync();
    }

    public static uint ParseId(string text) =>
        uint.Parse(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}

internal class CanSettings : CommandSettings
{
    [CommandOption("-c|--can"), Description("Interface: socketcan:can0, slcan:COM5, slcan-tcp:host:port, virtual:name, or sim (built-in ECU simulator).")]
    public string Can { get; init; } = "sim";

    [CommandOption("-b|--bitrate"), Description("Nominal bit rate for slcan adapters (default 500000).")]
    public int Bitrate { get; init; } = 500_000;

    [CommandOption("--fd"), Description("Enable CAN FD.")]
    public bool Fd { get; init; }
}

internal sealed class CanListCommand : Command<CanListCommand.Settings>
{
    public sealed class Settings : CommandSettings;

    public override int Execute(CommandContext context, Settings settings, CancellationToken ct)
    {
        var table = new Table().Border(TableBorder.Rounded).AddColumn("Interface").AddColumn("URI");
        foreach (var name in SocketCanBus.ListInterfaces()) table.AddRow(name, $"socketcan:{name}");
        foreach (var port in IoTCom.Net.Transport.Serial.SerialTransport.GetPortNames().Order(StringComparer.Ordinal)) table.AddRow($"{port} (slcan adapter?)", $"slcan:{port}");
        table.AddRow("built-in ECU simulator", "sim");
        AnsiConsole.Write(table);
        if (!SocketCanBus.IsSupported) Ui.Warn("SocketCAN is Linux-only; on this OS use an slcan USB adapter (CANable, CANtact, USBtin).");
        return 0;
    }
}

internal sealed class CanDumpCommand : AsyncCommand<CanDumpCommand.Settings>
{
    public sealed class Settings : CanSettings
    {
        [CommandOption("-f|--filter"), Description("id[:mask] in hex, e.g. 7E8 or 7E8:7F8.")]
        public string? Filter { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        await using var target = await CanTarget.OpenAsync(s.Can, s.Bitrate, s.Fd, ct);
        CanFilter? filter = null;
        if (s.Filter is { } f)
        {
            var parts = f.Split(':');
            filter = new CanFilter(CanTarget.ParseId(parts[0]), parts.Length > 1 ? CanTarget.ParseId(parts[1]) : CanFrame.MaxExtendedId);
        }
        using var reader = target.Bus.OpenReader(filter);
        Ui.Success($"Listening on {Markup.Escape(target.Bus.Channel)}{(filter is null ? "" : $" (filter {Markup.Escape(s.Filter!)})")}. Ctrl+C to stop.");
        if (target.Simulator is not null)
        {
            // Make the simulator talk: an OBD request every second.
            _ = Task.Run(async () =>
            {
                await using var obd = ObdClient.Create(target.Bus);
                await obd.ConnectAsync(ct);
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        await obd.ReadPidAsync(ObdPids.EngineRpm, ct);
                        await obd.ReadVinAsync(ct);
                        await Task.Delay(1000, ct);
                    }
                    catch (OperationCanceledException) { }
                }
            }, ct);
        }
        var start = DateTimeOffset.UtcNow;
        try
        {
            await foreach (var frame in reader.ReadAllAsync(ct))
            {
                var id = frame.IsExtended ? $"{frame.Id:X8}" : $"     {frame.Id:X3}";
                var data = frame.IsRemote ? "remote" : string.Join(' ', frame.Data.ToArray().Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
                AnsiConsole.MarkupLine(CultureInfo.InvariantCulture,
                    $"[{Ui.Hex(Ui.Muted)}]{(frame.Timestamp - start).TotalSeconds,9:0.000}[/]  [{Ui.Hex(Ui.CableBlue)}]{id}[/]  [{Ui.Hex(Ui.Amber)}][[{frame.Data.Length,2}]][/]{(frame.IsFd ? " FD" : "   ")}  {data}");
            }
        }
        catch (OperationCanceledException) { }
        return 0;
    }
}

internal sealed class CanSendCommand : AsyncCommand<CanSendCommand.Settings>
{
    public sealed class Settings : CanSettings
    {
        [CommandArgument(0, "<frames>"), Description("Frames in cansend notation, e.g. 123#DEADBEEF 7DF#02010C.")]
        public string[] Frames { get; init; } = [];
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        await using var target = await CanTarget.OpenAsync(s.Can, s.Bitrate, s.Fd, ct);
        foreach (var text in s.Frames)
        {
            var frame = CanFrame.Parse(text);
            await target.Bus.SendAsync(frame, ct);
            Ui.Success($"sent {Markup.Escape(frame.ToString())} on {Markup.Escape(target.Bus.Channel)}");
        }
        return 0;
    }
}

internal sealed class CanSimulateCommand : AsyncCommand<CanSimulateCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-p|--port"), Description("TCP port of the emulated slcan adapter (default 20100).")]
        public int Port { get; init; } = 20100;

        [CommandOption("--quiet"), Description("Do not print every diagnostic exchange.")]
        public bool Quiet { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        var net = new VirtualCanNetwork("simulate");
        await using var ecu = EcuSimulator.Create(net.CreateNode(o => o.Fd = true));
        if (!s.Quiet)
        {
            ecu.RequestHandled += x => AnsiConsole.MarkupLine(
                $"[{Ui.Hex(Ui.Muted)}]{DateTime.Now:HH:mm:ss.fff}[/] {(x.Functional ? "7DF" : "7E0")} [{Ui.Hex(Ui.Amber)}]{Convert.ToHexString(x.Request)}[/] → " +
                (x.Response is null ? $"[{Ui.Hex(Ui.Muted)}](no response)[/]" : x.Response[0] == 0x7F ? $"[{Ui.Hex(Ui.Fault)}]{Convert.ToHexString(x.Response)}[/]" : $"[{Ui.Hex(Ui.LampGreen)}]{Markup.Escape(Shorten(x.Response))}[/]"));
        }
        await ecu.StartAsync(ct);
        await using var listener = new TcpTransportListener(IPAddress.Any, s.Port);
        await listener.StartAsync(ct);
        Ui.Success($"Engine ECU simulator (UDS + OBD-II, VIN {ecu.Options.Vin}) behind an slcan adapter on tcp://0.0.0.0:{s.Port}.");
        Ui.Warn($"Try: iotcom obd live --can slcan-tcp:127.0.0.1:{s.Port}   ·   iotcom uds dtc --can slcan-tcp:127.0.0.1:{s.Port}");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var connection = await listener.AcceptAsync(ct);
                _ = Task.Run(async () =>
                {
                    await using (connection) await SlcanAdapterSimulator.RunAsync(connection, net, ct);
                }, ct);
            }
        }
        catch (OperationCanceledException) { }
        return 0;
    }

    private static string Shorten(byte[] r) => r.Length <= 12 ? Convert.ToHexString(r) : Convert.ToHexString(r, 0, 12) + $"… ({r.Length} B)";
}

internal sealed class UdsReadCommand : AsyncCommand<UdsReadCommand.Settings>
{
    public sealed class Settings : UdsSettings
    {
        [CommandOption("-d|--did"), Description("Data identifiers in hex (default: F190 F187 F189 F18C).")]
        public string[] Dids { get; init; } = [];
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        await using var target = await CanTarget.OpenAsync(s.Can, s.Bitrate, s.Fd, ct);
        await using var uds = s.CreateClient(target.Bus);
        await uds.ConnectAsync(ct);
        var dids = s.Dids.Length == 0 ? [UdsDid.Vin, UdsDid.SparePartNumber, UdsDid.SoftwareVersion, UdsDid.SerialNumber] : s.Dids.Select(d => (ushort)CanTarget.ParseId(d)).ToArray();
        var table = new Table().Border(TableBorder.Rounded).AddColumn("DID").AddColumn("Hex").AddColumn("Text");
        foreach (var did in dids)
        {
            try
            {
                var data = await uds.ReadDataByIdentifierAsync(did, ct);
                var text = new string(data.Select(b => b is >= 0x20 and < 0x7F ? (char)b : '.').ToArray());
                table.AddRow($"0x{did:X4}", Convert.ToHexString(data), Markup.Escape(text));
            }
            catch (UdsNegativeResponseException ex)
            {
                table.AddRow($"0x{did:X4}", $"[{Ui.Hex(Ui.Fault)}]NRC 0x{(byte)ex.ResponseCode:X2}[/]", Markup.Escape(UdsNegativeResponseException.Describe(ex.ResponseCode)));
            }
        }
        AnsiConsole.Write(table);
        return 0;
    }
}

internal class UdsSettings : CanSettings
{
    [CommandOption("--tx"), Description("Request identifier (default 7E0).")]
    public string Tx { get; init; } = "7E0";

    [CommandOption("--rx"), Description("Response identifier (default 7E8).")]
    public string Rx { get; init; } = "7E8";

    [CommandOption("--allow-write"), Description("Required for state-changing services (clear DTCs, reset, write).")]
    public bool AllowWrite { get; init; }

    public UdsClient CreateClient(ICanBus bus) => UdsClient.Create(bus, o =>
    {
        o.RequestId = CanTarget.ParseId(Tx);
        o.ResponseId = CanTarget.ParseId(Rx);
        o.ReadOnly = !AllowWrite;
        o.IsoTp = t => t.Fd = Fd;
    });
}

internal sealed class UdsDtcCommand : AsyncCommand<UdsDtcCommand.Settings>
{
    public sealed class Settings : UdsSettings
    {
        [CommandOption("--clear"), Description("Clear all DTCs afterwards (needs --allow-write).")]
        public bool Clear { get; init; }

        [CommandOption("-y|--yes"), Description("Skip the confirmation prompt.")]
        public bool Yes { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        await using var target = await CanTarget.OpenAsync(s.Can, s.Bitrate, s.Fd, ct);
        await using var uds = s.CreateClient(target.Bus);
        await uds.ConnectAsync(ct);
        var dtcs = await uds.ReadDtcsAsync(ct: ct);
        RenderDtcs(dtcs);
        if (!s.Clear) return 0;
        if (!s.AllowWrite)
        {
            Ui.Warn("Clearing DTCs changes the ECU's state. Re-run with [bold]--allow-write[/].");
            return 2;
        }
        if (!s.Yes && !AnsiConsole.Confirm($"Clear all DTCs on {Markup.Escape(target.Bus.Channel)} ({s.Tx}→{s.Rx})?", false)) return 1;
        await uds.ClearDtcsAsync(ct: ct);
        Ui.Success("DTCs cleared.");
        return 0;
    }

    internal static void RenderDtcs(IReadOnlyList<Dtc> dtcs)
    {
        if (dtcs.Count == 0)
        {
            Ui.Success("No DTCs stored.");
            return;
        }
        var table = new Table().Border(TableBorder.Rounded).AddColumn("DTC").AddColumn("Status").AddColumn("Flags");
        foreach (var d in dtcs)
        {
            var colour = d.Status.HasFlag(DtcStatus.Confirmed) ? Ui.Fault : Ui.Amber;
            table.AddRow($"[{Ui.Hex(colour)}]{d}[/]", $"0x{(byte)d.Status:X2}", Markup.Escape(d.Status.ToString()));
        }
        AnsiConsole.Write(table);
    }
}

internal sealed class UdsRawCommand : AsyncCommand<UdsRawCommand.Settings>
{
    public sealed class Settings : UdsSettings
    {
        [CommandArgument(0, "<request>"), Description("Request bytes in hex, e.g. 22F190 or \"10 03\".")]
        public string Request { get; init; } = "";
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        var request = Convert.FromHexString(s.Request.Replace(" ", "", StringComparison.Ordinal));
        await using var target = await CanTarget.OpenAsync(s.Can, s.Bitrate, s.Fd, ct);
        await using var uds = s.CreateClient(target.Bus);
        await uds.ConnectAsync(ct);
        AnsiConsole.Write(Ui.FrameLane(request, UdsAnatomy.Describe(request)));
        try
        {
            var response = await uds.RequestAsync(request, ct);
            if (response is null) Ui.Success("No response (suppressed).");
            else AnsiConsole.Write(Ui.FrameLane(response, UdsAnatomy.Describe(response)));
            return 0;
        }
        catch (UdsNegativeResponseException ex)
        {
            Ui.Error(Markup.Escape(ex.Message));
            return 2;
        }
    }
}

internal sealed class ObdLiveCommand : AsyncCommand<ObdLiveCommand.Settings>
{
    public sealed class Settings : CanSettings
    {
        [CommandOption("-w|--watch"), Description("Refresh every N ms.")]
        public int? Watch { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        await using var target = await CanTarget.OpenAsync(s.Can, s.Bitrate, s.Fd, ct);
        await using var obd = ObdClient.Create(target.Bus);
        await obd.ConnectAsync(ct);
        var supported = await obd.GetSupportedPidsAsync(ct);
        var pids = ObdPids.All.Values.Where(p => supported.Contains(p.Pid)).ToList();
        async Task<Table> ReadAsync()
        {
            var table = new Table().Border(TableBorder.Rounded).Title($"OBD-II · {Markup.Escape(target.Bus.Channel)}")
                .AddColumn("PID").AddColumn("Parameter").AddColumn(new TableColumn("Value").RightAligned()).AddColumn("Unit");
            foreach (var p in pids)
            {
                var v = await obd.ReadPidAsync(p, ct);
                table.AddRow($"[{Ui.Hex(Ui.Muted)}]{p.Pid:X2}[/]", p.Name, $"[bold]{v.Value.ToString("0.#", CultureInfo.InvariantCulture)}[/]", p.Unit);
            }
            return table;
        }
        if (s.Watch is not { } ms)
        {
            AnsiConsole.Write(await ReadAsync());
            return 0;
        }
        await AnsiConsole.Live(await ReadAsync()).StartAsync(async live =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(ms, ct);
                    live.UpdateTarget(await ReadAsync());
                }
            }
            catch (OperationCanceledException) { }
        });
        return 0;
    }
}

internal sealed class ObdVinCommand : AsyncCommand<CanSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, CanSettings s, CancellationToken ct)
    {
        await using var target = await CanTarget.OpenAsync(s.Can, s.Bitrate, s.Fd, ct);
        await using var obd = ObdClient.Create(target.Bus);
        await obd.ConnectAsync(ct);
        Ui.Success($"VIN [bold]{Markup.Escape(await obd.ReadVinAsync(ct))}[/]");
        return 0;
    }
}

internal sealed class ObdDtcCommand : AsyncCommand<ObdDtcCommand.Settings>
{
    public sealed class Settings : CanSettings
    {
        [CommandOption("--clear"), Description("Mode 04: clear DTCs and turn the MIL off (needs --allow-write).")]
        public bool Clear { get; init; }

        [CommandOption("--allow-write"), Description("Confirms you intend to change the vehicle's state.")]
        public bool AllowWrite { get; init; }

        [CommandOption("-y|--yes")]
        public bool Yes { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        await using var target = await CanTarget.OpenAsync(s.Can, s.Bitrate, s.Fd, ct);
        await using var obd = ObdClient.Create(target.Bus, o => o.ReadOnly = !s.AllowWrite);
        await obd.ConnectAsync(ct);
        var stored = await obd.ReadDtcsAsync(ct);
        var pending = await obd.ReadPendingDtcsAsync(ct);
        UdsDtcCommand.RenderDtcs([.. stored, .. pending]);
        if (!s.Clear) return 0;
        if (!s.AllowWrite)
        {
            Ui.Warn("Mode 04 erases DTCs and freeze frames. Re-run with [bold]--allow-write[/].");
            return 2;
        }
        if (!s.Yes && !AnsiConsole.Confirm("Clear DTCs and turn the MIL off?", false)) return 1;
        await obd.ClearDtcsAsync(ct);
        Ui.Success("DTCs cleared.");
        return 0;
    }
}
