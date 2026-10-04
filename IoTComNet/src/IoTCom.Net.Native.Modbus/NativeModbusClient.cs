using System.Buffers;
using System.Diagnostics;
using IoTCom.Net.Protocols.Modbus;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Native.Modbus;

/// <summary>
/// <see cref="IModbusClient"/> whose protocol engine is the Rust <c>iotcom-modbus</c> master machine
/// (sans-I/O pattern A). C# owns the transport and the driver loop; Rust owns framing, transaction matching,
/// RTU serialisation and timeouts. Configure it exactly like <see cref="ModbusClient"/>.
/// </summary>
/// <example>
/// <code>
/// await using var plc = NativeModbusClient.Create(o => o.UseTcp("192.168.1.10", 502));
/// ushort[] regs = await plc.ReadHoldingRegistersAsync(0, 10);
/// </code>
/// </example>
public sealed class NativeModbusClient : EndpointBase, IModbusClient
{
    private readonly ModbusClientOptions _options;
    private readonly ModbusFraming _framing;
    private readonly Lock _gate = new();
    private readonly Dictionary<uint, TaskCompletionSource<NativeModbusEvent>> _pending = [];
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly long _epoch = Stopwatch.GetTimestamp();
    private Session? _session;

    private NativeModbusClient(ModbusClientOptions options)
        : base(ModbusFraming.For(options.Framing).ProtocolName + "-native", options.Logger)
    {
        _options = options;
        _framing = ModbusFraming.For(options.Framing);
        Name = options.Name;
        if (options.Tap is not null) AddTap(options.Tap);
    }

    /// <summary>Creates a client. Throws <see cref="PlatformNotSupportedException"/> when the native library is unavailable.</summary>
    public static NativeModbusClient Create(Action<ModbusClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var o = new ModbusClientOptions();
        configure(o);
        if (o.TransportFactory is null) throw new ArgumentException("A transport is required (UseTcp, UseSerial or UseInMemory).", nameof(configure));
        NativeModbusMaster.EnsureCompatible();
        return new NativeModbusClient(o);
    }

    /// <inheritdoc />
    public byte UnitId => _options.UnitId;

    private ulong Now => (ulong)(Stopwatch.GetElapsedTime(_epoch).Ticks / 10);

