import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { randomBytes, randomUUID } from 'node:crypto';
import type { Config } from '../src/config.js';
import { Store } from '../src/store.js';
import { createApp } from '../src/server.js';

async function fixture() {
  const dir = mkdtempSync(join(tmpdir(), 'yachad-child-profile-'));
  const config: Config = { dataDir: dir, masterKey: randomBytes(32), connector: 'mock', realAllowed: false,
    host: '127.0.0.1', port: 3000, origin: 'http://localhost:3000', secureCookie: false,
    maxMedia: 1024 ** 2, maxStorage: 64 * 1024 ** 2, childIdleMs: 900000, sessionMs: 3600000,
    diagnosticsEnabled: false };
  const store = new Store(config), password = 'synthetic meadow cloud lantern';
  const a = await store.createFamily('A', { name: 'parent-a', password }, { name: 'child-a', password });
  const b = await store.createFamily('B', { name: 'parent-b', password }, { name: 'child-b', password });
  const session = (id: string) => store.createSession(store.user(store.one('SELECT * FROM users WHERE id=?', id)));
  const parent = session(a.parentId), child = session(a.childId), otherParent = session(b.parentId);
  const { app, service } = await createApp(config, { store, boot: false });
  const req = (s: typeof parent | null, body: any, extra: Record<string, string> = {}) => app.inject({
    method: 'PUT', url: '/api/parent/child-profile', payload: body, headers: { origin: config.origin,
      ...(s ? { cookie: 'sid=' + s.sid, 'x-csrf-token': s.csrf } : {}), ...extra } });
  const get = (s: typeof parent, url: string) => app.inject({ url, headers: { cookie: 'sid=' + s.sid } });
  const login = (name: string) => app.inject({ method: 'POST', url: '/api/login', payload: { name, password }, headers: { origin: config.origin } });
  return { login, store, service, a, b, parent, child, otherParent, req, get,
    async close() { await app.close(); store.close(); rmSync(dir, { recursive: true, force: true }); } };
}

test('parent changes only the child account name, preserving password, sessions, grants and signature', async () => {
  const f = await fixture();
  try {
    assert.equal(f.store.one('SELECT name FROM users WHERE id=?', f.a.childId).name, 'child-a',
      'the one canonical name is used for both display and login');
    f.store.setIdentityLabel(f.a.familyId, f.a.childId, 'חתימה סינתטית');
    await f.service.connect(f.a.familyId);
    await f.service.grant(f.a.familyId, f.a.childId, '120363001@g.us', true);
    const original = f.store.one('SELECT * FROM users WHERE id=?', f.a.childId);
    const grants = f.store.all('SELECT * FROM grants'), notifications: unknown[] = [];
    f.service.listeners.add((family, group, type) => { notifications.push({ family, group, type }); });
    const response = await f.req(f.parent, { childId: f.a.childId, name: '  עדי  ' });
    assert.equal(response.statusCode, 200, response.body);
    assert.deepEqual(response.json(), { ok: true, child: { id: f.a.childId, name: 'עדי' } });
    assert.deepEqual({ ...f.store.one('SELECT * FROM users WHERE id=?', f.a.childId) }, { ...original, name: 'עדי', name_key: 'עדי' });
    assert.deepEqual(f.store.all('SELECT * FROM grants'), grants);
    assert.equal(f.store.identityLabel(f.a.familyId, f.a.childId), 'חתימה סינתטית');
    assert.ok(f.store.session(f.parent.sid)); assert.ok(f.store.session(f.child.sid));
    assert.equal((await f.get(f.child, '/api/me')).json().user.name, 'עדי');
    assert.equal((await f.get(f.parent, '/api/parent/overview')).json().children[0].name, 'עדי');
    assert.equal((await f.get(f.otherParent, '/api/parent/overview')).json().children[0].name, 'child-b');
    assert.deepEqual(notifications, [{ family: f.a.familyId, group: undefined, type: 'identity' }]);
    assert.equal((await f.req(f.parent, { childId: f.a.childId, name: 'עדי' })).statusCode, 200,
      'renaming is idempotent if the first response is lost');
    assert.equal((await f.login('child-a')).statusCode, 401, 'old account name is not an alias');
    assert.equal((await f.login(' עדי ')).statusCode, 200, 'new Hebrew name logs in with the same password');
  } finally { await f.close(); }
});

