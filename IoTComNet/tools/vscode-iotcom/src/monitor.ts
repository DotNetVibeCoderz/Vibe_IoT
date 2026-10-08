// Traffic Monitor: a live "bus tape" of decoded frames from a CLI monitor source, with a filter and pcapng export.
import * as vscode from "vscode";
import { LANE_CSS, LANE_SCRIPT, escapeHtml, nonce } from "./lane";
import type { FrameEvent, IotcomRpc } from "./rpc";

export class TrafficMonitor {
  private readonly panel: vscode.WebviewPanel;
  private buffer: FrameEvent[] = [];
  private readonly flush: NodeJS.Timeout;
  private unsubscribe: (() => void) | undefined;
  private monitorId: string | undefined;
  private frames = 0;

  private constructor(private readonly rpc: IotcomRpc, private readonly source: string) {
    this.panel = vscode.window.createWebviewPanel("iotcom.monitor", vscode.l10n.t("Traffic monitor · {0}", source), vscode.ViewColumn.Active, {
      enableScripts: true,
      retainContextWhenHidden: true,
    });
    this.panel.webview.html = this.html();
    // Batch frames: a busy bus would otherwise flood the webview with messages.
    this.flush = setInterval(() => {
      if (this.buffer.length === 0) return;
      void this.panel.webview.postMessage({ type: "frames", frames: this.buffer.splice(0), total: this.frames });
    }, 150);
    this.panel.onDidDispose(() => void this.stop());
    this.panel.webview.onDidReceiveMessage(async (m: { type: string }) => {
      if (m.type === "stop") await this.stop();
      if (m.type === "save") await this.save();
    });
  }

  /** Starts a monitor for `source` (e.g. sim:can, can:socketcan:can0, mavlink:udp:14550) and opens its panel. */
  static async start(rpc: IotcomRpc, source: string): Promise<TrafficMonitor> {
    const m = new TrafficMonitor(rpc, source);
    m.unsubscribe = rpc.onFrame((f) => {
      if (f.monitor !== m.monitorId) return;
      m.frames++;
      m.buffer.push(f);
      if (m.buffer.length > 2000) m.buffer.splice(0, m.buffer.length - 2000);
    });
    try {
      m.monitorId = (await rpc.startMonitor(source)).id;
    } catch (e) {
      m.panel.dispose();
      throw e;
    }
    return m;
  }

  private async stop(): Promise<void> {
    clearInterval(this.flush);
    this.unsubscribe?.();
    this.unsubscribe = undefined;
    if (this.monitorId) {
      const id = this.monitorId;
      this.monitorId = undefined;
      try {
        await this.rpc.stopMonitor(id);
      } catch {
        /* the CLI may already be gone */
      }
      void this.panel.webview.postMessage({ type: "stopped" });
    }
  }

  private async save(): Promise<void> {
    if (!this.monitorId) return;
    const target = await vscode.window.showSaveDialog({
      defaultUri: vscode.Uri.file(`${this.source.replace(/[^a-z0-9]+/gi, "-")}-${new Date().toISOString().slice(0, 19).replace(/[:T]/g, "")}.pcapng`),
      filters: { "pcapng (Wireshark)": ["pcapng"] },
    });
    if (!target) return;
    const r = await this.rpc.saveMonitor(this.monitorId, target.fsPath);
    void vscode.window.showInformationMessage(vscode.l10n.t("Saved {0} frames to {1}", r.frames, r.path));
  }

