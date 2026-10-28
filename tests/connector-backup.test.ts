import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  mkdtempSync,
  writeFileSync,
  readFileSync,
  existsSync,
  rmSync,
  readdirSync,
  statSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { randomBytes } from 'node:crypto';
import { normalizeIncoming, encryptedAuth, BaileysConnector } from '../src/connectors/baileys.js';
import type { ConnectorHooks } from '../src/connectors/types.js';
import { proto, DisconnectReason } from '@whiskeysockets/baileys';
import { createBackup, restoreBackup } from '../scripts/backup-lib.js';
import { Store } from '../src/store.js';
import type { Config } from '../src/config.js';
import { acquireDataLock } from '../src/lock.js';
function hooks() {
  const saved = new Map<string, string>(),
    events: string[] = [];
  const h: ConnectorHooks = {
    allowed: (g) => g === '1@g.us',
    incoming: async () => {},
    state: (s) => events.push(s),
    update: () => {},
    typing: () => {},
    authGet: (k) => saved.get(k),
    authSet: (k, v) => {
      v === null ? saved.delete(k) : saved.set(k, v);
    },
    authBatch: (data) => {
      for (const [k, v] of Object.entries(data)) v === null ? saved.delete(k) : saved.set(k, v);
    },
    getMessage: () => undefined,
    maxMedia: 1048576,
  };
  return { h, saved, events };
}
test('real adapter normalizes supported types and strips untrusted embedded previews/quotes from raw', () => {
  const raw: any = {
    key: { id: '1', remoteJid: '1@g.us', fromMe: false, participant: 'lid@lid' },
    pushName: 'Sender',
    messageTimestamp: 1700000000,
    message: {
      extendedTextMessage: {
        text: 'visible',
        contextInfo: {
          stanzaId: 'outside',
          remoteJid: 'hidden@g.us',
          quotedMessage: { conversation: 'FORBIDDEN_QUOTED_SECRET' },
          externalAdReply: { body: 'HIDDEN_PREVIEW' },
        },
      },
    },
  };
  const m = normalizeIncoming(raw)!;
  assert.equal(m.text, 'visible');
  assert.equal(m.replyId, undefined);
  assert.ok(!m.raw!.includes('FORBIDDEN_QUOTED_SECRET'));
  assert.ok(!m.raw!.includes('HIDDEN_PREVIEW'));
  assert.equal(
    normalizeIncoming({ ...raw, key: { ...raw.key, remoteJid: 'private@s.whatsapp.net' } }),
    undefined,
  );
  for (const [field, kind] of [
    ['imageMessage', 'image'],
    ['videoMessage', 'video'],
    ['audioMessage', 'audio'],
    ['documentMessage', 'document'],
    ['stickerMessage', 'sticker'],
  ])
    assert.equal(
      normalizeIncoming({ ...raw, message: { [field]: { caption: 'text' } } })?.kind,
      kind,
    );
  assert.equal(
    normalizeIncoming({
      ...raw,
      message: { viewOnceMessage: { message: { imageMessage: { caption: 'secret' } } } },
    })?.raw,
    undefined,
  );
  const edit = normalizeIncoming({
    ...raw,
    message: {
      protocolMessage: {
        type: proto.Message.ProtocolMessage.Type.MESSAGE_EDIT,
        key: { id: 'target', remoteJid: '1@g.us' },
        editedMessage: { conversation: 'changed' },
      },
    },
  })!;
  assert.equal(edit.id, 'target');
  assert.equal(edit.edited, true);
  assert.equal(edit.text, 'changed');
});

test('encrypted auth store restores Buffer credentials, Signal keys, null deletes and app-state proto', async () => {
  const { h, saved } = hooks();
  const auth = encryptedAuth(h);
  auth.save();
  const restored = encryptedAuth(h);
  assert.deepEqual(restored.state.creds.noiseKey, auth.state.creds.noiseKey);
  assert.ok(Buffer.isBuffer(restored.state.creds.noiseKey.private));
  await auth.state.keys.set({ session: { test: Buffer.from([1, 2, 3]) } } as any);
  const read = await restored.state.keys.get('session', ['test']);
  assert.deepEqual(read.test, Buffer.from([1, 2, 3]));
  await auth.state.keys.set({ session: { test: null } } as any);
  assert.deepEqual(await restored.state.keys.get('session', ['test']), {});
  const value = proto.Message.AppStateSyncKeyData.fromObject({ keyData: Buffer.from([4, 5]) });
  await auth.state.keys.set({ 'app-state-sync-key': { k: value } });
  const v = await restored.state.keys.get('app-state-sync-key', ['k']);
  assert.ok(v.k instanceof proto.Message.AppStateSyncKeyData);
});

