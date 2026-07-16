using System.Text.Json;
using LiteCircuit.Core.Pcb;
using LiteCircuit.Infrastructure.Services;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace LiteCircuit.Infrastructure.Ai;

/// <summary>
/// AI-assisted design: natural-language schematic generation, placement advice,
/// pre-DRC error prediction and BOM recommendations.
/// </summary>
public class DesignAiService(ElectraKernelFactory kernelFactory, DrcService drc)
{
    private const string KnownFootprints =
        "R0805, C0805, L0805, LED0805, SOD-123, SMA, CP-RADIAL-5MM, SOT-23, SOT-223, SOIC-8, SOIC-16, " +
        "TSSOP-16, TSSOP-20, TSSOP-28, LGA-8, TQFP-32, LQFP-48, QFN-56, ESP32-MODULE, MODULE-DFPLAYER, " +
        "MODULE-RDA5807, MODULE-MP1584, MODULE-TP4056, MODULE-3PIN, TACT-6MM, USB-MICRO-B, USB-C-16P, " +
        "TERM-2, TERM-3, HC49-SMD, FC-135, TH-2PIN, TH-4PIN, HDR-1x2 … HDR-1x40";

    private const string KnownTypes =
        "resistor, capacitor, inductor, led, diode, transistor, ic, module, regulator, sensor, display, " +
        "connector, button, crystal, audio, battery";

    /// <summary>Generates a schematic + placed board from a plain-text description.</summary>
    public async Task<(Schematic? Sch, Board? Board, string Message)> GenerateFromTextAsync(string description)
    {
        Kernel kernel;
        try { kernel = kernelFactory.Create(withPlugins: false); }
        catch (InvalidOperationException e) { return (null, null, e.Message); }

        var prompt = $$"""
            You are an expert electronics engineer. Design a circuit for this request:

            "{{description}}"

            Reply with ONLY a JSON object, no markdown fences, matching exactly this schema:
            {
              "components": [
                { "type": "<one of: {{KnownTypes}}>",
                  "ref": "R1", "value": "10k",
                  "footprint": "<one of: {{KnownFootprints}}>",
                  "pinNets": ["NET_A", "NET_B"] }
              ],
              "notes": "one-paragraph design explanation"
            }

            Rules:
            - Use GND / 3V3 / 5V as power net names. Every component pin gets a net in pinNets (pin order).
            - Include decoupling capacitors and pull-up/pull-down resistors where good practice requires.
            - Give every component a unique ref (R1, R2, C1, U1, J1, SW1...).
            - 6 to 20 components.
            """;

        var chat = kernel.GetRequiredService<IChatCompletionService>();
        var settings = new OpenAIPromptExecutionSettings { Temperature = 0.2, MaxTokens = 4000 };
        string raw;
        try
        {
            var result = await chat.GetChatMessageContentAsync(prompt, settings, kernel);
            raw = result.Content ?? "";
        }
        catch (Exception e) { return (null, null, $"Provider error: {e.Message}"); }

        var json = ExtractJson(raw);
        AiSchematic? parsed;
        try { parsed = JsonSerializer.Deserialize<AiSchematic>(json, PcbJson.Options); }
        catch (JsonException) { return (null, null, "The model returned malformed JSON — try rephrasing the request."); }
        if (parsed is null || parsed.Components.Count == 0)
            return (null, null, "The model returned no components — try a more specific description.");

        // Build schematic with a simple reading-order layout
        var sch = new Schematic { Notes = parsed.Notes };
        var i = 0;
        foreach (var c in parsed.Components)
        {
            sch.Components.Add(new SchComponent
            {
                Ref = c.Ref, Type = c.Type.ToLowerInvariant(), Value = c.Value,
                X = 25 + (i % 5) * 32, Y = 20 + (i / 5) * 26,
                PinNets = c.PinNets,
            });
            i++;
        }

        var board = BuildBoard(parsed);
        return (sch, board, $"Generated {parsed.Components.Count} components. {parsed.Notes}");
    }

