using LiteCircuit.Core.Models;
using LiteCircuit.Core.Pcb;
using LiteCircuit.Infrastructure.Ai;
using LiteCircuit.Infrastructure.Data;
using LiteCircuit.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace LiteCircuit.Web.Api;

/// <summary>Minimal-API surface for external integrations (documented via Swagger at /swagger).</summary>
public static class ApiEndpoints
{
    public static void MapLiteCircuitApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        // ---- Projects -----------------------------------------------------
        var projects = api.MapGroup("/projects").WithTags("Projects");

        projects.MapGet("/", async (AppDbContext db) =>
            await db.Projects.AsNoTracking()
                .OrderByDescending(p => p.UpdatedAt)
                .Select(p => new { p.Id, p.Name, p.Description, p.TemplateKey, p.CreatedAt, p.UpdatedAt })
                .ToListAsync())
            .WithSummary("List all projects");

        projects.MapGet("/{id:guid}", async (Guid id, AppDbContext db) =>
            await db.Projects.FindAsync(id) is { } p ? Results.Ok(p) : Results.NotFound())
            .WithSummary("Get a project including schematic and board JSON");

        projects.MapPost("/", async (CreateProjectRequest req, AppDbContext db, TemplateService templates) =>
        {
            var t = templates.Find(req.TemplateKey ?? "blank") ?? templates.Find("blank")!;
            var (sch, board) = t.Build();
            board.Name = req.Name;
            var p = new PcbProject
            {
                Name = req.Name,
                Description = req.Description ?? "",
                TemplateKey = t.Key,
                SchematicJson = PcbJson.Serialize(sch),
                BoardJson = PcbJson.Serialize(board),
            };
            db.Projects.Add(p);
            await db.SaveChangesAsync();
            return Results.Created($"/api/projects/{p.Id}", new { p.Id, p.Name });
        }).WithSummary("Create a project from blank or a template key");

        projects.MapPut("/{id:guid}", async (Guid id, UpdateProjectRequest req, AppDbContext db) =>
        {
            var p = await db.Projects.FindAsync(id);
            if (p is null) return Results.NotFound();
            if (req.Name is not null) p.Name = req.Name;
            if (req.Description is not null) p.Description = req.Description;
            if (req.SchematicJson is not null) p.SchematicJson = req.SchematicJson;
            if (req.BoardJson is not null) p.BoardJson = req.BoardJson;
            p.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).WithSummary("Update project metadata or design documents");

        projects.MapDelete("/{id:guid}", async (Guid id, AppDbContext db) =>
            await db.Projects.Where(p => p.Id == id).ExecuteDeleteAsync() > 0
                ? Results.NoContent() : Results.NotFound())
            .WithSummary("Delete a project");

        // ---- Engineering outputs -------------------------------------------
        projects.MapGet("/{id:guid}/drc", async (Guid id, AppDbContext db, DrcService drc) =>
        {
            var p = await db.Projects.FindAsync(id);
            return p is null ? Results.NotFound() : Results.Ok(drc.Run(PcbJson.ParseBoard(p.BoardJson)));
        }).WithSummary("Run design rule check");

        projects.MapPost("/{id:guid}/autoroute", async (Guid id, AppDbContext db, AutoRouterService router) =>
        {
            var p = await db.Projects.FindAsync(id);
            if (p is null) return Results.NotFound();
            var (board, report) = router.Route(PcbJson.ParseBoard(p.BoardJson));
            p.BoardJson = PcbJson.Serialize(board);
            p.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(new { report, tracks = board.Tracks.Count });
        }).WithSummary("Auto-route the board");

        projects.MapGet("/{id:guid}/gerber.zip", async (Guid id, AppDbContext db, GerberService gerber) =>
        {
            var p = await db.Projects.FindAsync(id);
            return p is null
                ? Results.NotFound()
                : Results.File(gerber.ExportZip(PcbJson.ParseBoard(p.BoardJson)), "application/zip", $"{p.Name}-gerber.zip");
        }).WithSummary("Download fabrication files (Gerber, drill, pick-and-place)");

        projects.MapGet("/{id:guid}/bom.csv", async (Guid id, AppDbContext db, BomService bom) =>
        {
            var p = await db.Projects.FindAsync(id);
            if (p is null) return Results.NotFound();
            var lines = await bom.GenerateAsync(PcbJson.ParseBoard(p.BoardJson));
            return Results.File(System.Text.Encoding.UTF8.GetBytes(BomService.ToCsv(lines)), "text/csv", $"{p.Name}-bom.csv");
        }).WithSummary("Download bill of materials as CSV");

        projects.MapGet("/{id:guid}/board.stl", async (Guid id, AppDbContext db, McadService mcad) =>
        {
            var p = await db.Projects.FindAsync(id);
            return p is null
                ? Results.NotFound()
                : Results.File(System.Text.Encoding.ASCII.GetBytes(mcad.ExportStl(PcbJson.ParseBoard(p.BoardJson))),
                    "model/stl", $"{p.Name}.stl");
        }).WithSummary("Download 3D model as STL for MCAD (SolidWorks / Fusion 360)");

        projects.MapGet("/{id:guid}/netlist", async (Guid id, AppDbContext db, SpiceService spice) =>
        {
            var p = await db.Projects.FindAsync(id);
            return p is null
                ? Results.NotFound()
                : Results.Text(spice.BuildNetlist(PcbJson.ParseSchematic(p.SchematicJson)), "text/plain");
        }).WithSummary("Export SPICE netlist");

        // ---- Component library ---------------------------------------------
        api.MapGet("/components", async (string? q, string? category, decimal? maxPrice, int? minStock, AppDbContext db) =>
        {
            var query = db.Components.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(q))
                query = query.Where(c => c.Name.Contains(q) || c.Value.Contains(q) || c.Footprint.Contains(q));
            if (!string.IsNullOrWhiteSpace(category)) query = query.Where(c => c.Category == category);
            if (maxPrice > 0) query = query.Where(c => c.PriceUsd <= maxPrice);
            if (minStock > 0) query = query.Where(c => c.Stock >= minStock);
            return await query.OrderBy(c => c.Category).ThenBy(c => c.PriceUsd).Take(100).ToListAsync();
        }).WithTags("Library").WithSummary("Parametric component search");

