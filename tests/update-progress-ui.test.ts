import { test } from 'node:test';
import assert from 'node:assert/strict';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { UpdateProgressStatus, RejectedUpdateRequest, submitUpdateRequest, readUpdateStatus, UpdateAccessError } from '../web/UpdatesPanel.js';
import { createUpdatePoller, observesRequest, reconcileUpdateRequest, updateActionsDisabled, updatePhases, updatePollDelay,
  updateProgress, type ConfiguredUpdate, type LocalUpdateRequest } from '../web/updateProgress.js';
import { setCsrf } from '../web/api.js';

const before = '11111111-1111-4111-8111-111111111111';
const requestId = '22222222-2222-4222-8222-222222222222';
const base: ConfiguredUpdate = {
  configured: true, enabled: true, mode: 'notify', modeControlAllowed: true, phase: 'available',
  currentVersion: '0.3.2', latestVersion: '0.4.0', updatedAt: 1000, checkedAt: 1000,
  error: null, stale: false, queuedMode: null, queuedRequest: null,
};
const local: LocalUpdateRequest = { action: 'install', state: 'queued', previousRequestId: before };
const journal = (state: 'running' | 'succeeded' | 'failed', id = requestId) => ({
  id, action: 'install' as const, state, startedAt: 2000, finishedAt: state === 'running' ? null : 3000,
  error: state === 'failed' ? 'health' as const : null,
});

test('queued install is not represented as started, and old success cannot acknowledge a new request', () => {
  const old = { ...base, phase: 'healthy' as const, lastRequest: journal('succeeded', before) };
  assert.equal(observesRequest(old, local), false);
  const waiting = updateProgress(old, local);
  assert.equal(waiting.kind, 'queued'); assert.equal(waiting.active, true);
  assert.match(waiting.detail, /לא אישור שההתקנה התחילה/);
  assert.doesNotMatch(waiting.title, /הושלם/);
  const queued: ConfiguredUpdate = { ...old, queuedRequest: { id: requestId, action: 'install', createdAt: 2000 } };
  const result = updateProgress(queued, local);
  assert.equal(result.kind, 'queued'); assert.match(result.title, /בתור/);
  assert.match(result.action!, /התקנת/);
  assert.equal(observesRequest(queued, local), false, 'keep tracking if an older worker removes the queue without journalling');
  assert.equal(updateActionsDisabled(queued, null, false, false), true);
});

test('each worker stage is reported as real running work, never as an invented percentage', () => {
  for (const phase of ['checking', 'downloading', 'verifying', 'building', 'stopping', 'backing_up', 'activating', 'checking_health', 'rolling_back'] as const) {
    const data = { ...base, phase, lastRequest: journal('running') };
    assert.equal(observesRequest(data, local), true);
    const progress = updateProgress(data);
    assert.equal(progress.kind, 'running'); assert.equal(progress.active, true);
    assert.equal(progress.title, updatePhases[phase]);
    assert.equal(updateActionsDisabled(data, null, false, false), true);
    const html = renderToStaticMarkup(React.createElement(UpdateProgressStatus, { data }));
    assert.match(html, /aria-live="polite"/);
    assert.ok(html.includes(updatePhases[phase]));
    assert.doesNotMatch(html, /\d+%|aria-valuenow|role="progressbar"/);
  }
});

