import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { randomBytes, randomUUID } from 'node:crypto';
import { requestUpdate, updateStatus } from '../src/updates.js';
import { createApp } from '../src/server.js';
import type { Config } from '../src/config.js';
import type { User } from '../src/store.js';

function fixture() {
  const directory = mkdtempSync(join(tmpdir(), 'yachad-recovery-gate-'));
  const config: Config = {
    dataDir: directory, masterKey: randomBytes(32), connector: 'mock', realAllowed: false,
    host: '127.0.0.1', port: 3000, origin: 'http://localhost:3000', secureCookie: false,
    maxMedia: 1048576, maxStorage: 67108864, childIdleMs: 60000, sessionMs: 3600000,
    diagnosticsEnabled: false, updateStatusFile: join(directory, 'root-status.json'),
  };
  const parent: User = { id: randomUUID(), familyId: randomUUID(), role: 'parent', name: 'הורה' };
  const status = (phase: string, user = parent, mode: 'auto' | 'notify' = 'auto', extra = {}) => {
    const content = JSON.stringify({
      enabled: true, mode, modeControlAllowed: true, operator: { familyId: user.familyId, userId: user.id },
      phase, currentVersion: '0.4.0', latestVersion: '0.4.1', checkedAt: Date.now(), updatedAt: Date.now(),
      error: 'rollback', previousPath: '/private/previous-release', ...extra,
    });
    writeFileSync(config.updateStatusFile!, content);
    return content;
  };
  return { directory, config, parent, status, close() { rmSync(directory, { recursive: true, force: true }); } };
}

test('recovery phases reject every queued action while leaving sanitized status readable', async () => {
  const f = fixture();
  try {
    for (const phase of ['recovery_required', 'rollback_pending_health']) {
      for (const mode of ['auto', 'notify'] as const) {
        const before = f.status(phase, f.parent, mode);
        for (const [action, requestedMode] of [
          ['check', undefined], ['install', undefined], ['set_mode', 'auto'], ['set_mode', 'notify'],
        ] as const) {
          assert.equal(await requestUpdate(f.config, f.parent, action, requestedMode), 'unavailable');
        }
        assert.deepEqual(readdirSync(f.directory), ['root-status.json'], 'no queue, wake latch or temp publication');
        assert.equal(readFileSync(f.config.updateStatusFile!, 'utf8'), before, 'never clears or overwrites root recovery');
        const view = await updateStatus(f.config, f.parent);
        assert.equal(view.configured, true);
        if (view.configured) {
          assert.equal(view.phase, phase);
          assert.equal(view.mode, mode);
          assert.equal(view.queuedRequest, null);
        }
        assert.equal('previousPath' in view, false);
        assert.equal('operator' in view, false);
      }
    }
  } finally { f.close(); }
});

test('recovery refusal preserves an existing queue and wins over pending acknowledgement', async () => {
  const f = fixture();
  try {
    const id = randomUUID();
    const queue = JSON.stringify({ id, action: 'install', familyId: f.parent.familyId, userId: f.parent.id, createdAt: Date.now() });
    writeFileSync(join(f.directory, 'update-request.json'), queue, { mode: 0o600 });
    const before = f.status('recovery_required', f.parent, 'notify', {
      lastRequest: { id, action: 'install', state: 'running', startedAt: Date.now(), finishedAt: null, error: null },
      lastModeRequest: { id: randomUUID(), mode: 'auto', state: 'pending', error: 'configuration' },
    });
    assert.equal(await requestUpdate(f.config, f.parent, 'check'), 'unavailable');
    assert.equal(await requestUpdate(f.config, f.parent, 'set_mode', 'auto'), 'unavailable');
    assert.equal(readFileSync(join(f.directory, 'update-request.json'), 'utf8'), queue);
    assert.equal(readFileSync(f.config.updateStatusFile!, 'utf8'), before);
    assert.deepEqual(readdirSync(f.directory).sort(), ['root-status.json', 'update-request.json']);
  } finally { f.close(); }
});

test('HTTP recovery POSTs are conflict responses, GET is available, and normal terminal work remains queueable', async () => {
  const f = fixture();
  const { app, store } = await createApp(f.config, { boot: false });
  try {
    const family = await store.createFamily('Synthetic', { name: 'הורה', password: randomBytes(24).toString('hex') });
    const user = store.user(store.one('SELECT * FROM users WHERE id=?', family.parentId));
    const session = store.createSession(user);
    const headers = { origin: f.config.origin, cookie: 'sid=' + session.sid, 'x-csrf-token': session.csrf };
    for (const phase of ['recovery_required', 'rollback_pending_health']) {
      f.status(phase, user);
      for (const payload of [{ action: 'check' }, { action: 'install' }, { action: 'set_mode', mode: 'auto' }, { action: 'set_mode', mode: 'notify' }]) {
        const response = await app.inject({ method: 'POST', url: '/api/parent/updates', headers, payload });
        assert.equal(response.statusCode, 409);
        assert.equal(response.json().error, 'UPDATES_UNAVAILABLE');
      }
      const response = await app.inject({ method: 'GET', url: '/api/parent/updates', headers });
      assert.equal(response.statusCode, 200);
      assert.equal(response.json().phase, phase);
      assert.equal(response.json().configured, true);
      assert.equal(readdirSync(f.directory).some(name => /update-request/.test(name)), false);
    }
    f.status('failed', user, 'notify', { error: 'network' });
    const retriable = await app.inject({ method: 'POST', url: '/api/parent/updates', headers, payload: { action: 'check' } });
    assert.equal(retriable.statusCode, 200);
    assert.equal(retriable.json().state, 'queued');
  } finally { await app.close(); f.close(); }
});
