using System.Security.Cryptography;
using System.Threading.Channels;

namespace IoTCom.Net.Adapters.Zenoh;

/// <summary>
/// An in-process Zenoh network: sessions created from it route puts, deletes and queries to each other (and to
/// themselves, as Zenoh does) using <see cref="ZenohKeyExpr"/> matching. Used by tests, notebooks, the Gallery and
/// <c>--sim</c>; no native library or sockets involved. Delivery to one session is in order and on a background thread.
/// </summary>
/// <example>
/// <code>
/// var net = new VirtualZenohNetwork();
/// await using var a = ZenohSession.Create(o => o.UseVirtual(net));
/// await using var b = ZenohSession.Create(o => o.UseVirtual(net));
/// await a.ConnectAsync(); await b.ConnectAsync();
/// </code>
/// </example>
public sealed class VirtualZenohNetwork
{
    private readonly Lock _gate = new();
    private readonly List<VirtualBackend> _backends = [];

    /// <summary>Number of open sessions.</summary>
    public int SessionCount
    {
        get
        {
            lock (_gate) return _backends.Count;
        }
    }

    /// <summary>Creates a backend (one session on this network) with an optional fixed zenoh id.</summary>
    public IZenohBackend CreateBackend(string? zid = null)
    {
        var backend = new VirtualBackend(this, zid ?? Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)));
        lock (_gate) _backends.Add(backend);
        return backend;
    }

    private VirtualBackend[] Snapshot()
    {
        lock (_gate) return [.. _backends];
    }

    private void Remove(VirtualBackend backend)
    {
        lock (_gate) _backends.Remove(backend);
    }

    private sealed class Subscription(string keyExpr, Action<ZenohSample> handler)
    {
        public string KeyExpr { get; } = keyExpr;
        public Action<ZenohSample> Handler { get; } = handler;
    }

    private sealed class Queryable(string keyExpr, Action<string, string, byte[]?, IZenohQueryResponder> handler)
    {
        public string KeyExpr { get; } = keyExpr;
        public Action<string, string, byte[]?, IZenohQueryResponder> Handler { get; } = handler;
    }

    private sealed class VirtualBackend : IZenohBackend
    {
        private readonly VirtualZenohNetwork _network;
        private readonly Channel<Action> _inbox = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
        private readonly Task _pump;
        private readonly List<Subscription> _subscriptions = [];
        private readonly List<Queryable> _queryables = [];
        private readonly Lock _gate = new();
        private bool _closed;

        public VirtualBackend(VirtualZenohNetwork network, string zid)
        {
            _network = network;
            Zid = zid;
            _pump = Task.Run(PumpAsync);
        }

        public string Zid { get; }

        public string Description => "virtual zenoh network";

        private async Task PumpAsync()
        {
            await foreach (var work in _inbox.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    work();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // A faulty handler must not stop delivery to this session.
                    System.Diagnostics.Debug.WriteLine($"Zenoh virtual handler failed: {ex.Message}");
                }
            }
        }

        private void Enqueue(Action work) => _inbox.Writer.TryWrite(work);

        private void ThrowIfClosed() => ObjectDisposedException.ThrowIf(_closed, this);

        public Task PutAsync(string key, ReadOnlyMemory<byte> payload, string? encoding, CancellationToken ct) => Publish(key, ZenohSampleKind.Put, payload, encoding, ct);

        public Task DeleteAsync(string key, CancellationToken ct) => Publish(key, ZenohSampleKind.Delete, default, null, ct);

        private Task Publish(string key, ZenohSampleKind kind, ReadOnlyMemory<byte> payload, string? encoding, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ThrowIfClosed();
            var bytes = payload.ToArray();
            foreach (var backend in _network.Snapshot())
            {
                Subscription[] matching;
                lock (backend._gate) matching = [.. backend._subscriptions.Where(s => ZenohKeyExpr.Intersects(s.KeyExpr, key))];
                foreach (var sub in matching)
                {
                    var handler = sub.Handler;
                    backend.Enqueue(() => handler(new ZenohSample(key, kind, bytes, encoding, DateTimeOffset.UtcNow)));
                }
            }

            return Task.CompletedTask;
        }

        public IDisposable DeclareSubscriber(string keyExpr, Action<ZenohSample> handler)
        {
            ThrowIfClosed();
            var sub = new Subscription(keyExpr, handler);
            lock (_gate) _subscriptions.Add(sub);
            return new Undeclare(() =>
            {
                lock (_gate) _subscriptions.Remove(sub);
            });
        }

        public IDisposable DeclareQueryable(string keyExpr, Action<string, string, byte[]?, IZenohQueryResponder> handler)
        {
            ThrowIfClosed();
            var queryable = new Queryable(keyExpr, handler);
            lock (_gate) _queryables.Add(queryable);
            return new Undeclare(() =>
            {
                lock (_gate) _queryables.Remove(queryable);
            });
        }

        public async Task GetAsync(string selector, ReadOnlyMemory<byte> payload, TimeSpan timeout, Action<ZenohReply> onReply, CancellationToken ct)
        {
            ThrowIfClosed();
            var q = selector.IndexOf('?', StringComparison.Ordinal);
            var key = q < 0 ? selector : selector[..q];
            var parameters = q < 0 ? "" : selector[(q + 1)..];
            var body = payload.IsEmpty ? null : payload.ToArray();

            var targets = new List<(VirtualBackend Backend, Queryable Queryable)>();
            foreach (var backend in _network.Snapshot())
            {
                lock (backend._gate) targets.AddRange(backend._queryables.Where(x => ZenohKeyExpr.Intersects(x.KeyExpr, key)).Select(x => (backend, x)));
            }

            if (targets.Count == 0) return;
            var pending = targets.Count;
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var replyGate = new Lock();
            foreach (var (backend, queryable) in targets)
            {
                var responder = new GetResponder(key, onReply, replyGate, () =>
                {
                    if (Interlocked.Decrement(ref pending) == 0) done.TrySetResult();
                });
                var handler = queryable.Handler;
                backend.Enqueue(() =>
                {
                    try
                    {
                        handler(key, parameters, body, responder);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        responder.Finish();
                        System.Diagnostics.Debug.WriteLine($"Zenoh virtual queryable failed: {ex.Message}");
                    }
                });
            }

            using var timer = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token);
            try
            {
                await done.Task.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Timed out: whatever arrived is the result, as in Zenoh.
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_closed) return;
            _closed = true;
            _network.Remove(this);
            lock (_gate)
            {
                _subscriptions.Clear();
                _queryables.Clear();
            }

            _inbox.Writer.TryComplete();
            await _pump.ConfigureAwait(false);
        }
    }

    private sealed class GetResponder(string queryKey, Action<ZenohReply> onReply, Lock gate, Action finished) : IZenohQueryResponder
    {
        private int _finished;

        public ValueTask ReplyAsync(string key, ReadOnlyMemory<byte> payload, string? encoding, CancellationToken ct)
        {
            if (Volatile.Read(ref _finished) != 0) throw new InvalidOperationException("The query is already finished.");
            if (!ZenohKeyExpr.Intersects(key, queryKey)) throw new ArgumentException($"Reply key '{key}' does not intersect the query key '{queryKey}'.", nameof(key));
            Deliver(new ZenohReply { Sample = new ZenohSample(key, ZenohSampleKind.Put, payload.ToArray(), encoding, DateTimeOffset.UtcNow) });
            return ValueTask.CompletedTask;
        }

        public ValueTask ReplyErrorAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            if (Volatile.Read(ref _finished) != 0) throw new InvalidOperationException("The query is already finished.");
            Deliver(new ZenohReply { Error = payload.ToArray() });
            return ValueTask.CompletedTask;
        }

        private void Deliver(ZenohReply reply)
        {
            lock (gate) onReply(reply);
        }

        public void Finish()
        {
            if (Interlocked.Exchange(ref _finished, 1) == 0) finished();
        }
    }

    private sealed class Undeclare(Action action) : IDisposable
    {
        private Action? _action = action;

        public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
    }
}
