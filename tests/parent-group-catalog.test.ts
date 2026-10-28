import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, rmSync, readFileSync, existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { randomBytes, randomUUID } from 'node:crypto';
import type { Config } from '../src/config.js';
import { Store } from '../src/store.js';
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

test('parent opens, reads, sends and repeats in known same-family groups without granting any child access', async () => {
  const f = await fixture();
  try {
    const before = f.store.all('SELECT * FROM grants');
    const groups = await f.list(f.parent);
    assert.equal(groups.length, mockGroups.length);
    assert.ok(groups.every((g: any) => g.canRead));
    assert.deepEqual((await f.list(f.child)).map((g: any) => g.id), [first]);
    const empty = await f.req(f.parent, `/api/groups/${ungranted}/messages`);
    assert.equal(empty.statusCode, 200); assert.deepEqual(empty.json().messages, []);
    assert.equal(empty.json().history, 'partial', 'access does not invent old messages');
    assert.equal((await f.req(f.parent, `/api/groups/${ungranted}/history`)).statusCode, 200);
    await f.service.ingest(f.family.familyId, f.incoming('received'));
    assert.equal((await f.req(f.parent, `/api/groups/${ungranted}/search?q=Synthetic`)).json().messages.length, 1);
    const clientId = randomUUID();
    const sent = await f.req(f.parent, `/api/groups/${ungranted}/messages`, 'POST', { clientId, text: 'Parent sends', replyId: 'received' });
    assert.equal(sent.statusCode, 200, sent.body);
    assert.equal(sent.json().message.reply.id, 'received');
    const id = sent.json().message.id;
    assert.equal((await f.req(f.parent, `/api/groups/${ungranted}/messages`, 'POST', { clientId, text: 'Repeat' })).json().message.id, id);
    assert.equal(f.store.messages(f.family.familyId, ungranted).length, 2, 'repeat is idempotent');
    assert.equal((await f.req(f.parent, `/api/groups/${ungranted}/messages/${id}/reaction`, 'POST', { emoji: '👍' })).statusCode, 200);
    assert.equal((await f.req(f.parent, `/api/groups/${ungranted}/messages/${id}`, 'PATCH', { text: 'Edited by parent' })).statusCode, 200);
    assert.equal((await f.req(f.parent, `/api/groups/${ungranted}/read`, 'POST', { ids: [id] })).statusCode, 200);
    assert.equal((await f.req(f.parent, `/api/groups/${ungranted}/typing`, 'POST', { active: true })).statusCode, 200);
    assert.deepEqual(f.store.all('SELECT * FROM grants'), before, 'parent operations never grant child access');

    await f.service.grant(f.family.familyId, f.family.childId, ungranted, true);
    assert.equal((await f.req(f.child, `/api/groups/${ungranted}/messages`)).statusCode, 200);
    await f.service.grant(f.family.familyId, f.family.childId, ungranted, false);
    assert.equal((await f.req(f.child, `/api/groups/${ungranted}/messages`)).statusCode, 404);
    assert.equal((await f.req(f.parent, `/api/groups/${ungranted}/messages`)).json().messages.length, 2);
    assert.equal((await f.list(f.parent)).find((g: any) => g.id === ungranted).last.text, 'Edited by parent');
    assert.equal((await f.req(f.parent, `/api/groups/${ungranted}/messages/${id}`, 'DELETE')).statusCode, 200);
  } finally { await f.close(); }
});

