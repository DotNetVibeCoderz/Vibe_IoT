// LiteCircuit PCB layout editor — canvas 2D.
// Tools: select (move components), track, via, delete. Wheel zoom, right-drag pan.

let host, canvas, ctx, dotnet, board;
let tool = 'select', activeLayer = 'F.Cu', trackWidth = 0.25, grid = 0.5;
let view = { x: -5, y: -5, scale: 8 };      // world(mm) -> screen: (wx - view.x) * scale
let drag = null, panStart = null;
let trackPts = null, trackNet = '';
let drcMarkers = [];
let resizeObs;

const INNER_COLORS = ['#7bc86c', '#c86cb8', '#c8c26c', '#6cc8b8', '#a06cc8', '#c88a6c'];

function layerColor(name) {
    if (name === 'F.Cu') return '#d08a47';
    if (name === 'B.Cu') return '#58a6c8';
    const m = /^In(\d+)/.exec(name || '');
    return INNER_COLORS[m ? (parseInt(m[1]) - 1) % INNER_COLORS.length : 0];
}

function copperNames() {
    const names = (board.layers || []).filter(l => l.type === 'copper').map(l => l.name);
    return names.length ? names : ['F.Cu', 'B.Cu'];
}

export function init(dotnetRef, hostId, json) {
    dotnet = dotnetRef;
    host = document.getElementById(hostId);
    board = safeParse(json);
    canvas = document.createElement('canvas');
    canvas.style.width = '100%';
    canvas.style.height = '100%';
    host.appendChild(canvas);
    ctx = canvas.getContext('2d');

    canvas.addEventListener('pointerdown', onDown);
    canvas.addEventListener('pointermove', onMove);
    canvas.addEventListener('pointerup', onUp);
    canvas.addEventListener('dblclick', onDbl);
    canvas.addEventListener('wheel', onWheel, { passive: false });
    canvas.addEventListener('contextmenu', e => e.preventDefault());

    resizeObs = new ResizeObserver(() => { sizeCanvas(); draw(); });
    resizeObs.observe(host);
    sizeCanvas();
    fit();
    draw();
}

export function setTool(t) {
    tool = t;
    trackPts = null;
    status(`${t} — ${{
        select: 'drag components; wheel zoom; right-drag pan',
        track: `draw ${trackWidth}mm track on ${activeLayer}: click points, double-click to finish`,
        via: 'click to drop a via',
        delete: 'click a track or via to remove it'
    }[t] || ''}`);
    draw();
}

export function setLayer(l) { activeLayer = l; status(`active layer: ${l}`); draw(); }
export function setTrackWidth(w) { trackWidth = w; status(`track width: ${w}mm`); }
export function setGrid(g) { grid = g; status(`grid: ${g}mm`); draw(); }
export function getJson() { return JSON.stringify(board); }
export function setJson(json) { board = safeParse(json); drcMarkers = []; draw(); }
export function showDrc(markersJson) {
    try { drcMarkers = JSON.parse(markersJson); } catch { drcMarkers = []; }
    draw();
}
export function destroy() { resizeObs?.disconnect(); canvas?.remove(); }

// ---------- helpers ------------------------------------------------------------

function safeParse(json) {
    let b = {};
    try { b = JSON.parse(json || '{}'); } catch { }
    b.width ??= 80; b.height ??= 60;
    b.components ??= []; b.tracks ??= []; b.vias ??= []; b.nets ??= [];
    b.layers ??= []; b.rules ??= {};
    return b;
}

function status(t) { try { dotnet.invokeMethodAsync('SetStatus', t); } catch { } }
function snap(v) { return Math.round(v / grid) * grid; }

function sizeCanvas() {
    const r = host.getBoundingClientRect();
    const dpr = Math.min(window.devicePixelRatio || 1, 2);
    canvas.width = r.width * dpr;
    canvas.height = r.height * dpr;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
}

function fit() {
    const r = host.getBoundingClientRect();
    const scale = Math.min(r.width / (board.width + 20), r.height / (board.height + 20));
    view = { x: -(r.width / scale - board.width) / 2, y: -(r.height / scale - board.height) / 2, scale };
}

