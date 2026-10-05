using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using IoTCom.Net.Transport.Can;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Protocols.IsoTp;

/// <summary>ISO-TP channel configuration.</summary>
public sealed class IsoTpOptions
{
    /// <summary>CAN identifier we transmit on (e.g. 0x7E0 for a tester talking to the engine ECU).</summary>
    public uint TxId { get; set; }

    /// <summary>CAN identifier we receive on (e.g. 0x7E8).</summary>
    public uint RxId { get; set; }

    /// <summary>Force 29-bit identifiers (otherwise inferred from the values).</summary>
    public bool? ExtendedIds { get; set; }

    /// <summary>Use CAN FD frames (the bus must have <see cref="CanBusOptions.Fd"/> enabled).</summary>
    public bool Fd { get; set; }

    /// <summary>CAN FD bit-rate switch on transmitted frames.</summary>
    public bool BitRateSwitch { get; set; } = true;

    /// <summary>Transmit data length: 8 for classic CAN, up to 64 for CAN FD (default 64 when <see cref="Fd"/>).</summary>
    public int TxDataLength { get; set; }

    /// <summary>Padding byte (0xCC by default; null sends the shortest frames, classic CAN only).</summary>
    public byte? Padding { get; set; } = 0xCC;

    /// <summary>Extended/mixed addressing: address byte prepended to transmitted frames.</summary>
    public byte? TxAddressExtension { get; set; }

    /// <summary>Extended/mixed addressing: address byte expected in received frames.</summary>
    public byte? RxAddressExtension { get; set; }

    /// <summary>Block size we announce when receiving (0 = send everything).</summary>
    public byte BlockSize { get; set; }

    /// <summary>STmin we announce when receiving (raw ISO encoding: 0–127 ms, 0xF1–0xF9 = 100–900 µs).</summary>
    public byte SeparationTime { get; set; }

    /// <summary>FC.WAIT frames tolerated while sending.</summary>
    public ushort MaxWaitFrames { get; set; } = 10;

    /// <summary>N_Bs: how long we wait for flow control.</summary>
    public TimeSpan FlowControlTimeout { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>N_Cr: how long we wait for the next consecutive frame.</summary>
    public TimeSpan ConsecutiveFrameTimeout { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Largest message accepted (longer first frames are answered with FC.OVFLW).</summary>
    public int MaxMessageLength { get; set; } = 4095;

    /// <summary>Friendly name.</summary>
    public string? Name { get; set; }

    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }

    internal int EffectiveTxDataLength => TxDataLength > 0 ? TxDataLength : Fd ? 64 : 8;

    internal bool IsExtended(uint id) => ExtendedIds ?? id > CanFrame.MaxStandardId;
}

/// <summary>An ISO-TP transfer failed.</summary>
public sealed class IsoTpException : ProtocolException
{
    /// <summary>Creates the exception.</summary>
    public IsoTpException(IsoTpError error) : base(Describe(error)) => Error = error;

    /// <summary>Creates the exception.</summary>
    public IsoTpException() : this(IsoTpError.TimeoutBs) { }

    /// <summary>Creates the exception.</summary>
    public IsoTpException(string message) : base(message) { }

    /// <summary>Creates the exception.</summary>
    public IsoTpException(string message, Exception? inner) : base(message, inner) { }

    /// <summary>The error.</summary>
    public IsoTpError Error { get; }

    /// <summary>Human-readable description.</summary>
    public static string Describe(IsoTpError error) => error switch
    {
        IsoTpError.TimeoutBs => "ISO-TP: no flow control from the receiver (N_Bs timeout).",
        IsoTpError.TimeoutCr => "ISO-TP: the sender stopped mid-message (N_Cr timeout).",
        IsoTpError.WrongSequence => "ISO-TP: consecutive frame out of sequence.",
        IsoTpError.Overflow => "ISO-TP: the receiver reported overflow (message too large).",
        IsoTpError.TooManyWaits => "ISO-TP: too many FC.WAIT frames.",
        IsoTpError.InvalidFlowStatus => "ISO-TP: invalid flow-control status.",
        IsoTpError.Interrupted => "ISO-TP: reception interrupted by a new message.",
        IsoTpError.RxTooLarge => "ISO-TP: incoming message larger than MaxMessageLength.",
        _ => $"ISO-TP error {error}.",
    };
}

/// <summary>
/// A full-duplex ISO-TP (ISO 15765-2) channel on a CAN bus: segmentation, flow control and reassembly run in the
/// Rust state machine (<see cref="IsoTpMachine"/>); this class pumps frames between it and the <see cref="ICanBus"/>.
/// </summary>
/// <example>
/// <code>
/// await using var bus = await CanBus.OpenAsync("socketcan:can0");
/// await using var tp = IsoTpChannel.Create(bus, o => { o.TxId = 0x7E0; o.RxId = 0x7E8; });
/// await tp.ConnectAsync();
/// await tp.SendAsync(new byte[] { 0x22, 0xF1, 0x90 });
/// byte[] response = await tp.ReceiveAsync();
/// </code>
/// </example>
public sealed class IsoTpChannel : EndpointBase, IClientEndpoint
{
    private readonly ICanBus _bus;
    private readonly IsoTpOptions _options;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly Channel<byte[]> _tx = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<byte[]> _rx = Channel.CreateUnbounded<byte[]>();
    private readonly long _origin = Stopwatch.GetTimestamp();
    private IsoTpMachine? _machine;
    private CanReader? _reader;
    private CancellationTokenSource? _cts;
    private Task[] _loops = [];
    private TaskCompletionSource? _pendingSend;
    private long _messagesIn, _messagesOut;

