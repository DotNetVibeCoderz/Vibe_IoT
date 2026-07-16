# REST API

Interactive documentation: **`/swagger`** (Swagger UI, generated from the Minimal API).

Base URL: `http://localhost:5210` (or wherever the app runs). All bodies are JSON.

## Projects

| Method | Route | Description |
|---|---|---|
| GET | `/api/projects` | List projects (metadata) |
| GET | `/api/projects/{id}` | Full project incl. `schematicJson` / `boardJson` |
| POST | `/api/projects` | `{ "name", "description?", "templateKey?" }` — create from blank or template |
| PUT | `/api/projects/{id}` | Patch name/description/schematicJson/boardJson |
| DELETE | `/api/projects/{id}` | Delete |

## Engineering

| Method | Route | Description |
|---|---|---|
| GET | `/api/projects/{id}/drc` | Run DRC, returns violations |
| POST | `/api/projects/{id}/autoroute` | Auto-route and save; returns report |
| GET | `/api/projects/{id}/gerber.zip` | Fabrication bundle |
| GET | `/api/projects/{id}/bom.csv` | Bill of materials |
| GET | `/api/projects/{id}/board.stl` | 3D model for MCAD |
| GET | `/api/projects/{id}/netlist` | SPICE netlist |

## Library & templates

| Method | Route | Description |
|---|---|---|
| GET | `/api/components?q=&category=&maxPrice=&minStock=` | Parametric part search |
| POST | `/api/library/import/kicad/{lib}` | Import new parts from an official KiCad symbol library (e.g. `Timer`, `Sensor`, `RF_Module`) |
| POST | `/api/library/import/csv` | `{ "url" }` — import parts from a CSV URL (needs a `name` column) |
| GET | `/api/templates` | Available template keys |

## AI

| Method | Route | Description |
|---|---|---|
| POST | `/api/ai/generate` | `{ "description" }` → schematic + board JSON |
| POST | `/api/ai/chat` | `{ "message", "userId?" }` → one-shot Electra reply |

## Example

```bash
# Create a robot-arm board, route it, download gerbers
ID=$(curl -s -X POST localhost:5210/api/projects \
  -H "Content-Type: application/json" \
  -d '{"name":"Arm v1","templateKey":"robot-arm"}' | jq -r .id)

curl -s -X POST localhost:5210/api/projects/$ID/autoroute | jq .report
curl -s -o arm-gerber.zip localhost:5210/api/projects/$ID/gerber.zip
```

See `scripts/examples/` for a complete Python client.
