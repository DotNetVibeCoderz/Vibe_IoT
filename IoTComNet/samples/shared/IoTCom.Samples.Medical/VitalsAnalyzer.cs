using IoTCom.Net.Protocols.Hl7;

namespace IoTCom.Samples.Medical;

/// <summary>NEWS2 clinical risk band.</summary>
public enum RiskLevel
{
    /// <summary>Aggregate 0–4.</summary>
    Low,
    /// <summary>A single parameter scoring 3 (urgent ward-based response).</summary>
    LowMedium,
    /// <summary>Aggregate 5–6.</summary>
    Medium,
    /// <summary>Aggregate 7 or more.</summary>
    High,
}

/// <summary>NEWS2 result with per-parameter sub-scores.</summary>
public sealed record News2Score(int Total, RiskLevel Risk, IReadOnlyDictionary<string, int> Parts);

/// <summary>Trend of one vital sign over the analysis window.</summary>
/// <param name="Name">Vital name.</param>
/// <param name="Latest">Latest value.</param>
/// <param name="SlopePerHour">Least-squares slope, units per hour.</param>
/// <param name="ZScore">Deviation of the latest value from its EWMA baseline, in baseline standard deviations.</param>
/// <param name="Projected15Min">Linear projection 15 minutes ahead.</param>
public sealed record VitalTrend(string Name, double Latest, double SlopePerHour, double ZScore, double Projected15Min);

/// <summary>Everything the dashboard and the assistant need about one patient.</summary>
public sealed record ClinicalSnapshot(
    Hl7Patient Patient,
    string Bed,
    VitalsSample Latest,
    News2Score News2,
    News2Score ProjectedNews2,
    IReadOnlyList<VitalTrend> Trends,
    IReadOnlyList<string> Alerts,
    int Samples,
    TimeSpan Window);

/// <summary>
/// Deterministic early-warning analytics for streamed vitals:
/// <list type="bullet">
/// <item>NEWS2 (Royal College of Physicians, 2017; SpO2 scale 1, assumes room air and alert patient — the monitor feed
/// does not carry oxygen therapy or ACVPU);</item>
/// <item>per-vital least-squares trend and a 15-minute linear projection, re-scored as a projected NEWS2;</item>
/// <item>EWMA baseline with z-score anomaly detection (flags sudden departures from the patient's own baseline).</item>
/// </list>
/// Illustration for software demos with synthetic data — not a medical device.
/// </summary>
public sealed class VitalsAnalyzer(int windowSize = 240)
{
    private readonly Queue<VitalsSample> _window = new();
    private readonly Dictionary<string, (double Mean, double Var, bool Init)> _ewma = [];
    private const double Alpha = 0.05;

    /// <summary>Samples currently in the window.</summary>
    public int Count => _window.Count;

    /// <summary>Adds a sample.</summary>
    public void Add(VitalsSample sample)
    {
        _window.Enqueue(sample);
        while (_window.Count > windowSize) _window.Dequeue();
    }

    /// <summary>All samples in the window, oldest first.</summary>
    public IReadOnlyList<VitalsSample> History => [.. _window];

