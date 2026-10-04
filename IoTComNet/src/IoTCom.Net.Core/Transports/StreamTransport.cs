using System.IO.Pipelines;

namespace IoTCom.Net.Transports;

/// <summary>
/// Base class for transports backed by a <see cref="Stream"/> (TCP, serial, TLS...).
/// The stream is adapted to <see cref="IDuplexPipe"/> with <see cref="PipeReader.Create(Stream, StreamPipeReaderOptions?)"/>.
/// </summary>
public abstract class StreamTransport : ITransport
{
    private Stream? _stream;
    private IDuplexPipe? _pipe;
    private int _disposed;

    /// <summary>Minimum buffer size for reads (bytes).</summary>
    protected virtual int MinimumReadSize => 4096;

    /// <inheritdoc />
    public IDuplexPipe Pipe => _pipe ?? throw new InvalidOperationException("Transport is not open. Call OpenAsync first.");

    /// <inheritdoc />
    public abstract TransportInfo Info { get; }

    /// <summary>True once opened and not yet disposed.</summary>
    public bool IsOpen => _pipe is not null && Volatile.Read(ref _disposed) == 0;

    /// <inheritdoc />
    public async ValueTask OpenAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_pipe is not null) return;
        try
        {
            _stream = await OpenStreamAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not IoTComException)
        {
            throw new TransportException($"Failed to open {GetType().Name}: {ex.Message}", ex);
        }
        _pipe = new StreamDuplexPipe(
            PipeReader.Create(_stream, new StreamPipeReaderOptions(bufferSize: MinimumReadSize, minimumReadSize: Math.Min(1024, MinimumReadSize), leaveOpen: true)),
            PipeWriter.Create(_stream, new StreamPipeWriterOptions(leaveOpen: true)));
    }

    /// <summary>Opens and returns the underlying stream.</summary>
    protected abstract ValueTask<Stream> OpenStreamAsync(CancellationToken ct);

    /// <summary>Releases transport specific resources (socket, port) after the stream is closed.</summary>
    protected virtual ValueTask CloseCoreAsync() => ValueTask.CompletedTask;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_pipe is not null)
        {
            try { await _pipe.Output.CompleteAsync().ConfigureAwait(false); } catch { /* best effort */ }
            try { await _pipe.Input.CompleteAsync().ConfigureAwait(false); } catch { /* best effort */ }
        }
        if (_stream is not null) await _stream.DisposeAsync().ConfigureAwait(false);
        await CloseCoreAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private sealed class StreamDuplexPipe(PipeReader input, PipeWriter output) : IDuplexPipe
    {
        public PipeReader Input { get; } = input;
        public PipeWriter Output { get; } = output;
    }
}
