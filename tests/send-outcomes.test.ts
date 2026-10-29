import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, rmSync, readFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { randomBytes, randomUUID } from 'node:crypto';
import { Store, type Session } from '../src/store.js';
import { createApp } from '../src/server.js';
import { SendNotSubmittedError, sendRequestHash } from '../src/send-outcome.js';
import type { Config } from '../src/config.js';
import type { Outgoing, Incoming } from '../src/connectors/types.js';

const idFor = (id: string) => 'FC' + id.replaceAll('-', '').toUpperCase();
const ack = (m: Outgoing): Incoming => ({ id: m.id, groupId: m.groupId, sender: 'Synthetic', fromMe: true,
  text: m.text, kind: m.kind, time: Date.now(), status: 'sent', raw: '{}', replyId: m.reply?.id });
async function fixture() {
  const dir = mkdtempSync(join(tmpdir(), 'yahad-send-outcomes-synthetic-'));
  const config: Config = { dataDir: dir, masterKey: randomBytes(32), connector: 'mock', realAllowed: false,
    host: '127.0.0.1', port: 0, origin: 'http://localhost:3000', secureCookie: false,
    maxMedia: 1048576, maxStorage: 64 * 1048576, childIdleMs: 900000, sessionMs: 3600000, diagnosticsEnabled: false };
  let now = Date.now();
  const store = new Store(config, { usageClock: () => now });
  const family = await store.createFamily('Synthetic', { name: 'send-parent', password: 'synthetic password' }, { name: 'send-child', password: 'synthetic password' });
  const session = (id = family.childId) => { const s = store.createSession(store.user(store.one('SELECT * FROM users WHERE id=?', id))); return store.session(s.sid)!; };
  const parent = session(family.parentId), child = session(), second = session();
  const { app, service } = await createApp(config, { store, boot: false });
  await service.connect(family.familyId);
  const group = '120363001@g.us', path = '/api/groups/' + group;
  await service.grant(family.familyId, family.childId, group, true);
  store.usage.setTimezone(family.familyId, 'UTC');
  const req = (s: Session, url: string, method: any = 'GET', payload?: any, headers = {}) => app.inject({ method, url, payload,
    headers: { cookie: 'sid=' + s.sid, origin: config.origin, 'x-csrf-token': s.csrf, ...headers } });
  const c = service.connector(family.familyId); let entries = 0;
  c.send = async m => { m.beforeSend?.(); entries++; return ack(m); };
  const tab = store.usage.open(child).tab;
  const nearDeadline = () => {
    store.usage.setPolicy(family.familyId, family.childId, 1);
    store.usage.heartbeat(child, tab, 1, true);
    for (let seq = 2; seq <= 4; seq++) { now += 15000; store.usage.heartbeat(child, tab, seq, true); }
    now += 14999;
    assert.equal(store.usage.status(family.familyId, family.childId).remainingMs, 1);
  };
  const bonus = async () => {
    const status = store.usage.status(family.familyId, family.childId);
    const result = await req(parent, '/api/parent/usage/' + family.childId + '/bonus', 'POST',
      { requestId: randomUUID(), day: status.day, dayKey: status.dayKey, minutes: 7 });
    assert.equal(result.statusCode, 200, result.body);
    store.usage.heartbeat(child, tab, 5, true);
  };
  return { config, dir, family, store, session, parent, child, second, app, service, group, path, req, c, nearDeadline, bonus,
    entries: () => entries, advance(ms: number) { now += ms; }, now: () => now,
    post: (body: any, s = child) => req(s, path + '/messages', 'POST', body),
    state: (id: string, s = child) => req(s, path + '/outgoing/' + id),
    row: (id: string) => store.getMessage(family.familyId, group, idFor(id)),
    async close() { app.server.closeAllConnections(); await app.close(); store.close(); rmSync(dir, { force: true, recursive: true }); } };
}