function toWorld(e) {
    const r = canvas.getBoundingClientRect();
    return { x: (e.clientX - r.left) / view.scale + view.x, y: (e.clientY - r.top) / view.scale + view.y };
}
const sx = wx => (wx - view.x) * view.scale;
const sy = wy => (wy - view.y) * view.scale;

function padAbs(c, p) {
    const rad = (c.rot || 0) * Math.PI / 180;
    const cos = Math.cos(rad), sin = Math.sin(rad);
    return { x: c.x + p.x * cos - p.y * sin, y: c.y + p.x * sin + p.y * cos };
}

// ---------- events ---------------------------------------------------------------

function onWheel(e) {
    e.preventDefault();
    const p = toWorld(e);
    const f = e.deltaY > 0 ? 0.87 : 1.15;
    view.scale = Math.max(1.5, Math.min(80, view.scale * f));
    const r = canvas.getBoundingClientRect();
    view.x = p.x - (e.clientX - r.left) / view.scale;
    view.y = p.y - (e.clientY - r.top) / view.scale;
    draw();
}

function onDown(e) {
    const p = toWorld(e);
    if (e.button === 2 || e.button === 1) {
        panStart = { cx: e.clientX, cy: e.clientY, vx: view.x, vy: view.y };
        return;
    }
    if (tool === 'select') {
        const c = hitComponent(p);
        if (c) drag = { comp: c, dx: c.x - p.x, dy: c.y - p.y };
        return;
    }
    if (tool === 'track') {
        if (!trackPts) {
            trackPts = [];
            const padHit = hitPad(p);
            trackNet = padHit ? padHit.net : '';
            const start = padHit ? { x: padHit.x, y: padHit.y } : { x: snap(p.x), y: snap(p.y) };
            trackPts.push([start.x, start.y]);
            status(trackNet ? `routing net ${trackNet} on ${activeLayer}` : `routing on ${activeLayer}`);
        } else {
            trackPts.push([snap(p.x), snap(p.y)]);
        }
        draw();
        return;
    }
    if (tool === 'via') {
        board.vias.push({ net: trackNet || '', x: snap(p.x), y: snap(p.y), drill: 0.3, dia: 0.6 });
        status(`via at (${snap(p.x)}, ${snap(p.y)})`);
        draw();
        return;
    }
    if (tool === 'delete') {
        const ti = hitTrack(p);
        if (ti >= 0) { board.tracks.splice(ti, 1); status('track deleted'); draw(); return; }
        const vi = board.vias.findIndex(v => Math.hypot(v.x - p.x, v.y - p.y) < 1);
        if (vi >= 0) { board.vias.splice(vi, 1); status('via deleted'); draw(); }
    }
}

function onMove(e) {
    if (panStart) {
        view.x = panStart.vx - (e.clientX - panStart.cx) / view.scale;
        view.y = panStart.vy - (e.clientY - panStart.cy) / view.scale;
        draw();
        return;
    }
    if (drag) {
        const p = toWorld(e);
        drag.comp.x = snap(p.x + drag.dx);
        drag.comp.y = snap(p.y + drag.dy);
        draw();
        return;
    }
    if (tool === 'track' && trackPts) draw(toWorld(e));

    const p = toWorld(e);
    const c = hitComponent(p);
    canvas.style.cursor = (tool === 'select' && c) ? 'grab' : 'crosshair';
}

function onUp() {
    if (drag) status(`${drag.comp.ref} → (${drag.comp.x.toFixed(1)}, ${drag.comp.y.toFixed(1)})`);
    drag = null;
    panStart = null;
}

function onDbl(e) {
    if (tool === 'track' && trackPts && trackPts.length >= 2) {
        // land on a pad? adopt its exact position & net
        const p = toWorld(e);
        const padHit = hitPad(p);
        if (padHit) {
            trackPts[trackPts.length - 1] = [padHit.x, padHit.y];
            if (!trackNet) trackNet = padHit.net;
        }
        board.tracks.push({ net: trackNet || '', layer: activeLayer, width: trackWidth, points: trackPts });
        status(`track added (${trackNet || 'no net'}, ${trackWidth}mm, ${activeLayer})`);
        trackPts = null;
        draw();
        return;
    }
    if (tool === 'select') {
        const p = toWorld(e);
        const c = hitComponent(p);
        if (c) { c.rot = ((c.rot || 0) + 90) % 360; status(`${c.ref} rotated to ${c.rot}°`); draw(); }
    }
}

