using System.Text.Json.Serialization;

#pragma warning disable CS1591 // Field names follow the OCPP 2.0.1 JSON schemas one to one; see the specification for their meaning.

namespace IoTCom.Net.Protocols.Ocpp.V201;

/// <summary>Base for OCPP 2.0.1 requests.</summary>
public abstract class Ocpp201Request<TResponse> : IOcppRequest<TResponse> where TResponse : class
{
    private protected abstract string ActionName { get; }
    private protected virtual bool Command => false;
    string IOcppRequest<TResponse>.Action => ActionName;
    OcppVersion IOcppRequest<TResponse>.Version => OcppVersion.Ocpp201;
    bool IOcppRequest<TResponse>.IsCommand => Command;
}

// ---- enumerations ------------------------------------------------------------------------------------------------

public enum BootReason { ApplicationReset, FirmwareUpdate, LocalReset, PowerUp, RemoteReset, ScheduledReset, Triggered, Unknown, Watchdog }
public enum RegistrationStatus { Accepted, Pending, Rejected }
public enum ConnectorStatus { Available, Occupied, Reserved, Unavailable, Faulted }
public enum AuthorizationStatus { Accepted, Blocked, ConcurrentTx, Expired, Invalid, NoCredit, NotAllowedTypeEVSE, NotAtThisLocation, NotAtThisTime, Unknown }
public enum IdTokenType { Central, eMAID, ISO14443, ISO15693, KeyCode, Local, MacAddress, NoAuthorization }
public enum TransactionEventType { Ended, Started, Updated }
public enum ChargingState { Charging, EVConnected, SuspendedEV, SuspendedEVSE, Idle }

public enum TriggerReason
{
    Authorized, CablePluggedIn, ChargingRateChanged, ChargingStateChanged, Deauthorized, EnergyLimitReached, EVCommunicationLost,
    EVConnectTimeout, MeterValueClock, MeterValuePeriodic, TimeLimitReached, Trigger, UnlockCommand, StopAuthorized, EVDeparted,
    EVDetected, RemoteStop, RemoteStart, AbnormalCondition, SignedDataReceived, ResetCommand,
}

public enum Reason
{
    DeAuthorized, EmergencyStop, EnergyLimitReached, EVDisconnected, GroundFault, ImmediateReset, Local, LocalOutOfCredit, MasterPass,
    Other, OvercurrentFault, PowerLoss, PowerQuality, Reboot, Remote, SOCLimitReached, StoppedByEV, TimeLimitReached, Timeout,
}

public enum ReadingContext
{
    [JsonStringEnumMemberName("Interruption.Begin")] InterruptionBegin,
    [JsonStringEnumMemberName("Interruption.End")] InterruptionEnd,
    Other,
    [JsonStringEnumMemberName("Sample.Clock")] SampleClock,
    [JsonStringEnumMemberName("Sample.Periodic")] SamplePeriodic,
    [JsonStringEnumMemberName("Transaction.Begin")] TransactionBegin,
    [JsonStringEnumMemberName("Transaction.End")] TransactionEnd,
    Trigger,
}

public enum RequestStartStopStatus { Accepted, Rejected }
public enum ResetType { Immediate, OnIdle }
public enum ResetStatus { Accepted, Rejected, Scheduled }
public enum OperationalStatus { Inoperative, Operative }
public enum ChangeAvailabilityStatus { Accepted, Rejected, Scheduled }
public enum AttributeType { Actual, Target, MinSet, MaxSet }
public enum GetVariableStatus { Accepted, Rejected, UnknownComponent, UnknownVariable, NotSupportedAttributeType }
public enum SetVariableStatus { Accepted, Rejected, UnknownComponent, UnknownVariable, NotSupportedAttributeType, RebootRequired }
public enum UnlockStatus { Unlocked, UnlockFailed, OngoingAuthorizedTransaction, UnknownConnector }
public enum MessageTrigger { BootNotification, LogStatusNotification, FirmwareStatusNotification, Heartbeat, MeterValues, SignChargingStationCertificate, SignV2GCertificate, StatusNotification, TransactionEvent, SignCombinedCertificate, PublishFirmwareStatusNotification }
public enum TriggerMessageStatus { Accepted, Rejected, NotImplemented }
public enum ChargingProfilePurpose { ChargingStationExternalConstraints, ChargingStationMaxProfile, TxDefaultProfile, TxProfile }
public enum ChargingProfileKind { Absolute, Recurring, Relative }
public enum ChargingRateUnit { W, A }
public enum ChargingProfileStatus { Accepted, Rejected }
public enum ClearCacheStatus { Accepted, Rejected }
public enum ClearChargingProfileStatus { Accepted, Unknown }
public enum DataTransferStatus { Accepted, Rejected, UnknownMessageId, UnknownVendorId }

