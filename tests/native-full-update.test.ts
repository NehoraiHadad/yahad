import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createHash, randomBytes } from 'node:crypto';
import { DatabaseSync } from 'node:sqlite';
import { copyFileSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { tmpdir } from 'node:os';
import { spawnSync } from 'node:child_process';
import { pathToFileURL } from 'node:url';
import { migrateAccountNames } from '../src/db.js';
import { migrateUsage, migrateUsageCalendar, migrateUsageAdmission } from '../src/usage-schema.js';
import { NativeEffectsGate, nativeProbationBinding } from '../src/native-probation.js';
import { createApp } from '../src/server.js';
import type { Config } from '../src/config.js';

const hash = (path: string) => createHash('sha256').update(readFileSync(path)).digest('hex');
const op = '1'.repeat(32), sourceId = '2'.repeat(32), cloneId = '3'.repeat(32);
const binding = Object.freeze({ nonce: '4'.repeat(32), activationReceiptDigest: '5'.repeat(64), operationId: op });
const releaseFrame = { type: 'release-effects', id: '6'.repeat(32), ...binding };
const retained = ['families', 'sessions', 'grants', 'messages', 'auth', 'pending_sends'];
function snapshot(db: DatabaseSync) {
  return Object.fromEntries(retained.map(t => [t, db.prepare(`SELECT * FROM ${t}`).all().map(r => ({ ...r }))]));
}
function seed(path: string, version: number) {
  const db = new DatabaseSync(path);
  db.exec(`PRAGMA foreign_keys=ON;
    CREATE TABLE schema_version(version INTEGER NOT NULL); INSERT INTO schema_version VALUES(1);
    CREATE TABLE families(id TEXT PRIMARY KEY); INSERT INTO families VALUES('family');
    CREATE TABLE users(id TEXT NOT NULL,family_id TEXT NOT NULL REFERENCES families(id),username TEXT NOT NULL UNIQUE,display TEXT NOT NULL,role TEXT NOT NULL,password TEXT NOT NULL,PRIMARY KEY(family_id,id));
    INSERT INTO users VALUES('parent','family','parent','Legacy label','parent','synthetic-password-hash');
    CREATE TABLE sessions(id TEXT PRIMARY KEY,family_id TEXT,user_id TEXT,csrf TEXT,FOREIGN KEY(family_id,user_id) REFERENCES users(family_id,id));
    INSERT INTO sessions VALUES('session','family','parent','synthetic-csrf');
    CREATE TABLE grants(family_id TEXT,user_id TEXT,group_id TEXT,FOREIGN KEY(family_id,user_id) REFERENCES users(family_id,id));
    INSERT INTO grants VALUES('family','parent','group');
    CREATE TABLE messages(id TEXT PRIMARY KEY,body BLOB); INSERT INTO messages VALUES('history',x'0102ff03');
    CREATE TABLE auth(key TEXT PRIMARY KEY,value BLOB); INSERT INTO auth VALUES('encrypted-connector',x'0405ff06');
    CREATE TABLE pending_sends(id TEXT PRIMARY KEY,state TEXT,body BLOB); INSERT INTO pending_sends VALUES('unknown-outcome','unknown',x'0708ff09');`);
  if (version >= 2) migrateAccountNames(db);
  if (version >= 3) migrateUsage(db);
  if (version >= 4) migrateUsageCalendar(db);
  if (version >= 5) migrateUsageAdmission(db);
  const before = snapshot(db); db.close(); return before;
}
function fixture(version = 1) {
  // This private copy has the same fixed installed layout; no installed data or
  // updater profiles, ACLs, services, network, or real release is involved.
  const root = mkdtempSync(join(tmpdir(), 'yf-'));
  const src = join(root, 'migration-staging', op, 'payload', 'app', 'src');
  const work = join(root, 'migration-staging', op, 'work');
  const data = join(root, 'datasets', sourceId, 'restore-' + cloneId, 'completed', 'data');
  for (const p of [src, work, data]) mkdirSync(p, { recursive: true });
  writeFileSync(join(root, 'package.json'), '{"type":"module"}');
  for (const name of ['native-migrate-v1', 'db', 'account-name', 'usage-schema', 'child-access', 'database-schema'])
    copyFileSync(resolve('src', name + '.ts'), join(src, name + '.ts'));
  const original = join(root, 'original.sqlite'), before = seed(original, version);
  copyFileSync(original, join(data, 'app.sqlite'));
  const originalHash = hash(original), entry = join(src, 'native-migrate-v1.ts');
  const request: Record<string, unknown> = { format: 1, installationId: '7'.repeat(32), operationId: op,
    sourceDataId: sourceId, cloneOperationId: cloneId, newDataId: '8'.repeat(32), sourceSchema: version, targetSchema: 6,
    payloadDigest: '9'.repeat(64), entryDigest: hash(entry), dataDirectory: data, resultFile: join(work, 'result.json'), nonce: binding.nonce };
  function run() {
    writeFileSync(join(work, 'request.json'), JSON.stringify(request));
    return spawnSync(process.execPath, ['--import', pathToFileURL(resolve('node_modules/tsx/dist/loader.mjs')).href,
      entry, '--request', join(work, 'request.json')], { encoding: 'utf8', timeout: 20000, windowsHide: true,
      env: { ...process.env, NODE_OPTIONS: '' } });
  }
  return { root, data, original, originalHash, before, request, run, dispose: () => rmSync(root, { recursive: true, force: true }) };
}
for (const version of [1, 2, 3, 4, 5]) test(`offline fixed migration upgrades schema ${version} on a clone and preserves credentials, history, unknown sends and original bytes`, () => {
  const f = fixture(version);
  try {
    const result = f.run(); assert.equal(result.status, 0, result.stderr);
    const db = new DatabaseSync(join(f.data, 'app.sqlite'));
    try {
      assert.equal(db.prepare('SELECT version FROM schema_version').get()?.version, 6);
      assert.deepEqual(snapshot(db), f.before);
      assert.equal(db.prepare('SELECT password FROM users').get()?.password, 'synthetic-password-hash');
      assert.deepEqual(db.prepare('PRAGMA foreign_key_check').all(), []);
    } finally { db.close(); }
    assert.equal(hash(f.original), f.originalHash);
    assert.deepEqual(JSON.parse(readFileSync(String(f.request.resultFile), 'utf8')), { format: 1, operationId: op,
      nonce: binding.nonce, sourceSchema: version, targetSchema: 6, payloadDigest: '9'.repeat(64) });
  } finally { f.dispose(); }
});
for (const mutation of ['source-path', 'entry-hash', 'wrong-schema', 'extra-field', 'existing-result'])
  test(`offline migration refuses ${mutation} without touching original data`, () => {
    const f = fixture();
    try {
      if (mutation === 'source-path') f.request.dataDirectory = f.root;
      if (mutation === 'entry-hash') f.request.entryDigest = '0'.repeat(64);
      if (mutation === 'wrong-schema') f.request.sourceSchema = 2;
      if (mutation === 'extra-field') f.request.command = 'not-supported';
      if (mutation === 'existing-result') writeFileSync(String(f.request.resultFile), 'retained-result');
      const result = f.run(); assert.equal(result.status, 1); assert.match(result.stderr, /NATIVE_MIGRATION_REFUSED/);
      assert.equal(hash(f.original), f.originalHash);
      if (mutation === 'existing-result') assert.equal(readFileSync(String(f.request.resultFile), 'utf8'), 'retained-result');
    } finally { f.dispose(); }
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
