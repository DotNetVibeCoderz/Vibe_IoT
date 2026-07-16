// LiteCircuit schematic editor — SVG based, grid-snapped.
// Tools: select (click / block-select / move), place, wire, delete.
// Keyboard: S/P/W/X tools · arrows move · Shift+arrows fast · R rotate · Del delete
//           Ctrl+A select all · Enter finish wire · Esc cancel/deselect

const NS = 'http://www.w3.org/2000/svg';
const GRID = 5;
let host, svg, gLayer, doc, dotnet;
let tool = 'select', pendingType = 'resistor';
let view = { x: -10, y: -10, w: 220, h: 150 };
let selection = new Set();      // selected component ids
let drag = null;                // { start:{x,y}, orig: Map(id -> {x,y}) }
let marquee = null;             // { x1,y1,x2,y2, additive }
let wirePts = null;
let panStart = null;
let keyHandler = null;

const REF_PREFIX = {
    resistor: 'R', capacitor: 'C', inductor: 'L', led: 'D', diode: 'D',
    transistor: 'Q', ic: 'U', module: 'U', regulator: 'U', sensor: 'U',
    display: 'DS', connector: 'J', button: 'SW', crystal: 'Y', audio: 'BZ', battery: 'PWR'
};

export function init(dotnetRef, hostId, json) {
    dotnet = dotnetRef;
    host = document.getElementById(hostId);
    doc = safeParse(json);
    svg = document.createElementNS(NS, 'svg');
    svg.style.width = '100%';
    svg.style.height = '100%';
    svg.style.cursor = 'crosshair';
    svg.style.touchAction = 'none';
    host.appendChild(svg);
    gLayer = document.createElementNS(NS, 'g');
    svg.appendChild(gLayer);

    svg.addEventListener('pointerdown', onDown);
    svg.addEventListener('pointermove', onMove);
    svg.addEventListener('pointerup', onUp);
    svg.addEventListener('dblclick', onDbl);
    svg.addEventListener('wheel', onWheel, { passive: false });
    svg.addEventListener('contextmenu', e => e.preventDefault());

    keyHandler = onKey;
    window.addEventListener('keydown', keyHandler);

    fit();
    render();
}

export function setTool(t, type) {
    tool = t;
    if (type) pendingType = type;
    if (t !== 'wire') wirePts = null;
    status(`${t}${t === 'place' ? ' · ' + pendingType : ''} — ${hints(t)}`);
    render();
}

export function getJson() { return JSON.stringify(doc); }
export function setJson(json) { doc = safeParse(json); selection.clear(); fit(); render(); notifySelection(); }

/// Applies property edits coming from the Blazor property panel.
export function updateComponent(id, propsJson) {
    const c = doc.components.find(x => x.id === id);
    if (!c) return;
    let p = {};
    try { p = JSON.parse(propsJson || '{}'); } catch { }
    if (p.ref !== undefined && p.ref !== '') c.ref = p.ref;
    if (p.value !== undefined) c.value = p.value;
    if (p.rot !== undefined) c.rot = ((p.rot % 360) + 360) % 360;
    render();
    status(`${c.ref} updated (${c.type}${c.value ? ' ' + c.value : ''})`);
    notifySelection();
}

/// Deletes everything currently selected (used by the panel's delete button).
export function deleteSelection() {
    const n = selection.size;
    if (!n) return;
    doc.components = doc.components.filter(c => !selection.has(c.id));
    selection.clear();
    render();
    status(`deleted ${n} component(s)`);
    notifySelection();
}

function notifySelection() {
    const sel = selectedComps().map(c => ({
        id: c.id, ref: c.ref, type: c.type, value: c.value || '', rot: c.rot || 0,
        pins: (c.pinNets || []).length
    }));
    try { dotnet.invokeMethodAsync('OnSelectionChanged', JSON.stringify(sel)); } catch { }
}

export function destroy() {
    if (keyHandler) window.removeEventListener('keydown', keyHandler);
    if (svg) svg.remove();
}

// ---------- helpers -----------------------------------------------------------

function safeParse(json) {
    let d = {};
    try { d = JSON.parse(json || '{}'); } catch { }
    d.components ??= [];
    d.wires ??= [];
    d.notes ??= '';
    return d;
}

