using System.Buffers;
using System.Globalization;
using System.IO.Pipelines;
using System.Text;
using System.Threading.Channels;
using IoTCom.Net.Transports;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Transport.Can;

/// <summary>
/// Sans-I/O codec for the Lawicel / slcan ASCII protocol spoken by CANable, CANtact, USBtin, many ELM-style and
/// serial CAN adapters (and the CAN FD extension of CANable 2 firmware: <c>d/D/b/B</c> frames, <c>Y</c> data bit rate).
/// </summary>
public static class SlcanCodec
{
    private static readonly int[] Bitrates = [10_000, 20_000, 50_000, 100_000, 125_000, 250_000, 500_000, 800_000, 1_000_000];

    /// <summary>The <c>Sn</c> command for a nominal bit rate.</summary>
    public static string BitrateCommand(int bitrate)
    {
        var i = Array.IndexOf(Bitrates, bitrate);
        return i < 0 ? throw new ArgumentOutOfRangeException(nameof(bitrate), $"slcan supports {string.Join(", ", Bitrates)} bit/s.") : "S" + i.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The <c>Yn</c> command for a CAN FD data bit rate (1, 2, 4, 5 or 8 Mbit/s).</summary>
    public static string DataBitrateCommand(int bitrate) => bitrate switch
    {
        1_000_000 or 2_000_000 or 4_000_000 or 5_000_000 or 8_000_000 => "Y" + (bitrate / 1_000_000).ToString(CultureInfo.InvariantCulture),
        _ => throw new ArgumentOutOfRangeException(nameof(bitrate), "slcan FD data bit rates are 1, 2, 4, 5 and 8 Mbit/s."),
    };

    /// <summary>Bit rate selected by an <c>Sn</c> command, or null.</summary>
    public static int? ParseBitrateCommand(ReadOnlySpan<char> command) =>
        command.Length == 2 && command[0] == 'S' && command[1] is >= '0' and <= '8' ? Bitrates[command[1] - '0'] : null;

    /// <summary>Encodes a frame as an slcan line (without the trailing CR).</summary>
    public static string Encode(in CanFrame frame)
    {
        var sb = new StringBuilder(10 + frame.Data.Length * 2);
        char kind;
        if (frame.IsFd) kind = (frame.Flags & CanFrameFlags.BitRateSwitch) != 0 ? 'b' : 'd';
        else kind = frame.IsRemote ? 'r' : 't';
        sb.Append(frame.IsExtended ? char.ToUpperInvariant(kind) : kind);
        sb.Append(frame.IsExtended ? frame.Id.ToString("X8", CultureInfo.InvariantCulture) : frame.Id.ToString("X3", CultureInfo.InvariantCulture));
        sb.Append(frame.Dlc.ToString("X1", CultureInfo.InvariantCulture));
        if (!frame.IsRemote) sb.Append(Convert.ToHexString(frame.Data.Span));
        return sb.ToString();
    }

    /// <summary>True when <paramref name="line"/> starts with a frame letter.</summary>
    public static bool IsFrameLine(ReadOnlySpan<char> line) => line.Length > 0 && line[0] is 't' or 'T' or 'r' or 'R' or 'd' or 'D' or 'b' or 'B';

    /// <summary>Decodes a frame line (an optional 4-digit timestamp suffix is ignored).</summary>
    public static bool TryDecode(ReadOnlySpan<char> line, out CanFrame frame)
    {
        frame = default;
        if (!IsFrameLine(line)) return false;
        var kind = line[0];
        var extended = char.IsUpper(kind);
        var idLen = extended ? 8 : 3;
        if (line.Length < 1 + idLen + 1) return false;
        if (!uint.TryParse(line.Slice(1, idLen), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id)) return false;
        var dlc = Hex(line[1 + idLen]);
        if (dlc < 0) return false;
        var flags = extended ? CanFrameFlags.Extended : CanFrameFlags.None;
        var lower = char.ToLowerInvariant(kind);
        if (lower is 'd' or 'b') flags |= CanFrameFlags.Fd | (lower == 'b' ? CanFrameFlags.BitRateSwitch : 0);
        else if (dlc > 8) return false;
        try
        {
            if (lower == 'r')
            {
                frame = new CanFrame(id, ReadOnlyMemory<byte>.Empty, flags | CanFrameFlags.Remote, dlc);
                return true;
            }
            var len = CanDlc.ToLength(dlc, fd: (flags & CanFrameFlags.Fd) != 0);
            var start = 2 + idLen;
            var hex = line[start..];
            if (hex.Length != len * 2 && hex.Length != len * 2 + 4) return false;
            frame = new CanFrame(id, Convert.FromHexString(hex[..(len * 2)]), flags);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return false;
        }
    }

    private static int Hex(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    /// <summary>Splits complete lines (CR or LF terminated) from <paramref name="buffer"/>; BEL (0x07) is reported as the line "\a".</summary>
    internal static bool TryReadLine(ref ReadOnlySequence<byte> buffer, out string line)
    {
        var reader = new SequenceReader<byte>(buffer);
        while (reader.TryRead(out var b))
        {
            if (b == 0x07)
            {
                var consumedBefore = reader.Consumed - 1;
                if (consumedBefore > 0)
                {
                    // Flush pending text before the BEL as its own line.
                    line = Encoding.ASCII.GetString(buffer.Slice(0, consumedBefore));
                    buffer = buffer.Slice(consumedBefore);
                    return true;
                }
                line = "\a";
                buffer = buffer.Slice(reader.Position);
                return true;
            }
            if (b is (byte)'\r' or (byte)'\n')
            {
                line = Encoding.ASCII.GetString(buffer.Slice(0, reader.Consumed - 1));
                buffer = buffer.Slice(reader.Position);
                return true;
            }
        }
        line = "";
        return false;
    }
}

/// <summary>A CAN interface reached through an slcan adapter on a serial port or TCP stream.</summary>
public sealed class SlcanBus : CanBusBase
{
    private readonly TransportFactory _factory;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly Channel<string> _responses = System.Threading.Channels.Channel.CreateUnbounded<string>();
    private ITransport? _transport;
    private CancellationTokenSource? _cts;
    private Task? _readLoop;
    private long _errors;

    /// <summary>Creates a bus over a transport (e.g. <c>() =&gt; new SerialTransport(...)</c>).</summary>
    public SlcanBus(TransportFactory factory, string channel, CanBusOptions? options = null) : base("can-slcan", channel, options ?? new CanBusOptions())
        => _factory = factory;

    /// <summary>Error responses (BEL) received from the adapter.</summary>
    public long AdapterErrors => Interlocked.Read(ref _errors);

    /// <summary>Firmware version reported by the adapter (<c>V</c> command), if any.</summary>
    public string? AdapterVersion { get; private set; }

    internal static TransportFactory SerialFactory(string port) =>
        () => new IoTCom.Net.Transport.Serial.SerialTransport(new IoTCom.Net.Transport.Serial.SerialSettings { PortName = port, BaudRate = 115_200 });

    internal static TransportFactory TcpFactory(string hostPort)
    {
        var i = hostPort.LastIndexOf(':');
        if (i <= 0 || !int.TryParse(hostPort.AsSpan(i + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var port))
            throw new ArgumentException("slcan-tcp expects host:port.", nameof(hostPort));
        var host = hostPort[..i];
        return () => new TcpClientTransport(host, port);
    }

    /// <inheritdoc />
    protected override async ValueTask OpenCoreAsync(CancellationToken ct)
    {
        _transport = _factory();
        await _transport.OpenAsync(ct).ConfigureAwait(false);
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _readLoop = Task.Run(() => ReadLoopAsync(_transport.Pipe.Input, token), CancellationToken.None);

        await CommandAsync("C", required: false, ct).ConfigureAwait(false);      // close if left open
        await CommandAsync(SlcanCodec.BitrateCommand(Options.Bitrate), required: true, ct).ConfigureAwait(false);
        if (Options.Fd) await CommandAsync(SlcanCodec.DataBitrateCommand(Options.DataBitrate), required: true, ct).ConfigureAwait(false);
        var version = await CommandAsync("V", required: false, ct).ConfigureAwait(false);
        AdapterVersion = version is { Length: > 1 } v && v[0] == 'V' ? v[1..] : null;
        await CommandAsync(Options.ListenOnly ? "L" : "O", required: true, ct).ConfigureAwait(false);
    }

    /// <summary>Sends a command and waits for the adapter's answer (CR = ok, BEL = error).</summary>
    private async Task<string?> CommandAsync(string command, bool required, CancellationToken ct)
    {
        while (_responses.Reader.TryRead(out _)) { }
        await WriteLineAsync(command, ct).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            var answer = await _responses.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
            if (answer == "\a")
            {
                if (required) throw new DeviceException($"slcan adapter on {Channel} rejected '{command}'.");
                return null;
            }
            return answer;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            if (required) throw new IoTComTimeoutException($"slcan adapter on {Channel} did not answer '{command}'.");
            return null;
        }
    }

    private async Task WriteLineAsync(string line, CancellationToken ct)
    {
        var output = _transport?.Pipe.Output ?? throw new InvalidOperationException("Not open.");
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var n = Encoding.ASCII.GetBytes(line, output.GetSpan(line.Length + 1));
            output.GetSpan(n + 1)[n] = (byte)'\r';
            output.Advance(n + 1);
            await output.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _write.Release();
        }
    }

    private async Task ReadLoopAsync(PipeReader input, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await input.ReadAsync(ct).ConfigureAwait(false);
                var buffer = result.Buffer;
                while (SlcanCodec.TryReadLine(ref buffer, out var line))
                {
                    if (SlcanCodec.TryDecode(line, out var frame)) OnFrameReceived(frame);
                    else if (line is "z" or "Z") { /* transmit acknowledgement */ }
                    else if (line.Length == 0 && State == EndpointState.Connected) { /* ok to a frame */ }
                    else
                    {
                        if (line == "\a") Interlocked.Increment(ref _errors);
                        _responses.Writer.TryWrite(line);
                    }
                }
                input.AdvanceTo(buffer.Start, buffer.End);
                if (result.IsCompleted) throw new TransportException($"slcan adapter on {Channel} closed the connection.");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "slcan read loop on {Channel} stopped", Channel);
            Fault(ex);
        }
    }

    /// <inheritdoc />
    protected override async ValueTask CloseCoreAsync()
    {
        if (_transport is not null)
        {
            try
            {
                await WriteLineAsync("C", CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "slcan close command failed on {Channel}", Channel);
            }
        }
        if (_cts is not null) await _cts.CancelAsync().ConfigureAwait(false);
        if (_readLoop is not null) await _readLoop.ConfigureAwait(false);
        if (_transport is not null) await _transport.DisposeAsync().ConfigureAwait(false);
        _cts?.Dispose();
        (_transport, _cts, _readLoop) = (null, null, null);
    }

    /// <inheritdoc />
    protected override ValueTask SendCoreAsync(CanFrame frame, CancellationToken ct) => new(WriteLineAsync(SlcanCodec.Encode(frame), ct));

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore()
    {
        await base.DisposeAsyncCore().ConfigureAwait(false);
        _write.Dispose();
    }
}

