// Paste this into the in-app Scripting console (/scripting).
// Globals available there: Board, Schematic, Print(...)

var power = new[] { "VCC", "GND", "3V3", "5V", "VBAT", "VM" };
var widened = 0;
foreach (var t in Board.Tracks.Where(t => power.Contains(t.Net)))
{
    t.Width = Math.Max(t.Width, 0.5);
    widened++;
}

// Add stitching vias along the left edge on GND
for (double y = 5; y < Board.Height - 5; y += 10)
    Board.Vias.Add(new Via { Net = "GND", X = 2.5, Y = y, Drill = 0.3, Dia = 0.6 });

Print($"Widened {widened} power tracks; added {(int)((Board.Height - 10) / 10) + 1} GND stitching vias.");
Print("Tick 'Save changes back to project' before running to persist.");
