import { test } from 'node:test';
import assert from 'node:assert/strict';
import { fork, execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { randomBytes } from 'node:crypto';
import { createServer } from 'node:net';
import { once } from 'node:events';
import { createPlatformFiles } from '../src/platform-files.js';
import { loadConfig } from '../src/config.js';
import { readBootstrap } from '../src/bootstrap.js';
import { DATABASE_SCHEMA } from '../src/database-schema.js';

const helper = resolve('native/Yahad.Platform.exe');
const available = process.platform === 'win32' && existsSync(helper);
const native = createPlatformFiles('win32', helper);
function fixture() {
  const directory = mkdtempSync(join(tmpdir(), 'yahad-native-app-'));
  const who = execFileSync('whoami.exe', ['/user', '/fo', 'csv', '/nh'], { encoding: 'utf8', windowsHide: true });
  const sid = who.match(/S-1-5-\d+(?:-\d+)+/)?.[0];
  assert.ok(sid);
  execFileSync('icacls.exe', [directory, '/inheritance:r', '/grant:r', `*${sid}:(OI)(CI)F`, '*S-1-5-18:(OI)(CI)F', '*S-1-5-32-544:(OI)(CI)F'], { windowsHide: true, stdio: 'pipe' });
  return directory;
}
test('Windows native private key reader and app config use ACLs rather than POSIX mode', { skip: !available }, () => {
  const directory = fixture(), path = join(directory, 'master-key');
  const key = randomBytes(32); writeFileSync(path, key.toString('base64'));
  assert.equal(native.readMasterKey(path).toString(), key.toString('base64'));
  const previous = { ...process.env };
  try {
    process.env.MASTER_KEY_FILE = path; process.env.APP_ORIGIN = 'http://127.0.0.1:3000'; process.env.COOKIE_SECURE = 'false';
    assert.deepEqual(loadConfig(native).masterKey, key);
  } finally { process.env = previous; }
  execFileSync('icacls.exe', [path, '/grant', '*S-1-1-0:R'], { windowsHide: true, stdio: 'pipe' });
  assert.throws(() => native.readMasterKey(path), /Native file boundary refused/);
});
test('Windows capability keeps administrator-owner requirement and missing-file semantics', { skip: !available }, () => {
  const directory = fixture(), path = join(directory, 'capability');
  assert.equal(readBootstrap(path, Date.now(), native), undefined);
  writeFileSync(path, JSON.stringify({ token: randomBytes(32).toString('base64url'), expiresAt: Date.now() + 60000 }));
  assert.throws(() => readBootstrap(path, Date.now(), native), /Native file boundary refused/);
});
test('Windows native data lease excludes a second process and releases normally', { skip: !available }, async () => {
  const directory = fixture(); const release = await native.acquireDataLock(directory);
  try { await assert.rejects(native.acquireDataLock(directory), /Native data lock refused/); }
  finally { await release(); }
  await (await native.acquireDataLock(directory))();
});
test('missing Windows helper fails closed without falling back to chmod', { skip: process.platform !== 'win32' }, () => {
  const absent = createPlatformFiles('win32', resolve('native/absent-helper.exe'));
  assert.throws(() => absent.readMasterKey(resolve('package.json')), /Native file boundary refused/);
});
test('abrupt Node parent death closes the native helper lifetime pipe and releases its lease', { skip: !available, timeout: 20000 }, async () => {
  const directory = fixture();
  const owner = fork(resolve('tests/fixtures/native-lock-owner.ts'), [directory], { execArgv: ['--import', 'tsx'], stdio: ['ignore', 'pipe', 'pipe', 'ipc'] });
  const exited = once(owner, 'exit'); owner.stdout!.resume(); owner.stderr!.resume();
  try {
    const [message] = await once(owner, 'message'); assert.deepEqual(message, { ready: true });
    owner.kill(); await exited;
    const deadline = Date.now() + 8000;
    while (true) {
      try { await (await native.acquireDataLock(directory))(); break; }
      catch { assert.ok(Date.now() < deadline, 'orphaned native lease'); await new Promise(done => setTimeout(done, 40)); }
    }
  } finally { if (owner.exitCode === null && owner.signalCode === null) { owner.kill(); await exited; } }
});
async function freePort() {
  const server = createServer(); server.listen(0, '127.0.0.1'); await once(server, 'listening');
  const port = (server.address() as { port: number }).port;
  await new Promise<void>(done => server.close(() => done())); return port;
}
test('helper crash retains the exclusive native lease in its still-living Node owner', { skip: !available, timeout: 25000 }, async () => {
  const directory = fixture();
  const owner = fork(resolve('tests/fixtures/native-remote-lease-owner.ts'), [directory], { execArgv: ['--import', 'tsx'], stdio: ['ignore', 'pipe', 'pipe', 'ipc'] });
  const exited = once(owner, 'exit'); owner.stdout!.resume(); owner.stderr!.resume();
  try {
    assert.deepEqual((await once(owner, 'message'))[0], { ready: true });
    const helperDead = once(owner, 'message'); owner.send({ killHelper: true });
    assert.deepEqual((await helperDead)[0], { helperExited: true });
    await assert.rejects(native.acquireDataLock(directory), /Native data lock refused/);
    owner.kill(); await exited;
    await (await native.acquireDataLock(directory))();
  } finally { if (owner.exitCode === null && owner.signalCode === null) { owner.kill(); await exited; } }
});
test('actual mock app starts without development tools on PATH, excludes another writer and stops through private IPC', { skip: !available, timeout: 75000 }, async () => {
  const root = fixture(), data = join(root, 'data'); mkdirSync(data);
  const keyPath = join(root, 'master-key'); writeFileSync(keyPath, randomBytes(32).toString('base64'));
  const port = await freePort(), emptyPath = join(root, 'empty-path'); mkdirSync(emptyPath);
  const environment = { ...process.env, MASTER_KEY_FILE: keyPath, DATA_DIR: data, CONNECTOR: 'mock', ALLOW_REAL_WHATSAPP: 'no', HOST: '127.0.0.1',
    PORT: String(port), APP_ORIGIN: `http://127.0.0.1:${port}`, COOKIE_SECURE: 'false', SETUP_CAPABILITY_FILE: join(root, 'missing-capability'), DIAGNOSTICS_ENABLED: 'false',
    PATH: emptyPath, DOTNET_ROOT: undefined, DOTNET_ROOT_X64: undefined, DOTNET_STARTUP_HOOKS: undefined, DOTNET_SHARED_STORE: undefined, NODE_OPTIONS: undefined };
  // The test launches private Node through an absolute path. This proves PATH
  // independence, not that the future installer has bundled every dependency.
  const where = join(process.env.SystemRoot || 'C:\\Windows', 'System32', 'where.exe');
  for (const command of ['node', 'python', 'dotnet', 'ffmpeg', 'caddy'])
    assert.throws(() => execFileSync(where, [command], { env: environment, windowsHide: true, stdio: 'ignore' }));
  const start = (selectedPort = port) => fork(resolve('src/main.ts'), { execArgv: ['--import', 'tsx'], env: { ...environment, PORT: String(selectedPort), APP_ORIGIN: `http://127.0.0.1:${selectedPort}` }, stdio: ['ignore', 'pipe', 'pipe', 'ipc'] });
  const child = start(); const exited = once(child, 'exit'); let output = '';
  child.stdout!.on('data', chunk => output += chunk.toString()); child.stderr!.resume();
  try {
    const deadline = Date.now() + 20000;
    while (!output.includes('Yachad listening')) {
      if (child.exitCode !== null) throw new Error('Native mock app failed startup');
      assert.ok(Date.now() < deadline, 'startup deadline'); await new Promise(done => setTimeout(done, 40));
    }
    const health = await fetch(`http://127.0.0.1:${port}/health/ready`).then(r => r.json()) as { ok: boolean; databaseSchema: number };
    assert.equal(health.ok, true); assert.equal(health.databaseSchema, DATABASE_SCHEMA);
    // A different port ensures a missing data lock cannot pass through a bind failure.
    const concurrent = start(await freePort()); concurrent.stdout!.resume(); concurrent.stderr!.resume();
    const concurrentDeadline = setTimeout(() => concurrent.kill(), 20000);
    try { assert.equal((await once(concurrent, 'exit'))[0], 1); }
    finally { clearTimeout(concurrentDeadline); if (concurrent.exitCode === null) concurrent.kill(); }
    child.send({ type: 'yahad.shutdown' });
    assert.equal((await exited)[0], 0);
    assert.ok(existsSync(join(data, 'app.sqlite')));
    await (await native.acquireDataLock(data))();
    const resumed = start(); const resumedExit = once(resumed, 'exit'); let resumedOutput = '';
    resumed.stdout!.on('data', chunk => resumedOutput += chunk.toString()); resumed.stderr!.resume();
    try {
      const resumeDeadline = Date.now() + 20000;
      while (!resumedOutput.includes('Yachad listening')) {
        assert.equal(resumed.exitCode, null, 'restart failed'); assert.ok(Date.now() < resumeDeadline, 'restart deadline');
        await new Promise(done => setTimeout(done, 40));
      }
      await fetch(`http://127.0.0.1:${port}/health/ready`);
      resumed.disconnect(); assert.equal((await resumedExit)[0], 0);
      await (await native.acquireDataLock(data))();
    } finally { if (resumed.exitCode === null && resumed.signalCode === null) { resumed.kill(); await resumedExit; } }
    assert.equal(readFileSync(keyPath, 'utf8').length, 44);
  } finally { if (child.exitCode === null) { child.kill(); await exited; } }
});
