// LiteCircuit 3D board preview — Three.js (loaded via import map from CDN).
// Extrudes the board substrate, copper tracks, pads, vias and component envelopes.

import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';

let renderer, scene, camera, controls, host, componentGroup, resizeObs;
const BOARD_T = 1.6;

export function init(hostId, boardJson) {
    host = document.getElementById(hostId);
    let board = {};
    try { board = JSON.parse(boardJson || '{}'); } catch { }
    board.width ??= 80; board.height ??= 60;
    board.components ??= []; board.tracks ??= []; board.vias ??= [];

    scene = new THREE.Scene();
    scene.background = new THREE.Color(0x0a0f0c);
    scene.fog = new THREE.Fog(0x0a0f0c, 250, 520);

    const r = host.getBoundingClientRect();
    camera = new THREE.PerspectiveCamera(45, r.width / r.height, 0.1, 1000);
    const dist = Math.max(board.width, board.height) * 1.35;
    camera.position.set(dist * 0.7, dist * 0.75, dist * 0.7);

    renderer = new THREE.WebGLRenderer({ antialias: true });
    renderer.setSize(r.width, r.height);
    renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
    host.appendChild(renderer.domElement);

    controls = new OrbitControls(camera, renderer.domElement);
    controls.enableDamping = true;
    controls.dampingFactor = 0.06;
    controls.autoRotate = true;
    controls.autoRotateSpeed = 1.2;
    controls.maxPolarAngle = Math.PI * 0.72;

    buildLights();
    buildBoard(board);

    renderer.setAnimationLoop(() => {
        controls.update();
        renderer.render(scene, camera);
    });

    resizeObs = new ResizeObserver(() => {
        const b = host.getBoundingClientRect();
        if (b.width === 0) return;
        camera.aspect = b.width / b.height;
        camera.updateProjectionMatrix();
        renderer.setSize(b.width, b.height);
    });
    resizeObs.observe(host);
}

export function setAutoRotate(on) { if (controls) controls.autoRotate = on; }
export function setComponentsVisible(on) { if (componentGroup) componentGroup.visible = on; }

export function destroy() {
    resizeObs?.disconnect();
    renderer?.setAnimationLoop(null);
    renderer?.dispose();
    renderer?.domElement?.remove();
    scene = null;
}

// ---------------------------------------------------------------------------------

function buildLights() {
    scene.add(new THREE.HemisphereLight(0xdfe8e2, 0x11150f, 0.85));
    const key = new THREE.DirectionalLight(0xfff2dd, 1.6);
    key.position.set(60, 120, 80);
    scene.add(key);
    const rim = new THREE.DirectionalLight(0x58a6c8, 0.5);
    rim.position.set(-80, 40, -60);
    scene.add(rim);
}

function buildBoard(board) {
    const group = new THREE.Group();
    // Center the board: world x = mmX - w/2, world z = mmY - h/2, y up.
    const ox = -board.width / 2, oz = -board.height / 2;
    const X = mm => mm + ox, Z = mm => mm + oz;

    // Substrate (soldermask green)
    const substrate = new THREE.Mesh(
        boxRounded(board.width, BOARD_T, board.height),
        new THREE.MeshPhysicalMaterial({ color: 0x1c5c34, roughness: 0.55, clearcoat: 0.6, clearcoatRoughness: 0.35 })
    );
    substrate.position.set(0, 0, 0);
    group.add(substrate);

    // Copper tracks
    const copperTop = new THREE.MeshStandardMaterial({ color: 0xd08a47, metalness: 0.85, roughness: 0.35 });
    const copperBot = new THREE.MeshStandardMaterial({ color: 0x9d7136, metalness: 0.85, roughness: 0.4 });
    for (const t of board.tracks) {
        const isTop = t.layer !== 'B.Cu';
        const y = isTop ? BOARD_T / 2 + 0.02 : -BOARD_T / 2 - 0.02;
        for (let i = 1; i < t.points.length; i++) {
            const [x1, z1] = t.points[i - 1], [x2, z2] = t.points[i];
            const len = Math.hypot(x2 - x1, z2 - z1);
            if (len < 0.01) continue;
            const seg = new THREE.Mesh(
                new THREE.BoxGeometry(len, 0.05, Math.max(t.width, 0.15)),
                isTop ? copperTop : copperBot
            );
            seg.position.set(X((x1 + x2) / 2), y, Z((z1 + z2) / 2));
            seg.rotation.y = -Math.atan2(z2 - z1, x2 - x1);
            group.add(seg);
        }
    }

    // Pads (gold) — with rotation of parent component
    const gold = new THREE.MeshStandardMaterial({ color: 0xe8b96a, metalness: 0.9, roughness: 0.25 });
    const silver = new THREE.MeshStandardMaterial({ color: 0xc9c2ae, metalness: 0.9, roughness: 0.3 });
    for (const c of board.components) {
        const rad = (c.rot || 0) * Math.PI / 180;
        const cos = Math.cos(rad), sin = Math.sin(rad);
        for (const p of (c.pads || [])) {
            const px = c.x + p.x * cos - p.y * sin;
            const pz = c.y + p.x * sin + p.y * cos;
            const th = p.drill > 0 ? BOARD_T + 0.12 : 0.08;
            const py = p.drill > 0 ? 0 : BOARD_T / 2 + 0.05;
            let mesh;
            if (p.shape === 'circle' || p.drill > 0) {
                mesh = new THREE.Mesh(
                    new THREE.CylinderGeometry(Math.max(p.w, p.h) / 2, Math.max(p.w, p.h) / 2, th, 20),
                    p.drill > 0 ? silver : gold);
            } else {
                mesh = new THREE.Mesh(new THREE.BoxGeometry(p.w, th, p.h), gold);
                mesh.rotation.y = -rad;
            }
            mesh.position.set(X(px), py, Z(pz));
            group.add(mesh);
        }
    }

    // Vias
    for (const v of board.vias) {
        const via = new THREE.Mesh(
            new THREE.CylinderGeometry(v.dia / 2, v.dia / 2, BOARD_T + 0.1, 16), silver);
        via.position.set(X(v.x), 0, Z(v.y));
        group.add(via);
    }

    // Component bodies
    componentGroup = new THREE.Group();
    for (const c of board.components) componentGroup.add(componentMesh(c, X, Z));
    group.add(componentGroup);

    scene.add(group);

    // Bench surface reference grid
    const grid = new THREE.GridHelper(Math.max(board.width, board.height) * 3, 30, 0x24352b, 0x18251e);
    grid.position.y = -BOARD_T / 2 - 6;
    scene.add(grid);
}

