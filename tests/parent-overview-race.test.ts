import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import ts from 'typescript';

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
  let stale = false, busy = false;
  let data: unknown = null;
  const requests: { path: string; resolve: (value: unknown) => void; reject: (reason: unknown) => void }[] = [];
  const handlers = vm.runInNewContext(code, {
    useRef: (value: unknown) => ({ current: value }),
    useCallback: (fn: unknown) => fn,
    useEffect() {},
    api: (path: string) => new Promise((resolve, reject) => requests.push({ path, resolve, reject })),
    setData: (value: unknown) => { data = value; },
    setChild() {}, familyProgress: () => ({}), setQr() {},
    setOverviewStale: (value: boolean) => { stale = value; },
    setBusy: (value: boolean) => { busy = value; },
    setGroupPhotoRevision() {}, onError() {}, isTransientApiError: () => true,
  }) as { load: (background?: boolean) => Promise<void>; act: (path: string, body?: unknown) => Promise<void> };
  return { handlers, requests, state: () => ({ stale, busy, data }) };
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
