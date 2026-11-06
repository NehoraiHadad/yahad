const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');

const source = fs.readFileSync('dist/how.js', 'utf8');

function setup({ reduced = false, stageHeight = 600, viewportHeight = 640, top = 40 } = {}) {
  const events = {};
  const css = new Map();
  const attributes = new Set();
  const captions = Array.from({ length: 7 }, () => {
    const classes = new Set();
    return {
      classes,
      classList: {
        add: name => classes.add(name),
        remove: name => classes.delete(name),
      },
    };
  });
  const stage = { offsetHeight: stageHeight };
  const counter = {};
  const motion = { matches: !reduced, addEventListener: (name, fn) => { events[name] = fn; } };
  const section = {
    dataset: {},
    toggleAttribute(name, present) {
      if (present) attributes.add(name);
      else attributes.delete(name);
    },
    removeAttribute: name => attributes.delete(name),
    get clientHeight() { return stage.offsetHeight + 1400; },
    getBoundingClientRect: () => ({ top }),
    querySelector: selector => ({ '.how-stage': stage, '.how-counter': counter })[selector],
    querySelectorAll: () => captions,
    style: { setProperty: (name, value) => css.set(name, value) },
  };

  let queued;
  const window = {
    CSS: { supports: () => true },
    innerHeight: viewportHeight,
    matchMedia(query) {
      assert.match(query, /prefers-reduced-motion: no-preference/);
      return motion;
    },
    requestAnimationFrame(fn) {
      assert.equal(queued, undefined, 'scroll updates share one animation frame');
      queued = fn;
      return 1;
    },
    addEventListener(name, fn, options) {
      if (name === 'scroll') assert.equal(options.passive, true);
      events[name] = fn;
    },
  };
  vm.runInNewContext(source, { window, document: { getElementById: () => section } });

  return {
    section, stage, counter, captions, css, attributes, motion, events,
    scrollTo(value) {
      top = value;
      events.scroll();
      events.scroll();
      const render = queued;
      queued = undefined;
      render?.();
    },
  };
}

function assertStep(view, step) {
  assert.equal(view.section.dataset.step, String(step));
  assert.equal(view.counter.textContent, `שלב ${step} מתוך 7`);
  assert.deepEqual(
    view.captions.map(caption => caption.classes.has('is-current')),
    view.captions.map((_, i) => i + 1 === step),
  );
}

const view = setup();
assertStep(view, 1);
for (let step = 1; step <= 7; step++) {
  view.scrollTo(-200 * (step - 1) - 100);
  assertStep(view, step);
}
view.scrollTo(-1250);
assert.equal(view.css.get('--travel'), '75%');
assert.equal(view.attributes.has('data-arrived'), false);
view.scrollTo(-1450);
assert.equal(view.css.get('--travel'), '0%');
assert.equal(view.attributes.has('data-arrived'), true);
view.scrollTo(-1050);
assertStep(view, 6);
assert.equal(view.css.get('--travel'), '50%');
view.scrollTo(-850);
assertStep(view, 5);
assert.equal(view.css.get('--travel'), '25%');
assert.equal(view.attributes.has('data-arrived'), false);
view.scrollTo(-950);
assert.equal(view.css.get('--travel'), '75%');
assert.equal(view.attributes.has('data-arrived'), true);
view.scrollTo(40);
assertStep(view, 1);
assertStep(setup({ top: -650 }), 4);

view.motion.matches = false;
view.events.change();
assert.equal(view.section.dataset.scroll, undefined);
assert.equal(view.section.dataset.step, undefined);
assert.ok(view.captions.every(caption => !caption.classes.has('is-current')));
view.motion.matches = true;
view.events.change();
assertStep(view, 1);
view.stage.offsetHeight = 660;
view.events.resize();
assert.equal(view.section.dataset.scroll, undefined);
assert.equal(setup({ reduced: true }).section.dataset.scroll, undefined);
assert.equal(setup({ stageHeight: 650 }).section.dataset.scroll, undefined);
console.log('PASS: seven scroll steps, reversible transfers, passive scroll, reduced motion and short viewport fallback');
