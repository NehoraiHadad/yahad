import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { randomBytes, randomUUID } from 'node:crypto';
import { DatabaseSync } from 'node:sqlite';
import { Store } from '../src/store.js';
import { createApp } from '../src/server.js';
import { migrateChildAccess } from '../src/child-access.js';
import type { Config } from '../src/config.js';

async function fixture() {
  const dir = mkdtempSync(join(tmpdir(), 'yahad-child-access-synthetic-'));
  const config: Config = { dataDir: dir, masterKey: randomBytes(32), connector: 'mock', realAllowed: false,
    host: '127.0.0.1', port: 3000, origin: 'http://localhost:3000', secureCookie: false,
    maxMedia: 1048576, maxStorage: 64 * 1048576, childIdleMs: 900000, sessionMs: 3600000, diagnosticsEnabled: false };
  let now = Date.now();
  const store = new Store(config, { usageClock: () => now }), password = 'synthetic child access passphrase';
  const family = await store.createFamily('Synthetic A', { name: 'parent-a', password }, { name: 'child-a', password });
  const other = await store.createFamily('Synthetic B', { name: 'parent-b', password }, { name: 'child-b', password });
  const siblingId = randomUUID();
  store.run("INSERT INTO users VALUES(?,?,?,?,'child',?)", siblingId, family.familyId, 'sibling', 'sibling', 'synthetic');
  const session = (id: string) => {
    const s = store.createSession(store.user(store.one('SELECT * FROM users WHERE id=?', id)));
    return store.session(s.sid)!;
  };
  const parent = session(family.parentId), child = session(family.childId), second = session(family.childId), sibling = session(siblingId), foreign = session(other.parentId);
  const { app, service } = await createApp(config, { store, boot: false });
  await service.connect(family.familyId);
  const group = '120363001@g.us';
  await service.grant(family.familyId, family.childId, group, true);
  store.usage.setTimezone(family.familyId, 'UTC');
  const req = (s: typeof parent | null, url: string, method: any = 'GET', payload?: any, headers = {}) =>
    app.inject({ method, url, payload, headers: { origin: config.origin,
      ...(s ? { cookie: 'sid=' + s.sid, 'x-csrf-token': s.csrf } : {}), ...headers } });
  const access = (paused: boolean, revision: number, s: typeof parent | null = parent, childId = family.childId, headers = {}) =>
    req(s, '/api/parent/child-access', 'PUT', { childId, paused, revision }, headers);
  const login = () => req(null, '/api/login', 'POST', { name: 'child-a', password });
  return { app, service, store, config, family, other, parent, child, second, sibling, foreign, session, req, access, login, group,
    advance(ms: number) { now += ms; },
    async close() { app.server.closeAllConnections(); await app.close(); store.close(); rmSync(dir, { recursive: true, force: true }); } };
}

