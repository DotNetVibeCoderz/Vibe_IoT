using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Protocols.Iec104;

/// <summary>A monitored point of an <see cref="Iec104Server"/>.</summary>
public sealed class Iec104ServerPoint
{
    internal Iec104ServerPoint(uint address, Iec104TypeId type, string? name, byte group) => (Address, Type, Name, Group) = (address, type, name, group);

    /// <summary>Information object address.</summary>
    public uint Address { get; }

    /// <summary>Type without time tag (M_SP_NA_1, M_ME_NC_1…); spontaneous data uses the time-tagged variant.</summary>
    public Iec104TypeId Type { get; }

    /// <summary>Description.</summary>
    public string? Name { get; }

    /// <summary>Interrogation group 1–16 (0 = station interrogation only).</summary>
    public byte Group { get; }

    /// <summary>Current value.</summary>
    public double Value { get; internal set; }

    /// <summary>Current quality.</summary>
    public Iec104Quality Quality { get; internal set; }

    /// <summary>Counter sequence number (integrated totals).</summary>
    public byte Sequence { get; internal set; }

    /// <summary>Time of the last change.</summary>
    public DateTime Changed { get; internal set; } = DateTime.Now;

    internal Iec104Object ToObject(bool withTime) => new(Address, Value, Quality, Sequence, withTime ? new Cp56Time2a(Changed) : null);
}

/// <summary>A command received by an <see cref="Iec104Server"/>.</summary>
/// <param name="Type">Command type.</param>
/// <param name="Command">The information object (value and qualifier).</param>
/// <param name="CommonAddress">Addressed station.</param>
/// <param name="Peer">Remote address of the controlling station.</param>
public sealed record Iec104Command(Iec104TypeId Type, Iec104Object Command, ushort CommonAddress, string Peer)
{
    /// <summary>True for the select step of select-before-operate.</summary>
    public bool IsSelect => Command.Select;
}

/// <summary>What happened to a received command.</summary>
/// <param name="Command">The command.</param>
/// <param name="Accepted">Positive confirmation.</param>
/// <param name="Reason">Why it was refused (or null).</param>
public sealed record Iec104CommandResult(Iec104Command Command, bool Accepted, string? Reason);

/// <summary>Options for <see cref="Iec104Server"/>.</summary>
public sealed class Iec104ServerOptions : IListenerBuilder<Iec104ServerOptions>
{
    /// <summary>Listener (TCP, in-memory).</summary>
    public TransportListenerFactory? ListenerFactory { get; set; }

    /// <summary>Single pre-connected transport.</summary>
    public TransportFactory? TransportFactory { get; set; }

    /// <summary>Common address of this station (default 1). 0xFFFF (broadcast) is always accepted for system commands.</summary>
    public ushort CommonAddress { get; set; } = 1;

    /// <summary>APCI parameters.</summary>
    public Iec104LinkParameters Link { get; } = new();

    /// <summary>Send spontaneous values with CP56Time2a (M_SP_TB_1, M_ME_TF_1…; default true).</summary>
    public bool TimeTagSpontaneous { get; set; } = true;

    /// <summary>Refuse execute commands that were not selected first (default false).</summary>
    public bool RequireSelectBeforeOperate { get; set; }

    /// <summary>How long a selection stays valid (default 10 s).</summary>
    public TimeSpan SelectTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Maximum simultaneous connections (default 4).</summary>
    public int MaxConnections { get; set; } = 4;

    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }

    /// <summary>Endpoint name.</summary>
    public string? Name { get; set; }

    /// <inheritdoc />
    public Iec104ServerOptions UseListener(TransportListenerFactory factory)
    {
        ListenerFactory = factory;
        TransportFactory = null;
        return this;
    }

    /// <inheritdoc />
    public Iec104ServerOptions UseTransport(TransportFactory factory)
    {
        TransportFactory = factory;
        ListenerFactory = null;
        return this;
    }

    /// <summary>Listens on TCP (default port 2404, all interfaces).</summary>
    public Iec104ServerOptions UseTcp(int port = Iec104Apdu.DefaultPort) => this.UseTcp(IPAddress.Any, port);
}