test('terminal results distinguish check-only availability, verified installation, and rollback failure', () => {
  const available = { ...base, lastRequest: { ...journal('succeeded'), action: 'check' as const } };
  assert.equal(updateProgress(available).kind, 'success');
  assert.match(updateProgress(available).title, /נמצאה גרסה חדשה/);
  assert.match(updateProgress(available).detail, /לא התקבלה הודעה שהותקנה/);
  const healthy = { ...base, phase: 'healthy' as const, lastRequest: journal('succeeded') };
  assert.match(updateProgress(healthy).title, /הושלם בהצלחה/);
  assert.equal(updateActionsDisabled(healthy, null, false, false), false);
  for (const phase of ['failed', 'rolled_back', 'recovery_required', 'rollback_pending_health'] as const) {
    const failed = { ...base, phase, error: 'health' as const, lastRequest: journal('failed') };
    assert.equal(updateProgress(failed).kind, 'failure'); assert.equal(updateProgress(failed).active, false);
    assert.doesNotMatch(updateProgress(failed).title, /הושלם בהצלחה/);
  }
  assert.equal(updateProgress({ ...healthy, phase: 'failed' }).kind, 'failure', 'a later scheduled failure must not reuse old request success');
  assert.equal(updateProgress({ ...base, phase: 'idle', lastRequest: journal('failed') }).title, 'הפעולה לא הושלמה');
  const later = { ...base, phase: 'healthy' as const, lastRequest: journal('failed'), checkedAt: 4000, updatedAt: 5000 };
  assert.equal(updateProgress(later).kind, 'success', 'a later scheduled success must not reuse old request failure');
  assert.equal(updateProgress({ ...later, phase: 'available' }).kind, 'idle');
  const expired = { ...base, lastRequest: { ...journal('failed'), error: 'expired' as const } };
  assert.match(updateProgress(expired).title, /פג לפני הטיפול/);
  assert.match(updateProgress(expired).detail, /לא בוצעה עבורה התקנה/);
  assert.equal(updateActionsDisabled(expired, null, false, false), false, 'retry requires an explicit new action');
});

test('queue and journal publication race retains identity through fast completion and older-worker uncertainty', () => {
  const publishing: ConfiguredUpdate = { ...base, lastRequest: journal('running'),
    queuedRequest: { id: requestId, action: 'install', createdAt: 2000 } };
  const tracked = reconcileUpdateRequest(publishing, null)!;
  assert.equal(tracked.queuedId, requestId); assert.equal(tracked.previousRequestId, requestId);
  const complete = { ...base, phase: 'healthy' as const, lastRequest: journal('succeeded') };
  assert.equal(reconcileUpdateRequest(complete, tracked), null, 'same journal ID now belongs to the queue we observed');
  const oldWorker = reconcileUpdateRequest(base, { ...local, queuedId: requestId })!;
  assert.equal(oldWorker.state, 'uncertain'); assert.equal(updateProgress(base, oldWorker).kind, 'unknown');
  assert.equal(updateActionsDisabled(base, oldWorker, false, false), true);
});

test('unrelated automatic phases never acknowledge an in-flight or uncertain local request', () => {
  const unchanged: ConfiguredUpdate = { ...base, lastRequest: journal('succeeded', before),
    lastModeRequest: { id: before, mode: 'auto', state: 'applied', error: null } };
  for (const action of ['install', 'set_mode'] as const) {
    for (const state of ['sending', 'queued', 'uncertain'] as const) {
      const request: LocalUpdateRequest = { action, mode: action === 'set_mode' ? 'auto' : undefined,
        state, previousRequestId: before, previousModeId: before };
      for (const phase of ['building', 'checking_health', 'healthy', 'failed'] as const) {
        const automatic = { ...unchanged, phase, checkedAt: 4000, updatedAt: 5000 };
        assert.equal(observesRequest(automatic, request), false);
        const tracked = reconcileUpdateRequest(automatic, request);
        assert.equal(tracked, request, `${action}/${state} must survive unrelated ${phase}`);
        const progress = updateProgress(automatic, tracked);
        assert.equal(progress.active, true);
        assert.ok(progress.kind === 'queued' || progress.kind === 'unknown');
        assert.doesNotMatch(progress.title, /הושלם בהצלחה|מכינים ובודקים/);
      }
      assert.equal(observesRequest({ ...unchanged, phase: 'building', lastRequest: undefined, lastModeRequest: undefined }, request), false);
    }
  }
});

