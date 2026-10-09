using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Protocols.Ntp;

/// <summary>The four timestamps of one exchange and what follows from them (RFC 5905 §8).</summary>
public static class NtpMath
{
    /// <summary>Clock offset θ = ((T2 − T1) + (T3 − T4)) / 2: how far the local clock is behind the server.</summary>
    public static TimeSpan Offset(NtpTimestamp t1, NtpTimestamp t2, NtpTimestamp t3, NtpTimestamp t4) =>
        TimeSpan.FromSeconds((NtpTimestamp.Difference(t2, t1) + NtpTimestamp.Difference(t3, t4)) / 2);

    /// <summary>Round-trip delay δ = (T4 − T1) − (T3 − T2), never below zero.</summary>
    public static TimeSpan Delay(NtpTimestamp t1, NtpTimestamp t2, NtpTimestamp t3, NtpTimestamp t4) =>
        TimeSpan.FromSeconds(Math.Max(0, NtpTimestamp.Difference(t4, t1) - NtpTimestamp.Difference(t3, t2)));
}

/// <summary>The server refused service with a kiss-o'-death packet (stratum 0).</summary>
public sealed class NtpKissOfDeathException : DeviceException
{
    /// <summary>Creates the exception.</summary>
    public NtpKissOfDeathException() : base("Kiss-o'-death.") => Code = "";

    /// <summary>Creates the exception for a kiss code.</summary>
    public NtpKissOfDeathException(string code) : base($"The NTP server sent kiss-o'-death {code} ({Explain(code)}).") => Code = code;

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public NtpKissOfDeathException(string message, Exception? inner) : base(message, inner) => Code = "";

    /// <summary>The kiss code (RATE, DENY, RSTR…).</summary>
    public new string Code { get; }

    private static string Explain(string code) => code switch
    {
        "RATE" => "polling too often; back off",
        "DENY" or "RSTR" => "access denied; stop querying this server",
        "INIT" => "the association is not yet synchronised",
        _ => "see RFC 5905 §7.4",
    };
}

/// <summary>The outcome of one SNTP exchange.</summary>
/// <param name="Server">Server address.</param>
/// <param name="Response">The server's packet.</param>
/// <param name="T1">Client transmit time.</param>
/// <param name="T2">Server receive time.</param>
/// <param name="T3">Server transmit time.</param>
/// <param name="T4">Client receive time.</param>
public sealed record NtpResult(EndPoint Server, NtpPacket Response, NtpTimestamp T1, NtpTimestamp T2, NtpTimestamp T3, NtpTimestamp T4)
{
    /// <summary>Clock offset: add it to the local clock to get the server's time.</summary>
    public TimeSpan Offset => NtpMath.Offset(T1, T2, T3, T4);

    /// <summary>Round-trip delay through the network.</summary>
    public TimeSpan RoundTripDelay => NtpMath.Delay(T1, T2, T3, T4);

    /// <summary>Server stratum.</summary>
    public byte Stratum => Response.Stratum;

    /// <inheritdoc />
    public override string ToString() =>
        $"{Server}: offset {Offset.TotalMilliseconds:+0.000;-0.000} ms, delay {RoundTripDelay.TotalMilliseconds:0.000} ms, stratum {Stratum}, ref {Response.ReferenceIdText}";
}

/// <summary>A combined estimate from several servers.</summary>
/// <param name="Offset">Median offset of the accepted results.</param>
/// <param name="Accepted">Results used (those with a delay close to the best one).</param>
/// <param name="Failed">Servers that did not answer usefully, with the reason.</param>
public sealed record NtpEstimate(TimeSpan Offset, IReadOnlyList<NtpResult> Accepted, IReadOnlyList<(EndPoint Server, string Reason)> Failed);

/// <summary>Options for <see cref="SntpClient"/>.</summary>
public sealed class SntpClientOptions : IDatagramBuilder<SntpClientOptions>
{
    /// <summary>Servers to query (host names are resolved per query).</summary>
    public List<(string Host, int Port)> Servers { get; } = [];

