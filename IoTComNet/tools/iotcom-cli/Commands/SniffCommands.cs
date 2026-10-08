using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using IoTCom.Net.Protocols.Coap;
using IoTCom.Net.Protocols.Hl7;
using IoTCom.Net.Protocols.Mavlink;
using IoTCom.Net.Protocols.Mavlink.Common;
using IoTCom.Net.Protocols.Modbus;
using IoTCom.Net.Transport.Can;
using Spectre.Console;
using Spectre.Console.Cli;

namespace IoTCom.Net.Cli.Commands;

internal class SniffSettings : CommandSettings
{
    [CommandOption("-w|--pcap"), Description("Write every frame to this pcapng file (open it in Wireshark).")]
    public string? Pcap { get; init; }

    [CommandOption("--lanes"), Description("Draw every frame as a frame lane instead of one line.")]
    public bool Lanes { get; init; }
}

/// <summary>Prints frames and writes them to pcapng.</summary>
internal sealed class FramePrinter : IDisposable
{
    private readonly bool _lanes;
    private readonly PcapngTap? _pcap;
    private readonly DateTimeOffset _start = DateTimeOffset.UtcNow;
    private readonly Lock _gate = new();
    private long _frames;

    public FramePrinter(SniffSettings s)
    {
        _lanes = s.Lanes;
        _pcap = s.Pcap is null ? null : PcapngTap.Create(s.Pcap);
    }

    public long Frames => Interlocked.Read(ref _frames);

    public void Print(string protocol, FrameDirection direction, ReadOnlySpan<byte> frame, string label, string summary, IReadOnlyList<FrameField>? fields)
    {
        Interlocked.Increment(ref _frames);
        _pcap?.OnFrame(new TrafficFrame(protocol, direction, frame.ToArray(), DateTimeOffset.UtcNow, label, summary));
        lock (_gate)
        {
            var arrow = direction == FrameDirection.Outbound ? $"[{Ui.Hex(Ui.Amber)}]→[/]" : $"[{Ui.Hex(Ui.CableBlue)}]←[/]";
            AnsiConsole.MarkupLine(CultureInfo.InvariantCulture,
                $"[{Ui.Hex(Ui.Muted)}]{(DateTimeOffset.UtcNow - _start).TotalSeconds,8:0.000}[/] {arrow} [{Ui.Hex(Ui.Muted)}]{Markup.Escape(label)}[/] {Markup.Escape(summary)} [{Ui.Hex(Ui.Muted)}]({frame.Length} B)[/]");
            if (_lanes && fields is not null) AnsiConsole.Write(Ui.FrameLane(frame, fields));
        }
    }

    public void Dispose() => _pcap?.Dispose();
}

internal sealed class SniffTcpCommand : AsyncCommand<SniffTcpCommand.Settings>
{
    public sealed class Settings : SniffSettings
    {
        [CommandOption("-l|--listen"), Description("Local port clients connect to.")]
        public int Listen { get; init; } = 1502;

        [CommandOption("-t|--target"), Description("Real device host:port (e.g. 192.168.1.10:502).")]
        public string Target { get; init; } = "";