/// <summary>
/// IEC 60870-5-104 controlled station (outstation / RTU / gateway): serves a point table, answers general, group and
/// counter interrogations and reads, sends spontaneous changes to every connection that started data transfer, and
/// hands commands to handlers registered with <see cref="MapCommand"/> (unknown command addresses are refused).
/// </summary>
public sealed class Iec104Server : EndpointBase, IServerEndpoint
{
    private readonly Iec104ServerOptions _options;
    private readonly ConcurrentDictionary<uint, Iec104ServerPoint> _points = new();
    private readonly ConcurrentDictionary<uint, (Iec104TypeId Type, Func<Iec104Command, bool> Handler)> _commands = new();
    private readonly ConcurrentDictionary<uint, long> _selected = new();
    private readonly ConcurrentDictionary<Iec104Link, byte> _links = new();
    private ITransportListener? _listener;
    private CancellationTokenSource? _cts;

    private Iec104Server(Iec104ServerOptions options) : base("iec104", options.Logger)
    {
        _options = options;
        Name = options.Name;
    }

    /// <summary>Creates a server.</summary>
    public static Iec104Server Create(Action<Iec104ServerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var o = new Iec104ServerOptions();
        configure(o);
        if (o.ListenerFactory is null && o.TransportFactory is null) throw new ArgumentException("A listener (UseTcp/ListenInMemory) or transport is required.", nameof(configure));
        return new Iec104Server(o);
    }

    /// <summary>Common address of this station.</summary>
    public ushort CommonAddress => _options.CommonAddress;

    /// <summary>The point table.</summary>
    public IReadOnlyDictionary<uint, Iec104ServerPoint> Points => _points;

    /// <summary>Open connections.</summary>
    public int ConnectionCount => _links.Count;

    /// <summary>Connections with data transfer started.</summary>
    public int ActiveConnectionCount => _links.Keys.Count(l => l.DataTransferActive);

    /// <summary>Local address after start.</summary>
    public string? LocalAddress => _listener?.LocalAddress;

    /// <summary>Offset applied by the last clock synchronisation.</summary>
    public TimeSpan ClockOffset { get; private set; }

    /// <summary>Raised after every command, with the outcome.</summary>
    public event Action<Iec104CommandResult>? CommandHandled;

    /// <summary>Raised when a controlling station connects (true) or disconnects (false).</summary>
    public event Action<string, bool>? ConnectionChanged;

    /// <summary>Adds a monitored point (use the type without time tag).</summary>
    public Iec104ServerPoint Define(uint address, Iec104TypeId type, double value = 0, string? name = null, byte group = 0)
    {
        if (!Iec104Types.IsMonitoring(type) || Iec104Types.HasTime(type) || type == Iec104TypeId.EndOfInitialisation)
            throw new ArgumentException("Define points with a monitoring type without time tag (M_SP_NA_1, M_ME_NC_1, M_IT_NA_1…).", nameof(type));
        var p = new Iec104ServerPoint(address, type, name, group) { Value = value };
        if (!_points.TryAdd(address, p)) throw new ArgumentException($"IOA {address} is already defined.", nameof(address));
        return p;
    }

    /// <summary>
    /// Registers the handler for commands of <paramref name="type"/> to <paramref name="address"/>. The handler runs for
    /// the select step and for the execute step and returns false to refuse.
    /// </summary>
    public void MapCommand(uint address, Iec104TypeId type, Func<Iec104Command, bool> handler)
    {
        if (!Iec104Types.IsCommand(type)) throw new ArgumentException("Map a process command type (45–64).", nameof(type));
        _commands[address] = (Iec104Types.WithoutTime(type), handler);
    }

