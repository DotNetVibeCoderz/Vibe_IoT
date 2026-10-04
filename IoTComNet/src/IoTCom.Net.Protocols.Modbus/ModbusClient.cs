using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using IoTCom.Net.Framing;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Protocols.Modbus;

/// <summary>
/// Modbus master (client) for TCP, RTU and ASCII over any <see cref="ITransport"/>.
/// Modbus TCP requests are pipelined by transaction id; RTU/ASCII are serialised as the spec requires.
/// The client connects lazily on first use and reconnects with backoff when the link drops.
/// </summary>
/// <example>
/// <code>
/// await using var plc = ModbusClient.Create(o => o.UseTcp("192.168.1.10", 502).WithUnitId(1));
/// ushort[] regs = await plc.ReadHoldingRegistersAsync(0, 10);
/// </code>
/// </example>
public sealed class ModbusClient : EndpointBase, IModbusClient
{
    private readonly ModbusClientOptions _options;
    private readonly ModbusFraming _framing;
    private readonly SemaphoreSlim _concurrency;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly ConcurrentDictionary<ushort, Pending> _pending = new();
    private Connection? _connection;
    private int _transactionId;
    private int _reconnectAttempt;
    private long _nextReconnectTicks;

    private ModbusClient(ModbusClientOptions options)
        : base(ModbusFraming.For(options.Framing).ProtocolName, options.Logger)
    {
        _options = options;
        _framing = ModbusFraming.For(options.Framing);
        Name = options.Name;
        var concurrency = _framing.HasTransactionIds ? Math.Clamp(options.MaxConcurrentRequests, 1, 1024) : 1;
        _concurrency = new SemaphoreSlim(concurrency, concurrency);
        if (options.Tap is not null) AddTap(options.Tap);
    }

    /// <summary>Creates a client. Configure the transport with <c>UseTcp</c>, <c>UseSerial</c> or <c>UseInMemory</c>.</summary>
    public static ModbusClient Create(Action<ModbusClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var o = new ModbusClientOptions();
        configure(o);
        if (o.TransportFactory is null) throw new ArgumentException("A transport is required (UseTcp, UseSerial or UseInMemory).", nameof(configure));
        return new ModbusClient(o);
    }

    /// <inheritdoc />
    public byte UnitId => _options.UnitId;

    /// <summary>Framing in use.</summary>
    public ModbusFramingMode Framing => _framing.Mode;

    /// <summary>True when write functions are blocked.</summary>
    public bool IsReadOnly => _options.ReadOnly;

