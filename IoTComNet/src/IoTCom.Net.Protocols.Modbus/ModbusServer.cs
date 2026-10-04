using System.Buffers;
using System.Collections.Concurrent;
using IoTCom.Net.Framing;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Protocols.Modbus;

/// <summary>Fluent options for <see cref="ModbusServer"/>.</summary>
public sealed class ModbusServerOptions : IListenerBuilder<ModbusServerOptions>
{
    /// <summary>Listener factory (TCP or in-memory).</summary>
    public TransportListenerFactory? ListenerFactory { get; set; }
    /// <summary>Single transport served directly (serial RTU line).</summary>
    public TransportFactory? TransportFactory { get; set; }
    /// <summary>Framing (default TCP).</summary>
    public ModbusFramingMode Framing { get; set; } = ModbusFramingMode.Tcp;
    /// <summary>Unit ids answered; empty = answer every unit id.</summary>
    public HashSet<byte> UnitIds { get; } = [];
    /// <summary>Data store (a new full-size store when null).</summary>
    public ModbusDataStore? Store { get; set; }
    /// <summary>Reject every write with IllegalFunction.</summary>
    public bool ReadOnly { get; set; }
    /// <summary>Maximum simultaneous TCP clients.</summary>
    public int MaxConnections { get; set; } = 64;
    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }
    /// <summary>Endpoint name.</summary>
    public string? Name { get; set; }
    /// <summary>Optional tap.</summary>
    public ITrafficTap? Tap { get; set; }
    /// <summary>Device identification vendor.</summary>
    public string VendorName { get; set; } = "Gravicode Studios";
    /// <summary>Device identification product code.</summary>
    public string ProductCode { get; set; } = "IoTCom.Net Modbus Server";

    /// <inheritdoc />
    public ModbusServerOptions UseListener(TransportListenerFactory factory) { ListenerFactory = factory; TransportFactory = null; return this; }
    /// <inheritdoc />
    public ModbusServerOptions UseTransport(TransportFactory factory) { TransportFactory = factory; ListenerFactory = null; return this; }
    /// <summary>Uses RTU framing.</summary>
    public ModbusServerOptions UseRtuFraming() { Framing = ModbusFramingMode.Rtu; return this; }
    /// <summary>Uses ASCII framing.</summary>
    public ModbusServerOptions UseAsciiFraming() { Framing = ModbusFramingMode.Ascii; return this; }
    /// <summary>Answers only the given unit ids.</summary>
    public ModbusServerOptions WithUnitIds(params byte[] unitIds) { UnitIds.UnionWith(unitIds); return this; }
    /// <summary>Serves an existing data store (share it with a simulator or your app).</summary>
    public ModbusServerOptions WithStore(ModbusDataStore store) { Store = store; return this; }
    /// <summary>Rejects writes.</summary>
    public ModbusServerOptions AsReadOnly() { ReadOnly = true; return this; }
    /// <summary>Sets the logger.</summary>
    public ModbusServerOptions WithLogger(ILogger logger) { Logger = logger; return this; }
    /// <summary>Sets the endpoint name.</summary>
    public ModbusServerOptions WithName(string name) { Name = name; return this; }
    /// <summary>Attaches a tap.</summary>
    public ModbusServerOptions WithTap(ITrafficTap tap) { Tap = tap; return this; }
    /// <summary>Sets the device identification strings.</summary>
    public ModbusServerOptions WithIdentity(string vendorName, string productCode) { VendorName = vendorName; ProductCode = productCode; return this; }
}

/// <summary>Information about a handled request.</summary>
public sealed class ModbusRequestEventArgs(byte unitId, ModbusFunctionCode function, string summary, string peer, bool isException) : EventArgs
{
    /// <summary>Unit addressed.</summary>
    public byte UnitId { get; } = unitId;
    /// <summary>Function requested.</summary>
    public ModbusFunctionCode Function { get; } = function;
    /// <summary>Decoded summary.</summary>
    public string Summary { get; } = summary;
    /// <summary>Peer address.</summary>
    public string Peer { get; } = peer;
    /// <summary>True when an exception response was returned.</summary>
    public bool IsException { get; } = isException;
}

