using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using IoTCom.Net.Framing;

namespace IoTCom.Net.Protocols.Iec104;

/// <summary>ASDU type identifications (IEC 60870-5-101/104). The standard mnemonic is in <see cref="Iec104Types.Mnemonic"/>.</summary>
public enum Iec104TypeId : byte
{
    /// <summary>M_SP_NA_1: single-point information.</summary>
    SinglePoint = 1,
    /// <summary>M_DP_NA_1: double-point information.</summary>
    DoublePoint = 3,
    /// <summary>M_ST_NA_1: step position (transformer tap).</summary>
    StepPosition = 5,
    /// <summary>M_BO_NA_1: bitstring of 32 bits.</summary>
    Bitstring = 7,
    /// <summary>M_ME_NA_1: measured value, normalized.</summary>
    MeasuredNormalized = 9,
    /// <summary>M_ME_NB_1: measured value, scaled.</summary>
    MeasuredScaled = 11,
    /// <summary>M_ME_NC_1: measured value, short floating point.</summary>
    MeasuredFloat = 13,
    /// <summary>M_IT_NA_1: integrated totals (counters).</summary>
    IntegratedTotals = 15,
    /// <summary>M_ME_ND_1: measured value, normalized, without quality descriptor.</summary>
    MeasuredNormalizedNoQuality = 21,
    /// <summary>M_SP_TB_1: single-point with CP56Time2a.</summary>
    SinglePointTime = 30,
    /// <summary>M_DP_TB_1: double-point with CP56Time2a.</summary>
    DoublePointTime = 31,
    /// <summary>M_ST_TB_1: step position with CP56Time2a.</summary>
    StepPositionTime = 32,
    /// <summary>M_BO_TB_1: bitstring with CP56Time2a.</summary>
    BitstringTime = 33,
    /// <summary>M_ME_TD_1: normalized with CP56Time2a.</summary>
    MeasuredNormalizedTime = 34,
    /// <summary>M_ME_TE_1: scaled with CP56Time2a.</summary>
    MeasuredScaledTime = 35,
    /// <summary>M_ME_TF_1: float with CP56Time2a.</summary>
    MeasuredFloatTime = 36,
    /// <summary>M_IT_TB_1: integrated totals with CP56Time2a.</summary>
    IntegratedTotalsTime = 37,
    /// <summary>C_SC_NA_1: single command.</summary>
    SingleCommand = 45,
    /// <summary>C_DC_NA_1: double command.</summary>
    DoubleCommand = 46,
    /// <summary>C_RC_NA_1: regulating step command.</summary>
    RegulatingStep = 47,
    /// <summary>C_SE_NA_1: set point, normalized.</summary>
    SetpointNormalized = 48,
    /// <summary>C_SE_NB_1: set point, scaled.</summary>
    SetpointScaled = 49,
    /// <summary>C_SE_NC_1: set point, short floating point.</summary>
    SetpointFloat = 50,
    /// <summary>C_BO_NA_1: bitstring command.</summary>
    BitstringCommand = 51,
    /// <summary>C_SC_TA_1: single command with CP56Time2a.</summary>
    SingleCommandTime = 58,
    /// <summary>C_DC_TA_1: double command with CP56Time2a.</summary>
    DoubleCommandTime = 59,
    /// <summary>C_RC_TA_1: regulating step command with CP56Time2a.</summary>
    RegulatingStepTime = 60,
    /// <summary>C_SE_TA_1: normalized set point with CP56Time2a.</summary>
    SetpointNormalizedTime = 61,
    /// <summary>C_SE_TB_1: scaled set point with CP56Time2a.</summary>
    SetpointScaledTime = 62,
    /// <summary>C_SE_TC_1: float set point with CP56Time2a.</summary>
    SetpointFloatTime = 63,
    /// <summary>C_BO_TA_1: bitstring command with CP56Time2a.</summary>
    BitstringCommandTime = 64,
    /// <summary>M_EI_NA_1: end of initialisation.</summary>
    EndOfInitialisation = 70,
    /// <summary>C_IC_NA_1: interrogation command.</summary>
    Interrogation = 100,
    /// <summary>C_CI_NA_1: counter interrogation command.</summary>
    CounterInterrogation = 101,
    /// <summary>C_RD_NA_1: read command.</summary>
    Read = 102,
    /// <summary>C_CS_NA_1: clock synchronisation command.</summary>
    ClockSync = 103,
    /// <summary>C_RP_NA_1: reset process command.</summary>
    ResetProcess = 105,
    /// <summary>C_TS_TA_1: test command with CP56Time2a.</summary>
    TestCommand = 107,
}

/// <summary>Cause of transmission.</summary>
public enum Iec104Cause : byte
{
    /// <summary>Not used.</summary>
    None = 0,
    /// <summary>Periodic, cyclic.</summary>
    Periodic = 1,
    /// <summary>Background scan.</summary>
    Background = 2,
    /// <summary>Spontaneous.</summary>
    Spontaneous = 3,
    /// <summary>Initialised.</summary>
    Initialised = 4,
    /// <summary>Request or requested.</summary>
    Request = 5,
    /// <summary>Activation.</summary>
    Activation = 6,
    /// <summary>Activation confirmation.</summary>
    ActivationConfirmation = 7,
    /// <summary>Deactivation.</summary>
    Deactivation = 8,
    /// <summary>Deactivation confirmation.</summary>
    DeactivationConfirmation = 9,
    /// <summary>Activation termination.</summary>
    ActivationTermination = 10,
    /// <summary>Return information caused by a remote command.</summary>
    ReturnRemote = 11,
    /// <summary>Return information caused by a local command.</summary>
    ReturnLocal = 12,
    /// <summary>File transfer.</summary>
    File = 13,
    /// <summary>Interrogated by station interrogation (groups 1–16 are 21–36).</summary>
    InterrogatedByStation = 20,
    /// <summary>Requested by general counter request (groups 1–4 are 38–41).</summary>
    RequestedByGeneralCounter = 37,
    /// <summary>Unknown type identification.</summary>
    UnknownType = 44,
    /// <summary>Unknown cause of transmission.</summary>
    UnknownCause = 45,
    /// <summary>Unknown common address of ASDU.</summary>
    UnknownCommonAddress = 46,
    /// <summary>Unknown information object address.</summary>
    UnknownObjectAddress = 47,
}

