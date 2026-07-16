# Getting started

## Requirements

- .NET 10 SDK
- A modern browser (the 3D viewer needs WebGL; Three.js is loaded from CDN)
- Optional: an LLM API key (OpenAI / Anthropic / Gemini) or a local Ollama install

## Run

```bash
dotnet run --project src/LiteCircuit.Web
```

First start creates `litecircuit.db` (SQLite), seeds the component catalog (~55 parts) and a demo account:

- **Email:** `admin@litecircuit.dev`
- **Password:** `Admin123$`

## Your first board (10 minutes)

1. **Log in**, then open **Templates** and pick *Clock & Weather Station* (ESP32).
2. The **Schematic** tab opens. Double-click any part to edit its value; use the toolbar to place parts or draw wires (double-click ends a wire and asks for the net name).
3. Switch to the **PCB** tab. Drag components into position (grid-snapped), then press **⚡ Auto-route**. Dashed blue lines are the ratsnest (unrouted connections).
4. Press **☑ DRC** — violations appear as ✕ markers on the board and a list in the side panel.
5. Open the **3D** tab to orbit the board. Toggle auto-rotation, hide components, or download the STL.
6. **SPICE** shows the generated netlist; press *Run DC analysis* for node voltages.
7. **Fab** gives you the Gerber ZIP, BOM CSV, DFM report and compliance summary.
8. **History**: type a message and *Commit snapshot* — restore any commit later.

## Meet Electra

Open **⚡ Electra** in the top bar. Without an API key she will tell you what to configure. With one configured (see [configuration.md](configuration.md)) you can:

- ask engineering questions (Ohm's law, decoupling, footprints),
- attach a photo of a schematic and ask questions about it,
- say *"run DRC on Clock & Weather"* — she calls the real DRC engine,
- say *"find a cheaper motor driver in stock"* — she queries the library.

In the schematic editor, **✨ AI generate** turns a description like
*"ESP32 board with BME280 and two status LEDs"* into a placed schematic + board.

---

*LiteCircuit is built by **Gravicode Studios**, led by **Kang Fadhil**.*
