using System.IO.Pipelines;

namespace IoTCom.Net;

/// <summary>Kind of physical or logical link underneath a transport.</summary>
public enum TransportKind
{
    /// <summary>Unknown / custom.</summary>
    Other = 0,
    /// <summary>TCP stream.</summary>
    Tcp,
    /// <summary>UDP datagrams.</summary>
    Udp,
    /// <summary>Serial port (RS-232/422/485, USB CDC).</summary>
    Serial,
    /// <summary>In-process memory pipe (tests, simulators).</summary>
    InMemory,
    /// <summary>TLS over TCP.</summary>
    Tls,
    /// <summary>WebSocket.</summary>
    WebSocket,
    /// <summary>CAN / CAN FD bus.</summary>
    Can,
    /// <summary>USB bulk/interrupt endpoints.</summary>
    Usb,
    /// <summary>Bluetooth Low Energy.</summary>
    Ble,
}

/// <summary>Describes a transport for diagnostics, logging and the traffic tap.</summary>
/// <param name="Kind">Link kind.</param>
/// <param name="LocalAddress">Local address/port/device, if known.</param>
/// <param name="RemoteAddress">Remote address/port/device, if known.</param>
public sealed record TransportInfo(TransportKind Kind, string? LocalAddress, string? RemoteAddress)
{
    /// <inheritdoc />
    public override string ToString() => $"{Kind}:{LocalAddress ?? "?"}->{RemoteAddress ?? "?"}";
}

/// <summary>
/// A byte-stream transport exposed as a duplex <see cref="System.IO.Pipelines"/> pipe.
/// Protocol logic never touches sockets or ports directly: it reads from <see cref="Pipe"/>.Input
/// and writes to <see cref="Pipe"/>.Output (sans-I/O, see the architecture docs).
/// </summary>
public interface ITransport : IAsyncDisposable
{
    /// <summary>The duplex pipe. Only valid after <see cref="OpenAsync"/> completed.</summary>
    IDuplexPipe Pipe { get; }

    /// <summary>Transport description.</summary>
    TransportInfo Info { get; }

    /// <summary>Opens the underlying link (connect, open port...).</summary>
    ValueTask OpenAsync(CancellationToken ct);
}

/// <summary>Accepts incoming <see cref="ITransport"/> connections (e.g. a TCP listener).</summary>
public interface ITransportListener : IAsyncDisposable
{
    /// <summary>Local endpoint description (available after <see cref="StartAsync"/>).</summary>
    string LocalAddress { get; }

    /// <summary>Starts listening.</summary>
    ValueTask StartAsync(CancellationToken ct);

    /// <summary>Waits for the next peer. The returned transport is already open.</summary>
    ValueTask<ITransport> AcceptAsync(CancellationToken ct);
}

/// <summary>Factory delegate used by endpoint builders to create a fresh transport (also on reconnect).</summary>
public delegate ITransport TransportFactory();

/// <summary>Factory delegate used by server builders to create a listener.</summary>
public delegate ITransportListener TransportListenerFactory();
