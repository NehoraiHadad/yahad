import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { randomBytes } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { PassThrough, Writable } from 'node:stream';
import { createPrompter } from '../scripts/prompt.js';
import type { Config } from '../src/config.js';
import { Store } from '../src/store.js';
import { createApp } from '../src/server.js';
import { verifyPassword } from '../src/crypto.js';

const password = 'random meadow cloud lantern';
async function fixture(expired = false, enabled = true) {
  const dir = mkdtempSync(join(tmpdir(), 'yachad-onboarding-'));
  const config: Config = { dataDir: dir, masterKey: randomBytes(32), connector: 'mock', realAllowed: false,
    host: '127.0.0.1', port: 3000, origin: 'http://localhost:3000', secureCookie: false,
    maxMedia: 1024 ** 2, maxStorage: 64 * 1024 ** 2, childIdleMs: 900000, sessionMs: 3600000,
    diagnosticsEnabled: false };
  const store = new Store(config), secret = randomBytes(32).toString('base64url');
  const { app } = await createApp(config, { store, boot: false,
    ...(enabled ? { bootstrap: { token: secret, expiresAt: Date.now() + (expired ? -1 : 60000) } } : {}) });
  const setupBody = { name: 'Family', username: 'parent', password, confirmation: password };
  const setup = (body: any = setupBody, extra: any = {}) => app.inject({ method: 'POST', url: '/api/setup',
    headers: { origin: config.origin, 'x-setup-token': secret, ...extra }, payload: body });
  const req = (session: { sid: string; csrf: string }, url: string, body: any, extra: any = {}) => app.inject({
    method: 'POST', url, payload: body, headers: { origin: config.origin, cookie: 'sid=' + session.sid,
      'x-csrf-token': session.csrf, ...extra } });
  return { store, app, setup, req, setupBody,
    parent: () => store.createSession(store.user(store.one("SELECT * FROM users WHERE role='parent'"))),
    async close() { await app.close(); store.close(); rmSync(dir, { recursive: true, force: true }); } };
}

test('first-run requires local capability, same origin, confirmation, and single empty-store claim', async () => {
  const f = await fixture();
  try {
    assert.equal((await f.setup(undefined, { 'x-setup-token': 'wrong' })).statusCode, 403);
    assert.equal((await f.setup(undefined, { origin: 'https://other.example' })).statusCode, 403);
    assert.equal((await f.setup({ ...f.setupBody, confirmation: 'different' })).statusCode, 400);
    const results = await Promise.all([f.setup(), f.setup()]);
    assert.deepEqual(results.map(r => r.statusCode).sort(), [200, 409]);
    assert.equal(f.store.one('SELECT count(*) AS n FROM families').n, 1);
    assert.equal(f.store.one("SELECT count(*) AS n FROM users WHERE role='child'").n, 0);
    assert.equal((await f.setup()).statusCode, 403);
    const winner = results.find(r => r.statusCode === 200)!;
    const cookie = winner.cookies.find(c => c.name === 'sid');
    assert.ok(cookie?.value);
    assert.equal((await f.app.inject({ url: '/api/me', headers: { cookie: 'sid=' + cookie.value } })).statusCode, 200);
    assert.equal((await f.app.inject({ url: '/api/setup/status' })).json().state, 'complete');
    assert.ok(await verifyPassword(password, f.store.one("SELECT password FROM users WHERE username='parent'").password));
  } finally { await f.close(); }
});

test('normal startup and expired bootstrap cannot claim an empty database', async () => {
  for (const args of [[true, true], [false, false]] as const) {
    const f = await fixture(...args);
    try { assert.equal((await f.setup()).statusCode, 403); assert.equal(f.store.one('SELECT count(*) AS n FROM families').n, 0);
      assert.equal((await f.app.inject({ url: '/api/setup/status' })).json().state, 'closed'); }
    finally { await f.close(); }
  }
});

