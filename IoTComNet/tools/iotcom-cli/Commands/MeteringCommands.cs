using System.ComponentModel;
using System.Globalization;
using System.Net;
using IoTCom.Net.Protocols.Dlms;
using IoTCom.Net.Protocols.MBus;
using IoTCom.Net.Transport.Serial;
using IoTCom.Net.Transports;
using Spectre.Console;
using Spectre.Console.Cli;

namespace IoTCom.Net.Cli.Commands;

/// <summary>Prints DLMS or M-Bus frames with the frame lane.</summary>
internal sealed class MeteringFramePrinter(bool mbus) : ITrafficTap
{
    public void OnFrame(in TrafficFrame frame)
    {
        AnsiConsole.MarkupLine($"[{Ui.Hex(frame.Direction == FrameDirection.Outbound ? Ui.Amber : Ui.CableBlue)}]{(frame.Direction == FrameDirection.Outbound ? "TX" : "RX")}[/] {Markup.Escape(frame.Summary ?? "")}");
        AnsiConsole.Write(Ui.FrameLane(frame.Data.Span, mbus ? MBusAnatomy.Describe(frame.Data.Span) : DlmsAnatomy.Describe(frame.Data.Span)));
    }
}

internal class DlmsSettings : CommandSettings
{
    [CommandOption("--serial"), Description("Serial port of an optical probe or RS-485 adapter (COM3, /dev/ttyUSB0); HDLC.")]
    public string? Serial { get; init; }

    [CommandOption("--baud"), Description("Serial baud rate (default 9600).")]
    public int Baud { get; init; } = 9600;

    [CommandOption("-h|--host"), Description("TCP host of a meter or gateway.")]
    public string? Host { get; init; }

    [CommandOption("-p|--port"), Description("TCP port (default 4059).")]
    public int Port { get; init; } = DlmsWrapper.Port;

    [CommandOption("--hdlc"), Description("HDLC over TCP (gateways); TCP defaults to the wrapper.")]
    public bool Hdlc { get; init; }

    [CommandOption("--sim"), Description("Read the built-in simulated meter (no hardware).")]
    public bool Simulate { get; init; }

    [CommandOption("--physical"), Description("HDLC lower (physical) address of the meter (default 17).")]
    public ushort Physical { get; init; } = 17;

    [CommandOption("--password"), Description("Management client with low-level security (LLS).")]
    public string? Password { get; init; }

    [CommandOption("--hls"), Description("Management client with HLS-GMAC: system title, EK and AK as hex 'ST:EK:AK'.")]
    public string? Hls { get; init; }

    [CommandOption("--frames"), Description("Print every frame as a frame lane.")]
    public bool Frames { get; init; }

    public async Task<(DlmsClient Client, IAsyncDisposable? Meter)> ConnectAsync(bool allowWrite, CancellationToken ct)
    {
        IAsyncDisposable? meter = null;
        InMemoryTransportListener? listener = null;
        if (Simulate)
        {
            listener = new InMemoryTransportListener("meter");
            var server = DlmsServer.Create(o =>
            {
                o.ListenInMemory(listener);
                o.Security = DlmsSecurityKeys.FromHex("49534B0000000017", "000102030405060708090A0B0C0D0E0F", "D0D1D2D3D4D5D6D7D8D9DADBDCDDDEDF");
            });
            var sim = new DlmsMeterSimulator(server);
            await server.StartAsync(ct);
            sim.Start();
            meter = new Pair(sim, server);
        }

        var client = DlmsClient.Create(o =>
        {
            if (listener is not null) o.UseInMemory(listener);
            else if (Serial is not null) o.UseSerial(Serial, Baud);
            else o.UseTcp(Host ?? throw new InvalidOperationException("Give --serial, --host or --sim."), Port);
            o.Framing = Serial is not null || Simulate || Hdlc ? DlmsFraming.Hdlc : DlmsFraming.Wrapper;
            o.ServerPhysicalAddress = Physical;
            o.ReadOnly = !allowWrite;
            if (Password is not null) o.WithPassword(Password);
            if (Hls is { } hls)
            {
                var p = hls.Split(':');
                if (p.Length != 3) throw new InvalidOperationException("--hls expects 'SYSTEMTITLE:EK:AK' in hex.");
                o.WithHighSecurity(DlmsSecurityKeys.FromHex(p[0], p[1], p[2]));
            }
        });
        if (Frames) client.AddTap(new MeteringFramePrinter(false));
        await client.ConnectAsync(ct);
        return (client, meter);
    }

    private sealed class Pair(DlmsMeterSimulator sim, DlmsServer server) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await sim.DisposeAsync();
            await server.DisposeAsync();
        }
    }
}

