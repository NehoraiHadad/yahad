import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, readdirSync, rmSync, statSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { randomBytes } from 'node:crypto';
import { DisconnectReason } from '@whiskeysockets/baileys';
import { clientErrorCodes, errorCatalog, errorText } from '../web/errorCatalog';
import { toastExpires } from '../web/ErrorToast';
import { createApp } from '../src/server.js';
import { BaileysConnector } from '../src/connectors/baileys.js';
import { PairingDataUnreadable, type ConnectorHooks } from '../src/connectors/types.js';
import { Diagnostics, ERROR_EVENT_LIMIT } from '../src/diagnostics.js';
import { logServerFailure } from '../src/logging.js';
import type { Config } from '../src/config.js';

const root = new URL('..', import.meta.url);
function sources(dir: string): string[] {
  return readdirSync(new URL(dir, root)).flatMap(name => {
    const path = `${dir}/${name}`;
    return statSync(new URL(path, root)).isDirectory() ? sources(path) : path.endsWith('.ts') ? [path] : [];
  });
}
/** Every code the server can put in an error response: the literal arguments of its error
 * classes, the voice validator's codes and the handlers that send `error: 'CODE'` directly. */
function serverErrorCodes() {
  const codes = new Set<string>();
  for (const path of sources('src')) {
    const text = readFileSync(new URL(path, root), 'utf8');
    for (const match of text.matchAll(/new (AppError|UsageError|ChildAccessError|VoiceRejection)\(/g)) {
      let depth = 1, end = match.index! + match[0].length;
      while (depth && end < text.length) { const c = text[end++]; if (c === '(') depth++; else if (c === ')') depth--; }
      for (const literal of text.slice(match.index! + match[0].length, end).matchAll(/'([A-Z][A-Z0-9_]+)'/g)) codes.add(literal[1]);
    }
    for (const literal of text.matchAll(/\berror: '([A-Z][A-Z0-9_]+)'/g)) codes.add(literal[1]);
    const voice = text.match(/export type VoiceRejectionCode =([^;]+);/);
    if (voice) for (const literal of voice[1].matchAll(/'([A-Z][A-Z0-9_]+)'/g)) codes.add(literal[1]);
  }
  return codes;
}

test('every error code the server can return has a catalog entry, and the catalog has no dead codes', () => {
  const server = serverErrorCodes();
  assert.ok(server.size > 50, `scan found ${server.size} codes`);
  for (const code of ['LOGIN_BUSY', 'SEND_RATE_LIMIT', 'UPLOAD_BUSY', 'CHILD_CREATE_BUSY', 'ORIGIN_DENIED', 'CSRF_DENIED',
    'PAIRING_UNREADABLE', 'GROUP_SYNC_FAILED', 'AUDIO_NOT_OPUS', 'NOT_FOUND', 'REQUEST_FAILED']) assert.ok(server.has(code), code);
  const missing = [...server].filter(code => !(code in errorCatalog));
  assert.deepEqual(missing, [], 'codes without a catalog entry');
  const dead = Object.keys(errorCatalog).filter(code => !server.has(code) && !(clientErrorCodes as readonly string[]).includes(code));
  assert.deepEqual(dead, [], 'catalog entries no code path produces');
  for (const [code, entry] of Object.entries(errorCatalog)) {
    assert.ok(entry.text.trim(), code);
    assert.ok(!/[A-Z]{3,}_[A-Z]/.test(entry.text), `${code}: the default text is shown to children and must not name codes`);
  }
});

test('children never see admin wording; parents get the specific cause', () => {
  for (const code of ['LOGIN_WAIT', 'USAGE_CLOCK', 'SEND_ID_CONFLICT', 'PAIRING_UNREADABLE', 'CSRF_DENIED']) {
    const child = errorText(code, 'child');
    assert.ok(!/מנהל|הגדרות|QR|רכיב|מזהה|השעון של המחשב/.test(child), `${code}: ${child}`);
    assert.ok(!/[A-Z]{3,}_[A-Z]/.test(child), code);
  }
  assert.notEqual(errorText('PAIRING_UNREADABLE', 'parent'), errorText('PAIRING_UNREADABLE', 'child'));
  assert.equal(errorText('SOMETHING_NEW'), errorText('SOMETHING_ELSE'), 'unknown codes share one honest fallback');
});

test('SLOW_DOWN is gone: each limit has its own code, and a busy child creation is a conflict, not a rate limit', () => {
  for (const path of [...sources('src'), ...sources('web')]) assert.ok(!readFileSync(new URL(path, root), 'utf8').includes('SLOW_DOWN'), path);
  const server = readFileSync(new URL('src/server.ts', root), 'utf8');
  assert.match(server, /AppError\(409, 'CHILD_CREATE_BUSY'\)/);
  assert.match(server, /AppError\(429, 'SEND_RATE_LIMIT'\)/);
  assert.match(server, /AppError\(429, 'UPLOAD_BUSY'\)/);
  assert.match(server, /AppError\(429, 'LOGIN_BUSY'\)/);
  assert.equal(errorCatalog.CHILD_CREATE_BUSY.retry, true);
  assert.match(errorCatalog.CHILD_CREATE_BUSY.text, /לא נוצר/, 'says the account was not created');
});

test('only transient errors leave the notification row on their own', () => {
  assert.equal(toastExpires({ text: 'x', code: 'NETWORK_UNAVAILABLE', category: 'TRANSIENT' }), true);
  assert.equal(toastExpires({ text: 'x', code: 'PAIRING_UNREADABLE', category: 'USER_ACTION' }), false);
  assert.equal(toastExpires({ text: 'x', code: 'REQUEST_FAILED', category: 'DEFECT' }), false);
  assert.equal(toastExpires({ text: 'local message' }), false);
});

function fixture() {
  const dir = mkdtempSync(join(tmpdir(), 'yachad-errors-'));
  const config: Config = { dataDir: dir, masterKey: randomBytes(32), connector: 'mock', realAllowed: false,
    host: '127.0.0.1', port: 3000, origin: 'http://localhost:3000', secureCookie: false,
    maxMedia: 1048576, maxStorage: 64 * 1048576, childIdleMs: 60000, sessionMs: 3600000,
    updateStatusFile: join(dir, 'root-updater-status.json') };
  return { dir, config, close: () => rmSync(dir, { recursive: true, force: true }) };
}

test('error responses carry the request reference, and the parent report lists them without content', async () => {
  const f = fixture(); const { app, store } = await createApp(f.config, { boot: false });
  try {
    const password = randomBytes(24).toString('hex');
    const a = await store.createFamily('A', { name: 'pa', password }, { name: 'ca', password });
    const session = (id: string) => store.createSession(store.user(store.one('SELECT * FROM users WHERE family_id=? AND id=?', a.familyId, id)));
    const pa = session(a.parentId), ca = session(a.childId);
    const req = (s: typeof pa, url: string, method: any = 'GET', payload?: any, headers: Record<string, string> = {}) =>
      app.inject({ url, method, payload, headers: { origin: f.config.origin, cookie: 'sid=' + s.sid, 'x-csrf-token': s.csrf, ...headers } });

    const denied = await req(ca, '/api/parent/diagnostics');
    assert.equal(denied.statusCode, 403);
    assert.equal(denied.json().error, 'PARENT_ONLY');
    assert.match(denied.json().ref, /^[a-f0-9]{10}$/);
    assert.equal(denied.headers['x-request-id'], denied.json().ref);
    const ok = await req(pa, '/api/me');
    assert.match(String(ok.headers['x-request-id']), /^[a-f0-9]{10}$/);
    assert.notEqual(ok.headers['x-request-id'], denied.json().ref);

    // A forged inbound request id is never echoed back.
    const forged = await req(ca, '/api/parent/diagnostics', 'GET', undefined, { 'x-request-id': 'SECRET-forged-id' });
    assert.notEqual(forged.json().ref, 'SECRET-forged-id');

    // Invalid JSON is the caller's mistake (400), not a server failure.
    const invalid = await app.inject({ url: '/api/parent/identity', method: 'PUT', payload: '{"label":',
      headers: { origin: f.config.origin, cookie: 'sid=' + pa.sid, 'x-csrf-token': pa.csrf, 'content-type': 'application/json' } });
    assert.equal(invalid.statusCode, 400);
    assert.equal(invalid.json().error, 'INVALID_INPUT');

    const crash = await req(pa, '/api/client-error', 'POST', { kind: 'render', errorClass: 'TypeError', ref: 'abcdef0123' });
    assert.equal(crash.statusCode, 200);
    assert.equal((await req(pa, '/api/client-error', 'POST', { kind: 'render', errorClass: 'TypeError', ref: 'x', message: 'SECRET' })).statusCode, 400);

    const report = (await req(pa, '/api/parent/diagnostics')).json();
    assert.match(report.appVersion, /^\d+\.\d+\.\d+/);
    // The child's PARENT_ONLY is recorded in the family the parent sees.
    const recorded = report.recentErrors.find((event: any) => event.ref === denied.json().ref);
    assert.deepEqual({ code: recorded.code, status: recorded.status, method: recorded.method, route: recorded.route },
      { code: 'PARENT_ONLY', status: 403, method: 'GET', route: '/api/parent/diagnostics' });
    assert.ok(report.recentErrors.some((event: any) => event.code === 'CLIENT_RENDER_FAILED' && event.ref === 'abcdef0123'
      && event.errorClass === 'TypeError' && event.route === 'client:render'));
    assert.ok(report.recentErrors.some((event: any) => event.code === 'INVALID_INPUT' && event.route === '/api/parent/identity'));
    const serialized = JSON.stringify(report);
    for (const secret of ['SECRET', password, pa.sid, pa.csrf, '"label"']) assert.ok(!serialized.includes(secret), secret);
  } finally { await app.close(); f.close(); }
});

test('a cross-site write is ORIGIN_DENIED; a replaced session token is CSRF_DENIED', async () => {
  const f = fixture(); const { app, store } = await createApp(f.config, { boot: false });
  try {
    const password = randomBytes(24).toString('hex');
    const a = await store.createFamily('A', { name: 'pa', password }, { name: 'ca', password });
    const pa = store.createSession(store.user(store.one('SELECT * FROM users WHERE family_id=? AND id=?', a.familyId, a.parentId)));
    const write = (headers: Record<string, string>) => app.inject({ url: '/api/parent/sync-groups', method: 'POST', headers: { cookie: 'sid=' + pa.sid, ...headers } });
    assert.equal((await write({ 'x-csrf-token': pa.csrf })).json().error, 'ORIGIN_DENIED');
    assert.equal((await write({ origin: 'http://evil.example', 'x-csrf-token': pa.csrf })).json().error, 'ORIGIN_DENIED');
    assert.equal((await write({ origin: f.config.origin, 'x-csrf-token': 'stale' })).json().error, 'CSRF_DENIED');
    assert.notEqual(errorCatalog.CSRF_DENIED.text, errorCatalog.ORIGIN_DENIED.text);
    assert.ok(!/נסו שוב/.test(errorCatalog.ORIGIN_DENIED.text), 'retrying from the same address cannot help');
  } finally { await app.close(); f.close(); }
});

test('the error ring is bounded, sanitized and respects the diagnostics opt-out', async () => {
  const f = fixture();
  try {
    const d = new Diagnostics(f.config);
    for (let i = 0; i < ERROR_EVENT_LIMIT + 5; i++) d.recordError('a', { code: 'REQUEST_FAILED', status: 500, route: '/api/x/:id', ref: 'abcdef' + String(i).padStart(4, '0') });
    d.recordError('a', { code: 'free text SECRET' });
    d.recordError('a', { code: 'REQUEST_FAILED', route: '/api/chat?q=SECRET', ref: 'SECRET', error: new Error('SECRET message') });
    const events = (await d.report('a')).recentErrors;
    assert.equal(events.length, ERROR_EVENT_LIMIT, 'bounded; an event without a valid code is dropped');
    assert.equal(events[0].code, 'REQUEST_FAILED');
    assert.equal(events[1].ref, 'abcdef0054');
    assert.equal(events[0].route, null);
    assert.equal(events[0].ref, null);
    assert.equal(events[0].errorClass, 'Error');
    assert.ok(!JSON.stringify(events).includes('SECRET'));
    assert.equal((await d.report('b')).recentErrors.length, 0, 'families are isolated');
    d.recordDisconnect('a', 428); d.recordDisconnect('a', Number.NaN);
    assert.deepEqual((await d.report('a')).whatsappDisconnects.map(e => e.statusCode), [null, 428]);
    await d.clear('a');
    assert.equal((await d.report('a')).recentErrors.length, 0);
    const off = new Diagnostics({ ...f.config, diagnosticsEnabled: false });
    off.recordError('a', { code: 'REQUEST_FAILED' }); off.recordDisconnect('a', 428);
    const offReport = await off.report('a');
    assert.equal(offReport.recentErrors.length + offReport.whatsappDisconnects.length, 0);
    await d.stop(); await off.stop();
  } finally { f.close(); }
});

test('server failure log lines carry only sanitized identifiers', () => {
  let line = '';
  logServerFailure({ code: 'REQUEST_FAILED', route: '/api/groups/:id', ref: 'abc123', errorClass: 'Error SECRET text\nnext' }, value => { line = value; });
  assert.match(line, /^Request failed code=REQUEST_FAILED route=\/api\/groups\/:id ref=abc123 /);
  assert.ok(!line.slice(0, -1).includes('\n'));
  assert.ok(!line.includes(' SECRET'));
});

function hooks(overrides: Partial<ConnectorHooks> = {}) {
  const saved = new Map<string, string>(), states: string[] = [], disconnects: Array<number | undefined> = [], failures: unknown[] = [];
  const h: ConnectorHooks = {
    allowed: () => false, incoming: async () => {}, state: s => { states.push(s); }, update: () => {}, typing: () => {},
    authGet: k => saved.get(k), authSet: (k, v) => { v === null ? saved.delete(k) : saved.set(k, v); },
    authBatch: data => { for (const [k, v] of Object.entries(data)) v === null ? saved.delete(k) : saved.set(k, v); },
    getMessage: () => undefined, maxMedia: 1048576,
    disconnected: code => { disconnects.push(code); }, connectFailed: error => { failures.push(error); },
    ...overrides,
  };
  return { h, states, disconnects, failures };
}
function fakeSockets(failAt: Set<number>) {
  const sockets: any[] = []; let calls = 0;
  const factory = () => {
    if (failAt.has(calls++)) throw new Error('synthetic socket setup failure');
    const listeners = new Map<string, Function[]>();
    const sock: any = { ev: { on: (event: string, fn: Function) => { listeners.set(event, [...(listeners.get(event) || []), fn]); } },
      ws: { on() {} }, emit: (event: string, value: any) => { for (const fn of listeners.get(event) || []) fn(value); },
      end: () => {}, logout: async () => {} };
    sockets.push(sock);
    return sock;
  };
  return { factory, sockets, calls: () => calls };
}
const close = (statusCode: number) => ({ connection: 'close', lastDisconnect: { error: { output: { statusCode } } } });

test('a failed reconnect keeps retrying in "reconnecting" and never asks to rescan the QR', async () => {
  const { h, states, disconnects, failures } = hooks();
  const { factory, sockets, calls } = fakeSockets(new Set([1]));
  const c = new BaileysConnector(h, factory as any);
  try {
    await c.start();
    sockets[0].emit('connection.update', { connection: 'open' });
    sockets[0].emit('connection.update', close(DisconnectReason.restartRequired));
    assert.deepEqual(disconnects, [DisconnectReason.restartRequired]);
    await new Promise(r => setTimeout(r, 300));
    assert.equal(calls(), 2, 'the restart attempt ran and failed');
    assert.equal(failures.length, 1);
    assert.equal(states.at(-1), 'reconnecting');
    await new Promise(r => setTimeout(r, 1800));
    assert.equal(sockets.length, 2, 'the backoff retry opened a new socket');
    assert.ok(!states.includes('needs_pairing'), states.join(','));
  } finally { await c.stop(); }
});

test('a start failure that is not about pairing data retries instead of failing the family', async () => {
  const { h, states, failures } = hooks();
  const { factory, sockets } = fakeSockets(new Set([0]));
  const c = new BaileysConnector(h, factory as any);
  try {
    await c.start();
    assert.equal(failures.length, 1);
    assert.equal(states.at(-1), 'reconnecting');
    await new Promise(r => setTimeout(r, 1800));
    assert.equal(sockets.length, 1);
    assert.ok(!states.includes('needs_pairing'));
  } finally { await c.stop(); }
});

test('only unreadable pairing data asks for a new QR', async () => {
  let unreadable = false;
  const { h, states } = hooks();
  const authGet = h.authGet;
  h.authGet = key => { if (unreadable) throw new PairingDataUnreadable(); return authGet(key); };
  const { factory, sockets } = fakeSockets(new Set());
  const c = new BaileysConnector(h, factory as any);
  try {
    await c.start();
    sockets[0].emit('connection.update', { connection: 'open' });
    unreadable = true;
    sockets[0].emit('connection.update', close(DisconnectReason.connectionLost));
    await new Promise(r => setTimeout(r, 1800));
    assert.equal(states.at(-1), 'needs_pairing');
    assert.equal(sockets.length, 1);
    const fresh = new BaileysConnector(h, factory as any);
    await assert.rejects(fresh.start(), PairingDataUnreadable, 'start reports it to the caller, which shows a new QR');
    await fresh.stop();
  } finally { await c.stop(); }
});
