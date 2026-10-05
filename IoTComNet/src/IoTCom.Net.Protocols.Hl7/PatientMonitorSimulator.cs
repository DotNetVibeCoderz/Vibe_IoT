using System.Globalization;

namespace IoTCom.Net.Protocols.Hl7;

/// <summary>Clinical course followed by a simulated patient.</summary>
public enum PatientScenario
{
    /// <summary>Stable, normal ranges with natural variability.</summary>
    Stable,
    /// <summary>Evolving sepsis: rising heart rate, respiratory rate and temperature, falling blood pressure.</summary>
    Sepsis,
    /// <summary>Respiratory deterioration: falling SpO2, rising respiratory and heart rate.</summary>
    Hypoxia,
    /// <summary>Hypertensive episode: rising blood pressure.</summary>
    Hypertension,
}

/// <summary>One set of vital signs.</summary>
/// <param name="Time">Measurement time.</param>
/// <param name="HeartRate">/min.</param>
/// <param name="RespiratoryRate">/min.</param>
/// <param name="SpO2">%.</param>
/// <param name="Systolic">mm Hg.</param>
/// <param name="Diastolic">mm Hg.</param>
/// <param name="Temperature">°C.</param>
public readonly record struct VitalsSample(DateTimeOffset Time, double HeartRate, double RespiratoryRate, double SpO2, double Systolic, double Diastolic, double Temperature);

/// <summary>
/// A synthetic bedside monitor. Produces physiologically plausible vitals that follow a <see cref="PatientScenario"/>
/// and encodes them as HL7 v2.5.1 ORU^R01 (and ADT^A01 on admission). Deterministic for a given seed.
/// All patients are fictional; the data is for software testing only, never for clinical use.
/// </summary>
public sealed class PatientMonitorSimulator
{
    private readonly Random _rng;
    private double _t;

    /// <summary>Creates a simulator.</summary>
    public PatientMonitorSimulator(Hl7Patient patient, PatientScenario scenario, string bed, int seed = 1, TimeSpan? onset = null)
    {
        Patient = patient;
        Scenario = scenario;
        Bed = bed;
        Onset = onset ?? TimeSpan.FromMinutes(2);
        _rng = new Random(seed);
    }

    /// <summary>The fictional patient.</summary>
    public Hl7Patient Patient { get; }
    /// <summary>Scenario.</summary>
    public PatientScenario Scenario { get; }
    /// <summary>Bed / location (PV1-3).</summary>
    public string Bed { get; }
    /// <summary>Time at which the deterioration starts.</summary>
    public TimeSpan Onset { get; }
    /// <summary>Simulated time since admission.</summary>
    public TimeSpan Elapsed => TimeSpan.FromSeconds(_t);

    /// <summary>Advances simulated time and returns the next sample.</summary>
    public VitalsSample Next(TimeSpan step, DateTimeOffset now)
    {
        _t += step.TotalSeconds;
        // 0 → 1 over 10 simulated minutes after onset
        var p = Math.Clamp((_t - Onset.TotalSeconds) / 600.0, 0, 1);
        double N(double sd) => Gaussian() * sd;
        var breath = Math.Sin(_t / 4.0);

        double hr = 74, rr = 15, spo2 = 98, sys = 122, dia = 78, temp = 36.8;
        switch (Scenario)
        {
            case PatientScenario.Sepsis:
                hr += 44 * p; rr += 11 * p; temp += 2.2 * p; sys -= 34 * p; dia -= 22 * p; spo2 -= 4 * p;
                break;
            case PatientScenario.Hypoxia:
                spo2 -= 12 * p; rr += 13 * p; hr += 26 * p;
                break;
            case PatientScenario.Hypertension:
                sys += 58 * p; dia += 28 * p; hr += 8 * p;
                break;
        }
        return new VitalsSample(now,
            Math.Round(hr + N(2.2) + breath, 0),
            Math.Round(rr + N(0.8), 0),
            Math.Round(Math.Min(100, spo2 + N(0.6)), 0),
            Math.Round(sys + N(3), 0),
            Math.Round(dia + N(2), 0),
            Math.Round(temp + N(0.05), 1));
    }

