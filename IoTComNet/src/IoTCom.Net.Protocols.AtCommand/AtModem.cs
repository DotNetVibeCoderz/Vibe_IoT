using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Protocols.AtCommand;

/// <summary>AT modem options.</summary>
public sealed class AtModemOptions : ITransportBuilder<AtModemOptions>
{
    /// <summary>Transport (serial port of the module, or TCP for a serial server).</summary>
    public TransportFactory? TransportFactory { get; set; }

    /// <summary>Default time to wait for a final result.</summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Send ATE0 and AT+CMEE=2 on connect (no echo, verbose errors).</summary>
    public bool Initialise { get; set; } = true;

    /// <summary>Block commands that change the module or the network (AT+CFUN, dialling, SMS, writes to the SIM).</summary>
    public bool ReadOnly { get; set; }

    /// <summary>Friendly name.</summary>
    public string? Name { get; set; }

    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }

    /// <inheritdoc />
    public AtModemOptions UseTransport(TransportFactory factory)
    {
        TransportFactory = factory;
        return this;
    }
}

/// <summary>Module identity and network state.</summary>
public sealed record AtModemInfo(string Manufacturer, string Model, string Revision, string Imei, string? Iccid, string SimStatus, AtSignal Signal, AtRegistration Registration, string? Operator, string? AccessTechnology);

/// <summary>An SMS read from the module.</summary>
public sealed record AtSms(int Index, string Status, string Sender, string? Timestamp, string Text);

/// <summary>
/// A cellular / GNSS module driven with AT commands (3GPP TS 27.007/27.005): one command at a time with timeouts,
/// unsolicited result codes as a stream, SMS in text mode, and helpers for identity, signal and registration.
/// </summary>
/// <example>
/// <code>
/// await using var modem = AtModem.Create(o => o.UseSerial("COM7", 115200));
/// await modem.ConnectAsync();
/// Console.WriteLine(await modem.GetSignalAsync());           // -71 dBm (4/4)
/// await foreach (var urc in modem.UrcsAsync()) Console.WriteLine(urc.Line);
/// </code>
/// </example>
public sealed class AtModem : EndpointBase, IClientEndpoint
{
    private static readonly string[] Writes = ["AT+CFUN", "ATD", "AT+CMGS", "AT+CMGD", "AT+CMGW", "AT+CPWD", "AT+CLCK", "AT+COPS=", "AT+CGDCONT=", "AT+QPOWD", "AT+CPOWD", "AT&W", "ATZ"];
    private readonly AtModemOptions _options;
    private readonly AtParser _parser = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Channel<AtUrc> _urcs = Channel.CreateBounded<AtUrc>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Lock _parserGate = new();
    private ITransport? _transport;
    private CancellationTokenSource? _cts;
    private TaskCompletionSource<AtResponse>? _waiting;

    private AtModem(AtModemOptions options) : base("at", options.Logger)
    {
        _options = options;
        Name = options.Name;
    }

    /// <summary>Creates a modem client.</summary>
    public static AtModem Create(Action<AtModemOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var o = new AtModemOptions();
        configure(o);
        if (o.TransportFactory is null) throw new ArgumentException("A transport is required (UseSerial, UseTcp, UseInMemory).", nameof(configure));
        return new AtModem(o);
    }

    /// <summary>Raised for every unsolicited result code.</summary>
    public event EventHandler<AtUrc>? UrcReceived;

