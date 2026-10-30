import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, rmSync, readFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { randomBytes, randomUUID } from 'node:crypto';
import { Store, type Session } from '../src/store.js';
import { UsageBudgets, nextUsageDay, usageDay, USAGE_LEASE_MS } from '../src/usage.js';
import { createApp } from '../src/server.js';
import { MockConnector } from '../src/connectors/mock.js';
import type { Config } from '../src/config.js';
import { DATABASE_SCHEMA } from '../src/database-schema.js';
import { DatabaseSync } from 'node:sqlite';
import { migrateUsage, migrateUsageCalendar, migrateUsageAdmission } from '../src/usage-schema.js';

async function fixture(options: { now?: number; timezone?: string | null } = {}) {
  const dir = mkdtempSync(join(tmpdir(), 'yahad-usage-synthetic-'));
  const config: Config = { dataDir: dir, masterKey: randomBytes(32), connector: 'mock', realAllowed: false,
    host: '127.0.0.1', port: 3000, origin: 'http://localhost:3000', secureCookie: false,
    maxMedia: 1048576, maxStorage: 64 * 1048576, childIdleMs: 900000, sessionMs: 365 * 86400000, diagnosticsEnabled: false };
  let now = options.now ?? Date.now();
  const store = new Store(config, { usageClock: () => now });
  const f = await store.createFamily('Synthetic', { name: 'parent-usage', password: 'synthetic parent' }, { name: 'child-usage', password: 'synthetic child' });
  const other = await store.createFamily('Other synthetic', { name: 'other-parent-usage', password: 'synthetic parent' }, { name: 'other-child-usage', password: 'synthetic child' });
  const sibling = randomUUID();
  store.run("INSERT INTO users VALUES(?,?,?,?,'child',?)", sibling, f.familyId, 'sibling-usage', 'sibling-usage', 'not-a-password');
  if (options.timezone !== null) store.usage.setTimezone(f.familyId, options.timezone || 'UTC');
  const session = (id: string): Session => {
    const u = store.user(store.one('SELECT * FROM users WHERE id=?', id));
    const s = store.createSession(u);
    return store.session(s.sid)!;
  };
  const child = session(f.childId), parent = session(f.parentId), foreign = session(other.parentId), siblingSession = session(sibling);
  const { app, service } = await createApp(config, { store, boot: false, factory: h => new MockConnector(h) });
  const req = (s: Session, url: string, method: any = 'GET', payload?: any, headers = {}) => app.inject({ method, url, payload,
    headers: { cookie: 'sid=' + s.sid, origin: config.origin, 'x-csrf-token': s.csrf, ...headers } });
  const group = '120363001@g.us';
  store.syncGroups(f.familyId, [{ id: group, title: 'Synthetic group' }]);
  store.grant(f.familyId, child.user.id, group, true);
  store.putMessage(f.familyId, { id: 'sample', groupId: group, text: 'Synthetic only', sender: 'Synthetic', fromMe: false, time: 1, kind: 'text', status: 'delivered', reactions: {} });
  return { store, config, f, other, sibling, child, parent, foreign, siblingSession, session, app, service, req, group,
    get now() { return now; }, at: (value: number) => { now = value; }, advance: (ms: number) => { now += ms; },
    close: async () => { await app.close(); store.close(); rmSync(dir, { recursive: true, force: true }); } };
}

test('children start unlimited; arbitrary, zero and unlimited policies remain independent and parent-owned', async () => {
  const f = await fixture();
  try {
    const path = '/api/parent/usage/' + f.f.childId;
    assert.equal((await f.req(f.parent, path)).json().minutes, null);
    for (const minutes of [37, 1000000, 0, null]) {
      const result = await f.req(f.parent, path, 'PUT', { minutes });
      assert.equal(result.statusCode, 200); assert.equal(result.json().minutes, minutes);
      assert.equal(f.store.usage.status(f.f.familyId, f.sibling).minutes, null);
    }
    for (const minutes of [-1, .5, 1000001, '10']) assert.equal((await f.req(f.parent, path, 'PUT', { minutes })).statusCode, 400);
    assert.equal((await f.req(f.child, path, 'PUT', { minutes: 20 })).statusCode, 403);
    assert.equal((await f.req(f.foreign, path, 'PUT', { minutes: 20 })).statusCode, 403);
    assert.equal((await f.req(f.parent, '/api/parent/usage/' + f.f.parentId)).statusCode, 403);
    assert.equal(f.store.usage.status(f.f.familyId, f.f.childId).minutes, null);
  } finally { await f.close(); }
});