    private IsoTpChannel(ICanBus bus, IsoTpOptions options) : base("isotp", options.Logger)
    {
        _bus = bus;
        _options = options;
        Name = options.Name ?? $"{options.TxId:X3}/{options.RxId:X3}";
    }

    /// <summary>Creates a channel (call <see cref="ConnectAsync"/> to start it).</summary>
    public static IsoTpChannel Create(ICanBus bus, Action<IsoTpOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new IsoTpOptions();
        configure(options);
        if (options.TxId == options.RxId) throw new ArgumentException("TxId and RxId must differ.", nameof(configure));
        return new IsoTpChannel(bus, options);
    }

    /// <summary>Options.</summary>
    public IsoTpOptions Options => _options;

    /// <summary>The underlying bus.</summary>
    public ICanBus Bus => _bus;

    /// <summary>Messages received.</summary>
    public long MessagesReceived => Interlocked.Read(ref _messagesIn);

    /// <summary>Messages sent.</summary>
    public long MessagesSent => Interlocked.Read(ref _messagesOut);

    /// <summary>Raised for reception errors (timeouts, wrong sequence, overflow).</summary>
    public event Action<IsoTpError>? ReceiveError;

    /// <summary>Raised when a first frame announces a long message (progress).</summary>
    public event Action<int>? ReceiveStarted;

    /// <inheritdoc />
    public async ValueTask ConnectAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (State == EndpointState.Connected) return;
        SetState(EndpointState.Connecting);
        try
        {
            if (_bus.State != EndpointState.Connected) await _bus.ConnectAsync(ct).ConfigureAwait(false);
            _machine = new IsoTpMachine(_options);
            _reader = _bus.OpenReader(CanFilter.Exact(_options.RxId, _options.IsExtended(_options.RxId)));
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _loops =
            [
                Task.Run(() => ReceiveLoopAsync(_reader, token), CancellationToken.None),
                Task.Run(() => TimerLoopAsync(token), CancellationToken.None),
                Task.Run(() => TransmitLoopAsync(token), CancellationToken.None),
            ];
            SetState(EndpointState.Connected);
        }
        catch (Exception ex)
        {
            SetState(EndpointState.Faulted, ex);
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisconnectAsync(CancellationToken ct = default)
    {
        if (State != EndpointState.Connected && State != EndpointState.Faulted) return;
        SetState(EndpointState.Stopping);
        await StopLoopsAsync().ConfigureAwait(false);
        SetState(EndpointState.Disconnected);
    }

    private async Task StopLoopsAsync()
    {
        if (_cts is not null) await _cts.CancelAsync().ConfigureAwait(false);
        _reader?.Dispose();
        try
        {
            await Task.WhenAll(_loops).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        _pendingSend?.TrySetCanceled();
        _machine?.Dispose();
        _cts?.Dispose();
        (_machine, _reader, _cts, _loops) = (null, null, null, []);
    }

    private ulong Now => (ulong)(Stopwatch.GetElapsedTime(_origin).Ticks / 10);

    /// <summary>Sends one message and waits until its last frame has been handed to the bus.</summary>
    /// <exception cref="IsoTpException">Flow-control timeout, overflow or invalid flow status.</exception>
    public async Task SendAsync(ReadOnlyMemory<byte> message, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (State != EndpointState.Connected) throw new InvalidOperationException("ISO-TP channel is not connected.");
        if (message.Length == 0) throw new ArgumentException("Message must not be empty.", nameof(message));
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            List<Action>? actions;
            lock (_gate)
            {
                _pendingSend = done;
                _machine!.Send(Now, message.Span);
                actions = Drain();
            }
            Run(actions);
            Tap(FrameDirection.Outbound, message.Span);
            Wake();
            await done.Task.WaitAsync(ct).ConfigureAwait(false);
            Interlocked.Increment(ref _messagesOut);
        }
        finally
        {
            lock (_gate) _pendingSend = null;
            _sendGate.Release();
        }
    }

    /// <summary>Waits for the next received message.</summary>
    public ValueTask<byte[]> ReceiveAsync(CancellationToken ct = default) => _rx.Reader.ReadAsync(ct);

    /// <summary>Waits for the next received message, or returns null after <paramref name="timeout"/>.</summary>
    public async ValueTask<byte[]?> ReceiveAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            return await _rx.Reader.ReadAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Discards messages that were received but not read yet.</summary>
    public int DiscardPending()
    {
        var n = 0;
        while (_rx.Reader.TryRead(out _)) n++;
        return n;
    }

    /// <summary>Streams received messages.</summary>
    public async IAsyncEnumerable<byte[]> ReadAllAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var m in _rx.Reader.ReadAllAsync(ct).ConfigureAwait(false)) yield return m;
    }