test('provider ACK across exact expiry is retained as sent; bonus unlocks check and dedupe without another provider call', async () => {
  const f = await fixture();
  try {
    f.nearDeadline(); let calls = 0;
    f.c.send = async m => { m.beforeSend?.(); calls++; f.advance(1); return ack(m); };
    const body = { clientId: randomUUID(), text: 'Synthetic late acknowledgement' };
    const result = await f.post(body); assert.equal(result.statusCode, 403); assert.equal(result.json().error, 'USAGE_EXHAUSTED');
    assert.equal(f.row(body.clientId)?.status, 'sent'); assert.notEqual(f.row(body.clientId)?.retrySafe, true);
    assert.equal((await f.state(body.clientId)).statusCode, 403);
    assert.equal((await f.post({ clientId: randomUUID(), text: 'New blocked' })).statusCode, 403); assert.equal(calls, 1);
    const parentMessages = (await f.req(f.parent, f.path + '/messages')).json().messages;
    assert.equal(parentMessages.find((m: any) => m.id === idFor(body.clientId)).status, 'sent');
    assert.equal(JSON.stringify(parentMessages).includes('sendRequestHash'), false);
    await f.bonus(); assert.equal((await f.state(body.clientId)).json().state, 'confirmed');
    assert.equal((await f.post(body)).statusCode, 200); assert.equal(calls, 1);
  } finally { await f.close(); }
});

test('new submission at the exact deadline is denied; completion one millisecond before succeeds', async () => {
  for (const expired of [false, true]) {
    const f = await fixture();
    try { f.nearDeadline(); if (expired) f.advance(1);
      const result = await f.post({ clientId: randomUUID(), text: 'Boundary' });
      assert.equal(result.statusCode, expired ? 403 : 200); assert.equal(f.entries(), expired ? 0 : 1);
    } finally { await f.close(); }
  }
});

test('delayed preparation crossing expiry never enters provider; proven failure can retry explicitly after a bonus', async () => {
  const f = await fixture();
  try {
    f.nearDeadline(); let calls = 0;
    f.c.send = async m => { f.advance(1); m.beforeSend?.(); calls++; return ack(m); };
    const body = { clientId: randomUUID(), text: 'Prepared too late' };
    assert.equal((await f.post(body)).statusCode, 403); assert.equal(calls, 0);
    assert.equal(f.row(body.clientId)?.status, 'failed'); assert.equal(f.row(body.clientId)?.retrySafe, true);
    await f.bonus(); assert.equal((await f.state(body.clientId)).json().state, 'failed');
    f.c.send = async m => { m.beforeSend?.(); calls++; return ack(m); };
    assert.equal((await f.post(body)).statusCode, 200); assert.equal(calls, 1);
  } finally { await f.close(); }
});

test('two tabs racing one stable ID cannot both submit; changed payload and another author cannot reuse it', async () => {
  const f = await fixture();
  try {
    let release!: () => void, began!: () => void, calls = 0;
    const held = new Promise<void>(r => { release = r; }), started = new Promise<void>(r => { began = r; });
    f.c.send = async m => { m.beforeSend?.(); calls++; began(); await held; return ack(m); };
    const body = { clientId: randomUUID(), text: 'Only once' }, first = f.post(body).then(r => r);
    await started;
    assert.equal((await f.state(body.clientId, f.second)).json().state, 'pending');
    assert.equal((await f.post(body, f.second)).json().error, 'SEND_PENDING');
    assert.equal((await f.post({ ...body, text: 'Different' }, f.second)).json().error, 'SEND_ID_CONFLICT');
    assert.equal((await f.post(body, f.parent)).statusCode, 404);
    assert.equal((await f.state(body.clientId, f.parent)).statusCode, 404);
    release(); assert.equal((await first).statusCode, 200); assert.equal(calls, 1);
    assert.equal((await f.post(body, f.second)).statusCode, 200); assert.equal(calls, 1);
  } finally { await f.close(); }
});