test('leases bill the union across tabs and sessions; reordered, duplicate and stale pauses cannot extend/cancel', async () => {
  const f = await fixture();
  try {
    const u = f.store.usage, second = f.session(f.f.childId), a = u.open(f.child).tab, b = u.open(second).tab;
    u.setPolicy(f.f.familyId, f.f.childId, 37);
    u.heartbeat(f.child, a, 1, true);
    f.advance(4000); u.heartbeat(second, b, 1, true);
    f.advance(1000); u.heartbeat(f.child, a, 3, true);
    f.advance(1000); const stale = u.heartbeat(f.child, a, 2, false);
    assert.equal(stale.leaseUntil, f.now - 1000 + USAGE_LEASE_MS);
    f.advance(1000); const duplicate = u.heartbeat(f.child, a, 3, true);
    assert.equal(duplicate.leaseUntil, stale.leaseUntil);
    u.heartbeat(f.child, a, 4, false);
    f.advance(1000); u.heartbeat(second, b, 2, false);
    f.advance(100000);
    assert.equal(u.status(f.f.familyId, f.f.childId).usedMs, 8000);
    assert.equal(u.status(f.f.familyId, f.sibling).usedMs, 0);
    assert.equal(u.status(f.other.familyId, f.other.childId).usedMs, 0);
    assert.doesNotThrow(() => u.require(f.parent));
    const ledger = JSON.stringify(f.store.all('SELECT * FROM usage_leases'));
    assert.ok(!ledger.includes(f.child.sid));
    assert.ok(!ledger.includes('Synthetic only'));
  } finally { await f.close(); }
});

test('untrusted elapsed fields are rejected; pause halts at server time; crash/network tail is bounded', async () => {
  const f = await fixture();
  try {
    const u = f.store.usage, tab = u.open(f.child).tab;
    u.setPolicy(f.f.familyId, f.f.childId, 3);
    assert.equal((await f.req(f.child, '/api/usage/heartbeat', 'POST', { tab, seq: 1, active: true, elapsed: 1 })).statusCode, 400);
    u.heartbeat(f.child, tab, 1, true); f.advance(5000); u.heartbeat(f.child, tab, 2, false);
    f.advance(60000); assert.equal(u.status(f.f.familyId, f.f.childId).usedMs, 5000);
    u.heartbeat(f.child, tab, 3, true); f.advance(90000);
    assert.equal(u.status(f.f.familyId, f.f.childId).usedMs, 20000);
    assert.throws(() => u.require(f.child), /USAGE_PAUSED/);
    // A new mount gets a new tab, never a client-supplied usage baseline.
    u.heartbeat(f.child, u.open(f.child).tab, 1, true); f.advance(2000);
    assert.equal(u.status(f.f.familyId, f.f.childId).usedMs, 22000);
    const high = f.now; f.at(high - 1);
    assert.throws(() => u.heartbeat(f.child, tab, 4, true), /USAGE_CLOCK/);
    assert.doesNotThrow(() => u.revokeUser(f.f.familyId, f.f.childId), 'revocation remains available during clock rollback');
    assert.equal((await f.req(f.child, '/api/logout', 'POST')).statusCode, 200, 'clock rollback never blocks logout');
    f.at(high); assert.equal(u.status(f.f.familyId, f.f.childId).usedMs, 22000);
  } finally { await f.close(); }
});

test('policy changes settle atomically; today-only bonuses are idempotent and cannot move to a different day', async () => {
  const f = await fixture();
  try {
    const u = f.store.usage, tab = u.open(f.child).tab, id = randomUUID();
    u.setPolicy(f.f.familyId, f.f.childId, 37);
    u.heartbeat(f.child, tab, 1, true); f.advance(5500);
    const zero = u.setPolicy(f.f.familyId, f.f.childId, 0);
    assert.equal(zero.usedMs, 5500); assert.equal(zero.exhausted, true);
    assert.throws(() => u.require(f.child), /USAGE_EXHAUSTED/);
    const status = u.bonus(f.f.familyId, f.f.childId, id, zero.day, 7);
    assert.equal(status.remainingMs, 420000 - 5500);
    const repeat = u.bonus(f.f.familyId, f.f.childId, id, zero.day, 7);
    assert.equal(repeat.bonusMs, 420000);
    assert.throws(() => u.bonus(f.f.familyId, f.f.childId, id, zero.day, 8), /BONUS_CONFLICT/);
    assert.throws(() => u.bonus(f.f.familyId, f.f.childId, randomUUID(), '2000-01-01', 7), /BONUS_DAY_CHANGED/);
    f.at(status.resetAt + 1);
    assert.equal(u.status(f.f.familyId, f.f.childId).bonusMs, 0);
    assert.equal(u.bonus(f.f.familyId, f.f.childId, id, zero.day, 7).bonusMs, 0, 'retry never regrants yesterday bonus');
    assert.equal(f.store.all('SELECT * FROM usage_bonus').length, 1);
  } finally { await f.close(); }
});

