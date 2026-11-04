import { test } from 'node:test';
import assert from 'node:assert/strict';
import { fork, execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, writeFileSync, lstatSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { randomBytes } from 'node:crypto';
import { createServer } from 'node:net';
import { once } from 'node:events';

const prepared = process.env.YAHAD_PREPARED_WINDOWS_CORE;
test('offline packaged Node, bundled server and private CLR run actual mock app with empty PATH',
  { skip: process.platform !== 'win32' || !prepared, timeout: 120000 }, async t => {
    const app = join(prepared, 'app');
    assert.equal(process.execPath.toLowerCase(), resolve(prepared, 'runtime/node/node.exe').toLowerCase());
    // The payload carries no source: its schema is the one recorded at build time, and the
    // harness's own lease client comes from this checkout under the caller's loader.
    const { databaseSchema } = JSON.parse(readFileSync(join(prepared, 'legal/build-inputs.json'), 'utf8'));
    const { createPlatformFiles } = await import(new URL('../src/platform-files.ts', import.meta.url));
    const native = createPlatformFiles('win32', join(app, 'native/Yahad.Platform.exe'));
    const system32 = join(process.env.SystemRoot, 'System32');
    const root = mkdtempSync(join(tmpdir(), 'yahad-packaged-app-'));
    const data = join(root, 'data'), empty = join(root, 'empty');
    const identity = lstatSync(root), children = new Set();
    t.after(async () => {
      for (const child of children) {
        if (child.exitCode === null && child.signalCode === null) {
          const exited = once(child, 'exit'); child.kill();
          let timer;
          try { await Promise.race([exited, new Promise((_, reject) => { timer = setTimeout(() => reject(new Error('Owned packaged-app child did not exit')), 5000); })]); }
          finally { clearTimeout(timer); }
        }
      }
      // Root exit alone does not prove the native lease helper released its
      // data gate. Fail/retain the fixture if actual exclusive access is unknown.
      if (existsSync(data)) await (await native.acquireDataLock(data))();
      const observed = lstatSync(root);
      assert.ok(observed.isDirectory() && !observed.isSymbolicLink() && observed.dev === identity.dev && observed.ino === identity.ino, 'Owned fixture identity changed; cleanup refused');
      rmSync(root, { recursive: true });
    });
    const who = execFileSync(join(system32, 'whoami.exe'), ['/user', '/fo', 'csv', '/nh'], { encoding: 'utf8', windowsHide: true });
    const sid = who.match(/S-1-5-\d+(?:-\d+)+/)?.[0]; assert.ok(sid);
    execFileSync(join(system32, 'icacls.exe'), [root, '/inheritance:r', '/grant:r', `*${sid}:(OI)(CI)F`, '*S-1-5-18:(OI)(CI)F', '*S-1-5-32-544:(OI)(CI)F'], { windowsHide: true, stdio: 'pipe' });
    mkdirSync(data); mkdirSync(empty);
    const key = join(root, 'master-key'); writeFileSync(key, randomBytes(32).toString('base64'));
    const environment = { SystemRoot: process.env.SystemRoot, WINDIR: process.env.SystemRoot, TEMP: root, TMP: root,
      LOCALAPPDATA: root, PATH: empty, NODE_ENV: 'production', MASTER_KEY_FILE: key, DATA_DIR: data,
      CONNECTOR: 'mock', ALLOW_REAL_WHATSAPP: 'no', HOST: '127.0.0.1', COOKIE_SECURE: 'false',
      SETUP_CAPABILITY_FILE: join(root, 'missing-capability'), DIAGNOSTICS_ENABLED: 'false' };
    const entry = join(app, 'server/main.mjs');
    for (const command of ['node', 'npm', 'python', 'dotnet', 'ffmpeg', 'caddy'])
      assert.throws(() => execFileSync(join(system32, 'where.exe'), [command], { env: environment, windowsHide: true, stdio: 'ignore' }));
    async function port() {
      const server = createServer(); server.listen(0, '127.0.0.1'); await once(server, 'listening');
      const result = server.address().port; await new Promise(done => server.close(done)); return result;
    }
    function start(selected) {
      const child = fork(entry, { cwd: app, execPath: process.execPath,
        execArgv: [],
        env: { ...environment, PORT: String(selected), APP_ORIGIN: `http://127.0.0.1:${selected}` },
        stdio: ['ignore', 'pipe', 'pipe', 'ipc'] });
      children.add(child); return child;
    }
    async function ready(child, selected) {
      let output = '', error = '';
      child.stdout.on('data', chunk => output = (output + chunk).slice(-8192));
      child.stderr.on('data', chunk => error = (error + chunk).slice(-8192));
      // Measured cold packaged startup reached 24.9s. Keep this fixture bounded
      // independently of stop/cleanup rather than retaining its old 20s cutoff.
      const started = Date.now(), deadline = started + 30000;
      while (!output.includes('Yachad listening')) {
        assert.equal(child.exitCode, null, 'packaged mock app startup failed: ' + error);
        assert.ok(Date.now() < deadline, 'startup deadline; output=' + output + '; stderr=' + error);
        await new Promise(done => setTimeout(done, 40));
      }
      const response = await fetch(`http://127.0.0.1:${selected}/health/ready`); assert.equal(response.status, 200);
      const health = await response.json(); assert.equal(health.ok, true); assert.equal(health.databaseSchema, databaseSchema);
      t.diagnostic(JSON.stringify({ packagedStartupMilliseconds: Date.now() - started }));
    }
    const selected = await port(), child = start(selected), exited = once(child, 'exit');
    try {
      await ready(child, selected);
      const competing = start(await port()); competing.stdout.resume(); competing.stderr.resume(); const competingExit = once(competing, 'exit');
      const timeout = setTimeout(() => competing.kill(), 20000);
      // A second writer is refused at the data lock, whose fixed startup exit code is 11.
      try { assert.equal((await competingExit)[0], 11); }
      finally { clearTimeout(timeout); if (competing.exitCode === null) competing.kill(); }
      child.send({ type: 'yahad.shutdown' }); assert.equal((await exited)[0], 0);
      assert.ok(existsSync(join(data, 'app.sqlite'))); await (await native.acquireDataLock(data))();
      const resumed = start(selected), resumedExit = once(resumed, 'exit');
      try { await ready(resumed, selected); resumed.disconnect(); assert.equal((await resumedExit)[0], 0); }
      finally { if (resumed.exitCode === null && resumed.signalCode === null) { resumed.kill(); await resumedExit; } }
      await (await native.acquireDataLock(data))();
    } finally { if (child.exitCode === null && child.signalCode === null) { child.kill(); await exited; } }
  });
