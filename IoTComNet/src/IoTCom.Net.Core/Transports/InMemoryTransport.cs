using System.IO.Pipelines;
using System.Threading.Channels;

namespace IoTCom.Net.Transports;

/// <summary>
/// In-process transport backed by two <see cref="System.IO.Pipelines.Pipe"/>s.
/// Used by tests, notebooks, the Gallery and simulators so everything runs without hardware.
/// </summary>
public sealed class InMemoryTransport : ITransport
{
    private readonly IDuplexPipe _pipe;
    private int _disposed;

    private InMemoryTransport(PipeReader input, PipeWriter output, string local, string remote)
    {
        _pipe = new Duplex(input, output);
        Info = new TransportInfo(TransportKind.InMemory, local, remote);
    }

    /// <summary>Creates two connected transports: bytes written to one are read from the other.</summary>
    public static (InMemoryTransport A, InMemoryTransport B) CreatePair(string nameA = "mem-a", string nameB = "mem-b")
    {
        var options = new PipeOptions(useSynchronizationContext: false);
        var ab = new Pipe(options);
        var ba = new Pipe(options);
        return (new InMemoryTransport(ba.Reader, ab.Writer, nameA, nameB),
                new InMemoryTransport(ab.Reader, ba.Writer, nameB, nameA));
    }

    /// <inheritdoc />
    public IDuplexPipe Pipe => _pipe;

    /// <inheritdoc />
    public TransportInfo Info { get; }

    /// <inheritdoc />
    public ValueTask OpenAsync(CancellationToken ct) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _pipe.Output.CompleteAsync().ConfigureAwait(false);
        await _pipe.Input.CompleteAsync().ConfigureAwait(false);
    }

    private sealed class Duplex(PipeReader input, PipeWriter output) : IDuplexPipe
    {
        public PipeReader Input { get; } = input;
        public PipeWriter Output { get; } = output;
    }
}

/// <summary>
/// In-process listener: clients call <see cref="Connect"/> to obtain a transport whose peer is
/// delivered to <see cref="AcceptAsync"/>. Lets a client and a server/simulator talk inside one process.
/// </summary>
public sealed class InMemoryTransportListener(string name = "in-memory") : ITransportListener
{
    private readonly Channel<ITransport> _pending = Channel.CreateUnbounded<ITransport>();
    private int _clients;

    /// <inheritdoc />
    public string LocalAddress => name;

    /// <summary>Creates a new connection and queues the server side for <see cref="AcceptAsync"/>.</summary>
    public ITransport Connect()
    {
        var id = Interlocked.Increment(ref _clients);
        var (client, server) = InMemoryTransport.CreatePair($"{name}/client-{id}", name);
        if (!_pending.Writer.TryWrite(server))
            throw new TransportException($"In-memory listener '{name}' is closed.");
        return client;
    }

    /// <inheritdoc />
    public ValueTask StartAsync(CancellationToken ct) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask<ITransport> AcceptAsync(CancellationToken ct) => _pending.Reader.ReadAsync(ct);

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _pending.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