// ---- shared types -------------------------------------------------------------------------------------------------

public sealed class ChargingStation
{
    public string? SerialNumber { get; set; }
    [JsonRequired] public string Model { get; set; } = "";
    [JsonRequired] public string VendorName { get; set; } = "";
    public string? FirmwareVersion { get; set; }
}

public sealed class Evse
{
    [JsonRequired] public int Id { get; set; }
    public int? ConnectorId { get; set; }
}

public sealed class IdToken
{
    [JsonRequired] [JsonPropertyName("idToken")] public string Value { get; set; } = "";
    [JsonRequired] public IdTokenType Type { get; set; }
}

public sealed class IdTokenInfo
{
    [JsonRequired] public AuthorizationStatus Status { get; set; }
    public DateTimeOffset? CacheExpiryDateTime { get; set; }
}

public sealed class UnitOfMeasure
{
    public string? Unit { get; set; }
    public int? Multiplier { get; set; }
}

public sealed class SampledValue
{
    [JsonRequired] public decimal Value { get; set; }
    public ReadingContext? Context { get; set; }
    /// <summary>See <see cref="OcppMeasurand"/>; absent means Energy.Active.Import.Register.</summary>
    public string? Measurand { get; set; }
    public string? Phase { get; set; }
    public string? Location { get; set; }
    public UnitOfMeasure? UnitOfMeasure { get; set; }
}

public sealed class MeterValue
{
    [JsonRequired] public DateTimeOffset Timestamp { get; set; }
    [JsonRequired] public List<SampledValue> SampledValue { get; set; } = [];
}

public sealed class Transaction
{
    [JsonRequired] public string TransactionId { get; set; } = "";
    public ChargingState? ChargingState { get; set; }
    public int? TimeSpentCharging { get; set; }
    public Reason? StoppedReason { get; set; }
    public int? RemoteStartId { get; set; }
}

public sealed class Component
{
    [JsonRequired] public string Name { get; set; } = "";
    public string? Instance { get; set; }
    public Evse? Evse { get; set; }
}

public sealed class Variable
{
    [JsonRequired] public string Name { get; set; } = "";
    public string? Instance { get; set; }
}

public sealed class GetVariableData
{
    public AttributeType? AttributeType { get; set; }
    [JsonRequired] public Component Component { get; set; } = new();
    [JsonRequired] public Variable Variable { get; set; } = new();
}

public sealed class GetVariableResult
{
    [JsonRequired] public GetVariableStatus AttributeStatus { get; set; }
    public AttributeType? AttributeType { get; set; }
    public string? AttributeValue { get; set; }
    [JsonRequired] public Component Component { get; set; } = new();
    [JsonRequired] public Variable Variable { get; set; } = new();
}

public sealed class SetVariableData
{
    public AttributeType? AttributeType { get; set; }
    [JsonRequired] public string AttributeValue { get; set; } = "";
    [JsonRequired] public Component Component { get; set; } = new();
    [JsonRequired] public Variable Variable { get; set; } = new();
}

public sealed class SetVariableResult
{
    public AttributeType? AttributeType { get; set; }
    [JsonRequired] public SetVariableStatus AttributeStatus { get; set; }
    [JsonRequired] public Component Component { get; set; } = new();
    [JsonRequired] public Variable Variable { get; set; } = new();
}

public sealed class ChargingSchedulePeriod
{
    [JsonRequired] public int StartPeriod { get; set; }
    [JsonRequired] public decimal Limit { get; set; }
    public int? NumberPhases { get; set; }
}

public sealed class ChargingSchedule
{
    [JsonRequired] public int Id { get; set; }
    public DateTimeOffset? StartSchedule { get; set; }
    public int? Duration { get; set; }
    [JsonRequired] public ChargingRateUnit ChargingRateUnit { get; set; }
    [JsonRequired] public List<ChargingSchedulePeriod> ChargingSchedulePeriod { get; set; } = [];
    public decimal? MinChargingRate { get; set; }
}

