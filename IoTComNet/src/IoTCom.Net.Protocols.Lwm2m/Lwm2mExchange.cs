using System.Net;
using IoTCom.Net.Protocols.Coap;

namespace IoTCom.Net.Protocols.Lwm2m;

/// <summary>An LwM2M operation answered with an error code.</summary>
public sealed class Lwm2mException : DeviceException
{
    /// <summary>Creates the exception.</summary>
    public Lwm2mException() { }

    /// <summary>Creates the exception with a message.</summary>
    public Lwm2mException(string message) : base(message) { }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public Lwm2mException(string message, Exception? inner) : base(message, inner) { }

    /// <summary>Creates the exception for a response code.</summary>
    public Lwm2mException(string operation, CoapCode code) : base($"{operation} failed: {code}.", code.Value) => Status = code;

    /// <summary>The CoAP response code (4.04 not found, 4.05 method not allowed…).</summary>
    public CoapCode Status { get; }
}

/// <summary>Request/response over the shared CoAP message layer: confirmable request, piggybacked or separate response.</summary>
internal static class Lwm2mExchange
{
    public static CoapMessage Request(CoapCode code, IEnumerable<string> segments, IEnumerable<string>? query = null)
    {
        var m = new CoapMessage { Code = code };
        foreach (var s in segments) m.AddOption(CoapOptionNumber.UriPath, s);
        foreach (var q in query ?? []) m.AddOption(CoapOptionNumber.UriQuery, q);
        return m;
    }

    /// <param name="stack">The message layer.</param>
    /// <param name="request">The request (token assigned when empty).</param>
    /// <param name="remote">Peer.</param>
    /// <param name="timeout">How long to wait for a separate response.</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="routeSeparate">
    /// False when the caller already routes this token (observations): registering it again here would replace the
    /// caller's handler and remove it when the exchange ends.
    /// </param>
    public static async Task<CoapMessage> SendAsync(CoapStack stack, CoapMessage request, EndPoint remote, TimeSpan timeout, CancellationToken ct, bool routeSeparate = true)
    {
        request.MessageId = stack.NextMessageId();
        if (request.Token.IsEmpty) request.Token = CoapStack.NewToken();
        var separate = new TaskCompletionSource<CoapMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = routeSeparate
            ? stack.RegisterToken(remote, request.Token, (m, _) =>
            {
                if (m.Code.Value != 0) separate.TrySetResult(m);
            })
            : null;
        var ack = await stack.SendConfirmableAsync(request, remote, ct).ConfigureAwait(false);
        if (ack.Code.Value != 0) return ack;   // piggybacked response
        try
        {
            return await separate.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new IoTComTimeoutException($"No response from {remote} within {timeout.TotalSeconds:0.#} s ({request}).");
        }
    }

    public static CoapMessage Reply(CoapMessage request, CoapCode code, ushort? format = null, byte[]? payload = null)
    {
        var reply = new CoapMessage
        {
            Type = request.Type == CoapType.Confirmable ? CoapType.Acknowledgement : CoapType.NonConfirmable,
            Code = code,
            Token = request.Token,
        };
        if (format is { } f) reply.ContentFormat = f;
        if (payload is not null) reply.Payload = payload;
        return reply;
    }
}
