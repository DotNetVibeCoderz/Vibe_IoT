# Scripting & automation

## In-app C# console (`/scripting`)

Roslyn-powered scripts run against a project's board with these globals:

| Global | Type | Notes |
|---|---|---|
| `Board` | `LiteCircuit.Core.Pcb.Board` | Full board document (mutable) |
| `Schematic` | `Schematic` | Schematic document |
| `Print(object)` | method | Writes to the output pane |

Namespaces `System`, `System.Linq`, `System.Collections.Generic`, `LiteCircuit.Core.Pcb` are pre-imported.
Tick **Save changes back to project** to persist `Board` mutations. Scripts time out after 10 s.

### Examples (also in the console's dropdown)

```csharp
// Widen all power tracks
var power = new[] { "VCC", "GND", "3V3", "5V" };
foreach (var t in Board.Tracks.Where(t => power.Contains(t.Net)))
    t.Width = Math.Max(t.Width, 0.5);
Print("done");
```

```csharp
// Add a mounting-hole style via in each corner
foreach (var (x, y) in new[] { (4.0,4.0), (Board.Width-4,4.0), (4.0,Board.Height-4), (Board.Width-4,Board.Height-4) })
    Board.Vias.Add(new Via { X = x, Y = y, Drill = 2.2, Dia = 4.0 });
Print("4 mounting holes added");
```

## Python automation (REST API)

No SDK needed — plain HTTP. Full sample: [`scripts/examples/litecircuit_client.py`](../scripts/examples/litecircuit_client.py).

```python
import requests
BASE = "http://localhost:5210/api"

p = requests.post(f"{BASE}/projects", json={
    "name": "Sensor node", "templateKey": "plant-monitor"}).json()

print(requests.post(f"{BASE}/projects/{p['id']}/autoroute").json()["report"])

drc = requests.get(f"{BASE}/projects/{p['id']}/drc").json()
print(f"{len(drc)} DRC findings")

open("gerber.zip", "wb").write(
    requests.get(f"{BASE}/projects/{p['id']}/gerber.zip").content)
```

## C# external automation

Reference nothing — use `HttpClient` against the same API, or link `LiteCircuit.Core` +
`LiteCircuit.Infrastructure` directly to reuse `GerberService`, `DrcService`, etc. in your own tools.
Sample: [`scripts/examples/BatchExport.csx`](../scripts/examples/BatchExport.csx).
