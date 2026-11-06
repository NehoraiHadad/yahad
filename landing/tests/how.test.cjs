const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');

const source = fs.readFileSync('dist/how.js', 'utf8');
const html = fs.readFileSync('dist/index.html', 'utf8');

// Without the section the script exits quietly.
vm.runInNewContext(source, { document: { getElementById: () => null }, matchMedia: () => ({ matches: false }) });

// Reduced motion: no live mode, the calm still stays.
let added = false;
const section = { classList: { add: () => { added = true; } } };
vm.runInNewContext(source, {
  document: { getElementById: id => (id === 'how-it-works' ? section : null) },
  matchMedia: () => ({ matches: true }),
});
assert.equal(added, false, 'reduced motion keeps the static end state');

// Seven steps: seven captions and seven progress dots in the markup.
const explainer = html.split('id="how-it-works"')[1].split('</section>')[0];
assert.equal((explainer.match(/<li class="how-cap"/g) || []).length, 7);
assert.equal((explainer.match(/<i><\/i>/g) || []).length >= 7, true);

// Step timing remap: 8 monotonic boundaries from 0 to 1.
for (const name of ['BND', 'MAP']) {
  const m = source.match(new RegExp(name + '=\\[([^\\]]+)\\]'));
  assert.ok(m, name + ' missing');
  const v = m[1].split(',').map(Number);
  assert.equal(v.length, 8);
  assert.equal(v[0], 0); assert.equal(v[7], 1);
  v.slice(1).forEach((x, i) => assert.ok(x >= v[i], name + ' must not go backwards'));
}
console.log('PASS: how.js guards (no section, reduced motion), 7 captions, monotonic step remap');
