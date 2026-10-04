using System.Net;
using IoTCom.Net.Transports;

namespace IoTCom.Net;

/// <summary>Implemented by client option builders that accept a transport. Enables shared <c>UseTcp</c>/<c>UseSerial</c> extensions.</summary>
/// <typeparam name="TSelf">The concrete builder type (fluent return).</typeparam>
public interface ITransportBuilder<out TSelf> where TSelf : ITransportBuilder<TSelf>
{
    /// <summary>Sets the factory used to create the transport (called again on every reconnect).</summary>
    TSelf UseTransport(TransportFactory factory);
}

/// <summary>Implemented by server option builders that accept a listener.</summary>
/// <typeparam name="TSelf">The concrete builder type.</typeparam>
public interface IListenerBuilder<out TSelf> where TSelf : IListenerBuilder<TSelf>
{
    /// <summary>Sets the listener factory.</summary>
    TSelf UseListener(TransportListenerFactory factory);

    /// <summary>Serves exactly one already-connected transport (e.g. a serial line for RTU).</summary>
    TSelf UseTransport(TransportFactory factory);
}

/// <summary>Transport extensions available to every endpoint builder.</summary>
public static class TransportBuilderExtensions
{
    /// <summary>Connects over TCP.</summary>
    public static T UseTcp<T>(this T builder, string host, int port, TimeSpan? connectTimeout = null) where T : ITransportBuilder<T>
        => builder.UseTransport(() => new TcpClientTransport(host, port, connectTimeout));

    /// <summary>Uses a pre-created in-memory transport (single connection, no reconnect).</summary>
    public static T UseInMemory<T>(this T builder, ITransport transport) where T : ITransportBuilder<T>
        => builder.UseTransport(() => transport);

    /// <summary>Connects to an in-process <see cref="InMemoryTransportListener"/> (reconnect supported).</summary>
    public static T UseInMemory<T>(this T builder, InMemoryTransportListener listener) where T : ITransportBuilder<T>
        => builder.UseTransport(listener.Connect);

    /// <summary>Listens on TCP.</summary>
    public static T UseTcp<T>(this T builder, IPAddress address, int port) where T : IListenerBuilder<T>
        => builder.UseListener(() => new TcpTransportListener(address, port));

    /// <summary>Listens on an in-process listener (clients connect with <c>UseInMemory(listener)</c>).</summary>
    public static T ListenInMemory<T>(this T builder, InMemoryTransportListener listener) where T : IListenerBuilder<T>
        => builder.UseListener(() => listener);
}
