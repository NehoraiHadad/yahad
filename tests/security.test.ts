import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, rmSync, readFileSync, readdirSync, writeFileSync, existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { randomBytes, randomUUID } from 'node:crypto';
import type { Config } from '../src/config.js';
import { Store } from '../src/store.js';
import { createApp } from '../src/server.js';
import { MockConnector, mockGroups } from '../src/connectors/mock.js';
import { hash } from '../src/crypto.js';
class QuietMock extends MockConnector {
  override async approved() {}
}
const group = '120363001@g.us',
  blocked = '120363999@g.us';
async function fixture() {
  const dir = mkdtempSync(join(tmpdir(), 'yachad-test-'));
  const config: Config = {
    dataDir: join(dir, 'data'),
    masterKey: randomBytes(32),
    connector: 'mock',
    realAllowed: false,
    host: '127.0.0.1',
    port: 3000,
    origin: 'http://localhost:3000',
    secureCookie: false,
    maxMedia: 1024 * 1024,
    maxStorage: 64 * 1024 * 1024,
    childIdleMs: 900000,
    sessionMs: 3600000,
  };
  const store = new Store(config);
  const password = randomBytes(24).toString('base64url');
  const a = await store.createFamily(
    'A',
    { name: 'parent-a', password },
    { name: 'child-a', password: password + 'child' },
  );
  const b = await store.createFamily(
    'B',
    { name: 'parent-b', password },
    { name: 'child-b', password: password + 'child' },
  );
  const { app, service } = await createApp(config, { store, factory: (h) => new QuietMock(h) });
  await service.connect(a.familyId);
  await service.connect(b.familyId);
  const pa = store.user(
      store.one('SELECT * FROM users WHERE family_id=? AND id=?', a.familyId, a.parentId),
    ),
    ca = store.user(
      store.one('SELECT * FROM users WHERE family_id=? AND id=?', a.familyId, a.childId),
    ),
    cb = store.user(
      store.one('SELECT * FROM users WHERE family_id=? AND id=?', b.familyId, b.childId),
    );
  const parent = store.createSession(pa),
    child = store.createSession(ca),
    other = store.createSession(cb);
  const req = async (
    s: typeof child | null,
    url: string,
    method: any = 'GET',
    payload?: any,
    headers: Record<string, string> = {},
  ) =>
    app.inject({
      method,
      url,
      headers: {
        ...(s ? { cookie: 'sid=' + s.sid, 'x-csrf-token': s.csrf } : {}),
        origin: config.origin,
        ...headers,
      },
      payload,
    });
  await service.grant(a.familyId, a.childId, group, true);
  await service.grant(b.familyId, b.childId, group, true);
  const ingest = async (f: string, id: string, text: string, extra: any = {}) =>
    service.ingest(f, {
      id,
      groupId: group,
      text,
      sender: 'Sample',
      fromMe: false,
      time: Date.now(),
      kind: 'text',
      ...extra,
    });
  return {
    dir,
    config,
    store,
    app,
    service,
    a,
    b,
    parent,
    child,
    other,
    ca,
    cb,
    password,
    req,
    ingest,
    async close() {
      await app.close();
      store.close();
      rmSync(dir, { recursive: true, force: true });
    },
  };
}

test('repeated parent grant actions are idempotent and preserve selections hidden by group filtering', async () => {
  const f = await fixture();
  try {
    const second = '120363002@g.us';
    for (let i = 0; i < 2; i++) assert.equal((await f.req(f.parent, '/api/parent/grant', 'PUT', {
      childId: f.a.childId, groupId: second, allowed: true })).statusCode, 200);
    let overview = (await f.req(f.parent, '/api/parent/overview')).json();
    assert.equal(overview.grants.filter((x: any) => x.user_id === f.a.childId && x.group_id === second).length, 1);
    assert.ok(overview.grants.some((x: any) => x.user_id === f.a.childId && x.group_id === group));
    for (let i = 0; i < 2; i++) assert.equal((await f.req(f.parent, '/api/parent/grant', 'PUT', {
      childId: f.a.childId, groupId: second, allowed: false })).statusCode, 200);
    overview = (await f.req(f.parent, '/api/parent/overview')).json();
    assert.equal(overview.grants.filter((x: any) => x.group_id === second).length, 0);
    assert.ok(overview.grants.some((x: any) => x.user_id === f.a.childId && x.group_id === group));
    assert.equal((await f.req(f.child, '/api/parent/grant', 'PUT', {
      childId: f.a.childId, groupId: second, allowed: true })).statusCode, 403);
  } finally { await f.close(); }
});

