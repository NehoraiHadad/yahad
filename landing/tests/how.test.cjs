const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');

const source = fs.readFileSync('dist/how.js', 'utf8');

function element() {
  const attributes = {};
  const events = {};
  return {
    attributes,
    dataset: {},
    hidden: true,
    textContent: '',
    setAttribute: (key, value) => { attributes[key] = value; },
    addEventListener: (type, fn) => { events[type] = fn; },
    click: () => events.click(),
  };
}

const captions = Array.from({ length: 7 }, (_, i) => ({
  ...element(),
  querySelector: tag => ({ textContent: `${tag}-${i + 1}` }),
}));
const progress = element();
const previous = element();
const next = element();
const controls = element();
const status = element();
const elements = {
  '.how-progress': progress,
  '.how-controls': controls,
  '.how-prev': previous,
  '.how-next': next,
  '[role="status"]': status,
};
const section = {
  ...element(),
  querySelectorAll: () => captions,
  querySelector: selector => elements[selector],
};
const document = { getElementById: () => section };
vm.runInNewContext(source, { document });

function assertStep(step) {
  assert.equal(section.dataset.step, step);
  assert.equal(progress.textContent, `שלב ${step} מתוך 7`);
  assert.deepEqual(
    captions.map(caption => caption.attributes['aria-hidden'] === 'false'),
    captions.map((_, i) => i + 1 === step),
  );
  assert.equal(previous.attributes['aria-disabled'], String(step === 1));
}

assertStep(1);
assert.equal(controls.hidden, false);
assert.equal(progress.hidden, false);
assert.equal(status.textContent, '');
previous.click();
assertStep(1);
for (let step = 2; step <= 7; step++) {
  next.click();
  assertStep(step);
  assert.equal(status.textContent, `שלב ${step} מתוך 7. h3-${step}. p-${step}`);
}
assert.equal(next.textContent, 'מההתחלה');
previous.click();
assertStep(6);
assert.equal(next.textContent, 'הבא');
next.click();
next.click();
assertStep(1);

delete elements['.how-controls'];
section.dataset = {};
vm.runInNewContext(source, { document });
assert.equal(section.dataset.step, undefined, 'incomplete markup must leave the static page available');
console.log('PASS: all seven steps, previous, restart, accessible announcements and static fallback');
