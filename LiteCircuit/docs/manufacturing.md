# Manufacturing & analysis

Everything on this page is available in the **Fab** tab of a project and via [REST API](api.md).

## Fabrication outputs

**⬇ Gerber + drill + pos (.zip)** contains:

| File | Format | Contents |
|---|---|---|
| `<name>-F_Cu.gbr` / `-B_Cu.gbr` | Gerber RS-274X (mm, 4.6) | Copper: tracks as draws, pads as flashes, vias |
| `<name>-Edge_Cuts.gbr` | Gerber | Board outline profile |
| `<name>.drl` | Excellon (metric) | All drills, tools grouped by diameter |
| `<name>-pos.csv` | CSV | Pick-and-place: ref, value, footprint, XY, rotation, side |

Assembly drawings: use the pick-and-place CSV plus the PDF-quality 3D view; the STL export doubles as a mechanical reference.

## BOM

Grouped by (type, value, footprint), refs collected per line, matched against the library for
manufacturer / unit price / stock / source, with a board total. Export as CSV or ask
**✨ AI sourcing advice** for cheaper or better-stocked alternatives.

## DFM analysis

Heuristics run on the real geometry: smallest drill (0.3/0.25 mm thresholds), narrowest track (5 mil),
acute-angle trace joints (acid traps), hole count & distinct drill sizes, component density per cm²,
SMD/THT mix. Severity: info / warning / error.

## Compliance

- **IPC-2221** clearance class assessment from the board's design rules; annular-ring rule check.
- **RoHS**: lead-free finish reminder + BOM certificate note.
- **UL**: substrate material check (FR4 → 94V-0 note).
- Stackup summary (copper count, total thickness) for fab confirmation.

## High-speed helpers

`SignalIntegrityService` (available from scripting/API code):

- `MicrostripImpedance(width, dielectricHeight, er)` — IPC-2141 approximation.
- `CheckDifferentialPairs(board)` — finds `*_P/_N` net pairs and reports length skew vs a 1 mm budget.

## SPICE

The **SPICE** tab shows the netlist generated from schematic `pinNets`
(R/C/L/D/V/I supported; other parts are commented). *Run DC analysis* solves the
operating point with modified nodal analysis and lists node voltages.
For transient/AC analysis, download the netlist and run it in ngspice or LTspice.
