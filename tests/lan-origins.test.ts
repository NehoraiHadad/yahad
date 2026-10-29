import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, rmSync, readFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { randomBytes } from 'node:crypto';
import { parseAdditionalOrigins } from '../src/allowed-origins.js';
import type { Config } from '../src/config.js';
import { Store } from '../src/store.js';
import { createApp } from '../src/server.js';

const primary = 'https://192.168.4.25';
const remote = 'https://test-device.test-tailnet.ts.net';
const local = 'http://127.0.0.1:47123';

test('LAN payload advertises its origin contract and ships public help without app or script endpoints', () => {
  const marker = JSON.parse(readFileSync(new URL('../deploy/lan-capabilities.json', import.meta.url), 'utf8'));
  assert.deepEqual(marker, { format: 1, localHttps: true, exactAdditionalOrigins: true,
    ownerLoopbackBootstrap: true, publicHelpTemplate: 'deploy/lan-help.html' });
  const help = readFileSync(new URL('../deploy/lan-help.html', import.meta.url), 'utf8');
  assert.match(help, /<html lang="he" dir="rtl">/);
  assert.equal((help.match(/__LAN_HTTPS_ORIGIN__/g) ?? []).length, 2);
  assert.equal((help.match(/__ROOT_SHA256__/g) ?? []).length, 1);
  assert.doesNotMatch(help, /<script\b|<form\b|<input\b|<iframe\b|\/api\/|#setup=/i);
  assert.deepEqual([...help.matchAll(/href="([^"]+)"/g)].map((entry) => entry[1]),
    ['__LAN_HTTPS_ORIGIN__/', '/certificate.crt']);
});

test('additional entry points are bounded exact HTTPS or loopback origins; cookie downgrade is rejected', () => {
  assert.deepEqual(parseAdditionalOrigins('', false), []);
  assert.deepEqual(parseAdditionalOrigins(` ${remote}/,${local},${remote},http://[::1]:47123,http://localhost:47123`, true),
    [remote, local, 'http://[::1]:47123', 'http://localhost:47123']);
  for (const invalid of ['http://192.168.4.25', 'http://device.ts.net', 'http://127.0.0.2:47123',
    'http://127.0.0.1.nip.io:47123', 'https://*.ts.net', `${remote}/path`, `${remote}?x=1`,
    `${remote}#fragment`, `${remote}?`, `${remote}#`, `${remote}/..`, 'http://127.1:47123',
    'https://user:password@device.ts.net', `${remote},`, 'null', 'file:///path'])
    assert.throws(() => parseAdditionalOrigins(invalid, true), /exact HTTPS or loopback origins/);
  assert.throws(() => parseAdditionalOrigins(remote, false), /require secure cookies/);
  assert.throws(() => parseAdditionalOrigins(Array(9).fill(remote).join(','), true), /Too many/);
});

async function fixture(additionalOrigins: string[], fresh = false) {
  const directory = mkdtempSync(join(tmpdir(), 'yahad-lan-origins-'));
  const config: Config = {
    dataDir: join(directory, 'data'), masterKey: randomBytes(32), connector: 'mock', realAllowed: false,
    host: '127.0.0.1', port: 47123, origin: primary, additionalOrigins, secureCookie: true,
    maxMedia: 1024 * 1024, maxStorage: 64 * 1024 * 1024, childIdleMs: 900000, sessionMs: 3600000,
    diagnosticsEnabled: false,
  };
  const store = new Store(config);
  const password = randomBytes(24).toString('base64url');
  if (!fresh) await store.createFamily('Synthetic only', { name: 'test-parent', password });
  const setupToken = randomBytes(32).toString('base64url');
  const { app } = await createApp(config, { store, boot: false,
    ...(fresh ? { bootstrap: { token: setupToken, expiresAt: Date.now() + 60000 } } : {}) });
  return { app, store, password, setupToken, async close() {
    await app.close(); store.close(); rmSync(directory, { recursive: true, force: true });
  } };
}

test('LAN and selected HTTPS entry points retain secure login, authentication, and exact-origin CSRF checks', async () => {
  const f = await fixture([remote, local]);
  try {
    for (const origin of [primary, remote, local]) {
      const response = await f.app.inject({ method: 'POST', url: '/api/login', headers: { origin },
        payload: { name: 'test-parent', password: f.password } });
      assert.equal(response.statusCode, 200);
      assert.match(response.headers['set-cookie'] as string, /; Secure/);
      assert.match(response.headers['set-cookie'] as string, /HttpOnly/);
      assert.match(response.headers['set-cookie'] as string, /SameSite=Strict/);
    }
    const login = await f.app.inject({ method: 'POST', url: '/api/login', headers: { origin: remote },
      payload: { name: 'test-parent', password: f.password } });
    const cookie = (login.headers['set-cookie'] as string).split(';')[0];
    const me = await f.app.inject({ url: '/api/me', headers: { origin: remote, cookie } });
    assert.equal(me.statusCode, 200);
    const csrf = me.json().csrf;
    assert.equal((await f.app.inject({ method: 'POST', url: '/api/logout', headers: { origin: remote, cookie } })).statusCode, 403);
    for (const origin of ['https://different.test-tailnet.ts.net', 'http://test-device.test-tailnet.ts.net',
      'https://test-device.test-tailnet.ts.net.evil.invalid', 'http://127.0.0.1:47124', `${remote}/`])
      assert.equal((await f.app.inject({ method: 'POST', url: '/api/logout',
        headers: { origin, cookie, 'x-csrf-token': csrf } })).statusCode, 403);
    assert.equal((await f.app.inject({ url: '/api/me', headers: { origin: remote } })).statusCode, 401);
    assert.equal((await f.app.inject({ method: 'POST', url: '/api/logout',
      headers: { cookie, 'x-csrf-token': csrf } })).statusCode, 403);
    assert.equal((await f.app.inject({ method: 'POST', url: '/api/logout',
      headers: { origin: remote, cookie, 'x-csrf-token': csrf } })).statusCode, 200);
  } finally { await f.close(); }
});

test('additional origins remain disabled by default', async () => {
  const f = await fixture([]);
  try {
    const response = await f.app.inject({ method: 'POST', url: '/api/login', headers: { origin: remote },
      payload: { name: 'test-parent', password: f.password } });
    assert.equal(response.statusCode, 403);
    assert.equal(response.json().error, 'ORIGIN_DENIED');
  } finally { await f.close(); }
});

test('explicit owner loopback bootstrap still requires a valid one-time capability and secure cookies', async () => {
  const f = await fixture([local], true);
  try {
    const payload = { familyName: 'Synthetic only', name: 'test-parent', password: f.password, confirmation: f.password };
    const missing = await f.app.inject({ method: 'POST', url: '/api/setup', headers: { origin: local }, payload });
    assert.equal(missing.statusCode, 403);
    assert.equal(missing.json().error, 'SETUP_CLOSED');
    const denied = await f.app.inject({ method: 'POST', url: '/api/setup',
      headers: { origin: 'http://192.168.4.25', 'x-setup-token': f.setupToken }, payload });
    assert.equal(denied.statusCode, 403);
    assert.equal(denied.json().error, 'ORIGIN_DENIED');
    const setup = await f.app.inject({ method: 'POST', url: '/api/setup',
      headers: { origin: local, 'x-setup-token': f.setupToken }, payload });
    assert.equal(setup.statusCode, 200);
    assert.match(setup.headers['set-cookie'] as string, /; Secure/);
    const repeated = await f.app.inject({ method: 'POST', url: '/api/setup',
      headers: { origin: local, 'x-setup-token': f.setupToken }, payload });
    assert.equal(repeated.statusCode, 403);
    assert.equal(repeated.json().error, 'SETUP_CLOSED');
  } finally { await f.close(); }
});