test('intentional disconnect commits local revocation before provider wait, retains chats, blocks stale hooks and reboot reconnect', async () => {
  const f = await fixture();
  let finish!: () => void;
  try {
    await f.ingest(f.a.familyId, 'retained-disconnect-message', 'retained text');
    f.store.authSet(f.a.familyId, 'fixture', 'encrypted-local-auth');
    const c = f.service.connector(f.a.familyId), hooks = (c as QuietMock).hooks;
    hooks.state('qr', 'STALE_SYNTHETIC_QR');
    assert.equal((await f.req(f.child, '/api/parent/disconnect', 'POST', { erase: false })).statusCode, 403);
    assert.equal((await f.req(f.parent, '/api/parent/disconnect', 'POST', { erase: false }, { 'x-csrf-token': 'wrong' })).statusCode, 403);
    let started!: () => void;
    const waiting = new Promise<void>(resolve => { started = resolve; });
    const gate = new Promise<void>(resolve => { finish = resolve; });
    c.stop = async logout => { assert.equal(logout, true); started(); await gate; return { remoteRevoked: false }; };
    const request = f.req(f.parent, '/api/parent/disconnect', 'POST', { erase: false });
    await waiting;
    assert.equal(f.store.one('SELECT linked FROM families WHERE id=?', f.a.familyId).linked, 0);
    assert.equal(f.store.authGet(f.a.familyId, 'fixture'), undefined);
    assert.deepEqual(f.service.states.get(f.a.familyId), { state: 'idle' });
    hooks.state('connected'); hooks.authSet('late', 'must-not-return');
    assert.equal(f.store.authGet(f.a.familyId, 'late'), undefined);
    assert.equal((await f.req(f.parent, '/api/parent/connect', 'POST')).statusCode, 409);
    const { ChatService } = await import('../src/service.js');
    const restarted = new ChatService(f.store, h => new QuietMock(h));
    await restarted.boot(); assert.equal(restarted.connectors.has(f.a.familyId), false); await restarted.close();
    finish();
    const result = await request;
    assert.equal(result.statusCode, 200);
    assert.equal(result.json().history, 'retained'); assert.equal(result.json().remoteRevoked, false);
    assert.ok(f.store.getMessage(f.a.familyId, group, 'retained-disconnect-message'));
    assert.ok(f.store.allowed(f.ca, group));
    assert.equal((await f.req(f.parent, '/api/parent/qr')).json().qr, null);
    assert.equal((await f.req(f.parent, '/api/parent/disconnect', 'POST', { erase: false })).statusCode, 200);
    assert.equal((await f.req(f.parent, '/api/parent/connect', 'POST')).statusCode, 200);
    assert.equal(f.service.states.get(f.a.familyId)?.state, 'connected');
    assert.equal((await f.req(f.parent, '/api/parent/overview')).json().disconnect, null);
  } finally { finish?.(); await f.close(); }
});

test('in-flight provider media cannot persist after a retaining disconnect', async () => {
  const f = await fixture(); let release!: (bytes: Buffer) => void;
  try {
    const hooks = (f.service.connector(f.a.familyId) as QuietMock).hooks;
    let started!: () => void;
    const waiting = new Promise<void>(resolve => { started = resolve; });
    const gate = new Promise<Buffer>(resolve => { release = resolve; });
    const pending = hooks.incoming({ id: 'late-provider-media', groupId: group, sender: 'Synthetic', fromMe: false,
      time: Date.now(), kind: 'image', text: 'late fixture', download: () => { started(); return gate; } });
    await waiting;
    assert.equal((await f.req(f.parent, '/api/parent/disconnect', 'POST', { erase: false })).statusCode, 200);
    release(Buffer.from([137,80,78,71,13,10,26,10])); await pending;
    assert.equal(f.store.getMessage(f.a.familyId, group, 'late-provider-media'), undefined);
    assert.equal(f.store.one('SELECT count(*) AS n FROM media WHERE family_id=? AND message_id=?', f.a.familyId, 'late-provider-media').n, 0);
    assert.ok(f.store.allowed(f.ca, group));
  } finally { release?.(Buffer.alloc(0)); await f.close(); }
});

test('separately requested history deletion completes locally before unlink network wait and reports its outcome', async () => {
  const f = await fixture(); let finish!: () => void;
  try {
    await f.ingest(f.a.familyId, 'delete-only-when-requested', 'synthetic');
    const c = f.service.connector(f.a.familyId);
    let started!: () => void;
    const waiting = new Promise<void>(resolve => { started = resolve; });
    const gate = new Promise<void>(resolve => { finish = resolve; });
    c.stop = async () => { started(); await gate; return { remoteRevoked: true }; };
    const pending = f.req(f.parent, '/api/parent/disconnect', 'POST', { erase: true });
    await waiting;
    assert.equal(f.store.getMessage(f.a.familyId, group, 'delete-only-when-requested'), undefined);
    assert.equal((await f.req(f.parent, '/api/parent/overview')).json().disconnectHistory, 'deleted');
    finish(); assert.equal((await pending).json().history, 'deleted');
    assert.equal((await f.req(f.parent, '/api/parent/disconnect', 'POST', { erase: true })).json().history, 'deleted');
  } finally { finish?.(); await f.close(); }
});

