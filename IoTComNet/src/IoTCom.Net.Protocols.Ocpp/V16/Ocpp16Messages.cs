using System.Text.Json.Serialization;

#pragma warning disable CS1591 // Field names follow the OCPP 1.6 JSON schemas one to one; see the specification for their meaning.

namespace IoTCom.Net.Protocols.Ocpp.V16;

/// <summary>Base for OCPP 1.6 requests.</summary>
public abstract class Ocpp16Request<TResponse> : IOcppRequest<TResponse> where TResponse : class
{
    private protected abstract string ActionName { get; }
    private protected virtual bool Command => false;
    string IOcppRequest<TResponse>.Action => ActionName;
    OcppVersion IOcppRequest<TResponse>.Version => OcppVersion.Ocpp16;
    bool IOcppRequest<TResponse>.IsCommand => Command;
}

// ---- enumerations ------------------------------------------------------------------------------------------------

public enum RegistrationStatus { Accepted, Pending, Rejected }
public enum AuthorizationStatus { Accepted, Blocked, Expired, Invalid, ConcurrentTx }
public enum ChargePointStatus { Available, Preparing, Charging, SuspendedEVSE, SuspendedEV, Finishing, Reserved, Unavailable, Faulted }

public enum ChargePointErrorCode
{
    ConnectorLockFailure, EVCommunicationError, GroundFailure, HighTemperature, InternalError, LocalListConflict, NoError,
    OtherError, OverCurrentFailure, PowerMeterFailure, PowerSwitchFailure, ReaderFailure, ResetFailure, UnderVoltage,
    OverVoltage, WeakSignal,
}

public enum StopReason { EmergencyStop, EVDisconnected, HardReset, Local, Other, PowerLoss, Reboot, Remote, SoftReset, UnlockCommand, DeAuthorized }
public enum ResetType { Hard, Soft }
public enum AvailabilityType { Inoperative, Operative }
public enum AvailabilityStatus { Accepted, Rejected, Scheduled }
public enum ConfigurationStatus { Accepted, Rejected, RebootRequired, NotSupported }
public enum UnlockStatus { Unlocked, UnlockFailed, NotSupported }
public enum DataTransferStatus { Accepted, Rejected, UnknownMessageId, UnknownVendorId }
public enum MessageTrigger { BootNotification, DiagnosticsStatusNotification, FirmwareStatusNotification, Heartbeat, MeterValues, StatusNotification }
public enum TriggerMessageStatus { Accepted, Rejected, NotImplemented }
public enum ChargingProfilePurpose { ChargePointMaxProfile, TxDefaultProfile, TxProfile }
public enum ChargingProfileKind { Absolute, Recurring, Relative }
public enum ChargingRateUnit { W, A }
public enum ChargingProfileStatus { Accepted, Rejected, NotSupported }
public enum ClearChargingProfileStatus { Accepted, Unknown }

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

// ---- shared types -------------------------------------------------------------------------------------------------

public sealed class IdTagInfo
{
    [JsonRequired] public AuthorizationStatus Status { get; set; }
    public DateTimeOffset? ExpiryDate { get; set; }
    public string? ParentIdTag { get; set; }
}

public sealed class SampledValue
{
    [JsonRequired] public string Value { get; set; } = "";
    public ReadingContext? Context { get; set; }
    public string? Format { get; set; }
    /// <summary>See <see cref="OcppMeasurand"/>; absent means Energy.Active.Import.Register.</summary>
    public string? Measurand { get; set; }
    public string? Phase { get; set; }
    public string? Location { get; set; }
    /// <summary>Wh, kWh, W, kW, A, V, Celsius, Percent, …</summary>
    public string? Unit { get; set; }
}

public sealed class MeterValue
{
    [JsonRequired] public DateTimeOffset Timestamp { get; set; }
    [JsonRequired] public List<SampledValue> SampledValue { get; set; } = [];
}

public sealed class KeyValue
{
    [JsonRequired] public string Key { get; set; } = "";
    [JsonRequired] public bool Readonly { get; set; }
    public string? Value { get; set; }
}

public sealed class ChargingSchedulePeriod
{
    [JsonRequired] public int StartPeriod { get; set; }
    [JsonRequired] public decimal Limit { get; set; }
    public int? NumberPhases { get; set; }
}

public sealed class ChargingSchedule
{
    public int? Duration { get; set; }
    public DateTimeOffset? StartSchedule { get; set; }
    [JsonRequired] public ChargingRateUnit ChargingRateUnit { get; set; }
    [JsonRequired] public List<ChargingSchedulePeriod> ChargingSchedulePeriod { get; set; } = [];
    public decimal? MinChargingRate { get; set; }
}