test('real adapter lifecycle with injected socket: QR, restart reconnect, logout, no network', async () => {
  const { h, events } = hooks();
  const sockets: any[] = [];
  const factory = () => {
    const listeners = new Map<string, Function[]>();
    const sock: any = {
      ev: {
        on: (event: string, fn: Function) => {
          listeners.set(event, [...(listeners.get(event) || []), fn]);
        },
      },
      emit: (event: string, value: any) => {
        for (const fn of listeners.get(event) || []) fn(value);
      },
      end: () => {},
      logout: async () => {},
    };
    sockets.push(sock);
    return sock;
  };
  const c = new BaileysConnector(h, factory as any);
  try {
    await c.start();
    sockets[0].emit('connection.update', { qr: 'LOCAL_TEST_QR' });
    assert.equal(events.at(-1), 'qr');
    sockets[0].emit('connection.update', { connection: 'open' });
    assert.equal(events.at(-1), 'connected');
    sockets[0].emit('connection.update', {
      connection: 'close',
      lastDisconnect: { error: { output: { statusCode: DisconnectReason.restartRequired } } },
    });
    await new Promise((r) => setTimeout(r, 300));
    assert.equal(sockets.length, 2);
    sockets[1].emit('connection.update', {
      connection: 'close',
      lastDisconnect: { error: { output: { statusCode: DisconnectReason.loggedOut } } },
    });
    assert.equal(events.at(-1), 'needs_pairing');
    const before = sockets.length;
    await new Promise((r) => setTimeout(r, 250));
    assert.equal(sockets.length, before);
  } finally {
    await c.stop();
  }
});

test('provider unlink reports request transmission only, closes socket, ignores stale QR and never reconnects', async () => {
  for (const fails of [false, true]) {
    const { h, events } = hooks();
    let socket: any, ended = 0, loggedOut = 0, created = 0;
    const c = new BaileysConnector(h, (() => {
      created++;
      const listeners = new Map<string, Function[]>();
      socket = { ev: { on(name: string, fn: Function) { listeners.set(name, [...(listeners.get(name) || []), fn]); } },
        emit(name: string, data: unknown) { for (const fn of listeners.get(name) || []) fn(data); },
        end() { ended++; }, logout: async () => { loggedOut++; if (fails) throw new Error('synthetic offline'); } };
      return socket;
    }) as any);
    await c.start(); socket.emit('connection.update', { qr: 'SYNTHETIC_QR' });
    const result = await c.stop(true);
    assert.equal(result?.remoteRevoked, false); assert.equal(result?.unlinkRequested, !fails); assert.equal(loggedOut, 1); assert.equal(ended, 1);
    socket.emit('connection.update', { qr: 'STALE_QR', connection: 'close', lastDisconnect: { error: { output: { statusCode: DisconnectReason.restartRequired } } } });
    await new Promise(resolve => setTimeout(resolve, 300));
    assert.equal(events.at(-1), 'idle'); assert.equal(created, 1);
  }
});

test('provider unlink timeout still stops locally and reports remote revocation unconfirmed', async t => {
  const { h } = hooks(); let ended = false;
  const c = new BaileysConnector(h, (() => ({ ev: { on() {} }, logout: () => new Promise(() => {}), end() { ended = true; } })) as any);
  await c.start();
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const pending = c.stop(true);
  t.mock.timers.tick(8001);
  assert.equal((await pending)?.remoteRevoked, false);
  assert.equal(ended, true);
});

