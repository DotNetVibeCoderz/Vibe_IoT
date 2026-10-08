using System.Collections.Concurrent;
using System.Net;
using IoTCom.Net.Transports;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Protocols.Mdns;

/// <summary>A DNS-SD service instance (RFC 6763).</summary>
public sealed record MdnsService
{
    /// <summary>Instance name, e.g. "Line 1 PLC gateway".</summary>
    public required string Instance { get; init; }

    /// <summary>Service type, e.g. "_modbus._tcp".</summary>
    public required string Type { get; init; }

    /// <summary>Domain ("local").</summary>
    public string Domain { get; init; } = "local";

    /// <summary>Port.</summary>
    public ushort Port { get; init; }

    /// <summary>Host name, e.g. "gateway-01.local".</summary>
    public string Host { get; init; } = Environment.MachineName.ToLowerInvariant() + ".local";

    /// <summary>Addresses of the host.</summary>
    public IReadOnlyList<IPAddress> Addresses { get; init; } = [];

    /// <summary>TXT key/value pairs.</summary>
    public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();

    /// <summary>"_modbus._tcp.local".</summary>
    public string TypeName => $"{Type}.{Domain}";

    /// <summary>"Line 1 PLC gateway._modbus._tcp.local".</summary>
    public string FullName => $"{Instance}.{TypeName}";

    /// <summary>First IPv4 address, if any.</summary>
    public IPAddress? Address => Addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? (Addresses.Count > 0 ? Addresses[0] : null);

    /// <inheritdoc />
    public override string ToString() => $"{Instance} ({Type}) {Host}:{Port} {string.Join(",", Addresses)}{(Properties.Count > 0 ? " " + string.Join(" ", Properties.Select(p => $"{p.Key}={p.Value}")) : "")}";

    /// <summary>The PTR, SRV, TXT and address records of the service (TTL 0 = goodbye).</summary>
    public IEnumerable<DnsRecord> ToRecords(uint ttl = 120)
    {
        yield return new DnsRecord { Name = TypeName, Type = DnsType.Ptr, Target = FullName, Ttl = ttl == 0 ? 0u : 4500u };
        yield return new DnsRecord { Name = FullName, Type = DnsType.Srv, Target = Host, Port = Port, CacheFlush = true, Ttl = ttl };
        yield return new DnsRecord { Name = FullName, Type = DnsType.Txt, Text = [.. Properties.Select(p => $"{p.Key}={p.Value}")], CacheFlush = true, Ttl = ttl == 0 ? 0u : 4500u };
        foreach (var a in Addresses)
            yield return new DnsRecord { Name = Host, Type = a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? DnsType.Aaaa : DnsType.A, Address = a, CacheFlush = true, Ttl = ttl };
    }
}

/// <summary>Options shared by the responder and the browser.</summary>
public sealed class MdnsOptions : IDatagramBuilder<MdnsOptions>
{
    /// <summary>Binds the transport (default: UDP 5353 on all interfaces, joined to 224.0.0.251, address reuse on).</summary>
    public DatagramTransportFactory? TransportFactory { get; set; }

    /// <summary>Where queries and announcements go (224.0.0.251:5353).</summary>
    public EndPoint Group { get; set; } = MdnsAddresses.Endpoint4;

    /// <summary>Friendly name.</summary>
    public string? Name { get; set; }

    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }

    /// <inheritdoc />
    public MdnsOptions UseDatagramTransport(DatagramTransportFactory factory)
    {
        TransportFactory = factory;
        return this;
    }

    internal IDatagramTransport Open()
    {
        var t = (TransportFactory ?? (() => UdpDatagramTransport.Multicast(MdnsAddresses.Group4, MdnsAddresses.Port)))();
        if (t is InMemoryDatagramTransport m && Group is IPEndPoint g) m.JoinMulticastGroup(g.Address);
        return t;
    }
}

/// <summary>Extensions for mDNS on an in-memory network.</summary>
public static class MdnsInMemoryExtensions
{
    /// <summary>Binds <paramref name="address"/>:5353 on <paramref name="network"/> (each peer needs its own address).</summary>
    public static MdnsOptions UseInMemory(this MdnsOptions options, InMemoryDatagramNetwork network, IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(network);
        return options.UseDatagramTransport(() => network.Bind(new IPEndPoint(address, MdnsAddresses.Port)));
    }
}

