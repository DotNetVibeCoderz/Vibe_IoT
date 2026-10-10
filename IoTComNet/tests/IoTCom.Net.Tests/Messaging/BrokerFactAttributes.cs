namespace IoTCom.Net.Tests.Messaging;

/// <summary>Runs only when <c>IOTCOM_NATS_URL</c> points at a NATS server (CI "brokers" job; locally: <c>nats-server</c>).</summary>
public sealed class NatsFactAttribute : FactAttribute
{
    /// <summary>Server URL, or null when not configured.</summary>
    public static string? Url => Environment.GetEnvironmentVariable("IOTCOM_NATS_URL") is { Length: > 0 } u ? u : null;

    public NatsFactAttribute()
    {
        if (Url is null) Skip = "IOTCOM_NATS_URL not set (e.g. nats://localhost:4222); start a nats-server to run the NATS integration tests.";
    }
}

/// <summary>Runs only when <c>IOTCOM_KAFKA_BOOTSTRAP</c> points at a Kafka broker (CI "brokers" job).</summary>
public sealed class KafkaFactAttribute : FactAttribute
{
    /// <summary>Bootstrap servers, or null when not configured.</summary>
    public static string? Bootstrap => Environment.GetEnvironmentVariable("IOTCOM_KAFKA_BOOTSTRAP") is { Length: > 0 } u ? u : null;

    public KafkaFactAttribute()
    {
        if (Bootstrap is null) Skip = "IOTCOM_KAFKA_BOOTSTRAP not set (e.g. localhost:9092); start a Kafka broker to run the Kafka integration tests.";
    }
}
