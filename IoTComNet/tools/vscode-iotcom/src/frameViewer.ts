// Frame / Hex Viewer: paste or select bytes, pick a protocol, see the frame lane and the field table.
import * as vscode from "vscode";
import { LANE_CSS, LANE_SCRIPT, escapeHtml, nonce } from "./lane";
import type { IotcomRpc } from "./rpc";

export class FrameViewer {
  private static current: FrameViewer | undefined;
  private readonly panel: vscode.WebviewPanel;

  private constructor(private readonly rpc: () => Promise<IotcomRpc>, private readonly decoders: string[], initial?: { protocol?: string; hex?: string }) {
    this.panel = vscode.window.createWebviewPanel("iotcom.frameViewer", vscode.l10n.t("Frame viewer"), vscode.ViewColumn.Beside, { enableScripts: true });
    this.panel.webview.html = this.html(initial);
    this.panel.onDidDispose(() => (FrameViewer.current = undefined));
    this.panel.webview.onDidReceiveMessage(async (m: { type: string; protocol: string; hex: string; direction: "request" | "response" }) => {
      if (m.type !== "decode") return;
      try {
        const result = await (await this.rpc()).decode(m.protocol, m.hex, m.direction);
        void this.panel.webview.postMessage({ type: "decoded", result });
      } catch (e) {
        void this.panel.webview.postMessage({ type: "error", message: (e as Error).message });
      }
    });
  }

  /** Opens (or reuses) the viewer, optionally decoding right away. */
  static show(rpc: () => Promise<IotcomRpc>, decoders: string[], initial?: { protocol?: string; hex?: string }): void {
    if (FrameViewer.current) {
      FrameViewer.current.panel.reveal();
      if (initial?.hex) void FrameViewer.current.panel.webview.postMessage({ type: "load", ...initial });
      return;
    }
    FrameViewer.current = new FrameViewer(rpc, decoders, initial);
  }

  private html(initial?: { protocol?: string; hex?: string }): string {
    const n = nonce();
    const t = (s: string) => escapeHtml(vscode.l10n.t(s));
    const options = this.decoders.map((d) => `<option value="${escapeHtml(d)}"${d === initial?.protocol ? " selected" : ""}>${escapeHtml(d)}</option>`).join("");
    return /* html */ `<!DOCTYPE html>
<html lang="${escapeHtml(vscode.env.language)}"><head><meta charset="utf-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; script-src 'nonce-${n}';">
<meta name="viewport" content="width=device-width, initial-scale=1">
<style>${LANE_CSS}
  form { display: grid; grid-template-columns: auto 1fr auto auto; gap: 8px; align-items: center; margin-bottom: 18px; }
  form textarea { grid-column: 1 / -1; min-height: 4.2em; resize: vertical; font-family: var(--vscode-editor-font-family);
    color: var(--vscode-input-foreground); background: var(--vscode-input-background); border: 1px solid var(--vscode-input-border, var(--vscode-panel-border)); border-radius: 3px; padding: 6px 8px; }
  #summary { font-weight: 600; margin: 0 0 12px; min-height: 1.3em; }
  #error { color: var(--vscode-errorForeground); margin: 0 0 12px; }
  table { border-collapse: collapse; margin-top: 22px; width: 100%; max-width: 64em; }
  th { text-align: left; font-weight: 600; color: var(--vscode-descriptionForeground); font-size: .85em; padding: 4px 10px 6px 0; border-bottom: 1px solid var(--vscode-panel-border); }
  td { padding: 4px 10px 4px 0; border-bottom: 1px solid var(--vscode-panel-border); vertical-align: top; }
  tr:hover td { background: var(--vscode-list-hoverBackground); }
  .swatch { display: inline-block; width: .8em; height: .8em; border-radius: 2px; margin-right: 6px; vertical-align: -1px; }
</style></head>
<body>
<h1>${t("Frame viewer")}</h1>
<form id="form">
  <label for="protocol" class="muted">${t("Protocol")}</label>
  <select id="protocol">${options}</select>
  <select id="direction" title="${t("Direction")}"><option value="request">${t("request")}</option><option value="response">${t("response")}</option></select>
  <button type="submit">${t("Decode")}</button>
  <textarea id="hex" spellcheck="false" placeholder="00 01 00 00 00 06 01 03 00 00 00 0A">${escapeHtml(initial?.hex ?? "")}</textarea>
</form>
<p id="error" hidden></p>
<p id="summary" class="mono"></p>
<div id="lane" class="lane"><span class="muted">${t("Paste a frame in hex and choose its protocol.")}</span></div>
<table id="table" hidden><thead><tr><th>${t("Field")}</th><th>${t("Offset")}</th><th>${t("Bytes")}</th><th>${t("Value")}</th></tr></thead><tbody></tbody></table>
<script nonce="${n}">
  ${LANE_SCRIPT}
  const vscode = acquireVsCodeApi();
  const $ = (id) => document.getElementById(id);
  function decode() {
    vscode.postMessage({ type: "decode", protocol: $("protocol").value, hex: $("hex").value, direction: $("direction").value });
  }
  $("form").addEventListener("submit", (e) => { e.preventDefault(); decode(); });
  window.addEventListener("message", (e) => {
    const m = e.data;
    if (m.type === "load") { if (m.protocol) $("protocol").value = m.protocol; $("hex").value = m.hex; decode(); return; }
    if (m.type === "error") { $("error").hidden = false; $("error").textContent = m.message; return; }
    if (m.type !== "decoded") return;
    $("error").hidden = true;
    $("summary").textContent = m.result.summary;
    const lane = $("lane");
    const groups = renderLane(lane, m.result.hex, m.result.fields, 0);
    const body = $("table").querySelector("tbody");
    body.textContent = "";
    groups.forEach((g, i) => {
      if (!g.name) return;
      const tr = document.createElement("tr");
      const bytes = (m.result.hex.match(/.{2}/g) || []).slice(g.offset, g.offset + g.length).join(" ");
      const c = KIND[g.kind] || KIND.data;
      tr.innerHTML = "<td><span class=swatch></span></td><td class=mono></td><td class=mono></td><td></td>";
      tr.children[0].firstChild.style.background = c.tile;
      tr.children[0].append(g.name);
      tr.children[1].textContent = g.offset;
      tr.children[2].textContent = bytes.length > 48 ? bytes.slice(0, 48) + "…" : bytes;
      tr.children[3].textContent = g.value || "";
      tr.addEventListener("mouseenter", () => { lane.classList.add("dim"); lane.querySelector('[data-index="' + i + '"]')?.classList.add("on"); });
      tr.addEventListener("mouseleave", () => { lane.classList.remove("dim"); lane.querySelectorAll(".on").forEach((x) => x.classList.remove("on")); });
      body.appendChild(tr);
    });
    $("table").hidden = body.children.length === 0;
  });
  if ($("hex").value.trim()) decode();
</script>
</body></html>`;
  }
}
