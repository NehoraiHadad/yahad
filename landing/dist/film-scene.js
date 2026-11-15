/* יחד · the "how it works" film.
   Deterministic: YahadFilm.render(t) returns the whole frame at t seconds as an SVG
   string (1080x1920). film.js plays it; nothing here touches the page.
   The isometric helpers below come from the Anatomy kit the film was drawn with. */
(() => {
'use strict';
const FM = 'assets/family-moment-768.webp'; // the landing illustration (1536x1024 layout units)

const AZIMUTH = 45;
const ELEVATION = 30;
const SHADES = 4;
const RADIANS = Math.PI / 180;
const UNIT = Math.sqrt(1.6);
function cameraOf({ scale, azimuth = AZIMUTH, elevation = ELEVATION }) {
    const a = azimuth * RADIANS;
    const e = elevation * RADIANS;
    return { sinA: Math.sin(a), cosA: Math.cos(a), sinE: Math.sin(e), cosE: Math.cos(e), k: scale * UNIT };
}
function iso([x, y, z], projection) {
    const { origin } = projection;
    const { sinA, cosA, sinE, cosE, k } = cameraOf(projection);
    return [origin[0] + (x * sinA - y * cosA) * k, origin[1] + ((x * cosA + y * sinA) * sinE - z * cosE) * k];
}
function towardViewer(projection) {
    const { sinA, cosA } = cameraOf(projection);
    return [cosA, sinA];
}
function depthOf([x, y, z], projection) {
    const { sinA, cosA, sinE, cosE } = cameraOf(projection);
    return (x * cosA + y * sinA) * cosE + z * sinE;
}
const tenth = (value) => {
    const text = (Math.round(value * 10) / 10).toFixed(1).replace(/\.0$/, "");
    return text === "-0" ? "0" : text;
};
function pathOf(points, closed = false) {
    if (!points.length)
        return "";
    return points.map(([x, y], at) => `${at ? "L" : "M"}${tenth(x)} ${tenth(y)}`).join("") + (closed ? "Z" : "");
}
function roundedPlan({ x, y, w, d, r }, steps = 8) {
    const radius = Math.max(0, Math.min(r, w / 2, d / 2));
    const corners = [
        [x + w - radius, y + radius, -90],
        [x + w - radius, y + d - radius, 0],
        [x + radius, y + d - radius, 90],
        [x + radius, y + radius, 180],
    ];
    const out = [];
    for (const [cx, cy, start] of corners) {
        const count = radius > 0 ? steps : 0;
        for (let step = 0; step <= count; step++) {
            const angle = (start + (count ? (90 * step) / count : 45)) * RADIANS;
            out.push([cx + radius * Math.cos(angle), cy + radius * Math.sin(angle)]);
        }
    }
    return out;
}
function signedArea(points) {
    let area = 0;
    for (let index = 0; index < points.length; index++) {
        const [ax, ay] = points[index];
        const [bx, by] = points[(index + 1) % points.length];
        area += ax * by - bx * ay;
    }
    return area / 2;
}
const oriented = (points) => (signedArea(points) < 0 ? [...points].reverse() : points);
function convexHull(points) {
    const sorted = [...points].sort((a, b) => a[0] - b[0] || a[1] - b[1]);
    if (sorted.length < 3)
        return sorted;
    const cross = (o, a, b) => (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0]);
    const lower = [];
    for (const point of sorted) {
        while (lower.length >= 2 && cross(lower[lower.length - 2], lower[lower.length - 1], point) <= 0)
            lower.pop();
        lower.push(point);
    }
    const upper = [];
    for (let index = sorted.length - 1; index >= 0; index--) {
        const point = sorted[index];
        while (upper.length >= 2 && cross(upper[upper.length - 2], upper[upper.length - 1], point) <= 0)
            upper.pop();
        upper.push(point);
    }
    return [...lower.slice(0, -1), ...upper.slice(0, -1)];
}
function runsOf(flags, want) {
    const count = flags.length;
    const runs = [];
    const start = flags.findIndex((flag, index) => flag !== flags[(index + count - 1) % count]);
    if (start < 0)
        return flags[0] === want ? [Array.from({ length: count + 1 }, (_, index) => index % count)] : [];
    let run = null;
    for (let step = 0; step < count; step++) {
        const index = (start + step) % count;
        if (flags[index] === want) {
            if (!run)
                run = [index];
            run.push((index + 1) % count);
        }
        else if (run) {
            runs.push(run);
            run = null;
        }
    }
    if (run)
        runs.push(run);
    return runs;
}
// Convex solids only (every slab in the film is a rounded rectangle).
function extrude(ring, z, height, projection) {
    const count = ring.length;
    const counter = signedArea(ring) > 0;
    const [vx, vy] = towardViewer(projection);
    const { sinA, cosA } = cameraOf(projection);
    const normals = ring.map(([ax, ay], index) => {
        const [bx, by] = ring[(index + 1) % count];
        const dx = bx - ax;
        const dy = by - ay;
        const length = Math.hypot(dx, dy) || 1;
        return (counter ? [dy / length, -dx / length] : [-dy / length, dx / length]);
    });
    const facing = normals.map(([nx, ny]) => nx * vx + ny * vy > 1e-9);
    const top = ring.map(([x, y]) => iso([x, y, z + height], projection));
    const bottom = ring.map(([x, y]) => iso([x, y, z], projection));
    const outline = [];
    const crease = [];
    for (const run of runsOf(facing, false))
        outline.push(pathOf(run.map((index) => top[index])));
    for (const run of runsOf(facing, true)) {
        crease.push(pathOf(run.map((index) => top[index])));
        if (height > 0)
            outline.push(pathOf(run.map((index) => bottom[index])));
    }
    if (height > 0) {
        for (let index = 0; index < count; index++) {
            if (facing[(index + count - 1) % count] !== facing[index])
                outline.push(pathOf([top[index], bottom[index]]));
        }
    }
    const fill = pathOf(convexHull([...top, ...bottom]), true);
    const buckets = Array.from({ length: SHADES }, () => []);
    if (height > 0) {
        const [lx, ly] = [-sinA, cosA];
        for (let index = 0; index < count; index++) {
            if (!facing[index])
                continue;
            const [nx, ny] = normals[index];
            const lit = (nx * lx + ny * ly + 1) / 2;
            const shade = Math.min(SHADES - 1, Math.max(0, Math.floor(lit * SHADES)));
            const next = (index + 1) % count;
            buckets[shade].push(pathOf(oriented([top[index], top[next], bottom[next], bottom[index]]), true));
        }
    }
    return {
        fill,
        outline: outline.join(""),
        crease: crease.join(""),
        top: pathOf(oriented(top), true),
        shades: buckets.map((paths) => paths.join("")),
    };
}
const thousandth = (value) => Math.round(value * 1000) / 1000;
function axesAt(at, z, projection) {
    const origin = iso([at[0], at[1], z], projection);
    const along = (point) => {
        const [px, py] = iso(point, projection);
        return [px - origin[0], py - origin[1]];
    };
    return { origin, x: along([at[0] + 1, at[1], z]), y: along([at[0], at[1] + 1, z]), z: along([at[0], at[1], z + 1]) };
}
const matrixOf = (u, v, origin) => `matrix(${thousandth(u[0])} ${thousandth(u[1])} ${thousandth(v[0])} ${thousandth(v[1])} ${thousandth(origin[0])} ${thousandth(origin[1])})`;
function topMatrix(at, z, projection, along = "x") {
    const { origin, x, y } = axesAt(at, z, projection);
    return along === "x" ? matrixOf(x, y, origin) : matrixOf([-y[0], -y[1]], x, origin);
}

// יחד — exploded WhatsApp → Noa's interface. Deterministic: render(t) → SVG string.
const W = 1080, H = 1920;
const C = { ink:'#183e35', paper:'#f2f5f4', muted:'#5f7166', green:'#234f42', line:'#e0e6da', peach:'#f0d8c3', peachDeep:'#e3b48f', sage:'#557367',
  mint:'#e5efdc', mint2:'#c9dac0', white:'#ffffff', wa:'#25d366', waTeal:'#0b6b5a', waBg:'#efe7dc', shell:'#2c3b37', board:'#9fb3a6', screen:'#fbfaf6',
  tablet:'#d8e1d6', tabletSide:'#b9c7bb', red:'#d1584a', blush:'#e9837a' };
const clamp = (v, a = 0, b = 1) => Math.max(a, Math.min(b, v));
const lerp = (a, b, u) => a + (b - a) * u;
const ease = (u) => { u = clamp(u); return u * u * (3 - 2 * u); };
const eio = (u) => { u = clamp(u); return u < .5 ? 4 * u * u * u : 1 - Math.pow(-2 * u + 2, 3) / 2; };
const eout = (u) => 1 - Math.pow(1 - clamp(u), 3);
const ein = (u) => Math.pow(clamp(u), 2.4);
const backOut = (u) => { const v = clamp(u), c = 1.9; return 1 + (c + 1) * Math.pow(v - 1, 3) + c * Math.pow(v - 1, 2); };
// a settling spring: 0 → 1 with one overshoot; tau in seconds since start
const spring = (tau, k = 7, w = 11) => tau <= 0 ? 0 : 1 - Math.exp(-k * tau) * Math.cos(w * tau);
const hex = (s) => [1, 3, 5].map((i) => parseInt(s.slice(i, i + 2), 16));
const mix = (a, b, u) => { const A = hex(a), B = hex(b); return '#' + A.map((v, i) => Math.round(lerp(v, B[i], clamp(u))).toString(16).padStart(2, '0')).join(''); };
const esc = (s) => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
const f1 = (v) => (Math.round(v * 100) / 100).toString();

// ---------- text (Hebrew, right-to-left). anchor: 'r' right edge, 'c' centre, 'l' left edge
const tx = (x, y, s, { size = 32, weight = 400, color = C.ink, anchor = 'r', op = 1, ltr = false, extra = '' } = {}) => {
  const ta = anchor === 'c' ? 'middle' : (anchor === 'r') === !ltr ? 'start' : 'end';
  return `<text x="${f1(x)}" y="${f1(y)}" font-size="${size}" font-weight="${weight}" fill="${color}" text-anchor="${ta}" dominant-baseline="middle" style="direction:${ltr ? 'ltr' : 'rtl'}" opacity="${f1(op)}" ${extra}>${esc(s)}</text>`;
};
const rrect = (x, y, w, h, r, fill, extra = '') => `<rect x="${f1(x)}" y="${f1(y)}" width="${f1(w)}" height="${f1(h)}" rx="${f1(r)}" fill="${fill}" ${extra}/>`;
const circ = (x, y, r, fill, extra = '') => `<circle cx="${f1(x)}" cy="${f1(y)}" r="${f1(r)}" fill="${fill}" ${extra}/>`;
const heartPath = (cx, cy, s) => { const p = (x, y) => `${f1(cx + x * s)} ${f1(cy + y * s)}`;
  return `M${p(0, .35)} C${p(-.1, .25)} ${p(-.5, 0)} ${p(-.5, -.25)} C${p(-.5, -.5)} ${p(-.25, -.62)} ${p(0, -.42)} C${p(.25, -.62)} ${p(.5, -.5)} ${p(.5, -.25)} C${p(.5, 0)} ${p(.1, .25)} ${p(0, .35)}Z`; };
const check = (cx, cy, s, color = '#fff', w = 6) => `<path d="M${f1(cx - .42 * s)} ${f1(cy + .02 * s)} L${f1(cx - .12 * s)} ${f1(cy + .3 * s)} L${f1(cx + .44 * s)} ${f1(cy - .3 * s)}" fill="none" stroke="${color}" stroke-width="${w}" stroke-linecap="round" stroke-linejoin="round"/>`;

// ---------- camera
const KEYS = [
  [0.0, { tx: 0, ty: 0, tz: 40, sx: 540, sy: 1040, zoom: .9, A: 90, E: 90 }],
  [3.3, { tx: 0, ty: 0, tz: 40, sx: 540, sy: 1040, zoom: .84, A: 90, E: 90 }],
  [4.05, { tx: 0, ty: 0, tz: 250, sx: 540, sy: 1010, zoom: .86, A: 74, E: 54 }],
  [5.6, { tx: 0, ty: 0, tz: 300, sx: 540, sy: 990, zoom: .78, A: 74, E: 54 }],
  [9.9, { tx: 0, ty: 0, tz: 300, sx: 540, sy: 990, zoom: .78, A: 74, E: 54 }],
  [10.9, { tx: 0, ty: 2700, tz: 60, sx: 540, sy: 1060, zoom: .74, A: 64, E: 56 }],
  [13.95, { tx: 0, ty: 2700, tz: 40, sx: 540, sy: 1010, zoom: .82, A: 64, E: 56 }],
  [15.0, { tx: 0, ty: 2700, tz: 30, sx: 540, sy: 965, zoom: 1.0, A: 90, E: 90 }],
  [18.05, { tx: 0, ty: 2700, tz: 30, sx: 540, sy: 965, zoom: 1.0, A: 90, E: 90 }],
  [18.65, { tx: 0, ty: 2700, tz: 30, sx: 300, sy: 800, zoom: .64, A: 90, E: 90 }],
  [25.0, { tx: 0, ty: 2700, tz: 30, sx: 300, sy: 800, zoom: .64, A: 90, E: 90 }],
];
const camAt = (t) => {
  let i = 0; while (i < KEYS.length - 2 && t >= KEYS[i + 1][0]) i++;
  const [t0, a] = KEYS[i], [t1, b] = KEYS[i + 1];
  const u = eio((t - t0) / (t1 - t0));
  const c = {}; for (const key in a) c[key] = lerp(a[key], b[key], u);
  return c;
};
let P = null;
const setCamera = (c) => {
  const base = { origin: [0, 0], scale: c.zoom / Math.sqrt(1.6), azimuth: c.A, elevation: Math.min(89.999, c.E) };
  const p0 = iso([c.tx, c.ty, c.tz], base);
  P = { ...base, origin: [c.sx - p0[0], c.sy - p0[1]] };
};

// ---------- solids
const parts = [];
const ringRect = (cx, cy, w, d, r, theta = 0) => {
  const ring = roundedPlan({ x: -w / 2, y: -d / 2, w, d, r }, 6);
  const c = Math.cos(theta), s = Math.sin(theta);
  return ring.map(([x, y]) => [cx + x * c - y * s, cy + x * s + y * c]);
};
// one solid slab (a tile, a layer) with its face drawn in local coordinates on the top
const slab = ({ cx, cy, z, w, d, h, r = 20, theta = 0, top, side, face = '', op = 1, edge = .55, bias = 0 }) => {
  if (op <= 0.003) return;
  const ring = ringRect(cx, cy, w, d, r, theta);
  const pth = extrude(ring, z, h, P);
  let s = `<path d="${pth.fill}" fill="${side}"/>`;
  pth.shades.forEach((dd, i) => { if (dd) s += `<path d="${dd}" fill="${mix(side, '#0d1f1a', (3 - i) * .07)}"/>`; });
  s += `<path d="${pth.top}" fill="${top}"/>`;
  if (face) {
    const c = Math.cos(theta), sn = Math.sin(theta);
    const m = topMatrix([cx, cy], z + h, P, 'x');
    s += `<g transform="${m} matrix(${f1(c)} ${f1(sn)} ${f1(-sn)} ${f1(c)} 0 0) translate(${f1(-w / 2)} ${f1(-d / 2)})">${face}</g>`;
  }
  s += `<path d="${pth.outline}" fill="none" stroke="${C.ink}" stroke-opacity="${edge}" stroke-width="1.6" stroke-linejoin="round"/>`;
  if (pth.crease) s += `<path d="${pth.crease}" fill="none" stroke="#ffffff" stroke-opacity=".35" stroke-width="1.2"/>`;
  parts.push({ depth: depthOf([cx, cy, z + h], P) + bias, svg: op < 1 ? `<g opacity="${f1(op)}">${s}</g>` : s });
};
// a face with no body (the notification banner over the screen)
const faceOn = ({ cx, cy, z, w, d, face, bias = 0 }) => {
  parts.push({ depth: depthOf([cx, cy, z], P) + bias, svg: `<g transform="${topMatrix([cx, cy], z, P, 'x')} translate(${f1(-w / 2)} ${f1(-d / 2)})">${face}</g>` });
};
const shadowOn = (cx, cy, z, w, d, r, op) => { // a soft shadow on the floor (z=0)
  if (op <= 0.01) return;
  const ring = ringRect(cx, cy, w, d, r).map(([x, y]) => iso([x, y, z], P));
  parts.push({ depth: -1e9, svg: `<path d="${pathOf(ring, true)}" fill="#183e35" opacity="${f1(op)}" filter="url(#film-soft)"/>` });
};

// ---------- data
const ROWS = [
  { id: 'work', name: 'עבודה', group: true, prev: 'מי שולח את המצגת?', time: '09:12', badge: 23, L: 'ע', col: '#cdd8e0' },
  { id: 'family', name: 'המשפחה', group: true, prev: 'סבתא: מתי באים?', time: '09:05', badge: 3, L: 'מ', col: '#f0d8c3', pick: 6.0 },
  { id: 'house', name: 'ועד הבית', group: true, prev: 'מחר אין מים בבוקר', time: '08:58', badge: 41, L: 'ו', col: '#ddd6e8' },
  { id: 'class', name: 'הכיתה של נועה', group: true, prev: 'מחר מביאים כובע!', time: '08:41', badge: 12, L: 'כ', col: '#cfe3d0', pick: 6.5 },
  { id: 'cpa', name: 'דני, רואה חשבון', group: false, prev: 'שלחתי לך את הדוח', time: '08:30', badge: 1, L: 'ד', col: '#e6dcc9' },
  { id: 'friends', name: 'החברות של נועה', group: true, prev: 'מי באה לפארק?', time: '08:12', badge: 5, L: 'ח', col: '#f3cfca', pick: 7.0 },
  { id: 'soccer', name: 'הורים, כדורגל', group: true, prev: 'האימון הוזז ל־17:00', time: 'אתמול', badge: 9, L: 'ה', col: '#d4e3ee' },
  { id: 'spam', name: 'מספר לא מוכר', group: false, prev: 'זכית! לחצו על הקישור', time: 'אתמול', badge: 1, L: '?', col: '#e3e3e0' },
];
const PICKED = ROWS.filter((r) => r.pick);
const OFF = ROWS.filter((r) => !r.pick);
const OFF_T = [8.0, 8.25, 8.5, 8.75, 9.0];
const RISE_T = (i) => 4.5 + i * .125;

// the phone (world units ≈ pixels when flat at zoom 1)
const PH = { w: 660, d: 1300, r: 86 };
const SCR = { w: 620, d: 1260 };
const ROW_H = 122, ROW_W = 600, ROW_Y0 = -SCR.d / 2 + 196;
const rowY = (i) => ROW_Y0 + i * (ROW_H + 6) + ROW_H / 2;

// the home tablet
const TY = 2700;
const TB = { w: 880, d: 1230, r: 62 }, TS = { w: 830, d: 1180 };
const SLOT_W = 770, SLOT_H = 150, SLOT_Y0 = -TS.d / 2 + 250;
const slotY = (i) => SLOT_Y0 + i * (SLOT_H + 22) + SLOT_H / 2;

// ---------- faces
const avatar = (x, y, r, L, col, size) => circ(x, y, r, col) + tx(x, y + 2, L, { size: size || r * 1.0, weight: 700, color: C.ink, anchor: 'c' });
const rowFace = (row, w, h, o = {}) => {
  const picked = o.picked || 0, noa = o.noa || 0;
  let s = rrect(0, 0, w, h, 18, mix(C.white, C.mint, picked), `stroke="${mix(C.line, '#7fae8f', picked)}" stroke-width="${f1(2 + picked * 2)}"`);
  const ar = h * .36;
  s += avatar(w - h * .52, h / 2, ar, row.L, row.col, ar * .95);
  const nx = w - h * 1.02;
  s += tx(nx, h * .35, row.name, { size: h * .33, weight: 700, anchor: 'r' });
  const prev = o.prev || row.prev;
  s += tx(nx, h * .7, prev, { size: h * .22, color: o.prevBold ? C.ink : C.muted, weight: o.prevBold ? 700 : 400 });
  const wa = 1 - noa;
  if (wa > 0.01) {
    s += tx(26, h * .34, row.time, { size: h * .17, color: C.muted, anchor: 'l', op: wa * (1 - picked) });
    const b = o.badge ?? row.badge;
    if (b) { const bw = b > 9 ? 58 : 42; s += `<g opacity="${f1(wa * (1 - picked))}">${rrect(24, h * .62 - 21, bw, 42, 21, C.wa)}${tx(24 + bw / 2, h * .62 + 1, String(b), { size: 26, weight: 700, color: '#fff', anchor: 'c', ltr: true })}</g>`; }
  }
  if (picked > 0.01) s += `<g transform="translate(${f1(58)} ${f1(h / 2)}) scale(${f1(backOut(picked))})">${circ(0, 0, 32, '#2f8a5b')}${check(0, 1, 34, '#fff', 7)}</g>`;
  if (o.dot) s += `<g transform="translate(${f1(52)} ${f1(h / 2)}) scale(${f1(backOut(o.dot))})">${circ(0, 0, 22, C.wa)}${tx(0, 1, '1', { size: 26, weight: 700, color: '#fff', anchor: 'c', ltr: true })}</g>`;
  if (o.tap) { const u = o.tap; s += circ(w * .45, h / 2, 30 + u * 140, '#2f8a5b', `opacity="${f1(.28 * (1 - u))}"`) + circ(w * .45, h / 2, 34 * (1 - u * .4), '#183e35', `opacity="${f1(.18 * (1 - u))}"`); }
  return s;
};
const waHeader = (w) => {
  let s = rrect(0, 0, w, 190, 0, C.white);
  s += tx(w - 40, 70, 'WhatsApp', { size: 50, weight: 700, color: '#1b8a55', anchor: 'r', ltr: true });
  s += rrect(36, 116, w - 72, 56, 28, '#f0f2ef') + tx(w - 70, 145, 'חיפוש', { size: 26, color: C.muted });
  return s;
};
const notifFace = (n, w, u) => {
  const h = 128;
  let s = `<g transform="translate(0 ${f1(-160 + 186 * eout(u))})" opacity="${f1(clamp(u * 3))}">`;
  s += rrect(16, 0, w - 32, h, 26, '#ffffff', 'stroke="#d8ded6" stroke-width="2"');
  s += avatar(w - 84, h / 2, 40, n.L, n.col, 38);
  s += tx(w - 146, 40, n.name, { size: 30, weight: 700 });
  s += tx(w - 146, 86, n.text, { size: 28, color: C.ink });
  s += tx(48, 40, 'עכשיו', { size: 22, color: C.muted, anchor: 'l' });
  return s + '</g>';
};
const NOTIFS = [
  { t: 0.5, L: 'ו', col: '#ddd6e8', name: 'ועד הבית', text: 'מחר אין מים בבוקר' },
  { t: 1.25, L: 'ע', col: '#cdd8e0', name: 'עבודה', text: 'דחוף! מי שולח את המצגת?' },
  { t: 2.0, L: 'ה', col: '#d4e3ee', name: 'הורים, כדורגל', text: 'האימון הוזז ל־17:00' },
  { t: 2.75, L: '?', col: '#e3e3e0', name: 'מספר לא מוכר', text: 'זכית! לחצו על הקישור' },
];
const signinFace = (t, w, h) => {
  const u = (a) => clamp((t - a) / .18);
  const filled = ease((t - 10.95) / .3);
  let s = rrect(0, 0, w, h, 24, C.screen);
  s += tx(w / 2, 150, 'יחד', { size: 58, weight: 800, color: C.green, anchor: 'c' });
  s += circ(w / 2, 380, 96, mix('#eef1ec', '#2f8a5b', filled), `stroke="${C.mint2}" stroke-width="4"`);
  s += tx(w / 2, 386, 'נ', { size: 96, weight: 800, color: '#fff', anchor: 'c', op: filled });
  s += tx(w / 2, 560, 'נכנסים לשיחות', { size: 60, weight: 800, anchor: 'c' });
  s += tx(w / 2, 625, 'עם פרטי הכניסה האישיים שהוגדרו בבית.', { size: 28, color: C.muted, anchor: 'c' });
  s += rrect(110, 690, w - 220, 92, 18, '#fff', `stroke="${C.line}" stroke-width="3"`);
  s += tx(w - 140, 738, 'נועה', { size: 40, weight: 700, op: filled });
  s += tx(w - 140, 738, 'שם משתמש', { size: 32, color: '#9aa69e', op: 1 - filled });
  s += rrect(110, 806, w - 220, 92, 18, '#fff', `stroke="${C.line}" stroke-width="3"`);
  for (let i = 0; i < 5; i++) { const a = u(11.25 + i * .125); s += circ(w - 160 - i * 44, 852, 13 * (.4 + .6 * backOut(a)), C.ink, `opacity="${f1(a)}"`); }
  const press = clamp((t - 11.92) / .1) * (1 - clamp((t - 12.08) / .12));
  s += rrect(110 + press * 6, 940 + press * 4, w - 220 - press * 12, 96 - press * 8, 18, C.green);
  s += tx(w / 2, 988, 'בואו ניכנס', { size: 40, weight: 700, color: '#fff', anchor: 'c' });
  s += tx(w / 2, h - 70, 'כניסה אישית · מכשיר משותף? זכרו לנעול ביציאה', { size: 26, color: C.muted, anchor: 'c' });
  return s;
};
const noaHeaderFace = (w, h) => {
  let s = rrect(0, 0, w, h, 22, '#ffffff', `stroke="${C.line}" stroke-width="2"`);
  s += tx(w - 44, 78, 'השיחות שלי', { size: 58, weight: 800 });
  s += tx(w - 44, 146, 'רק הקבוצות שנבחרו בשבילך', { size: 32, color: C.sage, weight: 700 });
  s += circ(86, h / 2, 46, '#2f8a5b') + tx(86, h / 2 + 3, 'נ', { size: 52, weight: 800, color: '#fff', anchor: 'c' });
  s += tx(156, h / 2, 'נועה', { size: 34, weight: 700, color: C.ink, anchor: 'l' });
  return s;
};
const tabletBase = (w, h) => rrect(0, 0, w, h, 24, '#f6f4ee') + tx(w / 2, h - 52, 'יחד', { size: 40, weight: 800, color: C.mint2, anchor: 'c' });

// chat view on Noa's tablet (local, w × h)
const NOA_MSG = 'סבתא, סיימתי את הציור!';
const chatFace = (t, w, h) => {
  let s = rrect(0, 0, w, h, 24, '#f3f0e8');
  s += rrect(0, 0, w, 150, 24, '#ffffff') + `<rect x="0" y="120" width="${w}" height="30" fill="#ffffff"/>` + `<line x1="0" y1="150" x2="${w}" y2="150" stroke="${C.line}" stroke-width="3"/>`;
  s += avatar(w - 92, 76, 46, 'מ', '#f0d8c3', 44);
  s += tx(w - 162, 58, 'המשפחה', { size: 46, weight: 800 });
  s += tx(w - 162, 106, 'קבוצה', { size: 26, color: C.muted });
  s += `<path d="M44 76 l22 -22 M44 76 l22 22" stroke="${C.ink}" stroke-width="6" stroke-linecap="round" fill="none"/>`;
  // grandma's message (incoming, right side)
  const g = backOut(clamp((t - 16.0) / .3));
  s += `<g transform="translate(${w - 40} 230) scale(${f1(g)}) translate(${-(w - 40)} -230)" opacity="${f1(clamp(g * 2))}">` + rrect(w - 40 - 520, 180, 520, 170, 30, '#ffffff', `stroke="${C.line}" stroke-width="2"`) + tx(w - 72, 226, 'סבתא', { size: 32, weight: 800, color: '#b0623d' }) + tx(w - 72, 294, 'נועה, איך יצא הציור?', { size: 46 }) + '</g>';
  // Noa's sent message (outgoing, left side)
  const sent = clamp((t - 18.0) / .25);
  if (sent > 0) {
    const sc = backOut(sent);
    s += `<g transform="translate(40 420) scale(${f1(sc)}) translate(-40 -420)" opacity="${f1(clamp(sent * 2))}">` + rrect(40, 390, 600, 130, 30, '#d7ebd4', 'stroke="#bcd9b8" stroke-width="2"') + tx(606, 456, NOA_MSG, { size: 46, weight: 700 }) + '</g>';
    const rh = backOut(clamp((t - 20.25) / .3));
    if (rh > 0) s += `<g transform="translate(100 528) scale(${f1(rh * 1.2)})">${rrect(-42, -26, 84, 52, 26, '#ffffff', `stroke="${C.line}" stroke-width="2"`)}<path d="${heartPath(0, 4, 40)}" fill="${C.red}"/></g>`;
  }
  // grandma's reply
  const r2 = backOut(clamp((t - 21.0) / .3));
  if (r2 > 0) s += `<g transform="translate(${w - 40} 660) scale(${f1(r2)}) translate(${-(w - 40)} -660)" opacity="${f1(clamp(r2 * 2))}">` + rrect(w - 40 - 680, 610, 680, 170, 30, '#ffffff', `stroke="${C.line}" stroke-width="2"`) + tx(w - 72, 656, 'סבתא', { size: 32, weight: 800, color: '#b0623d' }) + tx(w - 72, 724, 'איזה יופי, מחכה לשמוע עליו', { size: 46 }) + '</g>';
  // composer
  s += rrect(30, h - 150, w - 160, 100, 50, '#ffffff', `stroke="${C.line}" stroke-width="3"`);
  const nType = Math.floor(clamp((t - 17.05) / .85) * NOA_MSG.length);
  const typed = t < 18.0 ? NOA_MSG.slice(0, nType) : '';
  if (typed) s += tx(w - 165, h - 100, typed, { size: 42 });
  else s += tx(w - 165, h - 100, 'משהו קטן לשתף…', { size: 34, color: '#9aa69e' });
  const sp = clamp((t - 17.92) / .08) * (1 - clamp((t - 18.05) / .1));
  s += circ(w - 66, h - 100, 46 - sp * 6, C.green) + `<path d="M${w - 82} ${h - 100} l30 0 m-12 -14 l14 14 l-14 14" stroke="#fff" stroke-width="6" stroke-linecap="round" stroke-linejoin="round" fill="none" transform="rotate(180 ${w - 66} ${h - 100})"/>`;
  return s;
};

// ---------- grandma's phone (flat overlay)
const grandmaPhone = (t) => {
  const u = backOut(clamp((t - 18.35) / .45));
  if (u <= 0) return '';
  const X = 596, Y = 832, w = 430, h = 760;
  let s = `<g transform="translate(${f1((1 - u) * 520)} ${f1((1 - u) * 160)})">`;
  s += rrect(X - 6, Y + 14, w + 12, h, 64, '#183e35', 'opacity=".18" filter="url(#film-soft)"');
  s += rrect(X, Y, w, h, 58, '#1f2a27');
  const sx = X + 16, sy = Y + 16, sw = w - 32, sh = h - 32;
  s += `<clipPath id="film-gp"><rect x="${sx}" y="${sy}" width="${sw}" height="${sh}" rx="44"/></clipPath><g clip-path="url(#film-gp)">`;
  s += rrect(sx, sy, sw, sh, 0, C.waBg);
  s += rrect(sx, sy, sw, 118, 0, C.waTeal);
  s += circ(sx + sw - 62, sy + 66, 34, '#f0d8c3') + tx(sx + sw - 62, sy + 68, 'מ', { size: 34, weight: 700, anchor: 'c' });
  s += tx(sx + sw - 112, sy + 56, 'המשפחה', { size: 34, weight: 700, color: '#fff' });
  s += tx(sx + sw - 112, sy + 92, 'WhatsApp', { size: 21, color: '#cfe9e2', ltr: true, anchor: 'r' });
  const m = backOut(clamp((t - 19.0) / .3));
  if (m > 0) {
    s += `<g transform="translate(${sx + sw - 18} 300) scale(${f1(m)}) translate(${-(sx + sw - 18)} -300)">`;
    s += rrect(sx + 18, sy + 168, sw - 36, 150, 20, '#ffffff');
    s += tx(sx + sw - 40, sy + 212, 'נועה: סבתא, סיימתי', { size: 31 });
    s += tx(sx + sw - 40, sy + 256, 'את הציור!', { size: 31 });
    s += tx(sx + 40, sy + 292, '10:24', { size: 19, color: '#8a948f', anchor: 'l', ltr: true });
    s += '</g>';
  }
  const hh = clamp((t - 19.75) / .5);
  if (hh > 0) {
    const land = backOut(clamp((t - 20.0) / .3));
    const fy0 = sy + 520, fy1 = sy + 330;
    if (t < 20.0) { const q = eio(hh / .5 * 1); s += `<path d="${heartPath(lerp(sx + 120, sx + sw - 80, q), lerp(fy0, fy1, q) - Math.sin(q * Math.PI) * 60, 64)}" fill="${C.red}"/>`; }
    else {
      s += `<g transform="translate(${sx + sw - 80} ${sy + 326}) scale(${f1(land * 1.35)})">${rrect(-44, -27, 88, 54, 27, '#ffffff', 'stroke="#e2ddd4" stroke-width="2"')}<path d="${heartPath(0, 4, 42)}" fill="${C.red}"/></g>`;
      const b = clamp((t - 20.0) / .55);
      for (let i = 0; i < 9; i++) { const a = i / 9 * Math.PI * 2; const rr = 30 + b * 90; s += circ(sx + sw - 80 + Math.cos(a) * rr, sy + 326 + Math.sin(a) * rr, 7 * (1 - b), i % 2 ? C.red : '#f2b8a8'); }
    }
  }
  s += tx(sx + sw / 2, sy + sh - 66, 'הקלדת הודעה', { size: 24, color: '#9aa69e', anchor: 'c' });
  s += '</g>';
  // grandma's face, above her phone
  const FX = X + w / 2, FY = Y - 150;
  s += `<g><circle cx="${FX}" cy="${FY}" r="86" fill="#fff" filter="url(#film-soft2)"/><clipPath id="film-gface"><circle cx="${FX}" cy="${FY}" r="80"/></clipPath><g clip-path="url(#film-gface)"><rect x="${FX - 90}" y="${FY - 90}" width="180" height="180" fill="#f6efe4"/><image href="${FM}" x="${FX - 615 * .52}" y="${FY - 310 * .52}" width="${1536 * .52}" height="${1024 * .52}"/></g>`;
  s += tx(FX, FY + 116, 'סבתא', { size: 34, weight: 800, anchor: 'c' }) + '</g>';
  return s + '</g>';
};

// ---------- captions (screen space)
const CAPS = [
  [0.25, 3.35, 'נועה רוצה לכתוב לסבתא.', 305],
  [1.7, 3.35, '״לתת לה את כל הווטסאפ שלי?״', 400, 54, 700],
  [4.15, 5.85, 'כל השיחות שלכם, אחת אחת.', 305],
  [5.9, 7.95, 'אתם בוחרים.', 305],
  [7.95, 9.9, 'רק מה שסימנתם עובר לנועה.', 305],
  [10.25, 12.0, 'בטאבלט של הבית, נועה נכנסת.', 305],
  [12.05, 15.95, 'היא רואה רק את מה שבחרתם.', 305],
  [16.0, 18.3, 'סבתא כותבת. נועה עונה.', 305],
  [18.35, 21.85, 'וסבתא מקבלת בווטסאפ.', 305],
];
// Text width for the caption pills. A rough estimate by default; the page swaps in
// a real measurement with the page font (setMeasure), so every pill fits its words.
let measure = (s, size) => s.length * size * .52;
const caption = (t, [t0, t1, s, y, size = 64, weight = 800]) => {
  if (t < t0 || t > t1) return '';
  const a = ease((t - t0) / .22) * (1 - ease((t - (t1 - .2)) / .2)), rise = (1 - eout((t - t0) / .3)) * 22;
  const wApprox = measure(s, size, weight) + size * 1.1;
  const sw = eout((t - t0) / .35);
  return `<g opacity="${f1(a)}">${rrect(540 + wApprox / 2 - wApprox * sw, y - size * .72 + rise, wApprox * sw, size * 1.44, size * .5, C.peach, 'opacity=".85"')}${tx(540, y + rise, s, { size, weight, anchor: 'c' })}</g>`;
};

// ---------- Noa's request (pain)
const noaAsk = (t) => {
  const u = backOut(clamp((t - 0.55) / .35)), out = 1 - ease((t - 3.2) / .3);
  if (u <= 0 || out <= 0) return '';
  const cx = 196, cy = 1452;
  let s = `<g opacity="${f1(out)}" transform="translate(${cx} ${cy}) scale(${f1(u)}) translate(${-cx} ${-cy})">`;
  s += circ(cx, cy, 98, '#ffffff', 'filter="url(#film-soft2)"');
  s += `<clipPath id="film-nface"><circle cx="${cx}" cy="${cy}" r="90"/></clipPath><g clip-path="url(#film-nface)"><rect x="${cx - 100}" y="${cy - 100}" width="200" height="200" fill="#f6efe4"/><image href="${FM}" x="${cx - 1060 * .55}" y="${cy - 350 * .55}" width="${1536 * .55}" height="${1024 * .55}"/></g>`;
  s += tx(cx, cy + 128, 'נועה', { size: 32, weight: 800, anchor: 'c', extra: 'paint-order="stroke" stroke="${C.paper}" stroke-width="8"' });
  const bu = backOut(clamp((t - 0.8) / .3));
  s += `<g transform="translate(${cx + 100} ${cy - 120}) scale(${f1(bu)}) translate(${-(cx + 100)} ${-(cy - 120)})">`;
  s += rrect(cx + 90, cy - 190, 560, 116, 40, '#ffffff', 'filter="url(#film-soft2)" stroke="#183e35" stroke-width="3"');
  s += `<path d="M${cx + 112} ${cy - 76} L${cx + 70} ${cy - 50} L${cx + 160} ${cy - 76}Z" fill="#fff" stroke="#183e35" stroke-width="3" stroke-linejoin="round"/><rect x="${cx + 108}" y="${cy - 82}" width="58" height="6" fill="#fff"/>`;
  s += tx(cx + 370, cy - 132, 'אפשר לכתוב לסבתא?', { size: 46, weight: 700, anchor: 'c' });
  s += '</g></g>';
  return s;
};

// ---------- end card
const endCard = (t) => {
  const l = t - 22.0; if (l < 0) return '';
  const wipe = eout(l / .45);
  let s = `<circle cx="540" cy="1000" r="${f1(wipe * 1300)}" fill="${C.paper}"/>`;
  const a = (d, dur = .4) => ease((l - d) / dur);
  s += `<g opacity="${f1(a(.35))}"><image href="${FM}" x="${540 - 470}" y="${1000}" width="940" height="${940 * 1024 / 1536}"/></g>`;
  const ls = backOut(clamp((l - .15) / .4));
  s += `<g transform="translate(540 560) scale(${f1(ls)})">${tx(0, 0, 'יחד', { size: 250, weight: 800, color: C.green, anchor: 'c', extra: 'letter-spacing="-6"' })}</g>`;
  const sw = a(.45, .45);
  s += `<path d="M790 700 Q540 742 290 700" stroke="${C.peachDeep}" stroke-width="12" stroke-linecap="round" fill="none" stroke-dasharray="560" stroke-dashoffset="${f1(560 * (1 - sw))}"/>`;
  s += tx(540, 820, 'הילדים מדברים. אתם בוחרים.', { size: 68, weight: 800, anchor: 'c', op: a(.7) });
  return s;
};

// ---------- the world at time t
const world = (t) => {
  // ---- phone
  const ex = (t0) => spring(t - t0);
  const gone = ein((t - 9.5) / .55); // the phone shell falls away
  if (t < 10.3) {
    const shellZ = -230 * ex(4.0) - 900 * gone, bodyZ = -95 * ex(4.25) - 820 * gone, scrZ = 70 * ex(4.25) - 760 * gone;
    const fade = 1 - ease((t - 9.6) / .45);
    shadowOn(0, 0, 0, PH.w, PH.d, PH.r, .10 * fade * (1 - ease((t - 3.4) / .6)));
    slab({ cx: 0, cy: 0, z: shellZ, w: PH.w, d: PH.d, h: 20, r: PH.r, top: '#3b4c47', side: C.shell, op: fade, face: `<rect x="${PH.w / 2 - 120}" y="70" width="240" height="0" fill="none"/>` });
    slab({ cx: 0, cy: 0, z: 20 + bodyZ, w: PH.w - 44, d: PH.d - 44, h: 12, r: PH.r - 18, top: C.board, side: '#7f978a', op: fade,
      face: `${rrect(140, 380, PH.w - 44 - 280, 520, 30, '#c8d6cc', 'stroke="#7f978a" stroke-width="3"')}${circ(120, 120, 34, '#2c3b37')}${circ(200, 120, 20, '#2c3b37')}` });
    const n = NOTIFS.filter((q) => t >= q.t).pop();
    const notif = n && t < 3.45 ? notifFace(n, SCR.w, clamp((t - n.t) / .28)) : '';
    slab({ cx: 0, cy: 0, z: 32 + scrZ, w: SCR.w + 10, d: SCR.d + 10, h: 6, r: PH.r - 22, top: '#f4f6f2', side: '#cfd6cf', op: fade,
      face: `<g transform="translate(5 5)">${waHeader(SCR.w)}</g>` });
    // rows
    ROWS.forEach((row, i) => {
      const y0 = rowY(i);
      let cx = 0, cy, z, th = 0, op = fade;
      const r = ex(RISE_T(i));
      cy = lerp(y0, y0 * 1.78, r); z = 38 + 300 * r;
      let picked = 0, tap = 0;
      if (row.pick) {
        picked = ease((t - row.pick) / .18);
        z += 60 * spring(t - row.pick, 8, 13);
        if (t > row.pick - .25 && t < row.pick + .5) tap = clamp((t - (row.pick - .02)) / .5);
        if (t < row.pick) tap = 0;
      }
      let badge = row.badge;
      if (t < 3.5) { const bumps = NOTIFS.filter((q) => q.t <= t && q.name === row.name).length; badge = row.badge + bumps * 1 + Math.floor(t / .5) % 1; }
      if (!row.pick) {
        const k = OFF.indexOf(row), t0 = OFF_T[k];
        const u = ein((t - t0) / .5);
        const dir = k % 2 ? 1 : -1;
        cx += dir * 2300 * u; cy += -dir * 900 * u; z += 500 * u; th = dir * 1.4 * u; op *= 1 - ease((t - t0 - .3) / .2);
      } else {
        op = 1;
        // after the phone falls away: travel with the camera to hover over the tablet
        const k = PICKED.indexOf(row);
        const tr = eio((t - 10.0) / 1.0);
        if (tr > 0) {
          cx = lerp(cx, 120 + (k - 1) * 40, tr); cy = lerp(cy, TY - 760 + k * 200, tr); z = lerp(z, 760 + k * 30, tr);
        }
      }
      if (t < 10.3 || row.pick) slab({ cx, cy, z, w: ROW_W, d: ROW_H, h: 8, r: 18, theta: th, top: '#fff', side: mix('#dfe5df', C.mint2, picked), op,
        face: rowFace(row, ROW_W, ROW_H, { picked, badge, tap }), bias: 2000 });
    });
    if (notif) faceOn({ cx: 0, cy: 0, z: 46, w: SCR.w, d: SCR.d, face: notif, bias: 3000 });
  }
  // ---- tablet
  if (t > 9.6) {
    const ti = ease((t - 9.7) / .7);
    const press = spring(t - 14.0, 9, 14);
    const lift = spring(t - 12.0) * (1 - press);
    const cx = 0, cy = TY;
    shadowOn(cx, cy, 0, TB.w, TB.d, TB.r, .12 * ti * (1 - ease((t - 14.2) / .6)));
    slab({ cx, cy, z: 0, w: TB.w, d: TB.d, h: 24, r: TB.r, top: C.tablet, side: C.tabletSide, op: ti, face: '' });
    // screen panel
    const pz = 24 + 150 * lift;
    slab({ cx, cy, z: pz, w: TS.w, d: TS.d, h: 6, r: 28, top: '#f6f4ee', side: '#d9dfd6', op: ti, face: tabletBase(TS.w, TS.d), bias: 500 });
    // sign-in layer: sits on the screen until 12.0, then flies off
    const so = ein((t - 12.0) / .45);
    if (so < 1) slab({ cx: cx - 900 * so, cy: cy - 600 * so, z: 30 + 700 * so, w: TS.w, d: TS.d, h: 4, r: 28, top: C.screen, side: '#e3e6df', op: ti * (1 - ease((t - 12.25) / .2)), face: signinFace(t, TS.w, TS.d), bias: 1000 });
    // Noa's header plate drops in
    const hdIn = t < 12.25 ? ein((t - 12.0) / .25) : 1 + Math.exp(-9 * (t - 12.25)) * Math.sin(16 * (t - 12.25)) * -.06;
    if (t > 12.0) {
      const hz = 30 + 150 * lift + (1 - hdIn) * 500;
      slab({ cx, cy: cy + (-TS.d / 2 + 105), z: hz, w: TS.w - 30, d: 190, h: 6, r: 22, top: '#ffffff', side: '#dfe5df', op: clamp((t - 12.0) / .15), face: noaHeaderFace(TS.w - 30, 190), bias: 2000 });
    }
  }
  // ---- the picked chats: from the hover into Noa's slots
  if (t >= 10.3) {
    PICKED.forEach((row, k) => {
      const tLand = 12.5 + k * .5;
      const hx = 120 + (k - 1) * 40, hy = TY - 760 + k * 200, hz = 760 + k * 30;
      const press = spring(t - 14.0, 9, 14);
      const lift = spring(t - 12.0) * (1 - press);
      const sz = 30 + 150 * lift;
      const mu = eio((t - (tLand - .32)) / .32);
      const land = t >= tLand ? Math.exp(-9 * (t - tLand)) * Math.sin(16 * (t - tLand)) * 26 : 0;
      const bob = (1 - mu) * Math.sin((t - 10.3) * 3 + k) * 12;
      const cx = lerp(hx, 0, mu), cy = lerp(hy, TY + slotY(k), mu), z = lerp(hz, sz, ein(mu)) + bob + land;
      const w = lerp(ROW_W, SLOT_W, mu), d = lerp(ROW_H, SLOT_H, mu);
      const noa = ease((t - tLand) / .3);
      const fam = row.id === 'family';
      const prev = fam && t >= 16.0 ? 'סבתא: נועה, איך יצא הציור?' : null;
      const dot = fam ? clamp((t - 16.0) / .25) : 0;
      const tap = fam && t > 16.55 && t < 17.0 ? clamp((t - 16.55) / .45) : 0;
      slab({ cx, cy, z, w, d, h: lerp(8, 4, noa), r: 18, top: '#fff', side: mix(C.mint2, '#dfe5df', noa), op: 1,
        face: rowFace(row, w, d, { picked: 1 - noa, noa, prev, prevBold: !!prev, dot: t < 16.6 ? dot : 0, tap }), bias: 2500 });
    });
  }
  // ---- chat view slides over the list
  if (t >= 16.6) {
    const u = eout((t - 16.6) / .35);
    slab({ cx: lerp(-900, 0, u), cy: TY, z: 40, w: TS.w, d: TS.d, h: 2, r: 28, top: '#f3f0e8', side: '#e0ddd3', op: 1, edge: .3, face: chatFace(t, TS.w, TS.d), bias: 5000 });
  }
};

function render(t) {
  parts.length = 0;
  setCamera(camAt(t));
  world(t);
  parts.sort((a, b) => a.depth - b.depth);
  let s = `<svg xmlns="http://www.w3.org/2000/svg" width="${W}" height="${H}" viewBox="0 0 ${W} ${H}" font-family="Yahad, system-ui, sans-serif">`;
  s += `<defs><filter id="film-soft" x="-30%" y="-30%" width="160%" height="160%"><feGaussianBlur stdDeviation="18"/></filter><filter id="film-soft2" x="-20%" y="-20%" width="140%" height="160%"><feDropShadow dx="0" dy="6" stdDeviation="8" flood-color="#183e35" flood-opacity=".18"/></filter></defs>`;
  s += `<rect width="${W}" height="${H}" fill="${C.paper}"/>`;
  s += parts.map((p) => p.svg).join('');
  s += noaAsk(t);
  s += grandmaPhone(t);
  for (const c of CAPS) s += caption(t, c);
  s += endCard(t);
  return s + '</svg>';
}

const END = 23.5; // the end card is fully drawn by 23.2 s
const setMeasure = (fn) => { measure = fn; };
window.YahadFilm = { render, setMeasure, END, W, H };
})();
