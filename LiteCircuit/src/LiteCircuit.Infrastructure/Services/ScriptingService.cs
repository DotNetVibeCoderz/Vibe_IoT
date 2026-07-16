using System.Text;
using LiteCircuit.Core.Pcb;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace LiteCircuit.Infrastructure.Services;

/// <summary>Globals available to user automation scripts.</summary>
public class ScriptGlobals
{
    public Board Board { get; set; } = Board.CreateBlank("Script", 80, 60);
    public Schematic Schematic { get; set; } = new();
    public StringBuilder Out { get; } = new();
    public void Print(object? o) => Out.AppendLine(o?.ToString() ?? "null");
}

/// <summary>Runs C# automation scripts against a design (Roslyn scripting).</summary>
public class ScriptingService
{
    public async Task<(bool Ok, string Output, Board Board)> RunAsync(string code, Board board, Schematic sch)
    {
        var globals = new ScriptGlobals { Board = board, Schematic = sch };
        try
        {
            var opts = ScriptOptions.Default
                .AddReferences(typeof(Board).Assembly, typeof(Enumerable).Assembly)
                .AddImports("System", "System.Linq", "System.Collections.Generic", "LiteCircuit.Core.Pcb");
            var result = await CSharpScript.EvaluateAsync(code, opts, globals,
                cancellationToken: new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
            if (result is not null) globals.Print(result);
            return (true, globals.Out.Length > 0 ? globals.Out.ToString() : "(script completed, no output)", globals.Board);
        }
        catch (CompilationErrorException ex)
        {
            return (false, "Compilation error:\n" + string.Join("\n", ex.Diagnostics), board);
        }
        catch (Exception ex)
        {
            return (false, $"Runtime error: {ex.Message}", board);
        }
    }

    public static readonly (string Name, string Code)[] Examples =
    {
        ("List components", """
            Print($"Board '{Board.Name}' — {Board.Width}x{Board.Height}mm");
            foreach (var c in Board.Components)
                Print($"{c.Ref,-6} {c.Type,-10} {c.Value,-10} at ({c.X:0.#}, {c.Y:0.#})");
            Print($"{Board.Tracks.Count} tracks, {Board.Nets.Count} nets");
            """),
        ("Align components to 1mm grid", """
            foreach (var c in Board.Components) {
                c.X = Math.Round(c.X);
                c.Y = Math.Round(c.Y);
            }
            Print($"Snapped {Board.Components.Count} components to the 1mm grid.");
            """),
        ("Widen all power tracks", """
            var power = new[] { "VCC", "GND", "3V3", "5V", "VBAT" };
            var n = 0;
            foreach (var t in Board.Tracks.Where(t => power.Contains(t.Net))) {
                t.Width = Math.Max(t.Width, 0.5);
                n++;
            }
            Print($"Widened {n} power tracks to ≥0.5mm.");
            """),
        ("Net statistics", """
            foreach (var net in Board.Nets) {
                var pads = Board.Components.SelectMany(c => c.Pads).Count(p => p.Net == net);
                var len = Board.Tracks.Where(t => t.Net == net)
                    .Sum(t => t.Points.Zip(t.Points.Skip(1),
                        (a, b) => Math.Sqrt(Math.Pow(a[0]-b[0],2) + Math.Pow(a[1]-b[1],2))).Sum());
                Print($"{net,-10} {pads} pads, {len:0.#}mm routed");
            }
            """),
        ("Add corner mounting holes", """
            // M2.5 mounting holes, 4mm from each corner
            double m = 4, drill = 2.7, ring = 5.0;
            foreach (var (x, y) in new[] { (m, m), (Board.Width - m, m),
                                           (m, Board.Height - m), (Board.Width - m, Board.Height - m) })
                Board.Vias.Add(new Via { X = x, Y = y, Drill = drill, Dia = ring });
            Print("Added 4 mounting holes (M2.5).");
            """),
        ("Center the design on the board", """
            if (Board.Components.Count == 0) { Print("No components."); return "";}
            var minX = Board.Components.Min(c => c.X); var maxX = Board.Components.Max(c => c.X);
            var minY = Board.Components.Min(c => c.Y); var maxY = Board.Components.Max(c => c.Y);
            double dx = Math.Round(Board.Width / 2 - (minX + maxX) / 2, 1);
            double dy = Math.Round(Board.Height / 2 - (minY + maxY) / 2, 1);
            foreach (var c in Board.Components) { c.X += dx; c.Y += dy; }
            foreach (var t in Board.Tracks) foreach (var p in t.Points) { p[0] += dx; p[1] += dy; }
            foreach (var v in Board.Vias) { v.X += dx; v.Y += dy; }
            Print($"Shifted everything by ({dx}, {dy})mm to center the design.");
            """),
        ("Set 4-layer stackup", """
            Board.Layers = Board.DefaultStackup(4);
            Print("Stackup rebuilt: " + string.Join(" | ",
                Board.Layers.Select(l => $"{l.Name} {l.ThicknessMm}mm")));
            Print("Route inner layers (In1.Cu, In2.Cu) manually — great for GND/PWR planes.");
            """),
        ("Rip up all routing", """
            int tracks = Board.Tracks.Count, vias = Board.Vias.Count;
            Board.Tracks.Clear();
            Board.Vias.RemoveAll(v => v.Drill < 2);   // keep mounting holes
            Print($"Removed {tracks} tracks and {vias - Board.Vias.Count} vias. Run auto-route to redo.");
            """),
        ("Find parts outside the board", """
            var outside = Board.Components.Where(c =>
                c.X - c.W/2 < 0 || c.Y - c.H/2 < 0 ||
                c.X + c.W/2 > Board.Width || c.Y + c.H/2 > Board.Height).ToList();
            if (outside.Count == 0) Print("All components are inside the outline. ✔");
            foreach (var c in outside)
                Print($"{c.Ref} at ({c.X:0.#},{c.Y:0.#}) sticks out — board is {Board.Width}x{Board.Height}mm");
            """),
        ("Copper usage per layer", """
            foreach (var g in Board.Tracks.GroupBy(t => t.Layer)) {
                var len = g.Sum(t => t.Points.Zip(t.Points.Skip(1),
                    (a, b) => Math.Sqrt(Math.Pow(a[0]-b[0],2) + Math.Pow(a[1]-b[1],2))).Sum());
                var area = g.Sum(t => t.Width * t.Points.Zip(t.Points.Skip(1),
                    (a, b) => Math.Sqrt(Math.Pow(a[0]-b[0],2) + Math.Pow(a[1]-b[1],2))).Sum());
                Print($"{g.Key,-8} {g.Count(),3} tracks  {len,8:0.#}mm  ~{area,6:0.#}mm² copper");
            }
            if (Board.Tracks.Count == 0) Print("No tracks yet.");
            """),
        ("Renumber refs by position", """
            // R1, R2... assigned left-to-right, top-to-bottom per type prefix
            foreach (var g in Board.Components.GroupBy(c => new string(c.Ref.TakeWhile(char.IsLetter).ToArray()))) {
                var n = 1;
                foreach (var c in g.OrderBy(c => Math.Round(c.Y / 10)).ThenBy(c => c.X))
                    c.Ref = $"{g.Key}{n++}";
            }
            Print("Refs renumbered in reading order. Update the schematic to match!");
            """),
        ("Grow board to fit design", """
            double margin = 5;
            var maxX = Board.Components.Max(c => c.X + c.W / 2);
            var maxY = Board.Components.Max(c => c.Y + c.H / 2);
            Board.Width  = Math.Max(Board.Width,  Math.Ceiling(maxX + margin));
            Board.Height = Math.Max(Board.Height, Math.Ceiling(maxY + margin));
            Print($"Board is now {Board.Width}x{Board.Height}mm ({margin}mm margin).");
            """),
    };
}