test('authenticated encrypted backup + restore of SQLite, encrypted media and WA auth; tamper/wrong password/retention/lock', { skip: process.platform === 'win32' ? 'Requires Linux private file modes; run on Linux before activation' : false }, async () => {
  const dir = mkdtempSync(join(tmpdir(), 'yachad-backup-'));
  const data = join(dir, 'data'),
    keyPath = join(dir, 'master.key'),
    key = randomBytes(32);
  writeFileSync(keyPath, key.toString('base64'), { mode: 0o600 });
  const config: Config = {
    dataDir: data,
    masterKey: key,
    connector: 'mock',
    realAllowed: false,
    host: '127.0.0.1',
    port: 3000,
    origin: 'http://localhost:3000',
    secureCookie: false,
    maxMedia: 1048576,
    maxStorage: 67108864,
    childIdleMs: 900000,
    sessionMs: 3600000,
  };
  let store = new Store(config);
  const password = randomBytes(24).toString('base64url'),
    pass = randomBytes(32).toString('base64url');
  try {
    const f = await store.createFamily(
      'Backup test',
      { name: 'p', password },
      { name: 'c', password: password + 'c' },
    );
    store.syncGroups(f.familyId, [{ id: '1@g.us', title: 'Test' }]);
    store.grant(f.familyId, f.childId, '1@g.us', true);
    store.putMessage(f.familyId, {
      id: 'm',
      groupId: '1@g.us',
      text: 'RESTORE_TEXT_MARKER',
      sender: 'sample',
      fromMe: false,
      time: 1,
      kind: 'image',
      status: 'unknown',
      reactions: {},
      mediaId: 'media-test',
    });
    store.putMedia(f.familyId, '1@g.us', 'media-test', 'm', readFileSync('fixtures/garden.jpg'));
    store.authSet(f.familyId, 'creds', 'PRIVATE_AUTH_MARKER');
    store.createSession(
      store.user(
        store.one('SELECT * FROM users WHERE family_id=? AND id=?', f.familyId, f.parentId),
      ),
    );
    store.close();
    const release = await acquireDataLock(data);
    await assert.rejects(createBackup(data, keyPath, join(dir, 'backups'), pass));
    await release();
    const archive = await createBackup(data, keyPath, join(dir, 'backups'), pass);
    assert.equal(statSync(archive).mode & 0o777, 0o600);
    const bytes = readFileSync(archive);
    assert.ok(!bytes.includes(Buffer.from('PRIVATE_AUTH_MARKER')));
    assert.ok(!bytes.includes(Buffer.from(key.toString('base64'))));
    const restored = join(dir, 'restored');
    await assert.rejects(restoreBackup(archive, restored, 'wrong-passphrase-at-least-eighteen'));
    assert.equal(existsSync(restored), false);
    const corrupt = join(dir, 'corrupt.backup');
    bytes[100] ^= 1;
    writeFileSync(corrupt, bytes);
    await assert.rejects(restoreBackup(corrupt, restored, pass));
    assert.equal(existsSync(restored), false);
    const result = await restoreBackup(archive, restored, pass);
    assert.equal(result.dataDir, restored);
    store = new Store({
      ...config,
      dataDir: restored,
      masterKey: Buffer.from(readFileSync(result.keyPath, 'utf8'), 'base64'),
    });
    assert.equal(store.authGet(f.familyId, 'creds'), 'PRIVATE_AUTH_MARKER');
    assert.equal(store.getMessage(f.familyId, '1@g.us', 'm')?.text, 'RESTORE_TEXT_MARKER');
    assert.deepEqual(
      store.getMedia(f.familyId, '1@g.us', 'media-test')!.bytes,
      readFileSync('fixtures/garden.jpg'),
    );
    assert.equal(store.all('SELECT * FROM sessions').length, 0);
    store.close();
    for (let i = 1; i <= 9; i++)
      await createBackup(data, keyPath, join(dir, 'backups'), pass, Date.now() + i * 86400000);
    assert.ok(readdirSync(join(dir, 'backups')).length <= 7);
  } finally {
    try {
      store.close();
    } catch {}
    rmSync(dir, { recursive: true, force: true });
  }
});

test('untrusted media URLs and paths cannot make server-side requests', async () => {
  const { validateMediaSource } = await import('../src/connectors/baileys.js');
  const raw = (url: string, directPath?: string) => ({
    key: { id: 'x', remoteJid: '1@g.us' },
    message: { imageMessage: { url, directPath } },
  });
  for (const url of [
    'http://127.0.0.1/secret',
    'https://evil.example/x',
    'file:///etc/passwd',
    'https://whatsapp.net.evil.example/x',
    'https://user:secret@mmg.whatsapp.net/x',
  ])
    assert.throws(() => validateMediaSource(raw(url)));
  assert.throws(() => validateMediaSource(raw('https://mmg.whatsapp.net/x', '//evil.example/x')));
  assert.doesNotThrow(() => validateMediaSource(raw('https://mmg.whatsapp.net/x', '/v/t62/x')));
});

test('third-party console payloads never reach production logging', async () => {
  const { spawnSync } = await import('node:child_process');
  const result = spawnSync(
    process.execPath,
    [
      '--import',
      'tsx',
      '--input-type=module',
      '-e',
      `import {silencePayloadConsoles} from './src/logging.ts';silencePayloadConsoles();console.warn('PRIVATE_SIGNAL_KEY_TEST');console.error('PRIVATE_TEXT_TEST');process.stdout.write('allowed operational event');`,
    ],
    { cwd: process.cwd() },
  );
  assert.equal(result.status, 0);
  assert.equal(result.stdout.toString(), 'allowed operational event');
  assert.equal(result.stderr.toString(), '');
});

