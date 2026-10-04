// IoTCom Gateway dashboard — vanilla JS, no build step. Data: /api/* (REST) and /api/stream (Server-Sent Events).
"use strict";

const $ = (id) => document.getElementById(id);
const store = {
  get(k) { try { return localStorage.getItem(k); } catch { return null; } },
  set(k, v) { try { localStorage.setItem(k, v); } catch { /* private mode */ } },
};

const text = {
  en: {
    subtitle: "Modbus → MQTT bridge", plant: "Virtual PLC", temperature: "Temperature", setpoint: "Setpoint", motor: "Main motor",
    running: "Running", stopped: "Stopped", run: "Run", stop: "Stop", humidity: "Humidity", pressure: "Pressure", power: "Power",
    energy: "Energy", counter: "Produced", door: "Door closed", estop: "E-stop healthy", pump: "Cooling pump", hot: "High temperature",
    trend: "Last 5 minutes", frames: "Frames on the wire", pause: "Pause", resume: "Resume",
    "k-address": "address", "k-function": "function", "k-length": "length", "k-data": "data", "k-checksum": "checksum",
    waiting: "Waiting for frames from the PLC…", offline: "PLC offline: ", writesOff: "Writes are disabled on this gateway (Gateway:AllowWrites).",
    publishing: "Publishing SenML to MQTT topic ", wrote: "Sent to PLC: ",
  },
  id: {
    subtitle: "Jembatan Modbus → MQTT", plant: "PLC virtual", temperature: "Suhu", setpoint: "Setpoint", motor: "Motor utama",
    running: "Berjalan", stopped: "Berhenti", run: "Jalan", stop: "Stop", humidity: "Kelembapan", pressure: "Tekanan", power: "Daya",
    energy: "Energi", counter: "Produksi", door: "Pintu tertutup", estop: "E-stop normal", pump: "Pompa pendingin", hot: "Suhu tinggi",
    trend: "5 menit terakhir", frames: "Frame di jalur", pause: "Jeda", resume: "Lanjut",
    "k-address": "alamat", "k-function": "fungsi", "k-length": "panjang", "k-data": "data", "k-checksum": "checksum",
    waiting: "Menunggu frame dari PLC…", offline: "PLC offline: ", writesOff: "Penulisan dinonaktifkan di gateway ini (Gateway:AllowWrites).",
    publishing: "Mengirim SenML ke topik MQTT ", wrote: "Dikirim ke PLC: ",
  },
};

let lang = store.get("iotcom.lang") === "id" ? "id" : "en";
let info = null;
let current = null;
let history = [];
let paused = false;
const t = (k) => text[lang][k] ?? k;

function applyLanguage() {
  document.documentElement.lang = lang;
  document.querySelectorAll("[data-i18n]").forEach((el) => { el.textContent = t(el.dataset.i18n); });
  $("lang").textContent = lang === "en" ? "ID" : "EN";
  $("pause").textContent = t(paused ? "resume" : "pause");
  if (info) $("credit").textContent = lang === "en" ? info.creditEn : info.creditId;
  if (current) render(current);
}

function applyTheme(theme) {
  if (theme) document.documentElement.dataset.theme = theme; else delete document.documentElement.dataset.theme;
}

// ---------- plant ----------
const scaleMin = 15, scaleMax = 35;
const pct = (v) => `${Math.min(100, Math.max(0, ((v - scaleMin) / (scaleMax - scaleMin)) * 100))}%`;
const lamp = (el, state) => { el.className = "lamp-dot" + (state ? " " + state : ""); };

