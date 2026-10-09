using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Protocols.Iec104;

/// <summary>A monitored value as last received.</summary>
/// <param name="CommonAddress">Station (common address of ASDU).</param>
/// <param name="Type">Type the value arrived with.</param>
/// <param name="Object">The information object.</param>
/// <param name="Cause">Cause of transmission.</param>
/// <param name="Received">Reception time.</param>
public sealed record Iec104PointValue(ushort CommonAddress, Iec104TypeId Type, Iec104Object Object, Iec104Cause Cause, DateTimeOffset Received)
{
    /// <inheritdoc />
    public override string ToString() => $"{Iec104Types.Mnemonic(Type)} CA {CommonAddress} {Object} ({Iec104Asdu.CauseName(Cause)})";
}

/// <summary>Options for <see cref="Iec104Client"/>.</summary>
public sealed class Iec104ClientOptions : ITransportBuilder<Iec104ClientOptions>
{
    /// <summary>Transport.</summary>
    public TransportFactory? TransportFactory { get; set; }

    /// <summary>Default common address (station) for requests (default 1).</summary>
    public ushort CommonAddress { get; set; } = 1;

    /// <summary>Originator address written in every ASDU (default 0).</summary>
    public byte Originator { get; set; }

    /// <summary>
    /// Refuses process commands, set points, clock synchronisation and reset process (default true). Interrogations
    /// and reads stay allowed. Call <see cref="AllowCommands"/> to operate switchgear.
    /// </summary>
    public bool ReadOnly { get; set; } = true;

    /// <summary>APCI parameters (k, w, t1, t2, t3).</summary>
    public Iec104LinkParameters Link { get; } = new();

    /// <summary>Time to wait for an activation confirmation or termination (default 10 s).</summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }

    /// <summary>Endpoint name.</summary>
    public string? Name { get; set; }

    /// <inheritdoc />
    public Iec104ClientOptions UseTransport(TransportFactory factory)
    {
        TransportFactory = factory;
        return this;
    }

    /// <summary>Connects over TCP (default port 2404).</summary>
    public Iec104ClientOptions UseTcp(string host, int port = Iec104Apdu.DefaultPort) => this.UseTransport(() => new Transports.TcpClientTransport(host, port, null));

    /// <summary>Allows commands, set points and clock synchronisation.</summary>
    public Iec104ClientOptions AllowCommands()
    {
        ReadOnly = false;
        return this;
    }
}

/// <summary>
/// IEC 60870-5-104 controlling station (SCADA master): connects, starts data transfer, keeps a table of the latest
/// monitored values, and runs general and counter interrogations, reads, commands (direct or select-before-operate),
/// set points and clock synchronisation.
/// </summary>
public sealed class Iec104Client : EndpointBase, IClientEndpoint
{
    private readonly Iec104ClientOptions _options;
    private readonly ConcurrentDictionary<(ushort, uint), Iec104PointValue> _points = new();
    private readonly List<Pending> _pending = [];
    private readonly List<Channel<Iec104Asdu>> _subscribers = [];
    private readonly Lock _gate = new();
    private Iec104Link? _link;