test('media, direct actions and settings remain scoped; unknown and foreign-only groups never ingest or download', async () => {
  const f = await fixture();
  try {
    const jpg = readFileSync('fixtures/garden.jpg');
    const boundary = 'synthetic-parent-upload';
    const payload = Buffer.concat([Buffer.from(`--${boundary}\r\nContent-Disposition: form-data; name="clientId"\r\n\r\n${randomUUID()}\r\n--${boundary}\r\nContent-Disposition: form-data; name="file"; filename="synthetic.jpg"\r\nContent-Type: image/jpeg\r\n\r\n`), jpg, Buffer.from(`\r\n--${boundary}--\r\n`)]);
    const uploaded = await f.req(f.parent, `/api/groups/${ungranted}/media`, 'POST', payload, { 'content-type': `multipart/form-data; boundary=${boundary}` });
    assert.equal(uploaded.statusCode, 200, uploaded.body);
    const id = uploaded.json().message.id, mediaId = uploaded.json().message.mediaId;
    assert.ok(mediaId);
    assert.deepEqual((await f.req(f.parent, `/api/groups/${ungranted}/media/${mediaId}`)).rawPayload, jpg);
    assert.equal((await f.req(f.otherParent, `/api/groups/${ungranted}/media/${mediaId}`)).statusCode, 404, 'identical group IDs are still family scoped');
    for (const [session, group] of [[f.child, ungranted], [f.parent, foreign], [f.parent, unknown], [f.parent, '555@s.whatsapp.net']] as const) {
      const path = `/api/groups/${group}`;
      for (const suffix of ['messages', 'search?q=Synthetic', 'history', `media/${mediaId}`, 'photo'])
        assert.equal((await f.req(session, `${path}/${suffix}`)).statusCode, 404, `${group}/${suffix}`);
      for (const [suffix, body] of [['messages', { clientId: randomUUID(), text: 'blocked' }], ['history', {}], ['typing', { active: true }], ['read', { ids: [id] }], [`messages/${id}/reaction`, { emoji: '👍' }]] as const)
        assert.equal((await f.req(session, `${path}/${suffix}`, 'POST', body)).statusCode, 404);
      assert.equal((await f.req(session, `${path}/media`, 'POST', payload, { 'content-type': `multipart/form-data; boundary=${boundary}` })).statusCode, 404);
      assert.equal((await f.req(session, `${path}/messages/${id}`, 'PATCH', { text: 'blocked' })).statusCode, 404);
      assert.equal((await f.req(session, `${path}/messages/${id}`, 'DELETE')).statusCode, 404);
    }
    assert.equal((await f.req(f.child, '/api/parent/overview')).statusCode, 403);
    assert.equal((await f.req(f.parent, '/api/parent/grant', 'PUT', { childId: f.other.parentId, groupId: ungranted, allowed: true })).statusCode, 400);
    let downloaded = false;
    for (const group of [unknown, foreign, '555@s.whatsapp.net'])
      await f.service.ingest(f.family.familyId, { ...f.incoming('blocked', group), kind: 'image', download: async () => { downloaded = true; return jpg; } });
    assert.equal(downloaded, false);
    assert.equal(f.store.all('SELECT * FROM messages WHERE id=?', 'blocked').length, 0);
  } finally { await f.close(); }
});

test('SSE grants parent known ungranted group updates, never leaks them to children or across families', async () => {
  const f = await fixture();
  try {
    await f.app.ready();
    const streams = [f.parent, f.child].map(s => f.app.inject({ url: '/api/events', headers: { cookie: 'sid=' + s.sid } }).then(r => r));
    for (let i = 0; i < 100 && f.service.listeners.size < 2; i++) await new Promise(r => setImmediate(r));
    assert.equal(f.service.listeners.size, 2);
    f.service.notify(f.other.familyId, foreign);
    f.service.notify(f.family.familyId, foreign);
    f.service.notify(f.family.familyId, unknown);
    f.service.notify(f.family.familyId, ungranted);
    await f.service.grant(f.family.familyId, f.family.childId, first, false);
    f.service.notify(f.family.familyId, first);
    for (const s of [f.parent, f.child]) await f.req(s, '/api/logout', 'POST', {});
    const [parent, child] = await Promise.all(streams);
    assert.ok(parent.body.includes(ungranted)); assert.ok(parent.body.includes(first));
    assert.ok(!child.body.includes(ungranted)); assert.ok(!child.body.includes(first));
    for (const r of [parent, child]) { assert.ok(!r.body.includes(foreign)); assert.ok(!r.body.includes(unknown)); assert.match(r.body, /event: expired/); }
  } finally { await f.close(); }
});

test('catalog startup initializes missing keys without changing existing ciphertext keys or granting children', async () => {
  const f = await fixture();
  try {
    await f.service.ingest(f.family.familyId, f.incoming('durable', first));
    const key = f.store.groupKey(f.family.familyId, first);
    // Exact shape of a legacy ungranted catalog entry: group row, no content/key.
    f.store.run('DELETE FROM group_keys WHERE family_id=? AND group_id=?', f.family.familyId, ungranted);
    const reopened = new Store(f.config);
    try {
      assert.deepEqual(reopened.groupKey(f.family.familyId, first), key);
      assert.equal(reopened.groupKey(f.family.familyId, ungranted).length, 32);
      assert.equal(reopened.getMessage(f.family.familyId, first, 'durable')?.text, 'Synthetic parent-only message');
      assert.equal(reopened.messages(f.family.familyId, ungranted).length, 0);
      assert.equal(reopened.allowed(reopened.user(reopened.one('SELECT * FROM users WHERE id=?', f.family.childId)), ungranted), false);
    } finally { reopened.close(); }
  } finally { await f.close(); }
});