internal sealed class DlmsReadCommand : AsyncCommand<DlmsReadCommand.Settings>
{
    public sealed class Settings : DlmsSettings
    {
        [CommandArgument(0, "[obis]"), Description("OBIS codes of registers (default: energy, voltage, current, power, frequency).")]
        public string[] Obis { get; init; } = [];
    }

    private static readonly string[] Defaults = ["1.0.1.8.0.255", "1.0.1.8.1.255", "1.0.1.8.2.255", "1.0.2.8.0.255", "1.0.1.7.0.255", "1.0.2.7.0.255",
        "1.0.32.7.0.255", "1.0.52.7.0.255", "1.0.72.7.0.255", "1.0.31.7.0.255", "1.0.13.7.0.255", "1.0.14.7.0.255"];

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        var (client, meter) = await s.ConnectAsync(false, ct);
        await using var _c = client;
        await using var _m = meter;
        var table = new Table().Border(TableBorder.Rounded).AddColumns("OBIS", "Quantity", "Value");
        table.AddRow("0.0.1.0.0.255", "Clock", (await client.ReadClockAsync(ct)).ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
        foreach (var text in s.Obis.Length > 0 ? s.Obis : Defaults)
        {
            var obis = ObisCode.Parse(text);
            try
            {
                var v = await client.ReadRegisterAsync(obis, ct);
                table.AddRow(obis.ToString(), Markup.Escape(obis.Description ?? ""), $"[bold]{Markup.Escape(v.ToString())}[/]");
            }
            catch (DlmsException ex)
            {
                table.AddRow(obis.ToString(), Markup.Escape(obis.Description ?? ""), $"[{Ui.Hex(Ui.Fault)}]{Markup.Escape(ex.Result?.ToString() ?? ex.Message)}[/]");
            }
        }

        AnsiConsole.Write(table);
        return 0;
    }
}

internal sealed class DlmsObjectsCommand : AsyncCommand<DlmsSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, DlmsSettings s, CancellationToken ct)
    {
        var (client, meter) = await s.ConnectAsync(false, ct);
        await using var _c = client;
        await using var _m = meter;
        var objects = await client.ReadObjectListAsync(ct);
        var table = new Table().Border(TableBorder.Rounded).Title($"{objects.Count} objects in the association").AddColumns("OBIS", "Class", "Description");
        foreach (var o in objects.OrderBy(o => o.LogicalName.ToString(), StringComparer.Ordinal))
            table.AddRow(o.LogicalName.ToString(), CosemClass.Name(o.ClassId), Markup.Escape(o.Description ?? ""));
        AnsiConsole.Write(table);
        return 0;
    }
}

internal sealed class DlmsProfileCommand : AsyncCommand<DlmsProfileCommand.Settings>
{
    public sealed class Settings : DlmsSettings
    {
        [CommandArgument(0, "[obis]"), Description("Profile OBIS code (default 1.0.99.1.0.255, load profile).")]
        public string Obis { get; init; } = "1.0.99.1.0.255";

        [CommandOption("--hours"), Description("Rows of the last N hours (selective access by range; default 6).")]
        public double Hours { get; init; } = 6;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        var (client, meter) = await s.ConnectAsync(false, ct);
        await using var _c = client;
        await using var _m = meter;
        var now = await client.ReadClockAsync(ct);
        var profile = await client.ReadProfileAsync(ObisCode.Parse(s.Obis), now.AddHours(-s.Hours), now, ct);
        var table = new Table().Border(TableBorder.Rounded).Title($"{profile.Rows.Count} rows · last {s.Hours:0.#} h");
        foreach (var c in profile.Columns) table.AddColumn(Markup.Escape(c.LogicalName.Description ?? c.LogicalName.ToString()));
        foreach (var row in profile.Rows) table.AddRow(row.Select(v => Markup.Escape(v.ToString())).ToArray());
        AnsiConsole.Write(table);
        return 0;
    }
}

internal sealed class DlmsRelayCommand : AsyncCommand<DlmsRelayCommand.Settings>
{
    public sealed class Settings : DlmsSettings
    {
        [CommandArgument(0, "<state>"), Description("on (reconnect) or off (disconnect).")]
        public string State { get; init; } = "";

        [CommandOption("--allow-write"), Description("Required: confirms you intend to switch the supply.")]
        public bool AllowWrite { get; init; }