test('midnight splits a lease by family timezone; DST creates 23/25-hour days and ignores browser dates', async () => {
  const f = await fixture({ now: Date.parse('2026-03-08T04:59:55Z'), timezone: 'America/New_York' });
  try {
    const zone = 'America/New_York';
    // No earlier policy was inserted; synthetic session expiry remains adequate.
    const u = new UsageBudgets(f.store, () => f.now), s = { ...f.child, expires: Number.MAX_SAFE_INTEGER };
    u.setPolicy(f.f.familyId, f.f.childId, 2);
    u.heartbeat(s, u.open(s).tab, 1, true);
    f.advance(10000);
    const status = u.status(f.f.familyId, f.f.childId);
    assert.equal(status.day, '2026-03-08'); assert.equal(status.usedMs, 5000);
    const rows = f.store.all('SELECT day,used_ms FROM usage_days ORDER BY day');
    assert.deepEqual(rows.map(x => [x.day, x.used_ms]), [['2026-03-07', 5000], ['2026-03-08', 5000]]);
    const spring = Date.parse('2026-03-08T05:00:00Z'), fall = Date.parse('2026-11-01T04:00:00Z');
    assert.equal(nextUsageDay(spring, zone) - spring, 23 * 3600000);
    assert.equal(nextUsageDay(fall, zone) - fall, 25 * 3600000);
    assert.equal(usageDay(spring - 1, zone), '2026-03-07');
  } finally { await f.close(); }
});

test('restart preserves ledger and bonus, settles only bounded tail, and requires a fresh lease', async () => {
  const f = await fixture();
  let reopened: Store | undefined;
  try {
    const u = f.store.usage, tab = u.open(f.child).tab;
    const policy = u.setPolicy(f.f.familyId, f.f.childId, 12);
    u.bonus(f.f.familyId, f.f.childId, randomUUID(), policy.day, 4);
    u.heartbeat(f.child, tab, 1, true); f.advance(60000);
    // A second handle simulates process restart against this synthetic DB only.
    reopened = new Store(f.config, { usageClock: () => f.now });
    const status = reopened.usage.status(f.f.familyId, f.f.childId, f.child.sid);
    assert.equal(status.minutes, 12); assert.equal(status.usedMs, USAGE_LEASE_MS); assert.equal(status.bonusMs, 240000);
    assert.equal(status.leaseUntil, 0); assert.throws(() => reopened!.usage.require(f.child), /USAGE_PAUSED/);
    reopened.usage.heartbeat(f.child, tab, 1, true);
    assert.throws(() => reopened!.usage.require(f.child), /USAGE_PAUSED/, 'pre-restart replay cannot renew');
    reopened.usage.heartbeat(f.child, reopened.usage.open(f.child).tab, 1, true);
    assert.doesNotThrow(() => reopened!.usage.require(f.child));
    f.advance(-1000);
    const rollback = new Store(f.config, { usageClock: () => f.now });
    try {
      assert.throws(() => rollback.usage.require(f.child), /USAGE_CLOCK/);
      assert.doesNotThrow(() => rollback.usage.require(f.parent), 'startup and parent recovery remain available after clock rollback');
      assert.doesNotThrow(() => rollback.usage.pauseSession(f.child.sid));
    } finally { rollback.close(); }
  } finally { reopened?.close(); await f.close(); }
});

test('central HTTP gate blocks content, ranges and side effects with typed 403 while recovery remains available', async () => {
  const f = await fixture();
  try {
    f.store.usage.setPolicy(f.f.familyId, f.f.childId, 0);
    const base = '/api/groups/' + f.group;
    for (const [url, method, body] of [
      ['/api/groups', 'GET'], [base + '/messages', 'GET'], [base + '/search?q=x', 'GET'], [base + '/photo', 'GET'],
      [base + '/history', 'GET'], [base + '/history', 'POST', {}], [base + '/media/nope', 'GET'],
      [base + '/messages', 'POST', { clientId: randomUUID(), text: 'not sent' }],
      [base + '/media', 'POST', {}], [base + '/typing', 'POST', { active: true }],
      [base + '/read', 'POST', { ids: ['sample'] }], [base + '/viewed', 'POST', { ids: ['sample'] }],
      [base + '/messages/sample', 'PATCH', { text: 'not edited' }], [base + '/messages/sample', 'DELETE'],
      [base + '/messages/sample/reaction', 'POST', { emoji: '' }],
    ] as const) {
      const result = await f.req(f.child, url, method, body, { range: 'bytes=0-3' });
      assert.equal(result.statusCode, 403, `${method} ${url}`); assert.equal(result.json().error, 'USAGE_EXHAUSTED');
    }
    assert.equal((await f.req(f.child, '/api/me')).statusCode, 200);
    const usage = await f.req(f.child, '/api/usage'); assert.equal(usage.statusCode, 200);
    assert.equal((await f.req(f.child, '/api/activity', 'POST')).statusCode, 200);
    assert.equal((await f.req(f.parent, '/api/groups')).statusCode, 200);
    const bonus = await f.req(f.parent, '/api/parent/usage/' + f.f.childId + '/bonus', 'POST', { requestId: randomUUID(), day: usage.json().day, minutes: 4 });
    assert.equal(bonus.statusCode, 200);
    assert.equal((await f.req(f.child, '/api/groups')).json().error, 'USAGE_PAUSED');
    const heartbeat = await f.req(f.child, '/api/usage/heartbeat', 'POST', { tab: f.store.usage.open(f.child).tab, seq: 1, active: true });
    assert.equal(heartbeat.statusCode, 200);
    assert.equal((await f.req(f.child, '/api/groups')).statusCode, 200);
    assert.equal((await f.req(f.child, '/api/logout', 'POST')).statusCode, 200);
    assert.equal(f.store.usage.status(f.f.familyId, f.f.childId, f.child.sid).leaseUntil, 0);
  } finally { await f.close(); }
});