public sealed class ChargingProfile
{
    [JsonRequired] public int Id { get; set; }
    [JsonRequired] public int StackLevel { get; set; }
    [JsonRequired] public ChargingProfilePurpose ChargingProfilePurpose { get; set; }
    [JsonRequired] public ChargingProfileKind ChargingProfileKind { get; set; }
    public DateTimeOffset? ValidFrom { get; set; }
    public DateTimeOffset? ValidTo { get; set; }
    public string? TransactionId { get; set; }
    [JsonRequired] public List<ChargingSchedule> ChargingSchedule { get; set; } = [];
}

public sealed class StatusInfo
{
    [JsonRequired] public string ReasonCode { get; set; } = "";
    public string? AdditionalInfo { get; set; }
}

// ---- charging station → CSMS ---------------------------------------------------------------------------------------

public sealed class BootNotificationRequest : Ocpp201Request<BootNotificationResponse>
{
    private protected override string ActionName => "BootNotification";
    [JsonRequired] public ChargingStation ChargingStation { get; set; } = new();
    [JsonRequired] public BootReason Reason { get; set; }
}

public sealed class BootNotificationResponse
{
    [JsonRequired] public DateTimeOffset CurrentTime { get; set; }
    [JsonRequired] public int Interval { get; set; }
    [JsonRequired] public RegistrationStatus Status { get; set; }
    public StatusInfo? StatusInfo { get; set; }
}

public sealed class HeartbeatRequest : Ocpp201Request<HeartbeatResponse>
{
    private protected override string ActionName => "Heartbeat";
}

public sealed class HeartbeatResponse
{
    [JsonRequired] public DateTimeOffset CurrentTime { get; set; }
}

public sealed class StatusNotificationRequest : Ocpp201Request<StatusNotificationResponse>
{
    private protected override string ActionName => "StatusNotification";
    [JsonRequired] public DateTimeOffset Timestamp { get; set; }
    [JsonRequired] public ConnectorStatus ConnectorStatus { get; set; }
    [JsonRequired] public int EvseId { get; set; }
    [JsonRequired] public int ConnectorId { get; set; }
}

public sealed class StatusNotificationResponse;

public sealed class AuthorizeRequest : Ocpp201Request<AuthorizeResponse>
{
    private protected override string ActionName => "Authorize";
    [JsonRequired] public IdToken IdToken { get; set; } = new();
}

public sealed class AuthorizeResponse
{
    [JsonRequired] public IdTokenInfo IdTokenInfo { get; set; } = new();
}

public sealed class TransactionEventRequest : Ocpp201Request<TransactionEventResponse>
{
    private protected override string ActionName => "TransactionEvent";
    [JsonRequired] public TransactionEventType EventType { get; set; }
    [JsonRequired] public DateTimeOffset Timestamp { get; set; }
    [JsonRequired] public TriggerReason TriggerReason { get; set; }
    [JsonRequired] public int SeqNo { get; set; }
    public bool? Offline { get; set; }
    [JsonRequired] public Transaction TransactionInfo { get; set; } = new();
    public IdToken? IdToken { get; set; }
    public Evse? Evse { get; set; }
    public List<MeterValue>? MeterValue { get; set; }
}

public sealed class TransactionEventResponse
{
    public decimal? TotalCost { get; set; }
    public IdTokenInfo? IdTokenInfo { get; set; }
}

public sealed class MeterValuesRequest : Ocpp201Request<MeterValuesResponse>
{
    private protected override string ActionName => "MeterValues";
    [JsonRequired] public int EvseId { get; set; }
    [JsonRequired] public List<MeterValue> MeterValue { get; set; } = [];
}

public sealed class MeterValuesResponse;

public sealed class DataTransferRequest : Ocpp201Request<DataTransferResponse>
{
    private protected override string ActionName => "DataTransfer";
    private protected override bool Command => true;
    public string? MessageId { get; set; }
    public string? Data { get; set; }
    [JsonRequired] public string VendorId { get; set; } = "";
}

public sealed class DataTransferResponse
{
    [JsonRequired] public DataTransferStatus Status { get; set; }
    public string? Data { get; set; }
}

// ---- CSMS → charging station ---------------------------------------------------------------------------------------

public sealed class RequestStartTransactionRequest : Ocpp201Request<RequestStartTransactionResponse>
{
    private protected override string ActionName => "RequestStartTransaction";
    private protected override bool Command => true;
    public int? EvseId { get; set; }
    [JsonRequired] public int RemoteStartId { get; set; }
    [JsonRequired] public IdToken IdToken { get; set; } = new();
    public ChargingProfile? ChargingProfile { get; set; }
}