    /// <inheritdoc />
    public async ValueTask ConnectAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_transport is not null) return;
        SetState(EndpointState.Connecting);
        var t = _options.TransportFactory!();
        try
        {
            await t.OpenAsync(ct).ConfigureAwait(false);
            _transport = t;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _ = Task.Run(() => ReadLoopAsync(t, token), CancellationToken.None);
            if (_options.Initialise)
            {
                await SendAsync("ATE0", ct: ct).ConfigureAwait(false);
                await SendAsync("AT+CMEE=2", ct: ct).ConfigureAwait(false);
            }

            SetState(EndpointState.Connected);
        }
        catch (Exception ex)
        {
            await DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
            SetState(EndpointState.Disconnected, ex);
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisconnectAsync(CancellationToken ct = default)
    {
        var t = Interlocked.Exchange(ref _transport, null);
        if (t is null) return;
        if (_cts is not null) await _cts.CancelAsync().ConfigureAwait(false);
        await t.DisposeAsync().ConfigureAwait(false);
        _cts?.Dispose();
        _cts = null;
        SetState(EndpointState.Disconnected);
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _urcs.Writer.TryComplete();
        _gate.Dispose();
    }

    /// <summary>Streams unsolicited result codes.</summary>
    public async IAsyncEnumerable<AtUrc> UrcsAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var u in _urcs.Reader.ReadAllAsync(ct).ConfigureAwait(false)) yield return u;
    }

    /// <summary>Sends a command and waits for its final result (does not throw on ERROR; check the result).</summary>
    public async Task<AtResponse> SendAsync(string command, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (_options.ReadOnly && Writes.Any(w => command.StartsWith(w, StringComparison.OrdinalIgnoreCase)))
            throw new ReadOnlyModeException($"{command} is blocked: the AT modem client is read-only.");
        return await ExchangeAsync(command, Encoding.ASCII.GetBytes(command + "\r"), timeout, ct).ConfigureAwait(false);
    }

    /// <summary>Sends a command and throws <see cref="DeviceException"/> unless it succeeds.</summary>
    public async Task<AtResponse> SendCheckedAsync(string command, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var r = await SendAsync(command, timeout, ct).ConfigureAwait(false);
        return r.IsSuccess ? r : throw new DeviceException($"{command}: {r.Result}{(r.ErrorCode is { } c ? $" {c} ({AtParser.CmeErrorName(c)})" : r.ErrorText is { } t ? $" ({t})" : "")}");
    }

    private async Task<AtResponse> ExchangeAsync(string command, byte[] bytes, TimeSpan? timeout, CancellationToken ct)
    {
        var t = _transport ?? throw new TransportException("Not connected (call ConnectAsync).");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var tcs = new TaskCompletionSource<AtResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_parserGate)
            {
                _waiting = tcs;
                _parser.Begin(command);
            }

            Tap(FrameDirection.Outbound, bytes, () => command);
            await t.Pipe.Output.WriteAsync(bytes, ct).ConfigureAwait(false);
            try
            {
                return await tcs.Task.WaitAsync(timeout ?? _options.CommandTimeout, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                lock (_parserGate)
                {
                    _waiting = null;
                    _parser.Begin("");   // reset; a late answer becomes a stray line
                    _parser.Feed("\r\nOK\r\n"u8);
                    _parser.TryTakeResponse(out _);
                }

                throw new IoTComTimeoutException($"No final result for {command} within {(timeout ?? _options.CommandTimeout).TotalSeconds:0.#} s.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ReadLoopAsync(ITransport t, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await t.Pipe.Input.ReadAsync(ct).ConfigureAwait(false);
                var bytes = result.Buffer.ToArray();
                t.Pipe.Input.AdvanceTo(result.Buffer.End);
                var responses = new List<(TaskCompletionSource<AtResponse>?, AtResponse)>();
                var urcs = new List<AtUrc>();
                lock (_parserGate)
                {
                    _parser.Feed(bytes);
                    while (_parser.TryTakeResponse(out var r))
                    {
                        responses.Add((_waiting, r!));
                        _waiting = null;
                    }

                    while (_parser.TryTakeUrc(out var u)) urcs.Add(u!);
                }

                foreach (var (waiter, r) in responses)
                {
                    Tap(FrameDirection.Inbound, Encoding.ASCII.GetBytes(string.Join("\r\n", [.. r.Lines, r.Result.ToString().ToUpperInvariant()])), () => r.ToString());
                    waiter?.TrySetResult(r);
                }

                foreach (var u in urcs)
                {
                    Tap(FrameDirection.Inbound, Encoding.ASCII.GetBytes(u.Line), () => $"URC {u.Line}");
                    _urcs.Writer.TryWrite(u);
                    UrcReceived?.Invoke(this, u);
                }

                if (result.IsCompleted) break;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    /// <summary>Signal quality (AT+CSQ).</summary>
    public async Task<AtSignal> GetSignalAsync(CancellationToken ct = default)
    {
        var v = (await SendCheckedAsync("AT+CSQ", ct: ct).ConfigureAwait(false)).Values("+CSQ") ?? throw new ProtocolException("AT+CSQ returned no +CSQ line.");
        return new AtSignal(int.Parse(v[0], CultureInfo.InvariantCulture), v.Count > 1 ? int.Parse(v[1], CultureInfo.InvariantCulture) : 99);
    }

    /// <summary>EPS registration (AT+CEREG?), falling back to AT+CREG?.</summary>
    public async Task<AtRegistration> GetRegistrationAsync(CancellationToken ct = default)
    {
        foreach (var (cmd, prefix) in new[] { ("AT+CEREG?", "+CEREG"), ("AT+CREG?", "+CREG") })
        {
            var r = await SendAsync(cmd, ct: ct).ConfigureAwait(false);
            if (r.IsSuccess && r.Values(prefix) is { Count: >= 2 } v) return (AtRegistration)int.Parse(v[1], CultureInfo.InvariantCulture);
        }

        return AtRegistration.Unknown;
    }

    /// <summary>Identity, SIM and network state.</summary>
    public async Task<AtModemInfo> GetInfoAsync(CancellationToken ct = default)
    {
        async Task<string> One(string cmd) => (await SendAsync(cmd, ct: ct).ConfigureAwait(false)) is { IsSuccess: true, Lines.Count: > 0 } r
            ? r.Lines[0].Contains(':', StringComparison.Ordinal) ? r.Lines[0][(r.Lines[0].IndexOf(':', StringComparison.Ordinal) + 1)..].Trim() : r.Lines[0] : "";
        var manufacturer = await One("AT+CGMI").ConfigureAwait(false);
        var model = await One("AT+CGMM").ConfigureAwait(false);
        var revision = await One("AT+CGMR").ConfigureAwait(false);
        var imei = await One("AT+CGSN").ConfigureAwait(false);
        var iccid = await One("AT+CCID").ConfigureAwait(false);
        var sim = await One("AT+CPIN?").ConfigureAwait(false);
        var signal = await GetSignalAsync(ct).ConfigureAwait(false);
        var registration = await GetRegistrationAsync(ct).ConfigureAwait(false);
        var cops = (await SendAsync("AT+COPS?", ct: ct).ConfigureAwait(false)).Values("+COPS");
        string? act = cops is { Count: >= 4 } ? cops[3] switch { "0" => "GSM", "2" => "UTRAN", "7" => "LTE", "8" => "LTE-M", "9" => "NB-IoT", "13" => "NR", var x => x } : null;
        return new AtModemInfo(manufacturer, model, revision, imei, iccid.Length > 0 ? iccid : null, sim, signal, registration, cops is { Count: >= 3 } ? cops[2] : null, act);
    }

    /// <summary>Sends an SMS in text mode (AT+CMGF=1, AT+CMGS, text, Ctrl-Z) and returns the message reference.</summary>
    public async Task<int> SendSmsAsync(string number, string text, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(number);
        ArgumentNullException.ThrowIfNull(text);
        await SendCheckedAsync("AT+CMGF=1", ct: ct).ConfigureAwait(false);
        var prompt = await SendAsync($"AT+CMGS=\"{number}\"", ct: ct).ConfigureAwait(false);
        if (prompt.Result != AtResult.Prompt) throw new DeviceException($"AT+CMGS: expected the '>' prompt, got {prompt.Result}.");
        var sent = await ExchangeAsync("SMS", Encoding.ASCII.GetBytes(text + "\u001A"), TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        if (!sent.IsSuccess) throw new DeviceException($"SMS to {number} failed: {sent.Result} {sent.ErrorCode} {sent.ErrorText}".TrimEnd());
        return sent.Values("+CMGS") is { Count: > 0 } v && int.TryParse(v[0], CultureInfo.InvariantCulture, out var mr) ? mr : -1;
    }

    /// <summary>Reads an SMS from storage (AT+CMGR in text mode).</summary>
    public async Task<AtSms> ReadSmsAsync(int index, CancellationToken ct = default)
    {
        await SendCheckedAsync("AT+CMGF=1", ct: ct).ConfigureAwait(false);
        var r = await SendCheckedAsync($"AT+CMGR={index}", ct: ct).ConfigureAwait(false);
        var head = r.Values("+CMGR") ?? throw new DeviceException($"No SMS at index {index}.");
        var body = string.Join("\n", r.Lines.SkipWhile(l => !l.StartsWith("+CMGR:", StringComparison.Ordinal)).Skip(1));
        return new AtSms(index, head[0], head.Count > 1 ? head[1] : "", head.Count > 3 ? head[3] : null, body);
    }
}
