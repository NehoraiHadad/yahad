import { test } from 'node:test';
import assert from 'node:assert/strict';
import { randomBytes, randomUUID } from 'node:crypto';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { readBootstrap, readFirstOwnerIdentity, type FirstOwnerIdentity } from '../src/bootstrap.js';
import type { PlatformFiles } from '../src/platform-files.js';
import type { Config } from '../src/config.js';
import { createApp } from '../src/server.js';
import { Store } from '../src/store.js';
import { UpdateIdleFence } from '../src/update-idle.js';

const identity = (): FirstOwnerIdentity => ({ format: 1, installationId: '1'.repeat(32), ownerSid: 'S-1-5-21-1-2-3-1001', familyId: randomUUID(), parentUserId: randomUUID() });
function files(record: unknown): PlatformFiles {
  return { readSetupCapability: () => Buffer.from(JSON.stringify(record)), readMasterKey: () => { throw new Error('UNUSED'); },
    acquireDataLock: async () => { throw new Error('UNUSED'); }, setupCapabilityPath: () => 'protected-fixture' };
}
test('protected first identity survives capability expiry and renewal, while malformed identity fails closed', () => {
  const firstOwner = identity(), token = randomBytes(32).toString('base64url'), now = Date.now();
  const expired = files({ token, expiresAt: now - 1, firstOwner });
  assert.equal(readBootstrap('fixture', now, expired), undefined);
  assert.deepEqual(readFirstOwnerIdentity('fixture', expired), firstOwner);
  const renewed = files({ token, expiresAt: now + 60000, firstOwner });
  assert.deepEqual(readBootstrap('fixture', now, renewed)?.firstOwner, firstOwner);
  for (const changed of [{ ...firstOwner, format: 2 }, { ...firstOwner, familyId: 'arbitrary' }, { ...firstOwner, parentUserId: firstOwner.familyId }, { ...firstOwner, ownerSid: 'S-1-5-18' }]) {
    assert.throws(() => readFirstOwnerIdentity('fixture', files({ token, expiresAt: now + 60000, firstOwner: changed })));
    assert.throws(() => readBootstrap('fixture', now, files({ token, expiresAt: now + 60000, firstOwner: changed })));
  }
});
async function fixture() {
  const directory = mkdtempSync(join(tmpdir(), 'yf-owner-'));
  const config: Config = { dataDir: directory, masterKey: randomBytes(32), connector: 'mock', realAllowed: false, host: '127.0.0.1', port: 3000,
    origin: 'http://127.0.0.1:3000', secureCookie: false, maxMedia: 1024 ** 2, maxStorage: 64 * 1024 ** 2, childIdleMs: 900000, sessionMs: 3600000, diagnosticsEnabled: false };
  const store = new Store(config), firstOwner = identity(), token = randomBytes(32).toString('base64url'), idle = new UpdateIdleFence(directory);
  const { app } = await createApp(config, { store, boot: false, updateIdle: idle, firstOwner, bootstrap: { token, expiresAt: Date.now() + 60000, firstOwner } });
  const body = { familyName: 'Synthetic family', name: 'Synthetic parent', password: 'synthetic-private-passphrase', confirmation: 'synthetic-private-passphrase' };
  const setup = (extra = {}) => app.inject({ method: 'POST', url: '/api/setup', headers: { origin: config.origin, 'x-setup-token': token }, payload: { ...body, ...extra } });
  return { app, store, firstOwner, idle, setup, config, async close() { await app.close(); store.close(); rmSync(directory, { recursive: true, force: true }); } };
}
test('first setup creates the exact protected REAL identity atomically and intrinsically holds update admission', async () => {
  const f = await fixture(), operation = '2'.repeat(32);
  try {
    assert.equal(f.idle.prepare(operation), false);
    const pending = f.setup(); assert.equal(f.idle.prepare(operation), false);
    assert.equal((await pending).statusCode, 200);
    const parent = f.store.one("SELECT id,family_id,role FROM users WHERE role='parent'");
    assert.deepEqual({ ...parent }, { id: f.firstOwner.parentUserId, family_id: f.firstOwner.familyId, role: 'parent' });
    assert.equal(f.store.one('SELECT count(*) AS n FROM families').n, 1);
    assert.equal(f.store.one("SELECT count(*) AS n FROM users WHERE role='child'").n, 0);
    assert.equal(f.idle.prepare(operation), true); f.idle.cancel(operation);
    assert.equal((await f.setup()).statusCode, 403);
  } finally { await f.close(); }
});
test('HTTP cannot choose the enrolled identity and a partial or changed parent keeps update admission held', async () => {
  const f = await fixture(), operation = '3'.repeat(32);
  try {
    assert.equal((await f.setup({ familyId: randomUUID(), parentUserId: randomUUID() })).statusCode, 400);
    assert.equal(f.store.one('SELECT count(*) AS n FROM families').n, 0);
    assert.equal((await f.setup()).statusCode, 200);
    f.store.run("UPDATE users SET role='child' WHERE id=?", f.firstOwner.parentUserId);
    assert.equal(f.idle.prepare(operation), false);
    assert.equal((await f.app.inject({ url: '/health/ready' })).statusCode, 200, 'initial Runtime health is separate from update admission');
    await assert.rejects(f.store.createFamily('Other', { name: 'Other', password: 'fixture-only' }, undefined, true, f.firstOwner));
    assert.equal(f.store.one('SELECT count(*) AS n FROM families').n, 1);
  } finally { await f.close(); }
});