test('a group refresh finishing after retaining disconnect cannot revoke grants or history', async () => {
  const f = await fixture(); let finish!: (groups: any[]) => void;
  try {
    await f.ingest(f.a.familyId, 'preserved-refresh-message', 'synthetic');
    const c = f.service.connector(f.a.familyId);
    let started!: () => void;
    const waiting = new Promise<void>(resolve => { started = resolve; });
    const gate = new Promise<any[]>(resolve => { finish = resolve; });
    c.groups = () => { started(); return gate; };
    const pending = f.req(f.parent, '/api/parent/sync-groups', 'POST');
    await waiting;
    assert.equal((await f.req(f.parent, '/api/parent/disconnect', 'POST', { erase: false })).statusCode, 200);
    finish([]); assert.equal((await pending).statusCode, 200);
    assert.ok(f.store.allowed(f.ca, group));
    assert.ok(f.store.getMessage(f.a.familyId, group, 'preserved-refresh-message'));
    assert.ok(f.store.groups(f.a.familyId).some(g => g.id === group));
  } finally { finish?.([]); await f.close(); }
});

test('persisted incomplete deletion blocks reconnect until explicit deletion is completed', async () => {
  const f = await fixture();
  try {
    await f.req(f.parent, '/api/parent/disconnect', 'POST', { erase: false });
    f.store.run('INSERT OR REPLACE INTO instance_metadata VALUES(?,?)', 'disconnect-history:' + f.a.familyId, 'deleting');
    const response = await f.req(f.parent, '/api/parent/connect', 'POST');
    assert.equal(response.statusCode, 409); assert.equal(response.json().error, 'DELETION_INCOMPLETE');
    assert.equal((await f.req(f.parent, '/api/parent/overview')).json().disconnectHistory, 'deleting');
    assert.equal((await f.req(f.parent, '/api/parent/disconnect', 'POST', { erase: true })).statusCode, 200);
    assert.equal((await f.req(f.parent, '/api/parent/connect', 'POST')).statusCode, 200);
  } finally { await f.close(); }
});

test('authentication, separate roles, secure headers and CSRF', async () => {
  const f = await fixture();
  try {
    for (const url of [
      '/api/me',
      '/api/groups',
      '/api/parent/qr',
      `/api/groups/${group}/messages`,
      `/api/groups/${group}/media/x`,
    ])
      assert.equal((await f.req(null, url)).statusCode, 401);
    assert.equal((await f.req(f.child, '/api/parent/qr')).statusCode, 403);
    assert.equal((await f.req(f.child, '/api/parent/overview')).statusCode, 403);
    assert.equal((await f.req(f.child, '/api/parent/connect', 'POST', {})).statusCode, 403);
    assert.equal(
      (await f.req(f.child, '/api/activity', 'POST', {}, { 'x-csrf-token': 'wrong' })).statusCode,
      403,
    );
    assert.equal(
      (await f.req(f.child, '/api/activity', 'POST', {}, { origin: 'https://evil.example' }))
        .statusCode,
      403,
    );
    assert.equal(
      (
        await f.app.inject({
          url: '/api/activity',
          method: 'POST',
          headers: { cookie: 'sid=' + f.child.sid, 'x-csrf-token': f.child.csrf },
          payload: {},
        })
      ).statusCode,
      403,
    );
    const r = await f.req(f.child, '/api/me');
    assert.match(r.headers['content-security-policy'] as string, /frame-ancestors/);
    assert.equal(r.headers['cache-control'], 'no-store, max-age=0');
    assert.equal(r.json().user.role, 'child');
    assert.equal('masterKey' in r.json(), false);
  } finally {
    await f.close();
  }
});

test('login uses salted passwords, expiring HttpOnly sessions and throttles', async () => {
  const f = await fixture();
  try {
    const r = await f.req(null, '/api/login', 'POST', {
      name: 'parent-a',
      password: f.password,
    });
    assert.equal(r.statusCode, 200);
    assert.match(r.headers['set-cookie'] as string, /HttpOnly/);
    assert.match(r.headers['set-cookie'] as string, /SameSite=Strict/);
    const pw = f.store.one('SELECT password FROM users WHERE name=?', 'parent-a').password;
    assert.ok(!pw.includes(f.password));
    for (let i = 0; i < 8; i++)
      assert.equal(
        (await f.req(null, '/api/login', 'POST', { name: 'nobody', password: 'wrong' }))
          .statusCode,
        401,
      );
    assert.equal(
      (await f.req(null, '/api/login', 'POST', { name: 'nobody', password: 'wrong' }))
        .statusCode,
      429,
    );
    f.store.run('UPDATE sessions SET expires=0 WHERE id=?', hash(f.child.sid));
    assert.equal((await f.req(f.child, '/api/me')).statusCode, 401);
    f.store.run('UPDATE sessions SET last=0 WHERE id=?', hash(f.other.sid));
    assert.equal((await f.req(f.other, '/api/me')).statusCode, 401);
  } finally {
    await f.close();
  }
});

