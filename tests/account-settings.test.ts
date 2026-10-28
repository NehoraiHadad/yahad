import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import { createRequire } from 'node:module';
import ts from 'typescript';
import { childLabel } from '../web/childLabels.js';
import { normalizeAccountName, accountNameKey } from '../src/account-name.js';

// Exercise the actual component handlers with deterministic hooks, not a DOM.
// Focus, browser validation and geometry still require browser acceptance.
function harness(initialChildId?: string, nameA?: string) {
  const slots: any[] = []; let cursor = 0;
  const requests: any[] = []; let resolve: (value?: any) => void = () => {}; let reject: (error: Error) => void = () => {};
  let closed = 0, refreshed = 0;
  const module = { exports: {} as any };
  const require = createRequire(import.meta.url);
  const code = ts.transpileModule(readFileSync(new URL('../web/AccountSettings.tsx', import.meta.url), 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, jsx: ts.JsxEmit.ReactJSX, target: ts.ScriptTarget.ES2022 },
  }).outputText;
  vm.runInNewContext(code, {
    module, exports: module.exports,
    require: (name: string) => name === 'react' ? {
      useState(initial: any) { const i = cursor++; if (!(i in slots)) slots[i] = initial; return [slots[i], (v: any) => slots[i] = typeof v === 'function' ? v(slots[i]) : v]; },
      useRef(initial: any) { const i = cursor++; return slots[i] ||= { current: initial }; },
    } : name === './useDialogFocus' ? { useDialogFocus() {} }
      : name === './FamilySetup' ? { passphraseHelp: 'synthetic help' }
      : name === './childLabels' ? { childLabel }
      : name === '../src/account-name' ? { normalizeAccountName, accountNameKey }
      : name === './api' ? { isTransientApiError: (e: Error) => e.message === 'network', api: (...args: any[]) => { requests.push(args); return new Promise((r, j) => { resolve = r; reject = j; }); } }
      : require(name),
  });
  const render = () => { cursor = 0; return module.exports.AccountSettings({ children: [{ id: 'a', name: nameA || 'alice' }, { id: 'b', name: 'נועם' }], initialChildId, onProfileChanged() { refreshed++; }, onClose() { closed++; } }); };
  function nodes(node: any): any[] { return !node || typeof node !== 'object' ? [] : [node, ...[node.props?.children].flat(Infinity).flatMap(nodes)]; }
  const find = (predicate: (n: any) => boolean) => nodes(render()).find(predicate);
  return { requests, finish: (value?: any) => resolve(value), fail: (error: Error) => reject(error), closed: () => closed, refreshed: () => refreshed,
    dialog: () => find(n => n.props?.role === 'dialog'),
    profileInput: () => find(n => n.type === 'input' && n.props.id === 'account-name'),
    save: () => find(n => n.type === 'button' && n.props.className === 'primary'),
    mode: (value: string) => find(n => n.type === 'button' && n.props.children === value),
    form: () => find(n => n.type === 'form'), select: () => find(n => n.type === 'select'),
    close: () => find(n => n.type === 'button' && n.props['aria-label'] === 'סגירה'),
    passwords: () => nodes(render()).filter(n => n.type === 'input' && n.props.autoComplete === 'new-password'),
    text: () => JSON.stringify(render()),
  };
}

test('password reset serializes repeated submits and holds its original target until confirmed', async () => {
  const h = harness();
  h.select().props.onChange({ target: { value: 'a' } });
  h.mode('סיסמה').props.onClick();
  for (const input of h.passwords()) input.props.onChange({ target: { value: 'synthetic secret phrase' } });
  const event = { preventDefault() {} };
  const submit = h.form().props.onSubmit;
  const pending = submit(event); await submit(event);
  assert.equal(h.requests.length, 1);
  assert.equal(h.select().props.disabled, true);
  assert.equal(h.close().props.disabled, true);
  h.select().props.onChange({ target: { value: 'b' } });
  h.close().props.onClick(); h.dialog().props.onKeyDown({ key: 'Escape' });
  assert.equal(h.closed(), 0);
  assert.equal(h.select().props.value, 'a');
  assert.equal(h.requests[0][2].childId, 'a');
  h.finish(); await pending;
  assert.equal(h.select().props.disabled, false);
  assert.ok(h.text().includes('הסיסמה של alice עודכנה וכל חיבורי החשבון נותקו.'));
  assert.ok(!h.text().includes('הסיסמה של bob עודכנה'));
  assert.ok(h.passwords().every(input => input.props.value === ''));
  h.dialog().props.onKeyDown({ key: 'Escape' });
  assert.equal(h.closed(), 1);
});

test('password mismatch is inline and sends no request', async () => {
  const h = harness();
  h.passwords()[0].props.onChange({ target: { value: 'synthetic secret phrase' } });
  await h.form().props.onSubmit({ preventDefault() {} });
  assert.equal(h.requests.length, 0);
  assert.ok(h.text().includes('הסיסמאות אינן זהות.'));
});


