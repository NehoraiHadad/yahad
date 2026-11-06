const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');

const source = fs.readFileSync('dist/how.js', 'utf8');

function setup({ top = 40, trackHeight = 1400 } = {}) {
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
  const track = { offsetHeight: trackHeight };
  const counter = {};
  const section = {
    dataset: {},
    clientTop: 1,
    toggleAttribute(name, present) {
      if (present) attributes.add(name);
      else attributes.delete(name);
    },
    getBoundingClientRect: () => ({ top }),
    querySelector: selector => ({ '.how-scroll-track': track, '.how-counter': counter })[selector],
    querySelectorAll: () => captions,
    style: { setProperty: (name, value) => css.set(name, value) },
  };

  let queued;
  const window = {
    CSS: { supports: () => true },
    requestAnimationFrame(fn) {
      assert.equal(queued, undefined, 'scroll updates share one animation frame');
      queued = fn;
    },
    addEventListener(name, fn, options) {
      if (name === 'scroll') assert.equal(options.passive, true);
      events[name] = fn;
    },
  };
  vm.runInNewContext(source, { window, document: { getElementById: () => section } });

  function flush() {
    const render = queued;
    queued = undefined;
    render?.();
  }

  return {
    section, track, counter, captions, css, attributes,
    scrollTo(value) {
      top = -value - section.clientTop;
      events.scroll();
      events.scroll();
      flush();
    },
    resize(height) {
      track.offsetHeight = height;
      events.resize();
      flush();
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
  view.scrollTo(200 * (step - 1) + 100);
  assertStep(view, step);
}
view.scrollTo(1250);
assert.equal(view.css.get('--travel'), '-75%');
assert.equal(view.attributes.has('data-arrived'), false);
view.scrollTo(1450);
assert.equal(view.css.get('--travel'), '0%');
assert.equal(view.attributes.has('data-arrived'), true);
view.scrollTo(1050);
assertStep(view, 6);
assert.equal(view.css.get('--travel'), '-50%');
view.scrollTo(850);
assertStep(view, 5);
assert.equal(view.css.get('--travel'), '-25%');
assert.equal(view.attributes.has('data-arrived'), false);
view.scrollTo(950);
assert.equal(view.css.get('--travel'), '-75%');
assert.equal(view.attributes.has('data-arrived'), true);
view.resize(1000);
assertStep(view, 7);
view.scrollTo(-40);
assertStep(view, 1);
assertStep(setup({ top: -651 }), 4);
assert.equal(setup({ trackHeight: 0 }).section.dataset.scroll, undefined);
console.log('PASS: seven scroll steps, reversible transfers, passive scroll, resize and missing stylesheet fallback');