test('account-name edit rejects children, other families, parent targets, missing auth and CSRF', async () => {
  const f = await fixture();
  try {
    const body = { childId: f.a.childId, name: 'לא יישמר' };
    assert.equal((await f.req(null, body)).statusCode, 401);
    assert.equal((await f.req(f.child, body)).statusCode, 403);
    assert.equal((await f.req(f.otherParent, body)).statusCode, 404);
    for (const childId of [f.b.childId, f.a.parentId, randomUUID()]) {
      assert.equal((await f.req(f.parent, { ...body, childId })).statusCode, 404);
    }
    assert.equal((await f.req(f.parent, body, { 'x-csrf-token': 'wrong' })).statusCode, 403);
    assert.equal((await f.req(f.parent, body, { origin: 'https://untrusted.example' })).statusCode, 403);
    assert.equal(f.store.one('SELECT name FROM users WHERE id=?', f.a.childId).name, 'child-a');
    assert.equal(f.store.one('SELECT name FROM users WHERE id=?', f.b.childId).name, 'child-b');
  } finally { await f.close(); }
});

test('account-name edit validates bounded names and rejects old name fields and cannot mutate security fields', async () => {
  const f = await fixture();
  try {
    const original = f.store.one('SELECT * FROM users WHERE id=?', f.a.childId);
    for (const name of ['', '  ', 'x'.repeat(61), 'a\x00b', 'a\x7fb', 'a\u0085b', 'a\u202eb', 'a\u2066b', 42, null]) {
      assert.equal((await f.req(f.parent, { childId: f.a.childId, name })).statusCode, 400, JSON.stringify(name));
    }
    for (const extra of [{ username: 'new-login' }, { display: 'old dual name' }, { role: 'parent' }, { password: 'new password' }, { familyId: f.b.familyId }, { label: 'changed signature' }]) {
      assert.equal((await f.req(f.parent, { childId: f.a.childId, name: 'עדי', ...extra })).statusCode, 400);
    }
    assert.deepEqual(f.store.one('SELECT * FROM users WHERE id=?', f.a.childId), original);
    assert.equal((await f.req(f.parent, { childId: f.a.childId, name: 'נ'.repeat(60) })).statusCode, 200);
  } finally { await f.close(); }
});


test('account name uniqueness includes normalized forms across all accounts, with no login alias fallback', async () => {
  const f = await fixture();
  try {
    for (const name of ['child-b', 'CHILD-B', ' parent-a ']) {
      const response = await f.req(f.parent, { childId: f.a.childId, name });
      assert.equal(response.statusCode, 409);
      assert.equal(response.json().error, 'NAME_UNAVAILABLE');
    }
    assert.equal((await f.req(f.parent, { childId: f.a.childId, name: '  Édi   לוי ' })).statusCode, 200);
    assert.equal(f.store.one('SELECT name FROM users WHERE id=?', f.a.childId).name, 'Édi לוי');
    assert.equal((await f.login('e\u0301DI לוי')).statusCode, 200);
    assert.equal((await f.login('child-a')).statusCode, 401);
    assert.ok(f.store.session(f.child.sid), 'renaming never revokes an already connected device');
  } finally { await f.close(); }
});


test('a login using the old name cannot finish after an in-flight rename', async () => {
  const f = await fixture();
  try {
    let began: () => void = () => {};
    const lookup = new Promise<void>(resolve => { began = resolve; });
    const one = f.store.one.bind(f.store);
    f.store.one = (sql: string, ...args: any[]) => {
      const row = one(sql, ...args);
      if (sql === 'SELECT * FROM users WHERE name_key=?' && args[0] === 'child-a') began();
      return row;
    };
    const oldLogin = f.login('child-a');
    await lookup;
    assert.equal((await f.req(f.parent, { childId: f.a.childId, name: 'שם חדש' })).statusCode, 200);
    assert.equal((await oldLogin).statusCode, 401);
    assert.equal((await f.login('שם חדש')).statusCode, 200);
    assert.ok(f.store.session(f.child.sid));
  } finally { await f.close(); }
});