/// <summary>
/// Modbus slave (server). Serves a <see cref="ModbusDataStore"/> over TCP (many clients), RTU/ASCII over a serial line,
/// or in-process. Combine it with <see cref="ModbusSimulator"/> to get a live virtual device.
/// </summary>
/// <example>
/// <code>
/// await using var slave = ModbusServer.Create(o => o.UseTcp(IPAddress.Any, 1502));
/// slave.Store.HoldingRegisters[0] = 230;
/// await slave.StartAsync();
/// </code>
/// </example>
public sealed class ModbusServer : EndpointBase, IServerEndpoint
{
    private readonly ModbusServerOptions _options;
    private readonly ModbusFraming _framing;
    private readonly ModbusRequestProcessor _processor;
    private readonly ConcurrentDictionary<int, ITransport> _peers = new();
    private ITransportListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private int _peerIds;
    private long _requests;

    private ModbusServer(ModbusServerOptions options)
        : base(ModbusFraming.For(options.Framing).ProtocolName, options.Logger)
    {
        _options = options;
        _framing = ModbusFraming.For(options.Framing);
        Store = options.Store ?? new ModbusDataStore();
        _processor = new ModbusRequestProcessor(Store) { ReadOnly = options.ReadOnly, VendorName = options.VendorName, ProductCode = options.ProductCode };
        Name = options.Name;
        if (options.Tap is not null) AddTap(options.Tap);
    }

    /// <summary>Creates a server.</summary>
    public static ModbusServer Create(Action<ModbusServerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var o = new ModbusServerOptions();
        configure(o);
        if (o.ListenerFactory is null && o.TransportFactory is null)
            throw new ArgumentException("A listener (UseTcp/ListenInMemory) or a transport (UseSerial) is required.", nameof(configure));
        return new ModbusServer(o);
    }

    /// <summary>The served data.</summary>
    public ModbusDataStore Store { get; }

    /// <summary>Number of connected peers.</summary>
    public int ConnectionCount => _peers.Count;

    /// <summary>Total requests handled.</summary>
    public long RequestCount => Interlocked.Read(ref _requests);

    /// <summary>Local address after <see cref="StartAsync"/> (useful with port 0).</summary>
    public string? LocalAddress => _listener?.LocalAddress;

    /// <summary>Raised after each handled request (on the I/O thread).</summary>
    public event EventHandler<ModbusRequestEventArgs>? RequestHandled;