/// <summary>
/// An mDNS responder (RFC 6762) advertising DNS-SD services (RFC 6763): answers PTR/SRV/TXT/A queries (with known-answer
/// suppression and the service-type enumeration), announces new services twice and sends goodbyes on removal.
/// </summary>
/// <example>
/// <code>
/// await using var mdns = MdnsResponder.Create();
/// await mdns.StartAsync();
/// await mdns.RegisterAsync(new MdnsService { Instance = "Line 1 gateway", Type = "_modbus._tcp", Port = 502,
///     Addresses = MdnsAddresses.LocalAddresses(), Properties = new Dictionary&lt;string, string&gt; { ["unit"] = "1" } });
/// </code>
/// </example>
public sealed class MdnsResponder : EndpointBase, IServerEndpoint
{
    private readonly MdnsOptions _options;
    private readonly ConcurrentDictionary<string, MdnsService> _services = new(StringComparer.OrdinalIgnoreCase);
    private IDatagramTransport? _transport;
    private CancellationTokenSource? _cts;
    private long _answered;

    private MdnsResponder(MdnsOptions options) : base("mdns", options.Logger)
    {
        _options = options;
        Name = options.Name;
    }

    /// <summary>Creates a responder.</summary>
    public static MdnsResponder Create(Action<MdnsOptions>? configure = null)
    {
        var o = new MdnsOptions();
        configure?.Invoke(o);
        return new MdnsResponder(o);
    }

    /// <summary>Services advertised.</summary>
    public IReadOnlyCollection<MdnsService> Services => [.. _services.Values];

    /// <summary>Queries answered.</summary>
    public long QueriesAnswered => Interlocked.Read(ref _answered);