    private sealed class Pending
    {
        public required Iec104TypeId Type { get; init; }
        public required ushort CommonAddress { get; init; }
        public required uint Address { get; init; }
        public Func<Iec104Asdu, bool>? Collect { get; init; }
        public List<Iec104PointValue> Collected { get; } = [];
        public TaskCompletionSource<Iec104Asdu> Confirmation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<Iec104Asdu> Termination { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private Iec104Client(Iec104ClientOptions options) : base("iec104", options.Logger)
    {
        _options = options;
        Name = options.Name;
    }

    /// <summary>Creates a client.</summary>
    public static Iec104Client Create(Action<Iec104ClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var o = new Iec104ClientOptions();
        configure(o);
        if (o.TransportFactory is null) throw new ArgumentException("A transport is required (UseTcp, UseInMemory).", nameof(configure));
        return new Iec104Client(o);
    }

    /// <summary>Latest value of every monitored point, keyed by (common address, IOA).</summary>
    public IReadOnlyDictionary<(ushort CommonAddress, uint Address), Iec104PointValue> Points => _points;

    /// <summary>Raised for every received ASDU (on the I/O thread).</summary>
    public event Action<Iec104Asdu>? AsduReceived;

    /// <summary>Raised for every monitored value (spontaneous, interrogated, periodic…).</summary>
    public event Action<Iec104PointValue>? PointReceived;

    /// <summary>True when connected with data transfer started.</summary>
    public bool IsConnected => _link is { DataTransferActive: true, Closed.IsCompleted: false };

    /// <inheritdoc />
    public async ValueTask ConnectAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_link is { Closed.IsCompleted: false }) return;
        SetState(EndpointState.Connecting);
        var transport = _options.TransportFactory!();
        try
        {
            await transport.OpenAsync(ct).ConfigureAwait(false);
            var link = new Iec104Link(transport, _options.Link, (d, f) => Tap(d, f, () => Summary(f)), Logger) { AsduReceived = OnAsduAsync };
            link.Start();
            _link = link;
            _ = link.Closed.ContinueWith(_ => OnClosed(link), TaskScheduler.Default);
            await link.ActivateAsync(Iec104UFunction.StartDtActivation, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (_link is { } l) await l.DisposeAsync().ConfigureAwait(false);
            else await transport.DisposeAsync().ConfigureAwait(false);
            _link = null;
            SetState(EndpointState.Disconnected, ex);
            throw;
        }

        SetState(EndpointState.Connected);
    }

    internal static string Summary(byte[] frame)
    {
        try { return Iec104Apdu.Parse(frame).ToString(); }
        catch (ProtocolException ex) { return ex.Message; }
    }

    private void OnClosed(Iec104Link link)
    {
        lock (_gate)
        {
            foreach (var p in _pending)
            {
                var ex = new TransportException("The IEC 104 connection closed.", link.Error);
                p.Confirmation.TrySetException(ex);
                p.Termination.TrySetException(ex);
            }

            _pending.Clear();
        }

        if (ReferenceEquals(_link, link)) SetState(link.Error is null ? EndpointState.Disconnected : EndpointState.Faulted, link.Error);
    }

    /// <inheritdoc />
    public async ValueTask DisconnectAsync(CancellationToken ct = default)
    {
        var link = Interlocked.Exchange(ref _link, null);
        if (link is null) return;
        try
        {
            if (link.DataTransferActive) await link.ActivateAsync(Iec104UFunction.StopDtActivation, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IoTComException or OperationCanceledException)
        {
            Logger.LogDebug(ex, "STOPDT failed");
        }

        await link.DisposeAsync().ConfigureAwait(false);
        SetState(EndpointState.Disconnected);
    }

    /// <summary>Streams every received ASDU.</summary>
    public async IAsyncEnumerable<Iec104Asdu> ReceiveAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        var ch = Channel.CreateBounded<Iec104Asdu>(new BoundedChannelOptions(4096) { FullMode = BoundedChannelFullMode.DropOldest });
        lock (_gate) _subscribers.Add(ch);
        try
        {
            await foreach (var a in ch.Reader.ReadAllAsync(ct).ConfigureAwait(false)) yield return a;
        }
        finally
        {
            lock (_gate) _subscribers.Remove(ch);
        }
    }

    private Task OnAsduAsync(byte[] bytes)
    {
        Iec104Asdu asdu;
        try
        {
            asdu = Iec104Asdu.Parse(bytes);
        }
        catch (ProtocolException ex)
        {
            Logger.LogDebug(ex, "Ignoring an ASDU that could not be parsed");
            return Task.CompletedTask;
        }

        AsduReceived?.Invoke(asdu);
        Channel<Iec104Asdu>[] subs;
        lock (_gate) subs = [.. _subscribers];
        foreach (var s in subs) s.Writer.TryWrite(asdu);

        if (Iec104Types.IsMonitoring(asdu.Type) && asdu.Type != Iec104TypeId.EndOfInitialisation)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var o in asdu.Objects)
            {
                var v = new Iec104PointValue(asdu.CommonAddress, asdu.Type, o, asdu.Cause, now);
                _points[(asdu.CommonAddress, o.Address)] = v;
                PointReceived?.Invoke(v);
            }