    /// <inheritdoc />
    public async ValueTask ConnectAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await _connectLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_session is { Alive: true }) return;
            SetState(EndpointState.Connecting);
            var transport = _options.TransportFactory!();
            try
            {
                await transport.OpenAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await transport.DisposeAsync().ConfigureAwait(false);
                SetState(EndpointState.Disconnected, ex);
                throw;
            }
            var master = new NativeModbusMaster(_options.Framing, _options.Timeout, _options.MaxConcurrentRequests);
            var session = new Session(transport, master);
            _session = session;
            session.Loops = Task.WhenAll(
                Task.Run(() => ReadLoopAsync(session), CancellationToken.None),
                Task.Run(() => TimerLoopAsync(session), CancellationToken.None));
            SetState(EndpointState.Connected);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisconnectAsync(CancellationToken ct = default)
    {
        var s = Interlocked.Exchange(ref _session, null);
        if (s is null) return;
        SetState(EndpointState.Stopping);
        await CloseSessionAsync(s, new TransportException("Disconnected.")).ConfigureAwait(false);
        SetState(EndpointState.Disconnected);
    }

    /// <inheritdoc />
    public async ValueTask<bool[]> ReadCoilsAsync(ushort address, ushort count, byte? unitId = null, CancellationToken ct = default)
        => ModbusPdu.ParseBits(await ExecuteAsync(ModbusPdu.Read(ModbusFunctionCode.ReadCoils, address, count), unitId, ct).ConfigureAwait(false), count);

    /// <inheritdoc />
    public async ValueTask<bool[]> ReadDiscreteInputsAsync(ushort address, ushort count, byte? unitId = null, CancellationToken ct = default)
        => ModbusPdu.ParseBits(await ExecuteAsync(ModbusPdu.Read(ModbusFunctionCode.ReadDiscreteInputs, address, count), unitId, ct).ConfigureAwait(false), count);

    /// <inheritdoc />
    public async ValueTask<ushort[]> ReadHoldingRegistersAsync(ushort address, ushort count, byte? unitId = null, CancellationToken ct = default)
        => ModbusPdu.ParseRegisters(await ExecuteAsync(ModbusPdu.Read(ModbusFunctionCode.ReadHoldingRegisters, address, count), unitId, ct).ConfigureAwait(false), count);

    /// <inheritdoc />
    public async ValueTask<ushort[]> ReadInputRegistersAsync(ushort address, ushort count, byte? unitId = null, CancellationToken ct = default)
        => ModbusPdu.ParseRegisters(await ExecuteAsync(ModbusPdu.Read(ModbusFunctionCode.ReadInputRegisters, address, count), unitId, ct).ConfigureAwait(false), count);

    /// <inheritdoc />
    public ValueTask WriteSingleCoilAsync(ushort address, bool value, byte? unitId = null, CancellationToken ct = default)
        => WriteAsync(ModbusPdu.WriteSingleCoil(address, value), unitId, ct);

    /// <inheritdoc />
    public ValueTask WriteSingleRegisterAsync(ushort address, ushort value, byte? unitId = null, CancellationToken ct = default)
        => WriteAsync(ModbusPdu.WriteSingleRegister(address, value), unitId, ct);

    /// <inheritdoc />
    public ValueTask WriteMultipleCoilsAsync(ushort address, ReadOnlyMemory<bool> values, byte? unitId = null, CancellationToken ct = default)
        => WriteAsync(ModbusPdu.WriteMultipleCoils(address, values.Span), unitId, ct);

    /// <inheritdoc />
    public ValueTask WriteMultipleRegistersAsync(ushort address, ReadOnlyMemory<ushort> values, byte? unitId = null, CancellationToken ct = default)
        => WriteAsync(ModbusPdu.WriteMultipleRegisters(address, values.Span), unitId, ct);

    /// <inheritdoc />
    public ValueTask<byte[]> SendAsync(ReadOnlyMemory<byte> pdu, byte? unitId = null, CancellationToken ct = default)
    {
        if (pdu.IsEmpty) throw new ArgumentException("PDU must contain a function code.", nameof(pdu));
        if (_options.ReadOnly && pdu.Span[0] is 0x05 or 0x06 or 0x0F or 0x10 or 0x16 or 0x17) throw new ReadOnlyModeException();
        return ExecuteAsync(pdu.ToArray(), unitId, ct);
    }

    private async ValueTask WriteAsync(byte[] request, byte? unitId, CancellationToken ct)
    {
        if (_options.ReadOnly) throw new ReadOnlyModeException();
        var response = await ExecuteAsync(request, unitId, ct).ConfigureAwait(false);
        ModbusPdu.ValidateEcho(request, response);
    }

    private async ValueTask<byte[]> ExecuteAsync(byte[] request, byte? unitIdOverride, CancellationToken ct)
    {
        ThrowIfDisposed();
        var unit = unitIdOverride ?? _options.UnitId;
        var function = (ModbusFunctionCode)request[0];
        for (var attempt = 0; ; attempt++)
        {
            if (_session is not { Alive: true }) await ConnectAsync(ct).ConfigureAwait(false);
            var session = _session ?? throw new TransportException("Not connected.");
            var started = Stopwatch.GetTimestamp();
            var tcs = new TaskCompletionSource<NativeModbusEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            List<byte[]> frames;
            uint id;
            lock (_gate)
            {
                if (!session.Alive) throw new TransportException("Connection closed.");
                id = session.Master.Submit(Now, unit, request);
                _pending[id] = tcs;
                frames = DrainTransmit(session);
            }
            await WriteFramesAsync(session, frames, ct).ConfigureAwait(false);

            NativeModbusEvent evt;
            try
            {
                // The Rust machine enforces the protocol timeout; this guard only protects against a dead driver.
                evt = await tcs.Task.WaitAsync(_options.Timeout + TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }
            finally
            {
                lock (_gate) _pending.Remove(id);
            }

            if (evt.IsTimeout)
            {
                if (attempt < _options.Retries) continue;
                throw new IoTComTimeoutException($"Modbus {function} to unit {unit} timed out after {_options.Timeout.TotalMilliseconds:0} ms.");
            }
            IoTComDiagnostics.Latency.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds, new KeyValuePair<string, object?>("protocol", Protocol));
            ModbusPdu.EnsureSuccess(evt.Pdu, function, unit);
            return evt.Pdu;
        }
    }

    /// <summary>Drains frames and events. Caller holds <see cref="_gate"/>.</summary>
    private List<byte[]> DrainTransmit(Session session)
    {
        var frames = new List<byte[]>();
        Span<byte> buf = stackalloc byte[1024];
        int n;
        while ((n = session.Master.PollTransmit(buf)) > 0) frames.Add(buf[..n].ToArray());
        while (session.Master.TryPollEvent(out var evt))
        {
            if (!evt.IsTimeout)
                Tap(FrameDirection.Inbound, _framing.Encode(0, evt.UnitId, evt.Pdu), () => ModbusPdu.Describe(evt.UnitId, evt.Pdu, isRequest: false));
            if (_pending.TryGetValue(evt.Id, out var tcs)) tcs.TrySetResult(evt);
        }
        return frames;
    }

    private async ValueTask WriteFramesAsync(Session session, List<byte[]> frames, CancellationToken ct)
    {
        if (frames.Count == 0) return;
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var output = session.Transport.Pipe.Output;
            foreach (var f in frames)
            {
                Tap(FrameDirection.Outbound, f, () => _framing.TryDecode(f, expectRequest: true, out var adu) ? ModbusPdu.Describe(adu.UnitId, adu.Pdu, true) : null);
                output.Write(f);
            }
            await output.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = LostAsync(session, ex);
            throw new TransportException($"Failed to send Modbus request: {ex.Message}", ex);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync(Session session)
    {
        var reader = session.Transport.Pipe.Input;
        try
        {
            while (true)
            {
                var result = await reader.ReadAsync(session.Cancellation.Token).ConfigureAwait(false);
                List<byte[]> frames;
                lock (_gate)
                {
                    if (!session.Alive) break;
                    foreach (var segment in result.Buffer) session.Master.HandleInput(Now, segment.Span);
                    frames = DrainTransmit(session);
                }
                reader.AdvanceTo(result.Buffer.End);
                await WriteFramesAsync(session, frames, session.Cancellation.Token).ConfigureAwait(false);
                if (result.IsCompleted) break;
            }
            await LostAsync(session, new TransportException("Remote closed the connection.")).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (session.Cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            await LostAsync(session, ex).ConfigureAwait(false);
        }
    }

    private async Task TimerLoopAsync(Session session)
    {
        try
        {
            while (!session.Cancellation.IsCancellationRequested)
            {
                ulong? deadline;
                lock (_gate)
                {
                    if (!session.Alive) break;
                    deadline = session.Master.PollTimeout();
                }
                var now = Now;
                var waitMs = deadline is { } d ? (d > now ? Math.Min((d - now) / 1000 + 1, 20) : 0) : 20;
                if (waitMs > 0) await Task.Delay(TimeSpan.FromMilliseconds(waitMs), session.Cancellation.Token).ConfigureAwait(false);
                List<byte[]> frames;
                lock (_gate)
                {
                    if (!session.Alive) break;
                    session.Master.HandleTimeout(Now);
                    frames = DrainTransmit(session);
                }
                await WriteFramesAsync(session, frames, session.Cancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Native Modbus timer loop failed");
        }
    }

    private async Task LostAsync(Session session, Exception error)
    {
        if (Interlocked.CompareExchange(ref _session, null, session) != session) return;
        Logger.LogWarning(error, "Native Modbus connection lost");
        await CloseSessionAsync(session, error as IoTComException ?? new TransportException(error.Message, error)).ConfigureAwait(false);
        if (!IsDisposed) SetState(EndpointState.Disconnected, error);
    }

    private async ValueTask CloseSessionAsync(Session session, Exception error)
    {
        if (!session.Close()) return;
        await session.Cancellation.CancelAsync().ConfigureAwait(false);
        await session.Transport.DisposeAsync().ConfigureAwait(false);
        lock (_gate)
        {
            foreach (var tcs in _pending.Values) tcs.TrySetException(error);
            _pending.Clear();
            session.Master.Dispose();
        }
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore()
    {
        var s = Interlocked.Exchange(ref _session, null);
        if (s is not null) await CloseSessionAsync(s, new ObjectDisposedException(nameof(NativeModbusClient))).ConfigureAwait(false);
        SetState(EndpointState.Disconnected);
        _writeLock.Dispose();
        _connectLock.Dispose();
    }

    private sealed class Session(ITransport transport, NativeModbusMaster master)
    {
        private int _closed;
        public ITransport Transport { get; } = transport;
        public NativeModbusMaster Master { get; } = master;
        public CancellationTokenSource Cancellation { get; } = new();
        public Task? Loops { get; set; }
        public bool Alive => Volatile.Read(ref _closed) == 0;
        public bool Close() => Interlocked.Exchange(ref _closed, 1) == 0;
    }
}