    /// <summary>Computes the snapshot for the latest sample.</summary>
    public ClinicalSnapshot Analyze(Hl7Patient patient, string bed)
    {
        var samples = _window.ToArray();
        if (samples.Length == 0) throw new InvalidOperationException("No samples yet.");
        var latest = samples[^1];
        var t0 = samples[0].Time;
        var trends = new List<VitalTrend>();
        VitalTrend Trend(string name, Func<VitalsSample, double> f)
        {
            var xs = samples.Select(s => (s.Time - t0).TotalHours).ToArray();
            var ys = samples.Select(f).ToArray();
            var slope = Slope(xs, ys);
            var last = ys[^1];
            var z = UpdateEwma(name, last);
            var t = new VitalTrend(name, last, slope, z, last + slope * 0.25);
            trends.Add(t);
            return t;
        }
        var hr = Trend("Heart rate", s => s.HeartRate);
        var rr = Trend("Respiratory rate", s => s.RespiratoryRate);
        var sp = Trend("SpO2", s => s.SpO2);
        var sys = Trend("Systolic BP", s => s.Systolic);
        Trend("Diastolic BP", s => s.Diastolic);
        var temp = Trend("Temperature", s => s.Temperature);

        var news = News2(latest.RespiratoryRate, latest.SpO2, latest.Systolic, latest.HeartRate, latest.Temperature);
        var projected = News2(rr.Projected15Min, Math.Min(100, sp.Projected15Min), sys.Projected15Min, hr.Projected15Min, temp.Projected15Min);

        var alerts = new List<string>();
        if (news.Risk == RiskLevel.High) alerts.Add($"NEWS2 {news.Total}: HIGH risk — emergency assessment by a critical-care team.");
        else if (news.Risk == RiskLevel.Medium) alerts.Add($"NEWS2 {news.Total}: MEDIUM risk — urgent review by a clinician.");
        else if (news.Risk == RiskLevel.LowMedium) alerts.Add($"NEWS2 {news.Total} with a single parameter scoring 3 — urgent ward-based review.");
        if (projected.Total >= news.Total + 2) alerts.Add($"Trajectory: projected NEWS2 {projected.Total} in 15 min (now {news.Total}).");
        foreach (var t in trends.Where(t => Math.Abs(t.ZScore) >= 3 && samples.Length > 20))
            alerts.Add($"{t.Name} {t.Latest:0.#} deviates {t.ZScore:+0.0;-0.0} SD from this patient's baseline.");
        if (sp.Latest <= 91) alerts.Add($"SpO2 {sp.Latest:0}% — hypoxaemia.");
        if (sys.Latest <= 90) alerts.Add($"Systolic BP {sys.Latest:0} mmHg — hypotension.");
        if (sys.Latest >= 180) alerts.Add($"Systolic BP {sys.Latest:0} mmHg — severe hypertension.");

        return new ClinicalSnapshot(patient, bed, latest, news, projected, trends, alerts, samples.Length, samples[^1].Time - t0);
    }

    /// <summary>NEWS2 aggregate score (room air, alert).</summary>
    public static News2Score News2(double rr, double spo2, double systolic, double pulse, double temp)
    {
        var parts = new Dictionary<string, int>
        {
            ["Respiration rate"] = rr switch { <= 8 => 3, <= 11 => 1, <= 20 => 0, <= 24 => 2, _ => 3 },
            ["SpO2"] = spo2 switch { <= 91 => 3, <= 93 => 2, <= 95 => 1, _ => 0 },
            ["Systolic BP"] = systolic switch { <= 90 => 3, <= 100 => 2, <= 110 => 1, <= 219 => 0, _ => 3 },
            ["Pulse"] = pulse switch { <= 40 => 3, <= 50 => 1, <= 90 => 0, <= 110 => 1, <= 130 => 2, _ => 3 },
            ["Temperature"] = temp switch { <= 35.0 => 3, <= 36.0 => 1, <= 38.0 => 0, <= 39.0 => 1, _ => 2 },
            ["Air or oxygen"] = 0,
            ["Consciousness"] = 0,
        };
        var total = parts.Values.Sum();
        var risk = total >= 7 ? RiskLevel.High : total >= 5 ? RiskLevel.Medium : parts.Values.Any(v => v == 3) ? RiskLevel.LowMedium : RiskLevel.Low;
        return new News2Score(total, risk, parts);
    }

    private double UpdateEwma(string key, double x)
    {
        if (!_ewma.TryGetValue(key, out var s) || !s.Init)
        {
            _ewma[key] = (x, 1, true);
            return 0;
        }
        var sd = Math.Sqrt(Math.Max(s.Var, 0.25));
        var z = (x - s.Mean) / sd;
        var diff = x - s.Mean;
        var mean = s.Mean + Alpha * diff;
        var variance = (1 - Alpha) * (s.Var + Alpha * diff * diff);
        _ewma[key] = (mean, variance, true);
        return z;
    }

    private static double Slope(double[] x, double[] y)
    {
        if (x.Length < 3) return 0;
        double mx = x.Average(), my = y.Average(), num = 0, den = 0;
        for (var i = 0; i < x.Length; i++)
        {
            num += (x[i] - mx) * (y[i] - my);
            den += (x[i] - mx) * (x[i] - mx);
        }
        return den < 1e-12 ? 0 : num / den;
    }
}