        [CommandOption("-y|--yes"), Description("Skip the interactive confirmation.")]
        public bool Yes { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        var connect = s.State.ToLowerInvariant() switch { "on" => true, "off" => false, _ => throw new InvalidOperationException("State is 'on' or 'off'.") };
        if (!s.AllowWrite)
        {
            Ui.Warn("Switching the relay changes the customer's supply. Re-run with [bold]--allow-write[/].");
            return 2;
        }

        if (!s.Yes && !s.Simulate && !AnsiConsole.Confirm($"{(connect ? "Reconnect" : "Disconnect")} the supply?", false)) return 1;
        var (client, meter) = await s.ConnectAsync(true, ct);
        await using var _c = client;
        await using var _m = meter;
        var relay = ObisCode.Parse("0.0.96.3.10.255");
        await client.ActionAsync(CosemClass.DisconnectControl, relay, connect ? (sbyte)2 : (sbyte)1, ct: ct);
        var state = (await client.GetAsync(CosemClass.DisconnectControl, relay, 2, ct: ct)).AsBoolean();
        Ui.Success($"Relay {(state ? "connected" : "disconnected")}.");
        return 0;
    }
}

internal sealed class DlmsSimulateCommand : AsyncCommand<DlmsSimulateCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-p|--port"), Description("TCP port (default 4059).")]
        public int Port { get; init; } = DlmsWrapper.Port;

        [CommandOption("--hdlc"), Description("HDLC over TCP instead of the wrapper.")]
        public bool Hdlc { get; init; }

        [CommandOption("--frames"), Description("Print every frame as a frame lane.")]
        public bool Frames { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        await using var server = DlmsServer.Create(o =>
        {
            o.UseTcp(IPAddress.Any, s.Port);
            o.Framing = s.Hdlc ? DlmsFraming.Hdlc : DlmsFraming.Wrapper;
            o.Security = DlmsSecurityKeys.FromHex("49534B0000000017", "000102030405060708090A0B0C0D0E0F", "D0D1D2D3D4D5D6D7D8D9DADBDCDDDEDF");
        });
        if (s.Frames) server.AddTap(new MeteringFramePrinter(false));
        server.RequestHandled += (_, e) => AnsiConsole.MarkupLine($"[{Ui.Hex(Ui.Muted)}]client {e.Client}[/] {Markup.Escape(e.Request)} → {Markup.Escape(e.Response)}");
        await using var meter = new DlmsMeterSimulator(server);
        await server.StartAsync(ct);
        meter.Start();
        Ui.Success($"Simulated smart meter on tcp://0.0.0.0:{s.Port} ({(s.Hdlc ? "HDLC" : "wrapper")}), {server.Objects.Count} objects. Ctrl+C to stop.");
        Ui.Warn($"Public client: iotcom dlms read -h 127.0.0.1 -p {s.Port}{(s.Hdlc ? " --hdlc" : "")} · management: --password 12345678 (test keys for HLS are documented).");
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException) { }
        return 0;
    }
}

internal class MBusSettings : CommandSettings
{
    [CommandOption("--serial"), Description("Serial port of the M-Bus level converter (2400 8E1 by default).")]
    public string? Serial { get; init; }

    [CommandOption("--baud"), Description("Baud rate (default 2400).")]
    public int Baud { get; init; } = 2400;

    [CommandOption("-h|--host"), Description("TCP host of an M-Bus gateway.")]
    public string? Host { get; init; }

    [CommandOption("-p|--port"), Description("TCP port of the gateway (default 10001).")]
    public int Port { get; init; } = 10001;

    [CommandOption("--sim"), Description("Use the built-in simulated segment (heat, water and electricity meters).")]
    public bool Simulate { get; init; }

    [CommandOption("--timeout"), Description("Response timeout in ms (default 600).")]
    public int Timeout { get; init; } = 600;

    [CommandOption("--frames"), Description("Print every frame as a frame lane.")]
    public bool Frames { get; init; }

    public async Task<(MBusMaster Master, IAsyncDisposable? Segment)> ConnectAsync(CancellationToken ct)
    {
        MBusSlaveSimulator? segment = null;
        InMemoryTransportListener? listener = null;
        if (Simulate)
        {
            listener = new InMemoryTransportListener("mbus");
            segment = MBusSlaveSimulator.Create(o => o.ListenInMemory(listener)).AddDefaultDevices();
            await segment.StartAsync(ct);
        }

        var master = MBusMaster.Create(o =>
        {
            if (listener is not null) o.UseInMemory(listener);
            else if (Serial is not null) o.UseSerial(Serial, Baud, System.IO.Ports.Parity.Even);
            else o.UseTcp(Host ?? throw new InvalidOperationException("Give --serial, --host or --sim."), Port);
            o.ResponseTimeout = TimeSpan.FromMilliseconds(Simulate ? Math.Min(Timeout, 200) : Timeout);
        });
        if (Frames) master.AddTap(new MeteringFramePrinter(true));
        await master.ConnectAsync(ct);
        return (master, segment);
    }