test('final child revocation preserves parent media and in-flight ingestion; membership removal purges and invalidates old generation', async () => {
  const f = await fixture();
  try {
    const jpg = readFileSync('fixtures/garden.jpg');
    await f.service.ingest(f.family.familyId, { ...f.incoming('photo', first), kind: 'image', bytes: jpg });
    const mediaId = f.store.getMessage(f.family.familyId, first, 'photo')!.mediaId!;
    const path = f.store.mediaPath(f.family.familyId, first, mediaId);
    let finish!: (bytes: Buffer) => void;
    const inFlight = f.service.ingest(f.family.familyId, { ...f.incoming('late', first), kind: 'image', download: () => new Promise(resolve => finish = resolve) });
    await f.service.grant(f.family.familyId, f.family.childId, first, false);
    finish(jpg); await inFlight;
    assert.ok(existsSync(path)); assert.ok(f.store.getMessage(f.family.familyId, first, 'late'));
    assert.equal((await f.req(f.child, `/api/groups/${first}/media/${mediaId}`)).statusCode, 404);
    assert.equal((await f.req(f.parent, `/api/groups/${first}/media/${mediaId}`)).statusCode, 200);
    const oldKey = f.store.groupKey(f.family.familyId, first);
    const stale = f.service.ingest(f.family.familyId, { ...f.incoming('stale', first), kind: 'image', download: () => new Promise(resolve => finish = resolve) });
    f.service.connector(f.family.familyId).groups = async () => mockGroups.filter(g => g.id !== first);
    await f.service.syncGroups(f.family.familyId);
    assert.equal(existsSync(path), false);
    assert.equal((await f.req(f.parent, `/api/groups/${first}/messages`)).statusCode, 404);
    f.store.syncGroups(f.family.familyId, [mockGroups[0]]);
    assert.notDeepEqual(f.store.groupKey(f.family.familyId, first), oldKey);
    finish(jpg); await stale;
    assert.equal(f.store.getMessage(f.family.familyId, first, 'stale'), undefined);
    assert.equal(f.store.messages(f.family.familyId, first).length, 0);
  } finally { await f.close(); }
});

test('a delayed send acknowledgement cannot resurrect content after group removal and re-add', async () => {
  const f = await fixture();
  let finish!: () => void;
  try {
    const connector = f.service.connector(f.family.familyId);
    const original = connector.send.bind(connector);
    let started!: () => void;
    const waiting = new Promise<void>(resolve => started = resolve);
    connector.send = async message => {
      const response = await original(message);
      started(); await new Promise<void>(resolve => finish = resolve);
      return response;
    };
    const request = f.req(f.parent, `/api/groups/${ungranted}/messages`, 'POST', { clientId: randomUUID(), text: 'Synthetic delayed send' });
    const result = request.then(r => r);
    await waiting;
    connector.groups = async () => mockGroups.filter(g => g.id !== ungranted);
    await f.service.syncGroups(f.family.familyId);
    f.store.syncGroups(f.family.familyId, [mockGroups[3]]);
    finish();
    assert.equal((await result).statusCode, 502, 'provider send already began, so outcome remains uncertain');
    assert.equal(f.store.messages(f.family.familyId, ungranted).length, 0);
  } finally { finish?.(); await f.close(); }
});

test('child grant revoked during async send ingestion cannot disclose a response retained for parent', async () => {
  const f = await fixture();
  let finish!: () => void;
  try {
    const original = f.service.ingest.bind(f.service);
    let started!: () => void;
    const waiting = new Promise<void>(resolve => started = resolve);
    f.service.ingest = async (...args) => {
      await original(...args);
      if (args[1].text === 'Synthetic in-flight child send') {
        started(); await new Promise<void>(resolve => finish = resolve);
      }
    };
    const result = f.req(f.child, `/api/groups/${first}/messages`, 'POST', { clientId: randomUUID(), text: 'Synthetic in-flight child send' }).then(r => r);
    await waiting;
    await f.service.grant(f.family.familyId, f.family.childId, first, false);
    finish();
    const response = await result;
    assert.equal(response.statusCode, 502);
    assert.ok(!response.body.includes('Synthetic in-flight child send'));
    assert.equal((await f.req(f.child, `/api/groups/${first}/messages`)).statusCode, 404);
    assert.equal((await f.req(f.parent, `/api/groups/${first}/messages`)).json().messages[0].text, 'Synthetic in-flight child send');
  } finally { finish?.(); await f.close(); }
});