test('family isolation with identical group/message/media IDs, all direct actions scoped', async () => {
  const f = await fixture();
  try {
    await f.ingest(f.a.familyId, 'same-id', 'FAMILY_A_SECRET');
    await f.ingest(f.b.familyId, 'same-id', 'FAMILY_B_SECRET');
    const a = (await f.req(f.child, `/api/groups/${group}/messages`)).body;
    const b = (await f.req(f.other, `/api/groups/${group}/messages`)).body;
    assert.match(a, /FAMILY_A_SECRET/);
    assert.doesNotMatch(a, /FAMILY_B_SECRET/);
    assert.match(b, /FAMILY_B_SECRET/);
    assert.doesNotMatch(b, /FAMILY_A_SECRET/);
    for (const [method, path, body] of [
      ['GET', 'messages', undefined],
      ['GET', 'search?q=SECRET', undefined],
      ['GET', 'media/guess', undefined],
      ['POST', 'messages', { clientId: randomUUID(), text: 'no' }],
      ['POST', 'typing', { active: true }],
      ['POST', 'read', { ids: ['same-id'] }],
      ['POST', 'history', { oldestId: 'same-id' }],
      ['POST', 'messages/same-id/reaction', { emoji: '👍' }],
      ['PATCH', 'messages/same-id', { text: 'no' }],
      ['DELETE', 'messages/same-id', undefined],
    ] as const) {
      const r = await f.req(f.child, `/api/groups/${blocked}/${path}`, method, body);
      assert.ok([404, 409].includes(r.statusCode), `${method} ${path}: ${r.statusCode}`);
    }
    assert.equal(
      (
        await f.req(f.parent, '/api/parent/grant', 'PUT', {
          childId: f.b.childId,
          groupId: group,
          allowed: true,
        })
      ).statusCode,
      400,
    );
    assert.equal(
      (
        await f.req(f.parent, '/api/parent/child-password', 'POST', {
          childId: f.b.childId,
          password: 'new-passphrase-for-child',
          confirmation: 'new-passphrase-for-child',
        })
      ).statusCode,
      404,
    );
  } finally {
    await f.close();
  }
});

test('filter unknown groups BEFORE media download and storage; external quote content removed', async () => {
  const blocked = 'never-known@g.us';
  const f = await fixture();
  try {
    let called = false;
    await f.service.ingest(f.a.familyId, {
      id: 'forbidden',
      groupId: blocked,
      text: 'MUST_NEVER_PERSIST',
      sender: 'Hidden',
      fromMe: false,
      time: Date.now(),
      kind: 'image',
      download: async () => {
        called = true;
        return Buffer.from('secret');
      },
    });
    assert.equal(called, false);
    assert.equal(
      f.store.one('SELECT 1 FROM messages WHERE family_id=? AND group_id=?', f.a.familyId, blocked),
      undefined,
    );
    await f.ingest(f.a.familyId, 'reply', 'Visible', { replyId: 'outside-message' });
    const r = await f.req(f.child, `/api/groups/${group}/messages`);
    assert.deepEqual(r.json().messages[0].reply, {
      id: 'outside-message', text: 'ההודעה המקורית אינה זמינה', sender: '', available: false,
    });
    assert.equal('raw' in r.json().messages[0], false);
    for (const file of readdirSync(f.config.dataDir))
      if (file.startsWith('app.sqlite'))
        assert.equal(
          readFileSync(join(f.config.dataDir, file)).includes(Buffer.from('MUST_NEVER_PERSIST')),
          false,
        );
  } finally {
    await f.close();
  }
});

test('send, idempotency, scoped replies, real media bytes, edit, delete and reactions', async () => {
  const f = await fixture();
  try {
    await f.ingest(f.a.familyId, 'parent-msg', 'hello');
    const clientId = randomUUID();
    let r = await f.req(f.child, `/api/groups/${group}/messages`, 'POST', {
      clientId,
      text: 'שלום ✨',
      replyId: 'parent-msg',
    });
    assert.equal(r.statusCode, 200, r.body);
    const id = r.json().message.id;
    assert.equal(r.json().message.reply.text, 'hello');
    r = await f.req(f.child, `/api/groups/${group}/messages`, 'POST', {
      clientId,
      text: 'duplicate',
    });
    assert.equal(r.json().message.id, id);
    assert.equal(
      f.store.all('SELECT id FROM messages WHERE family_id=? AND group_id=?', f.a.familyId, group)
        .length,
      2,
    );
    assert.equal(
      (
        await f.req(f.child, `/api/groups/${group}/messages`, 'POST', {
          clientId: randomUUID(),
          text: 'bad',
          replyId: 'not-here',
        })
      ).statusCode,
      404,
    );
    assert.equal(
      (await f.req(f.child, `/api/groups/${group}/messages/${id}`, 'PATCH', { text: 'נערכה' }))
        .statusCode,
      200,
    );
    assert.equal(f.store.getMessage(f.a.familyId, group, id)?.text, 'נערכה');
    assert.equal(
      (await f.req(f.child, `/api/groups/${group}/messages/parent-msg`, 'DELETE')).statusCode,
      403,
    );
    assert.equal(
      (
        await f.req(f.child, `/api/groups/${group}/messages/${id}/reaction`, 'POST', {
          emoji: '💚',
        })
      ).statusCode,
      200,
    );
    assert.deepEqual(
      f.service.public(f.a.familyId, f.store.getMessage(f.a.familyId, group, id)!).reactions,
      [{ emoji: '💚', count: 1, mine: true }],
    );
    const jpg = readFileSync('fixtures/garden.jpg');
    await f.ingest(f.a.familyId, 'photo', 'caption', {
      kind: 'image',
      bytes: jpg,
      mime: 'image/jpeg',
    });
    const media = f.store.getMessage(f.a.familyId, group, 'photo')!.mediaId!;
    r = await f.req(f.child, `/api/groups/${group}/media/${media}`);
    assert.equal(r.statusCode, 200);
    assert.deepEqual(r.rawPayload, jpg);
    const ranged = await f.req(f.child, `/api/groups/${group}/media/${media}`, 'GET', undefined, {
      range: 'bytes=10-29',
    });
    assert.equal(ranged.statusCode, 206);
    assert.deepEqual(ranged.rawPayload, jpg.subarray(10, 30));
    assert.equal((await f.req(f.other, `/api/groups/${group}/media/${media}`)).statusCode, 404);
    assert.equal(
      (await f.req(f.child, `/api/groups/${group}/messages/${id}`, 'DELETE')).statusCode,
      200,
    );
    assert.equal(f.store.getMessage(f.a.familyId, group, id)?.deleted, true);
    await f.ingest(f.a.familyId, id, 'A delayed echo must not restore this', {
      fromMe: true,
      status: 'sent',
    });
    assert.equal(f.store.getMessage(f.a.familyId, group, id)?.deleted, true);
  } finally {
    await f.close();
  }
});