/// <summary>
/// Emulates an slcan USB adapter on a byte stream and bridges it to a <see cref="VirtualCanNetwork"/>, so
/// <see cref="SlcanBus"/> (and third-party slcan tools over TCP) can be tested without hardware.
/// </summary>
public static class SlcanAdapterSimulator
{
    /// <summary>Firmware version string answered to <c>V</c>.</summary>
    public const string Version = "1013";

    /// <summary>Serves one adapter connection until the stream closes or <paramref name="ct"/> is cancelled.</summary>
    public static async Task RunAsync(ITransport transport, VirtualCanNetwork network, CancellationToken ct = default)
    {
        await using var node = network.CreateNode(o => o.Fd = true);
        var output = transport.Pipe.Output;
        var gate = new SemaphoreSlim(1, 1);
        var open = false;

        async Task WriteAsync(string text)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var bytes = Encoding.ASCII.GetBytes(text);
                await output.WriteAsync(bytes, ct).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        node.FrameReceived += f =>
        {
            if (Volatile.Read(ref open)) _ = WriteAsync(SlcanCodec.Encode(f) + "\r");
        };

        try
        {
            var input = transport.Pipe.Input;
            while (!ct.IsCancellationRequested)
            {
                var result = await input.ReadAsync(ct).ConfigureAwait(false);
                var buffer = result.Buffer;
                while (SlcanCodec.TryReadLine(ref buffer, out var line))
                {
                    if (line.Length == 0) continue;
                    string reply;
                    if (SlcanCodec.IsFrameLine(line))
                    {
                        if (open && SlcanCodec.TryDecode(line, out var frame))
                        {
                            if (node.State != EndpointState.Connected) await node.ConnectAsync(ct).ConfigureAwait(false);
                            await node.SendAsync(frame, ct).ConfigureAwait(false);
                            reply = frame.IsExtended ? "Z\r" : "z\r";
                        }
                        else reply = "\a";
                    }
                    else
                    {
                        switch (line[0])
                        {
                            case 'S' when SlcanCodec.ParseBitrateCommand(line) is not null && !open:
                            case 'Y' when !open:
                            case 'Z':
                                reply = "\r";
                                break;
                            case 'O' or 'L' when !open:
                                open = true;
                                await node.ConnectAsync(ct).ConfigureAwait(false);
                                reply = "\r";
                                break;
                            case 'C':
                                open = false;
                                reply = "\r";
                                break;
                            case 'V':
                                reply = "V" + Version + "\r";
                                break;
                            case 'F':
                                reply = "F00\r";
                                break;
                            default:
                                reply = "\a";
                                break;
                        }
                    }
                    await WriteAsync(reply).ConfigureAwait(false);
                }
                input.AdvanceTo(buffer.Start, buffer.End);
                if (result.IsCompleted) break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            gate.Dispose();
        }
    }
}