public sealed class ChargingProfile
{
    [JsonRequired] public int ChargingProfileId { get; set; }
    public int? TransactionId { get; set; }
    [JsonRequired] public int StackLevel { get; set; }
    [JsonRequired] public ChargingProfilePurpose ChargingProfilePurpose { get; set; }
    [JsonRequired] public ChargingProfileKind ChargingProfileKind { get; set; }
    public DateTimeOffset? ValidFrom { get; set; }
    public DateTimeOffset? ValidTo { get; set; }
    [JsonRequired] public ChargingSchedule ChargingSchedule { get; set; } = new();
}

// ---- charge point → central system ---------------------------------------------------------------------------------

public sealed class AuthorizeRequest : Ocpp16Request<AuthorizeResponse>
{
    private protected override string ActionName => "Authorize";
    [JsonRequired] public string IdTag { get; set; } = "";
}

public sealed class AuthorizeResponse
{
    [JsonRequired] public IdTagInfo IdTagInfo { get; set; } = new();
}

public sealed class BootNotificationRequest : Ocpp16Request<BootNotificationResponse>
{
    private protected override string ActionName => "BootNotification";
    [JsonRequired] public string ChargePointVendor { get; set; } = "";
    [JsonRequired] public string ChargePointModel { get; set; } = "";
    public string? ChargePointSerialNumber { get; set; }
    public string? ChargeBoxSerialNumber { get; set; }
    public string? FirmwareVersion { get; set; }
    public string? Iccid { get; set; }
    public string? Imsi { get; set; }
    public string? MeterType { get; set; }
    public string? MeterSerialNumber { get; set; }
}

public sealed class BootNotificationResponse
{
    [JsonRequired] public RegistrationStatus Status { get; set; }
    [JsonRequired] public DateTimeOffset CurrentTime { get; set; }
    /// <summary>Heartbeat interval in seconds (or retry interval when not accepted).</summary>
    [JsonRequired] public int Interval { get; set; }
}

public sealed class HeartbeatRequest : Ocpp16Request<HeartbeatResponse>
{
    private protected override string ActionName => "Heartbeat";
}

public sealed class HeartbeatResponse
{
    [JsonRequired] public DateTimeOffset CurrentTime { get; set; }
}

public sealed class MeterValuesRequest : Ocpp16Request<MeterValuesResponse>
{
    private protected override string ActionName => "MeterValues";
    [JsonRequired] public int ConnectorId { get; set; }
    public int? TransactionId { get; set; }
    [JsonRequired] public List<MeterValue> MeterValue { get; set; } = [];
}

public sealed class MeterValuesResponse;

public sealed class StartTransactionRequest : Ocpp16Request<StartTransactionResponse>
{
    private protected override string ActionName => "StartTransaction";
    [JsonRequired] public int ConnectorId { get; set; }
    [JsonRequired] public string IdTag { get; set; } = "";
    [JsonRequired] public int MeterStart { get; set; }
    public int? ReservationId { get; set; }
    [JsonRequired] public DateTimeOffset Timestamp { get; set; }
}

public sealed class StartTransactionResponse
{
    [JsonRequired] public IdTagInfo IdTagInfo { get; set; } = new();
    [JsonRequired] public int TransactionId { get; set; }
}

