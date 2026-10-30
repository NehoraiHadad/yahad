import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve, sep, basename } from 'node:path';
import { randomBytes } from 'node:crypto';
import type { Config } from '../src/config.js';
import { Store } from '../src/store.js';
import { createApp } from '../src/server.js';
import { MockConnector } from '../src/connectors/mock.js';

test('production connector waits for existing authenticated parent connect action, then preserves reconnect intent', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'yahad-installer-consent-'));
  const config: Config = { dataDir: join(directory, 'data'), masterKey: randomBytes(32), connector: 'baileys', realAllowed: true,
    host: '127.0.0.1', port: 47123, origin: 'http://127.0.0.1:47123', secureCookie: false,
    maxMedia: 1048576, maxStorage: 67108864, childIdleMs: 900000, sessionMs: 3600000 };
  let starts = 0, factories = 0;
  class NoNetworkConnector extends MockConnector {
    override async start() { starts++; this.hooks.state('qr', 'synthetic-qr-not-a-real-account'); }
    override async groups() { return []; }
  }
  const store = new Store(config);
  let originalClosed = false; let nextStore: Store | undefined;
  const token = randomBytes(32).toString('base64url');
  const factory = (hooks: ConstructorParameters<typeof MockConnector>[0]) => { factories++; return new NoNetworkConnector(hooks); };
  let app = (await createApp(config, { store, factory, bootstrap: { token, expiresAt: Date.now() + 60000 } })).app;
  try {
    assert.equal(starts, 0); assert.equal(factories, 0);
    assert.equal((await app.inject({ method: 'GET', url: '/api/setup/status' })).json().state, 'ready');
    const setup = await app.inject({ method: 'POST', url: '/api/setup', headers: { origin: config.origin, 'x-setup-token': token },
      payload: { familyName: 'Synthetic installer family', name: 'installer-parent', password: 'fixture-passphrase-only', confirmation: 'fixture-passphrase-only' } });
    assert.equal(setup.statusCode, 200); assert.equal(starts, 0); assert.equal(factories, 0);
    const cookieHeader = setup.headers['set-cookie'];
    const cookie = (Array.isArray(cookieHeader) ? cookieHeader[0] : cookieHeader)!.split(';')[0];
    const session = store.session(cookie.slice(4))!; assert.ok(session);
    assert.equal(store.one('SELECT linked FROM families WHERE id=?', session.user.familyId).linked, 0);
    assert.equal((await app.inject({ method: 'POST', url: '/api/parent/connect', headers: { origin: config.origin } })).statusCode, 401);
    assert.equal((await app.inject({ method: 'POST', url: '/api/parent/connect', headers: { origin: config.origin, cookie } })).statusCode, 403);
    assert.equal((await app.inject({ method: 'POST', url: '/api/parent/connect', headers: { origin: 'https://wrong.invalid', cookie, 'x-csrf-token': session.csrf } })).statusCode, 403);
    assert.equal(starts, 0); assert.equal(factories, 0);
    const connect = await app.inject({ method: 'POST', url: '/api/parent/connect', headers: { origin: config.origin, cookie, 'x-csrf-token': session.csrf } });
    assert.equal(connect.statusCode, 200); assert.equal(starts, 1); assert.equal(factories, 1);
    const qr = await app.inject({ method: 'GET', url: '/api/parent/qr', headers: { cookie } });
    assert.equal(qr.json().state, 'qr'); assert.match(qr.json().qr, /^data:image\/png;base64,/);
    assert.equal(store.one('SELECT value FROM instance_metadata WHERE key=?', 'connector').value, 'baileys');
    assert.equal(store.one('SELECT linked FROM families WHERE id=?', session.user.familyId).linked, 1);
    await app.close(); store.close(); originalClosed = true;
    nextStore = new Store(config);
    app = (await createApp(config, { store: nextStore, factory })).app;
    assert.equal(starts, 2, 'a previously requested connection reconnects after restart');
  } finally {
    await app.close(); nextStore?.close(); if (!originalClosed) store.close();
    assert.ok(resolve(directory).startsWith(resolve(tmpdir()) + sep) && basename(directory).startsWith('yahad-installer-consent-'));
    rmSync(directory, { recursive: true, force: true });
  }
});