    private async Task ReceiveLoopAsync(CanReader reader, CancellationToken ct)
    {
        try
        {
            await foreach (var frame in reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (frame.IsRemote || frame.IsError) continue;
                List<Action>? actions;
                lock (_gate)
                {
                    if (_machine is null) return;
                    _machine.HandleFrame(Now, frame.Data.Span);
                    actions = Drain();
                }
                Run(actions);
                Wake();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "ISO-TP receive loop {Name} stopped", Name);
            SetState(EndpointState.Faulted, ex);
        }
    }

    private async Task TimerLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                ulong? deadline;
                lock (_gate) deadline = _machine?.PollTimeout();
                if (deadline is { } d)
                {
                    var now = Now;
                    if (d > now)
                    {
                        var wait = TimeSpan.FromTicks((long)Math.Min(d - now, (ulong)TimeSpan.FromSeconds(30).Ticks / 10) * 10);
                        await _wake.WaitAsync(wait, ct).ConfigureAwait(false);
                    }
                    List<Action>? actions;
                    lock (_gate)
                    {
                        if (_machine is null) return;
                        _machine.HandleTimeout(Now);
                        actions = Drain();
                    }
                    Run(actions);
                }
                else
                {
                    await _wake.WaitAsync(Timeout.Infinite, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task TransmitLoopAsync(CancellationToken ct)
    {
        var flags = (_options.IsExtended(_options.TxId) ? CanFrameFlags.Extended : CanFrameFlags.None)
                    | (_options.Fd ? CanFrameFlags.Fd | (_options.BitRateSwitch ? CanFrameFlags.BitRateSwitch : 0) : 0);
        try
        {
            await foreach (var payload in _tx.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                await _bus.SendAsync(new CanFrame(_options.TxId, payload, flags), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "ISO-TP transmit to {Bus} failed", _bus.Channel);
            lock (_gate) _pendingSend?.TrySetException(ex);
        }
    }

    private void Wake()
    {
        try
        {
            if (_wake.CurrentCount == 0) _wake.Release();
        }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Moves frames to the transmit queue and turns events into actions run outside the lock.</summary>
    private List<Action>? Drain()
    {
        List<Action>? actions = null;
        var m = _machine!;
        while (m.PollFrame() is { } frame) _tx.Writer.TryWrite(frame);
        while (m.PollEvent() is { } e)
        {
            switch (e.Kind)
            {
                case IsoTpEventKind.Received:
                    var data = e.Data;
                    Interlocked.Increment(ref _messagesIn);
                    (actions ??= []).Add(() =>
                    {
                        Tap(FrameDirection.Inbound, data);
                        _rx.Writer.TryWrite(data);
                    });
                    break;
                case IsoTpEventKind.RxStarted:
                    var len = (int)e.Value;
                    (actions ??= []).Add(() => ReceiveStarted?.Invoke(len));
                    break;
                case IsoTpEventKind.Sent:
                    var sent = _pendingSend;
                    (actions ??= []).Add(() => sent?.TrySetResult());
                    break;
                case IsoTpEventKind.Error when e.IsTransmitError:
                    var failed = _pendingSend;
                    var txError = e.Error;
                    (actions ??= []).Add(() => failed?.TrySetException(new IsoTpException(txError)));
                    break;
                case IsoTpEventKind.Error:
                    var rxError = e.Error;
                    (actions ??= []).Add(() =>
                    {
                        Logger.LogDebug("ISO-TP {Name}: {Error}", Name, rxError);
                        ReceiveError?.Invoke(rxError);
                    });
                    break;
            }
        }
        return actions;
    }

    private static void Run(List<Action>? actions)
    {
        if (actions is null) return;
        foreach (var a in actions) a();
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore()
    {
        await StopLoopsAsync().ConfigureAwait(false);
        _tx.Writer.TryComplete();
        _rx.Writer.TryComplete();
        _sendGate.Dispose();
        _wake.Dispose();
    }
}
