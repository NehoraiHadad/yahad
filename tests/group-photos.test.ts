import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, rmSync, readFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { randomBytes } from 'node:crypto';
import { GroupPhotoCache, fetchGroupPhoto, photoUrl, MAX_GROUP_PHOTO_BYTES, type GroupPhoto } from '../src/group-photos.js';
import { Store } from '../src/store.js';
import { createApp } from '../src/server.js';
import { MockConnector } from '../src/connectors/mock.js';
import { BaileysConnector } from '../src/connectors/baileys.js';
import type { Config } from '../src/config.js';
import vm from 'node:vm';
import { createRequire } from 'node:module';
import ts from 'typescript';
const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aV0cAAAAASUVORK5CYII=', 'base64');
const photo: GroupPhoto = { bytes: png, mime: 'image/png' };
const g = '120363001@g.us', privateGroup = '120363999@g.us';
const tick = () => new Promise<void>(resolve => setImmediate(resolve));

test('CDN fetch permits only HTTPS allowlisted hosts, disables redirects and sniffs raster bytes', async () => {
  for (const value of ['http://pps.whatsapp.net/a', 'https://localhost/a', 'https://127.0.0.1/a',
    'https://pps.whatsapp.net.evil.example/a', 'https://user:pass@pps.whatsapp.net/a',
    'https://pps.whatsapp.net:8443/a', 'https://evil.whatsapp.net/a', 'file:///etc/passwd']) {
    assert.equal(photoUrl(value), undefined);
    assert.equal(await fetchGroupPhoto(value, async () => { throw Error('must not fetch'); }), undefined);
  }
  const fetched = await fetchGroupPhoto('https://pps.whatsapp.net/synthetic?token=not-real', async (url, options) => {
    assert.equal(new URL(String(url)).hostname, 'pps.whatsapp.net');
    assert.equal(options?.redirect, 'error'); assert.equal(options?.credentials, 'omit');
    assert.ok(options?.signal);
    return new Response(png, { headers: { 'content-type': 'text/html' } });
  });
  assert.deepEqual(fetched, photo);
  for (const response of [new Response('<svg/>', { headers: { 'content-type': 'image/png' } }),
    new Response(png, { status: 302, headers: { location: 'http://localhost' } }),
    new Response(png, { headers: { 'content-length': String(MAX_GROUP_PHOTO_BYTES + 1) } }),
    new Response(Buffer.alloc(MAX_GROUP_PHOTO_BYTES + 1))]) {
    assert.equal(await fetchGroupPhoto('https://pps.whatsapp.net/synthetic', async () => response), undefined);
  }
  assert.equal(await fetchGroupPhoto('https://pps.whatsapp.net/a', async () => { throw Error('private'); }), undefined);
});

test('cache coalesces, expires positive and negative entries, isolates owners and clears pending work', async () => {
  let now = 0, calls = 0;
  const cache = new GroupPhotoCache(() => now), a = {}, b = {};
  const load = async () => { calls++; return photo; };
  assert.deepEqual(await Promise.all([cache.get(a, 'g', load), cache.get(a, 'g', load)]), [photo, photo]);
  assert.equal(calls, 1); await cache.get(a, 'g', load); assert.equal(calls, 1);
  now += 15 * 60_000; await cache.get(a, 'g', load); assert.equal(calls, 2);
  await cache.get(b, 'g', load); assert.equal(calls, 3);
  let missing = 0; const absent = async () => { missing++; return undefined; };
  await cache.get(a, 'absent', absent); await cache.get(a, 'absent', absent); assert.equal(missing, 1);
  now += 60_000; await cache.get(a, 'absent', absent); assert.equal(missing, 2);
  let release!: (p: GroupPhoto) => void;
  const pending = cache.get(a, 'slow', () => new Promise(r => release = r));
  cache.clear(a); release(photo); assert.equal(await pending, undefined);
});

test('cache bounds concurrent downloads and retained entries', async () => {
  const cache = new GroupPhotoCache(); const owner = {}; let active = 0, peak = 0;
  const load = async () => { active++; peak = Math.max(active, peak); await tick(); active--; return photo; };
  await Promise.all(Array.from({ length: 80 }, (_, i) => cache.get(owner, String(i), load)));
  assert.equal(peak, 4);
  for (let i = 0; i < 200; i++) await cache.get(owner, String(i), load);
  assert.ok((cache as any).entries.size <= 128);
});