function hints(t) {
    return {
        select: 'click / drag a box to select · drag to move · arrows nudge',
        place: 'click the canvas to drop the component',
        wire: 'click points · Enter or double-click to finish',
        delete: 'click a component or wire to remove it'
    }[t] || '';
}

function status(text) { try { dotnet.invokeMethodAsync('SetStatus', text); } catch { } }
function snap(v) { return Math.round(v / GRID) * GRID; }
function selectedComps() { return doc.components.filter(c => selection.has(c.id)); }

// Client px -> world units via the SVG's real transform matrix.
// This stays exact regardless of preserveAspectRatio letterboxing,
// so the cursor position and drag distances always match 1:1.
function toWorld(e) {
    const m = svg.getScreenCTM();
    if (!m) {
        const r = svg.getBoundingClientRect();
        return {
            x: view.x + (e.clientX - r.left) / r.width * view.w,
            y: view.y + (e.clientY - r.top) / r.height * view.h
        };
    }
    const p = new DOMPoint(e.clientX, e.clientY).matrixTransform(m.inverse());
    return { x: p.x, y: p.y };
}

// World units per screen pixel (uniform under xMidYMid meet).
function worldPerPixel() {
    const m = svg.getScreenCTM();
    return m ? 1 / m.a : view.w / svg.getBoundingClientRect().width;
}

// The actually visible world rectangle (can be wider/taller than the viewBox
// when the aspect ratios differ).
function visibleBounds() {
    const m = svg.getScreenCTM();
    if (!m) return { x1: view.x, y1: view.y, x2: view.x + view.w, y2: view.y + view.h };
    const r = svg.getBoundingClientRect();
    const inv = m.inverse();
    const tl = new DOMPoint(r.left, r.top).matrixTransform(inv);
    const br = new DOMPoint(r.right, r.bottom).matrixTransform(inv);
    return { x1: tl.x, y1: tl.y, x2: br.x, y2: br.y };
}

function fit() {
    const xs = doc.components.map(c => c.x), ys = doc.components.map(c => c.y);
    if (xs.length) {
        const minX = Math.min(...xs) - 30, maxX = Math.max(...xs) + 30;
        const minY = Math.min(...ys) - 25, maxY = Math.max(...ys) + 30;
        view = { x: minX, y: minY, w: Math.max(maxX - minX, 120), h: Math.max(maxY - minY, 90) };
    }
    // Expand the viewBox to the host's aspect ratio so there is no letterboxing:
    // world/screen mapping then stays 1:1 and the zoom anchor is exact.
    const r = host.getBoundingClientRect();
    if (r.width > 0 && r.height > 0) {
        const aspect = r.width / r.height;
        const { w, h } = view;
        if (w / h < aspect) {
            const nw = h * aspect;
            view.x -= (nw - w) / 2;
            view.w = nw;
        } else {
            const nh = w / aspect;
            view.y -= (nh - h) / 2;
            view.h = nh;
        }
    }
    applyView();
}

function applyView() { svg.setAttribute('viewBox', `${view.x} ${view.y} ${view.w} ${view.h}`); }

function nextRef(type) {
    const prefix = REF_PREFIX[type] || 'X';
    let n = 1;
    while (doc.components.some(c => c.ref === prefix + n)) n++;
    return prefix + n;
}

function compBounds(c) {
    const pins = Math.max((c.pinNets || []).length, 2);
    const w = pins <= 2 ? 24 : Math.max(24, pins * 4.5) + 4;
    const h = c.type === 'module' ? 22 : 18;
    return { x1: c.x - w / 2, y1: c.y - h / 2 - 4, x2: c.x + w / 2, y2: c.y + h / 2 + 4 };
}

function pinPos(c) {
    const n = Math.max((c.pinNets || []).length, 2);
    if (n <= 2) return [{ x: c.x - 10, y: c.y }, { x: c.x + 10, y: c.y }];
    const w = Math.max(24, n * 5);
    return Array.from({ length: n }, (_, i) => ({
        x: c.x - w / 2 + (i + 0.5) * (w / n), y: c.y + 11
    }));
}

function defaultValue(type) {
    return {
        resistor: '10k', capacitor: '100nF', inductor: '10uH', led: 'red',
        battery: '5', crystal: '8MHz', regulator: '3.3V'
    }[type] || '';
}

// ---------- keyboard -------------------------------------------------------------

