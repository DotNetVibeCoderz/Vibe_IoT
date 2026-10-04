using System.ComponentModel;
using System.Globalization;
using System.IO.Ports;
using System.Net;
using IoTCom.Net.Native.Modbus;
using IoTCom.Net.Protocols.Modbus;
using IoTCom.Net.Transport.Serial;
using Spectre.Console;
using Spectre.Console.Cli;

namespace IoTCom.Net.Cli.Commands;

internal class ModbusConnectionSettings : CommandSettings
{
    [CommandOption("-h|--host"), Description("TCP host (default 127.0.0.1).")]
    public string Host { get; init; } = "127.0.0.1";

    [CommandOption("-p|--port"), Description("TCP port (default 502).")]
    public int Port { get; init; } = 502;

    [CommandOption("--serial"), Description("Serial port instead of TCP (COM3, /dev/ttyUSB0). Implies --rtu unless --ascii.")]
    public string? Serial { get; init; }

    [CommandOption("--baud"), Description("Serial baud rate (default 9600).")]
    public int Baud { get; init; } = 9600;

    [CommandOption("--parity"), Description("Serial parity: none, even, odd (default even, per Modbus spec).")]
    public string Parity { get; init; } = "even";

    [CommandOption("--rtu"), Description("RTU framing (also for RTU-over-TCP gateways).")]
    public bool Rtu { get; init; }

    [CommandOption("--ascii"), Description("ASCII framing.")]
    public bool Ascii { get; init; }

    [CommandOption("-u|--unit"), Description("Unit id (default 1).")]
    public byte Unit { get; init; } = 1;

    [CommandOption("--timeout"), Description("Response timeout in ms (default 1000).")]
    public int TimeoutMs { get; init; } = 1000;

    [CommandOption("--native"), Description("Use the Rust protocol engine (IoTCom.Net.Native.Modbus).")]
    public bool Native { get; init; }

    public ModbusFramingMode Mode => Ascii ? ModbusFramingMode.Ascii : (Rtu || Serial is not null) ? ModbusFramingMode.Rtu : ModbusFramingMode.Tcp;

    public IModbusClient CreateClient(bool readOnly)
    {
        void Configure(ModbusClientOptions o)
        {
            if (Serial is not null)
                o.UseSerial(Serial, Baud, Parity.ToLowerInvariant() switch { "none" => System.IO.Ports.Parity.None, "odd" => System.IO.Ports.Parity.Odd, _ => System.IO.Ports.Parity.Even });
            else
                o.UseTcp(Host, Port);
            o.Framing = Mode;
            o.WithUnitId(Unit).WithTimeout(TimeSpan.FromMilliseconds(TimeoutMs)).WithReconnect(ReconnectPolicy.None);
            if (readOnly) o.AsReadOnly();
        }
        return Native ? NativeModbusClient.Create(Configure) : ModbusClient.Create(Configure);
    }

    public string Target => Serial is not null ? $"{Serial} @ {Baud}" : $"{Host}:{Port}";
}

internal sealed class ModbusReadCommand : AsyncCommand<ModbusReadCommand.Settings>
{
    public sealed class Settings : ModbusConnectionSettings
    {
        [CommandOption("-t|--table"), Description("holding (default), input, coils, discrete.")]
        public string Table { get; init; } = "holding";

        [CommandOption("-a|--address"), Description("Start address (0-based).")]
        public ushort Address { get; init; }

        [CommandOption("-c|--count"), Description("Quantity (default 10).")]
        public ushort Count { get; init; } = 10;

        [CommandOption("--watch"), Description("Poll every N milliseconds until Ctrl+C.")]
        public int? Watch { get; init; }

