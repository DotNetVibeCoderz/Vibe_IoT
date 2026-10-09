using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Protocols.Ntp;

/// <summary>Options for <see cref="NtpServer"/>.</summary>
public sealed class NtpServerOptions : IDatagramBuilder<NtpServerOptions>
{
    /// <summary>Socket factory (for example <c>UseUdp(123)</c> or <c>UseInMemory(network, endpoint)</c>).</summary>
    public DatagramTransportFactory? TransportFactory { get; set; }

    /// <summary>The clock this server tells (UTC).</summary>
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    /// <summary>Stratum (default 1 with reference "GPS"; use 2+ with <see cref="NtpPacket.ReferenceAddress"/> for a relay).</summary>
    public byte Stratum { get; set; } = 1;

    /// <summary>Reference identifier (default "GPS").</summary>
    public uint ReferenceId { get; set; } = NtpPacket.ReferenceCode("GPS");

    /// <summary>Precision exponent (default −20, about 1 µs).</summary>
    public sbyte Precision { get; set; } = -20;

    /// <summary>Root delay advertised, seconds.</summary>
    public double RootDelay { get; set; }

    /// <summary>Root dispersion advertised, seconds.</summary>
    public double RootDispersion { get; set; } = 0.0001;

    /// <summary>Leap indicator advertised (set <see cref="NtpLeap.Unsynchronised"/> while the reference is lost).</summary>
    public NtpLeap Leap { get; set; }

    /// <summary>Minimum interval between two answers to the same client address; faster clients get kiss-o'-death RATE (default off).</summary>
    public TimeSpan RateLimit { get; set; }

    /// <summary>Simulated time between receiving a request and sending the answer (shows up as T3 − T2).</summary>
    public TimeSpan ProcessingDelay { get; set; }

    /// <summary>Endpoint name.</summary>
    public string? Name { get; set; }

    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }

    /// <inheritdoc />
    public NtpServerOptions UseDatagramTransport(DatagramTransportFactory factory)
    {
        TransportFactory = factory;
        return this;
    }

    /// <summary>Sets the reference identifier from a code ("GPS", "PPS", "DCF", "LOCL"…).</summary>
    public NtpServerOptions WithReference(string code, byte stratum = 1)
    {
        ReferenceId = NtpPacket.ReferenceCode(code);
        Stratum = stratum;
        return this;
    }
}

/// <summary>
/// NTP server (answers client-mode requests, RFC 5905 server mode): echoes the client's transmit timestamp as
/// originate, stamps receive and transmit times from <see cref="NtpServerOptions.Clock"/>, and can rate-limit clients
/// with kiss-o'-death RATE. Symmetric, broadcast and control modes are ignored.
/// </summary>
public sealed class NtpServer : EndpointBase, IServerEndpoint
{
    private readonly NtpServerOptions _options;
    private readonly ConcurrentDictionary<IPAddress, long> _lastAnswer = new();
    private IDatagramTransport? _transport;
    private CancellationTokenSource? _cts;
    private long _answered, _kisses;

    private NtpServer(NtpServerOptions options) : base("ntp", options.Logger)
    {
        _options = options;
        Name = options.Name;
    }

    /// <summary>Creates a server.</summary>
    public static NtpServer Create(Action<NtpServerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var o = new NtpServerOptions();
        configure(o);
        if (o.TransportFactory is null) throw new ArgumentException("A datagram transport is required (UseUdp(123), UseInMemory).", nameof(configure));
        return new NtpServer(o);
    }

    /// <summary>Requests answered.</summary>
    public long Answered => Interlocked.Read(ref _answered);

    /// <summary>Kiss-o'-death packets sent.</summary>
    public long KissesSent => Interlocked.Read(ref _kisses);

    /// <summary>Leap indicator advertised from now on.</summary>
    public NtpLeap Leap
    {
        get => _options.Leap;
        set => _options.Leap = value;
    }

    /// <summary>Raised for every answered request: client address and whether it was a kiss-o'-death.</summary>
    public event Action<EndPoint, bool>? Served;

    /// <summary>Local endpoint after start.</summary>
    public EndPoint? LocalEndPoint => _transport?.LocalEndPoint;