    /// <inheritdoc />
    public ValueTask StartAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_transport is not null) return ValueTask.CompletedTask;
        _transport = _options.Open();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(() => ReceiveLoopAsync(_transport, token), CancellationToken.None);
        SetState(EndpointState.Listening);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask StopAsync(CancellationToken ct = default)
    {
        var t = _transport;
        if (t is null) return;
        foreach (var s in _services.Values) await SendAsync(t, Response(s.ToRecords(0)), _options.Group, ct).ConfigureAwait(false);   // goodbye
        await _cts!.CancelAsync().ConfigureAwait(false);
        await t.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
        (_transport, _cts) = (null, null);
        SetState(EndpointState.Disconnected);
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore() => await StopAsync().ConfigureAwait(false);

    /// <summary>Advertises a service and announces it (twice, one second apart).</summary>
    public async Task RegisterAsync(MdnsService service, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        _services[service.FullName] = service;
        if (_transport is not { } t) return;
        await SendAsync(t, Response(service.ToRecords(120)), _options.Group, ct).ConfigureAwait(false);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1000, _cts?.Token ?? CancellationToken.None).ConfigureAwait(false);
                if (_transport is { } again && _services.ContainsKey(service.FullName)) await SendAsync(again, Response(service.ToRecords(120)), _options.Group, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or TransportException)
            {
            }
        }, CancellationToken.None);
    }

    /// <summary>Withdraws a service (goodbye: TTL 0).</summary>
    public async Task UnregisterAsync(string fullName, CancellationToken ct = default)
    {
        if (!_services.TryRemove(fullName, out var service) || _transport is not { } t) return;
        await SendAsync(t, Response(service.ToRecords(0)), _options.Group, ct).ConfigureAwait(false);
    }

    private static DnsMessage Response(IEnumerable<DnsRecord> answers) => new() { IsResponse = true, Authoritative = true, Answers = [.. answers] };

    private async Task SendAsync(IDatagramTransport t, DnsMessage m, EndPoint to, CancellationToken ct)
    {
        var bytes = m.Encode();
        Tap(FrameDirection.Outbound, bytes, () => $"response {string.Join(", ", m.Answers.Take(3))}{(m.Answers.Count > 3 ? " …" : "")}");
        await t.SendAsync(bytes, to, ct).ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(IDatagramTransport t, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Datagram d;
            try
            {
                d = await t.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or System.Threading.Channels.ChannelClosedException)
            {
                return;
            }
            catch (System.Net.Sockets.SocketException)
            {
                continue;
            }

            if (!DnsMessage.TryDecode(d.Data.Span, out var query, out _) || query!.IsResponse || query.Questions.Count == 0) continue;
            Tap(FrameDirection.Inbound, d.Data.Span, () => $"query {string.Join(", ", query.Questions.Select(q => $"{q.Name} {q.Type}"))}");
            var answers = new List<DnsRecord>();
            var additionals = new List<DnsRecord>();
            foreach (var q in query.Questions) Answer(q, answers, additionals);
            // Known-answer suppression: drop what the asker already holds with at least half the TTL.
            answers.RemoveAll(a => query.Answers.Any(k => k.Type == a.Type && string.Equals(k.Name, a.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(k.Target, a.Target, StringComparison.OrdinalIgnoreCase) && k.Ttl >= a.Ttl / 2));
            if (answers.Count == 0) continue;
            additionals.RemoveAll(a => answers.Contains(a));
            Interlocked.Increment(ref _answered);
            var unicast = query.Questions.Any(q => q.UnicastResponse) || (d.Remote is IPEndPoint ep && ep.Port != MdnsAddresses.Port);
            var reply = new DnsMessage { Id = unicast ? query.Id : (ushort)0, IsResponse = true, Authoritative = true, Answers = answers, Additionals = [.. additionals.Distinct()], Questions = unicast && d.Remote is IPEndPoint { Port: not MdnsAddresses.Port } ? query.Questions : [] };
            try
            {
                await SendAsync(t, reply, unicast ? d.Remote : _options.Group, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or TransportException)
            {
                Logger.LogDebug(ex, "mDNS reply failed");
            }
        }
    }

    private void Answer(DnsQuestion q, List<DnsRecord> answers, List<DnsRecord> additionals)
    {
        bool Is(DnsType t) => q.Type == t || q.Type == DnsType.Any;
        if (string.Equals(q.Name, "_services._dns-sd._udp.local", StringComparison.OrdinalIgnoreCase) && Is(DnsType.Ptr))
        {
            foreach (var type in _services.Values.Select(s => s.TypeName).Distinct(StringComparer.OrdinalIgnoreCase))
                answers.Add(new DnsRecord { Name = q.Name, Type = DnsType.Ptr, Target = type, Ttl = 4500 });
            return;
        }

        foreach (var s in _services.Values)
        {
            var records = s.ToRecords(120).ToList();
            if (string.Equals(q.Name, s.TypeName, StringComparison.OrdinalIgnoreCase) && Is(DnsType.Ptr))
            {
                answers.Add(records[0]);
                additionals.AddRange(records.Skip(1));
            }
            else if (string.Equals(q.Name, s.FullName, StringComparison.OrdinalIgnoreCase))
            {
                if (Is(DnsType.Srv)) answers.Add(records[1]);
                if (Is(DnsType.Txt)) answers.Add(records[2]);
                additionals.AddRange(records.Skip(3));
            }
            else if (string.Equals(q.Name, s.Host, StringComparison.OrdinalIgnoreCase))
            {
                answers.AddRange(records.Skip(3).Where(r => Is(r.Type)));
            }
        }
    }
}

/// <summary>What changed in a browse.</summary>
/// <param name="Service">The service.</param>
/// <param name="Lost">True when it said goodbye or expired.</param>
public sealed record MdnsChange(MdnsService Service, bool Lost);

/// <summary>
/// An mDNS / DNS-SD browser: queries for a service type (or every type), caches records with their TTLs, resolves
/// instances (SRV, TXT, A/AAAA), re-queries with back-off, and reports services as they appear, change or leave.
/// </summary>
/// <example>
/// <code>
/// await using var browser = MdnsBrowser.Create();
/// await browser.StartAsync();
/// foreach (var s in await browser.BrowseAsync("_modbus._tcp", TimeSpan.FromSeconds(3))) Console.WriteLine(s);
/// </code>
/// </example>
public sealed class MdnsBrowser : EndpointBase, IClientEndpoint
{
    private readonly MdnsOptions _options;
    private readonly ConcurrentDictionary<(string Name, DnsType Type, string Value), (DnsRecord Record, DateTimeOffset Expires)> _cache = new();
    private readonly ConcurrentDictionary<string, MdnsService> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _browsing = new(StringComparer.OrdinalIgnoreCase);
    private IDatagramTransport? _transport;
    private CancellationTokenSource? _cts;

    private MdnsBrowser(MdnsOptions options) : base("mdns", options.Logger)
    {
        _options = options;
        Name = options.Name;
    }

    /// <summary>Creates a browser.</summary>
    public static MdnsBrowser Create(Action<MdnsOptions>? configure = null)
    {
        var o = new MdnsOptions();
        configure?.Invoke(o);
        return new MdnsBrowser(o);
    }

    /// <summary>Raised when a service appears, changes or leaves.</summary>
    public event EventHandler<MdnsChange>? ServiceChanged;

    /// <summary>Resolved services currently known.</summary>
    public IReadOnlyCollection<MdnsService> Services => [.. _known.Values];

    /// <inheritdoc />
    public ValueTask ConnectAsync(CancellationToken ct = default) => StartAsync(ct);

    /// <inheritdoc />
    public async ValueTask DisconnectAsync(CancellationToken ct = default)
    {
        var t = _transport;
        if (t is null) return;
        await _cts!.CancelAsync().ConfigureAwait(false);
        await t.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
        (_transport, _cts) = (null, null);
        SetState(EndpointState.Disconnected);
    }

    /// <summary>Opens the socket and starts listening.</summary>
    public ValueTask StartAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_transport is not null) return ValueTask.CompletedTask;
        _transport = _options.Open();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(() => ReceiveLoopAsync(_transport, token), CancellationToken.None);
        _ = Task.Run(() => MaintainAsync(token), CancellationToken.None);
        SetState(EndpointState.Connected);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore() => await DisconnectAsync().ConfigureAwait(false);

    /// <summary>Keeps browsing <paramref name="serviceType"/> (e.g. "_http._tcp") and reports changes through <see cref="ServiceChanged"/>.</summary>
    public Task BrowseContinuouslyAsync(string serviceType, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        var type = Normalise(serviceType);
        _browsing[type] = 0;
        return Task.Run(async () =>
        {
            var delay = TimeSpan.FromSeconds(1);
            try
            {
                await ResolveAsync(ct).ConfigureAwait(false);
                while (!ct.IsCancellationRequested && _transport is not null)
                {
                    await QueryAsync(type, DnsType.Ptr, ct).ConfigureAwait(false);
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    delay = TimeSpan.FromSeconds(Math.Min(60, delay.TotalSeconds * 2));
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _browsing.TryRemove(type, out _);
            }
        }, CancellationToken.None);
    }

    /// <summary>Browses <paramref name="serviceType"/> for <paramref name="duration"/> and returns what answered.</summary>
    public async Task<IReadOnlyList<MdnsService>> BrowseAsync(string serviceType, TimeSpan duration, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (_transport is null) await StartAsync(ct).ConfigureAwait(false);
        var type = Normalise(serviceType);
        _browsing[type] = 0;
        var end = DateTimeOffset.UtcNow + duration;
        var delay = TimeSpan.FromMilliseconds(Math.Min(250, duration.TotalMilliseconds / 3));
        // Records may already be cached (announcements, other browses); known-answer suppression then keeps
        // responders quiet, so resolve from the cache as well as from new responses.
        await ResolveAsync(ct).ConfigureAwait(false);
        while (DateTimeOffset.UtcNow < end)
        {
            await QueryAsync(type, DnsType.Ptr, ct).ConfigureAwait(false);
            await Task.Delay(Min(delay, end - DateTimeOffset.UtcNow), ct).ConfigureAwait(false);
            await ResolveAsync(ct).ConfigureAwait(false);
            delay *= 2;
        }

        _browsing.TryRemove(type, out _);
        return [.. _known.Values.Where(s => string.Equals(s.TypeName, type, StringComparison.OrdinalIgnoreCase))];
    }

    /// <summary>Lists the service types advertised on the link (the "_services._dns-sd._udp" meta-query).</summary>
    public async Task<IReadOnlyList<string>> EnumerateTypesAsync(TimeSpan duration, CancellationToken ct = default)
    {
        if (_transport is null) await StartAsync(ct).ConfigureAwait(false);
        await QueryAsync("_services._dns-sd._udp.local", DnsType.Ptr, ct).ConfigureAwait(false);
        await Task.Delay(duration, ct).ConfigureAwait(false);
        return [.. Live().Where(r => r.Type == DnsType.Ptr && string.Equals(r.Name, "_services._dns-sd._udp.local", StringComparison.OrdinalIgnoreCase)).Select(r => r.Target!).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal)];
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? (a < TimeSpan.Zero ? TimeSpan.Zero : a) : (b < TimeSpan.Zero ? TimeSpan.Zero : b);

    private static string Normalise(string type) => type.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ? type : type.TrimEnd('.') + ".local";

    private async Task QueryAsync(string name, DnsType type, CancellationToken ct)
    {
        if (_transport is not { } t) return;
        // Known answers: PTRs we already hold, so responders stay quiet about them.
        var known = Live().Where(r => r.Type == DnsType.Ptr && string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        var query = new DnsMessage { Questions = [new DnsQuestion(name, type)], Answers = known };
        var bytes = query.Encode();
        Tap(FrameDirection.Outbound, bytes, () => $"query {name} {type}{(known.Count > 0 ? $" ({known.Count} known)" : "")}");
        try
        {
            await t.SendAsync(bytes, _options.Group, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or TransportException)
        {
            Logger.LogDebug(ex, "mDNS query failed");
        }
    }

    private IEnumerable<DnsRecord> Live()
    {
        var now = DateTimeOffset.UtcNow;
        return _cache.Values.Where(e => e.Expires > now).Select(e => e.Record);
    }

    private async Task ReceiveLoopAsync(IDatagramTransport t, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Datagram d;
            try
            {
                d = await t.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or System.Threading.Channels.ChannelClosedException)
            {
                return;
            }
            catch (System.Net.Sockets.SocketException)
            {
                continue;
            }

            if (!DnsMessage.TryDecode(d.Data.Span, out var m, out _) || !m!.IsResponse) continue;
            Tap(FrameDirection.Inbound, d.Data.Span, () => $"response {string.Join(", ", m.Answers.Take(3))}{(m.Answers.Count > 3 ? " …" : "")}");
            var now = DateTimeOffset.UtcNow;
            foreach (var r in m.AllRecords)
            {
                var key = (r.Name.ToLowerInvariant(), r.Type, Value(r));
                if (r.CacheFlush && r.Ttl > 0)
                    foreach (var stale in _cache.Keys.Where(k => k.Name == key.Item1 && k.Type == r.Type && k.Value != key.Item3).ToList()) _cache.TryRemove(stale, out _);
                if (r.Ttl == 0) _cache[key] = (r, now.AddSeconds(1));   // goodbye: expire in one second (RFC 6762 §10.1)
                else _cache[key] = (r, now.AddSeconds(r.Ttl));
            }

            await ResolveAsync(ct).ConfigureAwait(false);
        }
    }

    private static string Value(DnsRecord r) => r.Type switch
    {
        DnsType.A or DnsType.Aaaa => r.Address?.ToString() ?? "",
        DnsType.Ptr => r.Target?.ToLowerInvariant() ?? "",
        DnsType.Srv => $"{r.Target}:{r.Port}".ToLowerInvariant(),
        DnsType.Txt => string.Join("\u0001", r.Text),
        _ => Convert.ToHexString(r.Data),
    };

    private async Task ResolveAsync(CancellationToken ct)
    {
        var live = Live().ToList();
        var goodbyes = _cache.Values.Where(e => e.Record.Ttl == 0).Select(e => e.Record).ToList();
        foreach (var type in _browsing.Keys.ToList())
        {
            foreach (var ptr in live.Where(r => r.Type == DnsType.Ptr && string.Equals(r.Name, type, StringComparison.OrdinalIgnoreCase)))
            {
                var full = ptr.Target!;
                if (goodbyes.Any(g => g.Type == DnsType.Ptr && string.Equals(g.Target, full, StringComparison.OrdinalIgnoreCase))) continue;
                var srv = live.FirstOrDefault(r => r.Type == DnsType.Srv && string.Equals(r.Name, full, StringComparison.OrdinalIgnoreCase));
                if (srv is null)
                {
                    await QueryAsync(full, DnsType.Srv, ct).ConfigureAwait(false);
                    continue;
                }

                var txt = live.FirstOrDefault(r => r.Type == DnsType.Txt && string.Equals(r.Name, full, StringComparison.OrdinalIgnoreCase));
                var addresses = live.Where(r => r.Type is DnsType.A or DnsType.Aaaa && string.Equals(r.Name, srv.Target, StringComparison.OrdinalIgnoreCase)).Select(r => r.Address!).ToList();
                if (addresses.Count == 0) await QueryAsync(srv.Target!, DnsType.A, ct).ConfigureAwait(false);
                var instance = full.EndsWith("." + type, StringComparison.OrdinalIgnoreCase) ? full[..^(type.Length + 1)] : full;
                var service = new MdnsService
                {
                    Instance = instance,
                    Type = type[..^".local".Length],
                    Port = srv.Port,
                    Host = srv.Target!,
                    Addresses = addresses,
                    Properties = (txt?.Text ?? []).Select(t => t.Split('=', 2)).GroupBy(p => p[0], StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.First().Length > 1 ? g.First()[1] : "", StringComparer.OrdinalIgnoreCase),
                };
                if (!_known.TryGetValue(full, out var old) || old.Port != service.Port || old.Host != service.Host || !old.Addresses.SequenceEqual(service.Addresses) || !old.Properties.OrderBy(k => k.Key).SequenceEqual(service.Properties.OrderBy(k => k.Key)))
                {
                    _known[full] = service;
                    ServiceChanged?.Invoke(this, new MdnsChange(service, false));
                }
            }
        }

        foreach (var g in goodbyes.Where(g => g.Type == DnsType.Ptr))
            if (_known.TryRemove(g.Target!, out var gone)) ServiceChanged?.Invoke(this, new MdnsChange(gone, true));
    }

    private async Task MaintainAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var now = DateTimeOffset.UtcNow;
                foreach (var kv in _cache.Where(kv => kv.Value.Expires <= now).ToList()) _cache.TryRemove(kv.Key, out _);
                foreach (var full in _known.Keys.ToList())
                    if (!_cache.Values.Any(e => e.Record.Type == DnsType.Ptr && string.Equals(e.Record.Target, full, StringComparison.OrdinalIgnoreCase)) && _known.TryRemove(full, out var gone))
                        ServiceChanged?.Invoke(this, new MdnsChange(gone, true));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}

/// <summary>
/// A small plant network for demos and tests: responders on an <see cref="InMemoryDatagramNetwork"/> advertising a
/// Modbus gateway, an MQTT broker, a CoAP sensor, an HTTP dashboard and a printer. Add a browser with
/// <see cref="Browser"/>.
/// </summary>
public sealed class MdnsSimulator : IAsyncDisposable
{
    private readonly List<MdnsResponder> _responders = [];

    /// <summary>The simulated network.</summary>
    public InMemoryDatagramNetwork Network { get; } = new();

    /// <summary>The advertised devices (one responder each).</summary>
    public static IReadOnlyList<MdnsService> Devices { get; } =
    [
        Device("Line 1 Modbus gateway", "_modbus._tcp", "gw-line1", 502, "10.20.0.11", ("vendor", "IoTCom"), ("units", "1-8")),
        Device("Plant broker", "_mqtt._tcp", "broker", 1883, "10.20.0.2", ("sparkplug", "spBv1.0")),
        Device("Tank 7 level sensor", "_coap._udp", "tank7", 5683, "10.20.0.37", ("rt", "sensor.level")),
        Device("Edge dashboard", "_http._tcp", "edge", 8080, "10.20.0.5", ("path", "/")),
        Device("Label printer", "_ipp._tcp", "printer-l1", 631, "10.20.0.60", ("ty", "Zebra ZT411")),
    ];

    private static MdnsService Device(string instance, string type, string host, ushort port, string ip, params (string Key, string Value)[] txt) => new()
    {
        Instance = instance,
        Type = type,
        Host = host + ".local",
        Port = port,
        Addresses = [IPAddress.Parse(ip)],
        Properties = txt.ToDictionary(t => t.Key, t => t.Value),
    };

    /// <summary>Starts one responder per device.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        foreach (var d in Devices)
        {
            var r = MdnsResponder.Create(o => o.UseInMemory(Network, d.Address!));
            await r.StartAsync(ct).ConfigureAwait(false);
            await r.RegisterAsync(d, ct).ConfigureAwait(false);
            _responders.Add(r);
        }
    }

    /// <summary>Takes a device off the network (it sends a goodbye).</summary>
    public async Task UnplugAsync(string instance, CancellationToken ct = default)
    {
        var i = Devices.ToList().FindIndex(d => d.Instance == instance);
        if (i < 0 || i >= _responders.Count) return;
        await _responders[i].UnregisterAsync(Devices[i].FullName, ct).ConfigureAwait(false);
    }

    /// <summary>Plugs a device back in (announces it again).</summary>
    public async Task PlugAsync(string instance, CancellationToken ct = default)
    {
        var i = Devices.ToList().FindIndex(d => d.Instance == instance);
        if (i < 0 || i >= _responders.Count) return;
        await _responders[i].RegisterAsync(Devices[i], ct).ConfigureAwait(false);
    }

    /// <summary>A browser on the simulated network at <paramref name="address"/> (default 10.20.0.100).</summary>
    public MdnsBrowser Browser(string address = "10.20.0.100", Action<MdnsOptions>? configure = null) => MdnsBrowser.Create(o =>
    {
        o.UseInMemory(Network, IPAddress.Parse(address));
        configure?.Invoke(o);
    });

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var r in _responders) await r.DisposeAsync().ConfigureAwait(false);
        _responders.Clear();
    }
}
