import { test } from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import { createRequire } from 'node:module';
import { readFileSync } from 'node:fs';
import ts from 'typescript';
import { familyProgress, type FamilyChild } from '../web/FamilyProgress';

// Execute actual component handlers with deterministic hooks; browser QA is separate.
function harness() {
  const slots: any[] = [], cleanups: Array<() => void> = []; let cursor = 0;
  const pending: Array<{ change: any; resolve: (ok: boolean) => void; reject: () => void }> = [];
  const module = { exports: {} as any }, require = createRequire(import.meta.url);
  const code = ts.transpileModule(readFileSync(new URL('../web/ChildAccessSettings.tsx', import.meta.url), 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, jsx: ts.JsxEmit.ReactJSX, target: ts.ScriptTarget.ES2022 },
  }).outputText;
  vm.runInNewContext(code, { module, exports: module.exports,
    require: (name: string) => name === 'react' ? {
      useState(initial: any) { const i = cursor++; if (!(i in slots)) slots[i] = initial; return [slots[i], (v: any) => slots[i] = v]; },
      useRef(initial: any) { const i = cursor++; return slots[i] ||= { current: initial }; },
      useEffect(effect: () => (() => void)) { const i = cursor++; if (!(i in slots)) { slots[i] = true; const cleanup = effect(); if (cleanup) cleanups.push(cleanup); } },
    } : name === './useDialogFocus' ? { useDialogFocus() {} } : require(name),
  });
  const props = { child: { id: 'a', name: 'Synthetic child', paused: false, accessRevision: 0 } as FamilyChild, disabled: false, stale: false,
    onChange: (change: any) => new Promise<boolean>((resolve, reject) => pending.push({ change, resolve, reject: () => reject(new Error('lost response')) })) };
  const nodes = (node: any): any[] => !node || typeof node !== 'object' ? [] : [node, ...[node.props?.children].flat(Infinity).flatMap(nodes)];
  const render = () => { cursor = 0; return module.exports.ChildAccessSettings(props); };
  const all = () => nodes(render());
  const button = (label: string) => all().find(n => n.type === 'button' && n.props.children === label);
  return { props, pending, render, all, button, open: () => all().find(n => n.type === 'button').props.onClick(),
    dialog: () => all().find(n => n.props?.role === 'dialog'),
    unmount: () => cleanups.forEach(cleanup => cleanup()) };
}

test('opening and cancellation by button or Escape never mutate; dialog names the target and consequences', () => {
  const h = harness(); h.open();
  assert.ok(h.dialog()); assert.match(JSON.stringify(h.dialog()), /Synthetic child/);
  assert.match(JSON.stringify(h.dialog()), /כל המכשירים המחוברים ינותקו/);
  h.button('ביטול').props.onClick(); assert.equal(h.dialog(), undefined); assert.equal(h.pending.length, 0);
  h.open(); h.dialog().props.onKeyDown({ key: 'Escape' });
  assert.equal(h.dialog(), undefined); assert.equal(h.pending.length, 0);
});

test('actual confirmation serializes repeated clicks and cannot be cancelled mid-save; success requires fresh server state', async () => {
  const h = harness(); h.open();
  const confirm = h.button('אישור השהיה').props.onClick;
  confirm(); confirm();
  assert.equal(h.pending.length, 1);
  assert.deepEqual(JSON.parse(JSON.stringify(h.pending[0].change)), { childId: 'a', paused: true, revision: 0 });
  assert.equal(h.button('ביטול').props.disabled, true);
  h.dialog().props.onKeyDown({ key: 'Escape' }); assert.ok(h.dialog());
  h.pending[0].resolve(true); await new Promise(resolve => setImmediate(resolve));
  assert.equal(h.dialog(), undefined);
  assert.match(JSON.stringify(h.render()), /לא התקבל אישור לפעולה/);
  h.props.child = { ...h.props.child, paused: true, accessRevision: 1 };
  assert.match(JSON.stringify(h.render()), /הגישה הושהתה וכל המכשירים נותקו/);
  assert.ok(h.button('הפעלת הגישה מחדש'));
});

test('stale snapshot, changed revision, and duplicate clicks cannot submit; lost response reconciles only exact target/revision', async () => {
  const h = harness(); h.props.disabled = true; h.props.stale = true; h.open();
  assert.equal(h.dialog(), undefined);
  h.props.disabled = false; h.props.stale = false; h.open();
  const oldConfirm = h.button('אישור השהיה').props.onClick;
  h.props.child.accessRevision = 2;
  assert.equal(h.button('אישור השהיה').props.disabled, true);
  h.button('אישור השהיה').props.onClick(); assert.equal(h.pending.length, 0);
  h.button('ביטול').props.onClick(); h.props.child.accessRevision = 0; h.open();
  h.button('אישור השהיה').props.onClick(); h.pending[0].reject();
  await new Promise(resolve => setImmediate(resolve));
  h.props.child = { ...h.props.child, paused: true, accessRevision: 1 }; h.props.stale = true;
  assert.match(JSON.stringify(h.render()), /לא ניתן לאמת את הפעולה/);
  h.props.stale = false; assert.match(JSON.stringify(h.render()), /הגישה הושהתה/);
  h.props.child.accessRevision = 3; assert.match(JSON.stringify(h.render()), /לא התקבל אישור לפעולה/);
  assert.equal(typeof oldConfirm, 'function');
});

test('resume captures current revision and requires fresh login; unmount suppresses late feedback', async () => {
  const h = harness(); h.props.child = { ...h.props.child, paused: true, accessRevision: 1 }; h.open();
  assert.match(JSON.stringify(h.dialog()), /מכשירים שהתנתקו לא יתחברו אוטומטית/);
  h.button('אישור הפעלה מחדש').props.onClick();
  assert.deepEqual(JSON.parse(JSON.stringify(h.pending[0].change)), { childId: 'a', paused: false, revision: 1 });
  h.unmount(); h.pending[0].resolve(true); await new Promise(resolve => setImmediate(resolve));
  const next = harness(); next.props.child.id = 'b';
  assert.equal(next.dialog(), undefined); assert.equal(next.pending.length, 0);
});

test('paused accounts retain grants but are never advertised as ready for tablet login', () => {
  const data = { state: 'connected', children: [{ id: 'a', name: 'A', paused: true }, { id: 'b', name: 'B' }],
    groups: [{ id: 'g', title: 'Synthetic group' }], grants: [{ user_id: 'a', group_id: 'g' }, { user_id: 'b', group_id: 'g' }] };
  const progress = familyProgress(data);
  assert.equal(progress.children[0].ready, false); assert.equal(progress.children[0].groups.length, 1);
  assert.equal(progress.children[1].ready, true);
});