test('unknown provider outcome is durable, has no retry button permission and never resubmits after restart', async () => {
  const f = await fixture();
  try {
    let calls = 0; f.c.send = async m => { m.beforeSend?.(); calls++; throw new Error('Synthetic lost ACK after handoff'); };
    const body = { clientId: randomUUID(), text: 'Unknown outcome' };
    assert.equal((await f.post(body)).json().error, 'SEND_UNCERTAIN'); assert.equal(f.row(body.clientId)?.status, 'unknown');
    assert.equal((await f.state(body.clientId)).json().state, 'unknown');
    assert.equal((await f.post(body)).json().error, 'SEND_UNCERTAIN'); assert.equal(calls, 1);
    const persisted = new Store(f.config, { usageClock: f.now });
    const reopened = await createApp(f.config, { store: persisted, boot: false });
    try {
      await reopened.service.connect(f.family.familyId);
      reopened.service.connector(f.family.familyId).send = async () => { calls++; throw new Error('Must not run'); };
      const response = await reopened.app.inject({ method: 'POST', url: f.path + '/messages', payload: body,
        headers: { cookie: 'sid=' + f.child.sid, origin: f.config.origin, 'x-csrf-token': f.child.csrf } });
      assert.equal(response.json().error, 'SEND_UNCERTAIN'); assert.equal(calls, 1);
      assert.equal(reopened.service.public(f.family.familyId, persisted.getMessage(f.family.familyId, f.group, idFor(body.clientId))!).retrySafe, false);
    } finally { await reopened.app.close(); persisted.close(); }
  } finally { await f.close(); }
});

test('legacy failed and abandoned pending records are uncertain; confirmed provider echoes reconcile them', async () => {
  const f = await fixture();
  try {
    for (const status of ['failed', 'pending'] as const) {
      const body = { clientId: randomUUID(), text: 'Legacy ' + status }, id = idFor(body.clientId);
      f.store.putMessage(f.family.familyId, { ...ack({ ...body, id, groupId: f.group, kind: 'text' }), status, reactions: {}, clientId: body.clientId, authorUserId: f.child.user.id });
      assert.equal((await f.post(body)).json().error, 'SEND_UNCERTAIN'); assert.equal(f.entries(), 0);
      await f.service.ingest(f.family.familyId, { ...ack({ id, groupId: f.group, text: body.text, kind: 'text' }), raw: '{}' });
      assert.equal((await f.state(body.clientId)).json().state, 'confirmed');
    }
  } finally { await f.close(); }
});

test('typed preparation rejection is safely retryable; ordinary connector errors without proof stay unknown', async () => {
  const f = await fixture();
  try {
    const body = { clientId: randomUUID(), text: 'Prepare only' };
    f.c.send = async () => { throw new SendNotSubmittedError(new Error('Synthetic unavailable')); };
    assert.equal((await f.post(body)).json().error, 'SEND_NOT_SUBMITTED');
    assert.equal((await f.state(body.clientId)).json().state, 'failed');
    f.c.send = async m => { m.beforeSend?.(); return ack(m); };
    assert.equal((await f.post(body)).statusCode, 200);
    f.c.send = async () => { throw new Error('Untyped error cannot prove no handoff'); };
    const unknown = { clientId: randomUUID(), text: 'Conservative' };
    assert.equal((await f.post(unknown)).json().error, 'SEND_UNCERTAIN'); assert.equal(f.row(unknown.clientId)?.retrySafe, false);
  } finally { await f.close(); }
});

test('pause during preparation blocks provider even after resume; pause after handoff retains confirmed ACK', async () => {
  for (const handoff of [false, true]) {
    const f = await fixture();
    try {
      let release!: () => void, began!: () => void, calls = 0;
      const held = new Promise<void>(r => { release = r; }), started = new Promise<void>(r => { began = r; });
      f.c.send = async m => { if (handoff) { m.beforeSend?.(); calls++; } began(); await held;
        if (!handoff) { m.beforeSend?.(); calls++; } return ack(m); };
      const body = { clientId: randomUUID(), text: 'Pause race' }, request = f.post(body).then(r => r);
      await started;
      assert.equal((await f.req(f.parent, '/api/parent/child-access', 'PUT', { childId: f.family.childId, paused: true, revision: 0 })).statusCode, 200);
      assert.equal((await f.post({ clientId: randomUUID(), text: 'While paused' })).statusCode, 401);
      assert.equal((await f.req(f.parent, '/api/parent/child-access', 'PUT', { childId: f.family.childId, paused: false, revision: 1 })).statusCode, 200);
      release(); assert.equal((await request).statusCode, 401); assert.equal(calls, handoff ? 1 : 0);
      const fresh = f.session(); assert.equal((await f.state(body.clientId, fresh)).json().state, handoff ? 'confirmed' : 'failed');
      f.c.send = async m => { m.beforeSend?.(); calls++; return ack(m); };
      assert.equal((await f.post(body, fresh)).statusCode, 200); assert.equal(calls, 1);
    } finally { await f.close(); }
  }
});

