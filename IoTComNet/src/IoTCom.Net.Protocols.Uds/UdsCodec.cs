using System.Globalization;

namespace IoTCom.Net.Protocols.Uds;

/// <summary>UDS (ISO 14229-1) service identifiers.</summary>
public static class UdsService
{
    /// <summary>0x10 DiagnosticSessionControl.</summary>
    public const byte DiagnosticSessionControl = 0x10;
    /// <summary>0x11 ECUReset.</summary>
    public const byte EcuReset = 0x11;
    /// <summary>0x14 ClearDiagnosticInformation.</summary>
    public const byte ClearDiagnosticInformation = 0x14;
    /// <summary>0x19 ReadDTCInformation.</summary>
    public const byte ReadDtcInformation = 0x19;
    /// <summary>0x22 ReadDataByIdentifier.</summary>
    public const byte ReadDataByIdentifier = 0x22;
    /// <summary>0x27 SecurityAccess.</summary>
    public const byte SecurityAccess = 0x27;
    /// <summary>0x28 CommunicationControl.</summary>
    public const byte CommunicationControl = 0x28;
    /// <summary>0x2E WriteDataByIdentifier.</summary>
    public const byte WriteDataByIdentifier = 0x2E;
    /// <summary>0x31 RoutineControl.</summary>
    public const byte RoutineControl = 0x31;
    /// <summary>0x34 RequestDownload.</summary>
    public const byte RequestDownload = 0x34;
    /// <summary>0x36 TransferData.</summary>
    public const byte TransferData = 0x36;
    /// <summary>0x37 RequestTransferExit.</summary>
    public const byte RequestTransferExit = 0x37;
    /// <summary>0x3E TesterPresent.</summary>
    public const byte TesterPresent = 0x3E;
    /// <summary>0x85 ControlDTCSetting.</summary>
    public const byte ControlDtcSetting = 0x85;
    /// <summary>0x7F negative response.</summary>
    public const byte NegativeResponse = 0x7F;
    /// <summary>Positive responses carry the service identifier plus this offset.</summary>
    public const byte PositiveOffset = 0x40;
    /// <summary>Sub-function bit that suppresses the positive response.</summary>
    public const byte SuppressPositiveResponse = 0x80;

    /// <summary>Services that change ECU state; blocked by <see cref="UdsClientOptions.ReadOnly"/>.</summary>
    public static bool IsWrite(byte service) => service is EcuReset or ClearDiagnosticInformation or WriteDataByIdentifier
        or RoutineControl or RequestDownload or TransferData or RequestTransferExit or CommunicationControl or ControlDtcSetting;

    /// <summary>Name of a service identifier (request or positive response).</summary>
    public static string Name(byte sid) => (sid >= PositiveOffset && sid != NegativeResponse ? (byte)(sid - PositiveOffset) : sid) switch
    {
        DiagnosticSessionControl => "DiagnosticSessionControl",
        EcuReset => "ECUReset",
        ClearDiagnosticInformation => "ClearDiagnosticInformation",
        ReadDtcInformation => "ReadDTCInformation",
        ReadDataByIdentifier => "ReadDataByIdentifier",
        SecurityAccess => "SecurityAccess",
        CommunicationControl => "CommunicationControl",
        WriteDataByIdentifier => "WriteDataByIdentifier",
        RoutineControl => "RoutineControl",
        RequestDownload => "RequestDownload",
        TransferData => "TransferData",
        RequestTransferExit => "RequestTransferExit",
        TesterPresent => "TesterPresent",
        ControlDtcSetting => "ControlDTCSetting",
        NegativeResponse => "NegativeResponse",
        var s when s is >= 0x01 and <= 0x0A => $"OBD mode {s:X2}",
        var s => $"0x{s:X2}",
    };
}

/// <summary>Negative response codes (ISO 14229-1 Annex A).</summary>
public enum UdsNrc : byte
{
    /// <summary>0x10 General reject.</summary>
    GeneralReject = 0x10,
    /// <summary>0x11 Service not supported.</summary>
    ServiceNotSupported = 0x11,
    /// <summary>0x12 Sub-function not supported.</summary>
    SubFunctionNotSupported = 0x12,
    /// <summary>0x13 Incorrect message length or invalid format.</summary>
    IncorrectMessageLengthOrInvalidFormat = 0x13,
    /// <summary>0x14 Response too long.</summary>
    ResponseTooLong = 0x14,
    /// <summary>0x21 Busy, repeat request.</summary>
    BusyRepeatRequest = 0x21,
    /// <summary>0x22 Conditions not correct.</summary>
    ConditionsNotCorrect = 0x22,
    /// <summary>0x24 Request sequence error.</summary>
    RequestSequenceError = 0x24,
    /// <summary>0x31 Request out of range.</summary>
    RequestOutOfRange = 0x31,
    /// <summary>0x33 Security access denied.</summary>
    SecurityAccessDenied = 0x33,
    /// <summary>0x35 Invalid key.</summary>
    InvalidKey = 0x35,
    /// <summary>0x36 Exceeded number of attempts.</summary>
    ExceededNumberOfAttempts = 0x36,
    /// <summary>0x37 Required time delay not expired.</summary>
    RequiredTimeDelayNotExpired = 0x37,
    /// <summary>0x70 Upload/download not accepted.</summary>
    UploadDownloadNotAccepted = 0x70,
    /// <summary>0x72 General programming failure.</summary>
    GeneralProgrammingFailure = 0x72,
    /// <summary>0x78 Request correctly received, response pending.</summary>
    ResponsePending = 0x78,
    /// <summary>0x7E Sub-function not supported in active session.</summary>
    SubFunctionNotSupportedInActiveSession = 0x7E,
    /// <summary>0x7F Service not supported in active session.</summary>
    ServiceNotSupportedInActiveSession = 0x7F,
}

