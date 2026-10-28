import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import ts from 'typescript';

// Execute the actual hook with synthetic React/browser clocks and transport.
// This checks lifecycle behavior, not physical screen power or visual layout.
function harness() {
  const source = readFileSync(new URL('../web/useUsageBudget.ts', import.meta.url), 'utf8');
  const code = ts.transpileModule(source.slice(source.indexOf('export function useUsageBudget')).replace('export function', 'function') + ';useUsageBudget;',
    { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS } }).outputText;
  let now = 0, cursor = 0, visible = true, focused = true, online = true;
  const slots: any[] = [], effects: (() => (() => void) | void)[] = [], cleanup: (() => void)[] = [];
  const listeners = new Map<string, Set<() => void>>(), timers = new Map<number, () => void>();
  const requests: { body: any; resolve: (value: any) => void; reject: (e: Error) => void }[] = [], unload: any[] = [];
  const events = {
    addEventListener: (name: string, fn: () => void) => { if (!listeners.has(name)) listeners.set(name, new Set()); listeners.get(name)!.add(fn); },
    removeEventListener: (name: string, fn: () => void) => listeners.get(name)?.delete(fn),
  };
  const hook = vm.runInNewContext(code, {
    useState(value: any) { const i = cursor++; if (!(i in slots)) slots[i] = value; return [slots[i], (v: any) => { slots[i] = v; }]; },
    useRef(value: any) { const i = cursor++; return slots[i] ||= { current: value }; },
    useCallback: (fn: any) => fn,
    useEffect(fn: any, deps: any[]) { const i = cursor++, old = slots[i]; slots[i] = deps; if (!old || deps.some((d, j) => !Object.is(d, old[j]))) effects.push(fn); },
    window: events, document: { ...events, get visibilityState() { return visible ? 'visible' : 'hidden'; }, hasFocus: () => focused },
    navigator: { get onLine() { return online; } }, performance: { now: () => now }, newClientId: () => 'synthetic-tab', csrf: 'synthetic-csrf',
    setInterval: (fn: () => void, ms: number) => { timers.set(ms, fn); return ms; }, clearInterval: (id: number) => timers.delete(id),
    api: (_path: string, _method: string, body: any) => new Promise((resolve, reject) => requests.push({ body, resolve, reject })),
    fetch: (_path: string, options: any) => { unload.push(JSON.parse(options.body)); return Promise.resolve(); },
  });
  const render = () => { cursor = 0; return hook(true, 'synthetic-child'); };
  const flush = () => { let state = render(); while (effects.length) { for (const effect of effects.splice(0)) { const result = effect(); if (result) cleanup.push(result); } state = render(); } return state; };
  const emit = (name: string) => { for (const fn of listeners.get(name) || []) fn(); return flush(); };
  const respond = async (index: number, patch: Record<string, unknown> = {}) => {
    requests[index].resolve({ day: '2026-10-28', timezone: 'UTC', minutes: 37, usedMs: 0, bonusMs: 0, remainingMs: 2220000,
      exhausted: false, serverNow: 1000000 + now, resetAt: 10000000, leaseUntil: 1000000 + now + 15000, heartbeatMs: 5000, idleMs: 300000, ...patch });
    await Promise.resolve(); await Promise.resolve(); return flush();
  };
  return { flush, emit, requests, unload, respond,
    visibility: (value: boolean) => { visible = value; return emit('visibilitychange'); },
    focus: (value: boolean) => { focused = value; return emit(value ? 'focus' : 'blur'); },
    online: (value: boolean) => { online = value; return emit(value ? 'online' : 'offline'); },
    tick: (ms: number, timer = 5000) => { now += ms; timers.get(timer)?.(); return flush(); },
    close: () => { cleanup.forEach(fn => fn()); },
  };
}

test('content is hidden until server reconciliation; focus/visibility pause immediately and old responses cannot reveal chats', async () => {
  const h = harness(); assert.equal(h.flush().suspended, true);
  assert.equal(h.requests[0].body.active, true);
  h.visibility(false); assert.equal(h.requests[1].body.active, false);
  await h.respond(0); assert.equal(h.flush().suspended, true);
  await h.respond(1, { leaseUntil: 0 }); assert.equal(h.flush().suspended, true);
  h.visibility(true); assert.equal(h.flush().suspended, true);
  await h.respond(2); assert.equal(h.flush().suspended, false);
  assert.equal(h.focus(false).suspended, true);
  assert.equal(h.requests.at(-1)!.body.active, false);
  h.close();
});

test('reading counts without constant input; five-minute idle requires explicit resume and never sends content', async () => {
  const h = harness(); h.flush(); await h.respond(0);
  h.tick(240000); assert.equal(h.requests.at(-1)!.body.active, true);
  await h.respond(1);
  h.tick(60001); assert.equal(h.requests.at(-1)!.body.active, false);
  await h.respond(2, { leaseUntil: 0 }); assert.equal(h.flush().reason, 'idle');
  h.emit('pointerdown'); h.tick(5000); assert.equal(h.requests.at(-1)!.body.active, false);
  h.flush().resume(); assert.equal(h.requests.at(-1)!.body.active, true); assert.equal(h.flush().suspended, true);
  await h.respond(h.requests.length - 1); assert.equal(h.flush().suspended, false);
  h.close();
});

test('offline and expired leases mask cached content; reconnect alone cannot unlock; exhausted sessions can receive extensions', async () => {
  const h = harness(); h.flush(); await h.respond(0);
  assert.equal(h.online(false).suspended, true);
  assert.equal(h.online(true).suspended, true);
  await h.respond(h.requests.length - 1); assert.equal(h.flush().suspended, false);
  h.tick(15001, 250); assert.equal(h.flush().suspended, true);
  await h.respond(h.requests.length - 1, { remainingMs: 0, exhausted: true, leaseUntil: 0 });
  assert.equal(h.flush().reason, 'exhausted');
  h.flush().reconcile(); await h.respond(h.requests.length - 1, { remainingMs: 60000 });
  assert.equal(h.flush().suspended, false);
  h.close();
});

test('network error fails closed and pagehide uses higher-sequence best-effort pause', async () => {
  const h = harness(); h.flush(); await h.respond(0);
  h.tick(5000); h.requests.at(-1)!.reject(new Error('synthetic offline'));
  await Promise.resolve(); await Promise.resolve(); assert.equal(h.flush().reason, 'offline');
  h.emit('pagehide'); assert.equal(h.unload[0].active, false);
  assert.ok(h.unload[0].seq > h.requests.at(-1)!.body.seq);
  h.close();
});

test('a legacy limited session awaiting timezone choice shows parent setup rather than logging out', async () => {
  const h = harness(); h.flush();
  await h.respond(0, { timezoneConfirmed: false, minutes: 37, leaseUntil: 0 });
  assert.equal(h.flush().reason, 'setup'); assert.equal(h.flush().suspended, true);
  h.flush().reconcile(); await h.respond(1, { timezoneConfirmed: true, minutes: 37 });
  assert.equal(h.flush().suspended, false);
  h.close();
});
