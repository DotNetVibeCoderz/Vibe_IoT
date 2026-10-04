namespace IoTCom.Net;

/// <summary>Delivery guarantee requested for a published message. Protocols map it to their native semantics.</summary>
public enum QualityOfService
{
    /// <summary>Fire and forget.</summary>
    AtMostOnce = 0,
    /// <summary>Acknowledged delivery, duplicates possible.</summary>
    AtLeastOnce = 1,
    /// <summary>Exactly once (only where the protocol supports it).</summary>
    ExactlyOnce = 2,
}

/// <summary>Options applied to a single publish operation.</summary>
public sealed record PublishOptions
{
    /// <summary>Requested delivery guarantee.</summary>
    public QualityOfService QualityOfService { get; init; } = QualityOfService.AtMostOnce;

    /// <summary>Ask the broker/peer to retain the last value for late subscribers.</summary>
    public bool Retain { get; init; }

    /// <summary>Optional content type (MIME), e.g. <c>application/senml+json</c>.</summary>
    public string? ContentType { get; init; }

    /// <summary>Default options instance.</summary>
    public static PublishOptions Default { get; } = new();
}

/// <summary>A message received from a subscription.</summary>
/// <typeparam name="T">Payload type.</typeparam>
/// <param name="Topic">Topic, subject, key or address the message was published to.</param>
/// <param name="Payload">Decoded payload.</param>
/// <param name="Timestamp">Receive timestamp (UTC).</param>
public readonly record struct Message<T>(string Topic, T Payload, DateTimeOffset Timestamp);

/// <summary>Publishes messages to topics.</summary>
public interface IPublisher<in T>
{
    /// <summary>Publishes <paramref name="message"/> to <paramref name="topic"/>.</summary>
    ValueTask PublishAsync(string topic, T message, PublishOptions? options = null, CancellationToken ct = default);
}

/// <summary>Consumes messages from topics.</summary>
public interface ISubscriber<T>
{
    /// <summary>Subscribes to <paramref name="filter"/> and yields messages until <paramref name="ct"/> is cancelled.</summary>
    IAsyncEnumerable<Message<T>> SubscribeAsync(string filter, CancellationToken ct = default);
}