test('pause revokes all child sessions, blocks login and APIs, preserves settings and siblings; resume needs a new login', async () => {
  const f = await fixture();
  try {
    const original = f.store.one('SELECT * FROM users WHERE id=?', f.family.childId);
    const grants = f.store.all('SELECT * FROM grants');
    f.store.setIdentityLabel(f.family.familyId, f.family.childId, 'Synthetic signature');
    f.store.usage.setPolicy(f.family.familyId, f.family.childId, 37);
    const tab = f.store.usage.open(f.child).tab;
    f.store.usage.heartbeat(f.child, tab, 1, true); f.advance(4500);
    const before = f.store.usage.status(f.family.familyId, f.family.childId);
    assert.equal((await f.access(true, 0)).statusCode, 200);
    assert.deepEqual(f.store.childAccess(f.family.familyId, f.family.childId), { paused: true, revision: 1 });
    assert.equal(f.store.session(f.child.sid), undefined); assert.equal(f.store.session(f.second.sid), undefined);
    assert.ok(f.store.session(f.parent.sid)); assert.ok(f.store.session(f.sibling.sid));
    assert.equal(f.store.allowed(f.child.user, f.group), false);
    for (const [url, method, payload] of [
      ['/api/me', 'GET'], ['/api/groups', 'GET'], ['/api/groups/' + f.group + '/messages', 'POST', { text: 'blocked', clientId: randomUUID() }],
      ['/api/usage', 'GET'], ['/api/usage/open', 'POST', {}], ['/api/usage/heartbeat', 'POST', { tab, seq: 2, active: true }], ['/api/events', 'GET'],
    ] as const) assert.equal((await f.req(f.child, url, method, payload)).statusCode, 401, url);
    const denied = await f.login(); assert.equal(denied.statusCode, 403); assert.equal(denied.json().error, 'CHILD_ACCESS_PAUSED');
    assert.throws(() => f.session(f.family.childId), /CHILD_ACCESS_PAUSED/);
    f.advance(60000);
    const paused = f.store.usage.status(f.family.familyId, f.family.childId);
    for (const key of ['minutes', 'usedMs', 'bonusMs', 'remainingMs', 'dayKey'] as const) assert.equal(paused[key], before[key]);
    assert.deepEqual(f.store.one('SELECT * FROM users WHERE id=?', f.family.childId), original);
    assert.deepEqual(f.store.all('SELECT * FROM grants'), grants);
    assert.equal(f.store.identityLabel(f.family.familyId, f.family.childId), 'Synthetic signature');
    const overview = (await f.req(f.parent, '/api/parent/overview')).json();
    assert.equal(overview.children.find((c: any) => c.id === f.family.childId).paused, true);
    assert.equal(overview.children.find((c: any) => c.id === f.sibling.user.id).paused, false);
    assert.equal((await f.access(false, 1)).statusCode, 200);
    assert.equal((await f.req(f.child, '/api/me')).statusCode, 401, 'resume never revives an old cookie');
    assert.equal((await f.login()).statusCode, 200, 'same password remains valid');
    const fresh = f.session(f.family.childId), newTab = f.store.usage.open(fresh).tab;
    f.store.usage.heartbeat(fresh, newTab, 1, true); f.advance(1000);
    assert.equal(f.store.usage.status(f.family.familyId, f.family.childId).usedMs, before.usedMs + 1000);
    assert.equal((await f.access(false, 1)).statusCode, 200, 'replayed resume is idempotent');
    assert.ok(f.store.session(fresh.sid), 'replayed resume must not revoke new sessions');
    assert.ok(f.store.usage.status(f.family.familyId, f.family.childId, fresh.sid).leaseUntil > 0, 'or cancel a fresh usage lease');
    assert.equal((await f.access(true, 0)).statusCode, 409, 'old pause cannot undo a later resume');
    assert.equal(f.store.childAccess(f.family.familyId, f.family.childId).paused, false);
  } finally { await f.close(); }
});

test('only an authenticated parent in the same family with CSRF can change a child; input is strict', async () => {
  const f = await fixture();
  try {
    for (const [s, status] of [[null, 401], [f.child, 403], [f.foreign, 404]] as const)
      assert.equal((await f.access(true, 0, s)).statusCode, status);
    for (const childId of [f.other.childId, f.family.parentId, randomUUID()])
      assert.equal((await f.access(true, 0, f.parent, childId)).statusCode, 404);
    assert.equal((await f.access(true, 0, f.parent, f.family.childId, { 'x-csrf-token': 'bad' })).statusCode, 403);
    assert.equal((await f.access(true, 0, f.parent, f.family.childId, { origin: 'https://invalid.example' })).statusCode, 403);
    for (const extra of [{ paused: 'true' }, { revision: -1 }, { revision: .5 }, { revision: Number.MAX_SAFE_INTEGER }, { password: 'new' }, { familyId: f.other.familyId }]) {
      assert.equal((await f.req(f.parent, '/api/parent/child-access', 'PUT', { childId: f.family.childId, paused: true, revision: 0, ...extra })).statusCode, 400);
    }
    assert.ok(f.store.session(f.child.sid)); assert.deepEqual(f.store.childAccess(f.family.familyId, f.family.childId), { paused: false, revision: 0 });
  } finally { await f.close(); }
});

test('pause/login race, including pause then resume during password verification, cannot mint a stale session', async () => {
  for (const resume of [false, true]) {
    const f = await fixture();
    try {
      let began!: () => void;
      const started = new Promise<void>(resolve => { began = resolve; });
      const one = f.store.one.bind(f.store);
      f.store.one = (sql: string, ...args: any[]) => { const row = one(sql, ...args); if (sql === 'SELECT * FROM users WHERE name_key=?') began(); return row; };
      const pending = f.login(); await started;
      f.store.setChildAccess(f.family.familyId, f.family.childId, true, 0);
      if (resume) f.store.setChildAccess(f.family.familyId, f.family.childId, false, 1);
      const response = await pending;
      assert.equal(response.statusCode, resume ? 401 : 403);
      assert.equal(response.headers['set-cookie'], undefined);
      assert.equal(f.store.one('SELECT count(*) AS n FROM sessions WHERE user_id=?', f.family.childId).n, 0);
    } finally { await f.close(); }
  }
});

