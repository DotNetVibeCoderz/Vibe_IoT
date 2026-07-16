using System.ComponentModel;
using System.Data;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using LiteCircuit.Core.Pcb;
using LiteCircuit.Infrastructure.Data;
using LiteCircuit.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.SemanticKernel;

namespace LiteCircuit.Infrastructure.Ai;

/// <summary>Date, time and math helpers.</summary>
public class UtilityPlugin
{
    [KernelFunction("current_datetime")]
    [Description("Gets the current date and time (local and UTC).")]
    public string CurrentDateTime() =>
        $"Local: {DateTime.Now:dddd, dd MMMM yyyy HH:mm:ss} | UTC: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z";

    [KernelFunction("days_between")]
    [Description("Days between two dates in yyyy-MM-dd format.")]
    public string DaysBetween(string fromDate, string toDate) =>
        DateTime.TryParse(fromDate, out var a) && DateTime.TryParse(toDate, out var b)
            ? $"{(b - a).TotalDays:0} days" : "Could not parse dates; use yyyy-MM-dd.";

    [KernelFunction("calculate")]
    [Description("Evaluates a math expression, e.g. '(4.7*2)/0.02'. Supports + - * / % and parentheses.")]
    public string Calculate(string expression)
    {
        try
        {
            var result = new DataTable().Compute(expression, null);
            return Convert.ToDouble(result).ToString("G10");
        }
        catch (Exception e) { return $"Cannot evaluate '{expression}': {e.Message}"; }
    }

    [KernelFunction("ohms_law")]
    [Description("Ohm's law solver. Provide exactly two of: volts, amps, ohms. Pass 0 for the unknown.")]
    public string OhmsLaw(double volts, double amps, double ohms)
    {
        if (volts == 0 && amps > 0 && ohms > 0) return $"V = {amps * ohms:G6} V";
        if (amps == 0 && volts > 0 && ohms > 0) return $"I = {volts / ohms:G6} A";
        if (ohms == 0 && volts > 0 && amps > 0) return $"R = {volts / amps:G6} Ω, P = {volts * amps:G6} W";
        return "Provide exactly two known values.";
    }
}

/// <summary>Internet access: Tavily search, page scraping, file fetch.</summary>
public class WebPlugin(IHttpClientFactory httpFactory, string tavilyApiKey)
{
    [KernelFunction("search_internet")]
    [Description("Searches the internet via Tavily and returns top results with snippets.")]
    public async Task<string> SearchAsync([Description("The search query")] string query)
    {
        if (string.IsNullOrWhiteSpace(tavilyApiKey))
            return "Tavily is not configured. Ask the user to set Tavily:ApiKey in appsettings.json.";
        using var http = httpFactory.CreateClient("ai");
        var res = await http.PostAsJsonAsync("https://api.tavily.com/search",
            new { api_key = tavilyApiKey, query, max_results = 5, include_answer = true });
        if (!res.IsSuccessStatusCode) return $"Tavily error: {res.StatusCode}";
        var doc = await res.Content.ReadFromJsonAsync<JsonElement>();
        var sb = new System.Text.StringBuilder();
        if (doc.TryGetProperty("answer", out var ans)) sb.AppendLine($"Answer: {ans.GetString()}\n");
        if (doc.TryGetProperty("results", out var results))
            foreach (var r in results.EnumerateArray())
                sb.AppendLine($"- {r.GetProperty("title").GetString()} ({r.GetProperty("url").GetString()})\n  {r.GetProperty("content").GetString()?[..Math.Min(300, r.GetProperty("content").GetString()!.Length)]}");
        return sb.ToString();
    }

