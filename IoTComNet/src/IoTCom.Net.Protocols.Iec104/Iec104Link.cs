using System.Threading.Channels;
using IoTCom.Net.Framing;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Protocols.Iec104;

/// <summary>APCI parameters (IEC 60870-5-104 §9.6). Defaults are the standard values.</summary>
public sealed class Iec104LinkParameters
{
    /// <summary>k: maximum unacknowledged I frames sent (default 12).</summary>
    public int K { get; set; } = 12;

    /// <summary>w: acknowledge after this many received I frames (default 8).</summary>
    public int W { get; set; } = 8;

    /// <summary>t1: time-out of sent APDUs and TESTFR/STARTDT confirmations (default 15 s).</summary>
    public TimeSpan T1 { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>t2: acknowledge received I frames at the latest after this time (default 10 s, must be &lt; t1).</summary>
    public TimeSpan T2 { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>t3: send TESTFR after this idle time (default 20 s).</summary>
    public TimeSpan T3 { get; set; } = TimeSpan.FromSeconds(20);
}

/// <summary>
/// One IEC 104 connection: APDU framing, N(S)/N(R) bookkeeping, the k and w windows, the t1/t2/t3 timers and the
/// STARTDT/STOPDT/TESTFR handshakes. Used by both <see cref="Iec104Client"/> and <see cref="Iec104Server"/>.
/// </summary>
internal sealed class Iec104Link : IAsyncDisposable
{
    private readonly ITransport _transport;
    private readonly Iec104LinkParameters _p;
    private readonly Action<FrameDirection, byte[]> _tap;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly SemaphoreSlim _sendOrder = new(1, 1);
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly CancellationToken _token;
    private readonly Channel<byte[]> _inbox = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly Queue<(ushort Seq, long At)> _sent = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _window = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource? _pendingU;
    private Iec104UFunction _pendingUCon;
    private long _pendingUAt;
    private ushort _vs, _vr, _acked;
    private int _unackedIn;
    private long _firstUnackedIn, _lastRx;
    private long _testSentAt = -1;

    public Iec104Link(ITransport transport, Iec104LinkParameters parameters, Action<FrameDirection, byte[]> tap, ILogger logger)
    {
        (_transport, _p, _tap, _logger) = (transport, parameters, tap, logger);
        _lastRx = Environment.TickCount64;
        _token = _cts.Token;
    }

    /// <summary>Called for every received ASDU (raw bytes; the receiver parses them).</summary>
    public Func<byte[], Task>? AsduReceived { get; set; }

    /// <summary>Raised when the peer starts or stops data transfer (controlled-station side).</summary>
    public event Action<bool>? DataTransferChanged;

    /// <summary>True after STARTDT (either direction).</summary>
    public bool DataTransferActive { get; private set; }

    /// <summary>Completes when the connection is closed; the result is the reason (null when closed locally).</summary>
    public Task Closed => _closed.Task;

    /// <summary>Why the link closed.</summary>
    public Exception? Error { get; private set; }

    /// <summary>Remote address.</summary>
    public string Peer => _transport.Info.RemoteAddress ?? "peer";

    public void Start()
    {
        _ = Task.Run(ReadLoopAsync, CancellationToken.None);
        _ = Task.Run(TimerLoopAsync, CancellationToken.None);
        _ = Task.Run(DispatchLoopAsync, CancellationToken.None);
    }

    // ASDUs are handled off the read loop, so a handler that sends (and waits for the k window) never blocks acknowledgements.
    private async Task DispatchLoopAsync()
    {
        try
        {
            await foreach (var asdu in _inbox.Reader.ReadAllAsync(_token).ConfigureAwait(false))
            {
                try
                {
                    if (AsduReceived is { } handler) await handler(asdu).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is ProtocolException or TransportException or IoTComTimeoutException or ArgumentException)
                {
                    _logger.LogDebug(ex, "IEC 104 ASDU handler failed for {Peer}", Peer);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private int Outstanding => (_vs - _acked) & 0x7FFF;

    /// <summary>Sends an ASDU in an I frame, waiting while k frames are unacknowledged.</summary>
    public async Task SendAsduAsync(byte[] asdu, CancellationToken ct)
    {
        await _sendOrder.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            while (true)
            {
                Task wait;
                lock (_gate)
                {
                    if (_closed.Task.IsCompleted) throw new TransportException("The IEC 104 connection is closed.", Error);
                    if (Outstanding < _p.K) break;
                    if (_window.Task.IsCompleted) _window = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    wait = _window.Task;
                }

                await Task.WhenAny(wait, _closed.Task).WaitAsync(ct).ConfigureAwait(false);
            }

            await _write.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                byte[] frame;
                lock (_gate)
                {
                    frame = Iec104Apdu.I(_vs, _vr, asdu).Encode();
                    _sent.Enqueue((_vs, Environment.TickCount64));
                    _vs = (ushort)((_vs + 1) & 0x7FFF);
                    _unackedIn = 0;   // the I frame acknowledges what we received
                }

                await WriteRawAsync(frame, ct).ConfigureAwait(false);
            }
            finally
            {
                _write.Release();
            }
        }
        finally
        {
            _sendOrder.Release();
        }
    }

    /// <summary>Sends STARTDT/STOPDT/TESTFR act and waits for the confirmation (t1).</summary>
    public async Task ActivateAsync(Iec104UFunction act, CancellationToken ct)
    {
        var con = act switch
        {
            Iec104UFunction.StartDtActivation => Iec104UFunction.StartDtConfirmation,
            Iec104UFunction.StopDtActivation => Iec104UFunction.StopDtConfirmation,
            _ => Iec104UFunction.TestFrConfirmation,
        };
        TaskCompletionSource tcs;
        lock (_gate)
        {
            tcs = _pendingU = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingUCon = con;
            _pendingUAt = Environment.TickCount64;
        }

        await WriteAsync(Iec104Apdu.U(act).Encode(), ct).ConfigureAwait(false);
        await Task.WhenAny(tcs.Task, _closed.Task).WaitAsync(ct).ConfigureAwait(false);
        if (!tcs.Task.IsCompleted) throw new IoTComTimeoutException($"No {Iec104Apdu.FunctionName(con)} within t1.", Error);
        if (act == Iec104UFunction.StartDtActivation) DataTransferActive = true;
        if (act == Iec104UFunction.StopDtActivation) DataTransferActive = false;
    }

    private async Task WriteAsync(byte[] frame, CancellationToken ct)
    {
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try { await WriteRawAsync(frame, ct).ConfigureAwait(false); }
        finally { _write.Release(); }
    }

    private async Task WriteRawAsync(byte[] frame, CancellationToken ct)
    {
        _tap(FrameDirection.Outbound, frame);
        try
        {
            await _transport.Pipe.Output.WriteAsync(frame, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            Close(new TransportException("Write failed.", ex));
            throw new TransportException("The IEC 104 connection is closed.", ex);
        }
    }

    private async Task SendSupervisoryAsync()
    {
        ushort nr;
        lock (_gate)
        {
            if (_unackedIn == 0) return;
            _unackedIn = 0;
            nr = _vr;
        }

        await WriteAsync(Iec104Apdu.S(nr).Encode(), _token).ConfigureAwait(false);
    }

    private async Task ReadLoopAsync()
    {
        var ct = _token;
        try
        {
            await foreach (var frame in _transport.Pipe.Input.ReadFramesAsync(new Iec104Framing(), ct).ConfigureAwait(false))
            {
                _tap(FrameDirection.Inbound, frame);
                var apdu = Iec104Apdu.Parse(frame);
                Volatile.Write(ref _lastRx, Environment.TickCount64);
                switch (apdu.Format)
                {
                    case Iec104Format.I:
                        bool ackNow;
                        lock (_gate)
                        {
                            if (apdu.SendSequence != _vr) throw new ProtocolException($"Sequence error: received N(S)={apdu.SendSequence}, expected {_vr}.");
                            _vr = (ushort)((_vr + 1) & 0x7FFF);
                            Acknowledge(apdu.ReceiveSequence);
                            if (_unackedIn++ == 0) _firstUnackedIn = Environment.TickCount64;
                            ackNow = _unackedIn >= _p.W;
                        }

                        if (ackNow) await SendSupervisoryAsync().ConfigureAwait(false);
                        _inbox.Writer.TryWrite(apdu.Asdu.ToArray());
                        break;
                    case Iec104Format.S:
                        lock (_gate) Acknowledge(apdu.ReceiveSequence);
                        break;
                    default:
                        await OnUnnumberedAsync(apdu.Function).ConfigureAwait(false);
                        break;
                }
            }

            Close(new TransportException("The peer closed the connection."));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is ProtocolException or IOException or TransportException or ObjectDisposedException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "IEC 104 link to {Peer} closed", Peer);
            Close(ex);
        }
    }

    private async Task OnUnnumberedAsync(Iec104UFunction f)
    {
        switch (f)
        {
            case Iec104UFunction.StartDtActivation:
                DataTransferActive = true;
                await WriteAsync(Iec104Apdu.U(Iec104UFunction.StartDtConfirmation).Encode(), _token).ConfigureAwait(false);
                DataTransferChanged?.Invoke(true);
                break;
            case Iec104UFunction.StopDtActivation:
                await SendSupervisoryAsync().ConfigureAwait(false);
                DataTransferActive = false;
                await WriteAsync(Iec104Apdu.U(Iec104UFunction.StopDtConfirmation).Encode(), _token).ConfigureAwait(false);
                DataTransferChanged?.Invoke(false);
                break;
            case Iec104UFunction.TestFrActivation:
                await WriteAsync(Iec104Apdu.U(Iec104UFunction.TestFrConfirmation).Encode(), _token).ConfigureAwait(false);
                break;
            default:
                lock (_gate)
                {
                    if (f == Iec104UFunction.TestFrConfirmation) _testSentAt = -1;
                    if (_pendingU is { } p && f == _pendingUCon)
                    {
                        _pendingU = null;
                        p.TrySetResult();
                    }
                }

                break;
        }
    }

    // Called under _gate: N(R) acknowledges every frame with N(S) < N(R).
    private void Acknowledge(ushort nr)
    {
        var count = (nr - _acked) & 0x7FFF;
        if (count > Outstanding) throw new ProtocolException($"Acknowledgement N(R)={nr} for frames that were never sent (V(S)={_vs}).");
        for (var i = 0; i < count; i++) _sent.Dequeue();
        _acked = nr;
        if (count > 0) _window.TrySetResult();
    }

    private async Task TimerLoopAsync()
    {
        var ct = _token;
        var tick = TimeSpan.FromMilliseconds(Math.Clamp(Math.Min(_p.T2.TotalMilliseconds, _p.T1.TotalMilliseconds) / 10, 10, 200));
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(tick, ct).ConfigureAwait(false);
                var now = Environment.TickCount64;
                bool ack = false, test = false;
                string? fault = null;
                lock (_gate)
                {
                    if (_sent.Count > 0 && now - _sent.Peek().At > _p.T1.TotalMilliseconds) fault = "t1 expired: sent I frames were not acknowledged.";
                    else if (_pendingU is not null && now - _pendingUAt > _p.T1.TotalMilliseconds) fault = $"t1 expired: no {Iec104Apdu.FunctionName(_pendingUCon)}.";
                    else if (_testSentAt >= 0 && now - _testSentAt > _p.T1.TotalMilliseconds) fault = "t1 expired: no TESTFR con.";
                    if (_unackedIn > 0 && now - _firstUnackedIn >= _p.T2.TotalMilliseconds) ack = true;
                    if (_testSentAt < 0 && now - Volatile.Read(ref _lastRx) >= _p.T3.TotalMilliseconds)
                    {
                        test = true;
                        _testSentAt = now;
                    }
                }

                if (fault is not null)
                {
                    Close(new IoTComTimeoutException(fault));
                    return;
                }

                if (ack) await SendSupervisoryAsync().ConfigureAwait(false);
                if (test) await WriteAsync(Iec104Apdu.U(Iec104UFunction.TestFrActivation).Encode(), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (TransportException)
        {
        }
    }

    public void Close(Exception? reason)
    {
        lock (_gate)
        {
            if (_closed.Task.IsCompleted) return;
            Error = reason;
            _closed.TrySetResult();
            _window.TrySetResult();
        }

        DataTransferActive = false;
        _inbox.Writer.TryComplete();
        _cts.Cancel();
        _ = _transport.DisposeAsync().AsTask();
    }

    public ValueTask DisposeAsync()
    {
        Close(null);
        return ValueTask.CompletedTask;
    }
}
