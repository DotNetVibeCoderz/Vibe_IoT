namespace IoTCom.Net;

/// <summary>Direction of a captured frame relative to the local endpoint.</summary>
public enum FrameDirection
{
    /// <summary>Received from the peer.</summary>
    Inbound = 0,
    /// <summary>Sent to the peer.</summary>
    Outbound = 1,
}

/// <summary>A raw protocol frame captured by the traffic tap.</summary>
/// <remarks>
/// <see cref="Data"/> is only guaranteed to be valid during the <see cref="ITrafficTap.OnFrame"/> call.
/// Taps that keep frames must copy the bytes (see <see cref="ToOwned"/>).
/// </remarks>
public readonly struct TrafficFrame
{
    /// <summary>Creates a frame.</summary>
    public TrafficFrame(string protocol, FrameDirection direction, ReadOnlyMemory<byte> data, DateTimeOffset timestamp, string? endpoint = null, string? summary = null)
    {
        Protocol = protocol;
        Direction = direction;
        Data = data;
        Timestamp = timestamp;
        Endpoint = endpoint;
        Summary = summary;
    }

    /// <summary>Protocol name, e.g. <c>modbus-tcp</c>.</summary>
    public string Protocol { get; }
    /// <summary>Inbound or outbound.</summary>
    public FrameDirection Direction { get; }
    /// <summary>Raw frame bytes (on-the-wire ADU).</summary>
    public ReadOnlyMemory<byte> Data { get; }
    /// <summary>Capture time (UTC).</summary>
    public DateTimeOffset Timestamp { get; }
    /// <summary>Endpoint name or peer address.</summary>
    public string? Endpoint { get; }
    /// <summary>Human readable decode summary, e.g. <c>"Read Holding Registers unit=1 addr=0 count=10"</c>.</summary>
    public string? Summary { get; }

    /// <summary>Returns a copy whose <see cref="Data"/> owns its bytes.</summary>
    public TrafficFrame ToOwned() => new(Protocol, Direction, Data.ToArray(), Timestamp, Endpoint, Summary);
}

/// <summary>
/// Receives every raw frame sent or received by endpoints it is attached to.
/// Used by the Gallery Protocol Inspector, the <c>iotcom sniff</c> CLI command and pcap export.
/// Implementations must be fast and thread-safe; they are called on the I/O path.
/// </summary>
public interface ITrafficTap
{
    /// <summary>Called for each frame.</summary>
    void OnFrame(in TrafficFrame frame);
}