    public static void Print(MBusTelegram t)
    {
        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(t.SecondaryAddress)}[/]  {Markup.Escape(t.MediumName)} · address {t.Address} · access #{t.AccessNumber}{(t.Status != 0 ? $" · [{Ui.Hex(Ui.Amber)}]status 0x{t.Status:X2}[/]" : "")}");
        var table = new Table().Border(TableBorder.Rounded).AddColumns("Quantity", "Value", "Storage", "Tariff", "Function");
        foreach (var r in t.Records)
            table.AddRow(Markup.Escape(r.Quantity), $"[bold]{Markup.Escape(r.FormattedValue)}[/]", r.StorageNumber.ToString(CultureInfo.InvariantCulture),
                r.Tariff.ToString(CultureInfo.InvariantCulture), r.Function.ToString());
        AnsiConsole.Write(table);
    }
}

internal sealed class MBusScanCommand : AsyncCommand<MBusScanCommand.Settings>
{
    public sealed class Settings : MBusSettings
    {
        [CommandOption("--from"), Description("First primary address (default 0).")]
        public byte From { get; init; }

        [CommandOption("--to"), Description("Last primary address (default 250).")]
        public byte To { get; init; } = 250;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        var (master, segment) = await s.ConnectAsync(ct);
        await using var _m = master;
        await using var _s = segment;
        IReadOnlyList<byte> found = [];
        await AnsiConsole.Progress().StartAsync(async p =>
        {
            var task = p.AddTask($"Scanning {s.From}–{s.To}", maxValue: s.To - s.From + 1);
            found = await master.ScanAsync(s.From, s.To, new Progress<byte>(a => task.Value = a - s.From + 1), ct);
        });
        foreach (var a in found)
        {
            var t = await master.ReadAsync(a, ct);
            AnsiConsole.MarkupLine($"[{Ui.Hex(Ui.LampGreen)}]●[/] {a,3}  {Markup.Escape(t.SecondaryAddress)}  {Markup.Escape(t.MediumName)}, {t.Records.Count} records");
        }

        Ui.Success($"{found.Count} slave(s) answered.");
        return 0;
    }
}

internal sealed class MBusReadCommand : AsyncCommand<MBusReadCommand.Settings>
{
    public sealed class Settings : MBusSettings
    {
        [CommandArgument(0, "<address>"), Description("Primary address (0–250) or 8-digit secondary ID (wildcards F).")]
        public string Address { get; init; } = "";
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        var (master, segment) = await s.ConnectAsync(ct);
        await using var _m = master;
        await using var _s = segment;
        var t = s.Address.Length == 8 ? await master.ReadSecondaryAsync(s.Address, ct) : await master.ReadAsync(byte.Parse(s.Address, CultureInfo.InvariantCulture), ct);
        MBusSettings.Print(t);
        return 0;
    }
}

internal sealed class MBusDecodeCommand : Command<MBusDecodeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<hex>"), Description("A frame in hex (spaces allowed).")]
        public string Hex { get; init; } = "";
    }

    public override int Execute(CommandContext context, Settings s, CancellationToken ct)
    {
        var bytes = Convert.FromHexString(new string(s.Hex.Where(Uri.IsHexDigit).ToArray()));
        AnsiConsole.Write(Ui.FrameLane(bytes, MBusAnatomy.Describe(bytes)));
        if (MBusFrame.TryRead(bytes, out var frame, out _, out var error) != MBusFrame.ReadStatus.Frame)
        {
            Ui.Error(Markup.Escape(error ?? "incomplete frame"));
            return 1;
        }

        if (frame!.Ci == MBusCi.VariableLong) MBusSettings.Print(MBusTelegram.FromFrame(frame));
        else AnsiConsole.MarkupLine(Markup.Escape(frame.ToString()));
        return 0;
    }
}

internal sealed class MBusSimulateCommand : AsyncCommand<MBusSimulateCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-p|--port"), Description("TCP port of the simulated gateway (default 10001).")]
        public int Port { get; init; } = 10001;

        [CommandOption("--frames"), Description("Print every frame as a frame lane.")]
        public bool Frames { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        await using var segment = MBusSlaveSimulator.Create(o => o.UseTcp(IPAddress.Any, s.Port)).AddDefaultDevices();
        if (s.Frames) segment.AddTap(new MeteringFramePrinter(true));
        await segment.StartAsync(ct);
        Ui.Success($"Simulated M-Bus segment on tcp://0.0.0.0:{s.Port}: heat meter (1), water meter (2), electricity meter (3). Ctrl+C to stop.");
        Ui.Warn($"Try: iotcom mbus scan -h 127.0.0.1 -p {s.Port} --to 5");
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException) { }
        return 0;
    }
}
