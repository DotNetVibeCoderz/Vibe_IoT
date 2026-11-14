using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IoTCom.Net.Protocols.Ocpp;

/// <summary>OCPP-J protocol version, negotiated as the WebSocket subprotocol.</summary>
public enum OcppVersion
{
    /// <summary>OCPP 1.6 JSON (subprotocol <c>ocpp1.6</c>).</summary>
    Ocpp16,
    /// <summary>OCPP 2.0.1 (subprotocol <c>ocpp2.0.1</c>).</summary>
    Ocpp201,
}

/// <summary>Helpers for <see cref="OcppVersion"/>.</summary>
public static class OcppVersions
{
    /// <summary>The WebSocket subprotocol of a version.</summary>
    public static string SubProtocol(this OcppVersion version) => version == OcppVersion.Ocpp16 ? "ocpp1.6" : "ocpp2.0.1";

    /// <summary>Display name, e.g. <c>OCPP 1.6J</c>.</summary>
    public static string DisplayName(this OcppVersion version) => version == OcppVersion.Ocpp16 ? "OCPP 1.6J" : "OCPP 2.0.1";

    /// <summary>Parses a subprotocol; null when it is not one this library speaks.</summary>
    public static OcppVersion? FromSubProtocol(string? subProtocol) => subProtocol?.Trim().ToLowerInvariant() switch
    {
        "ocpp1.6" => OcppVersion.Ocpp16,
        "ocpp2.0.1" => OcppVersion.Ocpp201,
        _ => null,
    };
}

/// <summary>OCPP-J message type id (first element of the JSON array).</summary>
public enum OcppMessageType
{
    /// <summary>[2, id, action, payload]</summary>
    Call = 2,
    /// <summary>[3, id, payload]</summary>
    CallResult = 3,
    /// <summary>[4, id, errorCode, errorDescription, errorDetails]</summary>
    CallError = 4,
}

/// <summary>RPC framework error codes (OCPP-J §4.2.3 for 1.6, part 4 §4.3 for 2.0.1).</summary>
public static class OcppErrorCode
{
    /// <summary>Requested action is not known by the receiver.</summary>
    public const string NotImplemented = "NotImplemented";
    /// <summary>Requested action is recognised but not supported.</summary>
    public const string NotSupported = "NotSupported";
    /// <summary>An internal error occurred while processing the action.</summary>
    public const string InternalError = "InternalError";
    /// <summary>Payload is syntactically incorrect (2.0.1 name).</summary>
    public const string FormatViolation = "FormatViolation";
    /// <summary>Payload is syntactically incorrect (1.6 name).</summary>
    public const string FormationViolation = "FormationViolation";
    /// <summary>Payload is syntactically correct but a field violates its constraints.</summary>
    public const string PropertyConstraintViolation = "PropertyConstraintViolation";
    /// <summary>A field violates occurrence constraints (2.0.1 spelling).</summary>
    public const string OccurrenceConstraintViolation = "OccurrenceConstraintViolation";
    /// <summary>A field violates occurrence constraints (1.6 spelling, sic).</summary>
    public const string OccurenceConstraintViolation = "OccurenceConstraintViolation";
    /// <summary>A field violates data type constraints.</summary>
    public const string TypeConstraintViolation = "TypeConstraintViolation";
    /// <summary>Protocol error (wrong message for the state).</summary>
    public const string ProtocolError = "ProtocolError";
    /// <summary>Security issue (wrong credentials for the action).</summary>
    public const string SecurityError = "SecurityError";
    /// <summary>Any other error.</summary>
    public const string GenericError = "GenericError";
    /// <summary>Message type id unknown (2.0.1).</summary>
    public const string MessageTypeNotSupported = "MessageTypeNotSupported";
    /// <summary>Content is not a valid RPC request (2.0.1).</summary>
    public const string RpcFrameworkError = "RpcFrameworkError";

    /// <summary>The "format" error code for a version.</summary>
    public static string Format(OcppVersion version) => version == OcppVersion.Ocpp16 ? FormationViolation : FormatViolation;

