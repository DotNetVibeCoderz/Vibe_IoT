using System.Globalization;

namespace IoTCom.Net.Protocols.Hl7;

/// <summary>A coded element (CE/CWE): identifier, text and coding system (e.g. LOINC).</summary>
/// <param name="Identifier">Code, e.g. <c>8867-4</c>.</param>
/// <param name="Text">Display text, e.g. <c>Heart rate</c>.</param>
/// <param name="System">Coding system, e.g. <c>LN</c> (LOINC).</param>
public readonly record struct Hl7Code(string Identifier, string Text, string System = "LN");

/// <summary>An observation from an OBX segment.</summary>
public sealed record Hl7Observation
{
    /// <summary>OBX-2 value type (<c>NM</c> numeric, <c>ST</c> string, <c>CWE</c> coded…).</summary>
    public string ValueType { get; init; } = "NM";
    /// <summary>OBX-3 observation identifier.</summary>
    public Hl7Code Code { get; init; }
    /// <summary>OBX-5 value (as text).</summary>
    public string Value { get; init; } = string.Empty;
    /// <summary>OBX-6 units (identifier of the CE, e.g. <c>/min</c>, <c>%</c>, <c>mm[Hg]</c>).</summary>
    public string Units { get; init; } = string.Empty;
    /// <summary>OBX-7 reference range, e.g. <c>60-100</c>.</summary>
    public string ReferenceRange { get; init; } = string.Empty;
    /// <summary>OBX-8 abnormal flag (<c>N</c>, <c>H</c>, <c>L</c>, <c>HH</c>, <c>LL</c>, <c>A</c>).</summary>
    public string AbnormalFlag { get; init; } = string.Empty;
    /// <summary>OBX-11 result status (<c>F</c> final, <c>P</c> preliminary, <c>R</c> results entered).</summary>
    public string Status { get; init; } = "F";
    /// <summary>OBX-14 observation time.</summary>
    public DateTimeOffset? Timestamp { get; init; }

    /// <summary>Numeric value when <see cref="ValueType"/> is numeric and parsable.</summary>
    public double? NumericValue => double.TryParse(Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
}

/// <summary>Patient identity from PID.</summary>
public sealed record Hl7Patient
{
    /// <summary>PID-3.1 patient identifier (MRN).</summary>
    public string Id { get; init; } = string.Empty;
    /// <summary>PID-3.4 assigning authority.</summary>
    public string AssigningAuthority { get; init; } = string.Empty;
    /// <summary>PID-5.1 family name.</summary>
    public string FamilyName { get; init; } = string.Empty;
    /// <summary>PID-5.2 given name.</summary>
    public string GivenName { get; init; } = string.Empty;
    /// <summary>PID-7 date of birth.</summary>
    public DateOnly? BirthDate { get; init; }
    /// <summary>PID-8 administrative sex (<c>F</c>, <c>M</c>, <c>U</c>…).</summary>
    public string Sex { get; init; } = string.Empty;

    /// <summary>"Family, Given".</summary>
    public string DisplayName => GivenName.Length > 0 ? $"{FamilyName}, {GivenName}" : FamilyName;

    /// <summary>Age in whole years on <paramref name="on"/>.</summary>
    public int? AgeOn(DateOnly on)
    {
        if (BirthDate is not { } b) return null;
        var age = on.Year - b.Year;
        if (on < b.AddYears(age)) age--;
        return age;
    }
}

/// <summary>Common LOINC codes for vital signs (as used by bedside monitors in ORU^R01).</summary>
public static class VitalSigns
{
    /// <summary>Heart rate, /min.</summary>
    public static readonly Hl7Code HeartRate = new("8867-4", "Heart rate");
    /// <summary>Respiratory rate, /min.</summary>
    public static readonly Hl7Code RespiratoryRate = new("9279-1", "Respiratory rate");
    /// <summary>Oxygen saturation (pulse oximetry), %.</summary>
    public static readonly Hl7Code SpO2 = new("59408-5", "Oxygen saturation by pulse oximetry");
    /// <summary>Systolic blood pressure, mm[Hg].</summary>
    public static readonly Hl7Code SystolicBp = new("8480-6", "Systolic blood pressure");
    /// <summary>Diastolic blood pressure, mm[Hg].</summary>
    public static readonly Hl7Code DiastolicBp = new("8462-4", "Diastolic blood pressure");
    /// <summary>Body temperature, Cel.</summary>
    public static readonly Hl7Code Temperature = new("8310-5", "Body temperature");
}

/// <summary>Typed accessors on <see cref="Hl7Message"/>.</summary>
public static class Hl7MessageExtensions
{
    /// <summary>All OBX observations in order.</summary>
    public static IReadOnlyList<Hl7Observation> GetObservations(this Hl7Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.GetSegments("OBX").Select(s => new Hl7Observation
        {
            ValueType = s.Get(2),
            Code = new Hl7Code(s.Get(3, 1), s.Get(3, 2), s.Get(3, 3)),
            Value = s.Get(5),
            Units = s.Get(6, 1),
            ReferenceRange = s.Get(7),
            AbnormalFlag = s.Get(8),
            Status = s.Get(11),
            Timestamp = Hl7Time.Parse(s.Get(14)),
        }).ToArray();
    }

    /// <summary>The patient from PID, or null when absent.</summary>
    public static Hl7Patient? GetPatient(this Hl7Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var pid = message.GetSegments("PID").FirstOrDefault();
        if (pid is null) return null;
        return new Hl7Patient
        {
            Id = pid.Get(3, 1),
            AssigningAuthority = pid.Get(3, 4),
            FamilyName = pid.Get(5, 1),
            GivenName = pid.Get(5, 2),
            BirthDate = DateOnly.TryParseExact(pid.Get(7) is { Length: >= 8 } d ? d[..8] : "", "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var b) ? b : null,
            Sex = pid.Get(8),
        };
    }

    /// <summary>The acknowledgement code (MSA-1) of an ACK message, or empty.</summary>
    public static string AckCode(this Hl7Message message) => message.Get("MSA.1");

    /// <summary>True for AA/CA acknowledgements.</summary>
    public static bool IsPositiveAck(this Hl7Message message) => message.AckCode() is "AA" or "CA";
}