    /// <summary>
    /// Updates a point. When the value or quality changed (or <paramref name="force"/>), the change is sent to every
    /// active connection with <paramref name="cause"/> (spontaneous by default; use return-remote for command results).
    /// </summary>
    public void Update(uint address, double value, Iec104Quality? quality = null, Iec104Cause cause = Iec104Cause.Spontaneous, bool force = false)
    {
        if (!_points.TryGetValue(address, out var p)) throw new ArgumentException($"IOA {address} is not defined.", nameof(address));
        var q = quality ?? p.Quality;
        if (!force && p.Value.Equals(value) && p.Quality == q) return;
        p.Value = value;
        p.Quality = q;
        p.Changed = DateTime.Now + ClockOffset;
        if (p.Type == Iec104TypeId.IntegratedTotals && cause == Iec104Cause.Spontaneous) return;   // counters are read by counter interrogation
        var type = _options.TimeTagSpontaneous ? Iec104Types.WithTime(p.Type) : p.Type;
        Broadcast(new Iec104Asdu(type, cause, _options.CommonAddress, [p.ToObject(_options.TimeTagSpontaneous)]));
    }

    /// <summary>Freezes counters: the next counter interrogation reports these values with a new sequence number.</summary>
    public void IncrementCounter(uint address, double delta)
    {
        if (!_points.TryGetValue(address, out var p) || p.Type != Iec104TypeId.IntegratedTotals) throw new ArgumentException($"IOA {address} is not a counter.", nameof(address));
        p.Value += delta;
        p.Changed = DateTime.Now + ClockOffset;
    }

    private void Broadcast(Iec104Asdu asdu)
    {
        var bytes = asdu.Encode();
        foreach (var link in _links.Keys.Where(l => l.DataTransferActive))
            _ = SendQuietlyAsync(link, bytes);
    }

    private async Task SendQuietlyAsync(Iec104Link link, byte[] asdu)
    {
        try { await link.SendAsduAsync(asdu, _cts?.Token ?? CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IoTComException or OperationCanceledException) { Logger.LogDebug(ex, "IEC 104 send to {Peer} failed", link.Peer); }
    }

    /// <inheritdoc />
    public async ValueTask StartAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_cts is not null) return;
        SetState(EndpointState.Connecting);
        var cts = _cts = new CancellationTokenSource();
        if (_options.ListenerFactory is not null)
        {
            var listener = _listener = _options.ListenerFactory();
            await listener.StartAsync(ct).ConfigureAwait(false);
            _ = Task.Run(() => AcceptLoopAsync(listener, cts.Token), CancellationToken.None);
        }
        else
        {
            var t = _options.TransportFactory!();
            await t.OpenAsync(ct).ConfigureAwait(false);
            Serve(t);
        }