    /// <inheritdoc />
    public async ValueTask ConnectAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await _connectLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_connection is { IsAlive: true }) return;
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
            var conn = new Connection(transport);
            conn.ReadLoop = Task.Run(() => ReadLoopAsync(conn), CancellationToken.None);
            _connection = conn;
            _reconnectAttempt = 0;
            Logger.LogInformation("Modbus client connected via {Transport}", transport.Info);
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
        await _connectLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var conn = _connection;
            _connection = null;
            if (conn is null) return;
            SetState(EndpointState.Stopping);
            await conn.CloseAsync().ConfigureAwait(false);
            FailPending(new TransportException("Disconnected."));
            SetState(EndpointState.Disconnected);
        }
        finally
        {
            _connectLock.Release();
        }
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

    /// <summary>Mask Write Register (0x16).</summary>
    public ValueTask MaskWriteRegisterAsync(ushort address, ushort andMask, ushort orMask, byte? unitId = null, CancellationToken ct = default)
        => WriteAsync(ModbusPdu.MaskWriteRegister(address, andMask, orMask), unitId, ct);

    /// <summary>Read/Write Multiple Registers (0x17) in one transaction.</summary>
    public async ValueTask<ushort[]> ReadWriteMultipleRegistersAsync(ushort readAddress, ushort readCount, ushort writeAddress, ReadOnlyMemory<ushort> values, byte? unitId = null, CancellationToken ct = default)
    {
        EnsureWritable();
        var response = await ExecuteAsync(ModbusPdu.ReadWriteMultipleRegisters(readAddress, readCount, writeAddress, values.Span), unitId, ct).ConfigureAwait(false);
        return ModbusPdu.ParseRegisters(response, readCount);
    }

    /// <inheritdoc />
    public async ValueTask<byte[]> SendAsync(ReadOnlyMemory<byte> pdu, byte? unitId = null, CancellationToken ct = default)
    {
        if (pdu.IsEmpty) throw new ArgumentException("PDU must contain a function code.", nameof(pdu));
        if (IsWriteFunction(pdu.Span[0])) EnsureWritable();
        return await ExecuteAsync(pdu.ToArray(), unitId, ct).ConfigureAwait(false);
    }

    private async ValueTask WriteAsync(byte[] request, byte? unitId, CancellationToken ct)
    {
        EnsureWritable();
        var response = await ExecuteAsync(request, unitId, ct).ConfigureAwait(false);
        ModbusPdu.ValidateEcho(request, response);
    }

    private void EnsureWritable()
    {
        if (_options.ReadOnly) throw new ReadOnlyModeException();
    }

    private static bool IsWriteFunction(byte fc) => fc is 0x05 or 0x06 or 0x0F or 0x10 or 0x16 or 0x17;

    /// <summary>Sends <paramref name="request"/> and returns the validated (non-exception) response PDU.</summary>
    private async ValueTask<byte[]> ExecuteAsync(byte[] request, byte? unitIdOverride, CancellationToken ct)
    {
        ThrowIfDisposed();
        var unit = unitIdOverride ?? _options.UnitId;
        var function = (ModbusFunctionCode)request[0];
        using var activity = IoTComDiagnostics.ActivitySource.StartActivity($"modbus {function}", ActivityKind.Client);
        activity?.SetTag("iotcom.protocol", Protocol);
        activity?.SetTag("modbus.unit_id", unit);

        for (var attempt = 0; ; attempt++)
        {
            var conn = await EnsureConnectedAsync(ct).ConfigureAwait(false);
            await _concurrency.WaitAsync(ct).ConfigureAwait(false);
            ushort tid = 0;
            var started = Stopwatch.GetTimestamp();
            try
            {
                tid = _framing.HasTransactionIds ? NextTransactionId() : (ushort)0;
                var pending = new Pending(unit);
                _pending[tid] = pending;
                await SendFrameAsync(conn, tid, unit, request, ct).ConfigureAwait(false);

                byte[] response;
                try
                {
                    response = await pending.Completion.Task.WaitAsync(_options.Timeout, ct).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    IoTComDiagnostics.Errors.Add(1, new KeyValuePair<string, object?>("protocol", Protocol));
                    if (attempt < _options.Retries)
                    {
                        Logger.LogDebug("Modbus {Function} unit {Unit} timed out, retry {Attempt}", function, unit, attempt + 1);
                        continue;
                    }
                    throw new IoTComTimeoutException($"Modbus {function} to unit {unit} timed out after {_options.Timeout.TotalMilliseconds:0} ms.");
                }

                IoTComDiagnostics.Latency.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds, new KeyValuePair<string, object?>("protocol", Protocol));
                ModbusPdu.EnsureSuccess(response, function, unit);
                return response;
            }
            finally
            {
                _pending.TryRemove(tid, out _);
                _concurrency.Release();
            }
        }
    }

    private ushort NextTransactionId() => (ushort)Interlocked.Increment(ref _transactionId);

    private async ValueTask SendFrameAsync(Connection conn, ushort tid, byte unit, byte[] pdu, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var output = conn.Transport.Pipe.Output;
            var frame = _framing.Encode(tid, unit, pdu);
            Tap(FrameDirection.Outbound, frame, () => ModbusPdu.Describe(unit, pdu, isRequest: true));
            await output.WriteAsync(frame, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = HandleConnectionLostAsync(conn, ex);
            throw new TransportException($"Failed to send Modbus request: {ex.Message}", ex);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async ValueTask<Connection> EnsureConnectedAsync(CancellationToken ct)
    {
        var conn = _connection;
        if (conn is { IsAlive: true }) return conn;

        if (_reconnectAttempt > 0)
        {
            if (!_options.Reconnect.Enabled) throw new TransportException("Modbus connection lost and reconnect is disabled.");
            if (!_options.Reconnect.CanRetry(_reconnectAttempt)) throw new TransportException("Modbus reconnect attempts exhausted.");
            var wait = Volatile.Read(ref _nextReconnectTicks) - Environment.TickCount64;
            if (wait > 0) throw new TransportException($"Modbus connection lost; next reconnect attempt in {wait} ms.");
            IoTComDiagnostics.Reconnects.Add(1, new KeyValuePair<string, object?>("protocol", Protocol));
        }

        try
        {
            await ConnectAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _reconnectAttempt++;
            Volatile.Write(ref _nextReconnectTicks, Environment.TickCount64 + (long)_options.Reconnect.GetDelay(_reconnectAttempt).TotalMilliseconds);
            throw;
        }
        return _connection ?? throw new TransportException("Modbus connection closed during connect.");
    }

    private async Task ReadLoopAsync(Connection conn)
    {
        var reader = conn.Transport.Pipe.Input;
        Exception? error = null;
        try
        {
            while (true)
            {
                var result = await reader.ReadAsync(conn.Cancellation.Token).ConfigureAwait(false);
                var buffer = result.Buffer;
                while (true)
                {
                    var status = _framing.TryRead(ref buffer, expectRequest: false, out var adu);
                    if (status == FrameDecodeStatus.NeedMoreData) break;
                    if (status == FrameDecodeStatus.Invalid)
                    {
                        IoTComDiagnostics.Errors.Add(1, new KeyValuePair<string, object?>("protocol", Protocol));
                        continue;
                    }
                    Tap(FrameDirection.Inbound, adu.Raw, () => ModbusPdu.Describe(adu.UnitId, adu.Pdu, isRequest: false));
                    if (_pending.TryGetValue(adu.TransactionId, out var pending) && (_framing.HasTransactionIds || pending.UnitId == adu.UnitId))
                        pending.Completion.TrySetResult(adu.Pdu);
                    else
                        Logger.LogDebug("Dropping unsolicited Modbus frame tid={Tid} unit={Unit}", adu.TransactionId, adu.UnitId);
                }
                reader.AdvanceTo(buffer.Start, buffer.End);
                if (result.IsCompleted) break;
            }
        }
        catch (OperationCanceledException) when (conn.Cancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            error = ex;
        }
        await HandleConnectionLostAsync(conn, error ?? new TransportException("Remote closed the connection.")).ConfigureAwait(false);
    }

    private async Task HandleConnectionLostAsync(Connection conn, Exception error)
    {
        if (Interlocked.CompareExchange(ref _connection, null, conn) != conn) return;
        Logger.LogWarning(error, "Modbus connection lost");
        await conn.CloseAsync().ConfigureAwait(false);
        FailPending(error as IoTComException ?? new TransportException(error.Message, error));
        _reconnectAttempt = Math.Max(_reconnectAttempt, 1);
        Volatile.Write(ref _nextReconnectTicks, Environment.TickCount64 + (long)_options.Reconnect.GetDelay(1).TotalMilliseconds);
        if (!IsDisposed) SetState(EndpointState.Disconnected, error);
    }

    private void FailPending(Exception error)
    {
        foreach (var p in _pending.Values) p.Completion.TrySetException(error);
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore()
    {
        var conn = Interlocked.Exchange(ref _connection, null);
        if (conn is not null) await conn.CloseAsync().ConfigureAwait(false);
        FailPending(new ObjectDisposedException(nameof(ModbusClient)));
        SetState(EndpointState.Disconnected);
        _concurrency.Dispose();
        _writeLock.Dispose();
        _connectLock.Dispose();
    }

    private sealed class Pending(byte unitId)
    {
        public byte UnitId { get; } = unitId;
        public TaskCompletionSource<byte[]> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Connection(ITransport transport)
    {
        private int _closed;
        public ITransport Transport { get; } = transport;
        public CancellationTokenSource Cancellation { get; } = new();
        public Task? ReadLoop { get; set; }
        public bool IsAlive => Volatile.Read(ref _closed) == 0;

        public async ValueTask CloseAsync()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            await Cancellation.CancelAsync().ConfigureAwait(false);
            await Transport.DisposeAsync().ConfigureAwait(false);
        }
    }
}