test('multipart upload is sniffed, authorized, bounded, converted voice and persists media', async () => {
  const f = await fixture();
  try {
    const upload = async (g: string, bytes: Buffer, voice = false, sticker = false) => {
      const boundary = '----yachad-test';
      const fields = {
        clientId: randomUUID(),
        text: '',
        voice: String(voice),
        sticker: String(sticker),
      };
      const chunks: Buffer[] = [];
      for (const [k, v] of Object.entries(fields))
        chunks.push(
          Buffer.from(
            `--${boundary}\r\nContent-Disposition: form-data; name="${k}"\r\n\r\n${v}\r\n`,
          ),
        );
      chunks.push(
        Buffer.from(
          `--${boundary}\r\nContent-Disposition: form-data; name="file"; filename="sample.bin"\r\nContent-Type: image/jpeg\r\n\r\n`,
        ),
        bytes,
        Buffer.from(`\r\n--${boundary}--\r\n`),
      );
      return f.req(f.child, `/api/groups/${g}/media`, 'POST', Buffer.concat(chunks), {
        'content-type': `multipart/form-data; boundary=${boundary}`,
      });
    };
    let r = await upload(group, readFileSync('fixtures/garden.jpg'));
    assert.equal(r.statusCode, 200, r.body);
    assert.ok(r.json().message.mediaId);
    assert.equal(r.json().message.kind, 'image');
    r = await upload(blocked, readFileSync('fixtures/garden.jpg'));
    assert.equal(r.statusCode, 404);
    r = await upload(group, Buffer.alloc(2 * 1024 * 1024));
    assert.equal(r.statusCode, 413, r.body);
    r = await upload(group, readFileSync('fixtures/audio.ogg'), true);
    assert.equal(r.statusCode, 200, r.body);
    assert.equal(r.json().message.kind, 'audio');
    assert.match(r.json().message.mime, /audio\/ogg/);
    f.store.setIdentityLabel(f.a.familyId, f.a.childId, 'מהטאבלט');
    r = await upload(group, readFileSync('fixtures/flower.webp'), false, true);
    assert.equal(r.statusCode, 200, r.body);
    assert.equal(r.json().message.kind, 'sticker');
    assert.equal(r.json().message.reply.text, 'מהטאבלט · מדבקה');
    r = await upload(group, Buffer.from('<script>evil()</script>'));
    assert.equal(r.statusCode, 200);
    const m = r.json().message;
    assert.equal(m.kind, 'document');
    r = await f.req(f.child, `/api/groups/${group}/media/${m.mediaId}`);
    assert.match(r.headers['content-disposition'] as string, /attachment/);
    assert.equal(r.headers['content-type'], 'application/octet-stream');
  } finally {
    await f.close();
  }
});