    /// <inheritdoc />
    public ValueTask StartAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_cts is not null) return ValueTask.CompletedTask;
        SetState(EndpointState.Connecting);
        _transport = _options.TransportFactory!();
        var cts = _cts = new CancellationTokenSource();
        _ = Task.Run(() => LoopAsync(_transport, cts.Token), CancellationToken.None);
        SetState(EndpointState.Listening);
        return ValueTask.CompletedTask;
    }

    private async Task LoopAsync(IDatagramTransport transport, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Datagram d;
            try
            {
                d = await transport.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or System.Threading.Channels.ChannelClosedException or ObjectDisposedException)
            {
                break;
            }
            catch (System.Net.Sockets.SocketException ex)
            {
                Logger.LogDebug(ex, "NTP receive failed");
                continue;
            }

            var t2 = NtpTimestamp.FromDateTime(_options.Clock());
            if (d.Data.Length < NtpPacket.HeaderLength) continue;
            var request = NtpPacket.Parse(d.Data.Span);
            Tap(FrameDirection.Inbound, d.Data.Span, () => request.ToString());
            if (request.Mode != NtpMode.Client || request.Version is < 1 or > 4) continue;
            _ = AnswerAsync(transport, d.Remote, request, t2, ct);
        }
    }

    private async Task AnswerAsync(IDatagramTransport transport, EndPoint client, NtpPacket request, NtpTimestamp t2, CancellationToken ct)
    {
        try
        {
            var kiss = false;
            if (_options.RateLimit > TimeSpan.Zero && client is IPEndPoint ip)
            {
                var now = Environment.TickCount64;
                kiss = _lastAnswer.TryGetValue(ip.Address, out var last) && now - last < _options.RateLimit.TotalMilliseconds;
                if (!kiss) _lastAnswer[ip.Address] = now;
            }

            if (!kiss && _options.ProcessingDelay > TimeSpan.Zero) await Task.Delay(_options.ProcessingDelay, ct).ConfigureAwait(false);
            var reply = new NtpPacket
            {
                Leap = kiss ? NtpLeap.Unsynchronised : _options.Leap,
                Version = request.Version,
                Mode = NtpMode.Server,
                Stratum = kiss ? (byte)0 : _options.Stratum,
                Poll = request.Poll,
                Precision = _options.Precision,
                RootDelay = _options.RootDelay,
                RootDispersion = _options.RootDispersion,
                ReferenceId = kiss ? NtpPacket.ReferenceCode("RATE") : _options.ReferenceId,
                Reference = kiss ? default : NtpTimestamp.FromDateTime(_options.Clock().AddSeconds(-8)),
                Originate = request.Transmit,
                Receive = kiss ? default : t2,
                Transmit = kiss ? default : NtpTimestamp.FromDateTime(_options.Clock()),
            };
            var bytes = reply.Encode();
            Tap(FrameDirection.Outbound, bytes, () => reply.ToString());
            await transport.SendAsync(bytes, client, ct).ConfigureAwait(false);
            Interlocked.Increment(ref kiss ? ref _kisses : ref _answered);
            Served?.Invoke(client, kiss);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or System.Net.Sockets.SocketException)
        {
            Logger.LogDebug(ex, "NTP answer to {Client} failed", client);
        }
    }

    /// <inheritdoc />
    public async ValueTask StopAsync(CancellationToken ct = default)
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts is null) return;
        SetState(EndpointState.Stopping);
        await cts.CancelAsync().ConfigureAwait(false);
        if (_transport is not null) await _transport.DisposeAsync().ConfigureAwait(false);
        _transport = null;
        cts.Dispose();
        SetState(EndpointState.Disconnected);
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore() => await StopAsync().ConfigureAwait(false);
}

/// <summary>
/// A clock that is off and drifts: <c>UtcNow = real time + offset + drift × elapsed</c>. Use it to simulate devices with
/// cheap crystals (tens of ppm) and to watch SNTP corrections bring them back.
/// </summary>
public sealed class DriftingClock
{
    private readonly Func<DateTime> _reference;
    private readonly Lock _gate = new();
    private DateTime _anchorReal;
    private TimeSpan _anchorOffset;
    private double _ppm;

    /// <summary>Creates a clock with an initial offset and a drift in parts per million (positive = runs fast).</summary>
    public DriftingClock(TimeSpan offset, double driftPpm, Func<DateTime>? reference = null)
    {
        _reference = reference ?? (() => DateTime.UtcNow);
        _anchorReal = _reference();
        _anchorOffset = offset;
        _ppm = driftPpm;
    }

    /// <summary>Drift in parts per million.</summary>
    public double DriftPpm
    {
        get
        {
            lock (_gate) return _ppm;
        }
    }

    /// <summary>How far this clock is from the reference right now.</summary>
    public TimeSpan Error
    {
        get
        {
            lock (_gate) return ErrorAt(_reference());
        }
    }

    private TimeSpan ErrorAt(DateTime real) => _anchorOffset + TimeSpan.FromTicks((long)((real - _anchorReal).Ticks * _ppm / 1_000_000));

    /// <summary>The clock's current reading (UTC).</summary>
    public DateTime UtcNow
    {
        get
        {
            lock (_gate)
            {
                var real = _reference();
                return real + ErrorAt(real);
            }
        }
    }

    /// <summary>Steps the clock by <paramref name="correction"/> (add an NTP offset to correct it).</summary>
    public void Step(TimeSpan correction)
    {
        lock (_gate)
        {
            var real = _reference();
            _anchorOffset = ErrorAt(real) + correction;
            _anchorReal = real;
        }
    }

    /// <summary>Changes the drift (for example after estimating it from successive offsets).</summary>
    public void SetDrift(double ppm)
    {
        lock (_gate)
        {
            var real = _reference();
            _anchorOffset = ErrorAt(real);
            _anchorReal = real;
            _ppm = ppm;
        }
    }
}
