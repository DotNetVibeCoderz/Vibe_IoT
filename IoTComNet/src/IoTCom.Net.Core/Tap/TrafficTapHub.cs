namespace IoTCom.Net;

/// <summary>
/// Fan-out tap: forwards frames to any number of taps. Copy-on-write so <see cref="OnFrame"/> is lock free.
/// </summary>
public sealed class TrafficTapHub : ITrafficTap
{
    private ITrafficTap[] _taps = [];
    private readonly Lock _gate = new();

    /// <summary>True when at least one tap is attached (lets callers skip work).</summary>
    public bool HasTaps => Volatile.Read(ref _taps).Length > 0;

    /// <summary>Attaches a tap. Dispose the result to detach it.</summary>
    public IDisposable Add(ITrafficTap tap)
    {
        ArgumentNullException.ThrowIfNull(tap);
        lock (_gate) _taps = [.. _taps, tap];
        return new Detach(this, tap);
    }

    /// <summary>Detaches a tap.</summary>
    public void Remove(ITrafficTap tap)
    {
        lock (_gate) _taps = _taps.Where(t => !ReferenceEquals(t, tap)).ToArray();
    }

    /// <inheritdoc />
    public void OnFrame(in TrafficFrame frame)
    {
        var taps = Volatile.Read(ref _taps);
        foreach (var t in taps)
        {
            try { t.OnFrame(frame); }
            catch { /* a faulty tap must never break the I/O path */ }
        }
    }

    private sealed class Detach(TrafficTapHub hub, ITrafficTap tap) : IDisposable
    {
        public void Dispose() => hub.Remove(tap);
    }
}

/// <summary>
/// Keeps the most recent frames in a bounded ring buffer and raises <see cref="FrameCaptured"/>.
/// Ideal for UIs (Gallery Protocol Inspector) and diagnostics.
/// </summary>
public sealed class RecordingTap(int capacity = 1000) : ITrafficTap
{
    private readonly TrafficFrame[] _ring = new TrafficFrame[Math.Max(1, capacity)];
    private long _count;
    private readonly Lock _gate = new();

    /// <summary>Raised for every captured frame (on the I/O thread).</summary>
    public event Action<TrafficFrame>? FrameCaptured;

    /// <summary>Total frames seen since creation.</summary>
    public long TotalFrames => Interlocked.Read(ref _count);

    /// <inheritdoc />
    public void OnFrame(in TrafficFrame frame)
    {
        var owned = frame.ToOwned();
        lock (_gate)
        {
            _ring[_count % _ring.Length] = owned;
            _count++;
        }
        FrameCaptured?.Invoke(owned);
    }

    /// <summary>Returns the buffered frames, oldest first.</summary>
    public IReadOnlyList<TrafficFrame> Snapshot()
    {
        lock (_gate)
        {
            var n = (int)Math.Min(_count, _ring.Length);
            var result = new TrafficFrame[n];
            var start = _count - n;
            for (var i = 0; i < n; i++) result[i] = _ring[(start + i) % _ring.Length];
            return result;
        }
    }

    /// <summary>Clears the buffer.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_ring);
            _count = 0;
        }
    }
}

/// <summary>Adapts a delegate to <see cref="ITrafficTap"/>.</summary>
public sealed class DelegateTap(Action<TrafficFrame> onFrame) : ITrafficTap
{
    /// <inheritdoc />
    public void OnFrame(in TrafficFrame frame) => onFrame(frame);
}
