namespace IoTCom.Net;

/// <summary>Lifecycle state of an <see cref="IEndpoint"/>.</summary>
public enum EndpointState
{
    /// <summary>Not connected / not started.</summary>
    Disconnected = 0,
    /// <summary>Opening the transport (client) or binding (server).</summary>
    Connecting = 1,
    /// <summary>Client connected and ready.</summary>
    Connected = 2,
    /// <summary>Server bound and accepting peers.</summary>
    Listening = 3,
    /// <summary>Closing gracefully.</summary>
    Stopping = 4,
    /// <summary>Stopped after an unrecoverable error; see <see cref="StateChangedEventArgs.Error"/>.</summary>
    Faulted = 5,
}

/// <summary>Raised whenever an endpoint changes <see cref="EndpointState"/>.</summary>
public sealed class StateChangedEventArgs(EndpointState previous, EndpointState current, Exception? error = null) : EventArgs
{
    /// <summary>State before the transition.</summary>
    public EndpointState Previous { get; } = previous;
    /// <summary>State after the transition.</summary>
    public EndpointState Current { get; } = current;
    /// <summary>The error that caused a transition to <see cref="EndpointState.Faulted"/>, if any.</summary>
    public Exception? Error { get; } = error;
}

/// <summary>Common surface of every protocol endpoint (client, server, publisher, subscriber).</summary>
public interface IEndpoint : IAsyncDisposable
{
    /// <summary>Protocol name, e.g. <c>"modbus-tcp"</c>, <c>"mqtt"</c>, <c>"nmea0183"</c>.</summary>
    string Protocol { get; }

    /// <summary>Current lifecycle state.</summary>
    EndpointState State { get; }

    /// <summary>Raised on every state transition.</summary>
    event EventHandler<StateChangedEventArgs>? StateChanged;
}

/// <summary>An endpoint that actively connects to a peer (master, controller, tester, client).</summary>
public interface IClientEndpoint : IEndpoint
{
    /// <summary>Opens the transport and performs any protocol handshake.</summary>
    ValueTask ConnectAsync(CancellationToken ct = default);

    /// <summary>Closes the connection gracefully.</summary>
    ValueTask DisconnectAsync(CancellationToken ct = default);
}

/// <summary>An endpoint that accepts peers (slave, device, server, simulator).</summary>
public interface IServerEndpoint : IEndpoint
{
    /// <summary>Binds and starts serving.</summary>
    ValueTask StartAsync(CancellationToken ct = default);

    /// <summary>Stops serving and disconnects all peers.</summary>
    ValueTask StopAsync(CancellationToken ct = default);
}
