# LiteCircuit ⚡

**Browser-based PCB design with an AI copilot.** Blazor Server (.NET 10) + Three.js + Semantic Kernel.

> Bahasa Indonesia: [README.id.md](README.id.md)

![stack](https://img.shields.io/badge/.NET-10-512BD4) ![blazor](https://img.shields.io/badge/Blazor-Server-5C2D91) ![threejs](https://img.shields.io/badge/Three.js-r160-000000) ![sk](https://img.shields.io/badge/Semantic%20Kernel-1.78-0078D4)

## What it does

- **Schematic capture** — interactive SVG editor with components, wires, net labels, and AI generation from a plain-text description.
- **PCB layout** — canvas editor with drag-and-drop, grid snapping, ratsnest, multi-layer tracks, vias, and real-time DRC.
- **3D visualization** — Three.js preview of the board, copper, and components; STL export for SolidWorks / Fusion 360.
- **Electra, the AI copilot** — multi-session chat (image + document attachments), auto-router, error prediction, placement advice, BOM sourcing assistant. Providers: **OpenAI, Anthropic, Gemini, Ollama** (selectable in config).
- **Manufacturing outputs** — Gerber RS-274X, Excellon drill, pick-and-place CSV, BOM CSV, DFM analysis, IPC/RoHS/UL compliance checks.
- **SPICE** — netlist export plus a built-in DC operating-point solver.
- **Templates** — 10 ready designs: ESP32 clock & weather, LED running text, motor controller, robot arm, FM radio, MP3 player, mini arcade (TFT), pet feeder, plant monitor.
- **Collaboration** — Git-like design snapshots (commit / restore / diff) and inline comments.
- **Extensibility** — REST API (Minimal APIs + Swagger at `/swagger`), in-app C# scripting console, Python examples.
- **Unified library** — KiCad + vendor parts with parametric search (value, footprint, price, stock), plus **online updates**: pull the latest symbols straight from the official KiCad GitLab libraries or any CSV URL from the Library page.

## Quick start

```bash
# Requires .NET 10 SDK
dotnet run --project src/LiteCircuit.Web
```

Open http://localhost:5000 (or the printed URL) and log in with the demo account:

| Email | Password |
|---|---|
| `admin@litecircuit.dev` | `Admin123$` |

The default configuration uses **SQLite** and **filesystem storage** — zero external dependencies.

## Enable the AI copilot

Edit `src/LiteCircuit.Web/appsettings.json`:

```jsonc
"Ai": {
  "Provider": "Anthropic",          // OpenAI | Anthropic | Gemini | Ollama
  "ApiKey": "sk-...",               // not needed for Ollama
  "Temperature": 0.7
},
"Tavily": { "ApiKey": "tvly-..." }  // optional: internet search for Electra
```

Each provider is consumed through its OpenAI-compatible endpoint, so switching providers is a one-line change. Defaults per provider: `gpt-4o-mini`, `claude-sonnet-5`, `gemini-2.0-flash`, `llama3.2`.

## Switch database / storage

```jsonc
"Database": { "Provider": "Postgres" },   // Sqlite | SqlServer | Postgres | MySql
"Storage":  { "Provider": "MinIO" }       // FileSystem | AzureBlob | S3 | MinIO
```

Connection strings live under `ConnectionStrings` and `Storage:*`. See [docs/configuration.md](docs/configuration.md).

## Documentation

| Doc | Contents |
|---|---|
| [docs/getting-started.md](docs/getting-started.md) | Install, first board, walkthrough |
| [docs/architecture.md](docs/architecture.md) | Solution layout, design documents, data flow |
| [docs/editors.md](docs/editors.md) | Schematic, PCB and 3D editor usage |
| [docs/ai-features.md](docs/ai-features.md) | Electra, kernel functions, NL design |
| [docs/manufacturing.md](docs/manufacturing.md) | Gerber, BOM, DFM, compliance, SPICE |
| [docs/api.md](docs/api.md) | REST API reference |
| [docs/scripting.md](docs/scripting.md) | C# console + Python automation |
| [docs/configuration.md](docs/configuration.md) | Database, storage, AI providers, auth |

## Solution layout

```
src/
  LiteCircuit.Core/            Domain models (board/schematic JSON documents, entities)
  LiteCircuit.Infrastructure/  EF Core (4 DB providers), storage (4 providers),
                               DRC, auto-router, Gerber/drill, BOM, SPICE, DFM,
                               templates, scripting, Semantic Kernel + Electra
  LiteCircuit.Web/             Blazor Server UI, Identity auth, Minimal API + Swagger
docs/                          Documentation
scripts/examples/              Python + C# automation samples
```

## Credits

Built by **Gravicode Studios**, led by **Kang Fadhil**.

## License

MIT — build something and send it to a fab. 🛠