function onKey(e) {
    // Never steal keys from form fields or when the editor is gone
    const t = e.target;
    if (!document.body.contains(svg)) return;
    if (t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.tagName === 'SELECT' || t.isContentEditable)) return;

    const step = e.shiftKey ? GRID * 4 : GRID;
    const sel = selectedComps();

    switch (e.key) {
        case 'Delete':
        case 'Backspace':
            if (sel.length) {
                deleteSelection();
                e.preventDefault();
            }
            break;
        case 'ArrowLeft': nudge(sel, -step, 0, e); break;
        case 'ArrowRight': nudge(sel, step, 0, e); break;
        case 'ArrowUp': nudge(sel, 0, -step, e); break;
        case 'ArrowDown': nudge(sel, 0, step, e); break;
        case 'r': case 'R':
            if (sel.length) {
                sel.forEach(c => c.rot = ((c.rot || 0) + 90) % 360);
                status(`rotated ${sel.length} component(s)`);
                render();
                notifySelection();
            }
            break;
        case 'e': case 'E':
            if (noMods(e)) {
                try { dotnet.invokeMethodAsync('TogglePropsPanel'); } catch { }
                e.preventDefault();
            }
            break;
        case 'Escape':
            if (wirePts) { wirePts = null; status('wire cancelled'); }
            else if (selection.size) { selection.clear(); status('selection cleared'); notifySelection(); }
            render();
            break;
        case 'Enter':
            if (tool === 'wire' && wirePts?.length >= 2) finishWire();
            break;
        case 'a': case 'A':
            if (e.ctrlKey || e.metaKey) {
                doc.components.forEach(c => selection.add(c.id));
                status(`selected all (${selection.size})`);
                render();
                notifySelection();
                e.preventDefault();
            }
            break;
        case 's': case 'S': if (noMods(e)) setTool('select'); break;
        case 'p': case 'P': if (noMods(e)) setTool('place'); break;
        case 'w': case 'W': if (noMods(e)) setTool('wire'); break;
        case 'x': case 'X': if (noMods(e)) setTool('delete'); break;
    }
}

function noMods(e) { return !e.ctrlKey && !e.metaKey && !e.altKey; }

function nudge(sel, dx, dy, e) {
    if (!sel.length) return;
    sel.forEach(c => { c.x += dx; c.y += dy; });
    status(`moved ${sel.length} component(s)`);
    render();
    e.preventDefault();
}

// ---------- pointer events ---------------------------------------------------------

function onWheel(e) {
    e.preventDefault();
    const p = toWorld(e);
    const f = e.deltaY > 0 ? 1.15 : 0.87;
    view.w *= f; view.h *= f;
    view.x = p.x - (p.x - view.x) * f;
    view.y = p.y - (p.y - view.y) * f;
    applyView();
}

function onDown(e) {
    const p = toWorld(e);
    svg.setPointerCapture?.(e.pointerId);
    if (e.button === 2 || e.button === 1) {
        panStart = { x: e.clientX, y: e.clientY, vx: view.x, vy: view.y, wpp: worldPerPixel() };
        return;
    }
    if (tool === 'place') {
        const type = pendingType;
        const comp = {
            id: Math.random().toString(36).slice(2, 10),
            ref: nextRef(type), type, value: defaultValue(type),
            x: snap(p.x), y: snap(p.y), rot: 0, pinNets: []
        };
        doc.components.push(comp);
        selection = new Set([comp.id]);
        status(`placed ${comp.ref} (${type}) — arrows to nudge, R to rotate`);
        render();
        notifySelection();
        return;
    }
    if (tool === 'wire') {
        wirePts ??= [];
        wirePts.push([snap(p.x), snap(p.y)]);
        render();
        return;
    }
    if (tool === 'delete') {
        const c = hitComponent(p);
        if (c) {
            doc.components = doc.components.filter(x => x !== c);
            selection.delete(c.id);
            status(`deleted ${c.ref}`);
            render();
            notifySelection();
            return;
        }
        const wi = hitWire(p);
        if (wi >= 0) {
            doc.wires.splice(wi, 1);
            status('deleted wire');
            render();
        }
        return;
    }

    // ---- select tool ----
    const c = hitComponent(p);
    if (c) {
        if (e.shiftKey) {
            selection.has(c.id) ? selection.delete(c.id) : selection.add(c.id);
            status(`${selection.size} selected`);
            render();
            notifySelection();
            return;
        }
        if (!selection.has(c.id)) selection = new Set([c.id]);
        // begin moving everything selected
        const orig = new Map();
        selectedComps().forEach(sc => orig.set(sc.id, { x: sc.x, y: sc.y }));
        drag = { start: p, orig };
        svg.style.cursor = 'grabbing';
        render();
        notifySelection();
    } else {
        marquee = { x1: p.x, y1: p.y, x2: p.x, y2: p.y, additive: e.shiftKey };
        if (!e.shiftKey) selection.clear();
        render();
        notifySelection();
    }
}