    /// <summary>The "occurrence" error code for a version.</summary>
    public static string Occurrence(OcppVersion version) => version == OcppVersion.Ocpp16 ? OccurenceConstraintViolation : OccurrenceConstraintViolation;

    /// <summary>The error code used for a frame that is not an RPC message.</summary>
    public static string Framework(OcppVersion version) => version == OcppVersion.Ocpp16 ? FormationViolation : RpcFrameworkError;
}

/// <summary>A CALLERROR received from the peer, or raised by a handler to answer with one.</summary>
public sealed class OcppCallException : ProtocolException
{
    /// <summary>Creates the exception.</summary>
    public OcppCallException(string errorCode, string description, JsonObject? details = null)
        : base($"{errorCode}: {description}") => (ErrorCode, Description, Details) = (errorCode, description, details);

    /// <summary>Creates an exception with a generic error.</summary>
    public OcppCallException() : this(OcppErrorCode.GenericError, "") { }

    /// <summary>Creates an exception with a generic error.</summary>
    public OcppCallException(string message) : this(OcppErrorCode.GenericError, message) { }

    /// <summary>Creates an exception with a generic error.</summary>
    public OcppCallException(string message, Exception? inner) : base(message, inner) => (ErrorCode, Description) = (OcppErrorCode.GenericError, message);

    /// <summary>The OCPP error code.</summary>
    public string ErrorCode { get; }

    /// <summary>The error description.</summary>
    public string Description { get; } = "";

    /// <summary>Optional details object.</summary>
    public JsonObject? Details { get; }
}

/// <summary>
/// One OCPP-J RPC message (sans-I/O): parsing and formatting of the JSON array that travels in a WebSocket text frame.
/// </summary>
public sealed class OcppFrame
{
    /// <summary>Longest message id the specification allows (36 characters).</summary>
    public const int MaxMessageIdLength = 36;

    private OcppFrame(OcppMessageType type, string messageId) => (Type, MessageId) = (type, messageId);

    /// <summary>Message type.</summary>
    public OcppMessageType Type { get; }

    /// <summary>Unique id that pairs a CALL with its CALLRESULT or CALLERROR.</summary>
    public string MessageId { get; }

    /// <summary>Action name of a CALL, e.g. <c>BootNotification</c>.</summary>
    public string? Action { get; private init; }

    /// <summary>Payload of a CALL or CALLRESULT (a JSON object).</summary>
    public JsonObject? Payload { get; private init; }

    /// <summary>Error code of a CALLERROR.</summary>
    public string? ErrorCode { get; private init; }

    /// <summary>Error description of a CALLERROR.</summary>
    public string? ErrorDescription { get; private init; }

    /// <summary>Error details of a CALLERROR.</summary>
    public JsonObject? ErrorDetails { get; private init; }

    /// <summary>Creates a CALL.</summary>
    public static OcppFrame Call(string messageId, string action, JsonObject? payload) => new(OcppMessageType.Call, CheckId(messageId)) { Action = action, Payload = payload ?? [] };

    /// <summary>Creates a CALLRESULT.</summary>
    public static OcppFrame Result(string messageId, JsonObject? payload) => new(OcppMessageType.CallResult, CheckId(messageId)) { Payload = payload ?? [] };

    /// <summary>Creates a CALLERROR.</summary>
    public static OcppFrame Error(string messageId, string errorCode, string? description = null, JsonObject? details = null) =>
        new(OcppMessageType.CallError, messageId) { ErrorCode = errorCode, ErrorDescription = description ?? "", ErrorDetails = details ?? [] };

