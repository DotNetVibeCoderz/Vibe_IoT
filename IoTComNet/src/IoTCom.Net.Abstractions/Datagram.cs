using System.Net;

namespace IoTCom.Net;

/// <summary>A received datagram and its sender.</summary>
/// <param name="Data">Payload (owned by the receiver).</param>
/// <param name="Remote">Sender address.</param>
public readonly record struct Datagram(ReadOnlyMemory<byte> Data, EndPoint Remote);

/// <summary>
/// A message-oriented transport (UDP, in-memory network). Used by datagram protocols such as CoAP, where each send
/// is one message and peers are addressed per message rather than per connection.
/// </summary>
public interface IDatagramTransport : IAsyncDisposable
{
    /// <summary>Local address (after binding).</summary>
    EndPoint LocalEndPoint { get; }

    /// <summary>Sends one datagram to <paramref name="remote"/>.</summary>
    ValueTask SendAsync(ReadOnlyMemory<byte> data, EndPoint remote, CancellationToken ct = default);

    /// <summary>Waits for the next datagram.</summary>
    ValueTask<Datagram> ReceiveAsync(CancellationToken ct = default);
}

/// <summary>Factory used by endpoint builders to create (bind) a datagram transport.</summary>
public delegate IDatagramTransport DatagramTransportFactory();

/// <summary>Implemented by option builders of datagram endpoints; enables the shared <c>UseUdp</c>/<c>UseInMemory</c> extensions.</summary>
/// <typeparam name="TSelf">The concrete builder type.</typeparam>
public interface IDatagramBuilder<out TSelf> where TSelf : IDatagramBuilder<TSelf>
{
    /// <summary>Sets the factory that binds the datagram transport.</summary>
    TSelf UseDatagramTransport(DatagramTransportFactory factory);
}