test('Baileys requests group previews with a provider timeout and hides missing/private pictures', async () => {
  const calls: unknown[][] = [];
  const socket = { profilePictureUrl: async (...args: unknown[]) => { calls.push(args); return undefined; } };
  const connector = Object.assign(Object.create(BaileysConnector.prototype), { sock: socket });
  assert.equal(await connector.groupPhoto(g), undefined);
  assert.deepEqual(calls, [[g, 'preview', 5000]]);
  await connector.groupPhoto('123@s.whatsapp.net'); assert.equal(calls.length, 2);
  await connector.groupPhoto('status@broadcast'); assert.equal(calls.length, 2);
  socket.profilePictureUrl = async () => { throw Error('not authorized'); };
  assert.equal(await connector.groupPhoto(g), undefined);
});

class PhotoMock extends MockConnector {
  calls = 0;
  result: () => Promise<GroupPhoto | undefined> = async () => photo;
  override async approved() {}
  async groupPhoto() { this.calls++; return this.result(); }
}
async function fixture() {
  const dir = mkdtempSync(join(tmpdir(), 'yachad-photos-'));
  const config: Config = { dataDir: dir, masterKey: randomBytes(32), connector: 'mock', realAllowed: false,
    host: '127.0.0.1', port: 3000, origin: 'http://localhost:3000', secureCookie: false,
    maxMedia: 1048576, maxStorage: 67108864, childIdleMs: 900000, sessionMs: 3600000, diagnosticsEnabled: false };
  const store = new Store(config), password = randomBytes(24).toString('hex');
  const a = await store.createFamily('A', { name: 'parent-a', password }, { name: 'child-a', password });
  const b = await store.createFamily('B', { name: 'parent-b', password }, { name: 'child-b', password });
  const { app, service } = await createApp(config, { store, factory: h => new PhotoMock(h) });
  await service.connect(a.familyId); await service.connect(b.familyId);
  const session = (id: string) => store.createSession(store.user(store.one('SELECT * FROM users WHERE id=?', id)));
  const parent = session(a.parentId), child = session(a.childId), other = session(b.childId);
  const req = (s: typeof parent | undefined, group = g) => app.inject({ url: '/api/groups/' + encodeURIComponent(group) + '/photo', headers: s ? { cookie: 'sid=' + s.sid } : {} });
  const connector = service.connectors.get(a.familyId) as PhotoMock;
  return { store, service, a, b, parent, child, other, req, connector,
    async close() { await app.close(); store.close(); rmSync(dir, { recursive: true, force: true }); } };
}

test('photo route enforces session/family/grant boundaries, parent metadata access and no-store', async () => {
  const f = await fixture();
  try {
    assert.equal((await f.req(undefined)).statusCode, 401);
    assert.equal((await f.req(f.child)).statusCode, 404);
    assert.equal((await f.req(f.other)).statusCode, 404);
    assert.equal((await f.req(f.parent, 'unknown@g.us')).statusCode, 404);
    assert.equal(f.connector.calls, 0);
    const parentPhoto = await f.req(f.parent, privateGroup);
    assert.equal(parentPhoto.statusCode, 200); assert.deepEqual(parentPhoto.rawPayload, png);
    assert.equal(parentPhoto.headers['content-type'], 'image/png');
    assert.match(String(parentPhoto.headers['cache-control']), /no-store/);
    assert.equal(parentPhoto.headers['x-content-type-options'], 'nosniff');
    await f.service.grant(f.a.familyId, f.a.childId, g, true);
    assert.equal((await f.req(f.child)).statusCode, 200);
    assert.equal((await f.req(f.other)).statusCode, 404);
    // Same group identifier in another family must never reuse family A's bytes.
    await f.service.grant(f.b.familyId, f.b.childId, g, true);
    (f.service.connectors.get(f.b.familyId) as PhotoMock).result = async () => undefined;
    assert.equal((await f.req(f.other)).statusCode, 404);
    await f.service.grant(f.a.familyId, f.a.childId, g, false);
    assert.equal((await f.req(f.child)).statusCode, 404);
  } finally { await f.close(); }
});

