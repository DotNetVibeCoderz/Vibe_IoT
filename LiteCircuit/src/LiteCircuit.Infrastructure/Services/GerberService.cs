using System.IO.Compression;
using System.Text;
using LiteCircuit.Core.Pcb;

namespace LiteCircuit.Infrastructure.Services;

/// <summary>Generates fabrication outputs: Gerber RS-274X, Excellon drill, pick-and-place CSV.</summary>
public class GerberService
{
    public Dictionary<string, byte[]> ExportAll(Board b)
    {
        var files = new Dictionary<string, byte[]>();
        foreach (var layer in b.CopperLayers)
            files[$"{San(b.Name)}-{layer.Name.Replace('.', '_')}.gbr"] = Encoding.ASCII.GetBytes(CopperLayer(b, layer.Name));
        files[$"{San(b.Name)}-Edge_Cuts.gbr"] = Encoding.ASCII.GetBytes(EdgeCuts(b));
        files[$"{San(b.Name)}.drl"] = Encoding.ASCII.GetBytes(Excellon(b));
        files[$"{San(b.Name)}-pos.csv"] = Encoding.UTF8.GetBytes(PickAndPlace(b));
        return files;
    }

    public byte[] ExportZip(Board b)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (name, data) in ExportAll(b))
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(data);
            }
        return ms.ToArray();
    }

    private static string San(string s) =>
        string.Concat(s.Select(c => char.IsLetterOrDigit(c) ? c : '_')).Trim('_') is { Length: > 0 } r ? r : "board";

    private static string Coord(double mm) => ((long)Math.Round(mm * 1_000_000)).ToString();

    private string CopperLayer(Board b, string layerName)
    {
        var copper = b.CopperLayers.ToList();
        var index = Math.Max(copper.FindIndex(l => l.Name == layerName), 0) + 1;
        var position = layerName == "F.Cu" ? "Top" : layerName == "B.Cu" ? "Bot" : "Inr";
        var sb = new StringBuilder();
        sb.AppendLine("%TF.GenerationSoftware,LiteCircuit,LiteCircuit,1.0*%");
        sb.AppendLine($"%TF.FileFunction,Copper,L{index},{position}*%");
        sb.AppendLine("%FSLAX46Y46*%");
        sb.AppendLine("%MOMM*%");
        sb.AppendLine("%LPD*%");

        // Aperture table: track widths (circles) + pad shapes
        var apertures = new Dictionary<string, int>();
        var next = 10;
        int Ap(string def) { if (!apertures.TryGetValue(def, out var d)) { d = next++; apertures[def] = d; } return d; }

        var draws = new List<(int ap, string cmd)>();
        foreach (var t in b.Tracks.Where(t => t.Layer == layerName && t.Points.Count > 1))
        {
            var ap = Ap($"C,{t.Width:0.####}");
            var sbi = new StringBuilder();
            sbi.AppendLine($"X{Coord(t.Points[0][0])}Y{Coord(t.Points[0][1])}D02*");
            foreach (var p in t.Points.Skip(1))
                sbi.AppendLine($"X{Coord(p[0])}Y{Coord(p[1])}D01*");
            draws.Add((ap, sbi.ToString()));
        }
        foreach (var c in b.Components)
            foreach (var p in c.Pads.Where(p => p.Drill > 0 || layerName == "F.Cu"))
            {
                var ap = p.Shape == "circle"
                    ? Ap($"C,{Math.Max(p.W, p.H):0.####}")
                    : Ap($"R,{p.W:0.####}X{p.H:0.####}");
                var (px, py) = p.AbsoluteOn(c);
                draws.Add((ap, $"X{Coord(px)}Y{Coord(py)}D03*\n"));
            }
        foreach (var via in b.Vias)
            draws.Add((Ap($"C,{via.Dia:0.####}"), $"X{Coord(via.X)}Y{Coord(via.Y)}D03*\n"));

        foreach (var (def, d) in apertures)
            sb.AppendLine($"%ADD{d}{def}*%");
        foreach (var g in draws.GroupBy(d => d.ap).OrderBy(g => g.Key))
        {
            sb.AppendLine($"D{g.Key}*");
            foreach (var (_, cmd) in g) sb.Append(cmd);
        }
        sb.AppendLine("M02*");
        return sb.ToString();
    }

    private static string EdgeCuts(Board b)
    {
        var sb = new StringBuilder();
        sb.AppendLine("%TF.FileFunction,Profile,NP*%");
        sb.AppendLine("%FSLAX46Y46*%");
        sb.AppendLine("%MOMM*%");
        sb.AppendLine("%ADD10C,0.10*%");
        sb.AppendLine("D10*");
        sb.AppendLine($"X{Coord(0)}Y{Coord(0)}D02*");
        sb.AppendLine($"X{Coord(b.Width)}Y{Coord(0)}D01*");
        sb.AppendLine($"X{Coord(b.Width)}Y{Coord(b.Height)}D01*");
        sb.AppendLine($"X{Coord(0)}Y{Coord(b.Height)}D01*");
        sb.AppendLine($"X{Coord(0)}Y{Coord(0)}D01*");
        sb.AppendLine("M02*");
        return sb.ToString();
    }

    private static string Excellon(Board b)
    {
        var holes = b.Components
            .SelectMany(c => c.Pads.Where(p => p.Drill > 0).Select(p =>
            {
                var (px, py) = p.AbsoluteOn(c);
                return (Drill: p.Drill, X: px, Y: py);
            }))
            .Concat(b.Vias.Select(v => (Drill: v.Drill, X: v.X, Y: v.Y)))
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("M48");
        sb.AppendLine("METRIC,TZ");
        var tools = holes.Select(h => h.Drill).Distinct().OrderBy(d => d).Select((d, i) => (d, n: i + 1)).ToList();
        foreach (var (d, n) in tools) sb.AppendLine($"T{n:00}C{d:0.000}");
        sb.AppendLine("%");
        sb.AppendLine("G90");
        sb.AppendLine("G05");
        foreach (var (d, n) in tools)
        {
            sb.AppendLine($"T{n:00}");
            foreach (var h in holes.Where(h => Math.Abs(h.Drill - d) < 1e-9))
                sb.AppendLine($"X{h.X:0.###}Y{h.Y:0.###}");
        }
        sb.AppendLine("M30");
        return sb.ToString();
    }

    private static string PickAndPlace(Board b)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Ref,Val,Footprint,PosX,PosY,Rot,Side");
        foreach (var c in b.Components)
            sb.AppendLine($"{c.Ref},\"{c.Value}\",{c.Footprint},{c.X:0.###},{c.Y:0.###},{c.Rot:0.#},top");
        return sb.ToString();
    }
}
