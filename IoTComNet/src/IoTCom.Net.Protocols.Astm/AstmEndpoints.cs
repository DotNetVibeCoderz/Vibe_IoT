using System.Buffers;
using System.Globalization;
using System.IO.Pipelines;
using System.Text;
using System.Threading.Channels;
using IoTCom.Net.Protocols.Hl7;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Protocols.Astm;

/// <summary>Reads single control bytes and frames from a pipe (shared by both sides).</summary>
internal sealed class AstmReader(PipeReader input)
{
    private byte[] _pending = [];

    /// <summary>Next unit: a control byte (ENQ, ACK, NAK, EOT) or a complete frame (STX … LF).</summary>
    public async Task<byte[]?> NextAsync(CancellationToken ct)
    {
        while (true)
        {
            var start = Array.FindIndex(_pending, b => b is AstmLink.Enq or AstmLink.Ack or AstmLink.Nak or AstmLink.Eot or AstmLink.Stx);
            if (start > 0) _pending = _pending[start..];
            if (start >= 0 && _pending.Length > 0)
            {
                if (_pending[0] != AstmLink.Stx)
                {
                    var unit = _pending[..1];
                    _pending = _pending[1..];
                    return unit;
                }

                var lf = Array.IndexOf(_pending, (byte)0x0A);
                if (lf > 0)
                {
                    var frame = _pending[..(lf + 1)];
                    _pending = _pending[(lf + 1)..];
                    return frame;
                }
            }
            else
            {
                _pending = [];
            }

            var result = await input.ReadAsync(ct).ConfigureAwait(false);
            _pending = [.. _pending, .. result.Buffer.ToArray()];
            input.AdvanceTo(result.Buffer.End);
            if (result.IsCompleted && result.Buffer.IsEmpty) return null;
        }
    }
}

/// <summary>ASTM receiver options.</summary>
public sealed class AstmReceiverOptions : IListenerBuilder<AstmReceiverOptions>
{
    /// <summary>Listener (TCP, in-memory).</summary>
    public TransportListenerFactory? ListenerFactory { get; set; }

    /// <summary>A single transport (serial port to the analyzer).</summary>
    public TransportFactory? TransportFactory { get; set; }

    /// <summary>Time to wait for the next frame before abandoning a transfer (LIS1-A: 30 s).</summary>
    public TimeSpan FrameTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Friendly name.</summary>
    public string? Name { get; set; }

    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }

    /// <inheritdoc />
    public AstmReceiverOptions UseListener(TransportListenerFactory factory)
    {
        (ListenerFactory, TransportFactory) = (factory, null);
        return this;
    }

    /// <inheritdoc />
    public AstmReceiverOptions UseTransport(TransportFactory factory)
    {
        (TransportFactory, ListenerFactory) = (factory, null);
        return this;
    }
}