  private html(): string {
    const n = nonce();
    const t = (s: string) => escapeHtml(vscode.l10n.t(s));
    return /* html */ `<!DOCTYPE html>
<html lang="${escapeHtml(vscode.env.language)}"><head><meta charset="utf-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; script-src 'nonce-${n}';">
<meta name="viewport" content="width=device-width, initial-scale=1">
<style>${LANE_CSS}
  header { display: flex; flex-wrap: wrap; gap: 8px 12px; align-items: center; margin-bottom: 14px; }
  header h1 { margin: 0 auto 0 0; }
  #count { font-variant-numeric: tabular-nums; }
  #filter { min-width: 14em; }
  .tape { display: flex; flex-direction: column; }
  .row { display: grid; grid-template-columns: 7.5em 1.2em 6.5em minmax(10em, 1fr) auto; gap: 10px; align-items: center;
         padding: 5px 4px; border-bottom: 1px solid var(--vscode-panel-border); cursor: pointer; }
  .row:hover { background: var(--vscode-list-hoverBackground); }
  .row .time { font-family: var(--vscode-editor-font-family); color: var(--vscode-descriptionForeground); font-variant-numeric: tabular-nums; }
  .row .dir { font-weight: 700; text-align: center; }
  .row .dir.out { color: #F2A900; }
  .row .dir.in { color: #2F6FD6; }
  .row .proto { font-family: var(--vscode-editor-font-family); color: var(--vscode-descriptionForeground); }
  .row .summary { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .row .mini .tile { font-size: calc(var(--vscode-editor-font-size) * .8); padding: 1px 3px; }
  .detail { padding: 10px 4px 14px 2em; border-bottom: 1px solid var(--vscode-panel-border); }
  .empty { padding: 24px 0; }
  @media (max-width: 700px) { .row { grid-template-columns: 6em 1.2em 1fr; } .row .proto, .row .mini { display: none; } }
</style></head>
<body>
<header>
  <h1>${escapeHtml(vscode.l10n.t("Traffic monitor · {0}", this.source))}</h1>
  <span id="count" class="muted">0 ${t("frames")}</span>
  <input id="filter" type="search" placeholder="${t("Filter frames")}" aria-label="${t("Filter frames")}">
  <button id="save" class="secondary">${t("Save .pcapng")}</button>
  <button id="stop">${t("Stop")}</button>
</header>
<div id="tape" class="tape"><p class="empty muted">${t("Waiting for the first frame…")}</p></div>
<script nonce="${n}">
  ${LANE_SCRIPT}
  const vscode = acquireVsCodeApi();
  const tape = document.getElementById("tape");
  const filter = document.getElementById("filter");
  const MAX_ROWS = 400;
  let open = null;
  // Local wall-clock time with milliseconds (the CLI sends ISO 8601 in UTC).
  function clock(iso) {
    const d = new Date(iso);
    const p = (n, w = 2) => String(n).padStart(w, "0");
    return p(d.getHours()) + ":" + p(d.getMinutes()) + ":" + p(d.getSeconds()) + "." + p(d.getMilliseconds(), 3);
  }
  function matches(f) {
    const q = filter.value.trim().toLowerCase();
    return !q || (f.protocol + " " + (f.summary || "") + " " + f.hex).toLowerCase().includes(q);
  }
  function row(f) {
    const r = document.createElement("div");
    r.className = "row";
    r.tabIndex = 0;
    r._frame = f;
    const time = document.createElement("span"); time.className = "time"; time.textContent = clock(f.time);
    const dir = document.createElement("span"); dir.className = "dir " + f.direction; dir.textContent = f.direction === "out" ? "→" : "←";
    const proto = document.createElement("span"); proto.className = "proto"; proto.textContent = f.protocol;
    const summary = document.createElement("span"); summary.className = "summary"; summary.textContent = f.summary || "";
    const mini = document.createElement("div"); renderLane(mini, f.hex, f.fields, 14); mini.classList.add("mini");
    r.append(time, dir, proto, summary, mini);
    const toggle = () => {
      if (open) { open.remove(); if (open._for === r) { open = null; return; } }
      const d = document.createElement("div"); d.className = "detail"; d._for = r;
      const lane = document.createElement("div"); renderLane(lane, f.hex, f.fields, 0); d.appendChild(lane);
      r.after(d); open = d;
    };
    r.addEventListener("click", toggle);
    r.addEventListener("keydown", (e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); toggle(); } });
    r.hidden = !matches(f);
    return r;
  }
  window.addEventListener("message", (e) => {
    const m = e.data;
    if (m.type === "stopped") { document.getElementById("stop").disabled = true; document.getElementById("stop").textContent = ${JSON.stringify(vscode.l10n.t("Stopped"))}; return; }
    if (m.type !== "frames") return;
    tape.querySelector(".empty")?.remove();
    for (const f of m.frames) tape.prepend(row(f));
    while (tape.children.length > MAX_ROWS) tape.lastElementChild.remove();
    document.getElementById("count").textContent = m.total.toLocaleString() + " ${t("frames")}";
  });
  filter.addEventListener("input", () => { for (const r of tape.querySelectorAll(".row")) r.hidden = !matches(r._frame); });
  document.getElementById("stop").addEventListener("click", () => vscode.postMessage({ type: "stop" }));
  document.getElementById("save").addEventListener("click", () => vscode.postMessage({ type: "save" }));
</script>
</body></html>`;
  }
}
