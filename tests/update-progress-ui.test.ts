import { test } from 'node:test';
import assert from 'node:assert/strict';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { UpdateProgressStatus, RejectedUpdateRequest, submitUpdateRequest, readUpdateStatus, UpdateAccessError } from '../web/UpdatesPanel.js';
import { availableUpdateTarget, sameUpdateTarget, createUpdatePoller, observesRequest, reconcileUpdateRequest, updateActionsDisabled, updatePhases, updatePollDelay,
  updateProgress, type ConfiguredUpdate, type LocalUpdateRequest } from '../web/updateProgress.js';
import { setCsrf } from '../web/api.js';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import vm from 'node:vm';
import ts from 'typescript';
import * as progressModule from '../web/updateProgress.js';

const before = '11111111-1111-4111-8111-111111111111';
const requestId = '22222222-2222-4222-8222-222222222222';
const target = { version: '0.4.0', fingerprint: 'a'.repeat(64) };
const nextTarget = { version: '0.5.0', fingerprint: 'b'.repeat(64) };
const base: ConfiguredUpdate = {
  configured: true, enabled: true, mode: 'notify', modeControlAllowed: true, phase: 'available',
  currentVersion: '0.3.2', latestVersion: '0.4.0', updatedAt: 1000, checkedAt: 1000,
  error: null, stale: false, queuedMode: null, queuedRequest: null, installTarget: target,
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
  assert.match(html, /המצב העדכני אינו זמין/); assert.match(html, /אין כרגע אישור להתקדמות, להצלחה או לכשל חדש/);
  assert.match(html, /לא נשלח שוב בקשת עדכון/);
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
      assert.deepEqual(JSON.parse(init?.body as string), { action: 'install', target });
      return new Response(JSON.stringify({ state: 'queued' }));
    }) as typeof fetch;
    assert.equal((await submitUpdateRequest('install', undefined, target)).state, 'queued'); assert.equal(calls, 1);
    globalThis.fetch = (async () => new Response('{}', { status: 409 })) as typeof fetch;
    await assert.rejects(submitUpdateRequest('install', undefined, target), RejectedUpdateRequest);
    for (const fetcher of [async () => new Response('{}', { status: 503 }), async () => { throw new TypeError('Network lost'); }]) {
      globalThis.fetch = fetcher as typeof fetch;
      await assert.rejects(submitUpdateRequest('install', undefined, target), e => e instanceof Error && !(e instanceof RejectedUpdateRequest));
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


test('repair-required states override stale running journals and queues and never offer a blind retry', () => {
  for (const phase of ['recovery_required', 'rollback_pending_health'] as const) {
    for (const state of ['running', 'succeeded', 'failed'] as const) {
      const data = { ...base, phase, lastRequest: journal(state),
        queuedRequest: { id: requestId, action: 'install' as const, createdAt: 2000 } };
      const progress = updateProgress(data, local);
      assert.equal(progress.kind, 'failure'); assert.equal(progress.active, false);
      assert.equal(progress.title, updatePhases[phase]);
      assert.equal(updateActionsDisabled(data, null, false, false), true);
      assert.match(progress.detail, /נדרש|נדרשת/);
      assert.doesNotMatch(progress.detail, /המידע נשמר בבטחה|חזרנו לגרסה הקודמת|נסו שוב/);
    }
  }
  const recovery = updateProgress({ ...base, phase: 'recovery_required' });
  assert.match(recovery.detail, /לא ניתן להמשיך אוטומטית בבטחה/);
  assert.match(recovery.detail, /לא מתחיל עדכון נוסף/);
});

test('reopening uses reported worker state, while stale success and unknown phases are not current success', () => {
  const running = { ...base, phase: 'building' as const, lastRequest: journal('running') };
  const initial = updateProgress(running);
  const reopened = updateProgress(structuredClone(running));
  assert.deepEqual(reopened, initial);
  assert.match(reopened.detail, /גם אם סוגרים את הדפדפן/);
  assert.match(reopened.detail, /בפתיחה מחדש בודקים את המצב מהשירות/);
  const stale = { ...base, phase: 'healthy' as const, stale: true, lastRequest: journal('succeeded') };
  for (const data of [stale, { ...stale, stale: false }]) {
    const html = renderToStaticMarkup(React.createElement(UpdateProgressStatus, { data, reconnecting: true }));
    assert.match(html, /המצב העדכני אינו זמין<\/b>/);
    assert.ok(html.includes('הדיווח האחרון שהתקבל: העדכון הושלם בהצלחה'));
    assert.ok(!html.includes('שירות העדכון אישר שהגרסה החדשה פעילה'));
  }
  const unknown = { ...base, phase: 'future_unknown', lastRequest: journal('succeeded') } as unknown as ConfiguredUpdate;
  assert.equal(updateProgress(unknown).kind, 'unknown');
  assert.equal(updateActionsDisabled(unknown, null, false, false), true);
  assert.doesNotMatch(updateProgress(unknown).title, /הושלם|חזרנו/);
});

test('unrecognized or malformed status cannot replace the last real report with invented progress', async () => {
  const original = globalThis.fetch;
  try {
    for (const payload of [null, {}, { configured: 'yes' }, { ...base, phase: 'unknown_future' }]) {
      globalThis.fetch = (async () => new Response(JSON.stringify(payload))) as typeof fetch;
      await assert.rejects(readUpdateStatus(new AbortController().signal), /Status unavailable/);
    }
  } finally { globalThis.fetch = original; }
});


// Drive the actual component tree/handlers with deterministic hooks. Transport,
// poll cleanup and retry behavior are covered above; this is not a browser test.
function panelHarness(data: ConfiguredUpdate | null, reconnecting = false, transport?: (init?: RequestInit) => Promise<Response>) {
  const slots: any[] = [true, data, true, false, false, '', '', reconnecting, null, false, null, null];
  let cursor = 0;
  const calls: string[] = [], bodies: any[] = [];
  const module = { exports: {} as any }, require = createRequire(import.meta.url);
  const code = ts.transpileModule(readFileSync(new URL('../web/UpdatesPanel.tsx', import.meta.url), 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, jsx: ts.JsxEmit.ReactJSX, target: ts.ScriptTarget.ES2022 },
  }).outputText;
  vm.runInNewContext(code, {
    module, exports: module.exports, AbortController, setTimeout, clearTimeout,
    fetch: async (_url: string, init?: RequestInit) => { calls.push(init?.method || 'GET'); if (init?.body) bodies.push(JSON.parse(init.body as string)); if (transport) return transport(init); throw new Error('unexpected transport'); },
    require: (name: string) => name === 'react' ? {
      useState(initial: any) { const index = cursor++; if (!(index in slots)) slots[index] = initial; return [slots[index], (value: any) => { slots[index] = typeof value === 'function' ? value(slots[index]) : value; }]; },
      useRef(initial: any) { const index = cursor++; return slots[index] ||= { current: initial }; },
      useEffect() {},
    } : name === './api' ? { csrf: 'synthetic' } : name === './updateProgress' ? progressModule : require(name),
  });
  const render = () => { cursor = 0; return module.exports.UpdatesPanel(); };
  const nodes = (node: any): any[] => !node || typeof node !== 'object' ? [] : [node, ...[node.props?.children].flat(Infinity).flatMap(nodes)];
  const buttons = () => nodes(render()).filter(node => node.type === 'button');
  return { calls, bodies, html: () => renderToStaticMarkup(render()), buttons,
    status: (next: ConfiguredUpdate) => { slots[1] = next; if (slots[14]) slots[14].current = next; slots[11] = reconcileUpdateRequest(next, slots[15]?.current ?? slots[11]); if (slots[15]) slots[15].current = slots[11]; },
    unmount: () => { if (slots[12]) slots[12].current = false; },
    button: (label: RegExp) => buttons().find(node => label.test(renderToStaticMarkup(node)))!,
    toggle: () => buttons().find(node => node.props.className === 'diagnostics-toggle')!.props.onClick(),
    refresh: () => buttons().find(node => node.props.className === 'text-button')!.props.onClick(),
  };
}