test('quote change during preparation cancels safely; retry cannot bypass original-message validation', async () => {
  const f = await fixture();
  try {
    const quoted = { ...ack({ id: 'quoted', groupId: f.group, text: 'Original', kind: 'text' }), fromMe: false, status: 'sent' as const, reactions: {} };
    f.store.putMessage(f.family.familyId, quoted);
    let calls = 0;
    f.c.send = async m => { f.service.update(f.family.familyId, f.group, 'quoted', { deleted: true }); m.beforeSend?.(); calls++; return ack(m); };
    const body = { clientId: randomUUID(), text: 'Quoted reply', replyId: 'quoted' };
    assert.equal((await f.post(body)).json().error, 'ORIGINAL_UNAVAILABLE'); assert.equal(calls, 0);
    assert.equal(f.row(body.clientId)?.retrySafe, true);
    assert.equal((await f.post(body)).statusCode, 404); assert.equal(calls, 0);
  } finally { await f.close(); }
});

test('loopback client disconnection during a request does not lose ACK or resubmit on status check', async () => {
  const f = await fixture();
  try {
    let release!: () => void, began!: () => void, calls = 0;
    const held = new Promise<void>(r => { release = r; }), started = new Promise<void>(r => { began = r; });
    f.c.send = async m => { m.beforeSend?.(); calls++; began(); await held; return ack(m); };
    const address = await f.app.listen({ host: '127.0.0.1', port: 0 }), abort = new AbortController();
    const body = { clientId: randomUUID(), text: 'Synthetic disconnected client' };
    const request = fetch(address + f.path + '/messages', { method: 'POST', signal: abort.signal,
      headers: { cookie: 'sid=' + f.child.sid, origin: f.config.origin, 'x-csrf-token': f.child.csrf, 'content-type': 'application/json' }, body: JSON.stringify(body) });
    await started; abort.abort(); await assert.rejects(request, { name: 'AbortError' }); release();
    for (let i = 0; i < 50 && f.row(body.clientId)?.status !== 'sent'; i++) await new Promise(r => setImmediate(r));
    assert.equal(f.row(body.clientId)?.status, 'sent'); assert.equal((await f.state(body.clientId)).json().state, 'confirmed');
    assert.equal((await f.post(body)).statusCode, 200); assert.equal(calls, 1);
  } finally { await f.close(); }
});

test('request fingerprint binds quote and raw attachment bytes without exposing them', () => {
  const body = { text: 'Synthetic', replyId: 'one' }, file = { filename: 'a.bin', mime: 'application/octet-stream', bytes: Buffer.from('synthetic') };
  assert.equal(sendRequestHash(body, file), sendRequestHash(body, { ...file }));
  assert.notEqual(sendRequestHash(body, file), sendRequestHash({ ...body, replyId: 'two' }, file));
  assert.notEqual(sendRequestHash(body, file), sendRequestHash(body, { ...file, bytes: Buffer.from('different') }));
});

function upload(f: Awaited<ReturnType<typeof fixture>>, clientId: string, bytes: Buffer, sticker = false, text = 'Synthetic caption', voice = false) {
  const boundary = '----synthetic-send-outcomes';
  const fields = { clientId, text, sticker: String(sticker), voice: String(voice) };
  const chunks: Buffer[] = Object.entries(fields).map(([name, value]) => Buffer.from(`--${boundary}\r\nContent-Disposition: form-data; name="${name}"\r\n\r\n${value}\r\n`));
  chunks.push(Buffer.from(`--${boundary}\r\nContent-Disposition: form-data; name="file"; filename="synthetic.bin"\r\nContent-Type: application/octet-stream\r\n\r\n`), bytes, Buffer.from(`\r\n--${boundary}--\r\n`));
  return f.req(f.child, f.path + '/media', 'POST', Buffer.concat(chunks), { 'content-type': `multipart/form-data; boundary=${boundary}` });
}

