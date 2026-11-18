// dist/film-scene.js (the frames) and dist/film.js (the player), no browser needed.
'use strict';
const assert = require('assert');
const fs = require('fs');
const path = require('path');
const vm = require('vm');

const SCENE = fs.readFileSync(path.join(__dirname, '../dist/film-scene.js'), 'utf8');
const PLAYER = fs.readFileSync(path.join(__dirname, '../dist/film.js'), 'utf8');

// ---- the scene: every frame is a well-formed, deterministic SVG
const scene = (() => { const ctx = { window: {} }; vm.runInNewContext(SCENE, ctx); return ctx.window.YahadFilm; })();
assert.ok(scene && typeof scene.render === 'function', 'film-scene.js exposes YahadFilm.render');
assert.strictEqual(scene.W * 16, scene.H * 9, 'portrait 9:16 frame');
// Tags must nest: an unclosed <g> would pull later layers into its transform.
function balanced(svg) {
  const stack = [];
  for (const [, close, name, self] of svg.matchAll(/<(\/?)([a-zA-Z][\w:-]*)[^>]*?(\/?)>/g)) {
    if (self) continue;
    if (!close) stack.push(name);
    else if (stack.pop() !== name) return false;
  }
  return stack.length === 0;
}
assert.ok(balanced('<svg><g><rect/></g></svg>') && !balanced('<svg><g><rect/></svg>') && !balanced('<svg><g></svg></g>'), 'tag checker');
const BAD = /NaN|undefined|Infinity|="#(?!(?:[0-9a-f]{3}|[0-9a-f]{6})")/;
for (let i = 0; i <= Math.round(scene.END * 50); i++) {
  const t = i / 50, s = scene.render(t);
  assert.ok(s.startsWith('<svg') && s.endsWith('</svg>') && balanced(s), `frame ${t}: unbalanced tags`);
  assert.ok(!BAD.test(s), `invalid value in frame ${t}: ${s.slice(Math.max(0, s.search(BAD) - 80), s.search(BAD) + 30)}`);
  // The SVG is inlined into the page: every id must be namespaced.
  for (const [, id] of s.matchAll(/ id="([^"]+)"/g)) assert.ok(id.startsWith('film-'), `id ${id}`);
  for (const [, ref] of s.matchAll(/url\(#([^)]+)\)/g)) assert.ok(s.includes(` id="${ref}"`), `dangling url(#${ref}) at ${t}`);
}
assert.strictEqual(scene.render(12.34), scene.render(12.34), 'render is deterministic');
assert.ok(scene.render(scene.END).includes('הילדים מדברים. אתם בוחרים.'), 'ends on the end card');
// Caption pills follow the measured text width (the page supplies a real measure).
{
  const pill = (t) => Number(/<rect x="[\d.-]+" y="[\d.-]+" width="([\d.]+)"[^>]*fill="#f0d8c3"/.exec(scene.render(t))[1]);
  const rough = pill(6.5);
  scene.setMeasure(() => 500);
  assert.ok(Math.abs(pill(6.5) - (500 + 64 * 1.1)) < 0.5, `pill = text + padding: ${pill(6.5)}`);
  scene.setMeasure((s, size) => s.length * size * .52);
  assert.strictEqual(pill(6.5), rough);
}

// ---- the player, against a fake film whose frame is just its time
function el() {
  const listeners = {};
  const set = new Set();
  return {
    dataset: {}, hidden: false, innerHTML: '', textContent: '',
    classList: { add: (c) => set.add(c), remove: (c) => set.delete(c), contains: (c) => set.has(c), toggle: (c, on) => (on ? set.add(c) : set.delete(c), on) },
    addEventListener: (type, fn) => { listeners[type] = fn; },
    click() { listeners.click(); },
  };
}

function setup({ withSection = true, withFilm = true, withIO = true, reduced = false } = {}) {
  const frame = el(), box = el(), toggle = el('button'), label = el('span');
  box.hidden = true;
  const section = el();
  section.querySelector = (sel) => ({ '.film': box, '.film-frame': frame, '.film-toggle': toggle, '.film-toggle-text': label }[sel] || null);
  const frames = [];
  let now = 0;
  const observers = [];
  const mq = { matches: reduced, addEventListener(type, fn) { this.fn = fn; } };
  const window = {
    matchMedia: () => mq,
    requestAnimationFrame: (fn) => { frames.push(fn); return frames.length; },
    cancelAnimationFrame: () => { frames.length = 0; },
  };
  if (withFilm) window.YahadFilm = { END: scene.END, render: (t) => `T${t.toFixed(3)}`, setMeasure(fn) { this.measure = fn; } };
  if (withIO) {
    window.IntersectionObserver = class {
      constructor(cb, opts) { this.cb = cb; this.opts = opts; observers.push(this); }
      observe(target) { this.target = target; }
    };
  }
  const measured = [];
  const pen = { font: '', measureText(text) { measured.push([this.font, text]); return { width: 123 }; } };
  const document = { getElementById: (id) => (withSection && id === 'how-it-works' ? section : null), createElement: (tag) => (tag === 'canvas' ? { getContext: () => pen } : el()) };
  vm.runInNewContext(PLAYER, { window, document, performance: { now: () => now } });
  const time = () => Number(frame.innerHTML.slice(1));
  const run = (ms) => { // advance the clock one display frame (60 Hz) at a time
    for (let left = ms; left > 0; left -= 1000 / 60) {
      now += Math.min(1000 / 60, left);
      const fn = frames.shift(); if (fn) fn(now);
    }
  };
  const view = (ratio) => observers[0].cb([{ isIntersecting: ratio > 0, intersectionRatio: ratio }]);
  const gap = (ms) => { now += ms; };
  return { measured, film: window.YahadFilm, section, box, frame, toggle, label, observers, frames, mq, time, run, view, gap };
}

// The player measures caption text in the page font.
{
  const { measured, film } = setup();
  assert.strictEqual(film.measure('אתם בוחרים.', 64, 800), 123);
  assert.deepStrictEqual(measured, [['800 64px Yahad, system-ui, sans-serif', 'אתם בוחרים.']]);
}
// Missing pieces: nothing runs, the section stays a plain list.
for (const opts of [{ withSection: false }, { withFilm: false }, { withIO: false }]) {
  const { section, box } = setup(opts);
  assert.ok(!section.classList.contains('film-live') && box.hidden, JSON.stringify(opts));
}

// Live: shows the first frame, waits until half the film is on screen, then plays at ~30 fps.
{
  const { section, box, frame, toggle, label, observers, frames, time, run, view } = setup();
  assert.ok(section.classList.contains('film-live') && !box.hidden);
  assert.strictEqual(frame.innerHTML, 'T0.000');
  assert.strictEqual(toggle.dataset.state, 'play'); assert.strictEqual(label.textContent, 'הפעלה');
  assert.deepStrictEqual([...observers[0].opts.threshold], [0, 0.5]);
  assert.strictEqual(observers[0].target, frame, 'observes the film frame');
  view(0.3);
  assert.strictEqual(frames.length, 0, 'not yet: under half visible');
  view(0.6);
  assert.strictEqual(toggle.dataset.state, 'pause'); assert.strictEqual(label.textContent, 'עצירה');
  let draws = 0; const seen = new Set();
  for (let i = 0; i < 60; i++) { run(1000 / 60); if (!seen.has(frame.innerHTML)) { seen.add(frame.innerHTML); draws++; } }
  assert.ok(Math.abs(time() - 1) < 0.05, `one second of film after one second: ${time()}`);
  assert.ok(draws >= 28 && draws <= 31, `about 30 frames drawn: ${draws}`);
  // Scrolled away: pauses; back: resumes.
  view(0); const t1 = time(); run(500); assert.strictEqual(time(), t1); assert.strictEqual(toggle.dataset.state, 'play');
  view(0.8); run(500); assert.ok(time() > t1);
}

// The visitor pauses: scrolling back does not resume. Replay after the end.
{
  const { toggle, label, time, run, view } = setup();
  view(1); run(2000);
  toggle.click(); const t1 = time(); run(500);
  assert.strictEqual(time(), t1); assert.strictEqual(toggle.dataset.state, 'play');
  view(0); view(1); run(500); assert.strictEqual(time(), t1, 'held: no autoplay');
  toggle.click(); run(30000);
  assert.strictEqual(time(), scene.END); assert.strictEqual(toggle.dataset.state, 'replay'); assert.strictEqual(label.textContent, 'מההתחלה');
  view(0); view(1); run(500); assert.strictEqual(time(), scene.END, 'an ended film does not restart by scrolling');
  toggle.click(); run(100); assert.ok(time() > 0 && time() < 0.2, 'replay starts over');
}

// A hidden tab does not fast-forward the film.
{
  const { time, run, view, gap } = setup();
  view(1); run(100);
  const before = time();
  gap(60000); run(1000 / 60);
  assert.ok(time() - before <= 0.11, `capped step after a long gap: ${time()}`);
}

// Reduced motion starts with a still and honors manual play.
{
  const { frames, toggle, time, run, view } = setup({ reduced: true });
  assert.strictEqual(time(), 0);
  view(1); assert.strictEqual(frames.length, 0); assert.strictEqual(toggle.dataset.state, 'play');
  toggle.click(); run(500); assert.ok(time() > 0, 'plays when asked');
}

// Turning reduced motion off while in view lets the film play.
{
  const { mq, time, run, view } = setup({ reduced: true });
  view(1); mq.matches = false; mq.fn(); run(1000);
  assert.ok(time() > 0);
}

// Turning reduced motion on mid-film pauses at the current position.
{
  const { mq, toggle, time, run, view } = setup();
  view(1); run(5000);
  const before = time();
  mq.matches = true; mq.fn(); run(250);
  assert.strictEqual(toggle.dataset.state, 'play'); assert.strictEqual(time(), before);
}

console.log(`PASS: film scene (${Math.round(scene.END * 50) + 1} frames valid, ids namespaced, deterministic), player autoplay/pause/hold/replay and reduced motion`);