/// <summary>Quality and status flags (QDS, SIQ, DIQ, VTI transient bit and BCR flags), normalised to one set.</summary>
[Flags]
public enum Iec104Quality : ushort
{
    /// <summary>Good.</summary>
    None = 0,
    /// <summary>OV: overflow (measured values).</summary>
    Overflow = 0x01,
    /// <summary>Transient state (step position).</summary>
    Transient = 0x02,
    /// <summary>CY: counter carry.</summary>
    Carry = 0x04,
    /// <summary>CA: counter was adjusted.</summary>
    Adjusted = 0x08,
    /// <summary>BL: blocked.</summary>
    Blocked = 0x10,
    /// <summary>SB: substituted.</summary>
    Substituted = 0x20,
    /// <summary>NT: not topical.</summary>
    NotTopical = 0x40,
    /// <summary>IV: invalid.</summary>
    Invalid = 0x80,
}

/// <summary>Double-point states (DPI / DCS).</summary>
public enum Iec104DoublePoint
{
    /// <summary>Intermediate (0): moving, or not available as a command.</summary>
    Intermediate = 0,
    /// <summary>Off / open (1).</summary>
    Off = 1,
    /// <summary>On / closed (2).</summary>
    On = 2,
    /// <summary>Indeterminate (3): faulty position.</summary>
    Indeterminate = 3,
}

/// <summary>Seven-octet binary time (CP56Time2a). The day of week is written as 0 (not used).</summary>
/// <param name="Value">Date and time (milliseconds; years 2000–2099).</param>
/// <param name="Invalid">IV bit.</param>
/// <param name="SummerTime">SU bit.</param>
public readonly record struct Cp56Time2a(DateTime Value, bool Invalid = false, bool SummerTime = false)
{
    /// <summary>Encodes 7 bytes.</summary>
    public void Encode(Span<byte> d)
    {
        var t = Value;
        BinaryPrimitives.WriteUInt16LittleEndian(d, (ushort)((t.Second * 1000) + t.Millisecond));
        d[2] = (byte)(t.Minute | (Invalid ? 0x80 : 0));
        d[3] = (byte)(t.Hour | (SummerTime ? 0x80 : 0));
        d[4] = (byte)t.Day;
        d[5] = (byte)t.Month;
        d[6] = (byte)(t.Year % 100);
    }

    /// <summary>Decodes 7 bytes.</summary>
    /// <exception cref="ProtocolException">The fields are out of range.</exception>
    public static Cp56Time2a Decode(ReadOnlySpan<byte> d)
    {
        var ms = BinaryPrimitives.ReadUInt16LittleEndian(d);
        int minute = d[2] & 0x3F, hour = d[3] & 0x1F, day = d[4] & 0x1F, month = d[5] & 0x0F, year = 2000 + (d[6] & 0x7F);
        if (ms > 59_999 || minute > 59 || hour > 23 || day == 0 || month is 0 or > 12 || day > DateTime.DaysInMonth(year, month))
            throw new ProtocolException("CP56Time2a out of range.");
        return new Cp56Time2a(new DateTime(year, month, day, hour, minute, ms / 1000, ms % 1000, DateTimeKind.Unspecified), (d[2] & 0x80) != 0, (d[3] & 0x80) != 0);
    }

    /// <inheritdoc />
    public override string ToString() => Value.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture) + (Invalid ? " IV" : "") + (SummerTime ? " SU" : "");
}

/// <summary>
/// One information object. <see cref="Value"/> holds the element for every type: 0/1 for single points and single
/// commands, 0–3 for double points, −64…63 for step positions, the 32-bit bitstring, the normalized value (−1…1), the
/// scaled integer, the float, the counter, or the TSC of a test command.
/// <see cref="Qualifier"/> holds what is left of the element octet: QU and S/E bits of commands, QOS, QOI, QCC, COI,
/// QRP, or the counter sequence number.
/// </summary>
/// <param name="Address">Information object address (IOA, 24 bits).</param>
/// <param name="Value">Element value.</param>
/// <param name="Quality">Quality flags.</param>
/// <param name="Qualifier">Qualifier octet (see summary).</param>
/// <param name="Time">CP56Time2a for time-tagged types and clock sync.</param>
public sealed record Iec104Object(uint Address, double Value = 0, Iec104Quality Quality = Iec104Quality.None, byte Qualifier = 0, Cp56Time2a? Time = null)
{
    /// <summary>The value as a boolean (single point, single command).</summary>
    public bool IsOn => Value != 0;

    /// <summary>The value as a double-point state.</summary>
    public Iec104DoublePoint DoublePoint => (Iec104DoublePoint)((int)Value & 3);

    /// <summary>For commands: the select bit (S/E = 1 selects, 0 executes).</summary>
    public bool Select => (Qualifier & 0x80) != 0;