        [CommandOption("--float"), Description("Also show register pairs as float32 (big-endian).")]
        public bool Float { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        await using var client = s.CreateClient(readOnly: true);
        var table = s.Table.ToLowerInvariant();
        do
        {
            var t = new Table().Border(TableBorder.Rounded).BorderColor(Ui.Muted)
                .Title($"[bold]{Markup.Escape(table)}[/] [{Ui.Hex(Ui.Muted)}]{Markup.Escape(s.Target)} · unit {s.Unit} · {s.Mode.ToString().ToUpperInvariant()}{(s.Native ? " · rust engine" : "")}[/]")
                .AddColumn("[bold]Address[/]").AddColumn("[bold]Value[/]").AddColumn("[bold]Hex[/]");
            if (s.Float) t.AddColumn("[bold]Float32[/]");

            if (table is "coils" or "discrete")
            {
                var bits = table == "coils" ? await client.ReadCoilsAsync(s.Address, s.Count, ct: ct) : await client.ReadDiscreteInputsAsync(s.Address, s.Count, ct: ct);
                for (var i = 0; i < bits.Length; i++)
                {
                    var row = new List<string> { (s.Address + i).ToString(CultureInfo.InvariantCulture), bits[i] ? $"[{Ui.Hex(Ui.LampGreen)}]● ON[/]" : $"[{Ui.Hex(Ui.Muted)}]○ off[/]", "" };
                    if (s.Float) row.Add("");
                    t.AddRow([.. row]);
                }
            }
            else
            {
                var regs = table == "input" ? await client.ReadInputRegistersAsync(s.Address, s.Count, ct: ct) : await client.ReadHoldingRegistersAsync(s.Address, s.Count, ct: ct);
                for (var i = 0; i < regs.Length; i++)
                {
                    var row = new List<string> { (s.Address + i).ToString(CultureInfo.InvariantCulture), regs[i].ToString(CultureInfo.InvariantCulture), $"[{Ui.Hex(Ui.Muted)}]0x{regs[i]:X4}[/]" };
                    if (s.Float) row.Add(i % 2 == 0 && i + 1 < regs.Length ? ModbusConvert.ToSingle(regs.AsSpan(i, 2)).ToString("G6", CultureInfo.InvariantCulture) : "");
                    t.AddRow([.. row]);
                }
            }
            if (s.Watch is not null) AnsiConsole.Clear();
            AnsiConsole.Write(t);
            if (s.Watch is { } ms) await Task.Delay(ms, ct);
        }
        while (s.Watch is not null && !ct.IsCancellationRequested);
        return 0;
    }
}

internal sealed class ModbusWriteCommand : AsyncCommand<ModbusWriteCommand.Settings>
{
    public sealed class Settings : ModbusConnectionSettings
    {
        [CommandOption("-t|--table"), Description("holding (default) or coils.")]
        public string Table { get; init; } = "holding";

        [CommandOption("-a|--address"), Description("Start address.")]
        public ushort Address { get; init; }

        [CommandOption("-v|--values"), Description("Comma separated values (registers: 0-65535, coils: 1/0/on/off).")]
        public string Values { get; init; } = "";

        [CommandOption("--allow-write"), Description("Required: confirms you intend to change device state.")]
        public bool AllowWrite { get; init; }

        [CommandOption("-y|--yes"), Description("Skip the interactive confirmation.")]
        public bool Yes { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        if (!s.AllowWrite)
        {
            Ui.Warn("Writes change the state of real equipment. Re-run with [bold]--allow-write[/] to confirm you intend to.");
            return 3;
        }
        var parts = s.Values.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) throw new ArgumentException("Give at least one value with --values.");
        if (!s.Yes && !AnsiConsole.Confirm($"Write [bold]{parts.Length}[/] value(s) to [bold]{Markup.Escape(s.Table)}[/] {s.Address} on {Markup.Escape(s.Target)} unit {s.Unit}?", false))
            return 4;

        await using var client = s.CreateClient(readOnly: false);
        if (s.Table.Equals("coils", StringComparison.OrdinalIgnoreCase))
        {
            var bits = parts.Select(p => p is "1" or "on" or "ON" or "true").ToArray();
            if (bits.Length == 1) await client.WriteSingleCoilAsync(s.Address, bits[0], ct: ct);
            else await client.WriteMultipleCoilsAsync(s.Address, bits, ct: ct);
        }
        else
        {
            var regs = parts.Select(p => ushort.Parse(p, CultureInfo.InvariantCulture)).ToArray();
            if (regs.Length == 1) await client.WriteSingleRegisterAsync(s.Address, regs[0], ct: ct);
            else await client.WriteMultipleRegistersAsync(s.Address, regs, ct: ct);
        }
        Ui.Success($"Wrote {parts.Length} value(s) to {Markup.Escape(s.Table)} {s.Address}.");
        return 0;
    }
}

internal sealed class ModbusServeCommand : AsyncCommand<ModbusServeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-p|--port"), Description("TCP port (default 1502; 502 needs admin rights on most systems).")]
        public int Port { get; init; } = 1502;

        [CommandOption("--serial"), Description("Serve RTU on a serial port instead of TCP.")]
        public string? Serial { get; init; }

