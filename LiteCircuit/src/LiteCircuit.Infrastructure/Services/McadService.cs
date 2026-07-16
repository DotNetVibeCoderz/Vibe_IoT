using System.Globalization;
using System.Text;
using LiteCircuit.Core.Pcb;

namespace LiteCircuit.Infrastructure.Services;

/// <summary>
/// MCAD hand-off: exports the board and component envelopes as ASCII STL,
/// importable into SolidWorks / Fusion 360 for enclosure design.
/// </summary>
public class McadService
{
    public string ExportStl(Board b)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"solid {b.Name.Replace(' ', '_')}");
        var thickness = Math.Max(b.Layers.Sum(l => l.ThicknessMm), 1.6);

        Box(sb, 0, 0, 0, b.Width, b.Height, thickness);                       // board substrate
        foreach (var c in b.Components)                                        // component envelopes
        {
            var h = ComponentHeight(c.Type);
            Box(sb, c.X - c.W / 2, c.Y - c.H / 2, thickness, c.W, c.H, h);
        }
        sb.AppendLine($"endsolid {b.Name.Replace(' ', '_')}");
        return sb.ToString();
    }

    private static double ComponentHeight(string type) => type.ToLowerInvariant() switch
    {
        "resistor" or "capacitor" or "led" or "diode" => 0.6,
        "ic" => 1.5,
        "module" or "display" => 3.5,
        "connector" => 6.0,
        "button" => 4.0,
        _ => 2.0,
    };

    private static void Box(StringBuilder sb, double x, double y, double z, double w, double h, double d)
    {
        var v = new (double X, double Y, double Z)[]
        {
            (x, y, z), (x + w, y, z), (x + w, y + h, z), (x, y + h, z),
            (x, y, z + d), (x + w, y, z + d), (x + w, y + h, z + d), (x, y + h, z + d),
        };
        int[][] faces =
        {
            new[] {0,1,2}, new[] {0,2,3},      // bottom
            new[] {4,6,5}, new[] {4,7,6},      // top
            new[] {0,5,1}, new[] {0,4,5},      // front
            new[] {3,2,6}, new[] {3,6,7},      // back
            new[] {0,3,7}, new[] {0,7,4},      // left
            new[] {1,5,6}, new[] {1,6,2},      // right
        };
        foreach (var f in faces)
        {
            sb.AppendLine("  facet normal 0 0 0");
            sb.AppendLine("    outer loop");
            foreach (var i in f)
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "      vertex {0:0.###} {1:0.###} {2:0.###}", v[i].X, v[i].Y, v[i].Z));
            sb.AppendLine("    endloop");
            sb.AppendLine("  endfacet");
        }
    }
}