test('actual panel hides recovery mutations including already-open confirmations and preserves repair state on reopen', () => {
  for (const phase of ['recovery_required', 'rollback_pending_health'] as const) {
    const h = panelHarness(base);
    h.button(/התקנת גרסה/).props.onClick();
    h.status({ ...base, phase, lastRequest: journal('failed') });
    assert.equal(h.buttons().length, 2, 'only disclosure and read-only refresh remain');
    assert.doesNotMatch(h.html(), /כן, התקנה עכשיו|אישור שינוי מצב העדכונים|בדיקה ועדכון אוטומטי/);
    assert.ok(h.html().includes(updatePhases[phase]));
    h.refresh(); h.refresh();
    h.toggle(); assert.equal(h.buttons().length, 1, 'closing removes the body but not its actual status');
    h.toggle(); assert.ok(h.html().includes(updatePhases[phase]));
    assert.equal(h.buttons().length, 2);
    assert.deepEqual(h.calls, [], 'disclosure and refresh cannot post an update or replay a prior confirmation');
  }
});

test('actual panel treats an unavailable current status as unknown even when last report was healthy', () => {
  const h = panelHarness({ ...base, phase: 'healthy', lastRequest: journal('succeeded') }, true);
  assert.ok(h.html().includes('המצב העדכני אינו זמין'));
  assert.ok(h.html().includes('הדיווח האחרון שהתקבל: העדכון הושלם בהצלחה'));
  for (const button of h.buttons().filter(node => node.props.className === 'outline-button'))
    assert.equal(button.props.disabled, true, 'no mutating action while disconnected');
  h.toggle(); h.toggle();
  assert.ok(h.html().includes('המצב העדכני אינו זמין'));
  assert.deepEqual(h.calls, []);
});

