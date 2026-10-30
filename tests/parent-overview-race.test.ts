import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import ts from 'typescript';
import { explainConnection } from '../web/connectionGuidance';

// Exercise the actual Parent load/act implementation with deferred requests.
// This checks asynchronous state ordering, not browser focus or rendering.
function harness() {
  const source = readFileSync(new URL('../web/main.tsx', import.meta.url), 'utf8');
  const start = source.indexOf('  const acting = useRef(false), overviewRequest = useRef(0);');
  const end = source.indexOf('  const selectedChild =', start);
  assert.ok(start >= 0 && end > start, 'Parent load/act source boundaries must exist');
  const code = ts.transpileModule(`(function(){${source.slice(start, end)};return {load,act}})()`, {
    compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS },
  }).outputText;
  let stale = false, busy = false, failed = false, qr: unknown = 'unset';
  let data: unknown = null;
  const requests: { path: string; method: string; body: unknown; resolve: (value: unknown) => void; reject: (reason: unknown) => void }[] = [];
  const handlers = vm.runInNewContext(code, {
    useRef: (value: unknown) => ({ current: value }),
    useCallback: (fn: unknown) => fn,
    useEffect() {},
    api: (path: string, method = 'GET', body?: unknown) => new Promise((resolve, reject) => requests.push({ path, method, body, resolve, reject })),
    setData: (value: unknown) => { data = value; },
    setChild() {}, familyProgress: () => ({}), setQr: (value: unknown) => { qr = value; },
    setOverviewStale: (value: boolean) => { stale = value; },
    setOverviewFailed: (value: boolean) => { failed = value; },
    setBusy: (value: boolean) => { busy = value; },
    setGroupPhotoRevision() {}, onError() {}, isTransientApiError: () => true,
  }) as { load: (background?: boolean) => Promise<void>; act: (path: string, body?: unknown, method?: string) => Promise<boolean> };
  return { handlers, requests, state: () => ({ stale, busy, data, failed, qr }) };
}

for (const lostResponse of [false, true]) {
  test(`${lostResponse ? 'lost-response' : 'successful'} mutation stays stale until the newest overview confirms state`, async () => {
    const h = harness();
    const mutation = h.handlers.act('/parent/grant', { childId: 'synthetic-child', groupId: 'synthetic-group', allowed: true });
    assert.equal(h.state().busy, true);
    // Even before React has rendered a disabled button, duplicate actions are serialized.
    await h.handlers.act('/parent/grant', {});
    assert.equal(h.requests.length, 1);
    if (lostResponse) h.requests[0].reject(new Error('Synthetic lost mutation response'));
    else h.requests[0].resolve({ ok: true });
    await new Promise<void>(resolve => setImmediate(resolve));
    assert.equal(h.state().stale, true);
    assert.equal(h.requests[1].path, '/parent/overview');
    const newestRead = h.handlers.load(true);
    assert.equal(h.requests[2].path, '/parent/overview');
    h.requests[1].resolve({ state: 'connected', children: [], groups: [], grants: [] });
    await mutation;
    assert.equal(h.state().busy, false);
    assert.equal(h.state().stale, true, 'superseded reconciliation must not unlock stale grants');
    assert.equal(h.state().data, null, 'superseded data must not be applied');
    const confirmed = { state: 'connected', children: [], groups: [], grants: [] };
    h.requests[2].resolve(confirmed);
    await newestRead;
    assert.equal(h.state().stale, false);
    assert.equal(h.state().data, confirmed);
  });
}

test('QR read failure after a successful overview is not an overview failure and drops stale QR', async () => {
  const h = harness();
  const first = h.handlers.load(true);
  h.requests[0].resolve({ state: 'qr', children: [], groups: [], grants: [] });
  await new Promise<void>(resolve => setImmediate(resolve));
  assert.equal(h.requests[1].path, '/parent/qr');
  h.requests[1].resolve({ qr: 'data:old' });
  await first;
  assert.equal(h.state().qr, 'data:old');
  const second = h.handlers.load(true);
  h.requests[2].resolve({ state: 'qr', children: [], groups: [], grants: [] });
  await new Promise<void>(resolve => setImmediate(resolve));
  h.requests[3].reject(new Error('Synthetic QR failure'));
  await second;
  assert.equal(h.state().failed, false, 'overview succeeded, so the server-no-answer card must not show');
  assert.equal(h.state().qr, null, 'old QR must not stay visible');
  assert.equal(h.state().stale, false);
});

for (const lostResponse of [false, true]) {
  test(`child pause/resume reconciles with QR failure after a ${lostResponse ? 'lost' : 'successful'} mutation response`, async () => {
    const h = harness();
    const pause = { childId: 'synthetic-child', paused: true, revision: 0 };
    const saving = h.handlers.act('/parent/child-access', pause, 'PUT');
    assert.equal(h.requests[0].path, '/parent/child-access');
    assert.equal(h.requests[0].method, 'PUT');
    assert.equal(h.requests[0].body, pause);
    if (lostResponse) h.requests[0].reject(new Error('Synthetic lost pause response'));
    else h.requests[0].resolve({ ok: true });
    await new Promise<void>(resolve => setImmediate(resolve));
    const paused = { state: 'qr', children: [{ id: pause.childId, name: 'Synthetic child', paused: true, accessRevision: 1 }], groups: [], grants: [] };
    h.requests[1].resolve(paused);
    await new Promise<void>(resolve => setImmediate(resolve));
    assert.equal(h.requests[2].path, '/parent/qr');
    h.requests[2].reject(new Error('Synthetic QR unavailable'));
    assert.equal(await saving, !lostResponse);
    assert.equal(h.state().stale, false);
    assert.equal(h.state().failed, false);
    assert.equal(h.state().data, paused);
    assert.equal(h.state().qr, null);
    assert.equal(explainConnection({ overviewFailed: h.state().failed, browserOnline: true, state: paused.state, qrVisible: !!h.state().qr }).code, 'qr_pending');

    const resume = { childId: pause.childId, paused: false, revision: 1 };
    const resuming = h.handlers.act('/parent/child-access', resume, 'PUT');
    assert.equal(h.requests[3].body, resume);
    h.requests[3].resolve({ ok: true });
    await new Promise<void>(resolve => setImmediate(resolve));
    const resumed = { ...paused, state: 'connected', children: [{ ...paused.children[0], paused: false, accessRevision: 2 }] };
    h.requests[4].resolve(resumed);
    assert.equal(await resuming, true);
    assert.equal(h.state().data, resumed);
    assert.equal(h.state().stale, false);
    assert.equal(h.state().failed, false);
    assert.equal(h.requests.length, 5, 'connected refresh does not request a QR or trigger a relink');
  });
}

test('obsolete QR failure cannot replace a newer resumed-child overview or its connection state', async () => {
  const h = harness();
  const first = h.handlers.load(true);
  h.requests[0].resolve({ state: 'qr', children: [], groups: [], grants: [] });
  await new Promise<void>(resolve => setImmediate(resolve));
  const newest = h.handlers.load(true);
  const resumed = { state: 'connected', children: [{ id: 'synthetic-child', paused: false, accessRevision: 2 }], groups: [], grants: [] };
  h.requests[2].resolve(resumed); await newest;
  h.requests[1].reject(new Error('Synthetic obsolete QR read')); await first;
  assert.equal(h.state().data, resumed);
  assert.equal(h.state().qr, null);
  assert.equal(h.state().failed, false);
  assert.equal(h.state().stale, false);
});