            lock (_gate)
            {
                foreach (var p in _pending)
                {
                    if (p.Collect?.Invoke(asdu) == true) p.Collected.AddRange(asdu.Objects.Select(o => new Iec104PointValue(asdu.CommonAddress, asdu.Type, o, asdu.Cause, now)));
                    if (p.Type == Iec104TypeId.Read && asdu.Cause == Iec104Cause.Request && asdu.CommonAddress == p.CommonAddress && asdu.Objects.Any(o => o.Address == p.Address))
                        p.Confirmation.TrySetResult(asdu);
                }
            }

            return Task.CompletedTask;
        }

        var ioa = asdu.Objects.Count > 0 ? asdu.Objects[0].Address : 0;
        lock (_gate)
        {
            var p = _pending.FirstOrDefault(x => x.Type == asdu.Type && x.Address == ioa && (x.CommonAddress == asdu.CommonAddress || x.CommonAddress == 0xFFFF));
            if (p is null) return Task.CompletedTask;
            switch (asdu.Cause)
            {
                case Iec104Cause.ActivationConfirmation or Iec104Cause.DeactivationConfirmation:
                    p.Confirmation.TrySetResult(asdu);
                    if (asdu.Negative) p.Termination.TrySetResult(asdu);
                    break;
                case Iec104Cause.ActivationTermination:
                    p.Termination.TrySetResult(asdu);
                    break;
                case Iec104Cause.UnknownType or Iec104Cause.UnknownCause or Iec104Cause.UnknownCommonAddress or Iec104Cause.UnknownObjectAddress:
                    var ex = new DeviceException($"The station answered {Iec104Types.Mnemonic(asdu.Type)} IOA {ioa}: {Iec104Asdu.CauseName(asdu.Cause)}.", (byte)asdu.Cause);
                    p.Confirmation.TrySetException(ex);
                    p.Termination.TrySetException(ex);
                    break;
            }
        }