    /// <summary>Places AI-generated components on a fresh board in a spaced grid.</summary>
    private static Board BuildBoard(AiSchematic parsed)
    {
        var comps = new List<PcbComponent>();
        var i = 0;
        foreach (var c in parsed.Components)
        {
            var fp = c.Footprint is { Length: > 0 } ? c.Footprint : "R0805";
            comps.Add(PcbFactory.Make(c.Type.ToLowerInvariant(), c.Ref, c.Value, fp,
                0, 0, c.PinNets.ToArray()));
            i++;
        }
        // Size the board from the components, place big parts center-left, small parts around
        var cols = (int)Math.Ceiling(Math.Sqrt(comps.Count));
        var cellW = Math.Max(comps.Max(c => c.W) + 6, 14);
        var cellH = Math.Max(comps.Max(c => c.H) + 6, 12);
        var w = Math.Max(60, cols * cellW + 10);
        var h = Math.Max(45, Math.Ceiling((double)comps.Count / cols) * cellH + 10);
        i = 0;
        foreach (var c in comps.OrderByDescending(c => c.W * c.H))
        {
            c.X = Math.Round(10 + cellW / 2 + (i % cols) * cellW, 1);
            c.Y = Math.Round(8 + cellH / 2 + (i / cols) * cellH, 1);
            i++;
        }
        var board = Board.CreateBlank("AI design", Math.Round(w), Math.Round(h));
        board.Components = comps;
        board.Nets = comps.SelectMany(c => c.Pads.Select(p => p.Net))
            .Where(n => !string.IsNullOrEmpty(n)).Distinct().OrderBy(n => n).ToList();
        return board;
    }

    /// <summary>Predicts likely problems before a formal DRC (thermal, EMI, connectivity...).</summary>
    public async Task<string> PredictErrorsAsync(Board board)
    {
        var drcResult = drc.Run(board);
        var summary = BoardSummary(board);
        return await AskAsync($"""
            Review this PCB design summary and predict problems beyond standard DRC —
            thermal hotspots, EMI risk, connector placement, missing decoupling, power integrity.
            Reply as a short prioritized markdown list (max 8 items).

            {summary}

            Standard DRC already found: {(drcResult.Count == 0 ? "no violations" : string.Join("; ", drcResult.Take(10).Select(v => v.Message)))}
            """);
    }

    /// <summary>Suggests better placement (thermal & EMI aware).</summary>
    public async Task<string> PlacementAdviceAsync(Board board) =>
        await AskAsync($"""
            Suggest component placement improvements for thermal and EMI performance.
            Be specific: name refs and directions ("move U2 away from U1", "rotate J1 to the edge").
            Reply as a short markdown list.

            {BoardSummary(board)}
            """);

    /// <summary>BOM assistant: availability and cost advice. Uses the component library via kernel functions.</summary>
    public async Task<string> BomAdviceAsync(string bomText) =>
        await AskAsync($"""
            You are a BOM sourcing assistant. Review this bill of materials.
            Use the data.search_components function to check the library for cheaper or better-stocked alternatives.
            Reply in markdown: a short table of recommended swaps plus total cost notes.

            {bomText}
            """, withPlugins: true);

    private async Task<string> AskAsync(string prompt, bool withPlugins = false)
    {
        Kernel kernel;
        try { kernel = kernelFactory.Create(withPlugins); }
        catch (InvalidOperationException e) { return e.Message; }
        var chat = kernel.GetRequiredService<IChatCompletionService>();
        var settings = new OpenAIPromptExecutionSettings
        {
            Temperature = 0.4, MaxTokens = 1500,
            FunctionChoiceBehavior = withPlugins ? FunctionChoiceBehavior.Auto() : null,
        };
        try
        {
            var res = await chat.GetChatMessageContentAsync(prompt, settings, kernel);
            return res.Content ?? "(empty reply)";
        }
        catch (Exception e) { return $"Provider error: {e.Message}"; }
    }

    private static string BoardSummary(Board b)
    {
        var comps = string.Join("\n", b.Components.Select(c =>
            $"- {c.Ref} {c.Type} {c.Value} ({c.Footprint}) at ({c.X:0.#},{c.Y:0.#}) nets: {string.Join(",", c.Pads.Select(p => p.Net).Where(n => n != "").Distinct())}"));
        return $"Board {b.Width}x{b.Height}mm, {b.CopperLayers.Count()} copper layers, {b.Tracks.Count} tracks.\nComponents:\n{comps}";
    }

    private static string ExtractJson(string raw)
    {
        raw = raw.Trim();
        if (raw.StartsWith("```"))
        {
            var start = raw.IndexOf('\n') + 1;
            var end = raw.LastIndexOf("```", StringComparison.Ordinal);
            if (end > start) raw = raw[start..end].Trim();
        }
        var first = raw.IndexOf('{');
        var last = raw.LastIndexOf('}');
        return first >= 0 && last > first ? raw[first..(last + 1)] : raw;
    }

    private class AiSchematic
    {
        public List<AiComponent> Components { get; set; } = new();
        public string Notes { get; set; } = "";
    }

    private class AiComponent
    {
        public string Type { get; set; } = "resistor";
        public string Ref { get; set; } = "R1";
        public string Value { get; set; } = "";
        public string Footprint { get; set; } = "R0805";
        public List<string> PinNets { get; set; } = new();
    }
}