    /// <inheritdoc />
    public async ValueTask StartAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_cts is not null) return;
        SetState(EndpointState.Connecting);
        _cts = new CancellationTokenSource();
        try
        {
            if (_options.ListenerFactory is not null)
            {
                var listener = _listener = _options.ListenerFactory();
                await listener.StartAsync(ct).ConfigureAwait(false);
                var token = _cts.Token; // capture now: StopAsync may clear the fields before the loop starts
                _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, token), CancellationToken.None);
                Logger.LogInformation("Modbus server listening on {Address}", listener.LocalAddress);
            }
            else
            {
                var transport = _options.TransportFactory!();
                await transport.OpenAsync(ct).ConfigureAwait(false);
                _acceptLoop = ServePeerAsync(transport, _cts.Token);
            }
        }
        catch (Exception ex)
        {
            _cts = null;
            SetState(EndpointState.Faulted, ex);
            throw;
        }
        SetState(EndpointState.Listening);
    }

    /// <inheritdoc />
    public async ValueTask StopAsync(CancellationToken ct = default)
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts is null) return;
        SetState(EndpointState.Stopping);
        await cts.CancelAsync().ConfigureAwait(false);
        if (_listener is not null) await _listener.DisposeAsync().ConfigureAwait(false);
        _listener = null;
        foreach (var p in _peers.Values) await p.DisposeAsync().ConfigureAwait(false);
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); }
            catch { /* shutting down */ }
        }
        cts.Dispose();
        SetState(EndpointState.Disconnected);
    }

    private async Task AcceptLoopAsync(ITransportListener listener, CancellationToken ct)
    {
        var tasks = new List<Task>();
        while (!ct.IsCancellationRequested)
        {
            ITransport peer;
            try
            {
                peer = await listener.AcceptAsync(ct).ConfigureAwait(false);
            }
            catch (Exception) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Modbus accept failed");
                if (listener is not Transports.TcpTransportListener) break;
                continue;
            }
            if (_peers.Count >= _options.MaxConnections)
            {
                Logger.LogWarning("Modbus server rejecting {Peer}: connection limit reached", peer.Info.RemoteAddress);
                await peer.DisposeAsync().ConfigureAwait(false);
                continue;
            }
            tasks.RemoveAll(t => t.IsCompleted);
            tasks.Add(ServePeerAsync(peer, ct));
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task ServePeerAsync(ITransport peer, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _peerIds);
        _peers[id] = peer;
        var peerName = peer.Info.RemoteAddress ?? $"peer-{id}";
        Logger.LogDebug("Modbus peer {Peer} connected", peerName);
        var reader = peer.Pipe.Input;
        var writer = peer.Pipe.Output;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await reader.ReadAsync(ct).ConfigureAwait(false);
                var buffer = result.Buffer;
                List<byte[]>? responses = null;
                while (true)
                {
                    var status = _framing.TryRead(ref buffer, expectRequest: true, out var adu);
                    if (status == FrameDecodeStatus.NeedMoreData) break;
                    if (status == FrameDecodeStatus.Invalid) continue;
                    var response = Handle(adu, peerName);
                    if (response is not null) (responses ??= []).Add(response);
                }
                reader.AdvanceTo(buffer.Start, buffer.End);
                if (responses is not null)
                {
                    foreach (var r in responses) writer.Write(r);
                    var flush = await writer.FlushAsync(ct).ConfigureAwait(false);
                    if (flush.IsCompleted) break;
                }
                if (result.IsCompleted) break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Modbus peer {Peer} error", peerName);
        }
        finally
        {
            _peers.TryRemove(id, out _);
            await peer.DisposeAsync().ConfigureAwait(false);
            Logger.LogDebug("Modbus peer {Peer} disconnected", peerName);
        }
    }

    /// <summary>Handles one ADU; returns the encoded response or null when no answer is due.</summary>
    private byte[]? Handle(ModbusAdu adu, string peer)
    {
        Tap(FrameDirection.Inbound, adu.Raw, () => ModbusPdu.Describe(adu.UnitId, adu.Pdu, isRequest: true));
        var broadcast = adu.UnitId == 0 && _framing.Mode != ModbusFramingMode.Tcp;
        if (!broadcast && _options.UnitIds.Count > 0 && !_options.UnitIds.Contains(adu.UnitId))
        {
            if (_framing.Mode != ModbusFramingMode.Tcp) return null; // RTU multi-drop: not addressed to us, stay silent
            var gw = ModbusRequestProcessor.Error(adu.Pdu.Length > 0 ? adu.Pdu[0] : (byte)0, ModbusExceptionCode.GatewayTargetFailedToRespond);
            return _framing.Encode(adu.TransactionId, adu.UnitId, gw);
        }

        Interlocked.Increment(ref _requests);
        var responsePdu = _processor.Process(adu.Pdu);
        var isException = (responsePdu[0] & 0x80) != 0;
        RequestHandled?.Invoke(this, new ModbusRequestEventArgs(
            adu.UnitId, (ModbusFunctionCode)(adu.Pdu.Length > 0 ? adu.Pdu[0] : 0), ModbusPdu.Describe(adu.UnitId, adu.Pdu, true), peer, isException));
        if (broadcast) return null; // broadcasts are never answered

        var frame = _framing.Encode(adu.TransactionId, adu.UnitId, responsePdu);
        Tap(FrameDirection.Outbound, frame, () => ModbusPdu.Describe(adu.UnitId, responsePdu, isRequest: false));
        return frame;
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore() => await StopAsync().ConfigureAwait(false);
}
