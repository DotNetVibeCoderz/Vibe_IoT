using System.Collections.Concurrent;

namespace IoTCom.Net.Transport.Can;

/// <summary>
/// An in-process CAN network. Every <see cref="VirtualCanBus"/> attached to the same network sees the frames the
/// others send, synchronously and in order — ideal for tests, simulators, notebooks and the Gallery.
/// </summary>
public sealed class VirtualCanNetwork
{
    private static readonly ConcurrentDictionary<string, VirtualCanNetwork> Named = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private VirtualCanBus[] _nodes = [];

    /// <summary>Creates a private network (not reachable by name).</summary>
    public VirtualCanNetwork(string name = "private") => Name = name;

    /// <summary>Network name.</summary>
    public string Name { get; }

    /// <summary>Number of attached open nodes.</summary>
    public int NodeCount => Volatile.Read(ref _nodes).Length;

    /// <summary>Returns the process-wide network with this name (created on first use).</summary>
    public static VirtualCanNetwork Get(string name) => Named.GetOrAdd(name, n => new VirtualCanNetwork(n));

    /// <summary>Creates a node on this network.</summary>
    public VirtualCanBus CreateNode(Action<CanBusOptions>? configure = null)
    {
        var options = new CanBusOptions();
        configure?.Invoke(options);
        return new VirtualCanBus(this, options);
    }

    internal void Attach(VirtualCanBus node)
    {
        lock (_gate) _nodes = [.. _nodes, node];
    }

    internal void Detach(VirtualCanBus node)
    {
        lock (_gate) _nodes = Array.FindAll(_nodes, n => !ReferenceEquals(n, node));
    }

    internal void Broadcast(VirtualCanBus sender, CanFrame frame)
    {
        var stamped = frame.WithTimestamp(DateTimeOffset.UtcNow);
        foreach (var node in Volatile.Read(ref _nodes))
            if (!ReferenceEquals(node, sender) && (!stamped.IsFd || node.SupportsFd)) node.Receive(stamped);
    }
}

/// <summary>A node on a <see cref="VirtualCanNetwork"/>. Supports classic CAN and CAN FD.</summary>
public sealed class VirtualCanBus : CanBusBase
{
    private readonly VirtualCanNetwork _network;

    /// <summary>Creates a node on the named process-wide network (same as <c>CanBus.Create("virtual:name")</c>).</summary>
    public VirtualCanBus(string network, CanBusOptions? options = null)
        : this(VirtualCanNetwork.Get(network), options ?? new CanBusOptions()) { }

    /// <summary>Creates a node on <paramref name="network"/>.</summary>
    public VirtualCanBus(VirtualCanNetwork network, CanBusOptions options) : base("can", $"virtual:{network.Name}", options)
        => _network = network;

    /// <summary>The network.</summary>
    public VirtualCanNetwork Network => _network;

    internal void Receive(in CanFrame frame)
    {
        if (State == EndpointState.Connected) OnFrameReceived(frame);
    }

    /// <inheritdoc />
    protected override ValueTask OpenCoreAsync(CancellationToken ct)
    {
        _network.Attach(this);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override ValueTask CloseCoreAsync()
    {
        _network.Detach(this);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override ValueTask SendCoreAsync(CanFrame frame, CancellationToken ct)
    {
        _network.Broadcast(this, frame);
        return ValueTask.CompletedTask;
    }
}
