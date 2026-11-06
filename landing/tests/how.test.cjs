const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');

const source = fs.readFileSync('dist/how.js', 'utf8');

function setup({ sticky = true, stacked = false } = {}) {
  const events = {};
  const css = new Map();
  const attributes = new Set();
  let scroll = 0;
  const demo = {
    offsetWidth: 320,
    getBoundingClientRect: () => ({ bottom: 240, height: 240 }),
  };
  const story = { offsetWidth: stacked ? 320 : 800 };
  const label = {};
  const chapters = ['connected', 'approved', 'conversation'].map((sceneLabel, index) => {
    const attrs = new Map();
    return {
      attrs,
      dataset: { sceneLabel },
      getBoundingClientRect: () => ({ top: 300 + index * 160 - scroll, height: 160 }),
      setAttribute: (name, value) => attrs.set(name, value),
      removeAttribute: name => attrs.delete(name),
    };
  });
  const section = {
    dataset: {},
    querySelector: selector => ({
      '.how-story': story, '.how-demo': demo, '.how-scene-label': label,
    })[selector],
    querySelectorAll: () => chapters,
    toggleAttribute(name, present) {
      if (present) attributes.add(name);
      else attributes.delete(name);
    },
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
  const document = { getElementById: () => section, documentElement: { clientHeight: 620 } };
  const getComputedStyle = () => ({ position: sticky ? 'sticky' : 'static', top: '0px' });
  vm.runInNewContext(source, { window, document, getComputedStyle });

  return {
    section, label, chapters, css, attributes,
    scrollTo(value) {
      scroll = value;
      events.scroll();
      events.scroll();
      const render = queued;
      queued = undefined;
      render();
    },
  };
}

function assertChapter(view, index) {
  assert.equal(view.section.dataset.chapter, String(index + 1));
  assert.equal(view.label.textContent, view.chapters[index].dataset.sceneLabel);
  assert.deepEqual(
    view.chapters.map(chapter => chapter.attrs.get('aria-current')),
    view.chapters.map((_, i) => i === index ? 'step' : undefined),
  );
}

for (const stacked of [false, true]) {
  const view = setup({ stacked });
  const line = stacked ? 288 : 240 * 0.78 - 24;
  const conversation = 620 - line;
  assertChapter(view, 0);
  view.scrollTo(490 - line);
  assertChapter(view, 1);
  view.scrollTo(conversation + 20);
  assertChapter(view, 2);
  assert.equal(view.section.dataset.transfer, 'photo');
  assert.equal(view.attributes.has('data-photo'), false);
  assert.ok(parseFloat(view.css.get('--travel')) < 0);
  view.scrollTo(conversation + 55);
  assert.equal(view.section.dataset.transfer, 'reply');
  assert.equal(view.attributes.has('data-photo'), true);
  assert.equal(view.attributes.has('data-reply'), false);
  view.scrollTo(conversation + 100);
  assert.equal(view.section.dataset.transfer, '');
  assert.equal(view.attributes.has('data-reply'), true);
  view.scrollTo(0);
  assertChapter(view, 0);
  assert.equal(view.attributes.has('data-reply'), false);
}
assert.equal(setup({ sticky: false }).section.dataset.scroll, undefined);
console.log('PASS: chapters follow their position, photo and reply track scroll, and stylesheet failure stays static');