test('fresh panel without an app status reports unavailable rather than claiming a reachable recovery console', () => {
  const h = panelHarness(null, true);
  assert.match(h.html(), /מצב עדכני אינו זמין/);
  assert.doesNotMatch(h.html(), /העדכון הושלם בהצלחה|כן, התקנת גרסה|בדיקת גרסה חדשה|חזרנו לגרסה הקודמת/);
  assert.equal(h.buttons().filter(node => node.props.className === 'outline-button').length, 0);
  h.toggle(); h.toggle(); h.refresh();
  assert.deepEqual(h.calls, [], 'a UI loaded before an outage must not invent recovery or publish mutations');
});

test('only a valid available root target enables install, including older-root and failed/recovery status', () => {
  assert.deepEqual(availableUpdateTarget(base), target);
  assert.notEqual(availableUpdateTarget(base), target, 'confirmation receives a snapshot, not mutable status');
  for (const data of [
    { ...base, installTarget: undefined }, { ...base, installTarget: null },
    { ...base, installTarget: nextTarget }, { ...base, installTarget: { ...target, fingerprint: 'bad' } },
    ...(['failed', 'rolled_back', 'recovery_required', 'rollback_pending_health', 'healthy', 'checking'] as const).map(phase => ({ ...base, phase })),
  ]) assert.equal(availableUpdateTarget(data), null);
  assert.equal(sameUpdateTarget(target, { ...target }), true);
  assert.equal(sameUpdateTarget(target, { ...target, fingerprint: 'b'.repeat(64) }), false);
  const old = panelHarness({ ...base, installTarget: undefined });
  assert.match(old.html(), /אין עדיין יעד התקנה מאושר/);
  assert.equal(old.button(/התקנת גרסה/).props.disabled, true);
  assert.equal(old.button(/בדיקת גרסה חדשה/).props.disabled, false, 'check remains available to discover a supported target');
  old.button(/התקנת גרסה/).props.onClick();
  assert.doesNotMatch(old.html(), /אישור התקנת גרסה/);
  assert.deepEqual(old.calls, []);
});

