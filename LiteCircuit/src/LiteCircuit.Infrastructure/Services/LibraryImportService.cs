using System.Text.RegularExpressions;
using LiteCircuit.Core.Models;
using LiteCircuit.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace LiteCircuit.Infrastructure.Services;

/// <summary>
/// Imports/updates library parts from internet sources:
/// - official KiCad symbol libraries (GitLab, .kicad_sym s-expression files)
/// - any CSV reachable by URL (LCSC/JLC exports, vendor lists, own catalogs)
/// Existing parts (matched by Name) are never overwritten — only new parts are added.
/// </summary>
public class LibraryImportService(IHttpClientFactory httpFactory, IDbContextFactory<AppDbContext> dbFactory)
{
    public record ImportResult(int Added, int Skipped, int Parsed, string Message)
    {
        public bool Ok => Parsed > 0;
    }

    /// <summary>Curated official KiCad symbol libraries with a category mapping.</summary>
    public static readonly (string Lib, string Category, string Hint)[] KiCadLibraries =
    {
        ("Sensor", "Sensor", "generic sensors"),
        ("Sensor_Temperature", "Sensor", "temperature sensors"),
        ("Sensor_Motion", "Sensor", "IMU / motion"),
        ("MCU_Microchip_ATmega", "MCU", "ATmega MCUs"),
        ("MCU_ST_STM32F1", "MCU", "STM32F1 MCUs"),
        ("MCU_ST_STM32G0", "MCU", "STM32G0 MCUs"),
        ("MCU_Espressif", "MCU", "ESP32 family"),
        ("RF_Module", "Module", "radio modules (ESP, LoRa, BT)"),
        ("Regulator_Linear", "Regulator", "LDOs"),
        ("Regulator_Switching", "Regulator", "buck/boost"),
        ("Driver_Motor", "IC", "motor drivers"),
        ("Amplifier_Operational", "IC", "op-amps"),
        ("Amplifier_Audio", "IC", "audio amps"),
        ("Timer", "IC", "timers (555…)"),
        ("Memory_EEPROM", "IC", "EEPROMs"),
        ("Interface_USB", "IC", "USB interface ICs"),
        ("Interface_Expansion", "IC", "I/O expanders"),
        ("Diode", "Diode", "diodes"),
        ("LED", "LED", "LEDs"),
        ("Transistor_FET", "Transistor", "MOSFETs"),
        ("Transistor_BJT", "Transistor", "BJTs"),
        ("Display_Character", "Display", "character LCDs"),
        ("Display_Graphic", "Display", "graphic displays"),
        ("Connector", "Connector", "generic connectors"),
        ("Switch", "Switch", "switches"),
        ("Battery_Management", "Regulator", "chargers / fuel gauges"),
    };

    // ---- KiCad ------------------------------------------------------------------

    // GitLab project id of kicad/libraries/kicad-symbols
    private const string KiCadProjectId = "21545491";