        api.MapPost("/library/import/kicad/{lib}", async (string lib, LibraryImportService importer) =>
        {
            var r = await importer.ImportKiCadAsync(lib);
            return r.Ok ? Results.Ok(r) : Results.BadRequest(r);
        }).WithTags("Library").WithSummary("Import/update parts from an official KiCad symbol library (e.g. Sensor, RF_Module)");

        api.MapPost("/library/import/csv", async (CsvImportRequest req, LibraryImportService importer) =>
        {
            var r = await importer.ImportCsvAsync(req.Url);
            return r.Ok ? Results.Ok(r) : Results.BadRequest(r);
        }).WithTags("Library").WithSummary("Import/update parts from a CSV URL (needs a 'name' column)");

        // ---- Templates -----------------------------------------------------
        api.MapGet("/templates", (TemplateService templates) =>
                Results.Ok(templates.All.Select(t => new { t.Key, t.Name, t.Chip, t.Description })))
            .WithTags("Templates").WithSummary("List available board templates");

        // ---- AI --------------------------------------------------------------
        var ai = api.MapGroup("/ai").WithTags("AI");

        ai.MapPost("/generate", async (GenerateRequest req, DesignAiService designAi) =>
        {
            var (sch, board, message) = await designAi.GenerateFromTextAsync(req.Description);
            return sch is null
                ? Results.BadRequest(new { message })
                : Results.Ok(new { message, schematic = sch, board });
        }).WithSummary("Generate a schematic + board from a natural-language description");

        ai.MapPost("/chat", async (ChatRequest req, ElectraChatService electra, HttpContext http) =>
        {
            var session = await electra.CreateSessionAsync(req.UserId ?? "api", "API chat");
            var baseUrl = $"{http.Request.Scheme}://{http.Request.Host}";
            var sb = new System.Text.StringBuilder();
            await foreach (var chunk in electra.StreamReplyAsync(session.Id, req.Message, new(), baseUrl))
                sb.Append(chunk);
            return Results.Ok(new { sessionId = session.Id, reply = sb.ToString() });
        }).WithSummary("One-shot chat with Electra (non-streaming)");
    }

    public record CreateProjectRequest(string Name, string? Description, string? TemplateKey);
    public record CsvImportRequest(string Url);
    public record UpdateProjectRequest(string? Name, string? Description, string? SchematicJson, string? BoardJson);
    public record GenerateRequest(string Description);
    public record ChatRequest(string Message, string? UserId);
}
