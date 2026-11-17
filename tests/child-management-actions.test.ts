import { test } from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import { createRequire } from 'node:module';
import { readFileSync } from 'node:fs';
import ts from 'typescript';
import { childLabel } from '../web/childLabels';
import { familyProgress, type FamilyOverview } from '../web/FamilyProgress';
import { ChildActionGuard, confirmedChildGrant, type ActionOutcome } from '../web/childManagementState';
import * as apiModule from '../web/api';
import * as errorReferenceModule from '../web/ErrorReference';

// The real handlers, rendered with deterministic hooks. This does not claim browser/focus QA.
function harness() {
  const slots: any[] = []; let cursor = 0; const cleanups: Array<() => void> = [];
  const pending: Array<{ change: any; resolve: (outcome: ActionOutcome) => void }> = [];
  const GroupPicker = () => null, module = { exports: {} as any }, require = createRequire(import.meta.url);
  const code = ts.transpileModule(readFileSync(new URL('../web/ChildManagement.tsx', import.meta.url), 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, jsx: ts.JsxEmit.ReactJSX, target: ts.ScriptTarget.ES2022 },
  }).outputText;
  vm.runInNewContext(code, { module, exports: module.exports,
    require: (name: string) => name === 'react' ? {
      useState(initial: any) { const i = cursor++; if (!(i in slots)) slots[i] = initial; return [slots[i], (v: any) => slots[i] = typeof v === 'function' ? v(slots[i]) : v]; },
      useRef(initial: any) { const i = cursor++; return slots[i] ||= { current: initial }; },
      useEffect(effect: () => (() => void)) { const i = cursor++; if (!(i in slots)) { slots[i] = true; const cleanup = effect(); if (cleanup) cleanups.push(cleanup); } },
    } : name === './GroupPicker' ? { GroupPicker } : name === './FamilyProgress' ? { familyProgress }
      : name === './childLabels' ? { childLabel } : name === './childManagementState' ? { ChildActionGuard, confirmedChildGrant }
      : name === './ChildUsageSettings' ? { ChildUsageSettings: () => null }
      : name === './ChildAccessSettings' ? { ChildAccessSettings: () => null }
      : name === './api' ? apiModule : name === './ErrorReference' ? errorReferenceModule : require(name),
  });
  const data: FamilyOverview = { state: 'connected', children: [{ id: 'a', name: 'אלף' }, { id: 'b', name: 'בית' }],
    groups: [{ id: 'g', title: 'שיחה סינתטית' }], grants: [] };
  const props = { data, selected: 'a', stale: false, busy: false, createdChild: '', onSelect() {}, onAdd() {}, onSettings() {}, onRefresh() {}, onRetry() {},
    onGrant: (change: any) => new Promise<ActionOutcome>(resolve => pending.push({ change, resolve })) };
  function nodes(node: any): any[] { return !node || typeof node !== 'object' ? [] : [node, ...[node.props?.children].flat(Infinity).flatMap(nodes)]; }
  const render = () => {
    cursor = 1000; const root = module.exports.ChildManagement(props);
    const workspace = nodes(root).find(node => typeof node.type === 'function' && node.type.name === 'ChildWorkspace');
    cursor = 0; return workspace.type(workspace.props);
  };
  return { data, props, pending, render,
    picker: () => nodes(render()).find(node => node.type === GroupPicker),
    feedback: () => nodes(render()).find(node => typeof node.props?.className === 'string' && node.props.className.startsWith('child-save-feedback')),
    unmount: () => cleanups.forEach(cleanup => cleanup()),
  };
}

