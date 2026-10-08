using System.Buffers;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Protocols.AtCommand;

/// <summary>Simulator options.</summary>
public sealed class AtModemSimulatorOptions : IListenerBuilder<AtModemSimulatorOptions>
{
    /// <summary>Listener (TCP serial server, in-memory).</summary>
    public TransportListenerFactory? ListenerFactory { get; set; }

    /// <summary>A single transport (virtual serial port).</summary>
    public TransportFactory? TransportFactory { get; set; }

    /// <summary>Time until the module registers on the network after power-up or AT+CFUN=1.</summary>
    public TimeSpan RegistrationDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>SIM PIN; null means no PIN.</summary>
    public string? Pin { get; set; }

    /// <summary>Friendly name.</summary>
    public string? Name { get; set; }

    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }

    /// <inheritdoc />
    public AtModemSimulatorOptions UseListener(TransportListenerFactory factory)
    {
        (ListenerFactory, TransportFactory) = (factory, null);
        return this;
    }

    /// <inheritdoc />
    public AtModemSimulatorOptions UseTransport(TransportFactory factory)
    {
        (TransportFactory, ListenerFactory) = (factory, null);
        return this;
    }
}

/// <summary>
/// A simulated LTE-M/NB-IoT module: identity (AT+CGMI/CGMM/CGMR/CGSN/CCID), SIM PIN, network registration that
/// reports +CEREG URCs, signal quality that drifts, operator selection, SMS in text mode (send, receive with +CMTI,
/// read, delete) and AT+CFUN. Use <see cref="DeliverSms"/> to make a message arrive.
/// </summary>
public sealed class AtModemSimulator : EndpointBase, IServerEndpoint
{
    private readonly AtModemSimulatorOptions _options;
    private readonly Random _random = new(12);
    private readonly Lock _gate = new();
    private readonly List<ITransport> _peers = [];
    private readonly SortedDictionary<int, (string Status, string Sender, string Time, string Text)> _sms = [];
    private ITransportListener? _listener;
    private CancellationTokenSource? _cts;
    private bool _echo = true;
    private bool _verbose;
    private bool _textMode;
    private bool _pinOk;
    private int _ceregMode;
    private int _function = 1;
    private int _rssi = 21;
    private int _messageReference;
    private string? _smsTo;
    private AtRegistration _registration = AtRegistration.Searching;

    private AtModemSimulator(AtModemSimulatorOptions options) : base("at", options.Logger)
    {
        _options = options;
        Name = options.Name;
        _pinOk = options.Pin is null;
    }

    /// <summary>Creates a simulated module.</summary>
    public static AtModemSimulator Create(Action<AtModemSimulatorOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var o = new AtModemSimulatorOptions();
        configure(o);
        if (o.ListenerFactory is null && o.TransportFactory is null) throw new ArgumentException("A listener or transport is required.", nameof(configure));
        return new AtModemSimulator(o);
    }

    /// <summary>Registration state.</summary>
    public AtRegistration Registration
    {
        get
        {
            lock (_gate) return _registration;
        }
    }

    /// <summary>Messages sent by the client (number, text).</summary>
    public List<(string To, string Text)> SentSms { get; } = [];

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

        _ = Task.Run(() => RadioAsync(cts.Token), CancellationToken.None);
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
    protected override async ValueTask DisposeAsyncCore() => await StopAsync().ConfigureAwait(false);

