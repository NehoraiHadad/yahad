import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { randomBytes } from 'node:crypto';
import { Store } from '../src/store';
import { createApp } from '../src/server';
import type { Config } from '../src/config';

test('existing schema2 gains unread and throttle tables without changing credentials, grants, sessions or history', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'yahad-additive-upgrade-'));
  const config: Config = { dataDir: dir, masterKey: randomBytes(32), connector: 'mock', realAllowed: false,
    host: '127.0.0.1', port: 3000, origin: 'http://localhost:3000', secureCookie: false,
    maxMedia: 1048576, maxStorage: 64*1048576, childIdleMs: 900000, sessionMs: 3600000, diagnosticsEnabled: false };
  const seed = new Store(config);
  const password = 'synthetic legacy long password';
  const family = await seed.createFamily('Synthetic', { name: 'parent-upgrade', password }, { name: 'child-upgrade', password });
  const child = seed.user(seed.one('SELECT * FROM users WHERE id=?', family.childId));
  const session = seed.createSession(child), group = '123456@g.us';
  seed.syncGroups(family.familyId, [{ id: group, title: 'Synthetic' }]);
  seed.grant(family.familyId, child.id, group, true);
  seed.putMessage(family.familyId, { id: 'old', groupId: group, sender: 'Synthetic', fromMe: false,
    text: 'Synthetic retained history', time: 1, kind: 'text', status: 'delivered', reactions: {} });
  const originalHash = seed.one('SELECT password FROM users WHERE id=?', child.id).password;
  const originalGrants = seed.all('SELECT * FROM grants');
  seed.db.exec('DROP TABLE chat_unread; DROP TABLE credential_attempts; DROP TABLE usage_calendar; DROP TABLE usage_bonus; DROP TABLE usage_leases; DROP TABLE usage_days; DROP TABLE usage_policy; DROP TABLE usage_families; UPDATE schema_version SET version=2');
  seed.close();
  try {
    const upgraded = await createApp(config, { boot: false });
    try {
      const { store, app } = upgraded;
      const ready = await app.inject({ url: '/health/ready' });
      assert.equal(ready.statusCode, 200); assert.equal(ready.json().databaseSchema, 5);
      assert.equal(store.one('SELECT password FROM users WHERE id=?', child.id).password, originalHash);
      assert.equal(store.session(session.sid)?.user.id, child.id);
      assert.deepEqual(store.all('SELECT * FROM grants'), originalGrants);
      assert.equal(store.getMessage(family.familyId, group, 'old')?.text, 'Synthetic retained history');
      assert.equal(store.one('SELECT count(*) AS n FROM chat_unread').n, 0);
      assert.equal(store.one('SELECT count(*) AS n FROM credential_attempts').n, 0);
      assert.deepEqual(store.all('PRAGMA foreign_key_check'), []);
      const login = await app.inject({ method: 'POST', url: '/api/login', headers: { origin: config.origin }, payload: { name: 'child-upgrade', password } });
      assert.equal(login.statusCode, 200);
      assert.ok(store.one('SELECT count(*) AS n FROM credential_attempts').n > 0);
    } finally { await upgraded.app.close(); }
  } finally { rmSync(dir, { recursive: true, force: true }); }
});
