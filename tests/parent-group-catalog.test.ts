import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { randomBytes, randomUUID } from 'node:crypto';
import type { Config } from '../src/config.js';
import { Store } from '../src/store.js';
import { createApp } from '../src/server.js';

test('all parent groups include catalog-only entries without granting content access to anyone', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'yachad-parent-catalog-'));
  const config: Config = { dataDir: dir, masterKey: randomBytes(32), connector: 'mock', realAllowed: false,
    host: '127.0.0.1', port: 3000, origin: 'http://localhost:3000', secureCookie: false,
    maxMedia: 1024 ** 2, maxStorage: 64 * 1024 ** 2, childIdleMs: 900000, sessionMs: 3600000,
    diagnosticsEnabled: false };
  const store = new Store(config);
  const family = await store.createFamily('Synthetic', { name: 'catalog-parent', password: 'synthetic parent password' },
    { name: 'catalog-child', password: 'synthetic child password' });
  const other = await store.createFamily('Other synthetic', { name: 'other-parent', password: 'other synthetic password' });
  const siblingId = randomUUID();
  store.run("INSERT INTO users(id,family_id,name,name_key,role,password) SELECT ?,family_id,?,?,'child',password FROM users WHERE id=?",
    siblingId, 'Sibling', 'sibling', family.childId);
  const first = '111@g.us', second = '222@g.us', catalog = '333@g.us', foreign = '444@g.us';
  store.syncGroups(family.familyId, [{ id: first, title: 'First' }, { id: second, title: 'Second' }, { id: catalog, title: 'Catalog only' }]);
  store.syncGroups(other.familyId, [{ id: foreign, title: 'Other family only' }]);
  store.grant(family.familyId, family.childId, first, true);
  store.grant(family.familyId, siblingId, second, true);
  const { app } = await createApp(config, { store, boot: false });
  const session = (id: string) => store.createSession(store.user(store.one('SELECT * FROM users WHERE id=?', id)));
  const parent = session(family.parentId), child = session(family.childId), sibling = session(siblingId);
  const req = (s: typeof parent, url: string, method: 'GET' | 'POST' = 'GET', payload?: Record<string, unknown>) => app.inject({
    url, method, payload, headers: { cookie: 'sid=' + s.sid, 'x-csrf-token': s.csrf, origin: config.origin } });
  const list = async (s: typeof parent) => (await req(s, '/api/groups')).json();
  try {
    const before = store.all('SELECT * FROM grants');
    const groups = await list(parent);
    assert.deepEqual(groups.map((g: any) => g.id).sort(), [first, second, catalog],
      'All conversations must not start from the union of child grants');
    assert.deepEqual(groups.find((g: any) => g.id === catalog), { id: catalog, title: 'Catalog only', canRead: false });
    assert.equal(groups.find((g: any) => g.id === first).canRead, true);
    assert.deepEqual((await list(child)).map((g: any) => g.id), [first]);
    assert.deepEqual((await list(sibling)).map((g: any) => g.id), [second]);
    for (const s of [parent, child, sibling]) {
      for (const g of [catalog, foreign]) {
        for (const suffix of ['messages', 'search?q=synthetic', 'history', 'media/missing'])
          assert.equal((await req(s, `/api/groups/${g}/${suffix}`)).statusCode, 404);
        assert.equal((await req(s, `/api/groups/${g}/messages`, 'POST', {
          clientId: randomUUID(), text: 'must not send' })).statusCode, 404);
        assert.equal((await req(s, `/api/groups/${g}/history`, 'POST', {})).statusCode, 404);
      }
    }
    assert.deepEqual(store.all('SELECT * FROM grants'), before, 'catalog reads do not grant access');
    assert.equal(store.one('SELECT 1 FROM group_keys WHERE family_id=? AND group_id=?', family.familyId, catalog), undefined);
    assert.equal(store.putMessage(family.familyId, { id: 'blocked', groupId: catalog, sender: 'Synthetic',
      fromMe: false, text: 'must not retain', time: Date.now(), kind: 'text', status: 'unknown', reactions: {} }), false);

    // The final revocation removes content, not the parent-visible catalog row.
    store.putMessage(family.familyId, { id: 'saved', groupId: first, sender: 'Synthetic',
      fromMe: false, text: 'was approved', time: Date.now(), kind: 'text', status: 'unknown', reactions: {} });
    assert.equal((await list(parent)).find((g: any) => g.id === first).last.text, 'was approved');
    store.grant(family.familyId, family.childId, first, false);
    assert.deepEqual((await list(parent)).find((g: any) => g.id === first), { id: first, title: 'First', canRead: false });
    assert.deepEqual(await list(child), []);
    assert.equal((await req(parent, `/api/groups/${first}/messages`)).statusCode, 404);
    store.grant(family.familyId, siblingId, second, false);
    assert.equal((await list(parent)).length, 3, 'zero child grants still shows the complete parent catalog');
    assert.ok((await list(parent)).every((g: any) => g.canRead === false && !g.last));
  } finally { await app.close(); store.close(); rmSync(dir, { recursive: true, force: true }); }
});
