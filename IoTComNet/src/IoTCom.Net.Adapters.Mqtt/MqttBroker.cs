using Microsoft.Extensions.Logging;
using MQTTnet.Server;

namespace IoTCom.Net.Adapters.Mqtt;

/// <summary>
/// Embeddable MQTT broker (MQTTnet server) for gateways, tests, notebooks and the Gallery.
/// For production fleets use a dedicated broker (Mosquitto, EMQX, HiveMQ); this one is great at the edge.
/// </summary>
public sealed class MqttBroker : EndpointBase, IServerEndpoint
{
    private readonly MqttServer _server;
    private int _clients;

    private MqttBroker(int port, ILogger? logger) : base("mqtt-broker", logger)
    {
        Port = port;
        var factory = new MqttServerFactory();
        var options = factory.CreateServerOptionsBuilder()
            .WithDefaultEndpoint()
            .WithDefaultEndpointPort(port)
            .Build();
        _server = factory.CreateMqttServer(options);
        _server.ClientConnectedAsync += e =>
        {
            Interlocked.Increment(ref _clients);
            ClientConnected?.Invoke(e.ClientId);
            return Task.CompletedTask;
        };
        _server.ClientDisconnectedAsync += e =>
        {
            Interlocked.Decrement(ref _clients);
            ClientDisconnected?.Invoke(e.ClientId);
            return Task.CompletedTask;
        };
        _server.InterceptingPublishAsync += e =>
        {
            MessagesRouted++;
            return Task.CompletedTask;
        };
    }

    /// <summary>Creates a broker listening on <paramref name="port"/> (all interfaces).</summary>
    public static MqttBroker Create(int port = 1883, ILogger? logger = null) => new(port, logger);

    /// <summary>TCP port.</summary>
    public int Port { get; }

    /// <summary>Connected clients.</summary>
    public int ClientCount => Volatile.Read(ref _clients);

    /// <summary>Messages routed since start.</summary>
    public long MessagesRouted { get; private set; }

    /// <summary>Raised when a client connects (client id).</summary>
    public event Action<string>? ClientConnected;

    /// <summary>Raised when a client disconnects (client id).</summary>
    public event Action<string>? ClientDisconnected;

    /// <inheritdoc />
    public async ValueTask StartAsync(CancellationToken ct = default)
    {
        SetState(EndpointState.Connecting);
        try
        {
            await _server.StartAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SetState(EndpointState.Faulted, ex);
            throw new TransportException($"MQTT broker cannot listen on port {Port}: {ex.Message}", ex);
        }
        SetState(EndpointState.Listening);
        Logger.LogInformation("MQTT broker listening on port {Port}", Port);
    }

    /// <inheritdoc />
    public async ValueTask StopAsync(CancellationToken ct = default)
    {
        if (!_server.IsStarted) return;
        SetState(EndpointState.Stopping);
        await _server.StopAsync().ConfigureAwait(false);
        SetState(EndpointState.Disconnected);
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore()
    {
        await StopAsync().ConfigureAwait(false);
        _server.Dispose();
    }
}
