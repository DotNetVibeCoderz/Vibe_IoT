# Editors

All three views share one project: **Schematic → PCB → 3D** tabs at the top of every editor page.

## Schematic editor

| Tool | Use |
|---|---|
| ＋ Place | Pick a type in the dropdown, click the canvas to drop it (auto ref: R1, C1, U1…) |
| 〰 Wire | Click to add points; **double-click to finish** and name the net (GND, VCC, SDA…) |
| ▲ Select | Drag components; **double-click a part** to edit its value |
| ✕ Delete | Click a component or wire to remove it |

- Wheel = zoom, right-drag = pan. Everything snaps to a 5-unit grid.
- **Block select**: in select mode, drag on empty canvas to rubber-band select multiple components; **Shift+click** adds/removes from the selection; the whole selection drags together.
- **Keyboard** (also listed in the bar under the canvas):

  | Key | Action |
  |---|---|
  | `S` / `P` / `W` / `X` | Switch tool: select / place / wire / delete |
  | `E` | Show / hide the properties panel |
  | `← → ↑ ↓` | Nudge selection by one grid step (`Shift` = 4 steps) |
  | `R` | Rotate selection 90° |
  | `Del` / `Backspace` | Delete selection |
  | `Ctrl+A` | Select all |
  | `Enter` | Finish the wire being drawn |
  | `Esc` | Cancel wire / clear selection |

- **Properties panel** (right side, visible by default, toggle with `E` or the ☰ button): edit the selected part's parameters — reference, type-aware value (resistance, capacitance, voltage, frequency, LED color…) with common-value suggestions, a *pick from library* dropdown for ICs/sensors/modules/displays that fills the part name from the component catalog, and rotation. Multi-selection shows the group with bulk delete.
- When a wire endpoint lands on a component pin, that pin's `pinNet` is set — this drives SPICE and board generation.
- **✨ AI generate** replaces the schematic *and* the board from a text description (needs an AI key).
- **⎘ Commit** saves and snapshots to History.

## PCB editor

| Tool | Use |
|---|---|
| ▲ Move | Drag components (grid snap); **double-click rotates 90°** |
| ⌐ Track | Click points; start on a pad to inherit its net; **double-click to finish** (end on a pad to bind the net) |
| ◉ Via | Click to drop a 0.6/0.3 mm via |
| ✕ Delete | Click a track or via |

- **Multi-layer**: the layer selector lists every copper layer in the stackup — F.Cu (copper-orange), B.Cu (blue), inner layers (In1.Cu, In2.Cu… each with its own color), inactive layers dimmed. Open **≣ Stackup** to switch the board between **2 / 4 / 6 / 8 copper layers**; tracks on removed layers fall back to B.Cu. Gerber export emits every copper layer (`L1 Top`, `L2 Inr`, …). The auto-router works on F.Cu/B.Cu; inner layers are for manual routing and planes.
- Track width and grid pitch selectors are in the toolbar.
- Dashed blue lines = **ratsnest** (nets that still need routing).
- **⚡ Auto-route** runs the grid router server-side and reloads the board.
- **☑ DRC** draws ✕ markers at each violation and lists them in a side panel.
- **🔮 AI review** asks Electra for thermal/EMI/decoupling problems standard DRC can't see.
- **≣ Stackup** shows the layer stack; rules and stackup can be edited via API/scripting.

## 3D viewer

- Left-drag orbit, wheel zoom, right-drag pan (OrbitControls with damping).
- Toggle auto-rotation and component visibility from the toolbar.
- **⬇ STL for MCAD** exports the board + component envelopes for SolidWorks / Fusion 360 enclosure work.

Rendering: soldermask-green substrate, metallic copper tracks (top copper-orange, bottom bronze), gold pads, silver through-holes, per-type component bodies (modules get a shield-can lid, LEDs glow with their value color).