    /// <inheritdoc />
    public override string ToString() => $"IOA {Address} = {Value.ToString("0.#####", CultureInfo.InvariantCulture)}{(Quality != 0 ? $" [{Quality}]" : "")}{(Time is { } t ? $" @ {t}" : "")}";
}

/// <summary>Type metadata.</summary>
public static class Iec104Types
{
    /// <summary>Standard mnemonic (M_SP_NA_1…).</summary>
    public static string Mnemonic(Iec104TypeId type) => type switch
    {
        Iec104TypeId.SinglePoint => "M_SP_NA_1", Iec104TypeId.DoublePoint => "M_DP_NA_1", Iec104TypeId.StepPosition => "M_ST_NA_1",
        Iec104TypeId.Bitstring => "M_BO_NA_1", Iec104TypeId.MeasuredNormalized => "M_ME_NA_1", Iec104TypeId.MeasuredScaled => "M_ME_NB_1",
        Iec104TypeId.MeasuredFloat => "M_ME_NC_1", Iec104TypeId.IntegratedTotals => "M_IT_NA_1", Iec104TypeId.MeasuredNormalizedNoQuality => "M_ME_ND_1",
        Iec104TypeId.SinglePointTime => "M_SP_TB_1", Iec104TypeId.DoublePointTime => "M_DP_TB_1", Iec104TypeId.StepPositionTime => "M_ST_TB_1",
        Iec104TypeId.BitstringTime => "M_BO_TB_1", Iec104TypeId.MeasuredNormalizedTime => "M_ME_TD_1", Iec104TypeId.MeasuredScaledTime => "M_ME_TE_1",
        Iec104TypeId.MeasuredFloatTime => "M_ME_TF_1", Iec104TypeId.IntegratedTotalsTime => "M_IT_TB_1",
        Iec104TypeId.SingleCommand => "C_SC_NA_1", Iec104TypeId.DoubleCommand => "C_DC_NA_1", Iec104TypeId.RegulatingStep => "C_RC_NA_1",
        Iec104TypeId.SetpointNormalized => "C_SE_NA_1", Iec104TypeId.SetpointScaled => "C_SE_NB_1", Iec104TypeId.SetpointFloat => "C_SE_NC_1",
        Iec104TypeId.BitstringCommand => "C_BO_NA_1", Iec104TypeId.SingleCommandTime => "C_SC_TA_1", Iec104TypeId.DoubleCommandTime => "C_DC_TA_1",
        Iec104TypeId.RegulatingStepTime => "C_RC_TA_1", Iec104TypeId.SetpointNormalizedTime => "C_SE_TA_1", Iec104TypeId.SetpointScaledTime => "C_SE_TB_1",
        Iec104TypeId.SetpointFloatTime => "C_SE_TC_1", Iec104TypeId.BitstringCommandTime => "C_BO_TA_1", Iec104TypeId.EndOfInitialisation => "M_EI_NA_1",
        Iec104TypeId.Interrogation => "C_IC_NA_1", Iec104TypeId.CounterInterrogation => "C_CI_NA_1", Iec104TypeId.Read => "C_RD_NA_1",
        Iec104TypeId.ClockSync => "C_CS_NA_1", Iec104TypeId.ResetProcess => "C_RP_NA_1", Iec104TypeId.TestCommand => "C_TS_TA_1",
        _ => string.Create(CultureInfo.InvariantCulture, $"type {(byte)type}"),
    };

    /// <summary>True for types this library encodes and decodes.</summary>
    public static bool IsKnown(Iec104TypeId type) => ElementSize(type) >= 0;

    /// <summary>True for monitoring-direction types (process information, M_…).</summary>
    public static bool IsMonitoring(Iec104TypeId type) => (byte)type is < 45 or 70;

    /// <summary>True for process commands (single/double/step/set point/bitstring, with or without time).</summary>
    public static bool IsCommand(Iec104TypeId type) => (byte)type is >= 45 and <= 64;

    /// <summary>Whether the element carries a CP56Time2a.</summary>
    public static bool HasTime(Iec104TypeId type) => (byte)type is (>= 30 and <= 37) or (>= 58 and <= 64) or 103 or 107;

    /// <summary>The time-tagged variant of a monitoring type (M_SP_NA_1 → M_SP_TB_1…), or the type itself.</summary>
    public static Iec104TypeId WithTime(Iec104TypeId type) => type switch
    {
        Iec104TypeId.SinglePoint => Iec104TypeId.SinglePointTime, Iec104TypeId.DoublePoint => Iec104TypeId.DoublePointTime,
        Iec104TypeId.StepPosition => Iec104TypeId.StepPositionTime, Iec104TypeId.Bitstring => Iec104TypeId.BitstringTime,
        Iec104TypeId.MeasuredNormalized or Iec104TypeId.MeasuredNormalizedNoQuality => Iec104TypeId.MeasuredNormalizedTime,
        Iec104TypeId.MeasuredScaled => Iec104TypeId.MeasuredScaledTime, Iec104TypeId.MeasuredFloat => Iec104TypeId.MeasuredFloatTime,
        Iec104TypeId.IntegratedTotals => Iec104TypeId.IntegratedTotalsTime, _ => type,
    };

    /// <summary>The variant without time tag (M_SP_TB_1 → M_SP_NA_1…), or the type itself.</summary>
    public static Iec104TypeId WithoutTime(Iec104TypeId type) => (byte)type is >= 30 and <= 37
        ? (Iec104TypeId)((((byte)type - 30) * 2) + 1)
        : (byte)type is >= 58 and <= 64 ? (Iec104TypeId)((byte)type - 13) : type;

