// Behaviour of dist/how.js in a stubbed DOM (no browser needed).
// how.js must: stay inert without the section or without IntersectionObserver,
// opt the section into .how-live, mirror the step crossing the reading line
// into data-step / .is-active, and aim the camera transform at that step.
'use strict';
const assert = require('assert');
const fs = require('fs');
const path = require('path');
const vm = require('vm');

const SRC = fs.readFileSync(path.join(__dirname, '../dist/how.js'), 'utf8');

function classList() {
  const set = new Set();
  return {
    add: c => set.add(c),
    remove: c => set.delete(c),
    contains: c => set.has(c),
    toggle: (c, on) => (on ? set.add(c) : set.delete(c), on),
  };
}
function el(dataset = {}) {
  return { dataset, classList: classList(), style: {}, clientWidth: 0, clientHeight: 0, getBoundingClientRect: () => ({}) };
}

function setup({ withSection = true, withIO = true, narrow = false, stageSize = [600, 800], viewport = 900 } = {}) {
  const steps = [1, 2, 3, 4, 5, 6, 7].map(n => el({ step: String(n) }));
  const stage = el();
  [stage.clientWidth, stage.clientHeight] = stageSize;
  const cam = el();
  const section = el({ step: '7' });
  section.querySelector = sel => ({ '.how-stage': stage, '.how-cam': cam }[sel] || null);
  section.querySelectorAll = sel => (sel === '.how-step' ? steps : []);
  const observers = [];
  const listeners = {};
  const window = {
    innerHeight: viewport,
    matchMedia: () => ({ matches: narrow, addEventListener() {} }),
    addEventListener: (type, fn) => { listeners[type] = fn; },
  };
  if (withIO) {
    window.IntersectionObserver = class {
      constructor(cb, opts) { this.cb = cb; this.opts = opts; this.targets = []; observers.push(this); }
      observe(t) { this.targets.push(t); }
      disconnect() { this.targets = []; }
    };
  }
  const context = {
    window,
    document: { getElementById: id => (withSection && id === 'how-it-works' ? section : null) },
    IntersectionObserver: window.IntersectionObserver,
    ResizeObserver: undefined,
  };
  vm.runInNewContext(SRC, context);
  const cross = n => observers.at(-1).cb([{ isIntersecting: true, target: steps[n - 1] }]);
  return { section, steps, cam, observers, cross, window, listeners };
}

// Missing section: nothing runs, nothing throws.
setup({ withSection: false });

// No IntersectionObserver: the section stays a plain list.
{
  const { section, cam } = setup({ withIO: false });
  assert.ok(!section.classList.contains('how-live'), 'no-IO must not opt in');
  assert.strictEqual(section.dataset.step, '7');
  assert.strictEqual(cam.style.transform, undefined);
}

// Live: opts in, starts at step 1, follows the reading line.
{
  const { section, steps, cam, observers, cross } = setup({ stageSize: [1000, 600] });
  assert.strictEqual(section.dataset.world, 'wide', 'landscape stage: side-by-side world');
  assert.ok(section.classList.contains('how-live'));
  assert.strictEqual(section.dataset.step, '1');
  assert.ok(steps[0].classList.contains('is-active'));
  assert.strictEqual(observers.length, 1);
  assert.strictEqual(observers[0].targets.length, 7, 'every step is observed');
  // Pixels from the viewport height: rootMargin % would resolve against the width.
  assert.strictEqual(observers[0].opts.rootMargin, '-450px 0px -441px 0px', 'wide reading line is the middle');
  const atPhone = cam.style.transform;
  assert.match(atPhone, /^translate\(-?[\d.]+px, -?[\d.]+px\) scale\([\d.]+\)$/);

  cross(5);
  assert.strictEqual(section.dataset.step, '5');
  assert.deepStrictEqual(steps.map(s => s.classList.contains('is-active')), [false, false, false, false, true, false, false]);
  assert.notStrictEqual(cam.style.transform, atPhone, 'camera re-aims for the overview');

  // Overview fits the whole wide world (1180x680) inside 90% of a 1000x600 stage.
  const s = Number(/scale\(([\d.]+)\)/.exec(cam.style.transform)[1]);
  assert.ok(Math.abs(s - Math.min((1000 * 0.9) / 1180, (600 * 0.9) / 680)) < 1e-3, `overview scale ${s}`);

  // Leaving entries and unknown steps change nothing.
  observers[0].cb([{ isIntersecting: false, target: steps[1] }]);
  assert.strictEqual(section.dataset.step, '5');
  observers[0].cb([{ isIntersecting: true, target: el({ step: '9' }) }]);
  assert.strictEqual(section.dataset.step, '5');
}