function render(s) {
  current = s;
  $("temp").textContent = s.temperatureC.toFixed(1);
  $("sp").textContent = s.setpointC.toFixed(1);
  $("temp-needle").style.left = pct(s.temperatureC);
  $("sp-marker").style.left = pct(s.setpointC);
  $("alarm-band").style.left = pct(s.setpointC + 3);
  $("rpm").textContent = String(s.motorRpm).padStart(4, " ");
  $("hum").textContent = s.humidityPct.toFixed(1);
  $("press").textContent = s.pressureHpa;
  $("power").textContent = s.powerKw.toFixed(2);
  $("energy").textContent = s.energyKwh.toFixed(3);
  $("counter").textContent = s.counter.toLocaleString(lang === "id" ? "id-ID" : "en-US");
  $("motor-state").textContent = t(s.motorRun ? "running" : "stopped");
  lamp($("motor-lamp"), s.motorRun ? "on" : "");
  const btn = $("motor-btn");
  btn.setAttribute("aria-checked", String(s.motorRun));
  btn.querySelector(".switch-label").textContent = t(s.motorRun ? "stop" : "run");
  lamp($("il-door"), s.doorClosed ? "on" : "fault");
  lamp($("il-estop"), s.eStopOk ? "on" : "fault");
  lamp($("il-pump"), s.pump ? "warn" : "");
  lamp($("il-hot"), s.highTemp ? "fault" : "");
  const status = $("status");
  if (!s.online) { status.textContent = t("offline") + (s.error ?? ""); status.className = "status err"; }
  else if (info) { status.textContent = t("publishing") + info.mqttTopic; status.className = "status"; }
}

function drawChart() {
  const svg = $("chart");
  const pts = history.slice(-600);
  if (pts.length < 2) return;
  const W = 600, H = 160;
  const temps = pts.map((p) => p.temperatureC).concat(pts.map((p) => p.setpointC));
  let lo = Math.floor(Math.min(...temps) - 1), hi = Math.ceil(Math.max(...temps) + 1);
  if (hi - lo < 4) hi = lo + 4;
  const maxPow = Math.max(8, ...pts.map((p) => p.powerKw));
  const x = (i) => (i / (pts.length - 1)) * W;
  const yT = (v) => H - ((v - lo) / (hi - lo)) * (H - 10) - 5;
  const yP = (v) => H - (v / maxPow) * (H * 0.45) - 2;
  const line = (f) => pts.map((p, i) => `${i ? "L" : "M"}${x(i).toFixed(1)},${f(p).toFixed(1)}`).join("");
  let grid = "";
  for (let g = 1; g < 4; g++) grid += `<line class="grid" x1="0" x2="${W}" y1="${(H / 4) * g}" y2="${(H / 4) * g}"/>`;
  svg.innerHTML = `${grid}<path class="pow" d="${line((p) => yP(p.powerKw))}"/><path class="spl" d="${line((p) => yT(p.setpointC))}"/><path class="temp" d="${line((p) => yT(p.temperatureC))}"/>`;
}

// ---------- frame lane ----------
const maxFrames = 18;
let pendingFrames = [];

function frameElement(f) {
  const li = $("frame-tpl").content.firstElementChild.cloneNode(true);
  const time = new Date(f.time);
  li.querySelector("time").textContent = time.toLocaleTimeString(lang === "id" ? "id-ID" : "en-GB", { hour12: false }) + "." + String(time.getMilliseconds()).padStart(3, "0");
  const dir = li.querySelector(".dir");
  dir.textContent = f.direction === "out" ? "TX" : "RX";
  dir.classList.add(f.direction);
  li.querySelector(".sum").textContent = f.summary ?? f.protocol;
  const bytes = f.hex ? f.hex.split(" ") : [];
  const box = li.querySelector(".bytes");
  let cursor = 0;
  const addField = (kind, name, from, to, value) => {
    if (to <= from) return;
    const span = document.createElement("span");
    span.className = `field k-${kind}`;
    span.title = value ? `${name}: ${value}` : name;
    for (let i = from; i < to && i < bytes.length; i++) {
      const b = document.createElement("b");
      b.textContent = bytes[i];
      span.appendChild(b);
    }
    box.appendChild(span);
  };
  for (const fld of f.fields) {
    addField("header", "", cursor, fld.offset);
    addField(fld.kind, fld.name, fld.offset, fld.offset + fld.length, fld.value);
    cursor = fld.offset + fld.length;
  }
  addField("data", "", cursor, bytes.length);
  return li;
}