        [CommandOption("--baud"), Description("Serial baud rate.")]
        public int Baud { get; init; } = 9600;

        [CommandOption("--simulate"), Description("Animate the registers as the IoTCom.Net virtual PLC.")]
        public bool Simulate { get; init; }

        [CommandOption("--read-only"), Description("Reject every write with IllegalFunction.")]
        public bool ReadOnly { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        Ui.Banner();
        var store = new ModbusDataStore();
        await using var sim = s.Simulate ? ModbusSimulator.CreateVirtualPlc(store) : null;
        await using var server = ModbusServer.Create(o =>
        {
            if (s.Serial is not null) o.ServeSerial(s.Serial, s.Baud, Parity.Even).UseRtuFraming();
            else o.UseTcp(IPAddress.Any, s.Port);
            o.WithStore(store);
            if (s.ReadOnly) o.AsReadOnly();
        });
        server.RequestHandled += (_, e) =>
        {
            var color = e.IsException ? Ui.Fault : Ui.CableBlue;
            AnsiConsole.MarkupLine($"[{Ui.Hex(Ui.Muted)}]{DateTime.Now:HH:mm:ss.fff}[/] [{Ui.Hex(color)}]●[/] {Markup.Escape(e.Summary)} [{Ui.Hex(Ui.Muted)}]{Markup.Escape(e.Peer)}[/]");
        };
        await server.StartAsync(ct);
        sim?.Start();
        Ui.Success($"Modbus slave on [bold]{Markup.Escape(s.Serial ?? $"tcp://0.0.0.0:{s.Port}")}[/]{(s.Simulate ? " — virtual PLC running" : "")}. Ctrl+C to stop.");
        if (s.Simulate)
        {
            AnsiConsole.MarkupLine($"[{Ui.Hex(Ui.Muted)}]Map: IR0 temp×10 · IR1 humidity×10 · IR2 pressure · IR3 rpm · IR4-5 kW(f32) · IR6-7 kWh(f32) · HR0 setpoint×10 · HR1 counter · Coil0 motor[/]");
        }
        try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
        Ui.Warn($"Stopped after {server.RequestCount} requests.");
        return 0;
    }
}

internal sealed class ModbusDecodeCommand : Command<ModbusDecodeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<frame>"), Description("Frame as hex (TCP/RTU) or text (ASCII, e.g. \":1103006B00037E\").")]
        public string Frame { get; init; } = "";

        [CommandOption("-m|--mode"), Description("tcp, rtu or ascii (default: guess).")]
        public string? Mode { get; init; }

        [CommandOption("--response"), Description("Decode as a response instead of a request.")]
        public bool Response { get; init; }
    }

    public override int Execute(CommandContext context, Settings s, CancellationToken cancellationToken)
    {
        var isAscii = s.Mode?.Equals("ascii", StringComparison.OrdinalIgnoreCase) == true || s.Frame.TrimStart().StartsWith(':');
        var bytes = isAscii ? System.Text.Encoding.ASCII.GetBytes(s.Frame.Trim() + "\r\n") : HexDump.Parse(s.Frame);
        var mode = isAscii ? ModbusFramingMode.Ascii
            : s.Mode?.ToLowerInvariant() switch { "tcp" => ModbusFramingMode.Tcp, "rtu" => ModbusFramingMode.Rtu, _ => Guess(bytes) };
        var framing = ModbusFraming.For(mode);
        var ok = framing.TryDecode(bytes, !s.Response, out var adu);

        AnsiConsole.MarkupLine($"[bold]Modbus {mode.ToString().ToUpperInvariant()}[/] [{Ui.Hex(Ui.Muted)}]{(s.Response ? "response" : "request")} · {bytes.Length} bytes[/]");
        AnsiConsole.Write(Ui.FrameLane(bytes, ModbusAnatomy.Describe(bytes, mode, !s.Response), isAscii));
        if (ok) Ui.Success(Markup.Escape(ModbusPdu.Describe(adu.UnitId, adu.Pdu, !s.Response)));
        else Ui.Error(mode == ModbusFramingMode.Tcp ? "Invalid MBAP header or length." : "Checksum mismatch or incomplete frame.");
        return ok ? 0 : 2;
    }

    private static ModbusFramingMode Guess(byte[] b) =>
        b.Length >= 8 && b[2] == 0 && b[3] == 0 && ((b[4] << 8) | b[5]) == b.Length - 6 ? ModbusFramingMode.Tcp : ModbusFramingMode.Rtu;
}