    /// <summary>Element size in octets after the IOA, including quality and time; −1 for unknown types.</summary>
    public static int ElementSize(Iec104TypeId type) => type switch
    {
        Iec104TypeId.SinglePoint or Iec104TypeId.DoublePoint => 1,
        Iec104TypeId.StepPosition => 2,
        Iec104TypeId.Bitstring => 5,
        Iec104TypeId.MeasuredNormalized or Iec104TypeId.MeasuredScaled => 3,
        Iec104TypeId.MeasuredFloat or Iec104TypeId.IntegratedTotals => 5,
        Iec104TypeId.MeasuredNormalizedNoQuality => 2,
        Iec104TypeId.SinglePointTime or Iec104TypeId.DoublePointTime => 8,
        Iec104TypeId.StepPositionTime => 9,
        Iec104TypeId.BitstringTime => 12,
        Iec104TypeId.MeasuredNormalizedTime or Iec104TypeId.MeasuredScaledTime => 10,
        Iec104TypeId.MeasuredFloatTime or Iec104TypeId.IntegratedTotalsTime => 12,
        Iec104TypeId.SingleCommand or Iec104TypeId.DoubleCommand or Iec104TypeId.RegulatingStep => 1,
        Iec104TypeId.SetpointNormalized or Iec104TypeId.SetpointScaled => 3,
        Iec104TypeId.SetpointFloat => 5,
        Iec104TypeId.BitstringCommand => 4,
        Iec104TypeId.SingleCommandTime or Iec104TypeId.DoubleCommandTime or Iec104TypeId.RegulatingStepTime => 8,
        Iec104TypeId.SetpointNormalizedTime or Iec104TypeId.SetpointScaledTime => 10,
        Iec104TypeId.SetpointFloatTime => 12,
        Iec104TypeId.BitstringCommandTime => 11,
        Iec104TypeId.EndOfInitialisation or Iec104TypeId.Interrogation or Iec104TypeId.CounterInterrogation or Iec104TypeId.ResetProcess => 1,
        Iec104TypeId.Read => 0,
        Iec104TypeId.ClockSync => 7,
        Iec104TypeId.TestCommand => 9,
        _ => -1,
    };

    internal static void EncodeElement(Iec104TypeId type, Iec104Object o, Span<byte> d)
    {
        var baseType = WithoutTime(type);
        var q = (byte)((ushort)o.Quality & 0xF1);
        switch (baseType)
        {
            case Iec104TypeId.SinglePoint: d[0] = (byte)((o.Value != 0 ? 1 : 0) | (q & 0xF0)); break;
            case Iec104TypeId.DoublePoint: d[0] = (byte)(((int)o.Value & 3) | (q & 0xF0)); break;
            case Iec104TypeId.StepPosition:
                d[0] = (byte)(((int)Math.Clamp(o.Value, -64, 63) & 0x7F) | (o.Quality.HasFlag(Iec104Quality.Transient) ? 0x80 : 0));
                d[1] = q;
                break;
            case Iec104TypeId.Bitstring: BinaryPrimitives.WriteUInt32LittleEndian(d, (uint)(long)o.Value); d[4] = q; break;
            case Iec104TypeId.MeasuredNormalized: BinaryPrimitives.WriteInt16LittleEndian(d, Normalized(o.Value)); d[2] = q; break;
            case Iec104TypeId.MeasuredNormalizedNoQuality: BinaryPrimitives.WriteInt16LittleEndian(d, Normalized(o.Value)); break;
            case Iec104TypeId.MeasuredScaled: BinaryPrimitives.WriteInt16LittleEndian(d, (short)Math.Clamp(Math.Round(o.Value), short.MinValue, short.MaxValue)); d[2] = q; break;
            case Iec104TypeId.MeasuredFloat: BinaryPrimitives.WriteSingleLittleEndian(d, (float)o.Value); d[4] = q; break;
            case Iec104TypeId.IntegratedTotals:
                BinaryPrimitives.WriteInt32LittleEndian(d, (int)(long)o.Value);
                d[4] = (byte)((o.Qualifier & 0x1F) | (o.Quality.HasFlag(Iec104Quality.Carry) ? 0x20 : 0) | (o.Quality.HasFlag(Iec104Quality.Adjusted) ? 0x40 : 0) | (o.Quality.HasFlag(Iec104Quality.Invalid) ? 0x80 : 0));
                break;
            case Iec104TypeId.SingleCommand: d[0] = (byte)((o.Value != 0 ? 1 : 0) | (o.Qualifier & 0xFC)); break;
            case Iec104TypeId.DoubleCommand or Iec104TypeId.RegulatingStep: d[0] = (byte)(((int)o.Value & 3) | (o.Qualifier & 0xFC)); break;
            case Iec104TypeId.SetpointNormalized: BinaryPrimitives.WriteInt16LittleEndian(d, Normalized(o.Value)); d[2] = o.Qualifier; break;
            case Iec104TypeId.SetpointScaled: BinaryPrimitives.WriteInt16LittleEndian(d, (short)Math.Clamp(Math.Round(o.Value), short.MinValue, short.MaxValue)); d[2] = o.Qualifier; break;
            case Iec104TypeId.SetpointFloat: BinaryPrimitives.WriteSingleLittleEndian(d, (float)o.Value); d[4] = o.Qualifier; break;
            case Iec104TypeId.BitstringCommand: BinaryPrimitives.WriteUInt32LittleEndian(d, (uint)(long)o.Value); break;
            case Iec104TypeId.EndOfInitialisation or Iec104TypeId.Interrogation or Iec104TypeId.CounterInterrogation or Iec104TypeId.ResetProcess: d[0] = o.Qualifier; break;
            case Iec104TypeId.Read or Iec104TypeId.ClockSync: break;
            case Iec104TypeId.TestCommand: BinaryPrimitives.WriteUInt16LittleEndian(d, (ushort)o.Value); break;
            default: throw new ArgumentException($"Type {Mnemonic(type)} is not supported.", nameof(type));
        }

        if (HasTime(type)) (o.Time ?? new Cp56Time2a(DateTime.Now)).Encode(d[(ElementSize(type) - 7)..]);
    }