test('install transport refuses an absent target and explains a definitive stale-target rejection without retrying', async () => {
  const original = globalThis.fetch; let calls = 0;
  try {
    globalThis.fetch = (async () => { calls++; return new Response(JSON.stringify({ error: 'UPDATES_TARGET_CHANGED' }), { status: 409 }); }) as typeof fetch;
    await assert.rejects(submitUpdateRequest('install'), RejectedUpdateRequest);
    assert.equal(calls, 0, 'no silent unbound compatibility fallback');
    await assert.rejects(submitUpdateRequest('install', undefined, target), e => e instanceof RejectedUpdateRequest && /יעד העדכון השתנה/.test(e.message));
    assert.equal(calls, 1);
  } finally { globalThis.fetch = original; }
});

test('queued and restarted progress retain requested X rather than newer available Y', () => {
  const queued = { ...base, latestVersion: nextTarget.version, installTarget: nextTarget,
    queuedRequest: { id: requestId, action: 'install' as const, target, createdAt: 2000 } };
  const tracked = reconcileUpdateRequest(queued, null)!;
  assert.deepEqual(tracked.target, target);
  assert.equal(updateProgress(queued).action, 'התקנת גרסה 0.4.0');
  assert.equal(updateProgress(base, tracked).action, 'התקנת גרסה 0.4.0');
  const running = { ...base, latestVersion: nextTarget.version, installTarget: nextTarget, phase: 'building' as const,
    lastRequest: { ...journal('running'), target } };
  assert.equal(updateProgress(structuredClone(running)).action, 'התקנת גרסה 0.4.0');
  assert.equal(observesRequest({ ...running, lastRequest: { ...journal('running'), target: nextTarget } }, tracked), false);
  assert.equal(observesRequest({ ...running, lastRequest: journal('running') }, tracked), false, 'a legacy journal cannot acknowledge a target-bound install');
  const rejected = { ...base, phase: 'failed' as const, lastRequest: { ...journal('failed'), target, error: 'target_changed' as const } };
  assert.equal(reconcileUpdateRequest(rejected, tracked), null);
  assert.match(updateProgress(rejected).title, /יעד העדכון השתנה/);
  assert.match(updateProgress(rejected).detail, /לא נשלחת התקנה נוספת/);
});

test('actual confirmation snapshots X; refresh to Y or changed bytes invalidates consent without silently installing Y', async () => {
  for (const replacement of [nextTarget, { ...target, fingerprint: 'c'.repeat(64) }]) {
    const h = panelHarness(base, false, async () => new Response(JSON.stringify({ state: 'queued' })));
    h.button(/התקנת גרסה/).props.onClick();
    const oldConfirm = h.button(/כן, התקנת גרסה/);
    assert.match(renderToStaticMarkup(oldConfirm), /0\.4\.0/);
    h.status({ ...base, latestVersion: replacement.version, installTarget: replacement });
    assert.equal(h.button(/כן, התקנת גרסה/).props.disabled, true);
    assert.match(h.html(), /הגרסה או קובצי העדכון השתנו/);
    assert.match(renderToStaticMarkup(h.button(/כן, התקנת גרסה/)), /0\.4\.0/);
    oldConfirm.props.onClick(); await flush();
    assert.deepEqual(h.calls, [], 'even a captured stale event handler rereads the current status before posting');
    assert.doesNotMatch(h.html(), /אישור התקנת גרסה/);
  }
});