test('expiry is precise and cannot bill more than quota; bounded late heartbeat cannot revive yesterday bonus', async () => {
  const f = await fixture();
  try {
    const u = f.store.usage, tab = u.open(f.child).tab; u.setPolicy(f.f.familyId, f.f.childId, 1);
    for (let seq = 1; seq <= 12; seq++) { u.heartbeat(f.child, tab, seq, true); f.advance(5000); }
    const status = u.status(f.f.familyId, f.f.childId); assert.equal(status.usedMs, 60000); assert.equal(status.exhausted, true);
    assert.throws(() => u.require(f.child), /USAGE_EXHAUSTED/);
    f.advance(100000); assert.equal(u.status(f.f.familyId, f.f.childId).usedMs, 60000);
  } finally { await f.close(); }
});

test('schema and local recovery/package contract agree; UI preserves mounted drafts through quota suspension', () => {
  assert.equal(DATABASE_SCHEMA, 6);
  const recovery = readFileSync(new URL('../scripts/recover-account.py', import.meta.url), 'utf8');
  assert.match(recovery, /DATABASE_SCHEMA = 6/);
  const release = readFileSync(new URL('../scripts/release.mjs', import.meta.url), 'utf8');
  assert.match(release, /databaseSchema:DATABASE_SCHEMA/);
  const main = readFileSync(new URL('../web/main.tsx', import.meta.url), 'utf8');
  assert.match(main, /const verifyingSelection = usageSuspended \|\| !groupsVerified/);
  assert.match(main, /suspended=\{!activeSelected\}/);
  assert.match(main, /key=\{composerGroup.id\}/);
  assert.match(main, /inert=\{usageSuspended\}/);
  const hook = readFileSync(new URL('../web/useUsageBudget.ts', import.meta.url), 'utf8');
  assert.ok(!/localStorage|sessionStorage/.test(hook));
});

test('async photo and attachment-marker sends recheck the budget before later disclosure or side effects', async () => {
  const f = await fixture();
  try {
    const u = f.store.usage;
    let begin!: () => void, release!: () => void;
    let started = new Promise<void>(r => begin = r), pending = new Promise<void>(r => release = r);
    f.service.groupPhoto = async () => { begin(); await pending; return { bytes: Buffer.alloc(16), mime: 'image/png' }; };
    u.setPolicy(f.f.familyId, f.f.childId, 37); u.heartbeat(f.child, u.open(f.child).tab, 1, true);
    const photo = f.req(f.child, '/api/groups/' + f.group + '/photo'); const photoResult = photo.then(r => r);
    await started; u.setPolicy(f.f.familyId, f.f.childId, 0); release();
    assert.equal((await photoResult).json().error, 'USAGE_EXHAUSTED');
    await f.service.connect(f.f.familyId);
    u.setPolicy(f.f.familyId, f.f.childId, 37); u.heartbeat(f.child, u.open(f.child).tab, 1, true);
    f.store.setIdentityLabel(f.f.familyId, f.f.childId, 'Synthetic signature');
    let sends = 0;
    started = new Promise<void>(r => begin = r); pending = new Promise<void>(r => release = r);
    const connector = f.service.connector(f.f.familyId), original = connector.send.bind(connector);
    connector.send = async input => { sends++; begin(); await pending; return original(input); };
    const boundary = 'synthetic-usage-boundary', clientId = randomUUID();
    const bytes = readFileSync(new URL('../fixtures/flower.webp', import.meta.url));
    const payload = Buffer.concat([Buffer.from(`--${boundary}\r\nContent-Disposition: form-data; name="clientId"\r\n\r\n${clientId}\r\n--${boundary}\r\nContent-Disposition: form-data; name="sticker"\r\n\r\ntrue\r\n--${boundary}\r\nContent-Disposition: form-data; name="file"; filename="synthetic.webp"\r\nContent-Type: image/webp\r\n\r\n`), bytes, Buffer.from(`\r\n--${boundary}--\r\n`)]);
    const upload = f.req(f.child, '/api/groups/' + f.group + '/media', 'POST', payload, { 'content-type': 'multipart/form-data; boundary=' + boundary });
    const result = upload.then(r => r); await started;
    u.setPolicy(f.f.familyId, f.f.childId, 0); release();
    const response = await result;
    assert.equal(response.statusCode, 403); assert.equal(response.json().error, 'USAGE_EXHAUSTED');
    assert.equal(sends, 1, 'only the already-started signature marker may complete; attachment cannot start after expiry');
    assert.equal(f.store.one('SELECT COUNT(*) AS n FROM media WHERE family_id=?', f.f.familyId).n, 0);
  } finally { await f.close(); }
});