    /// <summary>Server endpoints (used as given; for in-memory networks).</summary>
    public List<EndPoint> Endpoints { get; } = [];

    /// <summary>Local socket factory (default UDP on any port).</summary>
    public DatagramTransportFactory? TransportFactory { get; set; }

    /// <summary>Time to wait for an answer (default 2 s).</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Protocol version written in requests (default 4).</summary>
    public byte Version { get; set; } = 4;

    /// <summary>
    /// Minimum time between two queries to the same server (default 15 s, RFC 4330 §10). Faster calls wait. Public pool
    /// servers send kiss-o'-death RATE to clients that poll faster.
    /// </summary>
    public TimeSpan MinimumPollInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>The local clock (UTC). Replace it to discipline a simulated or external clock.</summary>
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    /// <summary>Endpoint name.</summary>
    public string? Name { get; set; }

    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }

    /// <inheritdoc />
    public SntpClientOptions UseDatagramTransport(DatagramTransportFactory factory)
    {
        TransportFactory = factory;
        return this;
    }

    /// <summary>Adds a server by name (pool.ntp.org, time.cloudflare.com…).</summary>
    public SntpClientOptions UseServer(string host, int port = NtpPacket.DefaultPort)
    {
        Servers.Add((host, port));
        return this;
    }

    /// <summary>Adds a server endpoint.</summary>
    public SntpClientOptions UseServer(EndPoint server)
    {
        Endpoints.Add(server);
        return this;
    }
}

/// <summary>
/// SNTP client (RFC 4330 / RFC 5905 client mode): one request per query, with the RFC checks on the answer (mode,
/// originate timestamp, non-zero transmit time, stratum, leap alarm) and kiss-o'-death handling. It measures; it never
/// sets the system clock — apply <see cref="NtpResult.Offset"/> to your own clock if you want to.
/// </summary>
public sealed class SntpClient : EndpointBase
{
    private readonly SntpClientOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, long> _lastQuery = new();
    private IDatagramTransport? _transport;

    private SntpClient(SntpClientOptions options) : base("ntp", options.Logger)
    {
        _options = options;
        Name = options.Name;
    }

    /// <summary>Creates a client.</summary>
    public static SntpClient Create(Action<SntpClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var o = new SntpClientOptions();
        configure(o);
        if (o.Servers.Count == 0 && o.Endpoints.Count == 0) throw new ArgumentException("Add at least one server (UseServer).", nameof(configure));
        o.TransportFactory ??= () => new Transports.UdpDatagramTransport(new IPEndPoint(IPAddress.Any, 0));
        return new SntpClient(o);
    }

    private async Task<IReadOnlyList<EndPoint>> ResolveAsync(CancellationToken ct)
    {
        var list = new List<EndPoint>(_options.Endpoints);
        foreach (var (host, port) in _options.Servers)
        {
            if (IPAddress.TryParse(host, out var ip))
            {
                list.Add(new IPEndPoint(ip, port));
                continue;
            }

            try
            {
                var addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
                var address = addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
                if (address is not null) list.Add(new IPEndPoint(address, port));
            }
            catch (System.Net.Sockets.SocketException ex)
            {
                Logger.LogDebug(ex, "Cannot resolve {Host}", host);
            }
        }

        return list;
    }

    /// <summary>Queries the first configured server.</summary>
    public async Task<NtpResult> QueryAsync(CancellationToken ct = default)
    {
        var servers = await ResolveAsync(ct).ConfigureAwait(false);
        if (servers.Count == 0) throw new TransportException("No NTP server address could be resolved.");
        return await QueryAsync(servers[0], ct).ConfigureAwait(false);
    }

