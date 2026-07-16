# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
dotnet build                                    # build the whole solution (LiteCircuit.slnx)
dotnet run --project src/LiteCircuit.Web        # run (add --urls http://localhost:5210 to pin the port)
```

There are no tests yet. Smoke-test via the REST API after `dotnet run`:
`GET /api/templates`, `POST /api/projects {"name","templateKey"}`, `POST /api/projects/{id}/autoroute`,
`GET /api/projects/{id}/drc|gerber.zip|bom.csv`. Swagger UI at `/swagger`.

Seeded demo login: `admin@litecircuit.dev` / `Admin123$` (created in `DbSeeder`). First run creates
`litecircuit.db` (SQLite) automatically via `EnsureCreated` — there are **no EF migrations**; schema
changes require deleting the db file (dev) or adding migrations properly.

## Architecture

Three projects: `Core` (domain, no dependencies) ← `Infrastructure` (EF/storage/engineering/AI) ← `Web` (Blazor Server, .NET 10).

**The central contract is the design-document JSON** (`Core/Pcb/BoardModels.cs`, camelCase via `PcbJson.Options`):
a `PcbProject` DB row stores `SchematicJson` and `BoardJson` strings. That JSON is consumed by **four**
independent consumers that must stay in sync when the schema changes:

1. C# services (DRC, router, Gerber, BOM, SPICE, STL) — typed models
2. `wwwroot/js/schematic.js` — SVG schematic editor
3. `wwwroot/js/pcb.js` — canvas PCB editor
4. `wwwroot/js/viewer3d.js` — Three.js renderer (Three loaded via import map in `App.razor`, CDN r160)

Connectivity truth is `SchComponent.PinNets` (net name per pin), not the drawn wires; SPICE and
board generation read `PinNets`. `PcbFactory.Make(type, ref, value, footprint, ...)` is the only place
footprint pad geometry is defined — templates (`TemplateService`) and AI generation (`DesignAiService`)
both go through it.

**AI layer**: all four LLM providers (OpenAI/Anthropic/Gemini/Ollama) run through Semantic Kernel's
OpenAI connector against OpenAI-compatible endpoints — provider switching is config-only
(`AiOptions.Resolve()`). Kernel plugins live in `Infrastructure/Ai/Plugins.cs`. AI features must
degrade gracefully when no API key is configured (`ElectraKernelFactory.Create` throws
`InvalidOperationException` with guidance; callers catch and surface the message).

**Blazor render modes**: interactive pages declare `@rendermode InteractiveServer` individually;
the Account pages (Login/Register/Forgot/Reset) are **static SSR forms** and must stay that way —
`SignInManager` needs an unstarted HTTP response. Editors talk to their JS modules through the
`init(dotnetRef, hostId, json) / getJson() / setJson(json) / destroy()` contract; keep pointer-level
interaction in JS, only load/save/analysis crosses SignalR.

**Provider switching**: `Database:Provider` (Sqlite/SqlServer/Postgres/MySql, `DatabaseSetup`) and
`Storage:Provider` (FileSystem/AzureBlob/S3/MinIO, `StorageSetup`). Known issue: Pomelo MySQL provider
is EF9 against our EF10 (NU1608 warning) — MySQL is compile-supported but untested at runtime;
bump Pomelo when its EF10 release ships.

## Conventions

- UI text/copy is English; the user communicates in Indonesian or English; `requirements.md` (Indonesian) is the product spec.
- Design tokens live in `wwwroot/css/app.css` (`--copper`, `--trace`, dark/light via `data-theme` on `<html>`); fonts: Chakra Petch (display), IBM Plex Sans/Mono.
- Coordinates are millimetres, origin top-left, pad offsets component-relative (rotate via `Pad.AbsoluteOn`).
