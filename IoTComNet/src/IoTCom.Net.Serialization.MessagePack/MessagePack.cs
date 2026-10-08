using MessagePack;

namespace IoTCom.Net.Serialization.MessagePack;

/// <summary>
/// A MessagePack type as an <see cref="IPayloadCodec{T}"/>. Pass options whose resolver knows <typeparamref name="T"/>
/// (for NativeAOT use the source-generated resolver, e.g. <c>MessagePackSerializerOptions.Standard.WithResolver(GeneratedResolver.Instance)</c>).
/// </summary>
/// <typeparam name="T">The value type.</typeparam>
/// <param name="options">Serializer options (resolver, compression).</param>
public sealed class MessagePackCodec<T>(MessagePackSerializerOptions options) : IPayloadCodec<T>
{
    private readonly MessagePackSerializerOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <inheritdoc />
    public string ContentType => "application/msgpack";

    /// <inheritdoc />
    public byte[] Encode(T value) => MessagePackSerializer.Serialize(value, _options);

    /// <inheritdoc />
    public T Decode(ReadOnlySpan<byte> payload)
    {
        try
        {
            return MessagePackSerializer.Deserialize<T>(payload.ToArray(), _options);
        }
        catch (MessagePackSerializationException ex)
        {
            throw new ProtocolException($"Invalid MessagePack for {typeof(T).Name}: {ex.Message}", ex);
        }
    }
}

/// <summary>Schema-less views of MessagePack payloads.</summary>
public static class MessagePackView
{
    /// <summary>The payload as JSON (binary values become base64), or null when it is not valid MessagePack.</summary>
    public static string? ToJson(ReadOnlyMemory<byte> payload)
    {
        try
        {
            return MessagePackSerializer.ConvertToJson(payload);
        }
        catch (Exception ex) when (ex is MessagePackSerializationException or EndOfStreamException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Frame-lane fields: the whole payload with its JSON rendering.</summary>
    public static IReadOnlyList<FrameField> Describe(ReadOnlySpan<byte> data) =>
        ToJson(data.ToArray()) is { } json
            ? [new FrameField("MessagePack", 0, data.Length, FrameFieldKind.Data, json.Length <= 120 ? json : json[..120] + "…")]
            : [new FrameField("Invalid", 0, data.Length, FrameFieldKind.Error, "not MessagePack")];
}