public sealed class RequestStartTransactionResponse
{
    [JsonRequired] public RequestStartStopStatus Status { get; set; }
    public string? TransactionId { get; set; }
}

public sealed class RequestStopTransactionRequest : Ocpp201Request<RequestStopTransactionResponse>
{
    private protected override string ActionName => "RequestStopTransaction";
    private protected override bool Command => true;
    [JsonRequired] public string TransactionId { get; set; } = "";
}

public sealed class RequestStopTransactionResponse
{
    [JsonRequired] public RequestStartStopStatus Status { get; set; }
}

public sealed class ResetRequest : Ocpp201Request<ResetResponse>
{
    private protected override string ActionName => "Reset";
    private protected override bool Command => true;
    [JsonRequired] public ResetType Type { get; set; }
    public int? EvseId { get; set; }
}

public sealed class ResetResponse
{
    [JsonRequired] public ResetStatus Status { get; set; }
}

public sealed class ChangeAvailabilityRequest : Ocpp201Request<ChangeAvailabilityResponse>
{
    private protected override string ActionName => "ChangeAvailability";
    private protected override bool Command => true;
    public Evse? Evse { get; set; }
    [JsonRequired] public OperationalStatus OperationalStatus { get; set; }
}

public sealed class ChangeAvailabilityResponse
{
    [JsonRequired] public ChangeAvailabilityStatus Status { get; set; }
}

public sealed class GetVariablesRequest : Ocpp201Request<GetVariablesResponse>
{
    private protected override string ActionName => "GetVariables";
    [JsonRequired] public List<GetVariableData> GetVariableData { get; set; } = [];
}

public sealed class GetVariablesResponse
{
    [JsonRequired] public List<GetVariableResult> GetVariableResult { get; set; } = [];
}

public sealed class SetVariablesRequest : Ocpp201Request<SetVariablesResponse>
{
    private protected override string ActionName => "SetVariables";
    private protected override bool Command => true;
    [JsonRequired] public List<SetVariableData> SetVariableData { get; set; } = [];
}

public sealed class SetVariablesResponse
{
    [JsonRequired] public List<SetVariableResult> SetVariableResult { get; set; } = [];
}

public sealed class UnlockConnectorRequest : Ocpp201Request<UnlockConnectorResponse>
{
    private protected override string ActionName => "UnlockConnector";
    private protected override bool Command => true;
    [JsonRequired] public int EvseId { get; set; }
    [JsonRequired] public int ConnectorId { get; set; }
}

public sealed class UnlockConnectorResponse
{
    [JsonRequired] public UnlockStatus Status { get; set; }
}

public sealed class TriggerMessageRequest : Ocpp201Request<TriggerMessageResponse>
{
    private protected override string ActionName => "TriggerMessage";
    [JsonRequired] public MessageTrigger RequestedMessage { get; set; }
    public Evse? Evse { get; set; }
}

public sealed class TriggerMessageResponse
{
    [JsonRequired] public TriggerMessageStatus Status { get; set; }
}

public sealed class SetChargingProfileRequest : Ocpp201Request<SetChargingProfileResponse>
{
    private protected override string ActionName => "SetChargingProfile";
    private protected override bool Command => true;
    [JsonRequired] public int EvseId { get; set; }
    [JsonRequired] public ChargingProfile ChargingProfile { get; set; } = new();
}

public sealed class SetChargingProfileResponse
{
    [JsonRequired] public ChargingProfileStatus Status { get; set; }
}

public sealed class ClearCacheRequest : Ocpp201Request<ClearCacheResponse>
{
    private protected override string ActionName => "ClearCache";
    private protected override bool Command => true;
}

public sealed class ClearCacheResponse
{
    [JsonRequired] public ClearCacheStatus Status { get; set; }
}

public sealed class ClearChargingProfileCriterion
{
    public int? EvseId { get; set; }
    public ChargingProfilePurpose? ChargingProfilePurpose { get; set; }
    public int? StackLevel { get; set; }
}

public sealed class ClearChargingProfileRequest : Ocpp201Request<ClearChargingProfileResponse>
{
    private protected override string ActionName => "ClearChargingProfile";
    private protected override bool Command => true;
    public int? ChargingProfileId { get; set; }
    public ClearChargingProfileCriterion? ChargingProfileCriteria { get; set; }
}

public sealed class ClearChargingProfileResponse
{
    [JsonRequired] public ClearChargingProfileStatus Status { get; set; }
}
