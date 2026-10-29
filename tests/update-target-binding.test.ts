import { test } from 'node:test';
import { privateFixtureDirectory,assertPrivateFixtureFile } from './fixtures/private-directory.js';
import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync, lstatSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { randomBytes, randomUUID } from 'node:crypto';
import { createApp } from '../src/server.js';
import type { Config } from '../src/config.js';

const target = { version: '0.5.0', fingerprint: 'a'.repeat(64) };
const newer = { version: '0.6.0', fingerprint: 'b'.repeat(64) };
async function fixture() {
  const dir = privateFixtureDirectory('yachad-target-');
  const config: Config = { dataDir: dir, masterKey: randomBytes(32), connector: 'mock', realAllowed: false,
    host: '127.0.0.1', port: 3000, origin: 'http://localhost:3000', secureCookie: false, maxMedia: 1048576,
    maxStorage: 67108864, childIdleMs: 60000, sessionMs: 3600000, diagnosticsEnabled: false,
    updateStatusFile: join(dir, 'root-status.json') };
  const { app, store } = await createApp(config, { boot: false });
  const family = await store.createFamily('Synthetic', { name: 'parent', password: randomBytes(24).toString('hex') });
  const session = store.createSession(store.user(store.one('SELECT * FROM users WHERE id=?', family.parentId)));
  const headers = { origin: config.origin, cookie: 'sid=' + session.sid, 'x-csrf-token': session.csrf };
  const status = (patch: any = {}) => writeFileSync(config.updateStatusFile!, JSON.stringify({
    enabled: true, mode: 'notify', modeControlAllowed: true, operator: { familyId: family.familyId, userId: family.parentId },
    phase: 'available', currentVersion: '0.4.0', latestVersion: target.version, installTarget: target,
    checkedAt: Date.now(), updatedAt: Date.now(), error: null, ...patch,
  }));
  status();
  return { dir, status,
    get: () => app.inject({ method: 'GET', url: '/api/parent/updates', headers }),
    post: (payload: any) => app.inject({ method: 'POST', url: '/api/parent/updates', headers, payload }),
    queue: () => JSON.parse(readFileSync(join(dir, 'update-request.json'), 'utf8')),
    noPublication: () => assert.equal(readdirSync(dir).some(name => name.includes('update-request')), false),
    async close() { await app.close(); rmSync(dir, { recursive: true, force: true }); },
  };
}

test('HTTP install echoes the displayed root identity in a private atomic queue and repeated clicks preserve it', async () => {
  const f = await fixture();
  try {
    const shown = (await f.get()).json().installTarget;
    assert.deepEqual(shown, target);
    const results = await Promise.all([f.post({ action: 'install', target: shown }), f.post({ action: 'install', target: shown })]);
    assert.deepEqual(results.map(x => x.json().state).sort(), ['pending', 'queued']);
    const first = f.queue();
    assert.deepEqual(first.target, target);
    assert.equal(first.action, 'install');
    assertPrivateFixtureFile(join(f.dir,'update-request.json'));
    assert.ok(readFileSync(join(f.dir, 'update-request.json')).length <= 512, 'matches privileged queue size bound');
    const reopened = (await f.get()).json();
    assert.deepEqual(reopened.queuedRequest.target, target);
    assert.equal(reopened.queuedRequest.familyId, undefined);
    f.status({ latestVersion: newer.version, installTarget: newer });
    assert.equal((await f.post({ action: 'install', target: newer })).json().state, 'pending');
    assert.deepEqual(f.queue(), first, 'a newer status must not replace an already queued target');
  } finally { await f.close(); }
});

test('GET-to-POST release and same-version asset races fail closed without queue or wake mutation', async () => {
  const f = await fixture();
  try {
    const shown = (await f.get()).json().installTarget;
    for (const changed of [newer, { ...target, fingerprint: 'c'.repeat(64) }]) {
      f.status({ latestVersion: changed.version, installTarget: changed });
      const rejected = await f.post({ action: 'install', target: shown });
      assert.equal(rejected.statusCode, 409);
      assert.equal(rejected.json().error, 'UPDATES_TARGET_CHANGED');
      f.noPublication();
    }
  } finally { await f.close(); }
});