test('one child name is used for display and login, separate from password controls', () => {
  const h = harness('b');
  assert.equal(h.select().props.value, 'b');
  assert.equal(h.profileInput().props.value, 'נועם');
  assert.equal(h.passwords().length, 0, 'renaming must not require a password');
  assert.equal(h.save().props.disabled, true, 'unchanged names need no request');
  assert.ok(h.text().includes('שם אחד שמופיע באפליקציה וגם משמש לכניסה'));
  assert.ok(!h.text().includes('שם משתמש לכניסה'));
  assert.ok(h.text().includes('השינוי לא משנה את החתימה'));
  assert.ok(h.text().includes('אחרי השמירה נכנסים בשם החדש'));
});

test('account-name save serializes repeated submissions and preserves target through confirmation', async () => {
  const h = harness('a');
  h.profileInput().props.onChange({ target: { value: '  עלמה  ' } });
  const submit = h.form().props.onSubmit, event = { preventDefault() {} };
  const pending = submit(event); await submit(event);
  assert.equal(h.requests.length, 1);
  assert.equal(h.requests[0][0], '/parent/child-profile');
  assert.equal(h.requests[0][1], 'PUT');
  assert.deepEqual(JSON.parse(JSON.stringify(h.requests[0][2])), { childId: 'a', name: 'עלמה' });
  assert.equal(h.select().props.disabled, true);
  assert.equal(h.profileInput().props.disabled, true);
  assert.equal(h.mode('סיסמה').props.disabled, true);
  h.mode('סיסמה').props.onClick(); h.select().props.onChange({ target: { value: 'b' } });
  h.close().props.onClick(); h.dialog().props.onKeyDown({ key: 'Escape' });
  assert.equal(h.select().props.value, 'a'); assert.equal(h.closed(), 0);
  h.finish({ child: { id: 'a', name: 'עלמה' } }); await pending;
  assert.equal(h.refreshed(), 1);
  assert.equal(h.profileInput().props.value, 'עלמה');
  assert.equal(h.save().props.disabled, true);
  assert.ok(h.text().includes('השם עודכן ל־עלמה'));
  assert.ok(h.text().includes('השם הקודם כבר לא משמש לכניסה'));
  h.select().props.onChange({ target: { value: 'b' } });
  assert.equal(h.profileInput().props.value, 'נועם');
  assert.ok(!h.text().includes('השם עודכן ל־עלמה'));
  h.select().props.onChange({ target: { value: 'a' } });
  assert.equal(h.profileInput().props.value, 'עלמה', 'confirmed local name survives pending overview refresh');
});

test('empty or unsafe name is inline, cancel does not save, and changing accounts discards drafts', async () => {
  for (const value of ['   ', 'x'.repeat(61), 'a\u202eb', 'a\x7fb']) {
    const h = harness('a');
    h.profileInput().props.onChange({ target: { value } });
    await h.form().props.onSubmit({ preventDefault() {} });
    assert.equal(h.requests.length, 0); assert.ok(h.text().includes('כתבו שם באורך'));
  }
  const h = harness('a');
  h.profileInput().props.onChange({ target: { value: 'טיוטה' } });
  h.select().props.onChange({ target: { value: 'b' } });
  h.select().props.onChange({ target: { value: 'a' } });
  assert.equal(h.profileInput().props.value, 'alice');
  h.close().props.onClick(); assert.equal(h.closed(), 1); assert.equal(h.requests.length, 0);
});

test('failed or uncertain rename is never presented as saved and leaves a recoverable form', async () => {
  for (const error of [new Error('not allowed'), new Error('network')]) {
    const h = harness('a');
    h.profileInput().props.onChange({ target: { value: 'שם חדש' } });
    const pending = h.form().props.onSubmit({ preventDefault() {} });
    h.fail(error); await pending;
    assert.equal(h.profileInput().props.disabled, false); assert.equal(h.save().props.disabled, false);
    assert.ok(!h.text().includes('השם עודכן ל־'));
    assert.ok(h.text().includes(error.message === 'network' ? 'ייתכן שהשם עודכן' : 'not allowed'));
    assert.equal(h.refreshed(), 1);
    h.close().props.onClick(); assert.equal(h.closed(), 1);
  }
});


test('name conflict is explicit and does not apply an unconfirmed name', async () => {
  const h = harness('a');
  h.profileInput().props.onChange({ target: { value: 'נועם' } });
  const pending = h.form().props.onSubmit({ preventDefault() {} });
  h.fail(new Error('השם כבר בשימוש. בחרו שם אחר, למשל שם פרטי ושם משפחה.')); await pending;
  assert.ok(h.text().includes('השם כבר בשימוש'));
  assert.ok(!h.text().includes('השם עודכן ל־'));
  h.select().props.onChange({ target: { value: 'b' } });
  h.select().props.onChange({ target: { value: 'a' } });
  assert.equal(h.profileInput().props.value, 'alice');
});


test('normalization-equivalent rename does not falsely claim old capitalization is rejected', async () => {
  const h = harness('a');
  h.profileInput().props.onChange({ target: { value: 'Alice' } });
  const pending = h.form().props.onSubmit({ preventDefault() {} });
  h.finish({ child: { id: 'a', name: 'Alice' } }); await pending;
  assert.ok(h.text().includes('השם עודכן ל־Alice'));
  assert.ok(!h.text().includes('השם הקודם כבר לא משמש לכניסה'));
});