test('membership removal purges messages, media and active key; in-flight download cannot resurrect', async () => {
  const f = await fixture();
  try {
    await f.ingest(f.a.familyId, 'photo', 'private caption', {
      kind: 'image',
      bytes: readFileSync('fixtures/garden.jpg'),
    });
    const m = f.store.getMessage(f.a.familyId, group, 'photo')!;
    const path = f.store.mediaPath(f.a.familyId, group, m.mediaId!);
    assert.ok(existsSync(path));
    let done!: (b: Buffer) => void;
    const inflight = f.service.ingest(f.a.familyId, {
      id: 'late',
      groupId: group,
      sender: 'X',
      fromMe: false,
      time: Date.now(),
      kind: 'image',
      text: 'late',
      download: () => new Promise((r) => (done = r)),
    });
    f.service.connector(f.a.familyId).groups = async () => mockGroups.filter(g => g.id !== group);
    await f.service.syncGroups(f.a.familyId);
    done(readFileSync('fixtures/garden.jpg'));
    await inflight;
    assert.equal(existsSync(path), false);
    assert.equal(
      f.store.one('SELECT 1 FROM group_keys WHERE family_id=? AND group_id=?', f.a.familyId, group),
      undefined,
    );
    assert.equal(
      f.store.one('SELECT 1 FROM messages WHERE family_id=? AND group_id=?', f.a.familyId, group),
      undefined,
    );
    assert.equal((await f.req(f.child, `/api/groups/${group}/media/${m.mediaId}`)).statusCode, 404);
    assert.equal((await f.req(f.child, `/api/groups/${group}/messages`)).statusCode, 404);
    assert.ok(f.store.retained(f.b.familyId, group));
  } finally {
    await f.close();
  }
});

test('one child revocation preserves another child grant within the same family', async () => {
  const f = await fixture();
  try {
    const cid = randomUUID();
    const source = f.store.one(
      'SELECT password FROM users WHERE family_id=? AND id=?',
      f.a.familyId,
      f.a.childId,
    );
    f.store.run(
      'INSERT INTO users(id,family_id,name,name_key,role,password) VALUES(?,?,?,?,?,?)',
      cid,
      f.a.familyId,
      'second-child',
      'second-child',
      'child',
      source.password,
    );
    await f.service.grant(f.a.familyId, cid, group, true);
    await f.ingest(f.a.familyId, 'shared', 'Retained for child 2');
    await f.service.grant(f.a.familyId, f.a.childId, group, false);
    assert.equal((await f.req(f.child, `/api/groups/${group}/messages`)).statusCode, 404);
    assert.ok(f.store.getMessage(f.a.familyId, group, 'shared'));
    const user = f.store.user(
      f.store.one('SELECT * FROM users WHERE family_id=? AND id=?', f.a.familyId, cid),
    );
    const s = f.store.createSession(user);
    assert.equal((await f.req(s, `/api/groups/${group}/messages`)).statusCode, 200);
  } finally {
    await f.close();
  }
});

test('pagination stable on identical timestamps, search and cross-group reply protections', async () => {
  const f = await fixture();
  try {
    for (let i = 0; i < 85; i++)
      await f.ingest(f.a.familyId, `id-${String(i).padStart(3, '0')}`, 'needle-' + i, {
        time: 1700000000000,
      });
    const r = await f.req(f.child, `/api/groups/${group}/messages`);
    assert.equal(r.json().messages.length, 40);
    const second = await f.req(f.child, `/api/groups/${group}/messages?cursor=${r.json().cursor}`);
    assert.equal(second.json().messages.length, 40);
    assert.equal(
      new Set([...r.json().messages, ...second.json().messages].map((x: any) => x.id)).size,
      80,
    );
    const found = await f.req(f.child, `/api/groups/${group}/search?q=needle-84`);
    assert.equal(found.json().messages.length, 1);
    assert.equal(
      (await f.req(f.child, `/api/groups/${group}/messages?cursor=broken`)).statusCode,
      400,
    );
  } finally {
    await f.close();
  }
});

test('SSE reauthorizes per family/group and closes on session revocation', async () => {
  const f = await fixture();
  try {
    await f.app.ready();
    const response = f.app.inject({
      url: '/api/events',
      headers: { cookie: 'sid=' + f.child.sid },
    });
    const result = response.then((x) => x);
    for (let i = 0; i < 100 && f.service.listeners.size === 0; i++)
      await new Promise((r) => setImmediate(r));
    assert.ok(f.service.listeners.size);
    f.service.notify(f.b.familyId, group);
    f.service.notify(f.a.familyId, blocked);
    f.service.notify(f.a.familyId, group);
    await f.req(f.child, '/api/logout', 'POST', {});
    const r = await result;
    assert.match(r.body, /event: ready/);
    assert.match(r.body, /120363001@g.us/);
    assert.doesNotMatch(r.body, /120363999|FAMILY_B/);
    assert.match(r.body, /expired/);
    assert.equal((await f.req(f.child, '/api/me')).statusCode, 401);
  } finally {
    await f.close();
  }
});