test('lost install response followed by reload and duplicate request retains the one durable target and request ID', async () => {
  const f = await fixture();
  try {
    const shown = (await f.get()).json().installTarget;
    // Discard the accepted response, modeling loss after durable publication.
    await f.post({ action: 'install', target: shown });
    const original = f.queue();
    const reopened = (await f.get()).json();
    assert.equal(reopened.queuedRequest.id, original.id);
    assert.deepEqual(reopened.queuedRequest.target, shown);
    const duplicate = await f.post({ action: 'install', target: shown });
    assert.equal(duplicate.statusCode, 200);
    assert.equal(duplicate.json().state, 'pending');
    assert.deepEqual(f.queue(), original, 'reload/retry cannot replace or duplicate the accepted request');
    assert.equal((await f.get()).json().queuedRequest.id, original.id);
  } finally { await f.close(); }
});

test('older status and invalid root target remain readable but cannot silently queue an unbound install', async () => {
  const f = await fixture();
  try {
    for (const installTarget of [undefined, null, { ...target, fingerprint: 'invalid' }, newer]) {
      f.status({ installTarget });
      assert.equal((await f.get()).json().configured, true);
      for (const payload of [{ action: 'install' }, { action: 'install', target }]) {
        const rejected = await f.post(payload);
        assert.equal(rejected.statusCode, 409);
        assert.equal(rejected.json().error, 'UPDATES_TARGET_CHANGED');
        f.noPublication();
      }
    }
    f.status({ installTarget: undefined });
    assert.equal((await f.post({ action: 'check' })).json().state, 'queued', 'legacy status may request a new check, never unbound install');
  } finally { await f.close(); }
});

test('root target and journal expose only opaque identity, while unknown request keys are rejected', async () => {
  const f = await fixture();
  try {
    const id = randomUUID();
    f.status({ installTarget: { ...target, url: 'ROOT-PRIVATE-URL', token: 'ROOT-PRIVATE-TOKEN' },
      lastRequest: { id, action: 'install', state: 'failed', target: { ...target, secret: 'ROOT-PRIVATE-TOKEN' },
        startedAt: Date.now(), finishedAt: Date.now(), error: 'target_changed' } });
    const view = (await f.get()).json();
    assert.deepEqual(view.installTarget, target);
    assert.deepEqual(view.lastRequest.target, target);
    assert.equal(view.lastRequest.error, 'target_changed');
    assert.doesNotMatch(JSON.stringify(view), /ROOT-PRIVATE/);
    for (const payload of [
      { action: 'install', target: { ...target, url: 'https://example.invalid' } },
      { action: 'install', target: { ...target, fingerprint: 'A'.repeat(64) } },
      { action: 'install', target, repository: 'other/repo' },
      { action: 'check', target }, { action: 'set_mode', mode: 'auto', target },
    ]) assert.equal((await f.post(payload)).statusCode, 400);
    f.noPublication();
  } finally { await f.close(); }
});

test('failed target capability needs a fresh check and recovery barriers dominate even a matching target', async () => {
  const f = await fixture();
  try {
    for (const phase of ['failed', 'rolled_back', 'healthy', 'idle']) {
      f.status({ phase });
      const response = await f.post({ action: 'install', target });
      assert.equal(response.statusCode, 409);
      assert.equal(response.json().error, 'UPDATES_TARGET_CHANGED');
      f.noPublication();
    }
    for (const phase of ['recovery_required', 'rollback_pending_health']) {
      f.status({ phase });
      for (const payload of [{ action: 'install', target }, { action: 'check' }, { action: 'set_mode', mode: 'auto' }]) {
        const response = await f.post(payload);
        assert.equal(response.statusCode, 409);
        assert.equal(response.json().error, 'UPDATES_UNAVAILABLE');
      }
      f.noPublication();
    }
    f.status({ phase: 'failed', error: 'target_changed' });
    assert.equal((await f.post({ action: 'check' })).json().state, 'queued');
  } finally { await f.close(); }
});
