using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IoTCom.Net;

/// <summary>
/// Base class for endpoints: thread-safe state machine, <see cref="IEndpoint.StateChanged"/> event,
/// logging and traffic tap plumbing.
/// </summary>
public abstract class EndpointBase : IEndpoint
{
    private int _state;
    private readonly TrafficTapHub _taps = new();

    /// <summary>Creates the base with an optional logger.</summary>
    protected EndpointBase(string protocol, ILogger? logger = null)
    {
        Protocol = protocol;
        Logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public string Protocol { get; }

    /// <summary>Optional friendly name used in logs, metrics and the traffic tap.</summary>
    public string? Name { get; init; }

    /// <inheritdoc />
    public EndpointState State => (EndpointState)Volatile.Read(ref _state);

    /// <inheritdoc />
    public event EventHandler<StateChangedEventArgs>? StateChanged;

    /// <summary>Logger used by the endpoint.</summary>
    protected ILogger Logger { get; }

    /// <summary>Attached traffic taps.</summary>
    public TrafficTapHub Taps => _taps;

    /// <summary>Attaches a tap that receives every raw frame. Returns a handle that detaches it.</summary>
    public IDisposable AddTap(ITrafficTap tap) => _taps.Add(tap);

    /// <summary>Atomically moves to <paramref name="next"/> and raises <see cref="StateChanged"/>.</summary>
    protected void SetState(EndpointState next, Exception? error = null)
    {
        var prev = (EndpointState)Interlocked.Exchange(ref _state, (int)next);
        if (prev == next) return;
        Logger.LogDebug("{Protocol} endpoint {Name}: {Previous} -> {Current}", Protocol, Name, prev, next);
        if (next == EndpointState.Faulted)
            IoTComDiagnostics.Errors.Add(1, new KeyValuePair<string, object?>("protocol", Protocol));
        StateChanged?.Invoke(this, new StateChangedEventArgs(prev, next, error));
    }

    /// <summary>Publishes a frame to the attached taps and updates frame/byte metrics.</summary>
    protected void Tap(FrameDirection direction, ReadOnlySpan<byte> frame, Func<string?>? summary = null)
    {
        var tag = new KeyValuePair<string, object?>("protocol", Protocol);
        if (direction == FrameDirection.Inbound)
        {
            IoTComDiagnostics.FramesIn.Add(1, tag);
            IoTComDiagnostics.BytesIn.Add(frame.Length, tag);
        }
        else
        {
            IoTComDiagnostics.FramesOut.Add(1, tag);
            IoTComDiagnostics.BytesOut.Add(frame.Length, tag);
        }

        if (!_taps.HasTaps) return;
        // Taps get an owned copy so they can keep it; this path only runs while someone is listening.
        _taps.OnFrame(new TrafficFrame(Protocol, direction, frame.ToArray(), DateTimeOffset.UtcNow, Name, summary?.Invoke()));
    }

    /// <summary>Throws <see cref="ObjectDisposedException"/> when disposed.</summary>
    protected void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);

    /// <summary>True after <see cref="DisposeAsync"/>.</summary>
    protected bool IsDisposed { get; private set; }

    /// <summary>Releases resources. Override <see cref="DisposeAsyncCore"/>.</summary>
    public async ValueTask DisposeAsync()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        await DisposeAsyncCore().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    /// <summary>Endpoint specific cleanup.</summary>
    protected virtual ValueTask DisposeAsyncCore() => ValueTask.CompletedTask;
}