test('ciphertext storage survives restart, auth is encrypted and disconnect purge scoped', async () => {
  const f = await fixture();
  try {
    await f.ingest(f.a.familyId, 'durable', 'ENCRYPTED_TEXT_MARKER');
    f.store.authSet(f.a.familyId, 'creds', 'AUTH_SECRET_MARKER');
    assert.equal(f.store.authGet(f.a.familyId, 'creds'), 'AUTH_SECRET_MARKER');
    for (const file of readdirSync(f.config.dataDir))
      if (file.startsWith('app.sqlite')) {
        const bytes = readFileSync(join(f.config.dataDir, file));
        assert.equal(bytes.includes(Buffer.from('ENCRYPTED_TEXT_MARKER')), false);
        assert.equal(bytes.includes(Buffer.from('AUTH_SECRET_MARKER')), false);
      }
    const second = new Store(f.config);
    assert.equal(second.getMessage(f.a.familyId, group, 'durable')?.text, 'ENCRYPTED_TEXT_MARKER');
    second.close();
    await f.service.disconnect(f.a.familyId, true);
    assert.equal(f.store.authGet(f.a.familyId, 'creds'), undefined);
    assert.equal(f.store.groups(f.a.familyId).length, 0);
    assert.ok(f.store.groups(f.b.familyId).length > 0);
  } finally {
    await f.close();
  }
});

test('parent-controlled sender signature is enforced on server, isolated, and retained on edits', async () => {
  const f = await fixture();
  try {
    assert.equal(
      (
        await f.req(f.child, '/api/parent/identity', 'PUT', {
          childId: f.a.childId,
          label: 'Forged',
        })
      ).statusCode,
      403,
    );
    assert.equal(
      (
        await f.req(f.parent, '/api/parent/identity', 'PUT', {
          childId: f.b.childId,
          label: 'Cross family',
        })
      ).statusCode,
      404,
    );
    assert.equal(
      (
        await f.req(f.parent, '/api/parent/identity', 'PUT', {
          childId: f.a.childId,
          label: 'מהטאבלט',
        })
      ).statusCode,
      200,
    );
    assert.equal(f.store.identityLabel(f.b.familyId, f.b.childId), '');
    const r = await f.req(f.child, `/api/groups/${group}/messages`, 'POST', {
      clientId: randomUUID(),
      text: 'שלום',
    });
    assert.equal(r.statusCode, 200, r.body);
    const m = r.json().message;
    assert.equal(m.text, 'מהטאבלט: שלום');
    assert.equal(m.authorUserId, f.a.childId);
    assert.equal(
      (await f.req(f.child, `/api/groups/${group}/messages/${m.id}`, 'PATCH', { text: 'עוד שלום' }))
        .statusCode,
      200,
    );
    assert.equal(f.store.getMessage(f.a.familyId, group, m.id)?.text, 'מהטאבלט: עוד שלום');
    await f.ingest(f.a.familyId, 'native-parent-message', 'Sent by parent phone', {
      fromMe: true,
      status: 'sent',
    });
    assert.equal(
      (
        await f.req(f.child, `/api/groups/${group}/messages/native-parent-message`, 'PATCH', {
          text: 'impersonate',
        })
      ).statusCode,
      403,
    );
    const childMe = await f.req(f.child, '/api/me');
    assert.equal(childMe.json().senderLabel, 'מהטאבלט');
    await f.req(f.parent, '/api/parent/identity', 'PUT', { childId: f.a.childId, label: '' });
    assert.equal(f.store.identityLabel(f.a.familyId, f.a.childId), '');
  } finally {
    await f.close();
  }
});

test('terminal pairing state survives service restart, clears invalid auth and stops auto boot', async () => {
  const f = await fixture();
  try {
    f.store.authSet(f.a.familyId, 'creds', 'invalid-credential-marker');
    (f.service.connector(f.a.familyId) as QuietMock).hooks.state('needs_pairing');
    assert.equal(f.store.authGet(f.a.familyId, 'creds'), undefined);
    assert.equal(f.store.one('SELECT linked FROM families WHERE id=?', f.a.familyId).linked, 0);
    await f.service.close();
    const { ChatService } = await import('../src/service.js');
    const restarted = new ChatService(f.store, (h) => new QuietMock(h));
    assert.equal(restarted.states.get(f.a.familyId)?.state, 'needs_pairing');
    await restarted.boot();
    assert.equal(restarted.connectors.has(f.a.familyId), false);
    await restarted.close();
  } finally {
    await f.close();
  }
});

test('missing connector receipts stay unknown and an out-of-order pending echo cannot regress read', async () => {
  const f = await fixture();
  try {
    const connector = f.service.connector(f.a.familyId);
    const original = connector.send.bind(connector);
    connector.send = async (message) => ({ ...(await original(message)), status: 'unknown' });
    const response = await f.req(f.child, `/api/groups/${group}/messages`, 'POST', {
      clientId: randomUUID(),
      text: 'No invented receipt',
    });
    assert.equal(response.statusCode, 200, response.body);
    assert.equal(response.json().message.status, 'unknown');
    const id = response.json().message.id;
    f.service.update(f.a.familyId, group, id, { status: 'read' });
    await f.ingest(f.a.familyId, id, 'No invented receipt', {
      fromMe: true,
      status: 'pending',
      raw: 'test-wire-payload',
    });
    assert.equal(f.store.getMessage(f.a.familyId, group, id)?.status, 'read');
    f.service.update(f.a.familyId, group, id, { status: 'delivered' });
    assert.equal(f.store.getMessage(f.a.familyId, group, id)?.status, 'read');
  } finally {
    await f.close();
  }
});