    internal static Iec104Object DecodeElement(Iec104TypeId type, uint ioa, ReadOnlySpan<byte> d)
    {
        Cp56Time2a? time = HasTime(type) ? Cp56Time2a.Decode(d[(ElementSize(type) - 7)..]) : null;
        static Iec104Quality Q(byte b) => (Iec104Quality)(b & 0xF1);
        return WithoutTime(type) switch
        {
            Iec104TypeId.SinglePoint => new(ioa, d[0] & 1, Q((byte)(d[0] & 0xF0)), 0, time),
            Iec104TypeId.DoublePoint => new(ioa, d[0] & 3, Q((byte)(d[0] & 0xF0)), 0, time),
            Iec104TypeId.StepPosition => new(ioa, (sbyte)(d[0] << 1) >> 1, Q(d[1]) | ((d[0] & 0x80) != 0 ? Iec104Quality.Transient : 0), 0, time),
            Iec104TypeId.Bitstring => new(ioa, BinaryPrimitives.ReadUInt32LittleEndian(d), Q(d[4]), 0, time),
            Iec104TypeId.MeasuredNormalized => new(ioa, BinaryPrimitives.ReadInt16LittleEndian(d) / 32768.0, Q(d[2]), 0, time),
            Iec104TypeId.MeasuredNormalizedNoQuality => new(ioa, BinaryPrimitives.ReadInt16LittleEndian(d) / 32768.0),
            Iec104TypeId.MeasuredScaled => new(ioa, BinaryPrimitives.ReadInt16LittleEndian(d), Q(d[2]), 0, time),
            Iec104TypeId.MeasuredFloat => new(ioa, BinaryPrimitives.ReadSingleLittleEndian(d), Q(d[4]), 0, time),
            Iec104TypeId.IntegratedTotals => new(ioa, BinaryPrimitives.ReadInt32LittleEndian(d),
                ((d[4] & 0x20) != 0 ? Iec104Quality.Carry : 0) | ((d[4] & 0x40) != 0 ? Iec104Quality.Adjusted : 0) | ((d[4] & 0x80) != 0 ? Iec104Quality.Invalid : 0),
                (byte)(d[4] & 0x1F), time),
            Iec104TypeId.SingleCommand => new(ioa, d[0] & 1, 0, (byte)(d[0] & 0xFC), time),
            Iec104TypeId.DoubleCommand or Iec104TypeId.RegulatingStep => new(ioa, d[0] & 3, 0, (byte)(d[0] & 0xFC), time),
            Iec104TypeId.SetpointNormalized => new(ioa, BinaryPrimitives.ReadInt16LittleEndian(d) / 32768.0, 0, d[2], time),
            Iec104TypeId.SetpointScaled => new(ioa, BinaryPrimitives.ReadInt16LittleEndian(d), 0, d[2], time),
            Iec104TypeId.SetpointFloat => new(ioa, BinaryPrimitives.ReadSingleLittleEndian(d), 0, d[4], time),
            Iec104TypeId.BitstringCommand => new(ioa, BinaryPrimitives.ReadUInt32LittleEndian(d), 0, 0, time),
            Iec104TypeId.EndOfInitialisation or Iec104TypeId.Interrogation or Iec104TypeId.CounterInterrogation or Iec104TypeId.ResetProcess => new(ioa, 0, 0, d[0]),
            Iec104TypeId.Read => new(ioa),
            Iec104TypeId.ClockSync => new(ioa, 0, 0, 0, time),
            Iec104TypeId.TestCommand => new(ioa, BinaryPrimitives.ReadUInt16LittleEndian(d), 0, 0, time),
            _ => throw new ProtocolException($"Type {(byte)type} is not supported."),
        };
    }

    private static short Normalized(double v) => (short)Math.Clamp(Math.Round(v * 32768), short.MinValue, short.MaxValue);
}

/// <summary>An application service data unit.</summary>
/// <param name="Type">Type identification.</param>
/// <param name="Cause">Cause of transmission (6 bits).</param>
/// <param name="CommonAddress">Common address of ASDU (station address).</param>
/// <param name="Objects">Information objects (up to 127).</param>
/// <param name="Sequence">SQ = 1: one IOA followed by consecutive elements.</param>
/// <param name="Negative">P/N: negative confirmation.</param>
/// <param name="Test">T: test frame.</param>
/// <param name="Originator">Originator address.</param>
public sealed record Iec104Asdu(Iec104TypeId Type, Iec104Cause Cause, ushort CommonAddress, IReadOnlyList<Iec104Object> Objects, bool Sequence = false, bool Negative = false, bool Test = false, byte Originator = 0)
{
    /// <summary>Largest ASDU (APDU length 253 − 4 control octets).</summary>
    public const int MaxLength = 249;