test('an in-flight content response cannot disclose data after a pause, even if access resumes before it finishes', async () => {
  const f = await fixture();
  try {
    let began!: () => void, release!: () => void;
    const started = new Promise<void>(resolve => { began = resolve; }), held = new Promise<void>(resolve => { release = resolve; });
    f.app.addHook('preSerialization', async (req, _reply, payload) => {
      if (req.url === '/api/groups') { began(); await held; }
      return payload;
    });
    const pending = f.req(f.child, '/api/groups').then(response => response);
    await started;
    assert.equal((await f.access(true, 0)).statusCode, 200);
    assert.equal((await f.access(false, 1)).statusCode, 200);
    release();
    const response = await pending;
    assert.equal(response.statusCode, 401); assert.equal(response.json().error, 'SIGN_IN_REQUIRED');
    assert.ok(!response.body.includes(f.group));
  } finally { await f.close(); }
});

test('pause rolls back usage, sessions and policy together on storage failure; retries and clock rollback work', async () => {
  const f = await fixture();
  try {
    f.store.usage.setPolicy(f.family.familyId, f.family.childId, 1);
    const tab = f.store.usage.open(f.child).tab; f.store.usage.heartbeat(f.child, tab, 1, true); f.advance(3000);
    const rows = f.store.all('SELECT * FROM usage_leases');
    f.store.db.exec("CREATE TRIGGER synthetic_failure BEFORE INSERT ON child_access BEGIN SELECT RAISE(ABORT,'synthetic failure'); END;");
    assert.equal((await f.access(true, 0)).statusCode, 500);
    assert.deepEqual(f.store.all('SELECT * FROM usage_leases'), rows); assert.ok(f.store.session(f.child.sid));
    assert.equal(f.store.childAccess(f.family.familyId, f.family.childId).paused, false);
    f.store.db.exec('DROP TRIGGER synthetic_failure');
    f.store.usage.status(f.family.familyId, f.family.childId);
    f.advance(-1);
    assert.equal((await f.access(true, 0)).statusCode, 200);
    assert.equal((await f.access(true, 0)).statusCode, 200);
    f.advance(1);
    assert.equal(f.store.usage.status(f.family.familyId, f.family.childId).usedMs, 3000);
  } finally { await f.close(); }
});

test('usage requests authenticated before pause cannot create or renew leases after pause or resume', async () => {
  for (const operation of ['open', 'heartbeat']) for (const resume of [false, true]) {
    const f = await fixture();
    try {
      const u = f.store.usage; u.setPolicy(f.family.familyId, f.family.childId, 37);
      const tab = u.open(f.child).tab; u.heartbeat(f.child, tab, 1, true); f.advance(4000);
      const path = '/api/usage/' + operation;
      let began!: () => void, release!: () => void;
      const started = new Promise<void>(resolve => { began = resolve; }), held = new Promise<void>(resolve => { release = resolve; });
      f.app.addHook('preHandler', async req => { if (req.url === path) { began(); await held; } });
      const request = f.req(f.child, path, 'POST', operation === 'open' ? {} : { tab, seq: 2, active: true }).then(response => response);
      await started;
      assert.equal((await f.access(true, 0)).statusCode, 200);
      if (resume) assert.equal((await f.access(false, 1)).statusCode, 200);
      release();
      const response = await request;
      assert.equal(response.statusCode, 401, `${operation}; resume=${resume}`);
      assert.equal(response.json().error, 'SIGN_IN_REQUIRED');
      assert.equal(f.store.one('SELECT count(*) AS n FROM usage_leases').n, 1, 'stale admission cannot allocate a new row');
      assert.equal(f.store.one('SELECT MAX(until) AS n FROM usage_leases').n, 0);
      f.advance(5000);
      assert.equal(u.status(f.family.familyId, f.family.childId).usedMs, 4000, 'paused time is never billed by a delayed heartbeat');
    } finally { await f.close(); }
  }
});