// Narrow: reading line sits below the sticky stage; zoom never exceeds the cap.
{
  const { observers, cam, window, listeners } = setup({ narrow: true, stageSize: [375, 455], viewport: 812 });
  assert.strictEqual(observers[0].opts.rootMargin, '-601px 0px -203px 0px');
  // Same height: no rebuild. New height (mobile toolbar, rotation): a fresh line.
  listeners.resize();
  assert.strictEqual(observers.length, 1);
  window.innerHeight = 700;
  listeners.resize();
  assert.strictEqual(observers.length, 2);
  assert.strictEqual(observers[1].opts.rootMargin, '-518px 0px -175px 0px');
  assert.strictEqual(observers[0].targets.length, 0, 'old observer disconnected');
  const s = Number(/scale\(([\d.]+)\)/.exec(cam.style.transform)[1]);
  assert.ok(s > 0 && s <= 1.15, `scale ${s}`);
}

// Portrait stage on a wide screen (desktop 1024x768 -> stage 509x768): the stacked
// world shows larger, so it is chosen even though the text layout is wide.
{
  const { section, cam, observers, cross } = setup({ stageSize: [509, 768], viewport: 768 });
  assert.strictEqual(section.dataset.world, 'tall');
  assert.strictEqual(observers[0].opts.rootMargin, '-384px 0px -376px 0px', 'text layout still wide');
  cross(5);
  const s = Number(/scale\(([\d.]+)\)/.exec(cam.style.transform)[1]);
  assert.ok(Math.abs(s - Math.min((509 * 0.9) / 600, (768 * 0.9) / 1300)) < 1e-3, `overview scale ${s}`);
  assert.ok(s > Math.min((509 * 0.9) / 1180, (768 * 0.9) / 680), 'beats the side-by-side world');
}

// Camera geometry, every step in both worlds: the step's world rectangle is
// centred in the stage and fits inside 90% of it (or the zoom cap binds).
{
  const WORLD = vm.runInNewContext('(' + /const WORLD = (\{[\s\S]*?\n\});/.exec(SRC)[1] + ')');
  const TARGET = vm.runInNewContext('(' + /const TARGET = (\{[^}]*\})/.exec(SRC)[1] + ')');
  const parse = t => {
    const m = /^translate\((-?[\d.]+)px, (-?[\d.]+)px\) scale\(([\d.]+)\)$/.exec(t);
    return m.slice(1).map(Number);
  };
  for (const [w, h, world] of [[1000, 600, 'wide'], [509, 768, 'tall'], [375, 455, 'tall']]) {
    const { section, cam, cross } = setup({ stageSize: [w, h] });
    assert.strictEqual(section.dataset.world, world, `${w}x${h}`);
    for (let n = 1; n <= 7; n++) {
      if (n > 1) cross(n);
      const [tx, ty, s] = parse(cam.style.transform);
      const [x, y, rw, rh] = WORLD[world][TARGET[n]];
      assert.ok(Math.abs((x + rw / 2) * s + tx - w / 2) < 1 && Math.abs((y + rh / 2) * s + ty - h / 2) < 1, `step ${n} ${world}: target centred`);
      assert.ok(rw * s <= w * 0.9 + 0.5 && rh * s <= h * 0.9 + 0.5, `step ${n} ${world}: target fits`);
      assert.ok(rw * s >= w * 0.9 - 0.5 || rh * s >= h * 0.9 - 0.5 || Math.abs(s - 1.15) < 1e-3, `step ${n} ${world}: fills the stage unless capped`);
    }
  }
  // A huge stage: the natural zoom on the phone exceeds the cap, so the cap binds.
  const { cam, cross } = setup({ stageSize: [3000, 2400] });
  cross(2);
  assert.strictEqual(parse(cam.style.transform)[2], 1.15, 'zoom capped at 1.15');
}

// Zero-size stage (hidden): aim() must not write a broken transform.
{
  const { cam } = setup({ stageSize: [0, 0] });
  assert.strictEqual(cam.style.transform, undefined);
}

console.log('PASS: how.js inert without section/IO, opts in, follows a px reading line rebuilt on resize, aims the camera, caps zoom');