    private static string CheckId(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > MaxMessageIdLength)
            throw new ArgumentException($"OCPP message ids are 1–{MaxMessageIdLength} characters.", nameof(id));
        return id;
    }

    /// <summary>Formats the frame as compact JSON.</summary>
    public string ToJson()
    {
        var array = new JsonArray((JsonNode?)JsonValue.Create((int)Type), (JsonNode?)JsonValue.Create(MessageId));
        switch (Type)
        {
            case OcppMessageType.Call:
                array.Add((JsonNode?)JsonValue.Create(Action));
                array.Add((JsonNode?)(Payload?.DeepClone() ?? new JsonObject()));
                break;
            case OcppMessageType.CallResult:
                array.Add((JsonNode?)(Payload?.DeepClone() ?? new JsonObject()));
                break;
            default:
                array.Add((JsonNode?)JsonValue.Create(ErrorCode));
                array.Add((JsonNode?)JsonValue.Create(ErrorDescription ?? ""));
                array.Add((JsonNode?)(ErrorDetails?.DeepClone() ?? new JsonObject()));
                break;
        }

        return Format(array);
    }

    private static readonly JsonSerializerOptions WireOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Compact JSON text as it goes on the wire (no HTML escaping, so timestamps keep their '+').</summary>
    public static string Format(JsonNode node) => node.ToJsonString(WireOptions);

    /// <summary>UTF-8 bytes of <see cref="ToJson"/>.</summary>
    public byte[] ToUtf8() => Encoding.UTF8.GetBytes(ToJson());

    /// <summary>Parses a frame; throws <see cref="OcppFrameException"/> with the error code to answer with.</summary>
    public static OcppFrame Parse(string json, OcppVersion version = OcppVersion.Ocpp201)
    {
        if (!TryParse(json, version, out var frame, out var error)) throw error!;
        return frame!;
    }

    /// <summary>Parses a frame without throwing.</summary>
    public static bool TryParse(string json, OcppVersion version, [NotNullWhen(true)] out OcppFrame? frame, [NotNullWhen(false)] out OcppFrameException? error)
    {
        frame = null;
        error = null;
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            error = new OcppFrameException(OcppErrorCode.Framework(version), null, $"not JSON: {ex.Message}");
            return false;
        }

        if (node is not JsonArray a || a.Count < 3)
        {
            error = new OcppFrameException(OcppErrorCode.Framework(version), null, "an OCPP message is a JSON array of at least three elements");
            return false;
        }

        if (a[0] is not JsonValue tv || !tv.TryGetValue<int>(out var type))
        {
            error = new OcppFrameException(OcppErrorCode.Framework(version), null, "the message type id must be a number");
            return false;
        }

        if (a[1] is not JsonValue iv || !iv.TryGetValue<string>(out var id) || id.Length == 0 || id.Length > MaxMessageIdLength)
        {
            error = new OcppFrameException(OcppErrorCode.Framework(version), null, $"the message id must be a string of 1–{MaxMessageIdLength} characters");
            return false;
        }

        switch (type)
        {
            case 2:
                if (a.Count != 4 || a[2] is not JsonValue av || !av.TryGetValue<string>(out var action) || action.Length == 0)
                {
                    error = new OcppFrameException(OcppErrorCode.Framework(version), id, "a CALL is [2, id, action, payload]");
                    return false;
                }

                if (a[3] is not JsonObject payload)
                {
                    error = new OcppFrameException(OcppErrorCode.Format(version), id, "the CALL payload must be a JSON object");
                    return false;
                }

                frame = new OcppFrame(OcppMessageType.Call, id) { Action = action, Payload = (JsonObject)payload.DeepClone() };
                return true;
            case 3:
                if (a.Count != 3 || a[2] is not JsonObject result)
                {
                    error = new OcppFrameException(OcppErrorCode.Format(version), id, "a CALLRESULT is [3, id, payload object]");
                    return false;
                }

                frame = new OcppFrame(OcppMessageType.CallResult, id) { Payload = (JsonObject)result.DeepClone() };
                return true;
            case 4:
                if (a.Count is < 4 or > 5 || a[2] is not JsonValue ev || !ev.TryGetValue<string>(out var code))
                {
                    error = new OcppFrameException(OcppErrorCode.Format(version), id, "a CALLERROR is [4, id, errorCode, errorDescription, errorDetails]");
                    return false;
                }

                var description = a[3] is JsonValue dv && dv.TryGetValue<string>(out var d) ? d : "";
                var details = a.Count == 5 && a[4] is JsonObject o ? (JsonObject)o.DeepClone() : [];
                frame = new OcppFrame(OcppMessageType.CallError, id) { ErrorCode = code, ErrorDescription = description, ErrorDetails = details };
                return true;
            default:
                error = new OcppFrameException(version == OcppVersion.Ocpp16 ? OcppErrorCode.FormationViolation : OcppErrorCode.MessageTypeNotSupported, id, $"unknown message type {type}");
                return false;
        }
    }

    /// <summary>One-line description for logs and the traffic tap.</summary>
    public string Describe() => Type switch
    {
        OcppMessageType.Call => $"CALL {Action} #{MessageId}",
        OcppMessageType.CallResult => $"CALLRESULT #{MessageId}",
        _ => $"CALLERROR {ErrorCode} #{MessageId}{(string.IsNullOrEmpty(ErrorDescription) ? "" : " " + ErrorDescription)}",
    };

    /// <summary>Describes the fields of a raw message (frame lane, VS Code, CLI); offsets are UTF-8 byte offsets.</summary>
    public static IReadOnlyList<FrameField> DescribeFields(string json)
    {
        var utf8 = Encoding.UTF8.GetBytes(json);
        if (!TryParse(json, OcppVersion.Ocpp201, out var f, out var error))
            return [new FrameField("Invalid", 0, utf8.Length, FrameFieldKind.Error, error!.Message)];

        var spans = new List<(int Start, int Length)>();
        var reader = new Utf8JsonReader(utf8);
        reader.Read(); // [
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            var start = (int)reader.TokenStartIndex;
            reader.Skip();
            spans.Add((start, (int)reader.BytesConsumed - start));
        }

        FrameField Field(int i, string name, FrameFieldKind kind, string value) => new(name, spans[i].Start, spans[i].Length, kind, value);
        var fields = new List<FrameField>
        {
            new("[", 0, 1, FrameFieldKind.Delimiter),
            Field(0, "MessageTypeId", FrameFieldKind.Header, $"{(int)f!.Type} {f.Type}"),
            Field(1, "MessageId", FrameFieldKind.Address, f.MessageId),
        };
        switch (f.Type)
        {
            case OcppMessageType.Call:
                fields.Add(Field(2, "Action", FrameFieldKind.Function, f.Action!));
                fields.Add(Field(3, "Payload", FrameFieldKind.Data, Format(f.Payload!)));
                break;
            case OcppMessageType.CallResult:
                fields.Add(Field(2, "Payload", FrameFieldKind.Data, Format(f.Payload!)));
                break;
            default:
                fields.Add(Field(2, "ErrorCode", FrameFieldKind.Error, f.ErrorCode!));
                fields.Add(Field(3, "ErrorDescription", FrameFieldKind.Data, f.ErrorDescription ?? ""));
                if (spans.Count > 4) fields.Add(Field(4, "ErrorDetails", FrameFieldKind.Data, f.ErrorDetails is null ? "{}" : Format(f.ErrorDetails)));
                break;
        }

        fields.Add(new FrameField("]", utf8.Length - 1, 1, FrameFieldKind.Delimiter));
        return fields;
    }
}

/// <summary>A message that is not a valid OCPP-J frame, with the error code the receiver should answer with.</summary>
public sealed class OcppFrameException : ProtocolException
{
    /// <summary>Creates the exception.</summary>
    public OcppFrameException(string errorCode, string? messageId, string message) : base(message) => (ErrorCode, MessageId) = (errorCode, messageId);

    /// <summary>Creates the exception.</summary>
    public OcppFrameException() : this(OcppErrorCode.GenericError, null, "invalid OCPP frame") { }

    /// <summary>Creates the exception.</summary>
    public OcppFrameException(string message) : this(OcppErrorCode.GenericError, null, message) { }

    /// <summary>Creates the exception.</summary>
    public OcppFrameException(string message, Exception? inner) : base(message, inner) => ErrorCode = OcppErrorCode.GenericError;

    /// <summary>Error code for the CALLERROR answer.</summary>
    public string ErrorCode { get; }

    /// <summary>Message id when it could be read (an answer is only possible then).</summary>
    public string? MessageId { get; }
}
