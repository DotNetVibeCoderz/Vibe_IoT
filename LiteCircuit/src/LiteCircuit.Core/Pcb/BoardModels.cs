using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiteCircuit.Core.Pcb;

/// <summary>Shared JSON options for all design documents (camelCase, tolerant).</summary>
public static class PcbJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static Board ParseBoard(string json) =>
        string.IsNullOrWhiteSpace(json) || json == "{}"
            ? Board.CreateBlank("Untitled", 80, 60)
            : JsonSerializer.Deserialize<Board>(json, Options) ?? Board.CreateBlank("Untitled", 80, 60);

    public static Schematic ParseSchematic(string json) =>
        string.IsNullOrWhiteSpace(json) || json == "{}"
            ? new Schematic()
            : JsonSerializer.Deserialize<Schematic>(json, Options) ?? new Schematic();

    public static string Serialize<T>(T doc) => JsonSerializer.Serialize(doc, Options);
}

/// <summary>The PCB layout document. All coordinates are millimetres, origin top-left.</summary>
public class Board
{
    public string Name { get; set; } = "Untitled";
    public double Width { get; set; } = 80;
    public double Height { get; set; } = 60;
    public List<Layer> Layers { get; set; } = new();
    public List<PcbComponent> Components { get; set; } = new();
    public List<Track> Tracks { get; set; } = new();
    public List<Via> Vias { get; set; } = new();
    public List<string> Nets { get; set; } = new();
    public DesignRules Rules { get; set; } = new();

    public static Board CreateBlank(string name, double w, double h) => new()
    {
        Name = name, Width = w, Height = h,
        Layers = DefaultStackup(2),
        Nets = new List<string> { "GND", "VCC" },
    };

    public static List<Layer> DefaultStackup(int copperLayers)
    {
        var list = new List<Layer> { new() { Name = "F.Cu", Type = "copper", ThicknessMm = 0.035, Material = "Copper" } };
        for (var i = 1; i < copperLayers - 1; i++)
        {
            list.Add(new Layer { Name = $"Core{i}", Type = "core", ThicknessMm = 0.4, Material = "FR4" });
            list.Add(new Layer { Name = $"In{i}.Cu", Type = "copper", ThicknessMm = 0.018, Material = "Copper" });
        }
        list.Add(new Layer { Name = "Core", Type = "core", ThicknessMm = 1.2, Material = "FR4" });
        if (copperLayers > 1)
            list.Add(new Layer { Name = "B.Cu", Type = "copper", ThicknessMm = 0.035, Material = "Copper" });
        return list;
    }

    public IEnumerable<Layer> CopperLayers => Layers.Where(l => l.Type == "copper");
}

public class Layer
{
    public string Name { get; set; } = "F.Cu";
    public string Type { get; set; } = "copper"; // copper | core | prepreg
    public double ThicknessMm { get; set; } = 0.035;
    public string Material { get; set; } = "Copper";
}

public class PcbComponent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Ref { get; set; } = "U1";
    public string Type { get; set; } = "ic";      // resistor|capacitor|led|diode|ic|module|connector|transistor|crystal|inductor|button|display
    public string Value { get; set; } = "";
    public string Footprint { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Rot { get; set; }
    public double W { get; set; } = 5;
    public double H { get; set; } = 3;
    public List<Pad> Pads { get; set; } = new();
}

/// <summary>Pad position is relative to the component centre (before rotation).</summary>
public class Pad
{
    public string Net { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; } = 1.2;
    public double H { get; set; } = 1.2;
    public double Drill { get; set; }             // 0 = SMD
    public string Shape { get; set; } = "rect";   // rect | circle

    /// <summary>Absolute pad centre for a component placement.</summary>
    public (double X, double Y) AbsoluteOn(PcbComponent c)
    {
        var rad = c.Rot * Math.PI / 180.0;
        var (cos, sin) = (Math.Cos(rad), Math.Sin(rad));
        return (c.X + X * cos - Y * sin, c.Y + X * sin + Y * cos);
    }
}

public class Track
{
    public string Net { get; set; } = "";
    public string Layer { get; set; } = "F.Cu";
    public double Width { get; set; } = 0.25;
    public List<double[]> Points { get; set; } = new();
}

public class Via
{
    public string Net { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Drill { get; set; } = 0.3;
    public double Dia { get; set; } = 0.6;
}

public class DesignRules
{
    public double MinTrackWidth { get; set; } = 0.15;
    public double MinClearance { get; set; } = 0.15;
    public double MinDrill { get; set; } = 0.3;
    public double EdgeClearance { get; set; } = 0.3;
    public double MinAnnularRing { get; set; } = 0.13;
}

/// <summary>The schematic document.</summary>
public class Schematic
{
    public List<SchComponent> Components { get; set; } = new();
    public List<Wire> Wires { get; set; } = new();
    public string Notes { get; set; } = "";
}

public class SchComponent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Ref { get; set; } = "R1";
    public string Type { get; set; } = "resistor";
    public string Value { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Rot { get; set; }
    /// <summary>Net name per pin index (pin 1 first).</summary>
    public List<string> PinNets { get; set; } = new();
}

public class Wire
{
    public string Net { get; set; } = "";
    public List<double[]> Points { get; set; } = new();
}

/// <summary>A design-rule-check violation.</summary>
public record DrcViolation(string Rule, string Severity, string Message, double X, double Y);

/// <summary>Result of an engineering analysis pass (DFM, compliance...).</summary>
public record CheckFinding(string Code, string Severity, string Title, string Detail);