test('photo route rechecks revocation and logout after provider work and ignores late disconnect results', async () => {
  const f = await fixture();
  try {
    await f.service.grant(f.a.familyId, f.a.childId, g, true);
    let release!: (value: GroupPhoto) => void;
    f.connector.result = () => new Promise(resolve => release = resolve);
    const pending = f.req(f.child); void pending.then(() => {});
    while (!release) await tick();
    await f.service.grant(f.a.familyId, f.a.childId, g, false);
    release(photo); assert.equal((await pending).statusCode, 404);
    let releaseParent!: (value: GroupPhoto) => void;
    f.connector.result = () => new Promise(resolve => releaseParent = resolve);
    const parentPending = f.req(f.parent); void parentPending.then(() => {});
    while (!releaseParent) await tick();
    f.store.run('DELETE FROM sessions');
    releaseParent(photo); assert.equal((await parentPending).statusCode, 401);
    const user = f.store.user(f.store.one('SELECT * FROM users WHERE id=?', f.a.parentId));
    f.connector.result = () => new Promise(resolve => releaseParent = resolve);
    // Invalidate cached photo before beginning the disconnect race.
    await f.service.grant(f.a.familyId, f.a.childId, g, true);
    const late = f.service.groupPhoto(user, g); await tick();
    await f.service.disconnect(f.a.familyId, false);
    releaseParent(photo); assert.equal(await late, undefined);
  } finally { await f.close(); }
});

test('avatar uses only encoded same-origin paths and falls back on image errors; new group retries', () => {
  const slots: any[] = []; let cursor = 0;
  const module = { exports: {} as any }; const require = createRequire(import.meta.url);
  const code = ts.transpileModule(readFileSync(new URL('../web/GroupAvatar.tsx', import.meta.url), 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, jsx: ts.JsxEmit.ReactJSX, target: ts.ScriptTarget.ES2022 },
  }).outputText;
  vm.runInNewContext(code, { module, exports: module.exports,
    require: (name: string) => name.endsWith('.css') ? {} : name === 'react' ? { useState(initial: any) { const i = cursor++; if (!(i in slots)) slots[i] = initial; return [slots[i], (v: any) => slots[i] = v]; } } : require(name) });
  let previousKey: string | undefined;
  const render = (groupId = g, retryKey?: string | number) => {
    cursor = 0;
    const child = module.exports.GroupAvatar({ groupId, title: 'משפחה', retryKey });
    // React resets a keyed child's hooks whenever its key changes.
    if (child.key !== previousKey) { slots.length = 0; previousKey = child.key; }
    return child.type(child.props);
  };
  const first = render(); assert.equal(first.props['aria-hidden'], 'true');
  const img = first.props.children[1]; assert.equal(img.props.src, '/api/groups/120363001%40g.us/photo');
  assert.equal(img.props.alt, ''); assert.equal(img.props.loading, 'lazy');
  img.props.onError(); const fallback = render();
  assert.equal(fallback.props.children[1], false); assert.equal(fallback.props.children[0].props.children, 'מ');
  assert.equal(render().props.children[1], false); // repeated render must not retry
  const reconnected = render(g, 'connected');
  assert.equal(reconnected.props.children[1].props.src, '/api/groups/120363001%40g.us/photo?v=connected');
  reconnected.props.children[1].props.onError();
  assert.equal(render(g, 'connected').props.children[1], false);
  render(g, 'idle').props.children[1].props.onError();
  assert.ok(render(g, 'connected').props.children[1]); // A → B → A resets
  render(g, 1).props.children[1].props.onError();
  assert.equal(render(g, 1).props.children[1], false);
  assert.ok(render(g, 2).props.children[1]); // explicit refresh revision
  assert.ok(render(privateGroup).props.children[1]);
  assert.equal(render('https://evil.example/a').props.children[1].props.src, '/api/groups/https%3A%2F%2Fevil.example%2Fa/photo');
});