        return Task.CompletedTask;
    }

    private async Task<Pending> SendAsync(Iec104TypeId type, Iec104Object obj, ushort? commonAddress, Iec104Cause cause, Func<Iec104Asdu, bool>? collect, CancellationToken ct)
    {
        ThrowIfDisposed();
        if (!IsConnected) await ConnectAsync(ct).ConfigureAwait(false);
        var link = _link ?? throw new TransportException("Not connected.");
        var ca = commonAddress ?? _options.CommonAddress;
        var p = new Pending { Type = type, CommonAddress = ca, Address = obj.Address, Collect = collect };
        lock (_gate)
        {
            if (_pending.Any(x => x.Type == type && x.CommonAddress == ca && x.Address == obj.Address))
                throw new InvalidOperationException($"A {Iec104Types.Mnemonic(type)} for IOA {obj.Address} is already in progress.");
            _pending.Add(p);
        }

        try
        {
            await link.SendAsduAsync(new Iec104Asdu(type, cause, ca, [obj], Originator: _options.Originator).Encode(), ct).ConfigureAwait(false);
        }
        catch
        {
            Remove(p);
            throw;
        }

        return p;
    }

    private void Remove(Pending p)
    {
        lock (_gate) _pending.Remove(p);
    }

    private async Task<Iec104Asdu> AwaitAsync(Pending p, Task<Iec104Asdu> task, string what, CancellationToken ct)
    {
        try
        {
            return await task.WaitAsync(_options.CommandTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Remove(p);
            throw new IoTComTimeoutException($"No {what} for {Iec104Types.Mnemonic(p.Type)} IOA {p.Address} within {_options.CommandTimeout.TotalSeconds:0.#} s.");
        }
    }

    private static void ThrowIfNegative(Iec104Asdu confirmation)
    {
        if (confirmation.Negative)
            throw new DeviceException($"The station rejected {Iec104Types.Mnemonic(confirmation.Type)} IOA {(confirmation.Objects.Count > 0 ? confirmation.Objects[0].Address : 0)} (negative confirmation).", (byte)confirmation.Cause);
    }

    /// <summary>
    /// General (QOI 20) or group interrogation (QOI 21–36): returns every value the station sends with that cause
    /// before the activation termination.
    /// </summary>
    public async Task<IReadOnlyList<Iec104PointValue>> InterrogateAsync(byte qualifier = 20, ushort? commonAddress = null, CancellationToken ct = default)
    {
        var cause = (Iec104Cause)qualifier;
        var ca = commonAddress ?? _options.CommonAddress;
        var p = await SendAsync(Iec104TypeId.Interrogation, new Iec104Object(0, Qualifier: qualifier), ca, Iec104Cause.Activation,
            a => a.Cause == cause && (a.CommonAddress == ca || ca == 0xFFFF), ct).ConfigureAwait(false);
        try
        {
            ThrowIfNegative(await AwaitAsync(p, p.Confirmation.Task, "activation confirmation", ct).ConfigureAwait(false));
            await AwaitAsync(p, p.Termination.Task, "activation termination", ct).ConfigureAwait(false);
            lock (_gate) return [.. p.Collected];
        }
        finally
        {
            Remove(p);
        }
    }

    /// <summary>Counter interrogation (QCC request 5 = general, 1–4 = group; freeze bits in the upper two bits).</summary>
    public async Task<IReadOnlyList<Iec104PointValue>> CounterInterrogateAsync(byte qualifier = 5, ushort? commonAddress = null, CancellationToken ct = default)
    {
        var request = qualifier & 0x3F;
        var cause = (Iec104Cause)(request == 5 ? 37 : 37 + request);
        var ca = commonAddress ?? _options.CommonAddress;
        var p = await SendAsync(Iec104TypeId.CounterInterrogation, new Iec104Object(0, Qualifier: qualifier), ca, Iec104Cause.Activation,
            a => a.Cause == cause && (a.CommonAddress == ca || ca == 0xFFFF), ct).ConfigureAwait(false);
        try
        {
            ThrowIfNegative(await AwaitAsync(p, p.Confirmation.Task, "activation confirmation", ct).ConfigureAwait(false));
            await AwaitAsync(p, p.Termination.Task, "activation termination", ct).ConfigureAwait(false);
            lock (_gate) return [.. p.Collected];
        }
        finally
        {
            Remove(p);
        }
    }

    /// <summary>Reads one information object (C_RD_NA_1); the station answers with cause "request".</summary>
    public async Task<Iec104PointValue> ReadAsync(uint address, ushort? commonAddress = null, CancellationToken ct = default)
    {
        var p = await SendAsync(Iec104TypeId.Read, new Iec104Object(address), commonAddress, Iec104Cause.Request, null, ct).ConfigureAwait(false);
        try
        {
            var a = await AwaitAsync(p, p.Confirmation.Task, "answer", ct).ConfigureAwait(false);
            return new Iec104PointValue(a.CommonAddress, a.Type, a.Objects.First(o => o.Address == address), a.Cause, DateTimeOffset.UtcNow);
        }
        finally
        {
            Remove(p);
        }
    }

    /// <summary>
    /// Sends a process command or set point (types 45–64) and waits for the positive activation confirmation; with
    /// <paramref name="waitForTermination"/> also for the activation termination.
    /// </summary>
    /// <exception cref="ReadOnlyModeException">The client is read-only.</exception>
    /// <exception cref="DeviceException">Negative confirmation or unknown type/cause/address.</exception>
    public async Task<Iec104Asdu> CommandAsync(Iec104TypeId type, Iec104Object command, ushort? commonAddress = null, bool waitForTermination = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!Iec104Types.IsCommand(type)) throw new ArgumentException($"{Iec104Types.Mnemonic(type)} is not a process command.", nameof(type));
        if (_options.ReadOnly) throw new ReadOnlyModeException($"Refusing {Iec104Types.Mnemonic(type)} to IOA {command.Address}: the IEC 104 client is read-only (AllowCommands() enables commands).");
        var p = await SendAsync(type, command, commonAddress, Iec104Cause.Activation, null, ct).ConfigureAwait(false);
        try
        {
            var con = await AwaitAsync(p, p.Confirmation.Task, "activation confirmation", ct).ConfigureAwait(false);
            ThrowIfNegative(con);
            if (waitForTermination && !command.Select) return await AwaitAsync(p, p.Termination.Task, "activation termination", ct).ConfigureAwait(false);
            return con;
        }
        finally
        {
            Remove(p);
        }
    }

    private async Task<Iec104Asdu> OperateAsync(Iec104TypeId type, uint address, double value, byte qualifier, bool selectBeforeOperate, ushort? commonAddress, CancellationToken ct)
    {
        if (selectBeforeOperate) await CommandAsync(type, new Iec104Object(address, value, Qualifier: (byte)(qualifier | 0x80)), commonAddress, false, ct).ConfigureAwait(false);
        return await CommandAsync(type, new Iec104Object(address, value, Qualifier: (byte)(qualifier & 0x7F)), commonAddress, true, ct).ConfigureAwait(false);
    }

    /// <summary>Single command (C_SC_NA_1). Returns the activation termination.</summary>
    public Task<Iec104Asdu> SingleCommandAsync(uint address, bool on, bool selectBeforeOperate = false, ushort? commonAddress = null, CancellationToken ct = default)
        => OperateAsync(Iec104TypeId.SingleCommand, address, on ? 1 : 0, 0, selectBeforeOperate, commonAddress, ct);

    /// <summary>Double command (C_DC_NA_1): on = close (DCS 2), off = open (DCS 1). Returns the activation termination.</summary>
    public Task<Iec104Asdu> DoubleCommandAsync(uint address, bool on, bool selectBeforeOperate = false, ushort? commonAddress = null, CancellationToken ct = default)
        => OperateAsync(Iec104TypeId.DoubleCommand, address, on ? 2 : 1, 0, selectBeforeOperate, commonAddress, ct);

    /// <summary>Regulating step command (C_RC_NA_1): next step higher (2) or lower (1).</summary>
    public Task<Iec104Asdu> RegulatingStepAsync(uint address, bool higher, bool selectBeforeOperate = false, ushort? commonAddress = null, CancellationToken ct = default)
        => OperateAsync(Iec104TypeId.RegulatingStep, address, higher ? 2 : 1, 0, selectBeforeOperate, commonAddress, ct);

    /// <summary>Set point command: float (C_SE_NC_1, default), scaled (C_SE_NB_1) or normalized (C_SE_NA_1).</summary>
    public Task<Iec104Asdu> SetpointAsync(uint address, double value, Iec104TypeId type = Iec104TypeId.SetpointFloat, bool selectBeforeOperate = false, ushort? commonAddress = null, CancellationToken ct = default)
    {
        if (type is not (Iec104TypeId.SetpointFloat or Iec104TypeId.SetpointScaled or Iec104TypeId.SetpointNormalized))
            throw new ArgumentException("Use SetpointFloat, SetpointScaled or SetpointNormalized.", nameof(type));
        return OperateAsync(type, address, value, 0, selectBeforeOperate, commonAddress, ct);
    }

    /// <summary>Clock synchronisation (C_CS_NA_1) with the given or current local time.</summary>
    /// <exception cref="ReadOnlyModeException">The client is read-only.</exception>
    public async Task ClockSyncAsync(DateTime? time = null, ushort? commonAddress = null, CancellationToken ct = default)
    {
        if (_options.ReadOnly) throw new ReadOnlyModeException("Refusing clock synchronisation: the IEC 104 client is read-only.");
        var p = await SendAsync(Iec104TypeId.ClockSync, new Iec104Object(0, Time: new Cp56Time2a(time ?? DateTime.Now)), commonAddress, Iec104Cause.Activation, null, ct).ConfigureAwait(false);
        try
        {
            ThrowIfNegative(await AwaitAsync(p, p.Confirmation.Task, "activation confirmation", ct).ConfigureAwait(false));
        }
        finally
        {
            Remove(p);
        }
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore() => await DisconnectAsync().ConfigureAwait(false);
}
