using System.Globalization;
using System.Text;
using LiteCircuit.Core.Pcb;

namespace LiteCircuit.Infrastructure.Services;

public record SpiceResult(string Netlist, List<(string Node, double Voltage)> NodeVoltages, List<string> Notes, bool Success);

/// <summary>
/// SPICE integration: netlist generation from the schematic plus a built-in DC operating-point
/// solver (modified nodal analysis) for resistors, voltage and current sources.
/// The generated netlist can also be run in ngspice/LTspice.
/// </summary>
public class SpiceService
{
    public SpiceResult RunDcAnalysis(Schematic sch)
    {
        var notes = new List<string>();
        var netlist = BuildNetlist(sch, notes);

        // Collect elements with resolved nets
        var resistors = new List<(string N1, string N2, double Ohms)>();
        var vsources = new List<(string NPlus, string NMinus, double Volts)>();
        var isources = new List<(string NPlus, string NMinus, double Amps)>();

        foreach (var c in sch.Components)
        {
            var nets = c.PinNets;
            if (nets.Count < 2) continue;
            switch (c.Type.ToLowerInvariant())
            {
                case "resistor":
                    resistors.Add((nets[0], nets[1], ParseValue(c.Value, 1000)));
                    break;
                case "battery":
                case "vsource":
                case "power":
                    vsources.Add((nets[0], nets.Count > 1 ? nets[1] : "GND", ParseValue(c.Value, 5)));
                    break;
                case "isource":
                    isources.Add((nets[0], nets[1], ParseValue(c.Value, 0.001)));
                    break;
                case "led":
                case "diode":
                    // Rough DC model: LED as fixed drop approximated by a resistor
                    resistors.Add((nets[0], nets[1], 100));
                    notes.Add($"{c.Ref}: diode approximated as 100 ohm for DC operating point.");
                    break;
                default:
                    notes.Add($"{c.Ref} ({c.Type}) not included in DC analysis (treated as open).");
                    break;
            }
        }

        if (vsources.Count == 0 && isources.Count == 0)
        {
            notes.Add("No voltage/current source found — add a 'battery' component with a value like '5' or '3.3'.");
            return new SpiceResult(netlist, new(), notes, false);
        }

        // Node numbering (GND/0 = reference)
        var nodes = resistors.SelectMany(r => new[] { r.N1, r.N2 })
            .Concat(vsources.SelectMany(v => new[] { v.NPlus, v.NMinus }))
            .Concat(isources.SelectMany(i => new[] { i.NPlus, i.NMinus }))
            .Where(n => !IsGround(n))
            .Distinct()
            .ToList();
        if (nodes.Count == 0)
            return new SpiceResult(netlist, new(), notes, false);

        int N = nodes.Count, M = vsources.Count;
        var idx = nodes.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i);
        var A = new double[N + M, N + M];
        var z = new double[N + M];

        foreach (var (n1, n2, ohms) in resistors)
        {
            var g = 1.0 / Math.Max(ohms, 1e-9);
            if (!IsGround(n1)) A[idx[n1], idx[n1]] += g;
            if (!IsGround(n2)) A[idx[n2], idx[n2]] += g;
            if (!IsGround(n1) && !IsGround(n2)) { A[idx[n1], idx[n2]] -= g; A[idx[n2], idx[n1]] -= g; }
        }
        foreach (var (np, nm, amps) in isources)
        {
            if (!IsGround(np)) z[idx[np]] -= amps;
            if (!IsGround(nm)) z[idx[nm]] += amps;
        }
        for (var k = 0; k < M; k++)
        {
            var (np, nm, volts) = vsources[k];
            var row = N + k;
            if (!IsGround(np)) { A[row, idx[np]] = 1; A[idx[np], row] = 1; }
            if (!IsGround(nm)) { A[row, idx[nm]] = -1; A[idx[nm], row] = -1; }
            z[row] = volts;
        }

        var x = Solve(A, z, N + M);
        if (x is null)
        {
            notes.Add("Matrix is singular — check for floating nodes or shorted sources.");
            return new SpiceResult(netlist, new(), notes, false);
        }