function flushFrames() {
  if (paused || pendingFrames.length === 0) return;
  const lane = $("lane");
  lane.querySelector(".empty")?.remove();
  for (const f of pendingFrames.slice(-maxFrames)) lane.prepend(frameElement(f));
  pendingFrames = [];
  while (lane.children.length > maxFrames) lane.lastElementChild.remove();
}

// ---------- endpoints ----------
async function refreshEndpoints() {
  try {
    const list = await (await fetch("api/endpoints")).json();
    const ul = $("lamps");
    ul.innerHTML = "";
    for (const e of list) {
      const li = document.createElement("li");
      const state = e.state === "Connected" || e.state === "Listening" ? "on" : e.state === "Connecting" ? "warn" : "fault";
      li.innerHTML = `<span class="lamp-dot ${state}"></span><span></span><small></small>`;
      li.children[1].textContent = e.name;
      li.children[2].textContent = e.protocol;
      li.title = `${e.name} · ${e.protocol} · ${e.state}`;
      ul.appendChild(li);
    }
  } catch { /* gateway restarting */ }
}

// ---------- commands ----------
async function post(path, body) {
  const res = await fetch(path, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
  if (!res.ok) {
    const problem = await res.json().catch(() => ({ detail: res.statusText }));
    $("status").textContent = problem.detail;
    $("status").className = "status err";
  }
}

function bindControls() {
  $("motor-btn").addEventListener("click", () => current && post("api/motor", { run: !current.motorRun }));
  $("sp-up").addEventListener("click", () => current && post("api/setpoint", { celsius: current.setpointC + 0.5 }));
  $("sp-down").addEventListener("click", () => current && post("api/setpoint", { celsius: current.setpointC - 0.5 }));
  $("pause").addEventListener("click", () => { paused = !paused; $("pause").textContent = t(paused ? "resume" : "pause"); });
  $("lang").addEventListener("click", () => { lang = lang === "en" ? "id" : "en"; store.set("iotcom.lang", lang); applyLanguage(); });
  $("theme").addEventListener("click", () => {
    const dark = document.documentElement.dataset.theme
      ? document.documentElement.dataset.theme === "dark"
      : matchMedia("(prefers-color-scheme: dark)").matches;
    const next = dark ? "light" : "dark";
    applyTheme(next);
    store.set("iotcom.theme", next);
  });
}

async function start() {
  applyTheme(store.get("iotcom.theme"));
  bindControls();
  info = await (await fetch("api/info")).json();
  $("line").textContent = info.line;
  $("plc-addr").textContent = `modbus-tcp ${info.plcAddress}`;
  $("ver").textContent = `${info.product} ${info.version}`;
  if (!info.allowWrites) {
    for (const id of ["motor-btn", "sp-up", "sp-down"]) $(id).disabled = true;
    $("status").textContent = t("writesOff");
  }
  applyLanguage();
  $("lane").innerHTML = `<li class="empty">${t("waiting")}</li>`;

  history = await (await fetch("api/history")).json();
  if (history.length) render(history[history.length - 1]);
  drawChart();
  pendingFrames = await (await fetch("api/frames")).json();
  flushFrames();

  const es = new EventSource("api/stream");
  es.addEventListener("snapshot", (e) => {
    const s = JSON.parse(e.data);
    history.push(s);
    if (history.length > 600) history.shift();
    render(s);
  });
  es.addEventListener("frame", (e) => pendingFrames.push(JSON.parse(e.data)));

  setInterval(flushFrames, 250);
  setInterval(drawChart, 1000);
  refreshEndpoints();
  setInterval(refreshEndpoints, 2000);
}

start().catch((err) => {
  $("status").textContent = `Cannot reach the gateway API: ${err.message}`;
  $("status").className = "status err";
});