function onMove(e) {
    if (panStart) {
        view.x = panStart.vx - (e.clientX - panStart.x) * panStart.wpp;
        view.y = panStart.vy - (e.clientY - panStart.y) * panStart.wpp;
        applyView();
        return;
    }
    const p = toWorld(e);
    if (drag) {
        const dx = p.x - drag.start.x, dy = p.y - drag.start.y;
        for (const c of selectedComps()) {
            const o = drag.orig.get(c.id);
            c.x = snap(o.x + dx);
            c.y = snap(o.y + dy);
        }
        render();
        return;
    }
    if (marquee) {
        marquee.x2 = p.x; marquee.y2 = p.y;
        render();
        return;
    }
    if (tool === 'wire' && wirePts?.length) render(p);
    if (tool === 'select') svg.style.cursor = hitComponent(p) ? 'grab' : 'crosshair';
}

function onUp() {
    if (drag) {
        const n = selection.size;
        status(`moved ${n} component(s)`);
        drag = null;
    }
    if (marquee) {
        const { x1, y1, x2, y2 } = marquee;
        const [lx, hx] = [Math.min(x1, x2), Math.max(x1, x2)];
        const [ly, hy] = [Math.min(y1, y2), Math.max(y1, y2)];
        if (hx - lx > 2 || hy - ly > 2) {
            for (const c of doc.components)
                if (c.x >= lx && c.x <= hx && c.y >= ly && c.y <= hy) selection.add(c.id);
            status(selection.size ? `${selection.size} selected — drag to move, R rotate, Del delete` : 'nothing in selection box');
        }
        marquee = null;
        render();
        notifySelection();
    }
    panStart = null;
    if (tool === 'select') svg.style.cursor = 'crosshair';
}

function onDbl(e) {
    if (tool === 'wire' && wirePts && wirePts.length >= 2) {
        finishWire();
        return;
    }
    const p = toWorld(e);
    const c = hitComponent(p);
    if (c) {
        const v = prompt(`Value for ${c.ref} (${c.type}):`, c.value || '');
        if (v !== null) { c.value = v; render(); status(`${c.ref} = ${v}`); }
    }
}

function finishWire() {
    const net = prompt('Net name for this wire (e.g. GND, VCC, SDA):', '') || '';
    doc.wires.push({ net, points: wirePts });
    assignPinNets(wirePts, net);
    wirePts = null;
    status(net ? `wire on net ${net}` : 'wire added');
    render();
}

function assignPinNets(pts, net) {
    if (!net) return;
    for (const c of doc.components) {
        const pins = pinPos(c);
        pins.forEach((pin, idx) => {
            for (const [x, y] of [pts[0], pts.at(-1)]) {
                if (Math.abs(pin.x - x) <= GRID && Math.abs(pin.y - y) <= GRID) {
                    c.pinNets ??= [];
                    while (c.pinNets.length <= idx) c.pinNets.push('');
                    c.pinNets[idx] = net;
                }
            }
        });
    }
}

function hitComponent(p) {
    return doc.components.findLast(c => {
        const b = compBounds(c);
        return p.x >= b.x1 && p.x <= b.x2 && p.y >= b.y1 && p.y <= b.y2;
    });
}

function hitWire(p) {
    return doc.wires.findIndex(w => w.points.some(([x, y], i) => {
        if (i === 0) return false;
        const [px, py] = w.points[i - 1];
        return distToSeg(p.x, p.y, px, py, x, y) < 2;
    }));
}

function distToSeg(px, py, x1, y1, x2, y2) {
    const dx = x2 - x1, dy = y2 - y1, l2 = dx * dx + dy * dy;
    const t = l2 ? Math.max(0, Math.min(1, ((px - x1) * dx + (py - y1) * dy) / l2)) : 0;
    return Math.hypot(px - (x1 + t * dx), py - (y1 + t * dy));
}