test('exhausted SSE remains available for quota controls but omits group invalidations', async () => {
  const f = await fixture();
  try {
    f.store.usage.setPolicy(f.f.familyId, f.f.childId, 0);
    const stream = f.req(f.child, '/api/events').then(r => r);
    for (let i = 0; i < 100 && !f.service.listeners.size; i++) await new Promise(resolve => setImmediate(resolve));
    assert.equal(f.service.listeners.size, 1);
    f.service.notify(f.f.familyId, f.group);
    f.service.notify(f.other.familyId, undefined, 'usage');
    f.service.notify(f.f.familyId, undefined, 'usage');
    await f.req(f.child, '/api/logout', 'POST');
    const response = await stream;
    assert.match(response.body, /event: ready/); assert.match(response.body, /event: usage/); assert.match(response.body, /event: expired/);
    assert.ok(!response.body.includes(f.group));
    assert.equal((response.body.match(/event: usage/g) || []).length, 1);
  } finally { await f.close(); }
});

test('schema-2 to 3 DDL and version bump roll back together after a synthetic failure', () => {
  const db = new DatabaseSync(':memory:');
  try {
    db.exec(`PRAGMA foreign_keys=ON; CREATE TABLE schema_version(version INTEGER); INSERT INTO schema_version VALUES(2);
      CREATE TABLE families(id TEXT PRIMARY KEY); CREATE TABLE users(family_id TEXT,id TEXT,PRIMARY KEY(family_id,id));
      CREATE TRIGGER synthetic_failure BEFORE UPDATE ON schema_version BEGIN SELECT RAISE(ABORT,'synthetic migration failure'); END;`);
    assert.throws(() => migrateUsage(db), /synthetic migration failure/);
    assert.equal(db.prepare('SELECT version FROM schema_version').get()?.version, 2);
    assert.equal(db.prepare("SELECT 1 FROM sqlite_master WHERE name='usage_policy'").get(), undefined);
    db.exec('DROP TRIGGER synthetic_failure'); migrateUsage(db);
    assert.equal(db.prepare('SELECT version FROM schema_version').get()?.version, 3);
    assert.deepEqual(db.prepare('PRAGMA foreign_key_check').all(), []);
  } finally { db.close(); }
});

test('session cleanup settles before discarding orphan leases; graceful shutdown stops charging immediately', async () => {
  const f = await fixture();
  try {
    f.store.usage.setPolicy(f.f.familyId, f.f.childId, 37);
    f.store.usage.heartbeat(f.child, f.store.usage.open(f.child).tab, 1, true); f.advance(5000);
    await f.app.close(); f.advance(60000);
    assert.equal(f.store.usage.status(f.f.familyId, f.f.childId).usedMs, 5000);
    f.store.usage.heartbeat(f.child, f.store.usage.open(f.child).tab, 1, true); f.advance(5000);
    f.store.run('DELETE FROM sessions'); f.store.usage.cleanExpiredSessions();
    assert.equal(f.store.usage.status(f.f.familyId, f.f.childId).usedMs, 10000);
    assert.equal(f.store.all('SELECT * FROM usage_leases').length, 0);
  } finally { await f.close(); }
});