/// <summary>Diagnostic sessions (sub-functions of 0x10).</summary>
public enum UdsSession : byte
{
    /// <summary>0x01 Default session.</summary>
    Default = 0x01,
    /// <summary>0x02 Programming session.</summary>
    Programming = 0x02,
    /// <summary>0x03 Extended diagnostic session.</summary>
    Extended = 0x03,
    /// <summary>0x04 Safety system diagnostic session.</summary>
    SafetySystem = 0x04,
}

/// <summary>Reset types (sub-functions of 0x11).</summary>
public enum UdsResetType : byte
{
    /// <summary>0x01 Hard reset.</summary>
    Hard = 0x01,
    /// <summary>0x02 Key off/on.</summary>
    KeyOffOn = 0x02,
    /// <summary>0x03 Soft reset.</summary>
    Soft = 0x03,
}

/// <summary>The ECU answered with a negative response (0x7F).</summary>
public sealed class UdsNegativeResponseException : DeviceException
{
    /// <summary>Creates the exception.</summary>
    public UdsNegativeResponseException(byte service, UdsNrc code)
        : base($"{UdsService.Name(service)} rejected: {Describe(code)} (NRC 0x{(byte)code:X2})")
    {
        Service = service;
        ResponseCode = code;
    }

    /// <summary>Creates the exception.</summary>
    public UdsNegativeResponseException() : base("UDS negative response") { }

    /// <summary>Creates the exception.</summary>
    public UdsNegativeResponseException(string message) : base(message) { }

    /// <summary>Creates the exception.</summary>
    public UdsNegativeResponseException(string message, Exception? inner) : base(message, inner) { }

    /// <summary>Rejected service.</summary>
    public byte Service { get; }

    /// <summary>Negative response code.</summary>
    public UdsNrc ResponseCode { get; }

    /// <summary>Human-readable NRC.</summary>
    public static string Describe(UdsNrc code) => code switch
    {
        UdsNrc.GeneralReject => "general reject",
        UdsNrc.ServiceNotSupported => "service not supported",
        UdsNrc.SubFunctionNotSupported => "sub-function not supported",
        UdsNrc.IncorrectMessageLengthOrInvalidFormat => "incorrect message length or invalid format",
        UdsNrc.ResponseTooLong => "response too long",
        UdsNrc.BusyRepeatRequest => "busy, repeat request",
        UdsNrc.ConditionsNotCorrect => "conditions not correct",
        UdsNrc.RequestSequenceError => "request sequence error",
        UdsNrc.RequestOutOfRange => "request out of range",
        UdsNrc.SecurityAccessDenied => "security access denied",
        UdsNrc.InvalidKey => "invalid key",
        UdsNrc.ExceededNumberOfAttempts => "exceeded number of attempts",
        UdsNrc.RequiredTimeDelayNotExpired => "required time delay not expired",
        UdsNrc.UploadDownloadNotAccepted => "upload/download not accepted",
        UdsNrc.GeneralProgrammingFailure => "general programming failure",
        UdsNrc.ResponsePending => "response pending",
        UdsNrc.SubFunctionNotSupportedInActiveSession => "sub-function not supported in active session",
        UdsNrc.ServiceNotSupportedInActiveSession => "service not supported in active session",
        _ => "unknown",
    };
}

/// <summary>DTC status bits (ISO 14229-1 D.2).</summary>
[Flags]
public enum DtcStatus : byte
{
    /// <summary>No bit set.</summary>
    None = 0,
    /// <summary>Bit 0 testFailed.</summary>
    TestFailed = 0x01,
    /// <summary>Bit 1 testFailedThisOperationCycle.</summary>
    TestFailedThisOperationCycle = 0x02,
    /// <summary>Bit 2 pendingDTC.</summary>
    Pending = 0x04,
    /// <summary>Bit 3 confirmedDTC.</summary>
    Confirmed = 0x08,
    /// <summary>Bit 4 testNotCompletedSinceLastClear.</summary>
    TestNotCompletedSinceLastClear = 0x10,
    /// <summary>Bit 5 testFailedSinceLastClear.</summary>
    TestFailedSinceLastClear = 0x20,
    /// <summary>Bit 6 testNotCompletedThisOperationCycle.</summary>
    TestNotCompletedThisOperationCycle = 0x40,
    /// <summary>Bit 7 warningIndicatorRequested (MIL).</summary>
    WarningIndicatorRequested = 0x80,
}