test('acknowledgement requires the relevant action and exact ID after observing a queue', () => {
  const install = { ...local, state: 'uncertain' as const, queuedId: requestId };
  assert.equal(observesRequest({ ...base, lastRequest: journal('succeeded', before) }, install), false);
  assert.equal(observesRequest({ ...base, lastRequest: { ...journal('succeeded'), action: 'check' } }, install), false);
  assert.equal(observesRequest({ ...base, lastRequest: journal('succeeded') }, install), true);
  const mode: LocalUpdateRequest = { action: 'set_mode', mode: 'auto', state: 'uncertain', previousModeId: before };
  assert.equal(observesRequest({ ...base, lastRequest: journal('running'), phase: 'building' }, mode), false);
  assert.equal(observesRequest({ ...base, lastModeRequest: { id: requestId, mode: 'notify', state: 'applied', error: null } }, mode), false);
  assert.equal(observesRequest({ ...base, lastModeRequest: { id: requestId, mode: 'auto', state: 'applied', error: null } }, mode), true);
});

test('reconnect preserves the last worker stage and disables repeat actions without claiming update failure', () => {
  const data = { ...base, phase: 'checking_health' as const, lastRequest: journal('running') };
  const html = renderToStaticMarkup(React.createElement(UpdateProgressStatus, { data, reconnecting: true }));
  assert.ok(html.includes(updatePhases.checking_health));
  assert.match(html, /לא תוצאת כשל/); assert.match(html, /ננסה שוב אוטומטית/);
  assert.equal(updateActionsDisabled(data, null, true, false), true);
  assert.equal(updateProgress(base, { ...local, state: 'uncertain' }).kind, 'unknown');
  assert.equal(updateActionsDisabled(base, { ...local, state: 'uncertain' }, false, false), true);
  assert.equal(observesRequest({ ...base, lastRequest: journal('succeeded') }, local), true);
});

test('mode requests preserve applied mode and legacy missing acknowledgements never invent completion', () => {
  const data: ConfiguredUpdate = { ...base, queuedMode: { id: requestId, mode: 'auto' },
    lastModeRequest: { id: requestId, mode: 'auto', state: 'pending', error: null } };
  assert.equal(data.mode, 'notify');
  assert.equal(updateProgress(data).active, true);
  assert.match(updateProgress(data).detail, /רק לאחר אישור/);
  assert.equal(updateActionsDisabled(data, null, false, false), true);
  const oldWorker = { ...base, lastRequest: undefined, queuedRequest: null };
  assert.equal(observesRequest(oldWorker, local), false);
  assert.equal(updateProgress(oldWorker, local).kind, 'queued');
  assert.doesNotMatch(updateProgress(oldWorker, local).title, /הושלם|מתקינים/);
});

function timers() {
  let next = 0;
  const tasks = new Map<number, { fn: () => void; delay: number }>();
  const schedule = ((fn: () => void, delay: number) => { const id = ++next; tasks.set(id, { fn, delay }); return id; }) as unknown as typeof setTimeout;
  const cancel = ((id: number) => { tasks.delete(id); }) as unknown as typeof clearTimeout;
  return { schedule, cancel, tasks,
    nextDelay() { return Math.min(...[...tasks.values()].map(x => x.delay)); },
    runNext() { const [id, task] = [...tasks].sort((a, b) => a[1].delay - b[1].delay)[0]; tasks.delete(id); task.fn(); },
  };
}
const flush = () => new Promise<void>(resolve => setImmediate(resolve));

test('polling serializes refreshes, backs off disconnected reads, then resumes and stops cleanly', async () => {
  const clock = timers(), seen: number[] = [], pending: { resolve: (x: number) => void; reject: () => void; signal: AbortSignal }[] = [];
  let errors = 0, active = true;
  const poller = createUpdatePoller({ ...clock, active: () => active,
    read: signal => new Promise<number>((resolve, reject) => pending.push({ resolve, reject: () => reject(new Error('offline')), signal })),
    onValue: x => seen.push(x), onError: () => { errors++; },
  });
  poller.refresh(); poller.refresh(); poller.refresh();
  assert.equal(pending.length, 1, 'repeated refresh cannot overlap requests');
  pending[0].resolve(1); await flush(); assert.deepEqual(seen, [1]); assert.equal(clock.nextDelay(), 0);
  clock.runNext(); pending[1].reject(); await flush(); assert.equal(clock.nextDelay(), 3000);
  clock.runNext(); pending[2].reject(); await flush(); assert.equal(clock.nextDelay(), 6000);
  clock.runNext(); pending[3].reject(); await flush(); assert.equal(clock.nextDelay(), 12000);
  clock.runNext(); active = false; pending[4].resolve(5); await flush();
  assert.equal(clock.nextDelay(), 15000); assert.equal(errors, 3); assert.deepEqual(seen, [1, 5]);
  clock.runNext(); const late = pending[5]; poller.stop();
  assert.equal(late.signal.aborted, true);
  late.resolve(6); await flush(); assert.deepEqual(seen, [1, 5]); assert.equal(clock.tasks.size, 0);
  poller.refresh(); assert.equal(pending.length, 6);
  assert.equal(updatePollDelay(100, true), 30000); assert.equal(updatePollDelay(0, true), 3000);
});