    /// <summary>Encodes the ASDU.</summary>
    /// <exception cref="ArgumentException">Unknown type, too many objects, or the ASDU exceeds 249 octets.</exception>
    public byte[] Encode()
    {
        var size = Iec104Types.ElementSize(Type);
        if (size < 0) throw new ArgumentException($"Type {Iec104Types.Mnemonic(Type)} is not supported.");
        if (Objects.Count is 0 or > 127) throw new ArgumentException("An ASDU carries 1 to 127 information objects.");
        var length = 6 + (Sequence ? 3 + (Objects.Count * size) : Objects.Count * (3 + size));
        if (length > MaxLength) throw new ArgumentException($"The ASDU is {length} octets; the maximum is {MaxLength}.");
        var b = new byte[length];
        b[0] = (byte)Type;
        b[1] = (byte)((Sequence ? 0x80 : 0) | Objects.Count);
        b[2] = (byte)(((byte)Cause & 0x3F) | (Negative ? 0x40 : 0) | (Test ? 0x80 : 0));
        b[3] = Originator;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), CommonAddress);
        var p = 6;
        for (var i = 0; i < Objects.Count; i++)
        {
            if (!Sequence || i == 0)
            {
                WriteIoa(b.AsSpan(p), Objects[i].Address);
                p += 3;
            }

            Iec104Types.EncodeElement(Type, Objects[i], b.AsSpan(p, size));
            p += size;
        }

        return b;
    }

    /// <summary>Reads the 6-octet header (also for unknown types, so a station can answer "unknown type").</summary>
    /// <exception cref="ProtocolException">Shorter than 6 octets.</exception>
    public static Iec104Asdu ParseHeader(ReadOnlySpan<byte> d)
    {
        if (d.Length < 6) throw new ProtocolException("ASDU shorter than its 6-octet header.");
        return new Iec104Asdu((Iec104TypeId)d[0], (Iec104Cause)(d[2] & 0x3F), BinaryPrimitives.ReadUInt16LittleEndian(d[4..]), [], (d[1] & 0x80) != 0, (d[2] & 0x40) != 0, (d[2] & 0x80) != 0, d[3]);
    }

    /// <summary>Parses an ASDU.</summary>
    /// <exception cref="ProtocolException">Unknown type, wrong length or invalid time.</exception>
    public static Iec104Asdu Parse(ReadOnlySpan<byte> d)
    {
        var h = ParseHeader(d);
        var size = Iec104Types.ElementSize(h.Type);
        if (size < 0) throw new ProtocolException($"Unknown type identification {(byte)h.Type}.");
        var count = d[1] & 0x7F;
        if (count == 0) throw new ProtocolException("ASDU without information objects.");
        var expected = 6 + (h.Sequence ? 3 + (count * size) : count * (3 + size));
        if (d.Length != expected) throw new ProtocolException($"ASDU length {d.Length} does not match {count} × {Iec104Types.Mnemonic(h.Type)} ({expected}).");
        var objects = new Iec104Object[count];
        var p = 6;
        uint ioa = 0;
        for (var i = 0; i < count; i++)
        {
            if (!h.Sequence || i == 0)
            {
                ioa = ReadIoa(d[p..]);
                p += 3;
            }
            else
            {
                ioa = (ioa + 1) & 0xFF_FFFF;
            }

            objects[i] = Iec104Types.DecodeElement(h.Type, ioa, d.Slice(p, size));
            p += size;
        }

        return h with { Objects = objects };
    }

    internal static void WriteIoa(Span<byte> d, uint ioa)
    {
        d[0] = (byte)ioa;
        d[1] = (byte)(ioa >> 8);
        d[2] = (byte)(ioa >> 16);
    }

    internal static uint ReadIoa(ReadOnlySpan<byte> d) => (uint)(d[0] | (d[1] << 8) | (d[2] << 16));

    /// <summary>Cause as text, including interrogation groups.</summary>
    public static string CauseName(Iec104Cause cause) => (byte)cause switch
    {
        >= 21 and <= 36 => string.Create(CultureInfo.InvariantCulture, $"interrogated by group {(byte)cause - 20}"),
        >= 38 and <= 41 => string.Create(CultureInfo.InvariantCulture, $"requested by counter group {(byte)cause - 37}"),
        _ => cause switch
        {
            Iec104Cause.Periodic => "periodic", Iec104Cause.Background => "background", Iec104Cause.Spontaneous => "spontaneous",
            Iec104Cause.Initialised => "initialised", Iec104Cause.Request => "request", Iec104Cause.Activation => "activation",
            Iec104Cause.ActivationConfirmation => "activation confirmation", Iec104Cause.Deactivation => "deactivation",
            Iec104Cause.DeactivationConfirmation => "deactivation confirmation", Iec104Cause.ActivationTermination => "activation termination",
            Iec104Cause.ReturnRemote => "return (remote)", Iec104Cause.ReturnLocal => "return (local)", Iec104Cause.File => "file transfer",
            Iec104Cause.InterrogatedByStation => "interrogated by station", Iec104Cause.RequestedByGeneralCounter => "requested by general counter",
            Iec104Cause.UnknownType => "unknown type", Iec104Cause.UnknownCause => "unknown cause", Iec104Cause.UnknownCommonAddress => "unknown common address",
            Iec104Cause.UnknownObjectAddress => "unknown object address",
            _ => string.Create(CultureInfo.InvariantCulture, $"cause {(byte)cause}"),
        },
    };

    /// <inheritdoc />
    public override string ToString() =>
        $"{Iec104Types.Mnemonic(Type)} {CauseName(Cause)}{(Negative ? " NEGATIVE" : "")}{(Test ? " TEST" : "")} CA {CommonAddress}: {string.Join("; ", Objects)}";
}

