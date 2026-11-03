import { test } from 'node:test';
import assert from 'node:assert/strict';
import { randomBytes } from 'node:crypto';
import { readdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { createApp } from '../src/server.js';
import { requestUpdate, updateStatus } from '../src/updates.js';
import { createWindowsUpdateTransport, windowsUpdateResult, type WindowsUpdateTransport } from '../src/windows-update-transport.js';
import type { Config } from '../src/config.js';
import type { User } from '../src/store.js';
import { privateFixtureDirectory } from './fixtures/private-directory.js';

// The exact reply of `Yahad.Platform.exe windows-updates` in this release
// (installer/Yahad.Platform/Program.cs): every request is refused.
const platformRefusal = JSON.stringify({ schema: 1, ok: false, code: 'UPDATES_UNAVAILABLE' });
const platformTransport = (calls: string[]): WindowsUpdateTransport => ({
  status: async () => { calls.push('status'); return windowsUpdateResult(platformRefusal); },
  submit: async () => { calls.push('submit'); return windowsUpdateResult(platformRefusal); },
});

test('the Platform windows-updates refusal never yields a result', () => {
  assert.throws(() => windowsUpdateResult(platformRefusal), /WINDOWS_UPDATES_REPLY/);
  assert.throws(() => windowsUpdateResult(JSON.stringify({ schema: 2, ok: true, result: {} })), /WINDOWS_UPDATES_REPLY/);
  assert.deepEqual(windowsUpdateResult(JSON.stringify({ schema: 1, ok: true, result: { state: 'queued' } })), { state: 'queued' });
});

test('the Windows transport runs only an absolute Platform helper on Windows and fails closed without one', async () => {
  if (process.platform !== 'win32') {
    assert.throws(() => createWindowsUpdateTransport(), /WINDOWS_UPDATES_UNCONFIGURED/);
    return;
  }
  assert.throws(() => createWindowsUpdateTransport('Yahad.Platform.exe'), /WINDOWS_UPDATES_UNCONFIGURED/);
  const missing = createWindowsUpdateTransport(join(privateFixtureDirectory('yahad-no-platform-'), 'Yahad.Platform.exe'));
  await assert.rejects(missing.status(), /WINDOWS_UPDATES_UNAVAILABLE/);
});

test('Windows updates stay disabled: status is not configured and requests are refused without submitting', async () => {
  const dataDir = privateFixtureDirectory('yahad-windows-updates-');
  // A valid enabled Linux status file must not be consulted on the Windows path.
  const updateStatusFile = join(dataDir, 'linux-status.json');
  writeFileSync(updateStatusFile, JSON.stringify({ enabled: true, mode: 'notify', phase: 'idle', currentVersion: '0.1.0', latestVersion: null,
    operator: { familyId: 'unused', userId: 'unused' }, checkedAt: null, updatedAt: Date.now(), error: null }));
  const settings: Config = { dataDir, updateStatusFile, masterKey: randomBytes(32), connector: 'mock', realAllowed: false,
    host: '127.0.0.1', port: 3000, origin: 'http://localhost:3000', secureCookie: false, maxMedia: 1048576, maxStorage: 67108864,
    childIdleMs: 60000, sessionMs: 3600000, diagnosticsEnabled: false };
  const calls: string[] = [];
  const { app, store } = await createApp(settings, { boot: false, windowsUpdates: platformTransport(calls) });
  try {
    const password = randomBytes(24).toString('hex');
    const family = await store.createFamily('Fixture', { name: 'parent', password }, { name: 'child', password });
    const session = store.createSession(store.user(store.one('SELECT * FROM users WHERE id=?', family.parentId)));
    const headers = { origin: settings.origin, cookie: 'sid=' + session.sid, 'x-csrf-token': session.csrf };
    const status = await app.inject({ method: 'GET', url: '/api/parent/updates', headers });
    assert.equal(status.statusCode, 200); assert.deepEqual(status.json(), { configured: false });
    for (const payload of [{ action: 'check' }, { action: 'set_mode', mode: 'auto' }]) {
      const refused = await app.inject({ method: 'POST', url: '/api/parent/updates', payload, headers });
      assert.equal(refused.statusCode, 409); assert.equal(refused.json().error, 'UPDATES_UNAVAILABLE');
    }
    assert.deepEqual(calls, ['status', 'status', 'status'], 'a refused status never reaches submit');
  } finally { await app.close(); }
});

// Kept for the transport that step 6.5 puts behind this same interface.
test('a configured Windows transport binds the exact target, submits once and never retries or falls back to a file queue', async () => {
  const user = { role: 'parent', familyId: 'synthetic-family', id: 'synthetic-parent' } as User;
  const config = { dataDir: privateFixtureDirectory('yahad-windows-submit-') } as Config;
  const target = { version: '0.6.1', fingerprint: 'a'.repeat(64) }, calls: unknown[] = [];
  const snapshot = { enabled: true, mode: 'notify', modeControlAllowed: true, phase: 'available', currentVersion: '0.6.0', latestVersion: '0.6.1',
    operator: { familyId: user.familyId, userId: user.id }, installTarget: target, checkedAt: Date.now(), updatedAt: Date.now(), error: null };
  const transport: WindowsUpdateTransport = { status: async () => snapshot, submit: async command => { calls.push(command); return { state: 'queued' }; } };
  assert.equal((await updateStatus(config, { ...user, id: 'other-parent' }, transport)).configured, false);
  assert.equal(await requestUpdate(config, user, 'install', undefined, { ...target, fingerprint: 'b'.repeat(64) }, transport), 'target_changed');
  assert.equal(calls.length, 0);
  assert.equal(await requestUpdate(config, user, 'install', undefined, target, transport), 'queued');
  assert.deepEqual((calls[0] as { target: unknown }).target, target);
  let attempts = 0; transport.submit = async () => { attempts++; throw new Error('reply lost after durable acceptance'); };
  await assert.rejects(requestUpdate(config, user, 'check', undefined, undefined, transport)); assert.equal(attempts, 1);
  assert.deepEqual(readdirSync(config.dataDir), []);
});