test('actual grant handler serializes repeated clicks and sends only captured account/group/allowed fields', async () => {
  const h = harness(), toggle = h.picker().props.onToggle;
  const one = toggle('g', true), two = toggle('g', true);
  await two;
  assert.equal(h.pending.length, 1);
  assert.deepEqual(JSON.parse(JSON.stringify(h.pending[0].change)), { childId: 'a', groupId: 'g', allowed: true });
  assert.equal(h.picker().props.busy, true);
  assert.ok(JSON.stringify(h.feedback()).includes('שומרים את השינוי'));
  h.pending[0].resolve({ ok: true }); await one;
  assert.ok(!h.feedback().props.className.includes('confirmed'), 'HTTP acknowledgement alone cannot confirm persisted state');
  h.data.grants = [{ user_id: 'b', group_id: 'g' }];
  assert.ok(!h.feedback().props.className.includes('confirmed'), 'sibling grant cannot confirm selected-child save');
  h.data.grants.push({ user_id: 'a', group_id: 'g' });
  assert.ok(h.feedback().props.className.includes('confirmed'));
});

test('stale state and failed/lost mutation responses reconcile without fabricated success', async () => {
  const h = harness(); h.props.stale = true;
  await h.picker().props.onToggle('g', true); assert.equal(h.pending.length, 0);
  h.props.stale = false;
  const pending = h.picker().props.onToggle('g', true); h.pending[0].resolve({ ok: false, error: new Error('lost') }); await pending;
  assert.equal(h.feedback().props.role, 'alert');
  h.data.grants = [{ user_id: 'a', group_id: 'g' }]; h.props.stale = true;
  assert.ok(!h.feedback().props.className.includes('confirmed'));
  h.props.stale = false;
  assert.ok(h.feedback().props.className.includes('confirmed'), 'fresh authoritative state resolves a lost response');
});

test('switch/unmount invalidates late feedback and a new child workspace starts without old search/save state', async () => {
  const first = harness(); const pending = first.picker().props.onToggle('g', true);
  first.unmount(); const next = harness(); next.props.selected = 'b';
  assert.equal(next.picker().props.childId, 'b');
  assert.equal(next.picker().props.busy, false);
  assert.ok(!JSON.stringify(next.feedback()).includes('שומרים את השינוי'));
  first.pending[0].resolve({ ok: true }); await pending;
  assert.ok(!JSON.stringify(next.feedback()).includes('אושרה עבור'));
  const second = next.picker().props.onToggle('g', true);
  assert.equal(next.pending[0].change.childId, 'b'); next.pending[0].resolve({ ok: true }); await second;
});

test('revoke remains visibly approved while saving, then confirms only fresh persisted removal', async () => {
  const h = harness(); h.data.grants = [{ user_id: 'a', group_id: 'g' }];
  const pending = h.picker().props.onToggle('g', false);
  assert.deepEqual(h.picker().props.grants, [{ user_id: 'a', group_id: 'g' }]);
  h.pending[0].resolve({ ok: true }); await pending;
  assert.ok(!h.feedback().props.className.includes('confirmed'));
  h.data.grants = []; assert.ok(h.feedback().props.className.includes('confirmed'));
  assert.ok(JSON.stringify(h.feedback()).includes('הוסרה מהשיחות המאושרות'));
});

test('a definite server refusal shows its own reason and support reference; a lost response stays "not confirmed"', async () => {
  const h = harness();
  const pending = h.picker().props.onToggle('g', true);
  h.pending[0].resolve({ ok: false, error: new apiModule.ApiError('הבקשה נדחתה מסיבה ידועה.', 409, 'GRANT_CONFLICT_TEST', undefined, 'abc123def0') });
  await pending;
  const shown = JSON.stringify(h.feedback());
  assert.ok(shown.includes('הבקשה נדחתה מסיבה ידועה.'), shown);
  assert.ok(shown.includes('"code":"GRANT_CONFLICT_TEST"') && shown.includes('"refId":"abc123def0"'), shown);
  assert.ok(!shown.includes('לא התקבל אישור'), 'a refusal is not reported as an unknown outcome');
  const lost = h.picker().props.onToggle('g', true);
  h.pending[1].resolve({ ok: false, error: new apiModule.ApiNetworkError('offline') });
  await lost;
  assert.ok(JSON.stringify(h.feedback()).includes('לא התקבל אישור'));
});
