namespace IoTCom.Net;

/// <summary>
/// Turns values into message payloads and back (SenML, Protobuf, MessagePack, JSON …), so publishers and subscribers
/// can stay agnostic of the wire format.
/// </summary>
/// <typeparam name="T">The value type.</typeparam>
public interface IPayloadCodec<T>
{
    /// <summary>MIME type of the payload, e.g. <c>application/x-protobuf</c>.</summary>
    string ContentType { get; }

    /// <summary>Encodes a value.</summary>
    byte[] Encode(T value);

    /// <summary>Decodes a payload; throws <see cref="ProtocolException"/> when it is malformed.</summary>
    T Decode(ReadOnlySpan<byte> payload);
}
