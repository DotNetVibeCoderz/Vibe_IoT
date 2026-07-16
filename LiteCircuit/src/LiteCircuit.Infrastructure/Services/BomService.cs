using System.Text;
using LiteCircuit.Core.Models;
using LiteCircuit.Core.Pcb;
using LiteCircuit.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace LiteCircuit.Infrastructure.Services;

public record BomLine(string Refs, int Qty, string Type, string Value, string Footprint,
    string Manufacturer, decimal UnitPrice, int Stock, string Source)
{
    public decimal LineTotal => UnitPrice * Qty;
}

/// <summary>Generates a Bill of Materials from a board, enriched with library pricing/stock.</summary>
public class BomService(IDbContextFactory<AppDbContext> dbFactory)
{
    public async Task<List<BomLine>> GenerateAsync(Board board)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var catalog = await db.Components.AsNoTracking().ToListAsync();

        return board.Components
            .GroupBy(c => (c.Type, c.Value, c.Footprint))
            .OrderBy(g => g.First().Ref)
            .Select(g =>
            {
                var match = catalog.FirstOrDefault(lc =>
                                lc.Footprint.Equals(g.Key.Footprint, StringComparison.OrdinalIgnoreCase) &&
                                lc.Value.Equals(g.Key.Value, StringComparison.OrdinalIgnoreCase))
                            ?? catalog.FirstOrDefault(lc =>
                                lc.Footprint.Equals(g.Key.Footprint, StringComparison.OrdinalIgnoreCase));
                return new BomLine(
                    string.Join(", ", g.Select(c => c.Ref).OrderBy(r => r, StringComparer.OrdinalIgnoreCase)),
                    g.Count(), g.Key.Type, g.Key.Value, g.Key.Footprint,
                    match?.Manufacturer ?? "-", match?.PriceUsd ?? 0, match?.Stock ?? 0, match?.Source ?? "-");
            })
            .ToList();
    }

    public static string ToCsv(IEnumerable<BomLine> lines)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Refs,Qty,Type,Value,Footprint,Manufacturer,UnitPriceUSD,LineTotalUSD,Stock,Source");
        foreach (var l in lines)
            sb.AppendLine($"\"{l.Refs}\",{l.Qty},{l.Type},\"{l.Value}\",{l.Footprint},\"{l.Manufacturer}\",{l.UnitPrice:0.####},{l.LineTotal:0.####},{l.Stock},{l.Source}");
        return sb.ToString();
    }
}