test('unconfirmed families remain unlimited and unmetered until an explicit parent timezone choice', async () => {
  const f = await fixture({ timezone: null });
  try {
    const u = f.store.usage, tab = u.open(f.child).tab;
    assert.equal(u.status(f.f.familyId, f.f.childId).timezoneConfirmed, false);
    u.heartbeat(f.child, tab, 1, true); f.advance(10000);
    assert.equal(u.status(f.f.familyId, f.f.childId).usedMs, 0);
    assert.equal(f.store.all('SELECT * FROM usage_days').length, 0);
    assert.doesNotThrow(() => u.require(f.child), 'unconfigured existing unlimited children are not locked out');
    assert.throws(() => u.setPolicy(f.f.familyId, f.f.childId, 37), /USAGE_TIMEZONE_REQUIRED/);
    const path = '/api/parent/usage-timezone';
    assert.equal((await f.req(f.child, path, 'PUT', { timezone: 'Asia/Jerusalem' })).statusCode, 403);
    assert.equal((await f.req(f.parent, path, 'PUT', { timezone: 'Asia/Jerusalem' })).statusCode, 200);
    const status = u.setPolicy(f.f.familyId, f.f.childId, 37);
    assert.equal(status.timezoneConfirmed, true); assert.equal(status.timezone, 'Asia/Jerusalem');
    u.heartbeat(f.child, tab, 2, true); f.advance(5000);
    assert.equal(u.status(f.f.familyId, f.f.childId).usedMs, 5000, 'no retroactive metering under an implicit UTC day');
  } finally { await f.close(); }
});

test('timezone changes extend the current period to a future target midnight without early reset or bonus replay', async () => {
  const f = await fixture({ now: Date.parse('2026-10-28T12:00:00Z') });
  let reopened: Store | undefined;
  try {
    const u = f.store.usage, tab = u.open(f.child).tab;
    const original = u.setPolicy(f.f.familyId, f.f.childId, 1), bonusId = randomUUID();
    u.bonus(f.f.familyId, f.f.childId, bonusId, original.day, 7, original.dayKey);
    u.heartbeat(f.child, tab, 1, true); f.advance(5000);
    u.setTimezone(f.f.familyId, 'America/New_York');
    const scheduled = u.status(f.f.familyId, f.f.childId);
    assert.equal(scheduled.pendingTimezone, 'America/New_York');
    assert.equal(scheduled.timezoneChangesAt, Date.parse('2026-10-29T04:00:00Z'));
    u.setTimezone(f.f.familyId, 'America/New_York');
    assert.equal(u.status(f.f.familyId, f.f.childId).timezoneChangesAt, scheduled.timezoneChangesAt, 'lost-response retry cannot extend or advance the scheduled reset');
    f.at(Date.parse('2026-10-29T00:00:01Z'));
    const extended = u.status(f.f.familyId, f.f.childId);
    assert.equal(extended.dayKey, original.dayKey); assert.equal(extended.bonusMs, 420000);
    assert.equal(extended.usedMs, 15000, 'only bounded original lease tail is settled');
    f.at(scheduled.timezoneChangesAt! - 5000); u.heartbeat(f.child, tab, 2, true); f.advance(10000);
    const changed = u.status(f.f.familyId, f.f.childId);
    assert.equal(changed.timezone, 'America/New_York'); assert.equal(changed.pendingTimezone, null);
    assert.equal(changed.dayKey, '1:2026-10-29'); assert.equal(changed.usedMs, 5000); assert.equal(changed.bonusMs, 0);
    assert.equal(u.bonus(f.f.familyId, f.f.childId, bonusId, original.day, 7, original.dayKey).bonusMs, 0);
    assert.throws(() => u.bonus(f.f.familyId, f.f.childId, randomUUID(), changed.day, 1), /BONUS_DAY_CHANGED/, 'calendar era must be bound even when a visible date repeats');
    u.bonus(f.f.familyId, f.f.childId, randomUUID(), changed.day, 1, changed.dayKey);
    reopened = new Store(f.config, { usageClock: () => f.now });
    assert.equal(reopened.usage.status(f.f.familyId, f.f.childId).dayKey, changed.dayKey);
    assert.equal(reopened.usage.status(f.f.familyId, f.f.childId).bonusMs, 60000);
    u.setTimezone(f.f.familyId, 'Asia/Jerusalem');
    const again = u.status(f.f.familyId, f.f.childId);
    assert.equal(again.dayKey, changed.dayKey); assert.equal(again.pendingTimezone, 'Asia/Jerusalem');
    assert.ok(again.timezoneChangesAt! >= nextUsageDay(f.now, 'America/New_York'));
  } finally { reopened?.close(); await f.close(); }
});

test('an exhausted child cannot regain time at the old midnight while a calendar transition is pending', async () => {
  const f = await fixture({ now: Date.parse('2026-10-28T12:00:00Z') });
  try {
    const u = f.store.usage, tab = u.open(f.child).tab; u.setPolicy(f.f.familyId, f.f.childId, 1);
    for (let seq = 1; seq <= 12; seq++) { u.heartbeat(f.child, tab, seq, true); f.advance(5000); }
    u.setTimezone(f.f.familyId, 'America/New_York'); const before = u.status(f.f.familyId, f.f.childId);
    f.at(Date.parse('2026-10-29T00:00:01Z'));
    assert.throws(() => u.require(f.child), /USAGE_EXHAUSTED/);
    f.at(before.timezoneChangesAt!);
    assert.equal(u.status(f.f.familyId, f.f.childId).remainingMs, 60000);
    assert.throws(() => u.require(f.child), /USAGE_PAUSED/);
    u.heartbeat(f.child, tab, 13, true); assert.doesNotThrow(() => u.require(f.child));
  } finally { await f.close(); }
});

