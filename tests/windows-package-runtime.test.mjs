import { test } from 'node:test';
import assert from 'node:assert/strict';
import { fork, execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { randomBytes } from 'node:crypto';
import { createServer } from 'node:net';
import { once } from 'node:events';

const prepared = process.env.YAHAD_PREPARED_WINDOWS_CORE;
test('offline packaged Node, production modules and private CLR run actual mock app with empty PATH',
  { skip: process.platform !== 'win32' || !prepared, timeout: 75000 }, async () => {
    const app = join(prepared, 'app');
    assert.equal(process.execPath.toLowerCase(), resolve(prepared, 'runtime/node/node.exe').toLowerCase());
    const { DATABASE_SCHEMA } = await import(pathToFileURL(join(app, 'src/database-schema.ts')));
    const { createPlatformFiles } = await import(pathToFileURL(join(app, 'src/platform-files.ts')));
    const native = createPlatformFiles('win32', join(app, 'native/Yahad.Platform.exe'));
    const system32 = join(process.env.SystemRoot, 'System32');
    const root = mkdtempSync(join(tmpdir(), 'yahad-packaged-app-'));
    const who = execFileSync(join(system32, 'whoami.exe'), ['/user', '/fo', 'csv', '/nh'], { encoding: 'utf8', windowsHide: true });
    const sid = who.match(/S-1-5-\d+(?:-\d+)+/)?.[0]; assert.ok(sid);
    execFileSync(join(system32, 'icacls.exe'), [root, '/inheritance:r', '/grant:r', `*${sid}:(OI)(CI)F`, '*S-1-5-18:(OI)(CI)F', '*S-1-5-32-544:(OI)(CI)F'], { windowsHide: true, stdio: 'pipe' });
    const data = join(root, 'data'), empty = join(root, 'empty'); mkdirSync(data); mkdirSync(empty);
    const key = join(root, 'master-key'); writeFileSync(key, randomBytes(32).toString('base64'));
    const environment = { SystemRoot: process.env.SystemRoot, WINDIR: process.env.SystemRoot, TEMP: root, TMP: root,
      LOCALAPPDATA: root, PATH: empty, NODE_ENV: 'production', MASTER_KEY_FILE: key, DATA_DIR: data,
      CONNECTOR: 'mock', ALLOW_REAL_WHATSAPP: 'no', HOST: '127.0.0.1', COOKIE_SECURE: 'false',
      SETUP_CAPABILITY_FILE: join(root, 'missing-capability'), DIAGNOSTICS_ENABLED: 'false' };
    for (const command of ['node', 'npm', 'python', 'dotnet', 'ffmpeg', 'caddy'])
      assert.throws(() => execFileSync(join(system32, 'where.exe'), [command], { env: environment, windowsHide: true, stdio: 'ignore' }));
    async function port() {
      const server = createServer(); server.listen(0, '127.0.0.1'); await once(server, 'listening');
      const result = server.address().port; await new Promise(done => server.close(done)); return result;
    }
    function start(selected) {
      return fork(join(app, 'src/main.ts'), { cwd: app, execPath: process.execPath,
        execArgv: ['--import', pathToFileURL(join(app, 'node_modules/tsx/dist/loader.mjs')).href],
        env: { ...environment, PORT: String(selected), APP_ORIGIN: `http://127.0.0.1:${selected}` },
        stdio: ['ignore', 'pipe', 'pipe', 'ipc'] });
    }
    async function ready(child, selected) {
      let output = ''; child.stdout.on('data', chunk => output += chunk); child.stderr.resume();
      const deadline = Date.now() + 20000;
      while (!output.includes('Yachad listening')) {
        assert.equal(child.exitCode, null, 'packaged mock app startup failed'); assert.ok(Date.now() < deadline, 'startup deadline');
        await new Promise(done => setTimeout(done, 40));
      }
      const response = await fetch(`http://127.0.0.1:${selected}/health/ready`); assert.equal(response.status, 200);
      const health = await response.json(); assert.equal(health.ok, true); assert.equal(health.databaseSchema, DATABASE_SCHEMA);
    }
    const selected = await port(), child = start(selected), exited = once(child, 'exit');
    try {
      await ready(child, selected);
      const competing = start(await port()); competing.stdout.resume(); competing.stderr.resume(); const competingExit = once(competing, 'exit');
      const timeout = setTimeout(() => competing.kill(), 20000);
      try { assert.equal((await competingExit)[0], 1); }
      finally { clearTimeout(timeout); if (competing.exitCode === null) competing.kill(); }
      child.send({ type: 'yahad.shutdown' }); assert.equal((await exited)[0], 0);
      assert.ok(existsSync(join(data, 'app.sqlite'))); await (await native.acquireDataLock(data))();
      const resumed = start(selected), resumedExit = once(resumed, 'exit');
      try { await ready(resumed, selected); resumed.disconnect(); assert.equal((await resumedExit)[0], 0); }
      finally { if (resumed.exitCode === null && resumed.signalCode === null) { resumed.kill(); await resumedExit; } }
      await (await native.acquireDataLock(data))();
    } finally { if (child.exitCode === null && child.signalCode === null) { child.kill(); await exited; } }
  });