    /// <summary>Queries one server.</summary>
    /// <exception cref="IoTComTimeoutException">No valid answer in time.</exception>
    /// <exception cref="NtpKissOfDeathException">The server refused service.</exception>
    /// <exception cref="DeviceException">The server is not synchronised.</exception>
    public async Task<NtpResult> QueryAsync(EndPoint server, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        ThrowIfDisposed();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var key = server.ToString()!;
            if (_lastQuery.TryGetValue(key, out var last))
            {
                var wait = _options.MinimumPollInterval.TotalMilliseconds - (Environment.TickCount64 - last);
                if (wait > 0) await Task.Delay(TimeSpan.FromMilliseconds(wait), ct).ConfigureAwait(false);
            }

            _lastQuery[key] = Environment.TickCount64;
            var transport = _transport ??= _options.TransportFactory!();
            // T1 also serves as the nonce: the low fraction bits are random so a forged answer cannot guess them.
            var t1 = NtpTimestamp.FromDateTime(_options.Clock());
            t1 = new NtpTimestamp((t1.Raw & ~0xFFFFUL) | (uint)RandomNumberGenerator.GetInt32(0x10000));
            var request = new NtpPacket { Version = _options.Version, Mode = NtpMode.Client, Transmit = t1 }.Encode();
            Tap(FrameDirection.Outbound, request, () => $"client request to {server}");
            await transport.SendAsync(request, server, ct).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_options.Timeout);
            while (true)
            {
                Datagram d;
                try
                {
                    d = await transport.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new IoTComTimeoutException($"No NTP answer from {server} within {_options.Timeout.TotalSeconds:0.#} s.");
                }

                var t4 = NtpTimestamp.FromDateTime(_options.Clock());
                if (!d.Remote.Equals(server) || d.Data.Length < NtpPacket.HeaderLength) continue;
                var response = NtpPacket.Parse(d.Data.Span);
                Tap(FrameDirection.Inbound, d.Data.Span, () => response.ToString());
                if (response.Originate != t1) continue;   // stale, duplicated or forged
                if (response.Mode != NtpMode.Server) continue;
                if (response.IsKissOfDeath) throw new NtpKissOfDeathException(response.ReferenceIdText);
                if (response.Transmit.IsZero) throw new ProtocolException($"{server} answered with a zero transmit timestamp.");
                if (response.Leap == NtpLeap.Unsynchronised || response.Stratum > 15)
                    throw new DeviceException($"{server} is not synchronised (leap {response.Leap}, stratum {response.Stratum}).");
                return new NtpResult(server, response, t1, response.Receive, response.Transmit, t4);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Queries every configured server and combines the answers: results whose delay exceeds the best delay by more than
    /// the best delay itself (and at least 50 ms) are dropped, and the median offset of the rest is the estimate.
    /// </summary>
    /// <exception cref="IoTComException">No server gave a usable answer.</exception>
    public async Task<NtpEstimate> SynchronizeAsync(CancellationToken ct = default)
    {
        var results = new List<NtpResult>();
        var failed = new List<(EndPoint, string)>();
        foreach (var server in await ResolveAsync(ct).ConfigureAwait(false))
        {
            try
            {
                results.Add(await QueryAsync(server, ct).ConfigureAwait(false));
            }
            catch (IoTComException ex)
            {
                failed.Add((server, ex.Message));
            }
        }

        if (results.Count == 0) throw new IoTComException("No NTP server gave a usable answer: " + string.Join("; ", failed.Select(f => f.Item2)));
        // Keep answers whose delay is within twice the best one, but never cut closer than 50 ms above it: on a fast
        // network scheduler jitter alone exceeds "twice the best" and would drop good servers.
        var best = results.Min(r => r.RoundTripDelay);
        var limit = best + TimeSpan.FromTicks(Math.Max(best.Ticks, TimeSpan.FromMilliseconds(50).Ticks));
        var accepted = results.Where(r => r.RoundTripDelay <= limit).OrderBy(r => r.Offset).ToList();
        var mid = accepted.Count / 2;
        var offset = accepted.Count % 2 == 1 ? accepted[mid].Offset : (accepted[mid - 1].Offset + accepted[mid].Offset) / 2;
        return new NtpEstimate(offset, accepted, failed);
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore()
    {
        if (_transport is not null) await _transport.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