test('actual panel sends one target-bound request on repeated clicks, and closing or reopening never retries it', async () => {
  let finish!: (response: Response) => void;
  const h = panelHarness(base, false, () => new Promise(resolve => { finish = resolve; }));
  h.button(/התקנת גרסה/).props.onClick();
  const confirm = h.button(/כן, התקנת גרסה/);
  confirm.props.onClick(); confirm.props.onClick();
  assert.deepEqual(h.bodies, [{ action: 'install', target }]);
  assert.match(h.html(), /שולחים את הבקשה/);
  h.toggle(); h.toggle();
  assert.doesNotMatch(h.html(), /אישור התקנת גרסה/, 'closing discards unsubmitted confirmation, not the queued operation');
  assert.deepEqual(h.calls, ['POST']);
  finish(new Response(JSON.stringify({ state: 'queued' }))); await flush();
  assert.match(h.html(), /הבקשה נקלטה/);
  assert.match(h.html(), /התקנת גרסה 0\.4\.0/);
  h.toggle(); h.toggle(); assert.deepEqual(h.calls, ['POST']);
  const reopened = panelHarness({ ...base, queuedRequest: { id: requestId, action: 'install', target, createdAt: 2000 } });
  assert.match(reopened.html(), /הבקשה בתור/);
  assert.match(reopened.html(), /התקנת גרסה 0\.4\.0/);
  assert.deepEqual(reopened.calls, [], 'new browser reads durable status; it never republishes a request');
});

test('browser closure during target-bound POST causes no retry; unsubmitted confirmation is discarded on reopen', async () => {
  let finish!: (response: Response) => void;
  const h = panelHarness(base, false, () => new Promise(resolve => { finish = resolve; }));
  h.button(/התקנת גרסה/).props.onClick();
  h.toggle(); h.toggle();
  assert.doesNotMatch(h.html(), /אישור התקנת גרסה/);
  assert.deepEqual(h.calls, []);
  h.button(/התקנת גרסה/).props.onClick(); h.button(/כן, התקנת גרסה/).props.onClick();
  h.unmount(); finish(new Response(JSON.stringify({ state: 'queued' }))); await flush();
  assert.deepEqual(h.bodies, [{ action: 'install', target }]);
  const reopened = panelHarness({ ...base, phase: 'building', lastRequest: { ...journal('running'), target } });
  assert.match(reopened.html(), /התקנת גרסה 0\.4\.0/);
  assert.match(reopened.html(), /גם אם סוגרים את הדפדפן/);
  assert.deepEqual(reopened.calls, []);
});


test('definite pending response follows the actual competing Y request instead of pretending X was accepted', async () => {
  const h = panelHarness(base, false, async () => new Response(JSON.stringify({ state: 'pending' })));
  h.button(/התקנת גרסה/).props.onClick(); h.button(/כן, התקנת גרסה/).props.onClick(); await flush();
  assert.match(h.html(), /הבקשה הזאת לא נוספה לתור/);
  assert.doesNotMatch(h.html(), /הבקשה נקלטה/);
  assert.deepEqual(h.bodies, [{ action: 'install', target }]);
  const following: LocalUpdateRequest = { ...local, target, state: 'following' };
  assert.equal(updateProgress(base, following).action, undefined, 'do not label a competing operation as our X');
  const competing = { ...base, phase: 'building' as const, latestVersion: nextTarget.version, installTarget: nextTarget,
    lastRequest: { ...journal('running'), target: nextTarget } };
  assert.equal(reconcileUpdateRequest(competing, following), null);
  assert.equal(reconcileUpdateRequest({ ...competing, lastRequest: { ...competing.lastRequest, state: 'succeeded', finishedAt: 3000 } }, following), null);
  h.status(competing);
  assert.match(h.html(), /התקנת גרסה 0\.5\.0/);
  assert.doesNotMatch(h.html(), /הבקשה הזאת לא נוספה לתור|התקנת גרסה 0\.4\.0/);
  h.toggle(); h.toggle(); assert.deepEqual(h.calls, ['POST']);
});


test('app check timestamp does not claim to be the background transport check time', () => {
  const source = readFileSync(new URL('../web/UpdatesPanel.tsx', import.meta.url), 'utf8');
  assert.match(source, /בדיקת גרסה אחרונה של רכיב האפליקציה:/);
  assert.match(source, /בדיקות רקע של מנגנון העדכון עשויות להתבצע בלי לשנות/);
});