    [KernelFunction("scrape_page")]
    [Description("Downloads a web page and returns its readable text content.")]
    public async Task<string> ScrapeAsync([Description("Full page URL")] string url)
    {
        using var http = httpFactory.CreateClient("ai");
        try
        {
            var html = await http.GetStringAsync(url);
            html = Regex.Replace(html, "<(script|style)[^>]*>.*?</\\1>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            var text = Regex.Replace(html, "<[^>]+>", " ");
            text = System.Net.WebUtility.HtmlDecode(Regex.Replace(text, @"\s+", " ")).Trim();
            return text.Length > 6000 ? text[..6000] + " …(truncated)" : text;
        }
        catch (Exception e) { return $"Could not fetch {url}: {e.Message}"; }
    }

    [KernelFunction("read_file_from_url")]
    [Description("Reads a text-based file (txt, csv, json, md, netlist) from a URL and returns its content.")]
    public async Task<string> ReadFileAsync(string url)
    {
        using var http = httpFactory.CreateClient("ai");
        try
        {
            var text = await http.GetStringAsync(url);
            return text.Length > 8000 ? text[..8000] + " …(truncated)" : text;
        }
        catch (Exception e) { return $"Could not read {url}: {e.Message}"; }
    }
}

/// <summary>Queries LiteCircuit's own data: component library, projects, BOM.</summary>
public class DataPlugin(IDbContextFactory<AppDbContext> dbFactory, BomService bom)
{
    [KernelFunction("search_components")]
    [Description("Parametric search of the component library by text, category, max price or min stock.")]
    public async Task<string> SearchComponentsAsync(
        [Description("Free text matched against name/value/footprint; empty for all")] string query = "",
        [Description("Category filter, e.g. Resistor, MCU, Sensor; empty for all")] string category = "",
        [Description("Maximum unit price in USD; 0 = no limit")] double maxPrice = 0,
        [Description("Minimum stock; 0 = no limit")] int minStock = 0)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var q = db.Components.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(category)) q = q.Where(c => c.Category == category);
        if (maxPrice > 0) q = q.Where(c => c.PriceUsd <= (decimal)maxPrice);
        if (minStock > 0) q = q.Where(c => c.Stock >= minStock);
        if (!string.IsNullOrWhiteSpace(query))
            q = q.Where(c => c.Name.Contains(query) || c.Value.Contains(query) || c.Footprint.Contains(query));
        var rows = await q.OrderBy(c => c.PriceUsd).Take(15).ToListAsync();
        return rows.Count == 0
            ? "No matching components."
            : string.Join("\n", rows.Select(c =>
                $"{c.Name} | {c.Category} | {c.Value} | {c.Footprint} | {c.Manufacturer} | ${c.PriceUsd} | stock {c.Stock} | {c.Source}"));
    }

    [KernelFunction("list_projects")]
    [Description("Lists the user's PCB projects with component/track counts.")]
    public async Task<string> ListProjectsAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var projects = await db.Projects.AsNoTracking().OrderByDescending(p => p.UpdatedAt).Take(20).ToListAsync();
        if (projects.Count == 0) return "No projects yet.";
        return string.Join("\n", projects.Select(p =>
        {
            var b = PcbJson.ParseBoard(p.BoardJson);
            return $"{p.Name} (id {p.Id}): {b.Components.Count} components, {b.Tracks.Count} tracks, {b.Width}x{b.Height}mm, updated {p.UpdatedAt:yyyy-MM-dd}";
        }));
    }

    [KernelFunction("project_bom")]
    [Description("Returns the bill of materials with prices for a project by its name or id.")]
    public async Task<string> ProjectBomAsync(string projectNameOrId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var p = Guid.TryParse(projectNameOrId, out var id)
            ? await db.Projects.FindAsync(id)
            : await db.Projects.FirstOrDefaultAsync(x => x.Name.Contains(projectNameOrId));
        if (p is null) return $"Project '{projectNameOrId}' not found.";
        var lines = await bom.GenerateAsync(PcbJson.ParseBoard(p.BoardJson));
        return $"BOM for {p.Name} (total ${lines.Sum(l => l.LineTotal):0.##}):\n" +
               string.Join("\n", lines.Select(l => $"{l.Qty}x {l.Value} {l.Footprint} ({l.Refs}) — ${l.LineTotal:0.###} [{l.Manufacturer}, stock {l.Stock}]"));
    }
}

/// <summary>Design-aware functions: DRC, board statistics.</summary>
public class DesignPlugin(IDbContextFactory<AppDbContext> dbFactory, DrcService drc)
{
    [KernelFunction("run_drc")]
    [Description("Runs the design rule check on a project's board and reports violations.")]
    public async Task<string> RunDrcAsync(string projectNameOrId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var p = Guid.TryParse(projectNameOrId, out var id)
            ? await db.Projects.FindAsync(id)
            : await db.Projects.FirstOrDefaultAsync(x => x.Name.Contains(projectNameOrId));
        if (p is null) return $"Project '{projectNameOrId}' not found.";
        var violations = drc.Run(PcbJson.ParseBoard(p.BoardJson));
        return violations.Count == 0
            ? $"DRC clean for {p.Name} — no violations."
            : $"{violations.Count} violation(s) in {p.Name}:\n" +
              string.Join("\n", violations.Take(20).Select(v => $"[{v.Severity}] {v.Rule}: {v.Message} @({v.X:0.#},{v.Y:0.#})"));
    }

    [KernelFunction("board_stats")]
    [Description("Board statistics for a project: size, layers, nets, component breakdown.")]
    public async Task<string> BoardStatsAsync(string projectNameOrId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var p = Guid.TryParse(projectNameOrId, out var id)
            ? await db.Projects.FindAsync(id)
            : await db.Projects.FirstOrDefaultAsync(x => x.Name.Contains(projectNameOrId));
        if (p is null) return $"Project '{projectNameOrId}' not found.";
        var b = PcbJson.ParseBoard(p.BoardJson);
        var byType = b.Components.GroupBy(c => c.Type).Select(g => $"{g.Key}: {g.Count()}");
        return $"{p.Name}: {b.Width}x{b.Height}mm, {b.CopperLayers.Count()} copper layers, " +
               $"{b.Components.Count} components ({string.Join(", ", byType)}), " +
               $"{b.Tracks.Count} tracks, {b.Vias.Count} vias, nets: {string.Join(", ", b.Nets)}";
    }
}