/// <summary>APCI frame formats.</summary>
public enum Iec104Format
{
    /// <summary>Information transfer (numbered, carries an ASDU).</summary>
    I,
    /// <summary>Numbered supervisory (acknowledgement only).</summary>
    S,
    /// <summary>Unnumbered control (STARTDT, STOPDT, TESTFR).</summary>
    U,
}

/// <summary>U-format functions.</summary>
public enum Iec104UFunction : byte
{
    /// <summary>Not a U frame.</summary>
    None = 0,
    /// <summary>STARTDT act.</summary>
    StartDtActivation = 0x07,
    /// <summary>STARTDT con.</summary>
    StartDtConfirmation = 0x0B,
    /// <summary>STOPDT act.</summary>
    StopDtActivation = 0x13,
    /// <summary>STOPDT con.</summary>
    StopDtConfirmation = 0x23,
    /// <summary>TESTFR act.</summary>
    TestFrActivation = 0x43,
    /// <summary>TESTFR con.</summary>
    TestFrConfirmation = 0x83,
}

/// <summary>An application protocol data unit: start 0x68, length, four control octets and, for I frames, an ASDU.</summary>
/// <param name="Format">I, S or U.</param>
/// <param name="SendSequence">N(S), 15 bits (I frames).</param>
/// <param name="ReceiveSequence">N(R), 15 bits (I and S frames).</param>
/// <param name="Function">U function.</param>
/// <param name="Asdu">ASDU bytes (I frames).</param>
public readonly record struct Iec104Apdu(Iec104Format Format, ushort SendSequence, ushort ReceiveSequence, Iec104UFunction Function, ReadOnlyMemory<byte> Asdu)
{
    /// <summary>Start octet.</summary>
    public const byte Start = 0x68;

    /// <summary>Default TCP port.</summary>
    public const int DefaultPort = 2404;

    /// <summary>An I frame.</summary>
    public static Iec104Apdu I(ushort sendSequence, ushort receiveSequence, ReadOnlyMemory<byte> asdu) => new(Iec104Format.I, sendSequence, receiveSequence, 0, asdu);

    /// <summary>An S frame.</summary>
    public static Iec104Apdu S(ushort receiveSequence) => new(Iec104Format.S, 0, receiveSequence, 0, default);

    /// <summary>A U frame.</summary>
    public static Iec104Apdu U(Iec104UFunction function) => new(Iec104Format.U, 0, 0, function, default);

    /// <summary>Encodes the APDU.</summary>
    public byte[] Encode()
    {
        if (Asdu.Length > Iec104Asdu.MaxLength) throw new ArgumentException("ASDU longer than 249 octets.");
        var b = new byte[6 + Asdu.Length];
        b[0] = Start;
        b[1] = (byte)(4 + Asdu.Length);
        switch (Format)
        {
            case Iec104Format.I:
                BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), (ushort)((SendSequence & 0x7FFF) << 1));
                BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), (ushort)((ReceiveSequence & 0x7FFF) << 1));
                Asdu.Span.CopyTo(b.AsSpan(6));
                break;
            case Iec104Format.S:
                b[2] = 0x01;
                BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), (ushort)((ReceiveSequence & 0x7FFF) << 1));
                break;
            default:
                b[2] = (byte)Function;
                break;
        }

        return b;
    }

    /// <summary>Parses a complete APDU (as returned by <see cref="Iec104Framing"/>).</summary>
    /// <exception cref="ProtocolException">Malformed APCI.</exception>
    public static Iec104Apdu Parse(ReadOnlySpan<byte> d)
    {
        if (d.Length < 6 || d[0] != Start || d[1] != d.Length - 2) throw new ProtocolException("Malformed APCI.");
        var c1 = d[2];
        if ((c1 & 1) == 0)
        {
            if ((d[4] & 1) != 0) throw new ProtocolException("Malformed I-format control field.");
            if (d.Length == 6) throw new ProtocolException("I frame without an ASDU.");
            return I((ushort)(BinaryPrimitives.ReadUInt16LittleEndian(d[2..]) >> 1), (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(d[4..]) >> 1), d[6..].ToArray());
        }

        if (d.Length != 6) throw new ProtocolException("S and U frames carry no ASDU.");
        if ((c1 & 3) == 1)
        {
            if (c1 != 1 || d[3] != 0 || (d[4] & 1) != 0) throw new ProtocolException("Malformed S-format control field.");
            return S((ushort)(BinaryPrimitives.ReadUInt16LittleEndian(d[4..]) >> 1));
        }

        var f = (Iec104UFunction)c1;
        if (f is not (Iec104UFunction.StartDtActivation or Iec104UFunction.StartDtConfirmation or Iec104UFunction.StopDtActivation
            or Iec104UFunction.StopDtConfirmation or Iec104UFunction.TestFrActivation or Iec104UFunction.TestFrConfirmation) || d[3] != 0 || d[4] != 0 || d[5] != 0)
            throw new ProtocolException($"Malformed U-format control field 0x{c1:X2}.");
        return U(f);
    }

    /// <summary>The U function as text.</summary>
    public static string FunctionName(Iec104UFunction f) => f switch
    {
        Iec104UFunction.StartDtActivation => "STARTDT act", Iec104UFunction.StartDtConfirmation => "STARTDT con",
        Iec104UFunction.StopDtActivation => "STOPDT act", Iec104UFunction.StopDtConfirmation => "STOPDT con",
        Iec104UFunction.TestFrActivation => "TESTFR act", Iec104UFunction.TestFrConfirmation => "TESTFR con",
        _ => "?",
    };

    /// <inheritdoc />
    public override string ToString() => Format switch
    {
        Iec104Format.I => $"I(N(S)={SendSequence}, N(R)={ReceiveSequence}) {SafeAsdu(Asdu.Span)}",
        Iec104Format.S => $"S(N(R)={ReceiveSequence})",
        _ => $"U({FunctionName(Function)})",
    };

    private static string SafeAsdu(ReadOnlySpan<byte> d)
    {
        try { return Iec104Asdu.Parse(d).ToString(); }
        catch (ProtocolException ex) { return ex.Message; }
    }

    /// <summary>The frame lane of a complete APDU.</summary>
    public static IReadOnlyList<FrameField> Describe(ReadOnlySpan<byte> d)
    {
        var f = new List<FrameField>();
        if (d.Length < 2) return [new FrameField("Bytes", 0, d.Length, FrameFieldKind.Error, "too short")];
        f.Add(new("Start", 0, 1, FrameFieldKind.Header, "0x68"));
        f.Add(new("Length", 1, 1, FrameFieldKind.Length, d[1].ToString(CultureInfo.InvariantCulture)));
        Iec104Apdu apdu;
        try { apdu = Parse(d); }
        catch (ProtocolException ex)
        {
            f.Add(new("APCI", 2, d.Length - 2, FrameFieldKind.Error, ex.Message));
            return f;
        }

        f.Add(new("Control", 2, 4, FrameFieldKind.Function, apdu.Format switch
        {
            Iec104Format.I => $"I N(S)={apdu.SendSequence} N(R)={apdu.ReceiveSequence}",
            Iec104Format.S => $"S N(R)={apdu.ReceiveSequence}",
            _ => "U " + FunctionName(apdu.Function),
        }));
        if (apdu.Format != Iec104Format.I) return f;
        var a = d[6..];
        if (a.Length < 6)
        {
            f.Add(new("ASDU", 6, a.Length, FrameFieldKind.Error, "short ASDU"));
            return f;
        }

        var h = Iec104Asdu.ParseHeader(a);
        f.Add(new("Type", 6, 1, FrameFieldKind.Function, $"{Iec104Types.Mnemonic(h.Type)} ({(byte)h.Type})"));
        f.Add(new("VSQ", 7, 1, FrameFieldKind.Length, $"SQ={(h.Sequence ? 1 : 0)} n={a[1] & 0x7F}"));
        f.Add(new("COT", 8, 1, h.Negative ? FrameFieldKind.Error : FrameFieldKind.Function, Iec104Asdu.CauseName(h.Cause) + (h.Negative ? " (negative)" : "") + (h.Test ? " (test)" : "")));
        f.Add(new("ORG", 9, 1, FrameFieldKind.Address, a[3].ToString(CultureInfo.InvariantCulture)));
        f.Add(new("CA", 10, 2, FrameFieldKind.Address, h.CommonAddress.ToString(CultureInfo.InvariantCulture)));
        Iec104Asdu asdu;
        try { asdu = Iec104Asdu.Parse(a); }
        catch (ProtocolException ex)
        {
            f.Add(new("Objects", 12, d.Length - 12, FrameFieldKind.Error, ex.Message));
            return f;
        }

        var size = Iec104Types.ElementSize(asdu.Type);
        var p = 12;
        for (var i = 0; i < asdu.Objects.Count; i++)
        {
            var o = asdu.Objects[i];
            if (!asdu.Sequence || i == 0)
            {
                f.Add(new("IOA", p, 3, FrameFieldKind.Address, o.Address.ToString(CultureInfo.InvariantCulture)));
                p += 3;
            }

            var timeLen = Iec104Types.HasTime(asdu.Type) ? 7 : 0;
            if (size - timeLen > 0)
                f.Add(new("Value", p, size - timeLen, FrameFieldKind.Data, o.Value.ToString("0.#####", CultureInfo.InvariantCulture) + (o.Quality != 0 ? $" {o.Quality}" : "") + (o.Qualifier != 0 ? $" q=0x{o.Qualifier:X2}" : "")));
            if (timeLen > 0) f.Add(new("Time", p + size - 7, 7, FrameFieldKind.Data, o.Time?.ToString()));
            p += size;
        }

        return f;
    }
}

