const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');

const source = fs.readFileSync('dist/how.js', 'utf8');

function node() {
  const attrs = new Map();
  const classes = new Set();
  return {
    attrs, classes, dataset: {},
    setAttribute: (name, value) => attrs.set(name, value),
    removeAttribute: name => attrs.delete(name),
    toggleAttribute(name, on) { if (on) attrs.set(name, ''); else attrs.delete(name); },
    classList: {
      add: name => classes.add(name),
      remove: name => classes.delete(name),
      toggle(name, on) { if (on) classes.add(name); else classes.delete(name); },
    },
    style: { setProperty(name, value) { this[name] = value; }, removeProperty(name) { delete this[name]; } },
  };
}

function setup({ sticky = true, reduced = false } = {}) {
  const events = {};
  let scroll = 0;
  let queued;
  const demo = { offsetWidth: 320, getBoundingClientRect: () => ({ bottom: 240, height: 240 }) };
  const chapters = Array.from({ length: 3 }, (_, index) => ({
    ...node(),
    getBoundingClientRect: () => ({ top: 300 + index * 250 - scroll, height: 250 }),
  }));
  const scene = { ...node(), clientWidth: 320, clientHeight: 196 };
  const phone = node();
  const tablet = node();
  const typed = node();
  const dots = Array.from({ length: 7 }, node);
  const switches = Array.from({ length: 3 }, node);
  const pins = Array.from({ length: 4 }, node);
  const queries = {
    '.how-story': { offsetWidth: 320 }, '.how-demo': demo, '.how-scene': scene,
    '.how-scene-label': node(), '.typed': typed,
    '.how-phone-slot': { offsetWidth: 90, offsetLeft: 224 }, '.dev-phone': phone,
    '.how-tablet-slot': { offsetWidth: 160, offsetLeft: 6 }, '.dev-tablet': tablet,
    '.how-chapter': chapters, '.how-dots i': dots, '[data-approval]': switches, '.pin i': pins,
  };
  const section = { ...node(), querySelector: selector => queries[selector], querySelectorAll: selector => queries[selector] };
  const motion = { matches: reduced, addEventListener(name, fn) { events[name] = fn; } };
  const window = {
    CSS: { supports: () => true },
    matchMedia: () => motion,
    requestAnimationFrame(fn) {
      assert.equal(queued, undefined, 'scroll updates share one animation frame');
      queued = fn;
    },
    addEventListener(name, fn, options) {
      if (name === 'scroll') assert.equal(options.passive, true);
      events[name] = fn;
    },
  };
  const document = { getElementById: () => section, documentElement: { clientHeight: 620 } };
  const getComputedStyle = () => ({ position: sticky ? 'sticky' : 'static', top: '0px' });
  vm.runInNewContext(source, { window, document, getComputedStyle });

  return {
    section, scene, chapters, phone, tablet, typed, switches, pins, dots,
    scrollTo(index, progress) {
      const chapter = index === 0 ? 0 : index <= 3 ? 1 : 2;
      const advance = chapter === 0 ? progress * 110 : (index - (chapter === 1 ? 1 : 4) + progress) / 3 * 196;
      scroll = 300 + chapter * 250 - 288 + advance;
      events.scroll();
      events.scroll();
      const frame = queued;
      queued = undefined;
      frame();
    },
  };
}

const view = setup();
const names = ['connect', 'approve', 'blocked', 'signin', 'photo', 'type', 'reply'];
for (const [index, name] of names.entries()) {
  view.scrollTo(index, 0.5);
  assert.equal(view.section.dataset.beat, name);
  assert.deepEqual(view.dots.map(dot => dot.classes.has('on')), names.map((_, i) => i === index));
}
view.scrollTo(0, 0.1);
assert.equal(view.section.attrs.has('data-linked'), false);
view.scrollTo(0, 0.52);
assert.equal(view.section.attrs.has('data-linked'), true);
assert.match(view.phone.style.transform, /rotateY/);
assert.equal(view.tablet.style.opacity, '0');
view.scrollTo(1, 0.45);
assert.deepEqual(view.switches.map(control => control.classes.has('on')), [true, true, false]);
view.scrollTo(2, 0.52);
assert.equal(view.section.attrs.has('data-lock'), true);
const blocked = parseFloat(view.scene.style['--travel']);
view.scrollTo(2, 0.95);
assert.ok(parseFloat(view.scene.style['--travel']) > blocked);
view.scrollTo(3, 0.9);
assert.ok(view.pins.every(pin => pin.classes.has('filled')));
assert.equal(view.section.attrs.has('data-press'), true);
view.scrollTo(4, 0.72);
assert.equal(view.section.attrs.has('data-photo'), true);
view.scrollTo(5, 0.50);
assert.ok(view.typed.textContent.startsWith('וואו'));
view.scrollTo(6, 0.16);
assert.equal(view.typed.textContent, 'וואו! איזה יפים 😍');
assert.equal(view.section.attrs.has('data-send-press'), true);
view.scrollTo(6, 0.86);
assert.equal(view.section.attrs.has('data-reply'), true);
view.scrollTo(0, 0.12);
assert.equal(view.section.attrs.has('data-reply'), false);
assert.equal(view.chapters[0].attrs.get('aria-current'), 'step');
assert.equal(setup({ reduced: true }).phone.style.transform, undefined);
assert.equal(setup({ sticky: false }).section.dataset.scroll, undefined);
console.log('PASS: seven beats, reversible camera and transfers, approvals, PIN, typing and fallback');