    /// <summary>Delivers an incoming SMS: stores it and sends +CMTI.</summary>
    public void DeliverSms(string sender, string text)
    {
        int index;
        lock (_gate)
        {
            index = _sms.Count == 0 ? 1 : _sms.Keys.Max() + 1;
            _sms[index] = ("REC UNREAD", sender, DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7)).ToString("yy/MM/dd,HH:mm:ss'+28'", CultureInfo.InvariantCulture), text);
        }

        Broadcast($"+CMTI: \"ME\",{index}");
    }

    private async Task RadioAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(_options.RegistrationDelay, ct).ConfigureAwait(false);
            SetRegistration(AtRegistration.Home);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                lock (_gate) _rssi = Math.Clamp(_rssi + _random.Next(-2, 3), 8, 29);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void SetRegistration(AtRegistration state)
    {
        int mode;
        lock (_gate)
        {
            if (_registration == state) return;
            _registration = state;
            mode = _ceregMode;
        }

        if (mode >= 1) Broadcast(mode >= 2 ? $"+CEREG: {(int)state},\"1A2B\",\"01C3F07\",7" : $"+CEREG: {(int)state}");
    }

    private void Broadcast(string line)
    {
        ITransport[] peers;
        lock (_gate) peers = [.. _peers];
        var bytes = Encoding.ASCII.GetBytes($"\r\n{line}\r\n");
        foreach (var p in peers)
        {
            Tap(FrameDirection.Outbound, bytes, () => $"URC {line}");
            _ = p.Pipe.Output.WriteAsync(bytes).AsTask();
        }
    }

    private async Task ServeAsync(ITransport transport, CancellationToken ct)
    {
        lock (_gate) _peers.Add(transport);
        var line = new StringBuilder();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await transport.Pipe.Input.ReadAsync(ct).ConfigureAwait(false);
                var bytes = result.Buffer.ToArray();
                transport.Pipe.Input.AdvanceTo(result.Buffer.End);
                foreach (var b in bytes)
                {
                    if (_smsTo is not null)
                    {
                        if (b == 0x1A)
                        {
                            lock (_gate) SentSms.Add((_smsTo, line.ToString()));
                            _smsTo = null;
                            line.Clear();
                            await WriteAsync(transport, $"\r\n+CMGS: {++_messageReference}\r\n\r\nOK\r\n", ct).ConfigureAwait(false);
                        }
                        else if (b == 0x1B)
                        {
                            (_smsTo, _) = (null, line.Clear());
                            await WriteAsync(transport, "\r\nOK\r\n", ct).ConfigureAwait(false);
                        }
                        else
                        {
                            line.Append((char)b);
                        }

                        continue;
                    }

                    if (b == '\r')
                    {
                        var command = line.ToString().Trim();
                        line.Clear();
                        if (command.Length == 0) continue;
                        Tap(FrameDirection.Inbound, Encoding.ASCII.GetBytes(command), () => command);
                        var reply = (_echo ? command + "\r" : "") + Handle(command);
                        await WriteAsync(transport, reply, ct).ConfigureAwait(false);
                    }
                    else if (b != '\n')
                    {
                        line.Append((char)b);
                    }
                }

                if (result.IsCompleted) break;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
        finally
        {
            lock (_gate) _peers.Remove(transport);
            await transport.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task WriteAsync(ITransport t, string text, CancellationToken ct)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        Tap(FrameDirection.Outbound, bytes, () => text.Trim().Replace("\r\n", " | ", StringComparison.Ordinal));
        await t.Pipe.Output.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    private string Ok(params string[] lines) => string.Concat(lines.Select(l => $"\r\n{l}\r\n")) + "\r\nOK\r\n";

    private string Cme(int code) => _verbose ? $"\r\n+CME ERROR: {AtParser.CmeErrorName(code)}\r\n" : $"\r\n+CME ERROR: {code}\r\n";

    private string Handle(string command)
    {
        var c = command.ToUpperInvariant();
        if (c == "AT") return Ok();
        if (c is "ATE0" or "ATE1")
        {
            _echo = c == "ATE1";
            return Ok();
        }

        if (c.StartsWith("AT+CMEE=", StringComparison.Ordinal))
        {
            _verbose = c.EndsWith('2');
            return Ok();
        }

        if (c is "ATI" or "AT+CGMI") return Ok(c == "ATI" ? "IoTCom" : "IoTCom");
        if (c == "AT+CGMM") return Ok("SIM-LTE-M1 (simulated)");
        if (c == "AT+CGMR") return Ok("IOTSIM01A07M1G");
        if (c is "AT+CGSN" or "AT+GSN") return Ok("867123050123456");
        if (c == "AT+CPIN?") return _pinOk ? Ok("+CPIN: READY") : Ok("+CPIN: SIM PIN");
        if (c.StartsWith("AT+CPIN=", StringComparison.Ordinal))
        {
            if (_options.Pin is null) return Cme(3);
            if (command[8..].Trim('"') != _options.Pin) return Cme(16);
            _pinOk = true;
            return Ok();
        }

        if (!_pinOk && c.StartsWith("AT+C", StringComparison.Ordinal) && c is not ("AT+CFUN?" or "AT+CMEE?")) return Cme(11);
        if (c is "AT+CCID" or "AT+QCCID") return Ok("+CCID: 8962100123456789012");
        if (c == "AT+CIMI") return Ok("510101234567890");
        if (c == "AT+CSQ")
        {
            lock (_gate) return Ok($"+CSQ: {(_function == 1 ? _rssi : 99)},99");
        }

        if (c.StartsWith("AT+CEREG=", StringComparison.Ordinal))
        {
            _ceregMode = int.Parse(c[9..], CultureInfo.InvariantCulture);
            return Ok();
        }

        if (c is "AT+CEREG?" or "AT+CREG?")
        {
            var prefix = c[2..^1];
            lock (_gate) return Ok($"{prefix}: {_ceregMode},{(int)_registration}{(_ceregMode >= 2 && _registration is AtRegistration.Home or AtRegistration.Roaming ? ",\"1A2B\",\"01C3F07\",7" : "")}");
        }

        if (c == "AT+COPS?")
        {
            lock (_gate) return Ok(_registration is AtRegistration.Home or AtRegistration.Roaming ? "+COPS: 0,0,\"Telkomsel\",8" : "+COPS: 0");
        }

        if (c == "AT+CFUN?") return Ok($"+CFUN: {_function}");
        if (c.StartsWith("AT+CFUN=", StringComparison.Ordinal))
        {
            _function = c[8] - '0';
            if (_function == 0 || _function == 4) SetRegistration(AtRegistration.NotRegistered);
            else _ = Task.Run(async () =>
            {
                SetRegistration(AtRegistration.Searching);
                await Task.Delay(_options.RegistrationDelay).ConfigureAwait(false);
                SetRegistration(AtRegistration.Home);
            });
            return Ok();
        }

        if (c.StartsWith("AT+CMGF=", StringComparison.Ordinal))
        {
            _textMode = c.EndsWith('1');
            return Ok();
        }

        if (c.StartsWith("AT+CMGS=", StringComparison.Ordinal))
        {
            if (!_textMode) return "\r\n+CMS ERROR: 304\r\n";
            lock (_gate)
                if (_registration is not (AtRegistration.Home or AtRegistration.Roaming)) return "\r\n+CMS ERROR: 331\r\n";
            _smsTo = command[8..].Trim('"');
            return "\r\n> ";
        }

        if (c.StartsWith("AT+CMGR=", StringComparison.Ordinal))
        {
            var index = int.Parse(c[8..], CultureInfo.InvariantCulture);
            lock (_gate)
            {
                if (!_sms.TryGetValue(index, out var m)) return "\r\n+CMS ERROR: 321\r\n";
                _sms[index] = m with { Status = "REC READ" };
                return Ok($"+CMGR: \"{m.Status}\",\"{m.Sender}\",,\"{m.Time}\"", m.Text);
            }
        }

        if (c.StartsWith("AT+CMGD=", StringComparison.Ordinal))
        {
            lock (_gate) _sms.Remove(int.Parse(c[8..].Split(',')[0], CultureInfo.InvariantCulture));
            return Ok();
        }

        if (c.StartsWith("AT+CMGL", StringComparison.Ordinal))
        {
            lock (_gate)
                return Ok([.. _sms.SelectMany(kv => new[] { $"+CMGL: {kv.Key},\"{kv.Value.Status}\",\"{kv.Value.Sender}\",,\"{kv.Value.Time}\"", kv.Value.Text })]);
        }

        return "\r\nERROR\r\n";
    }
}
