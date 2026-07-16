namespace LiteCircuit.Core.Models;

/// <summary>A PCB design project owning one schematic and one board layout (stored as JSON documents).</summary>
public class PcbProject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string OwnerId { get; set; } = "";
    public string TemplateKey { get; set; } = "";
    public string SchematicJson { get; set; } = "{}";
    public string BoardJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<DesignCommit> Commits { get; set; } = new();
    public List<DesignComment> Comments { get; set; } = new();
}

/// <summary>Git-like snapshot of a project design.</summary>
public class DesignCommit
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public string Message { get; set; } = "";
    public string Author { get; set; } = "";
    public string SchematicJson { get; set; } = "{}";
    public string BoardJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Inline collaboration comment anchored to a location on the design.</summary>
public class DesignComment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public string Author { get; set; } = "";
    public string Text { get; set; } = "";
    public string Area { get; set; } = "board"; // board | schematic
    public double X { get; set; }
    public double Y { get; set; }
    public bool Resolved { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Unified component library entry (KiCad open-source + vendor sourced).</summary>
public class LibComponent
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";       // Resistor, Capacitor, MCU, ...
    public string Value { get; set; } = "";          // 10k, 100nF, ...
    public string Footprint { get; set; } = "";      // R0805, SOIC-8, LQFP-48, ...
    public string Manufacturer { get; set; } = "";
    public string Source { get; set; } = "KiCad";    // KiCad | Vendor
    public string DatasheetUrl { get; set; } = "";
    public int Pins { get; set; } = 2;
    public decimal PriceUsd { get; set; }
    public int Stock { get; set; }
}

/// <summary>Chat session with Electra.</summary>
public class ChatSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = "";
    public string Title { get; set; } = "New chat";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<ChatMessage> Messages { get; set; } = new();
}

public class ChatMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public string Role { get; set; } = "user"; // user | assistant
    public string Content { get; set; } = "";
    public string AttachmentsJson { get; set; } = "[]";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>An uploaded chat attachment.</summary>
public record ChatAttachment(string Url, string Name, string ContentType)
{
    public bool IsImage => ContentType.StartsWith("image/");
}