        var voltages = nodes.Select((n, i) => (n, Math.Round(x[i], 6))).OrderBy(v => v.n).ToList();
        return new SpiceResult(netlist, voltages, notes, true);
    }

    public string BuildNetlist(Schematic sch, List<string>? notes = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("* LiteCircuit netlist export");
        var i = 1;
        foreach (var c in sch.Components)
        {
            var nets = c.PinNets.Select(NodeName).ToList();
            while (nets.Count < 2) nets.Add("0");
            var line = c.Type.ToLowerInvariant() switch
            {
                "resistor" => $"R{i} {nets[0]} {nets[1]} {SpiceVal(c.Value, "1k")}",
                "capacitor" => $"C{i} {nets[0]} {nets[1]} {SpiceVal(c.Value, "100n")}",
                "inductor" => $"L{i} {nets[0]} {nets[1]} {SpiceVal(c.Value, "10u")}",
                "led" or "diode" => $"D{i} {nets[0]} {nets[1]} DDEFAULT",
                "battery" or "vsource" or "power" => $"V{i} {nets[0]} {nets[1]} DC {SpiceVal(c.Value, "5")}",
                "isource" => $"I{i} {nets[0]} {nets[1]} DC {SpiceVal(c.Value, "1m")}",
                _ => $"* {c.Ref} ({c.Type} {c.Value}) — no SPICE model",
            };
            sb.AppendLine($"{line} ; {c.Ref}");
            i++;
        }
        sb.AppendLine(".model DDEFAULT D(IS=1e-14)");
        sb.AppendLine(".op");
        sb.AppendLine(".end");
        return sb.ToString();
    }

    private static bool IsGround(string n) => string.IsNullOrEmpty(n) || n is "GND" or "0" or "gnd";
    private static string NodeName(string n) => IsGround(n) ? "0" : n.Replace(' ', '_');
    private static string SpiceVal(string v, string fallback) => string.IsNullOrWhiteSpace(v) ? fallback : v.Replace("Ω", "").Replace("ohm", "");

    /// <summary>Parses values like "10k", "4.7k", "100n", "3.3".</summary>
    public static double ParseValue(string raw, double fallback)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        raw = raw.Trim().Replace("Ω", "").Replace("ohm", "", StringComparison.OrdinalIgnoreCase)
                 .Replace("F", "").Replace("V", "").Replace("A", "");
        var mult = 1.0;
        if (raw.Length > 0)
        {
            var last = raw[^1];
            mult = last switch
            {
                'p' => 1e-12, 'n' => 1e-9, 'u' or 'µ' => 1e-6, 'm' => 1e-3,
                'k' or 'K' => 1e3, 'M' => 1e6, 'G' => 1e9, _ => 1.0,
            };
            if (mult != 1.0) raw = raw[..^1];
        }
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d * mult : fallback;
    }

    private static double[]? Solve(double[,] a, double[] b, int n)
    {
        var x = (double[])b.Clone();
        for (var col = 0; col < n; col++)
        {
            var pivot = col;
            for (var r = col + 1; r < n; r++)
                if (Math.Abs(a[r, col]) > Math.Abs(a[pivot, col])) pivot = r;
            if (Math.Abs(a[pivot, col]) < 1e-12) return null;
            if (pivot != col)
            {
                for (var c = 0; c < n; c++) (a[col, c], a[pivot, c]) = (a[pivot, c], a[col, c]);
                (x[col], x[pivot]) = (x[pivot], x[col]);
            }
            for (var r = col + 1; r < n; r++)
            {
                var f = a[r, col] / a[col, col];
                for (var c = col; c < n; c++) a[r, c] -= f * a[col, c];
                x[r] -= f * x[col];
            }
        }
        for (var r = n - 1; r >= 0; r--)
        {
            for (var c = r + 1; c < n; c++) x[r] -= a[r, c] * x[c];
            x[r] /= a[r, r];
        }
        return x;
    }
}