        [CommandOption("-p|--protocol"), Description("modbus (default), hl7 or raw.")]
        public string Protocol { get; init; } = "modbus";
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        var i = s.Target.LastIndexOf(':');
        if (i <= 0) throw new ArgumentException("--target host:port is required, e.g. --target 192.168.1.10:502");
        var (host, port) = (s.Target[..i], int.Parse(s.Target[(i + 1)..], CultureInfo.InvariantCulture));
        using var printer = new FramePrinter(s);
        var listener = new TcpListener(IPAddress.Any, s.Listen);
        listener.Start();
        Ui.Success($"Sniffing {Markup.Escape(s.Protocol)}: clients → tcp://0.0.0.0:{s.Listen} → {Markup.Escape(s.Target)}{(s.Pcap is null ? "" : $", writing {Markup.Escape(s.Pcap)}")}. Ctrl+C to stop.");
        Ui.Warn("Point your client (SCADA, HMI, script) at this port instead of the device; traffic is relayed unchanged.");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => RelayAsync(client, host, port, s.Protocol.ToLowerInvariant(), printer, ct), ct);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            listener.Stop();
        }
        Ui.Warn($"Stopped after {printer.Frames} frames.");
        return 0;
    }

    private static async Task RelayAsync(TcpClient client, string host, int port, string protocol, FramePrinter printer, CancellationToken ct)
    {
        var label = client.Client.RemoteEndPoint?.ToString() ?? "client";
        using (client)
        using (var upstream = new TcpClient())
        {
            try
            {
                await upstream.ConnectAsync(host, port, ct);
            }
            catch (SocketException ex)
            {
                Ui.Error($"{Markup.Escape(label)}: cannot reach {Markup.Escape(host)}:{port} ({ex.SocketErrorCode})");
                return;
            }
            AnsiConsole.MarkupLine($"[{Ui.Hex(Ui.LampGreen)}]●[/] {Markup.Escape(label)} connected");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var toDevice = Pump(client.GetStream(), upstream.GetStream(), FrameDirection.Outbound, protocol, label, printer, cts.Token);
            var toClient = Pump(upstream.GetStream(), client.GetStream(), FrameDirection.Inbound, protocol, label, printer, cts.Token);
            await Task.WhenAny(toDevice, toClient);
            await cts.CancelAsync();
            AnsiConsole.MarkupLine($"[{Ui.Hex(Ui.Muted)}]● {Markup.Escape(label)} closed[/]");
        }
    }

    private static async Task Pump(NetworkStream from, NetworkStream to, FrameDirection direction, string protocol, string label, FramePrinter printer, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        var pending = new List<byte>();
        try
        {
            while (true)
            {
                var n = await from.ReadAsync(buffer, ct);
                if (n == 0) return;
                await to.WriteAsync(buffer.AsMemory(0, n), ct);   // relay first: the sniffer never delays the device
                pending.AddRange(buffer.AsSpan(0, n));
                while (NextFrame(protocol, pending) is { } frame) Report(protocol, direction, frame, label, printer);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException) { }
    }

    /// <summary>Cuts one complete frame from the stream buffer: MBAP length for Modbus/TCP, 0x0B…0x1C 0x0D for MLLP, any chunk for raw.</summary>
    private static byte[]? NextFrame(string protocol, List<byte> pending)
    {
        switch (protocol)
        {
            case "modbus":
                if (pending.Count < 6) return null;
                var total = 6 + (pending[4] << 8 | pending[5]);
                if (total > 260 + 6) return Take(pending, pending.Count); // not Modbus/TCP: flush as is
                return pending.Count >= total ? Take(pending, total) : null;
            case "hl7":
                var end = -1;
                for (var i = 1; i < pending.Count; i++)
                    if (pending[i - 1] == 0x1C && pending[i] == 0x0D) { end = i + 1; break; }
                return end < 0 ? null : Take(pending, end);
            default:
                return pending.Count == 0 ? null : Take(pending, pending.Count);
        }
    }

    private static byte[] Take(List<byte> pending, int count)
    {
        var frame = pending.GetRange(0, count).ToArray();
        pending.RemoveRange(0, count);
        return frame;
    }

    private static void Report(string protocol, FrameDirection direction, byte[] frame, string label, FramePrinter printer)
    {
        switch (protocol)
        {
            case "modbus":
                var fields = ModbusAnatomy.Describe(frame, ModbusFramingMode.Tcp, direction == FrameDirection.Outbound);
                var function = fields.FirstOrDefault(f => f.Kind is FrameFieldKind.Function or FrameFieldKind.Error);
                printer.Print("modbus-tcp", direction, frame, label, $"unit {(frame.Length > 6 ? frame[6] : 0)} {function.Value ?? function.Name}", fields);
                break;
            case "hl7":
                var text = System.Text.Encoding.UTF8.GetString(frame).Trim('\v', '\x1c', '\r');
                var summary = Hl7Message.TryParse(text, out var m) ? $"{m.MessageType} {m.ControlId}{(m.MessageType.StartsWith("ACK", StringComparison.Ordinal) ? " " + m.AckCode() : "")}" : "HL7 (unparsed)";
                printer.Print("hl7", direction, frame, label, summary, null);
                break;
            default:
                printer.Print("tcp-raw", direction, frame, label, Convert.ToHexString(frame.AsSpan(0, Math.Min(16, frame.Length))) + (frame.Length > 16 ? "…" : ""), null);
                break;
        }
    }
}

internal sealed class SniffUdpCommand : AsyncCommand<SniffUdpCommand.Settings>
{
    public sealed class Settings : SniffSettings
    {
        [CommandOption("-l|--listen"), Description("Local UDP port clients send to.")]
        public int Listen { get; init; } = 15683;

        [CommandOption("-t|--target"), Description("Real device host:port (e.g. 192.168.1.40:5683).")]
        public string Target { get; init; } = "";