test('legacy schema-3 quota data requires a visible choice and retains usage when the parent chooses Jerusalem', async () => {
  const f = await fixture({ now: Date.parse('2026-10-28T12:00:00Z'), timezone: null });
  try {
    const u = f.store.usage, status = u.status(f.f.familyId, f.f.childId);
    f.store.run('UPDATE usage_policy SET minutes=37 WHERE family_id=? AND child_id=?', f.f.familyId, f.f.childId);
    f.store.run('INSERT INTO usage_days VALUES(?,?,?,?,?)', f.f.familyId, f.f.childId, status.day, 12000, 60000);
    assert.throws(() => u.require(f.child), /USAGE_TIMEZONE_REQUIRED/);
    u.setTimezone(f.f.familyId, 'Asia/Jerusalem'); const selected = u.status(f.f.familyId, f.f.childId);
    assert.equal(selected.timezoneConfirmed, true); assert.equal(selected.pendingTimezone, 'Asia/Jerusalem');
    assert.equal(selected.usedMs, 12000); assert.equal(selected.bonusMs, 60000);
    assert.ok(selected.timezoneChangesAt! > f.now);
  } finally { await f.close(); }
});

test('schema-3 to 4 calendar DDL and version bump are atomic', () => {
  const db = new DatabaseSync(':memory:');
  try {
    db.exec(`CREATE TABLE schema_version(version INTEGER); INSERT INTO schema_version VALUES(3); CREATE TABLE families(id TEXT PRIMARY KEY);
      CREATE TRIGGER synthetic_failure BEFORE UPDATE ON schema_version BEGIN SELECT RAISE(ABORT,'synthetic calendar failure'); END;`);
    assert.throws(() => migrateUsageCalendar(db), /synthetic calendar failure/);
    assert.equal(db.prepare('SELECT version FROM schema_version').get()?.version, 3);
    assert.equal(db.prepare("SELECT 1 FROM sqlite_master WHERE name='usage_calendar'").get(), undefined);
    db.exec('DROP TRIGGER synthetic_failure'); migrateUsageCalendar(db);
    assert.equal(db.prepare('SELECT version FROM schema_version').get()?.version, 4);
  } finally { db.close(); }
});

test('64 sequential remounts reuse bounded slots without relogin; retired heartbeats never recreate rows', async () => {
  const f = await fixture();
  try {
    const u = f.store.usage; u.setPolicy(f.f.familyId, f.f.childId, 37);
    const retired: string[] = [];
    for (let mount = 0; mount < 64; mount++) {
      const opened = await f.req(f.child, '/api/usage/open', 'POST', {});
      assert.equal(opened.statusCode, 200, `mount ${mount + 1}`);
      const { tab } = opened.json();
      assert.ok(!retired.includes(tab));
      assert.equal((await f.req(f.child, '/api/usage/heartbeat', 'POST', { tab, seq: 1, active: true })).statusCode, 200);
      f.advance(1000); u.heartbeat(f.child, tab, 2, false); retired.push(tab);
      assert.ok(f.store.one('SELECT COUNT(*) AS n FROM usage_leases').n <= 32);
    }
    const current = u.open(f.child).tab;
    assert.equal(u.status(f.f.familyId, f.f.childId, f.child.sid).leaseUntil, 0, 'admission alone grants no content lease');
    for (const tab of retired) for (const active of [true, false]) {
      const replay = await f.req(f.child, '/api/usage/heartbeat', 'POST', { tab, seq: 999999, active });
      assert.equal(replay.statusCode, 403); assert.equal(replay.json().error, 'USAGE_TAB_EXPIRED');
    }
    f.advance(5000);
    const status = u.status(f.f.familyId, f.f.childId);
    assert.equal(status.usedMs, 64000); assert.equal(status.remainingMs, 37 * 60000 - 64000);
    assert.equal(f.store.one('SELECT COUNT(*) AS n FROM usage_leases').n, 1);
    u.heartbeat(f.child, current, 1, true); assert.doesNotThrow(() => u.require(f.child));
  } finally { await f.close(); }
});

