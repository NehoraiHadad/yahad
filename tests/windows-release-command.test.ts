import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { createHash, generateKeyPairSync, sign } from 'node:crypto';
import { deflateRawSync } from 'node:zlib';
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';
import { collectReleaseAssets, install, planInfrastructure, publish, requireBuiltKey, requireRuntimeAboveBootstrap, runtimeVersionFor, withInfrastructureSource } from '../scripts/release-windows.mjs';

const scripts = join(import.meta.dirname, '..', 'scripts');
const git = (cwd: string, ...args: string[]) => execFileSync('git', ['-c', 'user.name=t', '-c', 'user.email=t@invalid', ...args], { cwd, stdio: 'pipe', encoding: 'utf8' });
const pair = () => {
  const { publicKey, privateKey } = generateKeyPairSync('ec', { namedCurve: 'prime256v1' });
  return { spki: publicKey.export({ type: 'spki', format: 'der' }) as Buffer, pem: privateKey.export({ type: 'pkcs8', format: 'pem' }) as string, private: privateKey };
};
const keyId = (spki: Buffer) => createHash('sha256').update(spki).digest('hex').slice(0, 16);
const sha = (bytes: Buffer) => createHash('sha256').update(bytes).digest('hex');

// The command locates its checkout from its own path, so each case runs a copy of it
// inside a throwaway repository. Every refusal comes before any build starts.
function fixture(root: string, committed: Buffer | null) {
  const repo = join(root, 'repo');
  mkdirSync(join(repo, 'scripts'), { recursive: true });
  mkdirSync(join(repo, 'installer'));
  for (const name of ['release-windows.mjs', 'windows-release-key.mjs']) copyFileSync(join(scripts, name), join(repo, 'scripts', name));
  writeFileSync(join(repo, 'package.json'), '{"version":"1.2.3"}');
  git(repo, 'init', '-q');
  if (committed) writeFileSync(join(repo, 'installer', 'release-key.spki'), committed);
  git(repo, 'add', '-A');
  git(repo, 'commit', '-q', '-m', 'fixture');
  return repo;
}
const release = (repo: string, ...args: string[]) => spawnSync(process.execPath, [join(repo, 'scripts', 'release-windows.mjs'), 'build', ...args], { cwd: repo, encoding: 'utf8' });