test('attachment ACK after expiry preserves media bytes for parent, and renewal cannot upload it twice', async () => {
  const f = await fixture();
  try {
    f.nearDeadline(); let calls = 0;
    f.c.send = async m => { m.beforeSend?.(); calls++; f.advance(1); return ack(m); };
    const id = randomUUID(), bytes = readFileSync('fixtures/garden.jpg');
    assert.equal((await upload(f, id, bytes)).statusCode, 403);
    const row = f.row(id)!; assert.equal(row.status, 'sent'); assert.ok(row.mediaId);
    assert.deepEqual((await f.req(f.parent, f.path + '/media/' + row.mediaId)).rawPayload, bytes);
    assert.equal((await f.req(f.child, f.path + '/media/' + row.mediaId)).statusCode, 403);
    await f.bonus(); assert.equal((await upload(f, id, bytes)).statusCode, 200); assert.equal(calls, 1);
    assert.equal((await upload(f, id, Buffer.from('changed bytes'))).json().error, 'SEND_ID_CONFLICT'); assert.equal(calls, 1);
  } finally { await f.close(); }
});

test('confirmed identity marker at expiry is reused after a bonus; attachment is not submitted early', async () => {
  const f = await fixture();
  try {
    f.nearDeadline(); f.store.setIdentityLabel(f.family.familyId, f.family.childId, 'Synthetic child');
    const submitted: string[] = [];
    f.c.send = async m => { m.beforeSend?.(); submitted.push(m.id); if (m.id.endsWith('L')) f.advance(1); return ack(m); };
    const id = randomUUID(), bytes = readFileSync('fixtures/flower.webp');
    const result = await upload(f, id, bytes, true, ''); assert.equal(result.statusCode, 403);
    assert.equal(result.json().sendOutcome, 'not_submitted');
    assert.deepEqual(submitted, [idFor(id) + 'L']); assert.equal(f.row(id)?.retrySafe, true);
    await f.bonus(); assert.equal((await upload(f, id, bytes, true, '')).statusCode, 200);
    assert.deepEqual(submitted, [idFor(id) + 'L', idFor(id)]);
    assert.equal(f.row(id)?.replyId, idFor(id) + 'L'); assert.equal(f.row(id)?.authorUserId, f.family.childId);
  } finally { await f.close(); }
});

test('unknown identity marker blocks the attachment and every retry without another marker submission', async () => {
  const f = await fixture();
  try {
    f.store.setIdentityLabel(f.family.familyId, f.family.childId, 'Synthetic child'); let calls = 0;
    f.c.send = async m => { m.beforeSend?.(); calls++; throw new Error('Synthetic lost marker ACK'); };
    const id = randomUUID(), bytes = readFileSync('fixtures/flower.webp');
    const result = await upload(f, id, bytes, true, ''); assert.equal(result.json().error, 'SEND_UNCERTAIN');
    assert.equal(result.json().sendOutcome, undefined); assert.equal(f.row(id)?.status, 'unknown');
    assert.equal((await upload(f, id, bytes, true, '')).json().error, 'SEND_UNCERTAIN'); assert.equal(calls, 1);
  } finally { await f.close(); }
});

test('a typed error after handoff cannot advertise a safe retry; matching ACK without delivery receipt remains confirmed', async () => {
  const f = await fixture();
  try {
    f.c.send = async m => { m.beforeSend?.(); throw new SendNotSubmittedError(new Error('Incorrect connector proof after handoff')); };
    const unknown = { clientId: randomUUID(), text: 'Uncertain' }, response = await f.post(unknown);
    assert.equal(response.json().error, 'SEND_UNCERTAIN'); assert.equal(response.json().sendOutcome, undefined);
    assert.equal(f.row(unknown.clientId)?.retrySafe, false);
    f.c.send = async m => { m.beforeSend?.(); return { ...ack(m), status: 'unknown' }; };
    const body = { clientId: randomUUID(), text: 'Confirmed submission, unknown delivery' };
    const confirmed = await f.post(body); assert.equal(confirmed.statusCode, 200);
    assert.equal(confirmed.json().message.status, 'unknown'); assert.equal(confirmed.json().message.sendAcknowledged, undefined);
    assert.equal((await f.state(body.clientId)).json().state, 'confirmed');
    assert.equal((await f.req(f.child, f.path + '/messages/' + idFor(body.clientId), 'PATCH', { text: 'Edited' })).statusCode, 200);
    assert.equal((await f.req(f.child, f.path + '/messages/' + idFor(body.clientId), 'DELETE')).statusCode, 200);
    assert.equal((await f.post(body)).statusCode, 200, 'deletion does not permit resubmission');
  } finally { await f.close(); }
});

