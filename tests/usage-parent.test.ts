import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import vm from 'node:vm';
import ts from 'typescript';
import { ApiError } from '../web/api.js';

function harness() {
  let cursor = 0, nextId = 0, writes = 0; const slots: any[] = [], effects: (() => (() => void) | void)[] = [], cleanup: (() => void)[] = [];
  const requests: { path: string; method?: string; body: any; resolve: (v: any) => void; reject: (e: Error) => void }[] = [];
  const module = { exports: {} as any }, require = createRequire(import.meta.url);
  const hooks = {
    useState(value: any) { const i = cursor++; if (!(i in slots)) slots[i] = value; return [slots[i], (v: any) => { writes++; slots[i] = typeof v === 'function' ? v(slots[i]) : v; }]; },
    useRef(value: any) { const i = cursor++; return slots[i] ||= { current: value }; },
    useEffect(fn: any, deps: any[]) { const i = cursor++, old = slots[i]; slots[i] = deps; if (!old || deps.some((d, j) => !Object.is(d, old[j]))) effects.push(fn); },
  };
  const code = ts.transpileModule(readFileSync(new URL('../web/ChildUsageSettings.tsx', import.meta.url), 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, jsx: ts.JsxEmit.ReactJSX, target: ts.ScriptTarget.ES2022 },
  }).outputText;
  vm.runInNewContext(code, { module, exports: module.exports, require: (name: string) => name === 'react' ? hooks
    : name === './api' ? { ApiError, api: (path: string, method?: string, body?: any) => new Promise((resolve, reject) => requests.push({ path, method, body, resolve, reject })) }
    : name === './UsageBudget' ? { usageMinutes: (ms: number) => ms / 60000 }
    : name === './clientId' ? { newClientId: () => 'synthetic-id-' + ++nextId } : require(name),
    setInterval() { return 1; }, clearInterval() {},
  });
  const props = { childId: 'child-a', name: 'Synthetic child', disabled: false };
  const render = () => { cursor = 0; return module.exports.ChildUsageSettings(props); };
  const flush = () => { let tree = render(); while (effects.length) { for (const effect of effects.splice(0)) { const result = effect(); if (result) cleanup.push(result); } tree = render(); } return tree; };
  const nodes = (node: any): any[] => !node || typeof node !== 'object' ? [] : [node, ...[node.props?.children].flat(Infinity).flatMap(nodes)];
  const find = (predicate: (node: any) => boolean) => nodes(flush()).filter(predicate);
  const submit = (index: number) => find(n => n.type === 'form')[index].props.onSubmit({ preventDefault() {} });
  const numeric = (index: number, value: string) => find(n => n.type === 'input' && n.props.type === 'number')[index].props.onChange({ target: { value } });
  const status = { day: '2026-10-28', timezone: 'UTC', minutes: null, usedMs: 0, bonusMs: 0, remainingMs: null, exhausted: false, serverNow: 1, resetAt: 2, leaseUntil: 0, heartbeatMs: 5000, idleMs: 300000 };
  const respond = async (index: number, patch = {}) => { requests[index].resolve({ ...status, ...patch }); await new Promise(resolve => setImmediate(resolve)); return flush(); };
  return { props, requests, flush, find, submit, numeric, respond, writes: () => writes, unmount: () => cleanup.forEach(fn => fn()) };
}

test('parent form captures the selected child, accepts arbitrary/zero/unlimited values and serializes clicks', async () => {
  const h = harness(); h.flush(); await h.respond(0);
  h.find(n => n.type === 'input' && n.props.type === 'checkbox')[0].props.onChange({ target: { checked: false } });
  h.numeric(0, '37'); h.submit(0); h.submit(0);
  assert.equal(h.requests.length, 2); assert.equal(h.requests[1].path, '/parent/usage/child-a');
  assert.equal(h.requests[1].body.minutes, 37); await h.respond(1, { minutes: 37 });
  h.numeric(0, '0'); h.submit(0); assert.equal(h.requests[2].body.minutes, 0); await h.respond(2, { minutes: 0 });
  h.find(n => n.type === 'input' && n.props.type === 'checkbox')[0].props.onChange({ target: { checked: true } });
  h.submit(0); assert.equal(h.requests[3].body.minutes, null); await h.respond(3);
  h.props.disabled = true; h.submit(0); assert.equal(h.requests.length, 4);
  h.unmount();
});

test('lost bonus response retains the same child/day/idempotency key; unmount suppresses stale feedback', async () => {
  const h = harness(); h.flush(); await h.respond(0);
  h.numeric(1, '7'); h.submit(1);
  const original = h.requests[1]; assert.equal(original.body.day, '2026-10-28'); assert.equal(original.body.minutes, 7);
  original.reject(new Error('Synthetic lost response')); await new Promise(resolve => setImmediate(resolve)); h.flush();
  h.submit(1); assert.equal(h.requests.length, 3);
  assert.deepEqual(h.requests[2].body, original.body);
  assert.equal(h.requests[2].path, '/parent/usage/child-a/bonus');
  h.unmount(); const before = h.writes();
  h.requests[2].resolve({ day: '2026-10-28', minutes: 37 }); await new Promise(resolve => setImmediate(resolve));
  assert.equal(h.writes(), before, 'response for unmounted child cannot update another workspace');
});

test('a confirmed retry after midnight acknowledges yesterday without claiming a new bonus today', async () => {
  const h = harness(); h.flush(); await h.respond(0);
  h.numeric(1, '7'); h.submit(1);
  h.requests[1].reject(new Error('Synthetic lost response')); await new Promise(resolve => setImmediate(resolve)); h.flush();
  h.submit(1); await h.respond(2, { day: '2026-10-29', bonusMs: 0 });
  assert.equal(h.requests[2].body.day, '2026-10-28');
  assert.ok(JSON.stringify(h.flush()).includes('לא נוסף זמן להיום'));
  h.unmount();
});