test('hung status reads time out and reconnect without publishing a fabricated terminal outcome', async () => {
  const clock = timers(); let errors = 0, values = 0;
  const poller = createUpdatePoller({ ...clock, active: () => true,
    read: signal => new Promise<void>((_resolve, reject) => signal.addEventListener('abort', () => reject(new Error('abort')), { once: true })),
    onValue: () => { values++; }, onError: () => { errors++; },
  });
  poller.refresh(); assert.equal(clock.nextDelay(), 15000); clock.runNext(); await flush();
  assert.equal(errors, 1); assert.equal(values, 0); assert.equal(clock.nextDelay(), 3000); poller.stop();
  assert.equal(clock.tasks.size, 0);
});

test('mutation transport sends one CSRF-protected request and separates rejection from uncertain disconnect', async () => {
  const original = globalThis.fetch; let calls = 0;
  setCsrf('synthetic-csrf');
  try {
    globalThis.fetch = (async (_url, init) => {
      calls++; assert.equal(init?.method, 'POST'); assert.equal(init?.credentials, 'same-origin');
      assert.equal((init?.headers as Record<string, string>)['X-CSRF-Token'], 'synthetic-csrf');
      assert.deepEqual(JSON.parse(init?.body as string), { action: 'install' });
      return new Response(JSON.stringify({ state: 'queued' }));
    }) as typeof fetch;
    assert.equal((await submitUpdateRequest('install')).state, 'queued'); assert.equal(calls, 1);
    globalThis.fetch = (async () => new Response('{}', { status: 409 })) as typeof fetch;
    await assert.rejects(submitUpdateRequest('install'), RejectedUpdateRequest);
    for (const fetcher of [async () => new Response('{}', { status: 503 }), async () => { throw new TypeError('Network lost'); }]) {
      globalThis.fetch = fetcher as typeof fetch;
      await assert.rejects(submitUpdateRequest('install'), e => e instanceof Error && !(e instanceof RejectedUpdateRequest));
    }
  } finally { globalThis.fetch = original; setCsrf(''); }
});

test('status transport distinguishes 401/403 access loss from retryable 503 and network loss', async () => {
  const original = globalThis.fetch, windowDescriptor = Object.getOwnPropertyDescriptor(globalThis, 'window');
  const events: string[] = [];
  Object.defineProperty(globalThis, 'window', { configurable: true, value: {
    dispatchEvent(event: Event) { events.push(event.type); return true; },
  } });
  try {
    for (const status of [401, 403]) {
      globalThis.fetch = (async () => new Response('{}', { status })) as typeof fetch;
      await assert.rejects(readUpdateStatus(new AbortController().signal), e => e instanceof UpdateAccessError && e.status === status);
    }
    assert.deepEqual(events, ['session-expired'], '401 still expires the session; 403 is explicit permission loss');
    for (const fetcher of [async () => new Response('{}', { status: 503 }), async () => { throw new TypeError('Network lost'); }]) {
      globalThis.fetch = fetcher as typeof fetch;
      await assert.rejects(readUpdateStatus(new AbortController().signal), e => e instanceof Error && !(e instanceof UpdateAccessError));
    }
  } finally {
    globalThis.fetch = original;
    if (windowDescriptor) Object.defineProperty(globalThis, 'window', windowDescriptor);
    else Reflect.deleteProperty(globalThis, 'window');
  }
});