test('windows release build refuses before building and publishes nothing', () => {
  const root = mkdtempSync(join(tmpdir(), 'yahad-release-cmd-'));
  try {
    const trusted = pair();
    const pem = join(root, 'key.pem');
    writeFileSync(pem, trusted.pem);
    const out = join(root, 'out');

    let repo = fixture(join(root, 'none'), null);
    let result = release(repo, pem, '--out', out);
    assert.equal(result.status, 1);
    assert.match(result.stderr, /installer\/release-key\.spki is not committed/);

    // On disk but not committed is still not committed.
    repo = fixture(join(root, 'uncommitted'), null);
    writeFileSync(join(repo, 'installer', 'release-key.spki'), trusted.spki);
    result = release(repo, pem, '--out', out);
    assert.match(result.stderr, /is not committed/);

    repo = fixture(join(root, 'mismatch'), pair().spki);
    result = release(repo, pem, '--out', out);
    assert.match(result.stderr, /is not the committed installer\/release-key\.spki/);

    repo = fixture(join(root, 'dirty'), trusted.spki);
    writeFileSync(join(repo, 'stray.txt'), 'x');
    result = release(repo, pem, '--out', out);
    assert.match(result.stderr, /working tree is dirty/);

    repo = fixture(join(root, 'inside'), trusted.spki);
    writeFileSync(join(repo, 'key.pem'), trusted.pem);
    result = release(repo, join(repo, 'key.pem'), '--out', out);
    assert.match(result.stderr, /signing key inside the checkout/);

    assert.notEqual(release(repo, pem).status, 0);
    assert.equal(existsSync(out), false);
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test('windows release command refuses a package that trusts another key', () => {
  const root = mkdtempSync(join(tmpdir(), 'yahad-release-built-'));
  try {
    const committed = pair().spki;
    const build = (verifiedKey: string | null, manifestKey: string) => {
      const dir = mkdtempSync(join(root, 'p-'));
      mkdirSync(join(dir, 'release'));
      writeFileSync(join(dir, 'verify-package.log'), JSON.stringify({ releaseKeyId: verifiedKey }));
      writeFileSync(join(dir, 'release', 'yahad-windows-release-1.2.3.json'), JSON.stringify({ format: 2, product: 'yahad-windows-release', keyId: manifestKey }));
      return dir;
    };
    const good = keyId(committed);
    assert.equal(requireBuiltKey(build(good, good), committed).manifestName, 'yahad-windows-release-1.2.3.json');
    assert.throws(() => requireBuiltKey(build('0123456789abcdef', good), committed), /trusts key 0123456789abcdef/);
    assert.throws(() => requireBuiltKey(build(null, good), committed), /trusts key none/);
    assert.throws(() => requireBuiltKey(build(good, '0123456789abcdef'), committed), /manifest names key/);
    // A package still emitting the previous manifest family is not a release.
    const old = build(good, good);
    rmSync(join(old, 'release', 'yahad-windows-release-1.2.3.json'));
    writeFileSync(join(old, 'release', 'yahad-windows-1.2.3.json'), JSON.stringify({ keyId: good }));
    assert.throws(() => requireBuiltKey(old, committed), /found 0/);
    const v1 = build(good, good);
    writeFileSync(join(v1, 'release', 'yahad-windows-release-1.2.3.json'), JSON.stringify({ format: 1, product: 'yahad-windows', keyId: good }));
    assert.throws(() => requireBuiltKey(v1, committed), /not format 2 of yahad-windows-release/);
  } finally { rmSync(root, { recursive: true, force: true }); }
});

// ---- publish and install: local bare origin, fake gh first on PATH, stubbed build and launch ----
type Pair = ReturnType<typeof pair>;
function zip(entries: Record<string, Buffer>) {
  const locals: Buffer[] = []; const central: Buffer[] = []; let offset = 0;
  for (const [name, data] of Object.entries(entries)) {
    const packed = deflateRawSync(data); const nameBytes = Buffer.from(name);
    const local = Buffer.alloc(30); local.writeUInt32LE(0x04034b50, 0); local.writeUInt16LE(20, 4); local.writeUInt16LE(8, 8);
    local.writeUInt32LE(packed.length, 18); local.writeUInt32LE(data.length, 22); local.writeUInt16LE(nameBytes.length, 26);
    const head = Buffer.alloc(46); head.writeUInt32LE(0x02014b50, 0); head.writeUInt16LE(20, 4); head.writeUInt16LE(20, 6); head.writeUInt16LE(8, 10);
    head.writeUInt32LE(packed.length, 20); head.writeUInt32LE(data.length, 24); head.writeUInt16LE(nameBytes.length, 28); head.writeUInt32LE(offset, 42);
    locals.push(local, nameBytes, packed); central.push(head, nameBytes);
    offset += 30 + nameBytes.length + packed.length;
  }
  const directory = Buffer.concat(central);
  const end = Buffer.alloc(22); end.writeUInt32LE(0x06054b50, 0); end.writeUInt16LE(Object.keys(entries).length, 8); end.writeUInt16LE(Object.keys(entries).length, 10);
  end.writeUInt32LE(directory.length, 12); end.writeUInt32LE(offset, 16);
  return Buffer.concat([...locals, directory, end]);
}

// What the manifest must be consistent with. Every field a check reads can be bent by a case.
type Flaw = {
  manifest?: (manifest: any) => void; appEntries?: (entries: Record<string, Buffer>) => void;
  infraTree?: Buffer; signWith?: Pair; setup?: Buffer; omitSetupInfraTree?: boolean;
};
function windowsFiles(version: string, keys: Pair, flaw: Flaw = {}) {
  const appFiles: Record<string, Buffer> = { 'app/package.json': Buffer.from('{"name":"yahad"}'), 'app/server/main.mjs': Buffer.from('export {};\n'.repeat(20)) };
  const tree = (files: Record<string, Buffer>) => Buffer.from(JSON.stringify({ format: 1, version, sourceCommit: 'a'.repeat(40), files: Object.entries(files).sort().map(([path, data]) => ({ path, size: data.length, sha256: sha(data) })) }));
  const treeBytes = tree(appFiles);
  const entries: Record<string, Buffer> = { 'app-tree.json': treeBytes, ...appFiles };
  flaw.appEntries?.(entries);
  const appZip = zip(entries);
  const infraTree = Buffer.from(JSON.stringify({ format: 1, version: '1.0.0', sourceCommit: 'b'.repeat(40), files: [{ path: 'host/Yahad.ServiceHost.exe', size: 1, sha256: sha(Buffer.from('h')) }] }));
  const install = zip({ 'infra/infra-tree.json': flaw.infraTree ?? infraTree, 'infra/host/Yahad.ServiceHost.exe': Buffer.from('h') });
  // The distributable Setup carries install.zip verbatim as a managed resource.
  const setup = flaw.setup ?? Buffer.concat([Buffer.from('MZ setup ' + version), install, Buffer.from(' trailer')]);
  const names = { manifest: `yahad-windows-release-${version}.json`, setup: `Yahad.Setup-${version}.exe`, app: `yahad-windows-app-${version}.zip` };
  const manifest = {
    format: 2, product: 'yahad-windows-release', version, sourceCommit: 'a'.repeat(40), keyId: keyId(keys.spki), createdUnix: 1,
    infrastructure: { version: '1.0.0', contractMin: 1, contractMax: 1, sourceCommit: 'b'.repeat(40), treeSha256: sha(infraTree), setup: { name: names.setup, sha256: sha(setup), bytes: setup.length } },
    app: { archive: { name: names.app, sha256: sha(appZip), bytes: appZip.length }, treeSha256: sha(treeBytes), requiresContract: 1, databaseSchema: 6, databaseMigrationsFrom: [1, 2, 3, 4, 5] },
    minimumFrom: { app: '0.0.0', infrastructure: '0.0.0' }
  };
  flaw.manifest?.(manifest);
  const bytes = Buffer.from(JSON.stringify(manifest));
  return {
    [names.manifest]: bytes,
    [`${names.manifest}.sig`]: sign('sha256', Buffer.concat([Buffer.from('yahad-release-v2\n'), bytes]), { key: (flaw.signWith ?? keys).private, dsaEncoding: 'ieee-p1363' }),
    [names.app]: appZip, [names.setup]: setup, 'Yahad.Setup.exe': setup
  } as Record<string, Buffer>;
}

function writeAssets(directory: string, version: string, keys: Pair, omit: string[] = [], flaw: Flaw = {}) {
  mkdirSync(directory, { recursive: true });
  const zipBytes = Buffer.from('zip-' + version);
  const worker = Buffer.from('worker-' + version);
  const files: Record<string, Buffer> = {
    'yachad-release.json': Buffer.from(JSON.stringify({ format: 2, version, archive: 'yachad-app.zip', bytes: zipBytes.length, sha256: sha(zipBytes) })),
    'yachad-app.zip': zipBytes,
    'yachad-updater.json': Buffer.from(JSON.stringify({ format: 1, kind: 'yachad-updater', version: runtimeVersionFor(version), archive: 'yachad-updater.zip', bytes: worker.length, sha256: sha(worker) })),
    'yachad-updater.zip': worker,
    ...windowsFiles(version, keys, flaw)
  };
  for (const [name, bytes] of Object.entries(files)) if (!omit.includes(name)) writeFileSync(join(directory, name), bytes);
}

function world(root: string) {
  const keys = pair();
  const pem = join(root, 'key.pem');
  writeFileSync(pem, keys.pem);
  const origin = join(root, 'origin.git');
  git(root, 'init', '-q', '--bare', '-b', 'main', origin);
  const repo = join(root, 'work');
  git(root, 'clone', '-q', origin, repo);
  git(repo, 'config', 'user.name', 'Maintainer');
  git(repo, 'config', 'user.email', 'm@invalid');
  git(repo, 'checkout', '-q', '-b', 'main');
  mkdirSync(join(repo, 'installer'));
  writeFileSync(join(repo, '.gitignore'), '.local/\n');
  writeFileSync(join(repo, 'package.json'), JSON.stringify({ name: 'x', version: '0.5.3' }, null, 2) + '\n');
  writeFileSync(join(repo, 'package-lock.json'), JSON.stringify({ name: 'x', version: '0.5.3', packages: { '': { version: '0.5.3' } } }, null, 2) + '\n');
  writeFileSync(join(repo, 'installer', 'release-key.spki'), keys.spki);
  git(repo, 'add', '-A');
  git(repo, 'commit', '-q', '-m', 'base');
  git(repo, 'push', '-q', 'origin', 'main');
  // A fake gh: records every call, reports v0.5.3 as latest and lists what it was asked to create.
  const bin = join(root, 'bin');
  mkdirSync(bin);
  const log = join(root, 'gh.log');
  writeFileSync(join(bin, 'fake-gh.mjs'), `
import { appendFileSync, existsSync, readFileSync, writeFileSync } from 'node:fs';
import { basename } from 'node:path';
const args = process.argv.slice(2); const state = ${JSON.stringify(join(root, 'gh-state.json'))};
appendFileSync(${JSON.stringify(log)}, JSON.stringify(args) + '\\n');
if (args[0] === 'api' && args.includes('.tag_name')) console.log('v0.5.3');
else if (args[0] === 'api') console.log(existsSync(state) ? readFileSync(state, 'utf8') : '');
else if (args[0] === '--version') console.log('fake-gh');
else if (args[0] === 'release' && process.env.FAKE_GH_FAIL) process.exit(1);
else if (args[0] === 'release') writeFileSync(state, args.slice(3, args.indexOf('--repo')).map((p) => basename(p)).join('\\n'));
`);
  if (process.platform === 'win32') writeFileSync(join(bin, 'gh.cmd'), '@node "%~dp0fake-gh.mjs" %*\r\n');
  else writeFileSync(join(bin, 'gh'), '#!/bin/sh\nexec node "$(dirname "$0")/fake-gh.mjs" "$@"\n', { mode: 0o755 });
  const calls = () => (existsSync(log) ? readFileSync(log, 'utf8').trim().split('\n').filter(Boolean).map((line) => JSON.parse(line) as string[]) : []);
  const stage = (omit: string[] = []) => async ({ clone, root: temp }: { clone: string; root: string }) => {
    const version = JSON.parse(readFileSync(join(clone, 'package.json'), 'utf8')).version as string;
    writeAssets(join(temp, 'assets'), version, keys, omit);
    return { assets: join(temp, 'assets'), spki: keys.spki };
  };
  const options = { key: pem, 'node-dir': root } as Record<string, string | boolean>;
  const plan = async () => ({ mode: 'build' as const, digest: 'd'.repeat(64) });
  return { keys, pem, origin, repo, calls, stage, plan, options };
}

async function withWorld(work: (w: ReturnType<typeof world>, root: string) => Promise<void>) {
  const root = mkdtempSync(join(tmpdir(), 'yahad-publish-'));
  const saved = { path: process.env.PATH, temp: process.env.TEMP, tmp: process.env.TMP, tmpdir: process.env.TMPDIR };
  try {
    const w = world(root);
    // Temporary clones go in a folder of their own so a leftover is visible.
    const scratch = join(root, 'scratch');
    mkdirSync(scratch);
    process.env.TEMP = process.env.TMP = process.env.TMPDIR = scratch;
    process.env.PATH = `${join(root, 'bin')}${process.platform === 'win32' ? ';' : ':'}${process.env.PATH}`;
    await work(w, root);
    assert.deepEqual(readdirSync(scratch), [], 'no temporary clone is left behind');
  } finally {
    process.env.PATH = saved.path;
    for (const [key, value] of [['TEMP', saved.temp], ['TMP', saved.tmp], ['TMPDIR', saved.tmpdir]] as const) { if (value === undefined) delete process.env[key]; else process.env[key] = value; }
    rmSync(root, { recursive: true, force: true });
  }
}
const originMain = (origin: string) => git(origin, 'rev-parse', 'main').trim();

test('release:publish refuses anything but a clean main equal to origin/main', async () => {
  await withWorld(async (w) => {
    const refused = (pattern: RegExp) => assert.rejects(publish({ repo: w.repo, plan: w.plan, options: w.options, stage: w.stage(), confirm: async () => '0.5.4' }), pattern);

    writeFileSync(join(w.repo, 'stray.txt'), 'x');
    await refused(/working tree is dirty/);
    rmSync(join(w.repo, 'stray.txt'));

    git(w.repo, 'commit', '-q', '--allow-empty', '-m', 'local only');
    await refused(/1 ahead, 0 behind/);
    git(w.repo, 'reset', '-q', '--hard', 'origin/main');

    // Behind: someone else pushed.
    const other = join(w.repo, '..', 'other');
    git(w.repo, 'clone', '-q', w.origin, other);
    git(other, 'commit', '-q', '--allow-empty', '-m', 'elsewhere');
    git(other, 'push', '-q', 'origin', 'HEAD:main');
    const moved = originMain(w.origin);
    await refused(/0 ahead, 1 behind/);
    git(w.repo, 'pull', '-q', '--ff-only', 'origin', 'main');

    git(w.repo, 'checkout', '-q', '-b', 'feature');
    await refused(/Releases come from main; this checkout is on feature/);
    git(w.repo, 'checkout', '-q', 'main');

    assert.equal(w.calls().some((call) => call[0] === 'release'), false);
    assert.equal(originMain(w.origin), moved);
    assert.equal(git(w.origin, 'tag', '--list').trim(), '');
  });
});

test('release:publish refuses a version not above the latest, a wrong confirmation and a missing asset', async () => {
  await withWorld(async (w) => {
    const head = originMain(w.origin);
    const run = (options: Record<string, string | boolean>, confirm: (q: string) => Promise<string>, stage = w.stage()) => publish({ repo: w.repo, plan: w.plan, options, stage, confirm });

    await assert.rejects(run({ ...w.options, version: '0.5.3' }, async () => '0.5.3'), /not above the latest release 0.5.3/);
    await assert.rejects(run({ ...w.options, version: '0.4.9' }, async () => '0.4.9'), /not above the latest release/);
    await assert.rejects(run({ ...w.options, version: 'banana' }, async () => ''), /not a version/);

    await assert.rejects(run(w.options, async () => '0.5.4', w.stage(['yahad-windows-release-0.5.4.json.sig'])), /missing or empty: yahad-windows-release-0\.5\.4\.json\.sig/);
    await assert.rejects(run(w.options, async () => '0.5.4', w.stage(['yahad-windows-app-0.5.4.zip'])), /missing or empty: yahad-windows-app-0\.5\.4\.zip/);
    await assert.rejects(run(w.options, async () => '0.5.4', w.stage(['yachad-app.zip'])), /missing or empty: yachad-app\.zip/);
    await assert.rejects(run(w.options, async () => '0.5.4', w.stage(['yachad-updater.zip'])), /missing or empty: yachad-updater\.zip/);

    let asked = '';
    await assert.rejects(run(w.options, async (question) => { asked = question; return '0.5.5'; }), /Confirmation did not match/);
    assert.match(asked, /Type 0\.5\.4 to push/);

    // Nothing was pushed or published by any refusal.
    assert.equal(originMain(w.origin), head);
    assert.equal(git(w.origin, 'tag', '--list').trim(), '');
    assert.equal(w.calls().some((call) => call[0] === 'release'), false);
  });
});

test('release:publish --dry-run stops before push; a confirmed run pushes the commit and tag and creates one latest release with all assets', async () => {
  await withWorld(async (w) => {
    const head = originMain(w.origin);
    const logs: string[] = [];
    const dry = await publish({ repo: w.repo, plan: w.plan, options: { ...w.options, 'dry-run': true }, stage: w.stage(), confirm: async () => assert.fail('dry run must not ask'), log: (line: string) => logs.push(line) });
    assert.equal(dry.published, false);
    assert.match(logs.join('\n'), /Release 0\.5\.4 \(latest is 0\.5\.3\)/);
    assert.equal(originMain(w.origin), head);
    assert.equal(git(w.origin, 'tag', '--list').trim(), '');

    const done = await publish({ repo: w.repo, plan: w.plan, options: w.options, stage: w.stage(), confirm: async () => '0.5.4', log: () => {} });
    assert.equal(done.published, true);
    assert.equal(git(w.origin, 'tag', '--list').trim(), 'v0.5.4');
    assert.equal(git(w.origin, 'rev-list', '--count', `${head}..main`).trim(), '1');
    assert.equal(git(w.origin, 'log', '-1', '--format=%s', 'main').trim(), 'Release 0.5.4');
    assert.equal(JSON.parse(git(w.origin, 'show', 'main:package.json')).version, '0.5.4');
    assert.equal(JSON.parse(git(w.origin, 'show', 'main:package-lock.json')).packages[''].version, '0.5.4');
    assert.equal(git(w.origin, 'rev-parse', 'v0.5.4^{commit}').trim(), originMain(w.origin));
    const create = w.calls().filter((call) => call[0] === 'release' && call[1] === 'create');
    assert.equal(create.length, 1);
    const args = create[0];
    assert.equal(args[2], 'v0.5.4');
    assert.deepEqual(args.slice(3, args.indexOf('--repo')).map((path) => path.split(/[\\/]/).at(-1)).sort(),
      ['Yahad.Setup-0.5.4.exe', 'Yahad.Setup.exe', 'yachad-app.zip', 'yachad-release.json', 'yachad-updater.json', 'yachad-updater.zip', 'yahad-windows-app-0.5.4.zip', 'yahad-windows-release-0.5.4.json', 'yahad-windows-release-0.5.4.json.sig']);
    for (const flag of ['--latest', '--verify-tag']) assert.ok(args.includes(flag));
    for (const flag of ['--draft', '--prerelease']) assert.equal(args.includes(flag), false);
    assert.equal(args[args.indexOf('--repo') + 1], 'NehoraiHadad/yahad');
  });
});

test('release:publish keeps the assets and prints a working retry when gh fails after the push', async () => {
  await withWorld(async (w) => {
    process.env.FAKE_GH_FAIL = '1';
    try {
      await assert.rejects(publish({ repo: w.repo, plan: w.plan, options: w.options, stage: w.stage(), confirm: async () => '0.5.4', log: () => {} }), (error: Error) => {
        assert.match(error.message, /v0\.5\.4 was pushed but gh release create failed/);
        const kept = join(w.repo, '.local', 'release-0.5.4');
        assert.equal(readdirSync(kept).length, 9);
        assert.ok(error.message.includes(join(kept, 'yachad-app.zip')));
        assert.equal(error.message.includes('yahad-release-'), false, 'the retry names no temporary path');
        assert.match(error.message, /--repo NehoraiHadad\/yahad --verify-tag --latest/);
        return true;
      });
    } finally { delete process.env.FAKE_GH_FAIL; }
  });
});

test('collectReleaseAssets accepts exactly the nine assets and rejects every inconsistency', () => {
  const root = mkdtempSync(join(tmpdir(), 'yahad-assets-'));
  try {
    const keys = pair();
    const only = (name: string, flaw: Flaw | null, omit: string[] = [], key = keys.spki) => {
      writeAssets(join(root, name), '1.0.0', keys, omit, flaw ?? {});
      return () => collectReleaseAssets(join(root, name), '1.0.0', key);
    };
    assert.deepEqual(only('ok', null)().map((file) => file.name).sort(), [
      'Yahad.Setup-1.0.0.exe', 'Yahad.Setup.exe', 'yachad-app.zip', 'yachad-release.json', 'yachad-updater.json', 'yachad-updater.zip',
      'yahad-windows-app-1.0.0.zip', 'yahad-windows-release-1.0.0.json', 'yahad-windows-release-1.0.0.json.sig']);

    assert.throws(only('missing-app', null, ['yahad-windows-app-1.0.0.zip']), /missing or empty: yahad-windows-app-1\.0\.0\.zip/);
    assert.throws(only('missing-manifest', null, ['yahad-windows-release-1.0.0.json']), /missing or empty: yahad-windows-release-1\.0\.0\.json/);
    assert.throws(only('missing-stable', null, ['Yahad.Setup.exe']), /missing or empty: Yahad\.Setup\.exe/);
    const extra = only('extra', null);
    writeFileSync(join(root, 'extra', 'yachad-updater.sig'), 'x');
    assert.throws(extra, /Unexpected release assets: yachad-updater\.sig/);
    // The previous manifest family is an extra asset now.
    const previous = only('previous', null);
    writeFileSync(join(root, 'previous', 'yahad-windows-1.0.0.json'), '{}');
    assert.throws(previous, /Unexpected release assets: yahad-windows-1\.0\.0\.json/);

    const stable = only('other-setup', null);
    writeFileSync(join(root, 'other-setup', 'Yahad.Setup.exe'), 'other bytes');
    assert.throws(stable, /Yahad\.Setup\.exe is not the same file/);

    // Signature: another key's, the committed key not being the signer, and a tampered manifest.
    assert.throws(only('foreign-key', null, [], pair().spki), /does not verify against the committed key/);
    assert.throws(only('signed-by-other', { signWith: pair() }), /does not verify against the committed key/);
    const tampered = only('tampered-manifest', null);
    writeFileSync(join(root, 'tampered-manifest', 'yahad-windows-release-1.0.0.json'), JSON.stringify({ format: 2 }));
    assert.throws(tampered, /does not verify/);
    // Signed correctly, but not a format 2 release or not for this version.
    assert.throws(only('v1', { manifest: (m) => { m.format = 1; } }), /Only format 2 of yahad-windows-release/);
    assert.throws(only('product', { manifest: (m) => { m.product = 'yahad-windows'; } }), /Only format 2 of yahad-windows-release/);
    assert.throws(only('version', { manifest: (m) => { m.version = '1.0.1'; } }), /is for 1\.0\.1, not 1\.0\.0/);

    // The Setup bytes the manifest names.
    assert.throws(only('setup-name', { manifest: (m) => { m.infrastructure.setup.name = 'Yahad.Setup-9.exe'; } }), /does not describe Yahad\.Setup-1\.0\.0\.exe/);
    assert.throws(only('setup-bytes', { manifest: (m) => { m.infrastructure.setup.bytes += 1; } }), /does not describe Yahad\.Setup-1\.0\.0\.exe/);
    assert.throws(only('setup-hash', { manifest: (m) => { m.infrastructure.setup.sha256 = '0'.repeat(64); } }), /does not describe Yahad\.Setup-1\.0\.0\.exe/);

    // The app archive: size, hash, and the app-tree.json inside it.
    assert.throws(only('zip-size', { manifest: (m) => { m.app.archive.bytes += 1; } }), /does not describe yahad-windows-app-1\.0\.0\.zip/);
    assert.throws(only('zip-hash', { manifest: (m) => { m.app.archive.sha256 = '0'.repeat(64); } }), /does not describe yahad-windows-app-1\.0\.0\.zip/);
    assert.throws(only('app-tree', { manifest: (m) => { m.app.treeSha256 = '0'.repeat(64); } }), /app-tree\.json inside .* does not hash to the manifest's app\.treeSha256/);
    assert.throws(only('app-tree-bytes', { appEntries: (e) => { e['app-tree.json'] = Buffer.from(e['app-tree.json'].toString() + ' '); } }), /app-tree\.json inside .* does not hash/);
    assert.throws(only('app-stray', { appEntries: (e) => { e['stray.txt'] = Buffer.from('x'); }, manifest: () => {} }), /does not hash|holds members outside/);
    // A member the tree does not list: the tree hash is kept consistent so only the member set differs.
    const unlisted = windowsFiles('1.0.0', keys, { appEntries: (e) => { e['app/extra.txt'] = Buffer.from('x'); } });
    mkdirSync(join(root, 'unlisted'));
    writeAssets(join(root, 'unlisted'), '1.0.0', keys, Object.keys(unlisted), {});
    for (const [name, bytes] of Object.entries(unlisted)) writeFileSync(join(root, 'unlisted', name), bytes);
    assert.throws(() => collectReleaseAssets(join(root, 'unlisted'), '1.0.0', keys.spki), /does not match its app-tree\.json/);

    // The infrastructure tree embedded in the Setup.
    assert.throws(only('infra-tree', { manifest: (m) => { m.infrastructure.treeSha256 = '0'.repeat(64); } }), /infra-tree\.json embedded in Yahad\.Setup-1\.0\.0\.exe does not hash/);
    assert.throws(only('infra-other', { infraTree: Buffer.from('{"other":true}'), manifest: () => {} }), /does not hash/);
    assert.throws(only('infra-missing', { setup: Buffer.from('MZ a Setup with no embedded package') }), /embeds 0 members named infra\/infra-tree\.json/);

    // The Linux assets keep their own checks.
    const updater = only('updater-tampered', null);
    writeFileSync(join(root, 'updater-tampered', 'yachad-updater.zip'), 'other bytes');
    assert.throws(updater, /yachad-updater\.json does not describe/);
    // A pair at the runtime an earlier release carried would be refused as a downgrade on enrolled machines.
    const stale = only('stale', null);
    const runtime = JSON.parse(readFileSync(join(root, 'stale', 'yachad-updater.json'), 'utf8'));
    writeFileSync(join(root, 'stale', 'yachad-updater.json'), JSON.stringify({ ...runtime, version: '1.0.1' }));
    assert.throws(stale, /at runtime 2\.0\.0/);
  } finally { rmSync(root, { recursive: true, force: true }); }
});

// ---- the infrastructure version rule and the reused-infrastructure worktree ----
test('the infrastructure bump rule refuses a missing bump and a bump without a change, and reuses on equal inputs', () => {
  const same = 'a'.repeat(64); const other = 'b'.repeat(64);
  const baseline = { version: '1.0.0', sourceCommit: 'c'.repeat(40) };
  const plan = (version: string, headDigest: string, baselineDigest: string | null) => planInfrastructure({
    head: { version, digest: headDigest }, baseline: baselineDigest === null ? null : baseline, digestAt: (commit: string) => { assert.equal(commit, baseline.sourceCommit); return baselineDigest as string; }
  });
  assert.throws(() => plan('1.0.0', other, same), /inputs changed since 1\.0\.0 .* is still 1\.0\.0\. Bump it/);
  assert.throws(() => plan('1.1.0', same, same), /no infrastructure input changed since 1\.0\.0/);
  assert.throws(() => plan('0.9.0', other, same), /below the latest release's 1\.0\.0/);
  assert.deepEqual(plan('1.1.0', other, same), { mode: 'build', digest: other });
  assert.deepEqual(plan('1.0.0', same, same), { mode: 'reuse', digest: same, sourceCommit: baseline.sourceCommit });
  // No earlier Windows release: nothing to compare with.
  assert.deepEqual(plan('1.0.0', same, null), { mode: 'first', digest: same });
});

test('a reused infrastructure is packaged from a git worktree at its commit that is removed afterwards', async () => {
  const root = mkdtempSync(join(tmpdir(), 'yahad-infra-source-'));
  try {
    const repo = join(root, 'repo');
    mkdirSync(repo);
    git(repo, 'init', '-q');
    writeFileSync(join(repo, '.gitignore'), '.local/\n');
    writeFileSync(join(repo, 'marker.txt'), 'old\n');
    git(repo, 'add', '-A'); git(repo, 'commit', '-q', '-m', 'old');
    const old = git(repo, 'rev-parse', 'HEAD').trim();
    writeFileSync(join(repo, 'marker.txt'), 'new\n');
    git(repo, 'commit', '-q', '-am', 'new');

    let seen: any = null;
    await withInfrastructureSource(repo, { mode: 'reuse' as const, digest: 'd', sourceCommit: old }, async (infra: any) => {
      seen = infra;
      assert.equal(readFileSync(join(infra.source, 'marker.txt'), 'utf8'), 'old\n');
      assert.equal(git(infra.source, 'rev-parse', 'HEAD').trim(), old);
      assert.equal(git(repo, 'status', '--porcelain').trim(), '', 'the worktree is git-ignored');
    });
    assert.equal(seen.sourceCommit, old);
    assert.equal(existsSync(seen.source), false);
    assert.equal(git(repo, 'worktree', 'list').trim().split('\n').length, 1);
    // Cleaned up when the work throws, too.
    await assert.rejects(withInfrastructureSource(repo, { mode: 'reuse' as const, digest: 'd', sourceCommit: old }, async (infra: any) => { seen = infra; throw new Error('boom'); }), /boom/);
    assert.equal(existsSync(seen.source), false);
    assert.equal(git(repo, 'worktree', 'list').trim().split('\n').length, 1);
    // A fresh build uses the checkout itself: no source, no worktree.
    assert.deepEqual(await withInfrastructureSource(repo, { mode: 'build' as const, digest: 'd' }, async (infra: any) => infra), { digest: 'd' });
    await assert.rejects(withInfrastructureSource(repo, { mode: 'reuse' as const, digest: 'd', sourceCommit: 'f'.repeat(40) }, async () => {}), /is not in this checkout/);
    writeFileSync(join(repo, '.gitignore'), '');
    git(repo, 'commit', '-q', '-am', 'unignore');
    await assert.rejects(withInfrastructureSource(repo, { mode: 'reuse' as const, digest: 'd', sourceCommit: old }, async () => {}), /not git-ignored/);
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test('every release carries a runtime above the bootstrap and above every earlier release', () => {
  const key = (version: string) => version.split('.').map(Number).reduce((total, part) => total * 100000 + part, 0);
  const releases = ['0.6.0', '0.6.1', '0.6.99', '0.7.0', '1.0.0', '1.0.1', '2.0.0'];
  const runtimes = releases.map(runtimeVersionFor);
  assert.deepEqual(runtimes.slice(0, 2), ['1.6.0', '1.6.1']);
  for (let i = 1; i < runtimes.length; i++) assert.ok(key(runtimes[i]) > key(runtimes[i - 1]), `${runtimes[i]} above ${runtimes[i - 1]}`);

  const bootstrap = readFileSync(join(import.meta.dirname, '..', 'deploy', 'upgrade-worker-github.py'), 'utf8');
  for (const runtime of runtimes) requireRuntimeAboveBootstrap(runtime, bootstrap);
  assert.throws(() => requireRuntimeAboveBootstrap('1.0.1', bootstrap), /not above the bootstrap's initial runtime 1\.0\.1/);
  assert.throws(() => requireRuntimeAboveBootstrap('1.0.0', bootstrap), /not above/);
  assert.throws(() => requireRuntimeAboveBootstrap('1.6.0', 'no such line'), /names no INITIAL_RUNTIME_VERSION/);
});

test('the real updater packager produces a pair that the release check accepts', () => {
  const root = mkdtempSync(join(tmpdir(), 'yahad-runtime-pair-'));
  try {
    const keys = pair();
    const assets = join(root, 'assets');
    writeAssets(assets, '0.6.0', keys);
    const repository = join(import.meta.dirname, '..');
    execFileSync(process.execPath, [join(repository, 'scripts', 'release-updater.mjs'), 'package-github', runtimeVersionFor('0.6.0'), join(root, 'pair')], { cwd: repository, stdio: 'pipe' });
    assert.deepEqual(readdirSync(join(root, 'pair')).sort(), ['yachad-updater.json', 'yachad-updater.zip']);
    for (const name of readdirSync(join(root, 'pair'))) copyFileSync(join(root, 'pair', name), join(assets, name));
    const names = collectReleaseAssets(assets, '0.6.0', keys.spki).map((file) => file.name);
    assert.ok(names.includes('yachad-updater.zip'));
    assert.equal(JSON.parse(readFileSync(join(assets, 'yachad-updater.json'), 'utf8')).version, '1.6.0');
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test('windows:install refuses a dirty tree, builds in a clone that is removed, copies the Setup out and launches it', { skip: process.platform !== 'win32' }, async () => {
  await withWorld(async (w) => {
    writeFileSync(join(w.repo, 'stray.txt'), 'x');
    await assert.rejects(install({ repo: w.repo, options: w.options, build: async () => assert.fail('no build'), launch: () => assert.fail('no launch') }), /working tree is dirty/);
    rmSync(join(w.repo, 'stray.txt'));

    const launched: string[] = [];
    let clonePath = '';
    const result = await install({
      repo: w.repo, options: w.options,
      build: async ({ clone, root }: { clone: string; root: string }) => {
        clonePath = clone;
        assert.equal(git(clone, 'config', 'core.autocrlf').trim(), 'false');
        assert.ok(existsSync(join(clone, 'installer', 'release-key.spki')));
        const setupFile = join(root, 'Yahad.Setup-0.5.3.exe');
        writeFileSync(setupFile, 'setup');
        return { version: '0.5.3', commit: git(clone, 'rev-parse', 'HEAD').trim(), setupFile };
      },
      launch: (file: string) => launched.push(file)
    });
    assert.deepEqual(launched, [result.setupFile]);
    assert.equal(readFileSync(result.setupFile, 'utf8'), 'setup');
    assert.equal(existsSync(clonePath), false);
    assert.equal(w.calls().length, 0); // install never talks to GitHub
    assert.equal(git(w.repo, 'status', '--porcelain').trim(), '');
  });
});
