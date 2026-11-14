namespace IoTCom.Net.Protocols.Ocpp;

/// <summary>The typed messages this library knows, by version and action (for decoders, tools and validation).</summary>
public static class OcppCatalog
{
    private static readonly Dictionary<string, (Type Request, Type Response)> V16Actions = new(StringComparer.Ordinal)
    {
        ["Authorize"] = (typeof(V16.AuthorizeRequest), typeof(V16.AuthorizeResponse)),
        ["BootNotification"] = (typeof(V16.BootNotificationRequest), typeof(V16.BootNotificationResponse)),
        ["Heartbeat"] = (typeof(V16.HeartbeatRequest), typeof(V16.HeartbeatResponse)),
        ["MeterValues"] = (typeof(V16.MeterValuesRequest), typeof(V16.MeterValuesResponse)),
        ["StartTransaction"] = (typeof(V16.StartTransactionRequest), typeof(V16.StartTransactionResponse)),
        ["StatusNotification"] = (typeof(V16.StatusNotificationRequest), typeof(V16.StatusNotificationResponse)),
        ["StopTransaction"] = (typeof(V16.StopTransactionRequest), typeof(V16.StopTransactionResponse)),
        ["DataTransfer"] = (typeof(V16.DataTransferRequest), typeof(V16.DataTransferResponse)),
        ["ChangeAvailability"] = (typeof(V16.ChangeAvailabilityRequest), typeof(V16.ChangeAvailabilityResponse)),
        ["ChangeConfiguration"] = (typeof(V16.ChangeConfigurationRequest), typeof(V16.ChangeConfigurationResponse)),
        ["ClearCache"] = (typeof(V16.ClearCacheRequest), typeof(V16.ClearCacheResponse)),
        ["GetConfiguration"] = (typeof(V16.GetConfigurationRequest), typeof(V16.GetConfigurationResponse)),
        ["RemoteStartTransaction"] = (typeof(V16.RemoteStartTransactionRequest), typeof(V16.RemoteStartTransactionResponse)),
        ["RemoteStopTransaction"] = (typeof(V16.RemoteStopTransactionRequest), typeof(V16.RemoteStopTransactionResponse)),
        ["Reset"] = (typeof(V16.ResetRequest), typeof(V16.ResetResponse)),
        ["UnlockConnector"] = (typeof(V16.UnlockConnectorRequest), typeof(V16.UnlockConnectorResponse)),
        ["TriggerMessage"] = (typeof(V16.TriggerMessageRequest), typeof(V16.TriggerMessageResponse)),
        ["SetChargingProfile"] = (typeof(V16.SetChargingProfileRequest), typeof(V16.SetChargingProfileResponse)),
        ["ClearChargingProfile"] = (typeof(V16.ClearChargingProfileRequest), typeof(V16.ClearChargingProfileResponse)),
    };

    private static readonly Dictionary<string, (Type Request, Type Response)> V201Actions = new(StringComparer.Ordinal)
    {
        ["BootNotification"] = (typeof(V201.BootNotificationRequest), typeof(V201.BootNotificationResponse)),
        ["Heartbeat"] = (typeof(V201.HeartbeatRequest), typeof(V201.HeartbeatResponse)),
        ["StatusNotification"] = (typeof(V201.StatusNotificationRequest), typeof(V201.StatusNotificationResponse)),
        ["Authorize"] = (typeof(V201.AuthorizeRequest), typeof(V201.AuthorizeResponse)),
        ["TransactionEvent"] = (typeof(V201.TransactionEventRequest), typeof(V201.TransactionEventResponse)),
        ["MeterValues"] = (typeof(V201.MeterValuesRequest), typeof(V201.MeterValuesResponse)),
        ["DataTransfer"] = (typeof(V201.DataTransferRequest), typeof(V201.DataTransferResponse)),
        ["RequestStartTransaction"] = (typeof(V201.RequestStartTransactionRequest), typeof(V201.RequestStartTransactionResponse)),
        ["RequestStopTransaction"] = (typeof(V201.RequestStopTransactionRequest), typeof(V201.RequestStopTransactionResponse)),
        ["Reset"] = (typeof(V201.ResetRequest), typeof(V201.ResetResponse)),
        ["ChangeAvailability"] = (typeof(V201.ChangeAvailabilityRequest), typeof(V201.ChangeAvailabilityResponse)),
        ["GetVariables"] = (typeof(V201.GetVariablesRequest), typeof(V201.GetVariablesResponse)),
        ["SetVariables"] = (typeof(V201.SetVariablesRequest), typeof(V201.SetVariablesResponse)),
        ["UnlockConnector"] = (typeof(V201.UnlockConnectorRequest), typeof(V201.UnlockConnectorResponse)),
        ["TriggerMessage"] = (typeof(V201.TriggerMessageRequest), typeof(V201.TriggerMessageResponse)),
        ["SetChargingProfile"] = (typeof(V201.SetChargingProfileRequest), typeof(V201.SetChargingProfileResponse)),
        ["ClearCache"] = (typeof(V201.ClearCacheRequest), typeof(V201.ClearCacheResponse)),
        ["ClearChargingProfile"] = (typeof(V201.ClearChargingProfileRequest), typeof(V201.ClearChargingProfileResponse)),
    };

    /// <summary>Actions with typed classes for a version.</summary>
    public static IReadOnlyCollection<string> Actions(OcppVersion version) => (version == OcppVersion.Ocpp16 ? V16Actions : V201Actions).Keys;

    /// <summary>Request and response types of an action, or null when the action has no typed class.</summary>
    public static (Type Request, Type Response)? Find(OcppVersion version, string action) =>
        (version == OcppVersion.Ocpp16 ? V16Actions : V201Actions).TryGetValue(action, out var t) ? t : null;

    /// <summary>
    /// Validates a payload against the typed schema of an action (required fields, enumerations, types) and returns it
    /// normalised (re-serialised from the typed class). Throws <see cref="System.Text.Json.JsonException"/> when invalid.
    /// </summary>
    public static System.Text.Json.Nodes.JsonObject Normalize(OcppVersion version, string action, bool response, System.Text.Json.Nodes.JsonObject payload)
    {
        var types = Find(version, action) ?? throw new System.Text.Json.JsonException($"{action} has no typed {version.DisplayName()} class");
        var type = response ? types.Response : types.Request;
        return OcppSession.Serialize(OcppSession.Deserialize(payload, type, version), type, version);
    }
}
