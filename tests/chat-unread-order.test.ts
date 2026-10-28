import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, rmSync, readFileSync, existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { randomBytes, randomUUID } from 'node:crypto';
import type { Config } from '../src/config.js';
import { Store } from '../src/store.js';
import { unreadCount } from '../src/chat-read-state.js';
import { compareChatActivity } from '../web/chatOrder.js';
import { createApp } from '../src/server.js';
import { MockConnector, mockGroups } from '../src/connectors/mock.js';

class QuietMock extends MockConnector { override async approved() {} }
const first = mockGroups[0].id, ungranted = mockGroups[3].id;
const foreign = '444@g.us', unknown = '555@g.us';
async function fixture() {
  const dir = mkdtempSync(join(tmpdir(), 'yachad-parent-access-'));
  const config: Config = { dataDir: dir, masterKey: randomBytes(32), connector: 'mock', realAllowed: false,
    host: '127.0.0.1', port: 3000, origin: 'http://localhost:3000', secureCookie: false,
    maxMedia: 1024 ** 2, maxStorage: 64 * 1024 ** 2, childIdleMs: 900000, sessionMs: 3600000,
    diagnosticsEnabled: false };
  const store = new Store(config);
  const family = await store.createFamily('Synthetic', { name: 'parent', password: 'synthetic parent password' },
    { name: 'child', password: 'synthetic child password' });
  const other = await store.createFamily('Other synthetic', { name: 'other-parent', password: 'other synthetic password' });
  const { app, service } = await createApp(config, { store, boot: false, factory: h => new QuietMock(h) });
  await service.connect(family.familyId);
  store.syncGroups(other.familyId, [{ id: foreign, title: 'Other family only' }, { id: ungranted, title: 'Same ID, other family' }]);
  store.grant(family.familyId, family.childId, first, true);
  const session = (id: string) => store.createSession(store.user(store.one('SELECT * FROM users WHERE id=?', id)));
  const parent = session(family.parentId), child = session(family.childId), otherParent = session(other.parentId);
  const req = (s: typeof parent, url: string, method: any = 'GET', payload?: any, headers = {}) => app.inject({
    url, method, payload, headers: { cookie: 'sid=' + s.sid, 'x-csrf-token': s.csrf, origin: config.origin, ...headers } });
  const list = async (s: typeof parent) => (await req(s, '/api/groups')).json();
  const incoming = (id: string, g = ungranted) => ({ id, groupId: g, sender: 'Synthetic', fromMe: false,
    text: 'Synthetic parent-only message', time: Date.now(), kind: 'text' as const });
  return { config, store, app, service, family, other, parent, child, otherParent, req, list, incoming,
    close: async () => { await app.close(); store.close(); rmSync(dir, { recursive: true, force: true }); } };
}

test('local unread is per account, authorized, incoming-only; fetching is not viewing', async () => {
 const f = await fixture();
 try {
   await f.service.ingest(f.family.familyId, f.incoming('new', first));
   await f.service.ingest(f.family.familyId, f.incoming('new', first));
   await f.service.ingest(f.family.familyId, { ...f.incoming('out', first), fromMe: true });
   await f.service.ingest(f.family.familyId, { ...f.incoming('old', first), history: {} });
   const count = (s: typeof f.parent) => unreadCount(f.store, f.store.session(s.sid)!.user, first);
   assert.equal(count(f.parent), 1); assert.equal(count(f.child), 1);
   await f.req(f.parent, `/api/groups/${first}/messages`);
   await f.req(f.parent, `/api/groups/${first}/search?q=Synthetic`);
   assert.equal(count(f.parent), 1);
   const catalog = await f.list(f.parent);
   assert.equal(catalog.find((g: any) => g.id === first).unreadCount, 1);
   const connector = f.service.connector(f.family.familyId);
   connector.read = async () => { throw new Error('Local viewing must never call provider read'); };
   assert.equal((await f.req(f.parent, `/api/groups/${first}/viewed`, 'POST', { ids: ['new'] })).statusCode, 200);
   assert.equal(count(f.parent), 0); assert.equal(count(f.child), 1);
   assert.equal((await f.req(f.parent, `/api/groups/${first}/viewed`, 'POST', { ids: ['new'] })).json().unreadCount, 0);
   await f.service.ingest(f.family.familyId, f.incoming('private-to-parent', ungranted));
   assert.equal((await f.req(f.child, `/api/groups/${ungranted}/viewed`, 'POST', { ids: ['private-to-parent'] })).statusCode, 404);
   assert.equal((await f.req(f.otherParent, `/api/groups/${first}/viewed`, 'POST', { ids: ['new'] })).statusCode, 404);
   assert.equal(unreadCount(f.store, f.store.session(f.child.sid)!.user, ungranted), 0);
   f.store.grant(f.family.familyId, f.family.childId, ungranted, true);
   assert.equal(unreadCount(f.store, f.store.session(f.child.sid)!.user, ungranted), 0, 'late grant does not invent old unread');
   f.store.grant(f.family.familyId, f.family.childId, first, false);
   f.store.grant(f.family.familyId, f.family.childId, first, true);
   assert.equal(count(f.child), 0, 'revoke removes old account-local state');
 } finally { await f.close(); }
});

test('viewing validates exact IDs atomically and cannot clear unseen new messages', async () => {
 const f = await fixture();
 try {
   await f.service.ingest(f.family.familyId, f.incoming('a', first));
   await f.service.ingest(f.family.familyId, f.incoming('b', first));
   assert.equal((await f.req(f.parent, `/api/groups/${first}/viewed`, 'POST', { ids: ['a', 'missing'] })).statusCode, 404);
   assert.equal(unreadCount(f.store, f.store.session(f.parent.sid)!.user, first), 2);
   assert.equal((await f.req(f.parent, `/api/groups/${first}/viewed`, 'POST', { ids: ['a'] })).json().unreadCount, 1);
   await f.service.ingest(f.family.familyId, { ...f.incoming('b', first), deleted: true });
   assert.equal(unreadCount(f.store, f.store.session(f.parent.sid)!.user, first), 0);
 } finally { await f.close(); }
});

test('activity ordering is newest first, stable ties, missing/invalid timestamp zero', () => {
 const groups = [{id:'z'}, {id:'b',lastMessageTime: 5}, {id:'a',lastMessageTime:5}, {id:'c',last:{time:9}}, {id:'d',lastMessageTime:NaN}];
 assert.deepEqual(groups.slice().sort(compareChatActivity).map(x=>x.id), ['c','a','b','d','z']);
 assert.equal(groups[0].id, 'z');
});

test('read-state persists across reopen, and additive initialization does not backfill old rows', async () => {
 const f = await fixture();
 try {
   await f.service.ingest(f.family.familyId, f.incoming('persist', first));
   const user = f.store.session(f.parent.sid)!.user;
   const reopened = new Store(f.config);
   try { assert.equal(unreadCount(reopened, user, first), 1); }
   finally { reopened.close(); }
   // Simulate a database created by an earlier release without local read state.
   f.store.db.exec('DROP TABLE chat_unread');
   const upgraded = new Store(f.config);
   try {
     assert.equal(unreadCount(upgraded, user, first), 0);
     assert.ok(upgraded.getMessage(f.family.familyId, first, 'persist'));
     assert.equal(upgraded.one('SELECT version FROM schema_version').version, 2);
   } finally { upgraded.close(); }
 } finally { await f.close(); }
});
