// Renders the extension's webviews outside VS Code for screenshots and visual review: the real webview HTML (through a
// stub of the `vscode` module), VS Code's Dark Modern theme variables, and real data from the iotcom CLI.
// Usage: node test/preview.mjs <out-dir>   → frame-viewer.html, monitor.html
import { mkdirSync, writeFileSync, existsSync, statSync } from "node:fs";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { createRequire } from "node:module";
import Module from "node:module";

const here = dirname(fileURLToPath(import.meta.url));
const out = process.argv[2] ?? join(here, "..", "preview");
mkdirSync(out, { recursive: true });

// --- a minimal `vscode` stub: l10n, env, and webview panels that capture their HTML ---
const panels = [];
const vscodeStub = {
  l10n: { t: (s, ...a) => s.replace(/\{(\d+)\}/g, (_, i) => String(a[Number(i)])) },
  env: { language: "en" },
  ViewColumn: { Beside: 2, Active: -1 },
  Uri: { file: (p) => ({ fsPath: p }) },
  window: {
    createWebviewPanel: () => {
      const panel = { webview: { html: "", postMessage: () => Promise.resolve(true), onDidReceiveMessage: () => ({}) }, onDidDispose: () => ({}), reveal() {}, dispose() {} };
      panels.push(panel);
      return panel;
    },
  },
};
const resolve = Module._resolveFilename;
Module._resolveFilename = function (request, ...rest) {
  return request === "vscode" ? "vscode" : resolve.call(this, request, ...rest);
};
const require = createRequire(import.meta.url);
require.cache["vscode"] = { id: "vscode", filename: "vscode", loaded: true, exports: vscodeStub };

const { FrameViewer } = require("../out/frameViewer.js");
const { TrafficMonitor } = require("../out/monitor.js");
const { IotcomRpc } = require("../out/rpc.js");

const dll = ["Release", "Debug"].map((c) => join(here, "..", "..", "iotcom-cli", "bin", c, "net10.0", "iotcom.dll")).filter(existsSync)
  .sort((a, b) => statSync(b).mtimeMs - statSync(a).mtimeMs)[0];
const rpc = new IotcomRpc("dotnet", [dll]);
const info = await rpc.initialize();
const decoders = [...new Set(info.protocols.flatMap((p) => p.decoders))];

// VS Code "Dark Modern" theme variables (the webview host injects these in the real editor).
const THEME = `
  --vscode-font-family: "Segoe UI", system-ui, sans-serif; --vscode-font-size: 13px;
  --vscode-editor-font-family: Consolas, "Cascadia Mono", monospace; --vscode-editor-font-size: 13px;
  --vscode-foreground: #CCCCCC; --vscode-descriptionForeground: #9D9D9D; --vscode-editor-background: #1F1F1F;
  --vscode-input-background: #313131; --vscode-input-foreground: #CCCCCC; --vscode-input-border: #3C3C3C;
  --vscode-button-background: #0078D4; --vscode-button-foreground: #FFFFFF; --vscode-button-hoverBackground: #026EC1;
  --vscode-button-secondaryBackground: #313131; --vscode-button-secondaryForeground: #CCCCCC;
  --vscode-panel-border: #2B2B2B; --vscode-list-hoverBackground: #2A2D2E; --vscode-focusBorder: #0078D4; --vscode-errorForeground: #F85149;`;

function standalone(html, script) {
  // Inject the theme, a fake acquireVsCodeApi, and a script that replays real messages; relax the CSP for the preview.
  return html
    .replace(/<meta http-equiv="Content-Security-Policy"[^>]*>/, "")
    .replace("<head>", `<head><style>:root{${THEME}}</style><script>window.acquireVsCodeApi=()=>({postMessage(){}});</script>`)
    .replace(/<\/body>/, `<script>${script}</script></body>`);
}

// 1) Frame viewer with a real Modbus/TCP response decoded by the CLI.
const hex = "000100000007010304002A0101";
FrameViewer.show(() => Promise.resolve(rpc), decoders, { protocol: "modbus-tcp", hex });
const decoded = await rpc.decode("modbus-tcp", hex, "response");
writeFileSync(join(out, "frame-viewer.html"), standalone(panels.at(-1).webview.html,
  `document.getElementById("direction").value="response"; window.postMessage({type:"decoded",result:${JSON.stringify(decoded)}},"*");
   setTimeout(()=>{const r=document.querySelector("tbody tr:nth-child(5)"); r&&r.dispatchEvent(new Event("mouseenter"));},200);`));

// 2) Traffic monitor with ~3 s of real frames from the CAN ECU simulator (OBD-II over ISO-TP).
const frames = [];
const off = rpc.onFrame((f) => frames.push(f));
const monitor = await TrafficMonitor.start(rpc, "sim:can");
await new Promise((r) => setTimeout(r, 3200));
off();
const html = panels.at(-1).webview.html;
writeFileSync(join(out, "monitor.html"), standalone(html,
  `window.postMessage({type:"frames",frames:${JSON.stringify(frames)},total:${frames.length}},"*");
   setTimeout(()=>{const r=document.querySelectorAll(".row")[3]; r&&r.click();},200);`));
void monitor;
await rpc.dispose();
console.log(`wrote ${out}/frame-viewer.html and monitor.html (${frames.length} frames)`);
process.exit(0);