function hitComponent(p) {
    return board.components.findLast(c =>
        Math.abs(p.x - c.x) < Math.max(c.w, 4) / 2 + 1 &&
        Math.abs(p.y - c.y) < Math.max(c.h, 4) / 2 + 1);
}

function hitPad(p) {
    for (const c of board.components)
        for (const pad of (c.pads || [])) {
            const a = padAbs(c, pad);
            if (Math.abs(p.x - a.x) < Math.max(pad.w, 1) && Math.abs(p.y - a.y) < Math.max(pad.h, 1))
                return { x: a.x, y: a.y, net: pad.net || '' };
        }
    return null;
}

function hitTrack(p) {
    return board.tracks.findIndex(t => t.points.some(([x, y], i) => {
        if (i === 0) return false;
        const [px, py] = t.points[i - 1];
        return distToSeg(p.x, p.y, px, py, x, y) < Math.max(t.width, 0.5);
    }));
}

function distToSeg(px, py, x1, y1, x2, y2) {
    const dx = x2 - x1, dy = y2 - y1, l2 = dx * dx + dy * dy;
    const t = l2 ? Math.max(0, Math.min(1, ((px - x1) * dx + (py - y1) * dy) / l2)) : 0;
    return Math.hypot(px - (x1 + t * dx), py - (y1 + t * dy));
}

// ---------- drawing ---------------------------------------------------------------

function draw(cursor) {
    const r = host.getBoundingClientRect();
    ctx.clearRect(0, 0, r.width, r.height);
    ctx.fillStyle = '#0a0f0c';
    ctx.fillRect(0, 0, r.width, r.height);

    // board substrate
    ctx.fillStyle = '#1a3325';
    ctx.strokeStyle = '#e8a75f';
    ctx.lineWidth = 1.5;
    roundRect(sx(0), sy(0), board.width * view.scale, board.height * view.scale, 4);
    ctx.fill();
    ctx.stroke();

    // grid dots
    if (view.scale > 4) {
        ctx.fillStyle = 'rgba(150,167,155,.28)';
        const step = grid < 0.5 ? 1 : grid * 2;
        for (let x = 0; x <= board.width; x += step)
            for (let y = 0; y <= board.height; y += step)
                ctx.fillRect(sx(x) - .5, sy(y) - .5, 1, 1);
    }

    // ratsnest for unrouted nets
    const routedNets = new Set(board.tracks.map(t => t.net));
    ctx.strokeStyle = 'rgba(88,166,200,.5)';
    ctx.setLineDash([4, 4]);
    ctx.lineWidth = 1;
    for (const net of board.nets) {
        if (!net || routedNets.has(net)) continue;
        const pts = [];
        for (const c of board.components)
            for (const pad of (c.pads || []))
                if (pad.net === net) pts.push(padAbs(c, pad));
        for (let i = 1; i < pts.length; i++) {
            ctx.beginPath();
            ctx.moveTo(sx(pts[i - 1].x), sy(pts[i - 1].y));
            ctx.lineTo(sx(pts[i].x), sy(pts[i].y));
            ctx.stroke();
        }
    }
    ctx.setLineDash([]);

    // tracks on every copper layer (inactive layers dimmed, active drawn last/on top)
    const layers = copperNames().filter(l => l !== activeLayer);
    layers.push(activeLayer);
    for (const layer of layers) {
        for (const t of board.tracks) {
            if (t.layer !== layer) continue;
            ctx.strokeStyle = layerColor(layer);
            ctx.globalAlpha = layer === activeLayer ? 0.95 : 0.35;
            ctx.lineWidth = Math.max(t.width * view.scale, 1);
            ctx.lineCap = 'round';
            ctx.lineJoin = 'round';
            ctx.beginPath();
            t.points.forEach(([x, y], i) => i ? ctx.lineTo(sx(x), sy(y)) : ctx.moveTo(sx(x), sy(y)));
            ctx.stroke();
        }
    }
    ctx.globalAlpha = 1;

    // in-progress track
    if (trackPts?.length) {
        const pts = cursor ? [...trackPts, [snap(cursor.x), snap(cursor.y)]] : trackPts;
        ctx.strokeStyle = '#fff';
        ctx.setLineDash([5, 3]);
        ctx.lineWidth = Math.max(trackWidth * view.scale, 1);
        ctx.beginPath();
        pts.forEach(([x, y], i) => i ? ctx.lineTo(sx(x), sy(y)) : ctx.moveTo(sx(x), sy(y)));
        ctx.stroke();
        ctx.setLineDash([]);
    }

    // vias
    for (const v of board.vias) {
        ctx.fillStyle = '#c9c2ae';
        ctx.beginPath();
        ctx.arc(sx(v.x), sy(v.y), (v.dia / 2) * view.scale, 0, Math.PI * 2);
        ctx.fill();
        ctx.fillStyle = '#0a0f0c';
        ctx.beginPath();
        ctx.arc(sx(v.x), sy(v.y), (v.drill / 2) * view.scale, 0, Math.PI * 2);
        ctx.fill();
    }

    // components with pads
    for (const c of board.components) drawComponent(c);

    // DRC markers
    for (const m of drcMarkers) {
        ctx.strokeStyle = m.severity === 'error' ? '#e2604d' : '#dfb35b';
        ctx.lineWidth = 2;
        const x = sx(m.x), y = sy(m.y), s = 6;
        ctx.beginPath();
        ctx.moveTo(x - s, y - s); ctx.lineTo(x + s, y + s);
        ctx.moveTo(x + s, y - s); ctx.lineTo(x - s, y + s);
        ctx.stroke();
        ctx.beginPath();
        ctx.arc(x, y, s + 3, 0, Math.PI * 2);
        ctx.stroke();
    }
}

