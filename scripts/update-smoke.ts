// Runs only on fresh temporary synthetic data, never household data or a real provider.
import assert from 'node:assert/strict';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { randomBytes } from 'node:crypto';
import { DatabaseSync } from 'node:sqlite';
import { Store } from '../src/store.js';
import { DATABASE_SCHEMA, DATABASE_MIGRATIONS_FROM } from '../src/database-schema.js';
import { createApp } from '../src/server.js';
if (process.versions.node.split('.')[0] !== '24') throw new Error('Node 24 required');
const args = process.argv.slice(2);
if (args.length) {
  if (args.length !== 4 || args[0] !== '--database-schema' || Number(args[1]) !== DATABASE_SCHEMA || args[2] !== '--database-migrations-from')
    throw new Error('Unexpected database migration contract');
  assert.deepEqual(JSON.parse(args[3]), [...DATABASE_MIGRATIONS_FROM], 'Migration sources do not match this release');
}
const dir = mkdtempSync(join(tmpdir(), 'yachad-release-'));
try {
  const config = { dataDir: join(dir, 'fresh'), masterKey: randomBytes(32), connector: 'mock' as const,
    realAllowed:false, host:'127.0.0.1',port:3000,origin:'http://localhost:3000',secureCookie:false,
    maxMedia:1048576,maxStorage:64*1048576,childIdleMs:60000,sessionMs:3600000,diagnosticsEnabled:false };
  const fresh = await createApp(config, { boot: false });
  try {
    const response = await fresh.app.inject({url:'/health/ready'});
    assert.equal(response.statusCode, 200);
    assert.equal(response.json().databaseSchema, DATABASE_SCHEMA);
    assert.equal(fresh.store.one('PRAGMA synchronous')?.synchronous, 2);
    await import('@whiskeysockets/baileys');
  } finally { await fresh.app.close(); }

  const migrationFixture = async (source: number) => {
  const upgradeConfig = { ...config, dataDir: join(dir, 'synthetic-upgrade-' + source) };
  const seed = new Store(upgradeConfig), password = 'synthetic release passphrase only';
  const family = await seed.createFamily('Synthetic', { name: 'parent-smoke', password }, { name: 'child-smoke', password });
  const child = seed.user(seed.one('SELECT * FROM users WHERE id=?', family.childId));
  const session = seed.createSession(child), group = '15550000001-15550000002@g.us';
  seed.syncGroups(family.familyId, [{ id: group, title: 'Synthetic group' }]);
  seed.grant(family.familyId, family.childId, group, true);
  seed.authSet(family.familyId, 'synthetic-auth', 'not a provider credential');
  seed.setIdentityLabel(family.familyId, family.childId, 'Synthetic signature');
  seed.putMessage(family.familyId, { id: 'synthetic-message', groupId: group, sender: 'Synthetic', fromMe: false,
    time: 1, kind: 'text', status: 'delivered', text: 'Synthetic history, not household data', reactions: {} });
  const originalPassword = seed.one('SELECT password FROM users WHERE id=?', family.childId).password;
  const originalAuth = seed.one('SELECT value FROM auth WHERE family_id=?', family.familyId).value;
  if (source >= 3) {
    seed.usage.setTimezone(family.familyId, 'Asia/Jerusalem');
    const policy = seed.usage.setPolicy(family.familyId, family.childId, 37);
    seed.run('INSERT INTO usage_days VALUES(?,?,?,?,?)', family.familyId, family.childId, policy.dayKey, 1234, 60000);
    if (source === 4) seed.usage.setTimezone(family.familyId, 'America/New_York');
  }
  const originalCalendar = source === 4 ? seed.one('SELECT * FROM usage_calendar WHERE family_id=?', family.familyId) : null;
  seed.close();
  // Produce an exact legacy users layout inside this disposable fixture only.
  // This is NOT a runtime downgrade or restore path.
  const legacy = new DatabaseSync(join(upgradeConfig.dataDir, 'app.sqlite'));
  try {
    legacy.exec('DROP TABLE child_access; UPDATE schema_version SET version=5;');
    if (source < 5) legacy.exec('UPDATE schema_version SET version=4;');
    if (source < 4) legacy.exec('DROP TABLE usage_calendar; UPDATE schema_version SET version=3;');
    if (source < 3) legacy.exec('DROP TABLE usage_bonus; DROP TABLE usage_leases; DROP TABLE usage_days; DROP TABLE usage_policy; DROP TABLE usage_families; UPDATE schema_version SET version=2;');
    if (source === 1)
    legacy.exec(`PRAGMA foreign_keys=OFF; BEGIN IMMEDIATE;
      CREATE TABLE users_legacy(id TEXT NOT NULL,family_id TEXT NOT NULL REFERENCES families(id) ON DELETE CASCADE,username TEXT NOT NULL UNIQUE,display TEXT NOT NULL,role TEXT NOT NULL CHECK(role IN ('parent','child')),password TEXT NOT NULL,PRIMARY KEY(family_id,id));
      INSERT INTO users_legacy SELECT id,family_id,name,'אני',role,password FROM users;
      DROP TABLE users; ALTER TABLE users_legacy RENAME TO users; UPDATE schema_version SET version=1; COMMIT;`);
  } finally { legacy.close(); }
  const upgraded = await createApp(upgradeConfig, { boot: false });
  try {
    const { app, store } = upgraded;
    const health = await app.inject({url:'/health/ready'});
    assert.equal(health.json().databaseSchema, DATABASE_SCHEMA);
    assert.equal(store.one('SELECT version FROM schema_version').version, DATABASE_SCHEMA);
    assert.equal(store.session(session.sid)?.user.name, 'child-smoke');
    assert.equal(store.one('SELECT password FROM users WHERE id=?', family.childId).password, originalPassword);
    assert.deepEqual(store.one('SELECT value FROM auth WHERE family_id=?', family.familyId).value, originalAuth);
    assert.equal(store.authGet(family.familyId, 'synthetic-auth'), 'not a provider credential');
    assert.equal(store.identityLabel(family.familyId, family.childId), 'Synthetic signature');
    assert.equal(store.getMessage(family.familyId, group, 'synthetic-message')?.text, 'Synthetic history, not household data');
    assert.deepEqual(store.all('PRAGMA foreign_key_check'), []);
    const usage = store.usage.status(family.familyId, family.childId);
    assert.equal(usage.minutes, source >= 3 ? 37 : null);
    if (source >= 3) { assert.equal(usage.usedMs, 1234); assert.equal(usage.bonusMs, 60000); assert.equal(usage.timezone, 'Asia/Jerusalem'); assert.equal(usage.timezoneConfirmed, true); }
    if (source === 4) { assert.deepEqual(store.one('SELECT * FROM usage_calendar WHERE family_id=?', family.familyId), originalCalendar); assert.equal(usage.pendingTimezone, 'America/New_York'); }
    const columns = store.all('PRAGMA table_info(users)').map(column => column.name);
    assert.ok(!columns.includes('username') && !columns.includes('display'));
    const login = await app.inject({ method: 'POST', url: '/api/login', headers: {origin:config.origin}, payload: { name: 'CHILD-SMOKE', password } });
    assert.equal(login.statusCode, 200);
  } finally { await upgraded.app.close(); }
  };
  const migrationFixtures = new Map<number, () => Promise<void>>([[1, () => migrationFixture(1)], [2, () => migrationFixture(2)], [3, () => migrationFixture(3)], [4, () => migrationFixture(4)], [5, () => migrationFixture(5)]]);
  for (const source of DATABASE_MIGRATIONS_FROM) {
    const fixture = migrationFixtures.get(source);
    if (!fixture) throw new Error(`No synthetic migration fixture for schema ${source}`);
    await fixture();
  }
  process.stdout.write('SMOKE_OK\n');
} finally { rmSync(dir,{recursive:true,force:true}); }
