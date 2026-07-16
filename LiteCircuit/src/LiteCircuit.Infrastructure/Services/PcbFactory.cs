using LiteCircuit.Core.Pcb;

namespace LiteCircuit.Infrastructure.Services;

/// <summary>
/// Builds PCB components with realistic pads from a footprint name.
/// Used by templates and by AI-generated designs so both produce routable boards.
/// </summary>
public static class PcbFactory
{
    public static PcbComponent Make(string type, string refDes, string value, string footprint,
        double x, double y, params string[] nets)
    {
        var c = new PcbComponent { Ref = refDes, Type = type, Value = value, Footprint = footprint, X = x, Y = y };
        BuildPads(c, footprint);
        for (var i = 0; i < c.Pads.Count && i < nets.Length; i++) c.Pads[i].Net = nets[i];
        return c;
    }

    private static void BuildPads(PcbComponent c, string footprint)
    {
        void Smd2(double pitch, double pw, double ph, double w, double h)
        {
            c.W = w; c.H = h;
            c.Pads.Add(new Pad { X = -pitch / 2, Y = 0, W = pw, H = ph });
            c.Pads.Add(new Pad { X = pitch / 2, Y = 0, W = pw, H = ph });
        }
        void HeaderRow(int n)
        {
            c.W = n * 2.54; c.H = 2.54;
            for (var i = 0; i < n; i++)
                c.Pads.Add(new Pad { X = (i - (n - 1) / 2.0) * 2.54, Y = 0, W = 1.7, H = 1.7, Drill = 1.0, Shape = "circle" });
        }
        void DualRow(int total, double pitch, double rowGap, double pw, double ph)
        {
            var perSide = total / 2;
            c.W = perSide * pitch + 1; c.H = rowGap + ph;
            for (var i = 0; i < perSide; i++)
                c.Pads.Add(new Pad { X = (i - (perSide - 1) / 2.0) * pitch, Y = -rowGap / 2, W = pw, H = ph });
            for (var i = 0; i < perSide; i++)
                c.Pads.Add(new Pad { X = (i - (perSide - 1) / 2.0) * pitch, Y = rowGap / 2, W = pw, H = ph });
        }
        void Quad(int total, double pitch, double body, double pw, double ph)
        {
            var perSide = total / 4;
            c.W = body; c.H = body;
            var half = body / 2 + ph / 2;
            for (var i = 0; i < perSide; i++)
            {
                var off = (i - (perSide - 1) / 2.0) * pitch;
                c.Pads.Add(new Pad { X = off, Y = -half, W = pw, H = ph });   // top
                c.Pads.Add(new Pad { X = half, Y = off, W = ph, H = pw });    // right
                c.Pads.Add(new Pad { X = off, Y = half, W = pw, H = ph });    // bottom
                c.Pads.Add(new Pad { X = -half, Y = off, W = ph, H = pw });   // left
            }
        }

        switch (footprint.ToUpperInvariant())
        {
            case "R0805": case "C0805": case "L0805": case "LED0805": Smd2(1.9, 1.0, 1.3, 2.0, 1.3); break;
            case "SOD-123": case "SMA": Smd2(3.4, 1.2, 1.4, 2.8, 1.6); break;
            case "CP-RADIAL-5MM":
                c.W = 5; c.H = 5;
                c.Pads.Add(new Pad { X = -1.0, Y = 0, W = 1.6, H = 1.6, Drill = 0.8, Shape = "circle" });
                c.Pads.Add(new Pad { X = 1.0, Y = 0, W = 1.6, H = 1.6, Drill = 0.8, Shape = "circle" });
                break;
            case "SOT-23":
                c.W = 3; c.H = 2.6;
                c.Pads.Add(new Pad { X = -0.95, Y = 1.1, W = 0.8, H = 0.9 });
                c.Pads.Add(new Pad { X = 0.95, Y = 1.1, W = 0.8, H = 0.9 });
                c.Pads.Add(new Pad { X = 0, Y = -1.1, W = 0.8, H = 0.9 });
                break;
            case "SOT-223":
                c.W = 6.5; c.H = 7;
                c.Pads.Add(new Pad { X = -2.3, Y = 3.0, W = 1.2, H = 2.0 });
                c.Pads.Add(new Pad { X = 0, Y = 3.0, W = 1.2, H = 2.0 });
                c.Pads.Add(new Pad { X = 2.3, Y = 3.0, W = 1.2, H = 2.0 });
                c.Pads.Add(new Pad { X = 0, Y = -3.0, W = 3.6, H = 2.2 });
                break;
            case "SOIC-8": DualRow(8, 1.27, 5.4, 0.6, 1.5); break;
            case "SOIC-16": DualRow(16, 1.27, 5.4, 0.6, 1.5); break;
            case "TSSOP-16": DualRow(16, 0.65, 5.7, 0.4, 1.2); break;
            case "TSSOP-20": DualRow(20, 0.65, 5.7, 0.4, 1.2); break;
            case "TSSOP-28": DualRow(28, 0.65, 5.7, 0.4, 1.2); break;
            case "LGA-8": DualRow(8, 0.65, 2.0, 0.4, 0.6); break;
            case "TQFP-32": Quad(32, 0.8, 7, 0.5, 1.2); break;
            case "LQFP-48": Quad(48, 0.5, 7, 0.3, 1.2); break;
            case "QFN-56": Quad(56, 0.4, 7, 0.25, 0.8); break;
            case "ESP32-MODULE":
                c.W = 18; c.H = 25.5;
                for (var i = 0; i < 14; i++)
                {
                    var yy = (i - 6.5) * 1.7;
                    c.Pads.Add(new Pad { X = -8.4, Y = yy, W = 1.5, H = 0.9 });
                    c.Pads.Add(new Pad { X = 8.4, Y = yy, W = 1.5, H = 0.9 });
                }
                for (var i = 0; i < 10; i++)
                    c.Pads.Add(new Pad { X = (i - 4.5) * 1.7, Y = 12.2, W = 0.9, H = 1.5 });
                break;
            case "MODULE-DFPLAYER": DualRow(16, 2.54, 17, 1.7, 1.7); break;
            case "MODULE-RDA5807": HeaderRow(10); break;
            case "MODULE-MP1584": HeaderRow(4); break;
            case "MODULE-TP4056": HeaderRow(6); break;
            case "MODULE-3PIN": HeaderRow(3); break;
            case "TACT-6MM":
                c.W = 6; c.H = 6;
                c.Pads.Add(new Pad { X = -3.2, Y = -2.25, W = 1.6, H = 1.6, Drill = 0.9, Shape = "circle" });
                c.Pads.Add(new Pad { X = 3.2, Y = -2.25, W = 1.6, H = 1.6, Drill = 0.9, Shape = "circle" });
                c.Pads.Add(new Pad { X = -3.2, Y = 2.25, W = 1.6, H = 1.6, Drill = 0.9, Shape = "circle" });
                c.Pads.Add(new Pad { X = 3.2, Y = 2.25, W = 1.6, H = 1.6, Drill = 0.9, Shape = "circle" });
                break;
            case "USB-MICRO-B":
                c.W = 8; c.H = 5.5;
                for (var i = 0; i < 5; i++)
                    c.Pads.Add(new Pad { X = (i - 2) * 0.65, Y = -2.2, W = 0.4, H = 1.35 });
                break;
            case "USB-C-16P":
                c.W = 9; c.H = 7.5;
                for (var i = 0; i < 16; i++)
                    c.Pads.Add(new Pad { X = (i - 7.5) * 0.5, Y = -3.2, W = 0.3, H = 1.1 });
                break;
            case "TERM-2":
                c.W = 10.2; c.H = 8;
                c.Pads.Add(new Pad { X = -2.54, Y = 0, W = 2.6, H = 2.6, Drill = 1.3, Shape = "circle" });
                c.Pads.Add(new Pad { X = 2.54, Y = 0, W = 2.6, H = 2.6, Drill = 1.3, Shape = "circle" });
                break;
            case "TERM-3":
                c.W = 15.3; c.H = 8;
                c.Pads.Add(new Pad { X = -5.08, Y = 0, W = 2.6, H = 2.6, Drill = 1.3, Shape = "circle" });
                c.Pads.Add(new Pad { X = 0, Y = 0, W = 2.6, H = 2.6, Drill = 1.3, Shape = "circle" });
                c.Pads.Add(new Pad { X = 5.08, Y = 0, W = 2.6, H = 2.6, Drill = 1.3, Shape = "circle" });
                break;
            case "HC49-SMD": Smd2(4.9, 1.9, 1.5, 11.5, 4.7); break;
            case "FC-135": Smd2(2.0, 1.0, 1.4, 3.2, 1.5); break;
            case "TH-2PIN": HeaderRow(2); break;
            case "TH-3PIN": HeaderRow(3); break;
            case "TH-4PIN": HeaderRow(4); break;
            case "MODULE-A4988": DualRow(16, 2.54, 15, 1.7, 1.7); break;
            case "MODULE-NRF24": DualRow(8, 2.54, 2.54, 1.7, 1.7); break;
            case "SMD-5050":
                c.W = 5; c.H = 5;
                c.Pads.Add(new Pad { X = -2.45, Y = -1.6, W = 1.5, H = 1.0 });
                c.Pads.Add(new Pad { X = -2.45, Y = 1.6, W = 1.5, H = 1.0 });
                c.Pads.Add(new Pad { X = 2.45, Y = 1.6, W = 1.5, H = 1.0 });
                c.Pads.Add(new Pad { X = 2.45, Y = -1.6, W = 1.5, H = 1.0 });
                break;
            case "RELAY-SRD":
                c.W = 19; c.H = 15.5;
                c.Pads.Add(new Pad { X = -6, Y = 6, W = 2.4, H = 2.4, Drill = 1.2, Shape = "circle" });   // coil+
                c.Pads.Add(new Pad { X = 6, Y = 6, W = 2.4, H = 2.4, Drill = 1.2, Shape = "circle" });    // coil-
                c.Pads.Add(new Pad { X = -7, Y = -6, W = 2.4, H = 2.4, Drill = 1.2, Shape = "circle" });  // COM
                c.Pads.Add(new Pad { X = 0, Y = -6, W = 2.4, H = 2.4, Drill = 1.2, Shape = "circle" });   // NO
                c.Pads.Add(new Pad { X = 7, Y = -6, W = 2.4, H = 2.4, Drill = 1.2, Shape = "circle" });   // NC
                break;
            case "TO-220":
                c.W = 10; c.H = 9;
                c.Pads.Add(new Pad { X = -2.54, Y = 3, W = 2.0, H = 2.0, Drill = 1.0, Shape = "circle" });
                c.Pads.Add(new Pad { X = 0, Y = 3, W = 2.0, H = 2.0, Drill = 1.0, Shape = "circle" });
                c.Pads.Add(new Pad { X = 2.54, Y = 3, W = 2.0, H = 2.0, Drill = 1.0, Shape = "circle" });
                break;
            case "SOP-4":
                c.W = 4.4; c.H = 3.6;
                c.Pads.Add(new Pad { X = -1.27, Y = 2.3, W = 0.8, H = 1.2 });
                c.Pads.Add(new Pad { X = 1.27, Y = 2.3, W = 0.8, H = 1.2 });
                c.Pads.Add(new Pad { X = 1.27, Y = -2.3, W = 0.8, H = 1.2 });
                c.Pads.Add(new Pad { X = -1.27, Y = -2.3, W = 0.8, H = 1.2 });
                break;
            case "EC11":
                c.W = 12; c.H = 13;
                c.Pads.Add(new Pad { X = -2.5, Y = 6, W = 1.8, H = 1.8, Drill = 1.0, Shape = "circle" });
                c.Pads.Add(new Pad { X = 0, Y = 6, W = 1.8, H = 1.8, Drill = 1.0, Shape = "circle" });
                c.Pads.Add(new Pad { X = 2.5, Y = 6, W = 1.8, H = 1.8, Drill = 1.0, Shape = "circle" });
                c.Pads.Add(new Pad { X = -2.5, Y = -6, W = 1.8, H = 1.8, Drill = 1.0, Shape = "circle" });
                c.Pads.Add(new Pad { X = 2.5, Y = -6, W = 1.8, H = 1.8, Drill = 1.0, Shape = "circle" });
                break;
            default:
                if (footprint.StartsWith("HDR-1x", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(footprint[6..], out var n) && n is > 0 and <= 40)
                    HeaderRow(n);
                else
                    Smd2(1.9, 1.0, 1.3, 2.0, 1.3);
                break;
        }
    }

    /// <summary>Reference schematic symbol sizes per type, used by the schematic editor.</summary>
    public static SchComponent MakeSch(string type, string refDes, string value, double x, double y, params string[] pinNets) =>
        new() { Ref = refDes, Type = type, Value = value, X = x, Y = y, PinNets = pinNets.ToList() };
}