    public async Task<ImportResult> ImportKiCadAsync(string libraryName, CancellationToken ct = default)
    {
        libraryName = libraryName.Trim();
        if (!Regex.IsMatch(libraryName, @"^[\w.-]+$"))
            return new(0, 0, 0, "Invalid library name.");

        var category = KiCadLibraries.FirstOrDefault(x => x.Lib == libraryName).Category ?? "Generic";
        // Modern KiCad keeps one symbol per file inside <lib>.kicad_symdir/ — pull the
        // whole directory as a single zip archive instead of hundreds of raw requests.
        var url = $"https://gitlab.com/api/v4/projects/{KiCadProjectId}/repository/archive.zip" +
                  $"?path={Uri.EscapeDataString(libraryName + ".kicad_symdir")}&sha=master";

        byte[] zipBytes;
        try
        {
            using var http = httpFactory.CreateClient("ai");
            zipBytes = await http.GetByteArrayAsync(url, ct);
        }
        catch (Exception e)
        {
            return new(0, 0, 0, $"Download failed for '{libraryName}': {e.Message}");
        }

        var parts = new List<LibComponent>();
        var extendsMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(zipBytes));
            foreach (var entry in zip.Entries)
            {
                if (!entry.FullName.EndsWith(".kicad_sym", StringComparison.OrdinalIgnoreCase)) continue;
                using var reader = new StreamReader(entry.Open());
                parts.AddRange(ParseKiCadSymbols(await reader.ReadToEndAsync(ct), category, extendsMap));
                if (parts.Count >= 1000) break;
            }
        }
        catch (Exception e)
        {
            return new(0, 0, 0, $"Could not read archive for '{libraryName}': {e.Message}");
        }

        // Alias symbols ((extends "PARENT")) carry no pins of their own — inherit the parent's.
        var pinsByName = parts.ToDictionary(p => p.Name, p => p.Pins, StringComparer.OrdinalIgnoreCase);
        foreach (var p in parts)
        {
            var name = p.Name;
            var hops = 0;
            while (extendsMap.TryGetValue(name, out var parent) && hops++ < 5)
            {
                if (pinsByName.TryGetValue(parent, out var pins) && pins > 0 && !extendsMap.ContainsKey(parent))
                {
                    p.Pins = pins;
                    break;
                }
                name = parent;
            }
        }

        if (parts.Count == 0)
            return new(0, 0, 0, $"No symbols found in '{libraryName}' — check the library name (e.g. Timer, Sensor, RF_Module).");
        return await UpsertAsync(parts, $"KiCad · {libraryName}");
    }

    /// <summary>
    /// Minimal .kicad_sym reader: extracts top-level symbol names, pin counts and datasheets.
    /// (Full s-expression parsing is not needed for catalog metadata.)
    /// </summary>
    internal static List<LibComponent> ParseKiCadSymbols(string text, string category,
        Dictionary<string, string>? extendsMap = null)
    {
        var result = new List<LibComponent>();
        // Top-level symbols look like:  (symbol "NAME" ...   — sub-units are "NAME_0_1" etc.
        var matches = Regex.Matches(text, "\\(symbol \"([^\"]+)\"");
        var tops = matches.Where(m => !Regex.IsMatch(m.Groups[1].Value, @"_\d+_\d+$")).ToList();

        for (var i = 0; i < tops.Count && result.Count < 1000; i++)
        {
            var name = tops[i].Groups[1].Value;
            var start = tops[i].Index;
            var end = i + 1 < tops.Count ? tops[i + 1].Index : text.Length;
            var block = text[start..Math.Min(end, text.Length)];

            var pins = Regex.Matches(block, @"\(pin\s").Count;
            var extends = Regex.Match(block, "\\(extends \"([^\"]+)\"").Groups[1].Value;
            if (extends.Length > 0 && extendsMap is not null) extendsMap[name] = extends;
            var ds = Regex.Match(block, "\\(property \"Datasheet\"\\s+\"([^\"]*)\"").Groups[1].Value;
            var keywords = Regex.Match(block, "\\(property \"ki_keywords\"\\s+\"([^\"]*)\"").Groups[1].Value;
            if (ds == "~") ds = "";

            result.Add(new LibComponent
            {
                Name = name,
                Category = category,
                Value = keywords.Length is > 0 and <= 60 ? keywords : name,
                Footprint = "",
                Manufacturer = "-",
                Source = "KiCad",
                DatasheetUrl = ds,
                Pins = pins > 0 ? pins : 2,
                PriceUsd = 0,
                Stock = 0,
            });
        }
        return result;
    }

    // ---- CSV --------------------------------------------------------------------

    /// <summary>
    /// Imports parts from a CSV URL. Recognized headers (case-insensitive):
    /// name, category, value, footprint, manufacturer, pins, price, stock, datasheet, source.
    /// Only "name" is required.
    /// </summary>
    public async Task<ImportResult> ImportCsvAsync(string url, CancellationToken ct = default)
    {
        string text;
        try
        {
            using var http = httpFactory.CreateClient("ai");
            text = await http.GetStringAsync(url, ct);
        }
        catch (Exception e)
        {
            return new(0, 0, 0, $"Download failed: {e.Message}");
        }

        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
        if (lines.Count < 2) return new(0, 0, 0, "CSV needs a header row and at least one data row.");

        var headers = SplitCsv(lines[0]).Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Col(params string[] names) => headers.FindIndex(h => names.Any(n => h.Contains(n)));
        int iName = Col("name", "part"), iCat = Col("category"), iVal = Col("value"),
            iFp = Col("footprint", "package"), iMfr = Col("manufacturer", "mfr", "brand"),
            iPins = Col("pins", "pin count"), iPrice = Col("price"), iStock = Col("stock", "qty"),
            iDs = Col("datasheet", "url"), iSrc = Col("source");
        if (iName < 0) return new(0, 0, 0, "CSV must have a 'name' (or 'part') column.");

        var parts = new List<LibComponent>();
        foreach (var line in lines.Skip(1).Take(2000))
        {
            var f = SplitCsv(line);
            string Get(int i) => i >= 0 && i < f.Count ? f[i].Trim() : "";
            var name = Get(iName);
            if (name.Length == 0) continue;
            parts.Add(new LibComponent
            {
                Name = name,
                Category = Get(iCat) is { Length: > 0 } c ? c : "Imported",
                Value = Get(iVal),
                Footprint = Get(iFp),
                Manufacturer = Get(iMfr) is { Length: > 0 } m ? m : "-",
                Source = Get(iSrc) is { Length: > 0 } s ? s : "Vendor",
                DatasheetUrl = Get(iDs),
                Pins = int.TryParse(Get(iPins), out var p) ? p : 2,
                PriceUsd = decimal.TryParse(Get(iPrice), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var pr) ? pr : 0,
                Stock = int.TryParse(Get(iStock), out var st) ? st : 0,
            });
        }
        if (parts.Count == 0) return new(0, 0, 0, "No usable rows found in the CSV.");
        return await UpsertAsync(parts, "CSV import");
    }

    private static List<string> SplitCsv(string line)
    {
        var fields = new List<string>();
        var cur = new System.Text.StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                else inQuotes = !inQuotes;
            }
            else if (ch == ',' && !inQuotes) { fields.Add(cur.ToString()); cur.Clear(); }
            else cur.Append(ch);
        }
        fields.Add(cur.ToString());
        return fields;
    }

    // ---- shared upsert -------------------------------------------------------------

    private async Task<ImportResult> UpsertAsync(List<LibComponent> parts, string sourceLabel)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var existing = (await db.Components.Select(c => c.Name).ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fresh = parts
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .Where(p => !existing.Contains(p.Name))
            .ToList();
        if (fresh.Count > 0)
        {
            db.Components.AddRange(fresh);
            await db.SaveChangesAsync();
        }
        var skipped = parts.Count - fresh.Count;
        return new(fresh.Count, skipped, parts.Count,
            $"{sourceLabel}: parsed {parts.Count} part(s) — added {fresh.Count} new, {skipped} already in the library.");
    }
}
