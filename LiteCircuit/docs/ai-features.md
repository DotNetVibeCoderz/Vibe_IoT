# AI features (Electra)

Electra is LiteCircuit's copilot, built on **Semantic Kernel** with tool calling enabled.

## Chat (`/chat`)

- **Multi-session**: create, delete, or reset (clear messages, keep session). The first message becomes the session title.
- **Attachments**: images are uploaded to the configured storage and sent to the model as image content (vision); documents are uploaded and linked into the message text.
- **Streaming** replies with full markdown rendering — tables, code blocks, task lists, and media embeds (Markdig, raw HTML disabled).
- Persona, temperature, model, and max tokens come from `appsettings.json` (`Ai:` section) and reload without code changes.

## Kernel functions (tools Electra can call)

| Plugin | Functions |
|---|---|
| `utility` | `current_datetime`, `days_between`, `calculate` (math expressions), `ohms_law` |
| `web` | `search_internet` (Tavily), `scrape_page` (readable text), `read_file_from_url` |
| `data` | `search_components` (parametric library search), `list_projects`, `project_bom` |
| `design` | `run_drc` (real DRC engine on a project), `board_stats` |

Example prompts that trigger tools:

> "Cari MCU termurah yang ada WiFi dan stoknya di atas 10 ribu"
> "Run DRC on my Clock & Weather project and explain the top 3 issues"
> "What's today's date, and how many days until 2026-12-31?"

## Natural-language design

`DesignAiService.GenerateFromTextAsync` asks the model for a strict-JSON component list
(types, refs, values, footprints from the known set, `pinNets`), then:

1. builds the schematic with a reading-order layout,
2. builds the board through `PcbFactory` (real pads → routable),
3. sizes the board from the components and places them grid-wise.

Used by **✨ AI generate** in the schematic editor and `POST /api/ai/generate`.

## Design intelligence

- **Error prediction** (`🔮 AI review` in the PCB editor): reviews the placed board for thermal hotspots, EMI, missing decoupling — beyond geometric DRC (which is run first and included as context).
- **Placement advice**: suggests concrete moves ("rotate J1 to the board edge").
- **BOM assistant** (Fab page): reads your BOM, calls `search_components` for cheaper/better-stocked swaps, and reports a markdown table.
- **Learning from past designs**: `list_projects`/`board_stats` let Electra reference your existing boards when advising on a new one.

## Providers

Configured in `Ai:Provider` — all consumed via OpenAI-compatible endpoints with a single connector:

| Provider | Default model | Endpoint |
|---|---|---|
| OpenAI | `gpt-4o-mini` | api.openai.com (default) |
| Anthropic | `claude-sonnet-5` | `https://api.anthropic.com/v1/` |
| Gemini | `gemini-2.0-flash` | `https://generativelanguage.googleapis.com/v1beta/openai/` |
| Ollama | `llama3.2` | `http://localhost:11434/v1` (no key needed) |

Per-provider overrides live under `Ai:Providers:<Name>` (model, endpoint, key). If no key is set, every AI feature degrades to a clear "not configured" message instead of failing.