// ---------- rendering ---------------------------------------------------------

const css = () => getComputedStyle(document.documentElement);

function render(cursor) {
    const ink = css().getPropertyValue('--ink').trim() || '#ece7d9';
    const copper = css().getPropertyValue('--copper').trim() || '#d08a47';
    const trace = css().getPropertyValue('--trace').trim() || '#58a6c8';
    const muted = css().getPropertyValue('--muted').trim() || '#96a79b';

    gLayer.innerHTML = '';

    // grid dots across the actually visible area (not just the viewBox)
    const vb = visibleBounds();
    const dots = document.createElementNS(NS, 'g');
    const step = GRID * 2;
    for (let x = Math.floor(vb.x1 / step) * step; x < vb.x2; x += step)
        for (let y = Math.floor(vb.y1 / step) * step; y < vb.y2; y += step) {
            const d = document.createElementNS(NS, 'circle');
            d.setAttribute('cx', x); d.setAttribute('cy', y); d.setAttribute('r', 0.35);
            d.setAttribute('fill', muted); d.setAttribute('opacity', 0.35);
            dots.appendChild(d);
        }
    gLayer.appendChild(dots);

    // wires
    for (const w of doc.wires) {
        const pl = document.createElementNS(NS, 'polyline');
        pl.setAttribute('points', w.points.map(p => p.join(',')).join(' '));
        pl.setAttribute('fill', 'none');
        pl.setAttribute('stroke', trace);
        pl.setAttribute('stroke-width', 0.8);
        gLayer.appendChild(pl);
        if (w.net) {
            const [x, y] = w.points[0];
            text(x + 1.5, y - 1.5, w.net, trace, 3.2, '500');
        }
        for (const [x, y] of [w.points[0], w.points.at(-1)]) {
            const j = document.createElementNS(NS, 'circle');
            j.setAttribute('cx', x); j.setAttribute('cy', y); j.setAttribute('r', 1);
            j.setAttribute('fill', trace);
            gLayer.appendChild(j);
        }
    }

    // in-progress wire
    if (wirePts?.length) {
        const pts = cursor ? [...wirePts, [snap(cursor.x), snap(cursor.y)]] : wirePts;
        const pl = document.createElementNS(NS, 'polyline');
        pl.setAttribute('points', pts.map(p => p.join(',')).join(' '));
        pl.setAttribute('fill', 'none');
        pl.setAttribute('stroke', copper);
        pl.setAttribute('stroke-width', 0.8);
        pl.setAttribute('stroke-dasharray', '2 1.5');
        gLayer.appendChild(pl);
    }

    // components (+ selection highlight)
    for (const c of doc.components) {
        if (selection.has(c.id)) {
            const b = compBounds(c);
            const hl = document.createElementNS(NS, 'rect');
            hl.setAttribute('x', b.x1); hl.setAttribute('y', b.y1);
            hl.setAttribute('width', b.x2 - b.x1); hl.setAttribute('height', b.y2 - b.y1);
            hl.setAttribute('fill', copper); hl.setAttribute('fill-opacity', 0.08);
            hl.setAttribute('stroke', copper); hl.setAttribute('stroke-width', 0.5);
            hl.setAttribute('stroke-dasharray', '2.5 1.5');
            hl.setAttribute('rx', 1.5);
            gLayer.appendChild(hl);
        }
        drawComponent(c, { ink, copper, muted, trace });
    }

    // marquee
    if (marquee) {
        const m = document.createElementNS(NS, 'rect');
        m.setAttribute('x', Math.min(marquee.x1, marquee.x2));
        m.setAttribute('y', Math.min(marquee.y1, marquee.y2));
        m.setAttribute('width', Math.abs(marquee.x2 - marquee.x1));
        m.setAttribute('height', Math.abs(marquee.y2 - marquee.y1));
        m.setAttribute('fill', trace); m.setAttribute('fill-opacity', 0.1);
        m.setAttribute('stroke', trace); m.setAttribute('stroke-width', 0.4);
        m.setAttribute('stroke-dasharray', '2 1.5');
        gLayer.appendChild(m);
    }
}

function text(x, y, str, fill, size = 3.4, weight = '400', anchor = 'start') {
    const t = document.createElementNS(NS, 'text');
    t.setAttribute('x', x); t.setAttribute('y', y);
    t.setAttribute('fill', fill);
    t.setAttribute('font-size', size);
    t.setAttribute('font-weight', weight);
    t.setAttribute('text-anchor', anchor);
    t.setAttribute('font-family', 'IBM Plex Mono, monospace');
    t.textContent = str;
    gLayer.appendChild(t);
    return t;
}