        [CommandOption("-p|--protocol"), Description("coap (default), mavlink or raw.")]
        public string Protocol { get; init; } = "coap";
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        var i = s.Target.LastIndexOf(':');
        if (i <= 0) throw new ArgumentException("--target host:port is required, e.g. --target 192.168.1.40:5683");
        var addresses = await Dns.GetHostAddressesAsync(s.Target[..i], ct);
        var target = new IPEndPoint(addresses.First(a => a.AddressFamily == AddressFamily.InterNetwork), int.Parse(s.Target[(i + 1)..], CultureInfo.InvariantCulture));
        using var printer = new FramePrinter(s);
        using var front = new UdpClient(new IPEndPoint(IPAddress.Any, s.Listen));
        using var back = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        IPEndPoint? lastClient = null;
        var protocol = s.Protocol.ToLowerInvariant();
        var parsers = new Dictionary<FrameDirection, MavlinkParser>
        {
            [FrameDirection.Outbound] = new(CommonDialect.Instance),
            [FrameDirection.Inbound] = new(CommonDialect.Instance),
        };
        Ui.Success($"Sniffing {Markup.Escape(protocol)}: clients → udp://0.0.0.0:{s.Listen} → {target}{(s.Pcap is null ? "" : $", writing {Markup.Escape(s.Pcap)}")}. Ctrl+C to stop.");

        void Report(FrameDirection direction, byte[] datagram, string label)
        {
            switch (protocol)
            {
                case "coap":
                    var summary = CoapMessage.TryDecode(datagram, out var m, out var error) ? m.ToString() : "invalid CoAP: " + error;
                    printer.Print("coap", direction, datagram, label, summary, CoapAnatomy.Describe(datagram));
                    break;
                case "mavlink":
                    var parser = parsers[direction];
                    parser.Feed(datagram);
                    while (parser.TryRead(out var p))
                        printer.Print("mavlink", direction, p.Frame.Span, label, p.ToString(), MavlinkAnatomy.Describe(p.Frame.Span, CommonDialect.Instance));
                    break;
                default:
                    printer.Print("udp-raw", direction, datagram, label, Convert.ToHexString(datagram.AsSpan(0, Math.Min(16, datagram.Length))), null);
                    break;
            }
        }

        var upstream = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                var r = await back.ReceiveAsync(ct);
                if (lastClient is null) continue;
                await front.SendAsync(r.Buffer, lastClient, ct);
                Report(FrameDirection.Inbound, r.Buffer, lastClient.ToString());
            }
        }, ct);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var r = await front.ReceiveAsync(ct);
                lastClient = r.RemoteEndPoint;
                await back.SendAsync(r.Buffer, target, ct);
                Report(FrameDirection.Outbound, r.Buffer, r.RemoteEndPoint.ToString());
            }
        }
        catch (OperationCanceledException) { }
        try
        {
            await upstream;
        }
        catch (OperationCanceledException) { }
        Ui.Warn($"Stopped after {printer.Frames} frames.");
        return 0;
    }
}

internal sealed class SniffCanCommand : AsyncCommand<SniffCanCommand.Settings>
{
    public sealed class Settings : SniffSettings
    {
        [CommandOption("-c|--can"), Description("Interface: socketcan:can0, slcan:COM5, slcan-tcp:host:port or sim (ECU simulator traffic).")]
        public string Can { get; init; } = "sim";

        [CommandOption("-b|--bitrate")]
        public int Bitrate { get; init; } = 500_000;

        [CommandOption("--fd")]
        public bool Fd { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        await using var target = await CanTarget.OpenAsync(s.Can, s.Bitrate, s.Fd, ct);
        using var printer = new FramePrinter(s);
        using var reader = target.Bus.OpenReader();
        Ui.Success($"Capturing {Markup.Escape(target.Bus.Channel)} (listen only){(s.Pcap is null ? "" : $", writing {Markup.Escape(s.Pcap)} (SocketCAN link type)")}. Ctrl+C to stop.");
        if (target.Simulator is not null)
        {
            // Generate traffic: an OBD scan tool polling the simulated ECU.
            _ = Task.Run(async () =>
            {
                await using var obd = Protocols.Uds.ObdClient.Create(target.Bus);
                await obd.ConnectAsync(ct);
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        await obd.ReadPidAsync(Protocols.Uds.ObdPids.EngineRpm, ct);
                        await obd.ReadVinAsync(ct);
                        await Task.Delay(1000, ct);
                    }
                }
                catch (OperationCanceledException) { }
            }, ct);
        }
        try
        {
            await foreach (var f in reader.ReadAllAsync(ct))
            {
                var bytes = CanBusBase.ToTapBytes(f);
                printer.Print("can", FrameDirection.Inbound, bytes, target.Bus.Channel, f.ToString(), null);
            }
        }
        catch (OperationCanceledException) { }
        Ui.Warn($"Stopped after {printer.Frames} frames.");
        return 0;
    }
}