/// <summary>Splits a TCP byte stream into complete APDUs (0x68, length 4–253, body). Skips noise before a start octet.</summary>
public sealed class Iec104Framing : IFrameDecoder
{
    /// <inheritdoc />
    public FrameDecodeStatus TryDecode(ref ReadOnlySequence<byte> buffer, IBufferWriter<byte> payload)
    {
        var reader = new SequenceReader<byte>(buffer);
        if (!reader.TryAdvanceTo(Iec104Apdu.Start, advancePastDelimiter: false))
        {
            buffer = buffer.Slice(buffer.End);
            return FrameDecodeStatus.NeedMoreData;
        }

        var start = reader.Position;
        if (reader.Remaining < 2)
        {
            buffer = buffer.Slice(start);
            return FrameDecodeStatus.NeedMoreData;
        }

        reader.Advance(1);
        reader.TryRead(out var length);
        if (length is < 4 or > 253)
        {
            buffer = buffer.Slice(buffer.GetPosition(1, start));
            return FrameDecodeStatus.Invalid;
        }

        if (reader.Remaining < length)
        {
            buffer = buffer.Slice(start);
            return FrameDecodeStatus.NeedMoreData;
        }

        var frame = buffer.Slice(start, length + 2);
        foreach (var seg in frame) payload.Write(seg.Span);
        buffer = buffer.Slice(frame.End);
        return FrameDecodeStatus.Frame;
    }
}