/// <summary>
/// A diagnostic trouble code: 3 bytes in UDS (2-byte SAE J2012 code + failure-type byte), 2 bytes in OBD-II.
/// <see cref="ToString"/> gives the familiar <c>P0301</c> form (plus <c>-1A</c> when a failure type is present).
/// </summary>
/// <param name="Code">24-bit code (OBD codes are shifted left by 8 bits, failure type 0).</param>
/// <param name="Status">Status byte (UDS) or <see cref="DtcStatus.Confirmed"/> for OBD mode 03.</param>
public readonly record struct Dtc(uint Code, DtcStatus Status = DtcStatus.None)
{
    private static readonly char[] Systems = ['P', 'C', 'B', 'U'];

    /// <summary>The 2-byte SAE J2012 part.</summary>
    public ushort SaeCode => (ushort)(Code >> 8);

    /// <summary>Failure type byte (UDS only).</summary>
    public byte FailureType => (byte)Code;

    /// <summary>Creates a DTC from an OBD 2-byte code.</summary>
    public static Dtc FromObd(ushort code, DtcStatus status = DtcStatus.Confirmed) => new((uint)code << 8, status);

    /// <summary>Parses <c>P0301</c> or <c>P0301-1A</c>.</summary>
    public static Dtc Parse(string text, DtcStatus status = DtcStatus.None)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var system = Array.IndexOf(Systems, char.ToUpperInvariant(text[0]));
        if (system < 0 || text.Length < 5) throw new FormatException($"'{text}' is not a DTC (expected e.g. P0301).");
        var digits = ushort.Parse(text.AsSpan(1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (digits > 0x3FFF) throw new FormatException($"'{text}' is not a DTC.");
        var code = (ushort)((system << 14) | digits);
        byte ft = 0;
        if (text.Length >= 8 && text[5] == '-') ft = byte.Parse(text.AsSpan(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new Dtc(((uint)code << 8) | ft, status);
    }

    /// <summary>SAE form such as <c>P0301</c>, with <c>-xx</c> failure type when non-zero.</summary>
    public override string ToString()
    {
        var sae = SaeCode;
        var text = $"{Systems[sae >> 14]}{(sae >> 12) & 0x3:X1}{sae & 0x0FFF:X3}";
        return FailureType == 0 ? text : $"{text}-{FailureType:X2}";
    }
}

/// <summary>Frame-lane descriptions of UDS / OBD-II messages (used by the CLI, Gallery and traffic tap).</summary>
public static class UdsAnatomy
{
    /// <summary>Describes the fields of a UDS or OBD-II message.</summary>
    public static IReadOnlyList<FrameField> Describe(ReadOnlySpan<byte> message)
    {
        var fields = new List<FrameField>(4);
        if (message.IsEmpty) return fields;
        var sid = message[0];
        if (sid == UdsService.NegativeResponse && message.Length >= 3)
        {
            fields.Add(new FrameField("SID", 0, 1, FrameFieldKind.Function, "7F negative"));
            fields.Add(new FrameField("Service", 1, 1, FrameFieldKind.Function, UdsService.Name(message[1])));
            fields.Add(new FrameField("NRC", 2, 1, FrameFieldKind.Error, UdsNegativeResponseException.Describe((UdsNrc)message[2])));
            return fields;
        }
        fields.Add(new FrameField("SID", 0, 1, FrameFieldKind.Function, UdsService.Name(sid) + (sid >= UdsService.PositiveOffset ? " +" : "")));
        var request = (byte)(sid >= UdsService.PositiveOffset ? sid - UdsService.PositiveOffset : sid);
        var offset = 1;
        if (request is UdsService.ReadDataByIdentifier or UdsService.WriteDataByIdentifier && message.Length >= 3)
        {
            fields.Add(new FrameField("DID", 1, 2, FrameFieldKind.Address, $"0x{message[1]:X2}{message[2]:X2}"));
            offset = 3;
        }
        else if (request is UdsService.RoutineControl && message.Length >= 4)
        {
            fields.Add(new FrameField("Type", 1, 1, FrameFieldKind.Function, $"{message[1]:X2}"));
            fields.Add(new FrameField("Routine", 2, 2, FrameFieldKind.Address, $"0x{message[2]:X2}{message[3]:X2}"));
            offset = 4;
        }
        else if (request is 0x01 or 0x02 or 0x09 && message.Length >= 2)
        {
            fields.Add(new FrameField("PID", 1, 1, FrameFieldKind.Address, $"0x{message[1]:X2}"));
            offset = 2;
        }
        else if (request is UdsService.DiagnosticSessionControl or UdsService.EcuReset or UdsService.SecurityAccess
                 or UdsService.ReadDtcInformation or UdsService.TesterPresent && message.Length >= 2)
        {
            fields.Add(new FrameField("Sub", 1, 1, FrameFieldKind.Function, $"{message[1]:X2}"));
            offset = 2;
        }
        if (message.Length > offset) fields.Add(new FrameField("Data", offset, message.Length - offset, FrameFieldKind.Data));
        return fields;
    }
}