/// <summary>
/// The LIS side of ASTM E1381/E1394: accepts ENQ, acknowledges valid frames in sequence (NAK on a bad checksum or
/// frame number), reassembles records at EOT and raises <see cref="MessageReceived"/>.
/// </summary>
public sealed class AstmReceiver : EndpointBase, IServerEndpoint
{
    private readonly AstmReceiverOptions _options;
    private readonly Channel<AstmMessage> _messages = Channel.CreateBounded<AstmMessage>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });
    private ITransportListener? _listener;
    private CancellationTokenSource? _cts;
    private long _received, _naks;

    private AstmReceiver(AstmReceiverOptions options) : base("astm", options.Logger)
    {
        _options = options;
        Name = options.Name;
    }

    /// <summary>Creates a receiver.</summary>
    public static AstmReceiver Create(Action<AstmReceiverOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var o = new AstmReceiverOptions();
        configure(o);
        if (o.ListenerFactory is null && o.TransportFactory is null) throw new ArgumentException("A listener (UseTcp/ListenInMemory) or transport (UseSerial) is required.", nameof(configure));
        return new AstmReceiver(o);
    }

    /// <summary>Raised for every complete message.</summary>
    public event EventHandler<AstmMessage>? MessageReceived;

    /// <summary>Messages received.</summary>
    public long MessagesReceived => Interlocked.Read(ref _received);

    /// <summary>Frames rejected with NAK.</summary>
    public long NakCount => Interlocked.Read(ref _naks);

    /// <summary>Streams received messages.</summary>
    public IAsyncEnumerable<AstmMessage> ReceiveAsync(CancellationToken ct = default) => _messages.Reader.ReadAllAsync(ct);

    /// <inheritdoc />
    public async ValueTask StartAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_cts is not null) return;
        var cts = _cts = new CancellationTokenSource();
        if (_options.ListenerFactory is { } factory)
        {
            var listener = _listener = factory();
            await listener.StartAsync(ct).ConfigureAwait(false);
            _ = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        var peer = await listener.AcceptAsync(cts.Token).ConfigureAwait(false);
                        _ = Task.Run(() => ServeAsync(peer, cts.Token), CancellationToken.None);
                    }
                    catch (Exception) when (cts.IsCancellationRequested)
                    {
                        break;
                    }
                }
            }, CancellationToken.None);
        }
        else
        {
            var t = _options.TransportFactory!();
            await t.OpenAsync(ct).ConfigureAwait(false);
            _ = Task.Run(() => ServeAsync(t, cts.Token), CancellationToken.None);
        }

        SetState(EndpointState.Listening);
    }

    /// <inheritdoc />
    public async ValueTask StopAsync(CancellationToken ct = default)
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts is null) return;
        await cts.CancelAsync().ConfigureAwait(false);
        if (_listener is not null) await _listener.DisposeAsync().ConfigureAwait(false);
        _listener = null;
        cts.Dispose();
        SetState(EndpointState.Disconnected);
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore()
    {
        await StopAsync().ConfigureAwait(false);
        _messages.Writer.TryComplete();
    }

    private async Task ServeAsync(ITransport transport, CancellationToken ct)
    {
        var reader = new AstmReader(transport.Pipe.Input);
        var text = new StringBuilder();
        var expected = 1;
        var receiving = false;
        async Task Reply(byte b, string what)
        {
            Tap(FrameDirection.Outbound, [b], () => what);
            await transport.Pipe.Output.WriteAsync(new[] { b }, ct).ConfigureAwait(false);
        }

        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                if (receiving) timeout.CancelAfter(_options.FrameTimeout);
                byte[]? unit;
                try
                {
                    unit = await reader.NextAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    (receiving, expected) = (false, 1);
                    text.Clear();
                    continue;
                }

                if (unit is null) break;
                switch (unit[0])
                {
                    case AstmLink.Enq:
                        Tap(FrameDirection.Inbound, unit, () => "ENQ");
                        (receiving, expected) = (true, 1);
                        text.Clear();
                        await Reply(AstmLink.Ack, "ACK").ConfigureAwait(false);
                        break;
                    case AstmLink.Stx when receiving:
                        if (AstmLink.TryParseFrame(unit, out var fn, out var chunk, out _, out var error) && fn == expected)
                        {
                            Tap(FrameDirection.Inbound, unit, () => $"frame {fn}: {chunk.TrimEnd('\r')}");
                            text.Append(chunk);
                            expected = (expected + 1) % 8;
                            await Reply(AstmLink.Ack, "ACK").ConfigureAwait(false);
                        }
                        else if (error is null && fn == (expected + 7) % 8)
                        {
                            await Reply(AstmLink.Ack, "ACK (duplicate frame)").ConfigureAwait(false);   // our ACK was lost: accept the repeat
                        }
                        else
                        {
                            Interlocked.Increment(ref _naks);
                            Tap(FrameDirection.Inbound, unit, () => $"bad frame: {error ?? $"frame number {fn}, expected {expected}"}");
                            await Reply(AstmLink.Nak, "NAK").ConfigureAwait(false);
                        }

                        break;
                    case AstmLink.Eot:
                        Tap(FrameDirection.Inbound, unit, () => "EOT");
                        if (receiving && text.Length > 0 && AstmMessage.TryParse(text.ToString(), out var message))
                        {
                            Interlocked.Increment(ref _received);
                            _messages.Writer.TryWrite(message!);
                            MessageReceived?.Invoke(this, message!);
                        }

                        (receiving, expected) = (false, 1);
                        text.Clear();
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
        finally
        {
            await transport.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>ASTM sender options.</summary>
public sealed class AstmSenderOptions : ITransportBuilder<AstmSenderOptions>
{
    /// <summary>Transport.</summary>
    public TransportFactory? TransportFactory { get; set; }

    /// <summary>Time to wait for each ACK (LIS1-A: 15 s).</summary>
    public TimeSpan AckTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Retransmissions of a frame after NAK (LIS1-A: 6).</summary>
    public int MaxRetries { get; set; } = 6;

    /// <summary>Friendly name.</summary>
    public string? Name { get; set; }

    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }

    /// <inheritdoc />
    public AstmSenderOptions UseTransport(TransportFactory factory)
    {
        TransportFactory = factory;
        return this;
    }
}

/// <summary>The analyzer side: ENQ, frames with ACK/NAK and retransmission, EOT.</summary>
public sealed class AstmSender : EndpointBase, IClientEndpoint
{
    private readonly AstmSenderOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ITransport? _transport;
    private AstmReader? _reader;

    private AstmSender(AstmSenderOptions options) : base("astm", options.Logger)
    {
        _options = options;
        Name = options.Name;
    }

    /// <summary>Creates a sender.</summary>
    public static AstmSender Create(Action<AstmSenderOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var o = new AstmSenderOptions();
        configure(o);
        if (o.TransportFactory is null) throw new ArgumentException("A transport is required.", nameof(configure));
        return new AstmSender(o);
    }

    /// <summary>Frames retransmitted after NAK.</summary>
    public long Retransmissions { get; private set; }

    /// <summary>Corrupts the next frame's checksum once (to demonstrate NAK and retransmission).</summary>
    public bool CorruptNextFrame { get; set; }

    /// <inheritdoc />
    public async ValueTask ConnectAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_transport is not null) return;
        var t = _options.TransportFactory!();
        await t.OpenAsync(ct).ConfigureAwait(false);
        (_transport, _reader) = (t, new AstmReader(t.Pipe.Input));
        SetState(EndpointState.Connected);
    }

    /// <inheritdoc />
    public async ValueTask DisconnectAsync(CancellationToken ct = default)
    {
        var t = Interlocked.Exchange(ref _transport, null);
        if (t is null) return;
        await t.DisposeAsync().ConfigureAwait(false);
        SetState(EndpointState.Disconnected);
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    /// <summary>Transfers one message.</summary>
    /// <exception cref="IoTComTimeoutException">No ACK in time.</exception>
    /// <exception cref="DeviceException">The receiver refused the transfer or a frame after all retries.</exception>
    public async Task SendAsync(AstmMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var t = _transport ?? throw new TransportException("Not connected.");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (await SendUnitAsync(t, [AstmLink.Enq], "ENQ", ct).ConfigureAwait(false) != AstmLink.Ack)
                throw new DeviceException("The receiver refused the transfer (NAK to ENQ).");
            foreach (var frame in AstmLink.Frames(message))
            {
                var attempt = 0;
                while (true)
                {
                    var bytes = frame;
                    if (CorruptNextFrame)
                    {
                        bytes = [.. frame];
                        bytes[^3] ^= 0x01;
                        CorruptNextFrame = false;
                    }

                    var answer = await SendUnitAsync(t, bytes, $"frame {frame[1] - '0'}", ct).ConfigureAwait(false);
                    if (answer == AstmLink.Ack) break;
                    if (++attempt > _options.MaxRetries) throw new DeviceException($"Frame {frame[1] - '0'} was refused {attempt} times.");
                    Retransmissions++;
                }
            }

            Tap(FrameDirection.Outbound, [AstmLink.Eot], () => "EOT");
            await t.Pipe.Output.WriteAsync(new[] { AstmLink.Eot }, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<byte> SendUnitAsync(ITransport t, byte[] bytes, string what, CancellationToken ct)
    {
        Tap(FrameDirection.Outbound, bytes, () => bytes[0] == AstmLink.Stx && AstmLink.TryParseFrame(bytes, out _, out var text, out _, out _) ? $"{what}: {text.TrimEnd('\r')}" : what);
        await t.Pipe.Output.WriteAsync(bytes, ct).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.AckTimeout);
        try
        {
            while (true)
            {
                var unit = await _reader!.NextAsync(timeout.Token).ConfigureAwait(false) ?? throw new TransportException("The receiver closed the link.");
                if (unit[0] is AstmLink.Ack or AstmLink.Nak)
                {
                    Tap(FrameDirection.Inbound, unit, () => unit[0] == AstmLink.Ack ? "ACK" : "NAK");
                    return unit[0];
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IoTComTimeoutException($"No ACK to {what} within {_options.AckTimeout.TotalSeconds:0.#} s.");
        }
    }
}

/// <summary>
/// A synthetic clinical chemistry analyzer: generates specimens for fictitious patients with a basic metabolic and
/// liver panel and flags values outside the reference ranges. Synthetic data, not a medical device.
/// </summary>
public sealed class AnalyzerSimulator(int seed = 21)
{
    private readonly Random _random = new(seed);
    private int _specimen = 2026_10_00;

    private static readonly (string Code, string Name, string Units, double Low, double High, double Mean, double Sd, string Format)[] Panel =
    [
        ("GLU", "Glucose", "mg/dL", 70, 99, 105, 30, "0"),
        ("CREA", "Creatinine", "mg/dL", 0.6, 1.2, 1.0, 0.35, "0.00"),
        ("UREA", "Urea nitrogen", "mg/dL", 7, 20, 15, 6, "0"),
        ("NA", "Sodium", "mmol/L", 135, 145, 139, 4, "0"),
        ("K", "Potassium", "mmol/L", 3.5, 5.1, 4.2, 0.5, "0.0"),
        ("ALT", "Alanine aminotransferase", "U/L", 7, 56, 35, 25, "0"),
        ("CHOL", "Cholesterol", "mg/dL", 0, 200, 190, 40, "0"),
    ];

    private static readonly string[] Families = ["SANTOSO", "WIJAYA", "HARAHAP", "NASUTION", "PRATAMA", "SIREGAR"];
    private static readonly string[] Givens = ["Budi", "Siti", "Andi", "Dewi", "Rina", "Agus"];

    /// <summary>Analyte names by code.</summary>
    public static IReadOnlyDictionary<string, string> AnalyteNames { get; } = Panel.ToDictionary(p => p.Code, p => p.Name);

    /// <summary>The next result message (one patient, one specimen, the full panel).</summary>
    public AstmMessage NextResult(DateTime? time = null)
    {
        var now = time ?? DateTime.Now;
        var patient = $"P{_random.Next(10000, 99999)}";
        var specimen = (++_specimen).ToString(CultureInfo.InvariantCulture);
        var b = new AstmMessageBuilder()
            .Header("IOTCOM-CHEM^1.2", now)
            .Patient(patient, $"{Families[_random.Next(Families.Length)]}^{Givens[_random.Next(Givens.Length)]}", new DateTime(1950 + _random.Next(60), 1 + _random.Next(12), 1 + _random.Next(28)), _random.Next(2) == 0 ? "M" : "F")
            .Order(specimen, Panel.Select(p => p.Code), collected: now.AddMinutes(-40));
        foreach (var p in Panel)
        {
            var u1 = 1.0 - _random.NextDouble();
            var raw = Math.Max(p.Mean / 10, p.Mean + (p.Sd * Math.Sqrt(-2 * Math.Log(u1)) * Math.Sin(2 * Math.PI * _random.NextDouble())));
            var value = double.Parse(raw.ToString(p.Format, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);   // flag what is reported
            var flag = value < p.Low ? (value < p.Low * 0.7 ? "LL" : "L") : value > p.High ? (value > p.High * 1.6 ? "HH" : "H") : "N";
            b.Result(p.Code, value.ToString(p.Format, CultureInfo.InvariantCulture), p.Units, $"{p.Low.ToString(CultureInfo.InvariantCulture)} to {p.High.ToString(CultureInfo.InvariantCulture)}", flag, completed: now);
        }

        return b.Comment("Synthetic data - not a medical device").Build();
    }
}

/// <summary>Converts ASTM results into HL7 v2.5.1 ORU^R01 (the job of an analyzer bridge).</summary>
public static class AstmToHl7
{
    /// <summary>Builds an ORU^R01 with one OBX per result (codes as local test codes, flags as OBX-8).</summary>
    public static Hl7Message ToOru(AstmMessage message, string receivingApp = "LIS", string receivingFacility = "LAB")
    {
        ArgumentNullException.ThrowIfNull(message);
        var name = (message.PatientName ?? "").Split(message.Header.Delimiters.Component);
        var b = new Hl7MessageBuilder()
            .Header(message.Sender.Length > 0 ? message.Sender : "ANALYZER", "LAB", receivingApp, receivingFacility, "ORU^R01")
            .Segment("PID", "1", null, $"{message.PatientId}^^^LAB^MR", null, $"{(name.Length > 0 ? name[0] : "")}^{(name.Length > 1 ? name[1] : "")}")
            .Segment("OBR", "1", null, message.SpecimenId, "CHEM^Chemistry panel^L");
        var i = 1;
        foreach (var r in message.Results)
            b.Segment("OBX", (i++).ToString(CultureInfo.InvariantCulture), r.Number is null ? "ST" : "NM",
                $"{r.TestCode}^{(AnalyzerSimulator.AnalyteNames.TryGetValue(r.TestCode, out var n) ? n : r.TestCode)}^L", null,
                r.Value, r.Units, r.ReferenceRange.Replace(" to ", "-", StringComparison.Ordinal), r.Flag is "N" or "" ? "N" : r.Flag, null, null, r.Status is { Length: > 0 } s ? s : "F",
                null, null, r.Completed is { } c ? Hl7Time.Format(new DateTimeOffset(c)) : null);
        return b.Build();
    }
}