test('real adapter maps actual media bytes, captions, Opus duration and thumbnail in memory (stub transport)', async () => {
  const { h } = hooks();
  const calls: any[] = [];
  const c = new BaileysConnector(h);
  c.sock = {
    sendMessage: async (g: string, content: any, options: any) => {
      calls.push({ g, content, options });
      return {
        key: { id: options.messageId, remoteJid: g, fromMe: true },
        messageTimestamp: 1700000000,
        message: { conversation: content.text || content.caption || '' },
      };
    },
  } as any;
  await c.send({ id: 'text-id', groupId: '1@g.us', kind: 'text', text: 'Signature: hello' });
  assert.equal(calls[0].content.text, 'Signature: hello');
  await c.send({
    id: 'image-id',
    groupId: '1@g.us',
    kind: 'image',
    text: 'Signature: picture',
    bytes: readFileSync('fixtures/garden.jpg'),
    mime: 'image/jpeg',
  });
  assert.ok(Buffer.isBuffer(calls[1].content.jpegThumbnail));
  assert.ok(calls[1].content.jpegThumbnail.length > 100);
  assert.equal(calls[1].content.caption, 'Signature: picture');
  await c.send({
    id: 'audio-id',
    groupId: '1@g.us',
    kind: 'audio',
    text: '',
    bytes: readFileSync('fixtures/audio.ogg'),
    mime: 'audio/ogg; codecs=opus',
  });
  assert.equal(calls[2].content.ptt, true);
  assert.ok(calls[2].content.seconds > 2);
  await c.send({
    id: 'doc-id',
    groupId: '1@g.us',
    kind: 'document',
    text: 'Signature: document',
    bytes: readFileSync('fixtures/weekend.pdf'),
    mime: 'application/pdf',
    filename: 'sample.pdf',
  });
  assert.equal(calls[3].content.caption, 'Signature: document');
});

test('documented setup CLI creates real separate accounts and a protected random key without printing passwords', { skip: process.platform === 'win32' ? 'Requires Linux private file modes; run on Linux before activation' : false }, async () => {
  const { spawn } = await import('node:child_process');
  const directory = mkdtempSync(join(tmpdir(), 'yachad-setup-'));
  const parentPassword = randomBytes(24).toString('base64url');
  const childPassword = randomBytes(24).toString('base64url');
  const prompts = [
    ['Family label (local only): ', 'Synthetic setup'],
    ['Parent name (also used to sign in): ', 'הורה לבדיקה'],
    ['Child name (also used to sign in): ', 'עדי לבדיקה'],
    ['Parent password, 14+ characters: ', parentPassword],
    ['Child passphrase, 14+ characters: ', childPassword],
    ['Confirm parent password: ', parentPassword],
    ['Confirm child passphrase: ', childPassword],
  ];
  try {
    const child = spawn(process.execPath, ['--import', 'tsx', 'scripts/setup.ts'], {
      cwd: process.cwd(),
      env: {
        ...process.env,
        DATA_DIR: join(directory, 'data'),
        MASTER_KEY_FILE: join(directory, 'master.key'),
        SETUP_WRITE_ENV: 'false',
        CONNECTOR: 'mock',
        APP_ORIGIN: 'http://localhost:3000',
        COOKIE_SECURE: 'false',
      },
      stdio: ['pipe', 'pipe', 'pipe'],
    });
    let output = '',
      errors = '',
      index = 0;
    const timer = setTimeout(() => child.kill('SIGKILL'), 10000);
    child.stdout.on('data', (chunk) => {
      output += chunk.toString();
      if (index < prompts.length && output.includes(prompts[index][0])) {
        child.stdin.write(prompts[index][1] + '\n');
        index++;
      }
    });
    child.stderr.on('data', (chunk) => {
      errors += chunk.toString();
    });
    const code = await new Promise<number | null>((resolve, reject) => {
      child.on('error', reject);
      child.on('close', resolve);
    });
    clearTimeout(timer);
    assert.equal(code, 0, errors || `setup stopped after ${index}/${prompts.length} prompts; completed=${output.includes('Family created.')}`);
    assert.equal(index, prompts.length);
    assert.ok(!output.includes(parentPassword) && !output.includes(childPassword));
    assert.equal(statSync(join(directory, 'master.key')).mode & 0o777, 0o600);
    const db = new (await import('node:sqlite')).DatabaseSync(
      join(directory, 'data', 'app.sqlite'),
    );
    assert.equal((db.prepare('SELECT count(*) AS n FROM users').get() as any).n, 2);
    const rows = db.prepare('SELECT role,name,name_key,password FROM users').all() as any[];
    assert.notEqual(rows[0].password, rows[1].password);
    assert.deepEqual(new Set(rows.map((r) => r.role)), new Set(['parent', 'child']));
    assert.deepEqual(new Set(rows.map((r) => r.name)), new Set(['הורה לבדיקה', 'עדי לבדיקה']));
    assert.ok(rows.every(row => row.name_key === row.name));
    const columns = db.prepare('PRAGMA table_info(users)').all() as any[];
    assert.ok(!columns.some(column => ['username', 'display'].includes(column.name)), 'fresh setup creates no login/display aliases');
    db.close();
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});