test('32 concurrent tab leases enforce capacity; pausing or expiry frees capacity without stale revival', async () => {
  const f = await fixture();
  try {
    const u = f.store.usage; u.setPolicy(f.f.familyId, f.f.childId, 37);
    const tabs = Array.from({ length: 32 }, () => { const { tab } = u.open(f.child); u.heartbeat(f.child, tab, 1, true); return tab; });
    const blocked = await f.req(f.child, '/api/usage/open', 'POST', {});
    assert.equal(blocked.statusCode, 403); assert.equal(blocked.json().error, 'USAGE_TAB_LIMIT');
    f.advance(1000); u.heartbeat(f.child, tabs[1], 2, true);
    u.heartbeat(f.child, tabs[0], 2, false);
    const replacement = u.open(f.child).tab; u.heartbeat(f.child, replacement, 1, true);
    assert.throws(() => u.heartbeat(f.child, tabs[0], 9999, true), /USAGE_TAB_EXPIRED/);
    assert.throws(() => u.heartbeat(f.siblingSession, replacement, 2, true), /USAGE_TAB_EXPIRED/);
    const second = f.session(f.f.childId);
    assert.throws(() => u.heartbeat(second, replacement, 2, true), /USAGE_TAB_EXPIRED/);
    const own = u.open(second).tab; u.heartbeat(second, own, 1, true);
    f.advance(1000); u.heartbeat(f.child, replacement, 3, false);
    u.heartbeat(f.child, replacement, 2, true);
    assert.equal(f.store.one('SELECT until FROM usage_leases WHERE tab=?', replacement).until, 0, 'retained sequence rejects reordered activation');
    f.advance(16000); const fresh = u.open(f.child).tab;
    u.heartbeat(f.child, fresh, 1, true);
    assert.equal(u.status(f.f.familyId, f.f.childId).usedMs, 16000, 'concurrent windows bill only union');
    assert.ok(f.store.one('SELECT COUNT(*) AS n FROM usage_leases WHERE session_hash=(SELECT session_hash FROM usage_leases WHERE tab=?)', fresh).n <= 32);
  } finally { await f.close(); }
});

test('lost admissions reserve bounded slots briefly but cannot bill or grant access, including across restart', async () => {
  const f = await fixture(); let reopened: Store | undefined;
  try {
    const u = f.store.usage; u.setPolicy(f.f.familyId, f.f.childId, 37);
    assert.equal((await f.req(f.parent, '/api/usage/open', 'POST', {})).statusCode, 403);
    assert.equal((await f.req(f.child, '/api/usage/open', 'POST', { tab: randomUUID() })).statusCode, 400);
    const pending = Array.from({ length: 32 }, () => u.open(f.child).tab);
    assert.throws(() => u.open(f.child), /USAGE_TAB_LIMIT/);
    assert.throws(() => u.require(f.child), /USAGE_PAUSED/);
    f.advance(15000);
    assert.equal(u.status(f.f.familyId, f.f.childId).usedMs, 0);
    assert.throws(() => u.heartbeat(f.child, pending[0], 1, true), /USAGE_TAB_EXPIRED/);
    const next = u.open(f.child).tab;
    reopened = new Store(f.config, { usageClock: () => f.now });
    assert.throws(() => reopened!.usage.heartbeat(f.child, next, 1, true), /USAGE_TAB_EXPIRED/);
    for (let mount = 0; mount < 40; mount++) {
      const tab = reopened.usage.open(f.child).tab;
      reopened.usage.heartbeat(f.child, tab, 1, true); f.advance(1000);
      reopened.usage.heartbeat(f.child, tab, 2, false);
    }
    assert.equal(reopened.usage.status(f.f.familyId, f.f.childId).usedMs, 40000);
  } finally { reopened?.close(); await f.close(); }
});

test('schema-4 to 5 admission policy boundary advances atomically without rewriting leases', () => {
  const db = new DatabaseSync(':memory:');
  try {
    db.exec(`CREATE TABLE schema_version(version INTEGER); INSERT INTO schema_version VALUES(4);
      CREATE TABLE usage_leases(tab TEXT,seq INTEGER,until INTEGER); INSERT INTO usage_leases VALUES('retained',77,1234);
      CREATE TRIGGER synthetic_failure BEFORE UPDATE ON schema_version BEGIN SELECT RAISE(ABORT,'synthetic admission failure'); END;`);
    assert.throws(() => migrateUsageAdmission(db), /synthetic admission failure/);
    assert.equal(db.prepare('SELECT version FROM schema_version').get()?.version, 4);
    db.exec('DROP TRIGGER synthetic_failure'); migrateUsageAdmission(db);
    assert.equal(db.prepare('SELECT version FROM schema_version').get()?.version, 5);
    assert.deepEqual({ ...db.prepare('SELECT * FROM usage_leases').get() }, { tab: 'retained', seq: 77, until: 1234 });
  } finally { db.close(); }
});
