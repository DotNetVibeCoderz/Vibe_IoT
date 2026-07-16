# Architecture

## Projects

| Project | Role |
|---|---|
| `LiteCircuit.Core` | Dependency-free domain: design documents, EF entities, `IFileStorage` |
| `LiteCircuit.Infrastructure` | EF Core + Identity stores, storage providers, all engineering services, Semantic Kernel AI |
| `LiteCircuit.Web` | Blazor Server UI, Identity auth, Minimal API + Swagger, JS editors |

## The design documents

A `PcbProject` row owns two JSON documents (camelCase, schema in `Core/Pcb/BoardModels.cs`):

- **Schematic** — `components[{ref,type,value,x,y,pinNets[]}]`, `wires[{net,points}]`. `pinNets` is the source of truth for connectivity (SPICE, board generation); wires are the visual capture layer and write into `pinNets` when an endpoint lands on a pin.
- **Board** — `width/height` (mm), `layers[]` (stackup), `components[{…,footprint,pads[{net,x,y,w,h,drill,shape}]}]`, `tracks[{net,layer,width,points}]`, `vias[]`, `nets[]`, `rules{}`. Pad positions are component-relative; `Pad.AbsoluteOn()` applies rotation.

The same JSON is consumed by three renderers: `pcb.js` (2D canvas editor), `viewer3d.js` (Three.js) and the C# services (DRC, Gerber, router). **If you change the schema, update all three.**

`PcbFactory` builds pads from a footprint name (R0805 … LQFP-48, ESP32-MODULE, HDR-1xN). Both `TemplateService` and the AI design generator go through it, so every generated board is routable and manufacturable.

## Engineering services (Infrastructure/Services)

| Service | What it does |
|---|---|
| `DrcService` | Clearance (pad/track pairs), widths, drills, annular rings, edge distance, unrouted nets |
| `AutoRouterService` | Lee/BFS wave router on a 0.5 mm grid, F.Cu with B.Cu fallback |
| `GerberService` | RS-274X copper + profile, Excellon drill, pick-and-place CSV, ZIP |
| `BomService` | Groups board parts, matches library rows for price/stock, CSV |
| `SpiceService` | Netlist from `pinNets` + MNA DC solver (R, V, I; diode ≈ 100 Ω) |
| `DfmService` / `ComplianceService` | Manufacturability heuristics; IPC class, RoHS, UL notes |
| `SignalIntegrityService` | Microstrip impedance (IPC-2141), differential-pair skew report |
| `VersionControlService` | Commit / log / restore / diff snapshots (`DesignCommit` rows) |
| `TemplateService` | 10 programmatic reference designs |
| `ScriptingService` | Roslyn C# scripts with `Board`/`Schematic`/`Print` globals, 10 s timeout |
| `McadService` | ASCII STL export (board + component envelopes) |

## AI layer (Infrastructure/Ai)

`ElectraKernelFactory` builds a Semantic Kernel from `Ai:` config. All four providers
(OpenAI, Anthropic, Gemini, Ollama) are consumed through their **OpenAI-compatible chat endpoints**,
so one SK connector serves all — switching provider is config-only.

Plugins registered on every kernel: `utility` (time, math, Ohm's law), `web` (Tavily search, page scrape, file fetch), `data` (component search, projects, BOM), `design` (DRC, board stats). `ElectraChatService` streams replies, persists sessions/messages, and passes image attachments as `ImageContent` URLs. `DesignAiService` does JSON-constrained schematic generation, error prediction, placement advice and BOM sourcing.

## Web layer

- Interactive pages are `@rendermode InteractiveServer`; Identity pages are static SSR forms (required for `SignInManager`).
- Editors are plain JS ES modules loaded per page via `IJSObjectReference` (`init/getJson/setJson/destroy` contract) — this keeps pointer-level interactions off the SignalR circuit for latency.
- Three.js resolves through an import map in `App.razor` (CDN r160).
- Minimal API in `Api/ApiEndpoints.cs`; Swagger UI at `/swagger`.
- DB provider selection in `DatabaseSetup` (EnsureCreated at startup — no migrations); storage selection in `StorageSetup`.