    /// <summary>ADT^A01 admission message.</summary>
    public Hl7Message Admission(DateTimeOffset now) => new Hl7MessageBuilder()
        .Header("MONITOR", $"ICU-{Bed}", "IOTCOM", "HOSPITAL", "ADT^A01", timestamp: now)
        .Segment("EVN", "A01", Hl7Time.Format(now))
        .Patient(Patient)
        .Segment("PV1", "1", "I", $"ICU^{Bed}^1")
        .Build();

    /// <summary>Encodes a sample as ORU^R01 with LOINC-coded OBX segments and abnormal flags.</summary>
    public Hl7Message ToOru(VitalsSample v)
    {
        var b = new Hl7MessageBuilder()
            .Header("MONITOR", $"ICU-{Bed}", "IOTCOM", "HOSPITAL", "ORU^R01", timestamp: v.Time)
            .Patient(Patient)
            .Segment("PV1", "1", "I", $"ICU^{Bed}^1")
            .Segment("OBR", "1", null, null, "VITALS^Vital signs panel^L", null, null, Hl7Time.Format(v.Time));
        var i = 1;
        void Add(Hl7Code code, double value, string units, string range, double low, double high, string format = "0")
        {
            var flag = value < low ? "L" : value > high ? "H" : "N";
            b.Observation(i++, new Hl7Observation
            {
                Code = code,
                Value = value.ToString(format, CultureInfo.InvariantCulture),
                Units = units,
                ReferenceRange = range,
                AbnormalFlag = flag,
                Timestamp = v.Time,
            });
        }
        Add(VitalSigns.HeartRate, v.HeartRate, "/min", "60-100", 60, 100);
        Add(VitalSigns.RespiratoryRate, v.RespiratoryRate, "/min", "12-20", 12, 20);
        Add(VitalSigns.SpO2, v.SpO2, "%", "95-100", 95, 100);
        Add(VitalSigns.SystolicBp, v.Systolic, "mm[Hg]", "90-140", 90, 140);
        Add(VitalSigns.DiastolicBp, v.Diastolic, "mm[Hg]", "60-90", 60, 90);
        Add(VitalSigns.Temperature, v.Temperature, "Cel", "36.1-37.8", 36.1, 37.8, "0.0");
        return b.Build();
    }

    /// <summary>Reads a vitals sample back from an ORU^R01 (missing values become NaN).</summary>
    public static VitalsSample FromOru(Hl7Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var obs = message.GetObservations();
        double Get(Hl7Code code) => obs.FirstOrDefault(o => o.Code.Identifier == code.Identifier)?.NumericValue ?? double.NaN;
        return new VitalsSample((obs.Count > 0 ? obs[0].Timestamp : null) ?? message.Timestamp ?? DateTimeOffset.Now,
            Get(VitalSigns.HeartRate), Get(VitalSigns.RespiratoryRate), Get(VitalSigns.SpO2),
            Get(VitalSigns.SystolicBp), Get(VitalSigns.DiastolicBp), Get(VitalSigns.Temperature));
    }

    /// <summary>A small cast of fictional patients for demos.</summary>
    public static IReadOnlyList<(Hl7Patient Patient, PatientScenario Scenario, string Bed)> DemoWard { get; } =
    [
        (new Hl7Patient { Id = "MRN-1001", AssigningAuthority = "IOTCOM", FamilyName = "Santoso", GivenName = "Budi", BirthDate = new DateOnly(1958, 4, 12), Sex = "M" }, PatientScenario.Sepsis, "01"),
        (new Hl7Patient { Id = "MRN-1002", AssigningAuthority = "IOTCOM", FamilyName = "Wulandari", GivenName = "Sari", BirthDate = new DateOnly(1971, 9, 3), Sex = "F" }, PatientScenario.Stable, "02"),
        (new Hl7Patient { Id = "MRN-1003", AssigningAuthority = "IOTCOM", FamilyName = "Hartono", GivenName = "Agus", BirthDate = new DateOnly(1949, 1, 27), Sex = "M" }, PatientScenario.Hypoxia, "03"),
        (new Hl7Patient { Id = "MRN-1004", AssigningAuthority = "IOTCOM", FamilyName = "Pratiwi", GivenName = "Dewi", BirthDate = new DateOnly(1965, 6, 18), Sex = "F" }, PatientScenario.Hypertension, "04"),
    ];

    private double Gaussian()
    {
        var u1 = 1.0 - _rng.NextDouble();
        var u2 = _rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }
}