test('four-character codes work across child creation, child reset and own password change', async () => {
  const f = await fixture();
  try {
    const created = await f.req(f.parent, '/api/parent/children', 'POST', { name: 'קוד קצר', password: 'a1ב2', confirmation: 'a1ב2' });
    assert.equal(created.statusCode, 200);
    const id = created.json().id;
    const login = (name: string, password: string) => f.req(null, '/api/login', 'POST', { name, password });
    assert.equal((await login('קוד קצר', 'a1ב2')).statusCode, 200);
    assert.equal((await f.req(f.parent, '/api/parent/child-password', 'POST', { childId: id, password: '123', confirmation: '123' })).statusCode, 400);
    assert.equal((await f.req(f.parent, '/api/parent/child-password', 'POST', { childId: id, password: '9876', confirmation: '9876' })).statusCode, 200);
    assert.equal((await login('קוד קצר', 'a1ב2')).statusCode, 401);
    assert.equal((await login('קוד קצר', '9876')).statusCode, 200);
    assert.equal((await f.req(f.parent, '/api/password', 'POST', { current: f.password, next: 'Ab12', confirmation: 'Ab12' })).statusCode, 200);
    assert.equal((await login('parent-a', 'Ab12')).statusCode, 200);
    // A different existing account retains its old long password unchanged.
    assert.equal((await login('parent-b', f.password)).statusCode, 200);
  } finally { await f.close(); }
});

test('login guessing budget follows the account across IPs and does not lock the family NAT', async () => {
  const f = await fixture();
  try {
    const login = (name: string, password: string, remoteAddress = '192.0.2.5') => f.app.inject({
      method: 'POST', url: '/api/login', headers: { origin: f.config.origin }, remoteAddress, payload: { name, password } });
    for (let i = 0; i < 8; i++) assert.equal((await login('child-a', 'wrong', `192.0.2.${i}`)).statusCode, 401);
    const blocked = await login('CHILD-A', f.password + 'child');
    assert.equal(blocked.statusCode, 429);
    assert.equal(blocked.json().error, 'LOGIN_WAIT');
    assert.equal(blocked.headers['retry-after'], '900');
    assert.equal((await login('parent-a', f.password)).statusCode, 200);
    assert.equal((await login('child-b', f.password + 'child')).statusCode, 200);
    // A successful login for someone else never clears this account's budget.
    assert.equal((await login('child-a', f.password + 'child')).statusCode, 429);
  } finally { await f.close(); }
});

test('credential throttle is durable, bounded, expires, and does not reset the network budget on success', async () => {
  const { CredentialThrottle } = await import('../src/credential-throttle.js');
  const f = await fixture();
  try {
    const now = Date.now(), throttle = new CredentialThrottle(f.store);
    for (let i = 0; i < 8; i++) assert.equal(throttle.reserve('account:synthetic', '192.0.2.1', now), true);
    const secondStore = new Store(f.config);
    try { assert.equal(new CredentialThrottle(secondStore).reserve('account:synthetic', '192.0.2.2', now), false); }
    finally { secondStore.close(); }
    assert.equal(throttle.reserve('account:synthetic', '192.0.2.1', now + 900000), true);
    for (let i = 0; i < 79; i++) {
      throttle.clear('account:synthetic');
      assert.equal(throttle.reserve('account:synthetic', '192.0.2.1', now + 900000), true);
    }
    throttle.clear('account:synthetic');
    assert.equal(throttle.reserve('account:synthetic', '192.0.2.1', now + 900000), false);
    assert.ok(f.store.all('SELECT key FROM credential_attempts').every(row => /^[a-f0-9]{64}$/.test(row.key)));
  } finally { await f.close(); }
});

test('short code hashing preserves salted scrypt format and rejects fewer than four characters', async () => {
  const { passwordHash, verifyPassword } = await import('../src/crypto.js');
  await assert.rejects(passwordHash('123'));
  const a = await passwordHash('1234'), b = await passwordHash('1234');
  assert.match(a, /^[a-f0-9]{32}:[a-f0-9]{128}$/);
  assert.notEqual(a, b);
  assert.equal(await verifyPassword('1234', a), true);
  assert.equal(await verifyPassword('4321', a), false);
});

test('password re-verification shares account budget and explicit child reset restores access', async () => {
  const f = await fixture();
  try {
    for (let i = 0; i < 8; i++) assert.equal((await f.req(f.child, '/api/password', 'POST', {
      current: 'wrong', next: '1234', confirmation: '1234' })).statusCode, 403);
    assert.equal((await f.req(null, '/api/login', 'POST', { name: 'child-a', password: f.password + 'child' })).statusCode, 429);
    assert.equal((await f.req(f.parent, '/api/parent/child-password', 'POST', {
      childId: f.a.childId, password: '5678', confirmation: '5678' })).statusCode, 200);
    assert.equal((await f.req(null, '/api/login', 'POST', { name: 'child-a', password: '5678' })).statusCode, 200);
  } finally { await f.close(); }
});
