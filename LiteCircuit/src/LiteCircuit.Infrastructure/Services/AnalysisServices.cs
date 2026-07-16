using LiteCircuit.Core.Pcb;

namespace LiteCircuit.Infrastructure.Services;

/// <summary>Design-for-manufacturability heuristics run before production.</summary>
public class DfmService
{
    public List<CheckFinding> Analyze(Board b)
    {
        var f = new List<CheckFinding>();

        var drills = b.Components.SelectMany(c => c.Pads.Where(p => p.Drill > 0).Select(p => p.Drill))
            .Concat(b.Vias.Select(v => v.Drill)).ToList();
        if (drills.Count > 0)
        {
            var min = drills.Min();
            f.Add(new("dfm-drill", min < 0.25 ? "error" : min < 0.3 ? "warning" : "info",
                $"Smallest drill: {min:0.###}mm",
                min < 0.3 ? "Drills under 0.3mm raise fabrication cost; under 0.25mm many fabs reject the board."
                          : "All drills are within standard capability."));
            f.Add(new("dfm-drill-count", "info", $"{drills.Count} holes total",
                $"Distinct drill sizes: {drills.Distinct().Count()}. Fewer sizes = cheaper drilling."));
        }

        var minTrack = b.Tracks.Count > 0 ? b.Tracks.Min(t => t.Width) : double.MaxValue;
        if (minTrack < double.MaxValue)
            f.Add(new("dfm-track", minTrack < 0.127 ? "warning" : "info",
                $"Narrowest track: {minTrack:0.###}mm",
                minTrack < 0.127 ? "Below 5mil (0.127mm) requires an advanced fab process."
                                 : "Track widths compatible with standard fabrication."));

        // Acute-angle trace joints ("acid traps")
        var acid = 0;
        foreach (var t in b.Tracks)
            for (var i = 1; i + 1 < t.Points.Count; i++)
            {
                var a = Angle(t.Points[i - 1], t.Points[i], t.Points[i + 1]);
                if (a < 85) acid++;
            }
        if (acid > 0)
            f.Add(new("dfm-acid-trap", "warning", $"{acid} acute trace joint(s) found",
                "Angles below ~90° can trap etchant (acid traps). Prefer 135° bends."));

        var area = b.Width * b.Height;
        var density = b.Components.Count / Math.Max(area / 100, 0.01);
        f.Add(new("dfm-density", density > 8 ? "warning" : "info",
            $"Component density: {density:0.#} per cm²",
            density > 8 ? "High density complicates assembly and rework." : "Comfortable assembly density."));

        var smd = b.Components.Count(c => c.Pads.All(p => p.Drill == 0));
        f.Add(new("dfm-mix", "info", $"{smd} SMD / {b.Components.Count - smd} through-hole components",
            "Mixed technology needs both reflow and wave/hand soldering passes."));

        if (f.All(x => x.Severity == "info"))
            f.Add(new("dfm-ok", "info", "No manufacturability blockers found", "Board is ready for standard fabrication."));
        return f;
    }

    private static double Angle(double[] a, double[] b, double[] c)
    {
        double v1x = a[0] - b[0], v1y = a[1] - b[1], v2x = c[0] - b[0], v2y = c[1] - b[1];
        var dot = v1x * v2x + v1y * v2y;
        var mag = Math.Sqrt(v1x * v1x + v1y * v1y) * Math.Sqrt(v2x * v2x + v2y * v2y);
        return mag < 1e-12 ? 180 : Math.Acos(Math.Clamp(dot / mag, -1, 1)) * 180 / Math.PI;
    }
}

/// <summary>IPC / RoHS / UL compliance checks.</summary>
public class ComplianceService
{
    public List<CheckFinding> Check(Board b)
    {
        var f = new List<CheckFinding>();

        // IPC-2221 class assessment from the configured clearance
        var cls = b.Rules.MinClearance >= 0.2 ? "Class 3 (high reliability)"
                : b.Rules.MinClearance >= 0.13 ? "Class 2 (dedicated service)"
                : "Class 1 (general electronics)";
        f.Add(new("ipc-class", "info", $"IPC-2221 clearance profile: {cls}",
            $"Minimum clearance rule is {b.Rules.MinClearance}mm. Class 3 needs ≥0.2mm at logic voltages."));

        f.Add(new("ipc-annular", b.Rules.MinAnnularRing >= 0.13 ? "info" : "warning",
            $"Annular ring rule: {b.Rules.MinAnnularRing}mm",
            "IPC-600 Class 2 requires ≥0.05mm after fabrication; design-side ≥0.13mm is the safe norm."));

        f.Add(new("rohs", "info", "RoHS: specify lead-free finish",
            "Order HASL lead-free or ENIG finish and confirm all BOM parts carry RoHS certificates (library vendor parts are RoHS by default)."));

        var core = b.Layers.FirstOrDefault(l => l.Type == "core");
        f.Add(new("ul", core?.Material == "FR4" ? "info" : "warning",
            $"Substrate: {core?.Material ?? "unknown"}",
            core?.Material == "FR4"
                ? "Standard FR4 is UL 94V-0 rated at qualified fabs — request the flammability cert with your order."
                : "Non-FR4 substrate: verify UL 94V-0 flammability rating with the fabricator."));

        f.Add(new("stackup", "info",
            $"{b.CopperLayers.Count()} copper layer(s), total thickness {b.Layers.Sum(l => l.ThicknessMm):0.##}mm",
            "Confirm the stackup matches the fabricator's standard build to avoid impedance surprises."));

        return f;
    }
}

/// <summary>High-speed design helpers: microstrip impedance and length matching.</summary>
public class SignalIntegrityService
{
    /// <summary>Approximate microstrip impedance (IPC-2141) for a track over the nearest plane.</summary>
    public double MicrostripImpedance(double traceWidthMm, double dielectricHeightMm, double er = 4.3, double copperThicknessMm = 0.035)
    {
        double w = traceWidthMm, h = dielectricHeightMm, t = copperThicknessMm;
        return 87.0 / Math.Sqrt(er + 1.41) * Math.Log(5.98 * h / (0.8 * w + t));
    }

    public record PairReport(string NetA, string NetB, double LenA, double LenB, double SkewMm, bool Matched);

    /// <summary>Length-compare differential pair candidates (nets ending in _P/_N or +/-).</summary>
    public List<PairReport> CheckDifferentialPairs(Board b, double maxSkewMm = 1.0)
    {
        double NetLen(string net) => b.Tracks.Where(t => t.Net == net)
            .Sum(t => t.Points.Zip(t.Points.Skip(1), (a, c) =>
                Math.Sqrt(Math.Pow(a[0] - c[0], 2) + Math.Pow(a[1] - c[1], 2))).Sum());

        var reports = new List<PairReport>();
        foreach (var net in b.Nets)
        {
            string? partner = null;
            if (net.EndsWith("_P")) partner = net[..^2] + "_N";
            else if (net.EndsWith("+")) partner = net[..^1] + "-";
            if (partner is null || !b.Nets.Contains(partner) || string.CompareOrdinal(net, partner) > 0) continue;
            double la = NetLen(net), lb = NetLen(partner);
            reports.Add(new(net, partner, la, lb, Math.Abs(la - lb), Math.Abs(la - lb) <= maxSkewMm));
        }
        return reports;
    }
}