function componentMesh(c, X, Z) {
    const g = new THREE.Group();
    const w = Math.max(c.w || 3, 0.8), d = Math.max(c.h || 2, 0.8);
    const type = (c.type || 'ic').toLowerCase();

    const mat = {
        ic: new THREE.MeshStandardMaterial({ color: 0x14161a, roughness: 0.6 }),
        module: new THREE.MeshStandardMaterial({ color: 0x2b3138, roughness: 0.55, metalness: 0.35 }),
        resistor: new THREE.MeshStandardMaterial({ color: 0x2a2d31, roughness: 0.5 }),
        capacitor: new THREE.MeshStandardMaterial({ color: 0xb59a6a, roughness: 0.6 }),
        led: new THREE.MeshPhysicalMaterial({ color: colorOf(c.value), roughness: 0.15, transmission: 0.5, emissive: colorOf(c.value), emissiveIntensity: 0.7 }),
        connector: new THREE.MeshStandardMaterial({ color: 0x2f2f2f, roughness: 0.5 }),
        button: new THREE.MeshStandardMaterial({ color: 0x3a3f45, roughness: 0.4, metalness: 0.5 }),
        display: new THREE.MeshStandardMaterial({ color: 0x0b1d33, roughness: 0.25, metalness: 0.2 }),
        crystal: new THREE.MeshStandardMaterial({ color: 0xb8bcc0, roughness: 0.3, metalness: 0.85 }),
    }[type] || new THREE.MeshStandardMaterial({ color: 0x23272b, roughness: 0.55 });

    let mesh;
    const h = { resistor: 0.6, capacitor: 0.9, led: 0.8, ic: 1.4, module: 3.2, display: 3, connector: 6, button: 3.5, crystal: 1.2, regulator: 1.6 }[type] ?? 1.8;
    if (type === 'led' || type === 'button') {
        mesh = new THREE.Mesh(new THREE.CylinderGeometry(Math.min(w, d) / 2, Math.min(w, d) / 2, h, 24), mat);
    } else if (type === 'capacitor' && Math.max(w, d) > 4) {
        mesh = new THREE.Mesh(new THREE.CylinderGeometry(Math.min(w, d) / 2, Math.min(w, d) / 2, h * 3, 24), mat);
    } else {
        mesh = new THREE.Mesh(new THREE.BoxGeometry(w, h, d), mat);
    }
    mesh.position.y = BOARD_T / 2 + h / 2 + 0.05;
    g.add(mesh);

    // Module shielding can detail
    if (type === 'module') {
        const lid = new THREE.Mesh(
            new THREE.BoxGeometry(w * 0.7, 0.15, d * 0.55),
            new THREE.MeshStandardMaterial({ color: 0x9aa2a8, metalness: 0.9, roughness: 0.3 }));
        lid.position.y = BOARD_T / 2 + h + 0.15;
        g.add(lid);
    }

    g.position.set(X(c.x), 0, Z(c.y));
    g.rotation.y = -(c.rot || 0) * Math.PI / 180;
    return g;
}

function colorOf(value) {
    return { red: 0xff3b2f, green: 0x37d67a, blue: 0x3b82f6, yellow: 0xffd23f, white: 0xf5f5f5 }[(value || '').toLowerCase()] ?? 0xff3b2f;
}

function boxRounded(w, h, d) {
    // Simple box is fine at board scale; kept as a helper if bevel is wanted later.
    return new THREE.BoxGeometry(w, h, d);
}