test('definite preflight failure reports non-submission without a persisted row, but never overrides an existing unknown ID', async () => {
  const f = await fixture();
  try {
    const body = { clientId: randomUUID(), text: 'Keep my draft' };
    f.service.states.set(f.family.familyId, { state: 'reconnecting' });
    const response = await f.post(body); assert.equal(response.json().error, 'NOT_CONNECTED');
    assert.equal(response.json().sendOutcome, 'not_submitted'); assert.equal(f.row(body.clientId), undefined);
    f.service.states.set(f.family.familyId, { state: 'connected' });
    const id = randomUUID(), invalid = await upload(f, id, Buffer.from('not webp'), true, '');
    assert.equal(invalid.json().error, 'WEBP_REQUIRED'); assert.equal(invalid.json().sendOutcome, 'not_submitted');
    f.c.send = async m => { m.beforeSend?.(); throw new Error('Unknown'); };
    assert.equal((await f.post(body)).json().sendOutcome, undefined);
    f.service.states.set(f.family.familyId, { state: 'reconnecting' });
    const retry = await f.post(body); assert.equal(retry.json().error, 'SEND_UNCERTAIN'); assert.equal(retry.json().sendOutcome, undefined);
  } finally { await f.close(); }
});

test('forgetting and re-adding a conversation while ACK is in flight cannot resurrect its old content', async () => {
  const f = await fixture();
  try {
    let release!: () => void, began!: () => void;
    const held = new Promise<void>(r => { release = r; }), started = new Promise<void>(r => { began = r; });
    f.c.send = async m => { m.beforeSend?.(); began(); await held; return ack(m); };
    const body = { clientId: randomUUID(), text: 'Forgotten synthetic' }, request = f.post(body).then(r => r);
    await started; f.store.purgeGroup(f.family.familyId, f.group);
    f.store.syncGroups(f.family.familyId, f.store.groups(f.family.familyId)); // New retention generation.
    const competing = await f.post(body); assert.equal(competing.json().error, 'SEND_PENDING');
    assert.equal((await f.post(body, f.parent)).statusCode, 404);
    assert.equal((await f.state(body.clientId, f.parent)).statusCode, 404);
    assert.equal((await f.state(body.clientId)).json().state, 'pending');
    release(); assert.equal((await request).statusCode, 404); assert.equal(f.row(body.clientId), undefined);
    assert.equal((await f.state(body.clientId)).json().state, 'not_found');
  } finally { await f.close(); }
});

test('one stable ID reserves asynchronous media preparation across tabs, rejects conflicts and releases safely at expiry', async () => {
  const f = await fixture();
  try {
    f.nearDeadline(); let began!: () => void, release!: () => void;
    const started = new Promise<void>(r => { began = r; }), held = new Promise<void>(r => { release = r; });
    const measure = f.store.diagnostics.measureAsync.bind(f.store.diagnostics), audio = readFileSync('fixtures/audio.ogg');
    f.store.diagnostics.measureAsync = (async (family: string, operation: string, work: () => Promise<any>) => {
      if (operation !== 'audio_encode') return measure(family, operation as any, work);
      began(); await held; return audio;
    }) as typeof f.store.diagnostics.measureAsync;
    const id = randomUUID(), request = upload(f, id, audio, false, '', true).then(r => r);
    await started;
    f.store.usage.heartbeat(f.second, f.store.usage.open(f.second).tab, 1, true);
    assert.equal((await f.state(id, f.second)).json().state, 'pending');
    assert.equal((await upload(f, id, audio, false, '', true)).json().error, 'SEND_PENDING');
    const conflict = await upload(f, id, Buffer.from('invalid sticker'), true, '');
    assert.equal(conflict.json().error, 'SEND_ID_CONFLICT'); assert.equal(conflict.json().sendOutcome, undefined);
    assert.equal((await f.post({ clientId: id, text: 'Different request' }, f.second)).json().error, 'SEND_ID_CONFLICT');
    f.advance(1); release();
    const response = await request; assert.equal(response.statusCode, 403);
    assert.equal(response.json().sendOutcome, 'not_submitted'); assert.equal(f.entries(), 0);
    assert.equal(f.row(id), undefined);
    await f.bonus();
    assert.equal((await upload(f, id, audio, false, '', true)).statusCode, 200); assert.equal(f.entries(), 1);
  } finally { await f.close(); }
});
