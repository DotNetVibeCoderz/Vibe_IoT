using Spectre.Console;
using Spectre.Console.Rendering;

namespace IoTCom.Net.Cli;

/// <summary>Visual language shared with the Gallery and dashboard: control-cabinet greys + IEC 60073 signal colours.</summary>
internal static class Ui
{
    public static readonly Color Amber = new(242, 169, 0);
    public static readonly Color CableBlue = new(79, 140, 230);
    public static readonly Color LampGreen = new(46, 158, 91);
    public static readonly Color Fault = new(210, 59, 47);
    public static readonly Color Panel = new(228, 229, 224);
    public static readonly Color Anthracite = new(43, 48, 54);
    public static readonly Color Muted = new(130, 136, 142);

    public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static Color For(FrameFieldKind kind) => kind switch
    {
        FrameFieldKind.Address => CableBlue,
        FrameFieldKind.Function => Amber,
        FrameFieldKind.Checksum => LampGreen,
        FrameFieldKind.Error => Fault,
        FrameFieldKind.Length => new Color(150, 120, 200),
        FrameFieldKind.Header or FrameFieldKind.Delimiter => Muted,
        _ => Panel,
    };

    public static void Banner()
    {
        var lamps = $"[{Hex(LampGreen)}]●[/] [{Hex(Amber)}]●[/] [{Hex(CableBlue)}]●[/]";
        AnsiConsole.MarkupLine($"{lamps}  [bold]IoTCom.Net[/] [{Hex(Muted)}]{IoTComInfo.Version} · {IoTComInfo.CreditEn}[/]");
    }

    public static void Success(string text) => AnsiConsole.MarkupLine($"[{Hex(LampGreen)}]●[/] {text}");
    public static void Warn(string text) => AnsiConsole.MarkupLine($"[{Hex(Amber)}]●[/] {text}");
    public static void Error(string text) => AnsiConsole.MarkupLine($"[{Hex(Fault)}]●[/] {text}");

    /// <summary>The signature "frame lane": bytes as tiles coloured by field, with a field legend underneath.</summary>
    public static IRenderable FrameLane(ReadOnlySpan<byte> frame, IReadOnlyList<FrameField> fields, bool asciiFrame = false)
    {
        var lane = new System.Text.StringBuilder();
        var cursor = 0;
        foreach (var f in fields)
        {
            if (f.Offset > cursor) lane.Append(Tiles(frame.Slice(cursor, f.Offset - cursor), Muted, asciiFrame));
            lane.Append(Tiles(frame.Slice(f.Offset, Math.Min(f.Length, frame.Length - f.Offset)), For(f.Kind), asciiFrame));
            cursor = f.Offset + f.Length;
        }
        if (cursor < frame.Length) lane.Append(Tiles(frame[cursor..], Muted, asciiFrame));

        var table = new Table().Border(TableBorder.Simple).BorderColor(Muted)
            .AddColumn(new TableColumn("[bold]Field[/]"))
            .AddColumn(new TableColumn("[bold]Bytes[/]"))
            .AddColumn(new TableColumn("[bold]Value[/]"));
        foreach (var f in fields)
        {
            var bytes = frame.Slice(f.Offset, Math.Min(f.Length, frame.Length - f.Offset));
            var shown = asciiFrame ? System.Text.Encoding.ASCII.GetString(bytes).Replace("\r", "\\r").Replace("\n", "\\n") : HexDump.ToHex(bytes.Length > 12 ? bytes[..12] : bytes) + (bytes.Length > 12 ? " …" : "");
            table.AddRow($"[{Hex(For(f.Kind))}]■[/] {Markup.Escape(f.Name)}", $"[{Hex(Muted)}]{Markup.Escape(shown)}[/]", Markup.Escape(f.Value ?? ""));
        }
        return new Rows(new Markup(lane.ToString()), new Text(""), table);
    }

    private static string Tiles(ReadOnlySpan<byte> bytes, Color color, bool ascii)
    {
        var fg = color == Panel || color == Amber ? "black" : "white";
        var sb = new System.Text.StringBuilder();
        foreach (var b in bytes)
        {
            var label = ascii ? (b is >= 0x20 and < 0x7F ? $" {(char)b} " : b == '\r' ? "\\r " : b == '\n' ? "\\n " : " . ") : $" {b:X2} ";
            sb.Append($"[{fg} on {Hex(color)}]{Markup.Escape(label)}[/]");
        }
        return sb.ToString();
    }
}