function drawComponent(c) {
    const rad = (c.rot || 0) * Math.PI / 180;
    ctx.save();
    ctx.translate(sx(c.x), sy(c.y));
    ctx.rotate(rad);

    // body / courtyard
    const w = (c.w || 4) * view.scale, h = (c.h || 3) * view.scale;
    ctx.fillStyle = 'rgba(20,24,20,.72)';
    ctx.strokeStyle = 'rgba(236,231,217,.75)';
    ctx.lineWidth = 1;
    ctx.fillRect(-w / 2, -h / 2, w, h);
    ctx.strokeRect(-w / 2, -h / 2, w, h);

    // pads (in local space)
    for (const p of (c.pads || [])) {
        const px = p.x * view.scale, py = p.y * view.scale;
        const pw = p.w * view.scale, ph = p.h * view.scale;
        ctx.fillStyle = p.drill > 0 ? '#c9c2ae' : '#e8b96a';
        if (p.shape === 'circle' || p.drill > 0) {
            ctx.beginPath();
            ctx.arc(px, py, Math.max(pw, ph) / 2, 0, Math.PI * 2);
            ctx.fill();
            if (p.drill > 0) {
                ctx.fillStyle = '#0a0f0c';
                ctx.beginPath();
                ctx.arc(px, py, (p.drill / 2) * view.scale, 0, Math.PI * 2);
                ctx.fill();
            }
        } else {
            ctx.fillRect(px - pw / 2, py - ph / 2, pw, ph);
        }
    }

    // ref label
    if (view.scale > 3.5) {
        ctx.rotate(-rad);
        ctx.fillStyle = '#ece7d9';
        ctx.font = `${Math.max(10, view.scale * 1.2)}px "IBM Plex Mono", monospace`;
        ctx.textAlign = 'center';
        ctx.fillText(c.ref || '', 0, -h / 2 - 4);
    }
    ctx.restore();
}

function roundRect(x, y, w, h, r) {
    ctx.beginPath();
    ctx.moveTo(x + r, y);
    ctx.arcTo(x + w, y, x + w, y + h, r);
    ctx.arcTo(x + w, y + h, x, y + h, r);
    ctx.arcTo(x, y + h, x, y, r);
    ctx.arcTo(x, y, x + w, y, r);
    ctx.closePath();
}