function drawComponent(c, col) {
    const g = document.createElementNS(NS, 'g');
    g.setAttribute('transform', `translate(${c.x},${c.y}) rotate(${c.rot || 0})`);
    const stroke = col.ink, sw = 0.7;
    const el = (name, attrs) => {
        const n = document.createElementNS(NS, name);
        for (const k in attrs) n.setAttribute(k, attrs[k]);
        n.setAttribute('stroke', stroke);
        n.setAttribute('stroke-width', sw);
        if (!('fill' in attrs)) n.setAttribute('fill', 'none');
        g.appendChild(n);
        return n;
    };

    const pins = (c.pinNets || []).length;
    switch (c.type) {
        case 'resistor':
            el('path', { d: 'M-10 0 h3 l1.5 -3 3 6 3 -6 3 6 1.5 -3 h3' });
            break;
        case 'capacitor':
            el('path', { d: 'M-10 0 h8 M-2 -4 v8 M2 -4 v8 M2 0 h8' });
            break;
        case 'inductor':
            el('path', { d: 'M-10 0 h2 a2 2 0 0 1 4 0 a2 2 0 0 1 4 0 a2 2 0 0 1 4 0 h2' });
            break;
        case 'led':
        case 'diode':
            el('path', { d: 'M-10 0 H-3 M-3 -3.5 v7 l6 -3.5 z M3 -3.5 v7 M3 0 h7' });
            if (c.type === 'led') el('path', { d: 'M1 -5 l3 -3 M4 -4 l3 -3', stroke: col.copper });
            break;
        case 'battery':
            el('path', { d: 'M-10 0 h6 M-4 -4 v8 M-1 -2 v4 M-1 0 h11' });
            break;
        case 'button':
            el('path', { d: 'M-10 0 h6 M4 0 h6 M-4 -2 l8 -3' });
            el('circle', { cx: -4, cy: 0, r: 0.8, fill: stroke });
            el('circle', { cx: 4, cy: 0, r: 0.8, fill: stroke });
            break;
        case 'crystal':
            el('path', { d: 'M-10 0 h5 M-5 -3 v6 M5 -3 v6 M5 0 h5' });
            el('rect', { x: -3.5, y: -4, width: 7, height: 8 });
            break;
        case 'transistor':
            el('circle', { cx: 0, cy: 0, r: 6 });
            el('path', { d: 'M-10 0 H-2 M-2 -4 v8 M-2 -2 l6 -4 M-2 2 l6 4' });
            break;
        default: {
            const w = Math.max(22, pins * 4.5), h = c.type === 'module' ? 18 : 14;
            el('rect', { x: -w / 2, y: -h / 2, width: w, height: h, rx: 1.2 });
            el('circle', { cx: -w / 2 + 2.5, cy: -h / 2 + 2.5, r: 0.9, fill: col.muted });
        }
    }

    svgText(g, 0, -9, c.ref, col.copper, 3.6, '600', 'middle');
    if (c.value) svgText(g, 0, (c.type === 'module' ? 13 : 8) + 2.5, c.value, col.muted, 3, '400', 'middle');
    gLayer.appendChild(g);

    const pp = pinPos(c);
    pp.forEach((p, i) => {
        const dot = document.createElementNS(NS, 'circle');
        dot.setAttribute('cx', p.x); dot.setAttribute('cy', p.y); dot.setAttribute('r', 0.9);
        dot.setAttribute('fill', col.copper);
        gLayer.appendChild(dot);
        const net = (c.pinNets || [])[i];
        if (net) text(p.x + 1.2, p.y + 3.6, net, col.trace, 2.6);
    });
}

function svgText(parent, x, y, str, fill, size, weight, anchor) {
    const t = document.createElementNS(NS, 'text');
    t.setAttribute('x', x); t.setAttribute('y', y);
    t.setAttribute('fill', fill);
    t.setAttribute('font-size', size);
    t.setAttribute('font-weight', weight);
    t.setAttribute('text-anchor', anchor);
    t.setAttribute('font-family', 'IBM Plex Mono, monospace');
    t.textContent = str;
    parent.appendChild(t);
}
