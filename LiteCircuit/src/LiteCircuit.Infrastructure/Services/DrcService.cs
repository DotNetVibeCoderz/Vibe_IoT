using LiteCircuit.Core.Pcb;

namespace LiteCircuit.Infrastructure.Services;

/// <summary>Real-time design rule check: clearances, widths, drills, edge distance, unrouted nets.</summary>
public class DrcService
{
    public List<DrcViolation> Run(Board b)
    {
        var v = new List<DrcViolation>();
        var r = b.Rules;

        // 1. Track width & board edge
        foreach (var t in b.Tracks)
        {
            if (t.Width < r.MinTrackWidth)
                v.Add(new("track-width", "error",
                    $"Track on {t.Layer} ({t.Net}) width {t.Width:0.###}mm < min {r.MinTrackWidth}mm",
                    t.Points.FirstOrDefault()?[0] ?? 0, t.Points.FirstOrDefault()?[1] ?? 0));
            foreach (var p in t.Points)
                if (p[0] < r.EdgeClearance || p[1] < r.EdgeClearance ||
                    p[0] > b.Width - r.EdgeClearance || p[1] > b.Height - r.EdgeClearance)
                {
                    v.Add(new("edge-clearance", "error",
                        $"Track ({t.Net}) too close to board edge", p[0], p[1]));
                    break;
                }
        }

        // 2. Drills & annular rings
        foreach (var via in b.Vias)
        {
            if (via.Drill < r.MinDrill)
                v.Add(new("drill-size", "error", $"Via drill {via.Drill}mm < min {r.MinDrill}mm", via.X, via.Y));
            if ((via.Dia - via.Drill) / 2 < r.MinAnnularRing)
                v.Add(new("annular-ring", "warning", $"Via annular ring below {r.MinAnnularRing}mm", via.X, via.Y));
        }
        foreach (var c in b.Components)
            foreach (var p in c.Pads.Where(p => p.Drill > 0 && p.Drill < r.MinDrill))
            {
                var (px, py) = p.AbsoluteOn(c);
                v.Add(new("drill-size", "error", $"{c.Ref} pad drill {p.Drill}mm < min {r.MinDrill}mm", px, py));
            }

        // 3. Pad vs pad clearance (different nets)
        var pads = b.Components
            .SelectMany(c => c.Pads.Select(p =>
            {
                var (px, py) = p.AbsoluteOn(c);
                return (c.Ref, p.Net, X: px, Y: py, R: Math.Max(p.W, p.H) / 2);
            }))
            .ToList();
        for (var i = 0; i < pads.Count; i++)
            for (var j = i + 1; j < pads.Count; j++)
            {
                var (a, c2) = (pads[i], pads[j]);
                if (a.Ref == c2.Ref) continue; // footprint-internal geometry is trusted
                if (a.Net == c2.Net && a.Net != "") continue;
                var d = Dist(a.X, a.Y, c2.X, c2.Y) - a.R - c2.R;
                if (d < r.MinClearance)
                    v.Add(new("clearance", "error",
                        $"Pads {a.Ref}({a.Net.OrNone()}) and {c2.Ref}({c2.Net.OrNone()}) clearance {Math.Max(d, 0):0.###}mm < {r.MinClearance}mm",
                        (a.X + c2.X) / 2, (a.Y + c2.Y) / 2));
            }

        // 4. Track vs track clearance (same layer, different nets)
        var segs = b.Tracks.SelectMany(t => Segments(t)).ToList();
        for (var i = 0; i < segs.Count; i++)
            for (var j = i + 1; j < segs.Count; j++)
            {
                var (a, s2) = (segs[i], segs[j]);
                if (a.Layer != s2.Layer || a.Net == s2.Net) continue;
                var d = SegSegDist(a, s2) - a.HalfW - s2.HalfW;
                if (d < r.MinClearance)
                    v.Add(new("clearance", "error",
                        $"Tracks {a.Net.OrNone()} / {s2.Net.OrNone()} on {a.Layer} clearance {Math.Max(d, 0):0.###}mm < {r.MinClearance}mm",
                        (a.X1 + s2.X1) / 2, (a.Y1 + s2.Y1) / 2));
            }

        // 5. Track vs pad clearance (different nets)
        foreach (var s in segs)
            foreach (var p in pads)
            {
                if (s.Net == p.Net && p.Net != "") continue;
                var d = PointSegDist(p.X, p.Y, s) - s.HalfW - p.R;
                if (d < r.MinClearance)
                    v.Add(new("clearance", "error",
                        $"Track {s.Net.OrNone()} to pad {p.Ref}({p.Net.OrNone()}) clearance {Math.Max(d, 0):0.###}mm < {r.MinClearance}mm",
                        p.X, p.Y));
            }

        // 6. Unrouted nets (>=2 pads, no copper)
        var routed = b.Tracks.Select(t => t.Net).Concat(b.Vias.Select(x => x.Net)).ToHashSet();
        foreach (var net in b.Nets.Where(n => !string.IsNullOrEmpty(n)))
        {
            var count = pads.Count(p => p.Net == net);
            if (count >= 2 && !routed.Contains(net))
            {
                var first = pads.First(p => p.Net == net);
                v.Add(new("unrouted", "warning", $"Net '{net}' has {count} pads but no routing", first.X, first.Y));
            }
        }

        return v;
    }

    private record struct Seg(double X1, double Y1, double X2, double Y2, string Net, string Layer, double HalfW);

    private static IEnumerable<Seg> Segments(Track t)
    {
        for (var i = 0; i + 1 < t.Points.Count; i++)
            yield return new Seg(t.Points[i][0], t.Points[i][1], t.Points[i + 1][0], t.Points[i + 1][1],
                t.Net, t.Layer, t.Width / 2);
    }

    private static double Dist(double x1, double y1, double x2, double y2) =>
        Math.Sqrt((x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2));

    private static double PointSegDist(double px, double py, Seg s)
    {
        double dx = s.X2 - s.X1, dy = s.Y2 - s.Y1;
        var len2 = dx * dx + dy * dy;
        if (len2 < 1e-12) return Dist(px, py, s.X1, s.Y1);
        var t = Math.Clamp(((px - s.X1) * dx + (py - s.Y1) * dy) / len2, 0, 1);
        return Dist(px, py, s.X1 + t * dx, s.Y1 + t * dy);
    }

    private static double SegSegDist(Seg a, Seg b)
    {
        if (SegmentsIntersect(a, b)) return 0;
        return Math.Min(
            Math.Min(PointSegDist(a.X1, a.Y1, b), PointSegDist(a.X2, a.Y2, b)),
            Math.Min(PointSegDist(b.X1, b.Y1, a), PointSegDist(b.X2, b.Y2, a)));
    }

    private static bool SegmentsIntersect(Seg a, Seg b)
    {
        static double Cross(double ox, double oy, double ax, double ay, double bx, double by) =>
            (ax - ox) * (by - oy) - (ay - oy) * (bx - ox);
        var d1 = Cross(b.X1, b.Y1, b.X2, b.Y2, a.X1, a.Y1);
        var d2 = Cross(b.X1, b.Y1, b.X2, b.Y2, a.X2, a.Y2);
        var d3 = Cross(a.X1, a.Y1, a.X2, a.Y2, b.X1, b.Y1);
        var d4 = Cross(a.X1, a.Y1, a.X2, a.Y2, b.X2, b.Y2);
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }
}

internal static class NetStr
{
    public static string OrNone(this string net) => string.IsNullOrEmpty(net) ? "no net" : net;
}