        SetState(EndpointState.Listening);
    }

    private async Task AcceptLoopAsync(ITransportListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            ITransport peer;
            try { peer = await listener.AcceptAsync(ct).ConfigureAwait(false); }
            catch (Exception) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or TransportException or InvalidOperationException)
            {
                Logger.LogWarning(ex, "IEC 104 accept failed");
                break;
            }

            if (_links.Count >= _options.MaxConnections)
            {
                Logger.LogWarning("IEC 104: refusing {Peer}, {Max} connections open", peer.Info.RemoteAddress, _options.MaxConnections);
                await peer.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            Serve(peer);
        }
    }

    private void Serve(ITransport transport)
    {
        Iec104Link? link = null;
        link = new Iec104Link(transport, _options.Link, (d, f) => Tap(d, f, () => Iec104Client.Summary(f)), Logger)
        {
            AsduReceived = a => OnAsduAsync(link!, a),
        };
        var initialised = false;
        link.DataTransferChanged += started =>
        {
            if (!started || initialised) return;
            initialised = true;
            _ = SendQuietlyAsync(link, new Iec104Asdu(Iec104TypeId.EndOfInitialisation, Iec104Cause.Initialised, _options.CommonAddress, [new Iec104Object(0)]).Encode());
        };
        _links[link] = 0;
        ConnectionChanged?.Invoke(link.Peer, true);
        _ = link.Closed.ContinueWith(closed =>
        {
            _links.TryRemove(link, out _);
            ConnectionChanged?.Invoke(link.Peer, false);
        }, TaskScheduler.Default);
        link.Start();
    }

    private Task Reply(Iec104Link link, Iec104Asdu request, Iec104Cause cause, bool negative = false, IReadOnlyList<Iec104Object>? objects = null) =>
        link.SendAsduAsync((request with { Cause = cause, Negative = negative, Objects = objects ?? request.Objects }).Encode(), _cts?.Token ?? CancellationToken.None);

    private async Task OnAsduAsync(Iec104Link link, byte[] bytes)
    {
        Iec104Asdu request;
        try
        {
            request = Iec104Asdu.Parse(bytes);
        }
        catch (ProtocolException)
        {
            var header = Iec104Asdu.ParseHeader(bytes);
            if (Iec104Types.IsKnown(header.Type)) throw;   // malformed known type: drop
            // Unknown type: mirror the raw ASDU with cause 44 and P/N set.
            var mirror = bytes.ToArray();
            mirror[2] = (byte)((byte)Iec104Cause.UnknownType | 0x40 | (mirror[2] & 0x80));
            await link.SendAsduAsync(mirror, _cts?.Token ?? CancellationToken.None).ConfigureAwait(false);
            return;
        }

        var system = request.Type is Iec104TypeId.Interrogation or Iec104TypeId.CounterInterrogation or Iec104TypeId.ClockSync or Iec104TypeId.ResetProcess or Iec104TypeId.TestCommand;
        if (request.CommonAddress != _options.CommonAddress && !(system && request.CommonAddress == 0xFFFF))
        {
            await Reply(link, request, Iec104Cause.UnknownCommonAddress, true).ConfigureAwait(false);
            return;
        }

        if (Iec104Types.IsMonitoring(request.Type))
        {
            await Reply(link, request, Iec104Cause.UnknownType, true).ConfigureAwait(false);
            return;
        }

        var o = request.Objects[0];
        switch (request.Type)
        {
            case Iec104TypeId.Interrogation:
                if (request.Cause != Iec104Cause.Activation || o.Qualifier is < 20 or > 36)
                {
                    await Reply(link, request, request.Cause == Iec104Cause.Activation ? Iec104Cause.ActivationConfirmation : Iec104Cause.UnknownCause, true).ConfigureAwait(false);
                    return;
                }

                await Reply(link, request, Iec104Cause.ActivationConfirmation).ConfigureAwait(false);
                var group = o.Qualifier - 20;
                await SendPointsAsync(link, _points.Values.Where(p => p.Type != Iec104TypeId.IntegratedTotals && (group == 0 || p.Group == group)), (Iec104Cause)o.Qualifier).ConfigureAwait(false);
                await Reply(link, request, Iec104Cause.ActivationTermination).ConfigureAwait(false);
                break;

            case Iec104TypeId.CounterInterrogation:
                var rqt = o.Qualifier & 0x3F;
                if (request.Cause != Iec104Cause.Activation || rqt is < 1 or > 5)
                {
                    await Reply(link, request, request.Cause == Iec104Cause.Activation ? Iec104Cause.ActivationConfirmation : Iec104Cause.UnknownCause, true).ConfigureAwait(false);
                    return;
                }

                await Reply(link, request, Iec104Cause.ActivationConfirmation).ConfigureAwait(false);
                var counters = _points.Values.Where(p => p.Type == Iec104TypeId.IntegratedTotals && (rqt == 5 || p.Group == rqt)).ToList();
                foreach (var c in counters) c.Sequence = (byte)((c.Sequence + 1) & 0x1F);
                await SendPointsAsync(link, counters, (Iec104Cause)(rqt == 5 ? 37 : 37 + rqt)).ConfigureAwait(false);
                await Reply(link, request, Iec104Cause.ActivationTermination).ConfigureAwait(false);
                break;

            case Iec104TypeId.Read:
                if (request.Cause != Iec104Cause.Request)
                    await Reply(link, request, Iec104Cause.UnknownCause, true).ConfigureAwait(false);
                else if (_points.TryGetValue(o.Address, out var point))
                    await link.SendAsduAsync(new Iec104Asdu(point.Type, Iec104Cause.Request, _options.CommonAddress, [point.ToObject(false)]).Encode(), _cts?.Token ?? CancellationToken.None).ConfigureAwait(false);
                else
                    await Reply(link, request, Iec104Cause.UnknownObjectAddress, true).ConfigureAwait(false);
                break;

            case Iec104TypeId.ClockSync:
                if (request.Cause != Iec104Cause.Activation || o.Time is null)
                {
                    await Reply(link, request, Iec104Cause.UnknownCause, true).ConfigureAwait(false);
                    return;
                }

                ClockOffset = o.Time.Value.Value - DateTime.Now;
                await Reply(link, request, Iec104Cause.ActivationConfirmation).ConfigureAwait(false);
                break;

            case Iec104TypeId.ResetProcess or Iec104TypeId.TestCommand:
                await Reply(link, request, request.Cause == Iec104Cause.Activation ? Iec104Cause.ActivationConfirmation : Iec104Cause.UnknownCause, request.Cause != Iec104Cause.Activation).ConfigureAwait(false);
                break;

            default:
                await HandleCommandAsync(link, request, o).ConfigureAwait(false);
                break;
        }
    }

    private async Task SendPointsAsync(Iec104Link link, IEnumerable<Iec104ServerPoint> points, Iec104Cause cause)
    {
        foreach (var byType in points.OrderBy(p => p.Address).GroupBy(p => p.Type))
        {
            var perAsdu = Math.Min(127, (Iec104Asdu.MaxLength - 6) / (3 + Iec104Types.ElementSize(byType.Key)));
            foreach (var chunk in byType.Chunk(perAsdu))
                await link.SendAsduAsync(new Iec104Asdu(byType.Key, cause, _options.CommonAddress, [.. chunk.Select(p => p.ToObject(false))]).Encode(), _cts?.Token ?? CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task HandleCommandAsync(Iec104Link link, Iec104Asdu request, Iec104Object o)
    {
        var command = new Iec104Command(request.Type, o, request.CommonAddress, link.Peer);
        if (request.Cause == Iec104Cause.Deactivation)
        {
            _selected.TryRemove(o.Address, out _);
            await Reply(link, request, Iec104Cause.DeactivationConfirmation).ConfigureAwait(false);
            CommandHandled?.Invoke(new Iec104CommandResult(command, true, "deactivated"));
            return;
        }

        if (request.Cause != Iec104Cause.Activation)
        {
            await Reply(link, request, Iec104Cause.UnknownCause, true).ConfigureAwait(false);
            return;
        }

        if (!_commands.TryGetValue(o.Address, out var mapped) || mapped.Type != Iec104Types.WithoutTime(request.Type))
        {
            await Reply(link, request, Iec104Cause.UnknownObjectAddress, true).ConfigureAwait(false);
            CommandHandled?.Invoke(new Iec104CommandResult(command, false, "unknown object address"));
            return;
        }

        string? reason = null;
        var now = Environment.TickCount64;
        if (!o.Select && _options.RequireSelectBeforeOperate &&
            !(_selected.TryRemove(o.Address, out var at) && now - at <= _options.SelectTimeout.TotalMilliseconds))
            reason = "not selected";
        var accepted = reason is null && SafeInvoke(mapped.Handler, command);
        if (reason is null && !accepted) reason = "refused by the station";
        if (accepted && o.Select) _selected[o.Address] = now;
        await Reply(link, request, Iec104Cause.ActivationConfirmation, !accepted).ConfigureAwait(false);
        if (accepted && !o.Select) await Reply(link, request, Iec104Cause.ActivationTermination).ConfigureAwait(false);
        CommandHandled?.Invoke(new Iec104CommandResult(command, accepted, reason));
    }

    private bool SafeInvoke(Func<Iec104Command, bool> handler, Iec104Command command)
    {
        try
        {
            return handler(command);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IoTComException)
        {
            Logger.LogWarning(ex, "IEC 104 command handler for IOA {Address} failed", command.Command.Address);
            return false;
        }
    }

    /// <inheritdoc />
    public async ValueTask StopAsync(CancellationToken ct = default)
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts is null) return;
        SetState(EndpointState.Stopping);
        await cts.CancelAsync().ConfigureAwait(false);
        if (_listener is not null) await _listener.DisposeAsync().ConfigureAwait(false);
        foreach (var l in _links.Keys) await l.DisposeAsync().ConfigureAwait(false);
        _links.Clear();
        cts.Dispose();
        SetState(EndpointState.Disconnected);
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore() => await StopAsync().ConfigureAwait(false);
}
