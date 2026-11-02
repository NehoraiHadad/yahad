import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { randomUUID } from 'node:crypto';
import { UpdateIdleFence } from '../src/update-idle.js';
import { createApp } from '../src/server.js';
import { privateFixtureDirectory } from './fixtures/private-directory.js';
import { randomBytes } from 'node:crypto';
import type { Config } from '../src/config.js';

const root = () => mkdtempSync(join(tmpdir(), 'yahad-idle-'));
const operation = () => randomUUID().replaceAll('-', '');
test('browser acknowledgement survives restart and cannot expire into idle', () => {
  const directory = root(), id = randomUUID();
  const fence = new UpdateIdleFence(directory);
  fence.lease(id, 'child-a', 'open');
  assert.equal(fence.prepare(operation()), false);
  const restarted = new UpdateIdleFence(directory);
  assert.equal(restarted.busy, true);
  restarted.lease(id, 'child-a', 'release');
  assert.equal(new UpdateIdleFence(directory).prepare(operation()), true);
});
test('another identity cannot release a browser lease', () => {
  const fence = new UpdateIdleFence(root()), id = randomUUID();
  fence.lease(id, 'owner-a', 'open');
  assert.throws(() => fence.lease(id, 'owner-b', 'release'));
  assert.equal(fence.busy, true);
});
test('atomic idle fence excludes new requests and browser admissions', () => {
  const fence = new UpdateIdleFence(root()), id = operation();
  const done = fence.enterRequest();
  assert.equal(fence.prepare(id), false);
  done(); done();
  assert.equal(fence.prepare(id), true);
  assert.equal(fence.prepare(id), true);
  assert.equal(fence.prepare(operation()), false);
  assert.throws(() => fence.enterRequest());
  assert.throws(() => fence.lease(randomUUID(), 'child', 'open'));
  fence.cancel(operation());
  assert.equal(fence.preparing, true);
  fence.cancel(id);
  assert.equal(fence.preparing, false);
});
test('interrupted publication and corrupt safety state remain busy', () => {
  const partial = root();
  writeFileSync(join(partial, 'update-idle.json.partial-abandoned'), '{}');
  assert.equal(new UpdateIdleFence(partial).prepare(operation()), false);
  const corrupt = root(); writeFileSync(join(corrupt, 'update-idle.json'), '{');
  assert.equal(new UpdateIdleFence(corrupt).prepare(operation()), false);
});
test('multiple browsers all have to settle and duplicate open/release are idempotent', () => {
  const fence = new UpdateIdleFence(root()), a = randomUUID(), b = randomUUID();
  fence.lease(a, 'same-child', 'open'); fence.lease(a, 'same-child', 'open');
  fence.lease(b, 'same-child', 'open'); fence.lease(a, 'same-child', 'release');
  fence.lease(a, 'same-child', 'release'); assert.equal(fence.busy, true);
  fence.lease(b, 'same-child', 'release'); assert.equal(fence.busy, false);
});
test('non-HTTP in-flight work is an independent refusal', () => {
  let sending = true;
  const fence = new UpdateIdleFence(root(), () => sending);
  assert.equal(fence.prepare(operation()), false);
  sending = false; assert.equal(fence.prepare(operation()), true);
});
test('generation CAS prevents delayed old page releases and opens from clearing a new page', () => {
  const fence = new UpdateIdleFence(root()), id = randomUUID(), first = randomUUID(), next = randomUUID();
  fence.lease(id, 'child', 'open', first);
  fence.lease(id, 'child', 'open', next, first);
  fence.lease(id, 'child', 'release', first);
  assert.equal(fence.busy, true);
  assert.throws(() => fence.lease(id, 'child', 'open', first));
  assert.throws(() => fence.lease(id, 'child', 'open', randomUUID(), first));
  fence.lease(id, 'child', 'open', next, first); // Lost reply retry is idempotent.
  fence.lease(id, 'child', 'release', next);
  assert.equal(fence.busy, false);
});
test('explicit settlement is owner and generation bound and refuses in-flight work', () => {
  const fence = new UpdateIdleFence(root()), id = randomUUID(), generation = randomUUID();
  fence.lease(id, 'child-a', 'open', generation);
  assert.deepEqual(fence.inventory('child-b'), []);
  assert.throws(() => fence.settle('child-b', [{ id, generation }]));
  fence.settle('child-a', [{ id, generation: randomUUID() }]); assert.equal(fence.busy, true);
  const a = fence.enterRequest(), b = fence.enterRequest();
  assert.throws(() => fence.settle('child-a', [{ id, generation }])); a(); b();
  fence.settle('child-a', [{ id, generation }]); assert.equal(fence.busy, false);
});
test('HTTP lease is authenticated, CSRF protected, owner bound and blocks prepare', async () => {
  const settings: Config = { dataDir: privateFixtureDirectory('yahad-idle-http-'), masterKey: randomBytes(32), connector: 'mock', realAllowed: false,
    host: '127.0.0.1', port: 3000, origin: 'http://localhost:3000', secureCookie: false, maxMedia: 1048576, maxStorage: 67108864,
    childIdleMs: 60000, sessionMs: 3600000, diagnosticsEnabled: false };
  const fence = new UpdateIdleFence(settings.dataDir);
  const { app, store } = await createApp(settings, { boot: false, updateIdle: fence });
  try {
    const password = randomBytes(24).toString('hex');
    const family = await store.createFamily('Fixture', { name: 'parent', password }, { name: 'child', password });
    const parent = store.createSession(store.user(store.one('SELECT * FROM users WHERE id=?', family.parentId)));
    const child = store.createSession(store.user(store.one('SELECT * FROM users WHERE id=?', family.childId)));
    const headers = (s: typeof parent) => ({ origin: settings.origin, cookie: 'sid=' + s.sid, 'x-csrf-token': s.csrf });
    const id = randomUUID(), generation = randomUUID();
    assert.equal((await app.inject({ method: 'POST', url: '/api/update-idle/lease', payload: { id, generation, action: 'open' } })).statusCode, 401);
    assert.equal((await app.inject({ method: 'POST', url: '/api/update-idle/lease', headers: { cookie: 'sid=' + parent.sid }, payload: { id, generation, action: 'open' } })).statusCode, 403);
    assert.equal((await app.inject({ method: 'POST', url: '/api/update-idle/lease', headers: headers(parent), payload: { id, generation, action: 'open' } })).statusCode, 200);
    assert.equal(fence.prepare(operation()), false);
    assert.equal((await app.inject({ method: 'POST', url: '/api/update-idle/lease', headers: headers(child), payload: { id, generation, action: 'release' } })).statusCode, 409);
    assert.equal((await app.inject({ method: 'POST', url: '/api/update-idle/lease', headers: headers(parent), payload: { id, generation, action: 'release' } })).statusCode, 200);
    const op = operation(); assert.equal(fence.prepare(op), true);
    // Fence rejects before authentication can refresh the persistent session.
    assert.equal((await app.inject({ method: 'GET', url: '/api/me', headers: headers(parent) })).statusCode, 503);
    assert.equal(fence.cancel(op), true);
  } finally { await app.close(); }
});
