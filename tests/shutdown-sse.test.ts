import { test } from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import { mkdtempSync, rmSync, statSync, writeFileSync, mkdirSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { randomBytes } from 'node:crypto';
import { privateFixtureDirectory } from './fixtures/private-directory.js';
import { createApp } from '../src/server.js';
import { acquireDataLock } from '../src/lock.js';
import { createBackup } from '../scripts/backup-lib.js';
import type { Config } from '../src/config.js';

async function bounded<T>(operation: Promise<T>, milliseconds = 2000): Promise<T> {
  let timer: ReturnType<typeof setTimeout> | undefined;
  try {
    return await Promise.race([operation, new Promise<never>((_, reject) => {
      timer = setTimeout(() => reject(new Error('Graceful shutdown did not complete')), milliseconds);
    })]);
  } finally { clearTimeout(timer); }
}

async function fixture(preClose?: () => Promise<void>, captureResponse?: (response: http.ServerResponse) => void) {
  const root = privateFixtureDirectory('yachad-shutdown-test-');
  const dataDir = join(root, 'data'), key = join(root, 'key');
  const masterKey = randomBytes(32);
  writeFileSync(key, masterKey.toString('base64'), { mode: 0o600 });
  const config: Config = { dataDir, masterKey, connector: 'mock', realAllowed: false,
    host: '127.0.0.1', port: 3000, origin: 'http://localhost:3000', secureCookie: false,
    maxMedia: 1048576, maxStorage: 64 * 1048576, childIdleMs: 60000, sessionMs: 3600000,
    diagnosticsEnabled: false };
  mkdirSync(dataDir,{recursive:true,mode:0o700});
  const releaseLock = await acquireDataLock(dataDir);
  let released: Promise<void> | undefined;
  const release = () => released ||= releaseLock();
  const { app, store, service } = await createApp(config, { boot: false });
  const family = await store.createFamily('Synthetic shutdown fixture', {
    name: 'shutdown-parent', password: randomBytes(24).toString('hex'),
  });
  const session = store.createSession(store.user(store.one(
    'SELECT * FROM users WHERE family_id=? AND id=?', family.familyId, family.parentId,
  )));
  if (preClose) app.addHook('preClose', preClose);
  if (captureResponse) app.addHook('onRequest', async (request, reply) => {
    if (request.url === '/api/events') captureResponse(reply.raw);
  });
  await app.listen({ port: 0, host: '127.0.0.1' });
  const url = app.listeningOrigin + '/api/events', cookie = 'sid=' + session.sid;
  return { root, dataDir, key, app, service, family, url, cookie, release,
    async cleanup() {
      // Also releases sockets on a regression failure, so the test cannot hang.
      app.server.closeAllConnections();
      await app.close();
      await release();
      rmSync(root, { recursive: true, force: true });
    } };
}

function response(url: string, cookie?: string): Promise<http.IncomingMessage> {
  return new Promise((resolve, reject) => {
    const request = http.get(url, { headers: cookie ? { cookie } : {} }, resolve);
    request.on('error', reject);
  });
}

async function stream(url: string, cookie: string) {
  const incoming = await response(url, cookie);
  assert.equal(incoming.statusCode, 200);
  incoming.on('error', () => {}); // A forced SSE close is intentional at shutdown.
  const first = await bounded(new Promise<string>(resolve => incoming.once('data', data => resolve(String(data)))));
  assert.match(first, /event: ready/);
  return incoming;
}

test('open authenticated SSE drains before HTTP shutdown and immediately permits a backup', { timeout: 10000, skip: process.platform==='win32' ? 'Requires the Linux backup adapter; native Windows backup acceptance remains blocked' : false }, async () => {
  const f = await fixture();
  try {
    const denied = await response(f.url);
    assert.equal(denied.statusCode, 401);
    denied.resume();
    const events = await stream(f.url, f.cookie);
    const normalEvent = new Promise<string>(resolve => events.once('data', data => resolve(String(data))));
    f.service.notify(f.family.familyId, undefined);
    assert.match(await bounded(normalEvent), /event: refresh/);
    assert.equal(f.service.listeners.size, 1);
    const disconnected = new Promise<void>(resolve => events.once('close', resolve));
    // main.ts releases the data lock only after app.close() resolves.
    await bounded(f.app.close().then(f.release));
    await bounded(disconnected);
    assert.equal(f.service.listeners.size, 0);
    const backup = await createBackup(f.dataDir, f.key, join(f.root, 'backups'), randomBytes(24).toString('hex'));
    assert.ok(statSync(backup).size > 0);
  } finally { await f.cleanup(); }
});

test('shutdown rejects SSE reconnects while preClose is running', { timeout: 10000 }, async () => {
  let allowClose!: () => void;
  let entered!: () => void;
  const held = new Promise<void>(resolve => { allowClose = resolve; });
  const started = new Promise<void>(resolve => { entered = resolve; });
  const f = await fixture(async () => { entered(); await held; });
  try {
    await stream(f.url, f.cookie);
    const closing = f.app.close();
    await bounded(started);
    assert.equal(f.service.listeners.size, 0);
    const reconnect = await response(f.url, f.cookie);
    assert.equal(reconnect.statusCode, 503);
    reconnect.resume();
    assert.equal(f.service.listeners.size, 0);
    allowClose();
    await bounded(closing);
  } finally { allowClose(); await f.cleanup(); }
});

test('already-ended backpressured SSE transport remains reachable during shutdown', { timeout: 10000 }, async () => {
  let outgoing!: http.ServerResponse;
  const f = await fixture(async () => {
    // Check before Node's HTTP-server close/idle-connection fallback runs:
    // preClose itself must retain and destroy the logically ended transport.
    assert.equal(outgoing.destroyed, true, 'SSE transport escaped preClose cleanup');
  }, response => { outgoing = response; });
  let restoreTransport: (() => void) | undefined;
  try {
    const events = await stream(f.url, f.cookie);
    events.pause();
    const socket = outgoing.socket;
    assert.ok(socket);
    const uncork = socket.uncork.bind(socket);
    // Deterministically retain pending writes, modelling a peer that cannot
    // drain them without relying on platform-specific TCP buffer capacities.
    socket.cork();
    socket.uncork = () => {};
    restoreTransport = () => { socket.uncork = uncork; socket.destroy(); };
    outgoing.write('x'.repeat(65537));
    assert.ok(outgoing.writableLength > 65536);
    f.service.notify(f.family.familyId, undefined);
    assert.equal(outgoing.writableEnded, true);
    assert.equal(outgoing.writableFinished, false);
    assert.equal(f.service.listeners.size, 0);
    // end(false) already ran due to backpressure. preClose must still find and
    // destroy this transport instead of waiting forever for its buffered flush.
    await bounded(f.app.close().then(f.release));
    assert.equal(outgoing.destroyed, true);
  } finally { restoreTransport?.(); await f.cleanup(); }
});