test('a paused session cannot begin provider submission after delayed connector preparation, including after resume', async () => {
  for (const resume of [false, true]) {
    const f = await fixture();
    try {
      const c = f.service.connector(f.family.familyId), original = c.send.bind(c);
      let began!: () => void, release!: () => void, submitted = 0;
      const started = new Promise<void>(resolve => { began = resolve; }), held = new Promise<void>(resolve => { release = resolve; });
      c.send = async message => { began(); await held; message.beforeSend?.(); submitted++; return original(message); };
      const request = f.req(f.child, '/api/groups/' + f.group + '/messages', 'POST', { text: 'Synthetic delayed send', clientId: randomUUID() }).then(response => response);
      await started;
      assert.equal((await f.access(true, 0)).statusCode, 200);
      if (resume) assert.equal((await f.access(false, 1)).statusCode, 200);
      release();
      assert.equal((await request).statusCode, 502, 'existing conservative send-uncertain response is preserved');
      assert.equal(submitted, 0, 'no provider submission is allowed for the revoked originating session');
      assert.equal(f.store.session(f.child.sid), undefined);
    } finally { await f.close(); }
  }
});

test('time exhaustion stays exhausted through pause/resume, and a parent bonus does not bypass a pause', async () => {
  const f = await fixture();
  try {
    f.store.usage.setPolicy(f.family.familyId, f.family.childId, 0);
    assert.equal((await f.access(true, 0)).statusCode, 200); assert.equal((await f.access(false, 1)).statusCode, 200);
    const fresh = f.session(f.family.childId);
    assert.equal(f.store.usage.status(f.family.familyId, f.family.childId).exhausted, true);
    assert.equal((await f.req(fresh, '/api/groups')).json().error, 'USAGE_EXHAUSTED');
    assert.equal((await f.access(true, 2)).statusCode, 200);
    const status = f.store.usage.status(f.family.familyId, f.family.childId);
    f.store.usage.bonus(f.family.familyId, f.family.childId, randomUUID(), status.day, 5, status.dayKey);
    assert.equal((await f.login()).statusCode, 403);
    assert.equal((await f.access(false, 3)).statusCode, 200);
    assert.equal(f.store.usage.status(f.family.familyId, f.family.childId).remainingMs, 300000);
  } finally { await f.close(); }
});

test('open child SSE expires immediately on pause; parent session and persisted pause survive', { timeout: 10000 }, async () => {
  const f = await fixture();
  try {
    let began!: () => void;
    const listening = new Promise<void>(resolve => { began = resolve; });
    const add = f.service.listeners.add.bind(f.service.listeners);
    f.service.listeners.add = listener => { const result = add(listener); began(); return result; };
    const stream = f.req(f.child, '/api/events').then(response => response);
    await listening;
    assert.equal((await f.access(true, 0)).statusCode, 200);
    const ended = await stream;
    assert.match(ended.body, /event: ready/); assert.match(ended.body, /event: expired/);
    assert.equal(f.service.listeners.size, 0);
    const reopened = new Store(f.config);
    try {
      assert.deepEqual(reopened.childAccess(f.family.familyId, f.family.childId), { paused: true, revision: 1 });
      assert.equal(reopened.session(f.child.sid), undefined); assert.ok(reopened.session(f.parent.sid));
      assert.deepEqual(reopened.all('PRAGMA foreign_key_check'), []);
    } finally { reopened.close(); }
  } finally { await f.close(); }
});

test('schema 5 to 6 migration is atomic and idempotent', () => {
  const db = new DatabaseSync(':memory:');
  try {
    db.exec(`CREATE TABLE schema_version(version INTEGER); INSERT INTO schema_version VALUES(5);
      CREATE TABLE users(family_id TEXT,id TEXT,PRIMARY KEY(family_id,id));
      CREATE TRIGGER synthetic_failure BEFORE UPDATE ON schema_version BEGIN SELECT RAISE(ABORT,'synthetic migration failure'); END;`);
    assert.throws(() => migrateChildAccess(db), /synthetic migration failure/);
    assert.equal(db.prepare('SELECT version FROM schema_version').get()?.version, 5);
    assert.equal(db.prepare("SELECT 1 FROM sqlite_master WHERE name='child_access'").get(), undefined);
    db.exec('DROP TRIGGER synthetic_failure'); migrateChildAccess(db); migrateChildAccess(db);
    assert.equal(db.prepare('SELECT version FROM schema_version').get()?.version, 6);
  } finally { db.close(); }
});
