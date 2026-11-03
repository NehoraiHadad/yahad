import { test } from 'node:test';
import assert from 'node:assert/strict';
import { randomBytes } from 'node:crypto';
import { mkdtempSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { NativeEffectsGate, nativeProbationBinding } from '../src/native-probation.js';
import { createApp } from '../src/server.js';
import { Store } from '../src/store.js';
import { DATABASE_SCHEMA } from '../src/database-schema.js';
import type { Config } from '../src/config.js';

const op = '1'.repeat(32);
const binding = Object.freeze({ nonce: '4'.repeat(32), activationReceiptDigest: '5'.repeat(64), operationId: op });
const releaseFrame = { type: 'release-effects', id: '6'.repeat(32), ...binding };

// An update only swaps the release and starts it; the Store migrates an older
// database when it opens it and must refuse one written by a newer release.
test('Store refuses a database newer than this release instead of opening it', () => {
  const dir = mkdtempSync(join(tmpdir(), 'yfn-'));
  const config: Config = { dataDir: dir, masterKey: randomBytes(32), connector: 'mock', realAllowed: false,
    host: '127.0.0.1', port: 3000, origin: 'http://localhost:3000', secureCookie: false,
    maxMedia: 1048576, maxStorage: 64 * 1048576, childIdleMs: 900000, sessionMs: 3600000, diagnosticsEnabled: false };
  try {
    const seed = new Store(config);
    seed.db.exec(`UPDATE schema_version SET version=${DATABASE_SCHEMA + 1}`); seed.close();
    assert.throws(() => new Store(config), /Unsupported database schema/);
  } finally { rmSync(dir, { recursive: true, force: true }); }
});

test('probation requires complete native binding and releases effects only once after an exact bound command', async () => {
  assert.equal(nativeProbationBinding({}), undefined);
  assert.throws(() => nativeProbationBinding({ YAHAD_NATIVE_PROBATION_NONCE: binding.nonce }));
  const gate = new NativeEffectsGate(binding); let starts = 0;
  await assert.rejects(gate.release({ ...releaseFrame, nonce: '0'.repeat(32) }, async () => { starts++; }));
  assert.equal(starts, 0); assert.equal(gate.released, false);
  assert.deepEqual(await gate.release(releaseFrame, async () => { starts++; }), { ...releaseFrame, type: 'effects-released' });
  assert.equal(gate.released, true); assert.equal(starts, 1);
  await assert.rejects(gate.release(releaseFrame, async () => { starts++; })); assert.equal(starts, 1);
});
test('probation cancellation or failed effects release never acknowledges release or retries it', async () => {
  const gate = new NativeEffectsGate(binding); let starts = 0;
  await assert.rejects(gate.release(releaseFrame, async () => { starts++; gate.abort(); }));
  assert.equal(gate.released, false);
  await assert.rejects(gate.release(releaseFrame, async () => { starts++; })); assert.equal(starts, 1);
});
test('probation API barrier runs before login/session/throttle writes; local health remains available', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'yfp-'));
  const config: Config = { dataDir: dir, masterKey: randomBytes(32), connector: 'mock', realAllowed: false,
    host: '127.0.0.1', port: 3000, origin: 'http://localhost:3000', secureCookie: false,
    maxMedia: 1048576, maxStorage: 64 * 1048576, childIdleMs: 900000, sessionMs: 3600000, diagnosticsEnabled: false };
  let effects = false, providers = 0;
  const instance = await createApp(config, { boot: false, effectsReady: () => effects,
    factory: () => { providers++; throw new Error('synthetic provider should stay held'); } });
  try {
    const before = instance.store.one('SELECT count(*) AS n FROM credential_attempts').n;
    const login = await instance.app.inject({ method: 'POST', url: '/api/login', headers: { origin: config.origin }, payload: { name: 'parent', password: 'synthetic password' } });
    assert.equal(login.statusCode, 503); assert.equal(login.json().error, 'UPDATE_PROBATION');
    assert.equal(instance.store.one('SELECT count(*) AS n FROM credential_attempts').n, before); assert.equal(providers, 0);
    assert.equal((await instance.app.inject({ url: '/health/ready' })).statusCode, 200);
    effects = true; assert.notEqual((await instance.app.inject({ url: '/api/login', method: 'POST', payload: {} })).statusCode, 503);
  } finally { await instance.app.close(); rmSync(dir, { recursive: true, force: true }); }
});