test('child creation is parent-only, CSRF protected, unique, and grants no groups', async () => {
  const f = await fixture();
  try {
    await f.setup(); const parent = f.parent();
    const body = { username: 'child', display: 'Child', password: password + ' child', confirmation: password + ' child' };
    assert.equal((await f.req(parent, '/api/parent/children', body, { 'x-csrf-token': 'wrong' })).statusCode, 403);
    assert.equal((await f.req(parent, '/api/parent/children', { ...body, confirmation: '' })).statusCode, 400);
    assert.equal((await f.req(parent, '/api/parent/children', body)).statusCode, 200);
    assert.equal((await f.req(parent, '/api/parent/children', body)).statusCode, 409);
    assert.equal(f.store.one('SELECT count(*) AS n FROM grants').n, 0);
    assert.ok(f.store.session(parent.sid), 'creating a child preserves the parent session');
    assert.equal((await f.app.inject({ url: '/api/me', headers: { cookie: 'sid=' + parent.sid } })).statusCode, 200);
    const child = f.store.createSession(f.store.user(f.store.one("SELECT * FROM users WHERE username='child'")));
    assert.equal((await f.req(child, '/api/parent/children', { ...body, username: 'sibling' })).statusCode, 403);
  } finally { await f.close(); }
});

test('own and child password confirmation reject mismatch and reset revokes sessions', async () => {
  const f = await fixture();
  try {
    await f.setup(); const parent = f.parent(), next = 'another random constellation phrase';
    await f.req(parent, '/api/parent/children', { username: 'child', display: 'Child', password, confirmation: password });
    const row = f.store.one("SELECT * FROM users WHERE username='child'"), child = f.store.createSession(f.store.user(row));
    assert.equal((await f.req(parent, '/api/parent/child-password', { childId: row.id, password: next, confirmation: '' })).statusCode, 400);
    assert.ok(f.store.session(child.sid));
    assert.equal((await f.req(parent, '/api/password', { current: password, next, confirmation: '' })).statusCode, 400);
    assert.equal((await f.req(parent, '/api/parent/child-password', { childId: row.id, password: next, confirmation: next })).statusCode, 200);
    assert.equal(f.store.session(child.sid), undefined);
    assert.equal((await f.req(parent, '/api/password', { current: password, next, confirmation: next })).statusCode, 200);
    assert.equal(f.store.session(parent.sid), undefined);
  } finally { await f.close(); }
});

test('prompt preserves buffered Unicode lines and never echoes supplied secrets', () => {
  const result = spawnSync(process.execPath, ['--import', 'tsx', '--input-type=module', '-e',
    "const {ask,closePrompt}=await import('./scripts/prompt.ts'); try { const a=await ask('Name: '); const b=await ask('Secret: ',true); const c=await ask('Confirm: ',true); if(a!=='family'||b!=='סוד ארוך עם רווחים'||c!==b) process.exitCode=1; } finally {closePrompt();}"],
    { input: 'family\nסוד ארוך עם רווחים\nסוד ארוך עם רווחים\n', encoding: 'utf8', timeout: 15000 });
  assert.equal(result.status, 0, result.stderr);
  assert.ok(!result.stdout.includes('סוד'));
});

test('terminal multiline paste never echoes later secret lines before their prompt', async () => {
  const input = new PassThrough();
  let visible = '';
  const output = new Writable({ write(chunk, _encoding, done) { visible += chunk.toString(); done(); } });
  const prompt = createPrompter(input, output, true);
  try {
    const first = prompt.ask('Name: ');
    input.write('family\rsecret pasted words\rsecret pasted words\r');
    assert.equal(await first, 'family');
    assert.ok(!visible.includes('secret pasted words'));
    assert.equal(await prompt.ask('Secret: ', true), 'secret pasted words');
    assert.equal(await prompt.ask('Confirm: ', true), 'secret pasted words');
    assert.ok(!visible.includes('secret pasted words'));
  } finally { prompt.close(); input.destroy(); }
});