public sealed class StatusNotificationRequest : Ocpp16Request<StatusNotificationResponse>
{
    private protected override string ActionName => "StatusNotification";
    [JsonRequired] public int ConnectorId { get; set; }
    [JsonRequired] public ChargePointErrorCode ErrorCode { get; set; }
    public string? Info { get; set; }
    [JsonRequired] public ChargePointStatus Status { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
    public string? VendorId { get; set; }
    public string? VendorErrorCode { get; set; }
}

public sealed class StatusNotificationResponse;

public sealed class StopTransactionRequest : Ocpp16Request<StopTransactionResponse>
{
    private protected override string ActionName => "StopTransaction";
    public string? IdTag { get; set; }
    [JsonRequired] public int MeterStop { get; set; }
    [JsonRequired] public DateTimeOffset Timestamp { get; set; }
    [JsonRequired] public int TransactionId { get; set; }
    public StopReason? Reason { get; set; }
    public List<MeterValue>? TransactionData { get; set; }
}

public sealed class StopTransactionResponse
{
    public IdTagInfo? IdTagInfo { get; set; }
}

public sealed class DataTransferRequest : Ocpp16Request<DataTransferResponse>
{
    private protected override string ActionName => "DataTransfer";
    private protected override bool Command => true;
    [JsonRequired] public string VendorId { get; set; } = "";
    public string? MessageId { get; set; }
    public string? Data { get; set; }
}

public sealed class DataTransferResponse
{
    [JsonRequired] public DataTransferStatus Status { get; set; }
    public string? Data { get; set; }
}

// ---- central system → charge point ---------------------------------------------------------------------------------

public sealed class ChangeAvailabilityRequest : Ocpp16Request<ChangeAvailabilityResponse>
{
    private protected override string ActionName => "ChangeAvailability";
    private protected override bool Command => true;
    [JsonRequired] public int ConnectorId { get; set; }
    [JsonRequired] public AvailabilityType Type { get; set; }
}

public sealed class ChangeAvailabilityResponse
{
    [JsonRequired] public AvailabilityStatus Status { get; set; }
}

public sealed class ChangeConfigurationRequest : Ocpp16Request<ChangeConfigurationResponse>
{
    private protected override string ActionName => "ChangeConfiguration";
    private protected override bool Command => true;
    [JsonRequired] public string Key { get; set; } = "";
    [JsonRequired] public string Value { get; set; } = "";
}

public sealed class ChangeConfigurationResponse
{
    [JsonRequired] public ConfigurationStatus Status { get; set; }
}

public sealed class ClearCacheRequest : Ocpp16Request<ClearCacheResponse>
{
    private protected override string ActionName => "ClearCache";
    private protected override bool Command => true;
}

public sealed class ClearCacheResponse
{
    [JsonRequired] public OcppGenericStatus Status { get; set; }
}

public sealed class GetConfigurationRequest : Ocpp16Request<GetConfigurationResponse>
{
    private protected override string ActionName => "GetConfiguration";
    public List<string>? Key { get; set; }
}

public sealed class GetConfigurationResponse
{
    public List<KeyValue>? ConfigurationKey { get; set; }
    public List<string>? UnknownKey { get; set; }
}

public sealed class RemoteStartTransactionRequest : Ocpp16Request<RemoteStartTransactionResponse>
{
    private protected override string ActionName => "RemoteStartTransaction";
    private protected override bool Command => true;
    public int? ConnectorId { get; set; }
    [JsonRequired] public string IdTag { get; set; } = "";
    public ChargingProfile? ChargingProfile { get; set; }
}

public sealed class RemoteStartTransactionResponse
{
    [JsonRequired] public OcppGenericStatus Status { get; set; }
}

public sealed class RemoteStopTransactionRequest : Ocpp16Request<RemoteStopTransactionResponse>
{
    private protected override string ActionName => "RemoteStopTransaction";
    private protected override bool Command => true;
    [JsonRequired] public int TransactionId { get; set; }
}

public sealed class RemoteStopTransactionResponse
{
    [JsonRequired] public OcppGenericStatus Status { get; set; }
}

public sealed class ResetRequest : Ocpp16Request<ResetResponse>
{
    private protected override string ActionName => "Reset";
    private protected override bool Command => true;
    [JsonRequired] public ResetType Type { get; set; }
}

public sealed class ResetResponse
{
    [JsonRequired] public OcppGenericStatus Status { get; set; }
}

public sealed class UnlockConnectorRequest : Ocpp16Request<UnlockConnectorResponse>
{
    private protected override string ActionName => "UnlockConnector";
    private protected override bool Command => true;
    [JsonRequired] public int ConnectorId { get; set; }
}

public sealed class UnlockConnectorResponse
{
    [JsonRequired] public UnlockStatus Status { get; set; }
}

public sealed class TriggerMessageRequest : Ocpp16Request<TriggerMessageResponse>
{
    private protected override string ActionName => "TriggerMessage";
    [JsonRequired] public MessageTrigger RequestedMessage { get; set; }
    public int? ConnectorId { get; set; }
}

public sealed class TriggerMessageResponse
{
    [JsonRequired] public TriggerMessageStatus Status { get; set; }
}

public sealed class SetChargingProfileRequest : Ocpp16Request<SetChargingProfileResponse>
{
    private protected override string ActionName => "SetChargingProfile";
    private protected override bool Command => true;
    [JsonRequired] public int ConnectorId { get; set; }
    [JsonRequired] public ChargingProfile CsChargingProfiles { get; set; } = new();
}

public sealed class SetChargingProfileResponse
{
    [JsonRequired] public ChargingProfileStatus Status { get; set; }
}

public sealed class ClearChargingProfileRequest : Ocpp16Request<ClearChargingProfileResponse>
{
    private protected override string ActionName => "ClearChargingProfile";
    private protected override bool Command => true;
    public int? Id { get; set; }
    public int? ConnectorId { get; set; }
    public ChargingProfilePurpose? ChargingProfilePurpose { get; set; }
    public int? StackLevel { get; set; }
}

public sealed class ClearChargingProfileResponse
{
    [JsonRequired] public ClearChargingProfileStatus Status { get; set; }
}
