// The frame lane, shared by the frame viewer and the traffic monitor webviews: byte tiles coloured by field kind,
// each field group bracketed with its name and decoded value — the IoTCom.Net visual signature, adapted to VS Code themes.
import type { FrameField } from "./rpc";

/** Field colours (same as the CLI, the gateway dashboard and the Gallery). Ink is the text colour on the tile. */
export const KIND_COLORS: Record<FrameField["kind"], { tile: string; ink: string }> = {
  header: { tile: "#8A9098", ink: "#FFFFFF" },
  address: { tile: "#2F6FD6", ink: "#FFFFFF" },
  function: { tile: "#F2A900", ink: "#1D1D1D" },
  length: { tile: "#7A5BB5", ink: "#FFFFFF" },
  data: { tile: "#C9CBC4", ink: "#2B3036" },
  checksum: { tile: "#2E9E5B", ink: "#FFFFFF" },
  delimiter: { tile: "#8A9098", ink: "#FFFFFF" },
  error: { tile: "#D23B2F", ink: "#FFFFFF" },
};

export const LANE_CSS = /* css */ `
  :root { color-scheme: light dark; }
  body {
    margin: 0; padding: 18px 22px 28px;
    font-family: var(--vscode-font-family); font-size: var(--vscode-font-size);
    color: var(--vscode-foreground); background: var(--vscode-editor-background);
  }
  h1 { font-size: 1.05em; font-weight: 600; letter-spacing: .02em; margin: 0 0 14px; }
  .muted { color: var(--vscode-descriptionForeground); }
  .mono { font-family: var(--vscode-editor-font-family); font-size: var(--vscode-editor-font-size); }
  input, select, button {
    font: inherit; color: var(--vscode-input-foreground); background: var(--vscode-input-background);
    border: 1px solid var(--vscode-input-border, var(--vscode-panel-border)); border-radius: 3px; padding: 4px 8px;
  }
  button { background: var(--vscode-button-background); color: var(--vscode-button-foreground); border: none; cursor: pointer; }
  button:hover { background: var(--vscode-button-hoverBackground); }
  button.secondary { background: var(--vscode-button-secondaryBackground); color: var(--vscode-button-secondaryForeground); }
  :focus-visible { outline: 1px solid var(--vscode-focusBorder); outline-offset: 1px; }

  /* The lane: field groups side by side, each a row of byte tiles over a bracket with the field's name. */
  .lane { display: flex; flex-wrap: wrap; gap: 10px 6px; align-items: flex-start; }
  .group { display: flex; flex-direction: column; gap: 4px; border-radius: 4px; transition: opacity .12s; }
  .tiles { display: flex; gap: 2px; flex-wrap: wrap; max-width: 34em; }
  .tile {
    font-family: var(--vscode-editor-font-family); font-size: calc(var(--vscode-editor-font-size) * .95);
    min-width: 2.2ch; padding: 3px 4px; text-align: center; border-radius: 3px; font-weight: 600;
  }
  .bracket {
    border-top: 2px solid currentColor; padding-top: 3px; font-size: .82em; line-height: 1.25;
    white-space: nowrap; overflow: hidden; text-overflow: ellipsis; max-width: 34em;
  }
  .bracket b { font-weight: 600; }
  .lane.dim .group { opacity: .28; }
  .lane.dim .group.on { opacity: 1; }
  @media (prefers-reduced-motion: reduce) { .group { transition: none; } }
  body.vscode-high-contrast .tile { outline: 1px solid var(--vscode-contrastBorder); }
`;

/** Client-side renderer (embedded in the webview script): builds a lane from hex and fields. */
export const LANE_SCRIPT = /* js */ `
  const KIND = ${JSON.stringify(KIND_COLORS)};
  function renderLane(container, hex, fields, maxBytes) {
    container.textContent = "";
    container.className = "lane";
    const bytes = hex.match(/.{2}/g) || [];
    const groups = [];
    let cursor = 0;
    const sorted = [...fields].sort((a, b) => a.offset - b.offset);
    for (const f of sorted) {
      if (f.offset > cursor) groups.push({ name: "", kind: "data", offset: cursor, length: f.offset - cursor });
      groups.push(f);
      cursor = Math.max(cursor, f.offset + f.length);
    }
    if (cursor < bytes.length) groups.push({ name: "", kind: "data", offset: cursor, length: bytes.length - cursor });
    let shown = 0;
    groups.forEach((g, index) => {
      if (maxBytes && shown >= maxBytes) return;
      const c = KIND[g.kind] || KIND.data;
      const el = document.createElement("div");
      el.className = "group";
      el.dataset.index = String(index);
      const tiles = document.createElement("div");
      tiles.className = "tiles";
      const slice = bytes.slice(g.offset, g.offset + g.length);
      for (const b of slice) {
        if (maxBytes && shown >= maxBytes) { const more = document.createElement("span"); more.className = "muted tile"; more.textContent = "…"; tiles.appendChild(more); break; }
        const t = document.createElement("span");
        t.className = "tile";
        t.style.background = c.tile;
        t.style.color = c.ink;
        t.textContent = b;
        tiles.appendChild(t);
        shown++;
      }
      el.appendChild(tiles);
      if (g.name && !maxBytes) {
        const br = document.createElement("div");
        br.className = "bracket";
        br.style.color = c.tile;
        const name = document.createElement("b");
        name.textContent = g.name;
        br.appendChild(name);
        if (g.value) { const v = document.createElement("span"); v.className = "muted"; v.textContent = " " + g.value; br.appendChild(v); }
        br.title = g.name + (g.value ? " = " + g.value : "") + " · offset " + g.offset + ", " + g.length + " B";
        el.appendChild(br);
      }
      container.appendChild(el);
    });
    return groups;
  }
`;

/** A random nonce for the webview Content-Security-Policy. */
export function nonce(): string {
  const chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
  let s = "";
  for (let i = 0; i < 32; i++) s += chars[Math.floor(Math.random() * chars.length)];
  return s;
}

/** Escapes text for HTML attributes and content. */
export function escapeHtml(s: string): string {
  return s.replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]!);
}
