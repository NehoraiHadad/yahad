import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import { createRequire } from 'node:module';
import ts from 'typescript';

// Exercise the actual component handlers with deterministic hooks, not a DOM.
// Focus, browser validation and geometry still require browser acceptance.
function harness() {
  const slots: any[] = []; let cursor = 0;
  const requests: any[] = []; let resolve: () => void = () => {};
  let closed = 0;
  const module = { exports: {} as any };
  const require = createRequire(import.meta.url);
  const code = ts.transpileModule(readFileSync(new URL('../web/AccountSettings.tsx', import.meta.url), 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, jsx: ts.JsxEmit.ReactJSX, target: ts.ScriptTarget.ES2022 },
  }).outputText;
  vm.runInNewContext(code, {
    module, exports: module.exports,
    require: (name: string) => name === 'react' ? {
      useState(initial: any) { const i = cursor++; if (!(i in slots)) slots[i] = initial; return [slots[i], (v: any) => slots[i] = v]; },
      useRef(initial: any) { const i = cursor++; return slots[i] ||= { current: initial }; },
    } : name === './useDialogFocus' ? { useDialogFocus() {} }
      : name === './FamilySetup' ? { passphraseHelp: 'synthetic help' }
      : name === './api' ? { api: (...args: any[]) => { requests.push(args); return new Promise<void>(r => resolve = r); } }
      : require(name),
  });
  const render = () => { cursor = 0; return module.exports.AccountSettings({ children: [{ id: 'a', username: 'alice' }, { id: 'b', username: 'bob' }], onClose() { closed++; } }); };
  function nodes(node: any): any[] { return !node || typeof node !== 'object' ? [] : [node, ...[node.props?.children].flat(Infinity).flatMap(nodes)]; }
  const find = (predicate: (n: any) => boolean) => nodes(render()).find(predicate);
  return { requests, finish: () => resolve(), closed: () => closed,
    form: () => find(n => n.type === 'form'), select: () => find(n => n.type === 'select'),
    close: () => find(n => n.type === 'button' && n.props['aria-label'] === 'סגירה'),
    passwords: () => nodes(render()).filter(n => n.type === 'input' && n.props.autoComplete === 'new-password'),
    text: () => JSON.stringify(render()),
  };
}

test('password reset serializes repeated submits and holds its original target until confirmed', async () => {
  const h = harness();
  h.select().props.onChange({ target: { value: 'a' } });
  for (const input of h.passwords()) input.props.onChange({ target: { value: 'synthetic secret phrase' } });
  const event = { preventDefault() {} };
  const submit = h.form().props.onSubmit;
  const pending = submit(event); await submit(event);
  assert.equal(h.requests.length, 1);
  assert.equal(h.select().props.disabled, true);
  assert.equal(h.close().props.disabled, true);
  h.select().props.onChange({ target: { value: 'b' } });
  h.close().props.onClick(); h.form().props.onKeyDown({ key: 'Escape' });
  assert.equal(h.closed(), 0);
  assert.equal(h.select().props.value, 'a');
  assert.equal(h.requests[0][2].childId, 'a');
  h.finish(); await pending;
  assert.equal(h.select().props.disabled, false);
  assert.ok(h.text().includes('הסיסמה של alice עודכנה וכל חיבורי החשבון נותקו.'));
  assert.ok(!h.text().includes('הסיסמה של bob עודכנה'));
  assert.ok(h.passwords().every(input => input.props.value === ''));
  h.form().props.onKeyDown({ key: 'Escape' });
  assert.equal(h.closed(), 1);
});

test('password mismatch is inline and sends no request', async () => {
  const h = harness();
  h.passwords()[0].props.onChange({ target: { value: 'synthetic secret phrase' } });
  await h.form().props.onSubmit({ preventDefault() {} });
  assert.equal(h.requests.length, 0);
  assert.ok(h.text().includes('הסיסמאות אינן זהות.'));
});
