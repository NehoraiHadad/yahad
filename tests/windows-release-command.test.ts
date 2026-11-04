import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { createHash, generateKeyPairSync } from 'node:crypto';
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';
import { requireBuiltKey } from '../scripts/release-windows.mjs';

const source = join(import.meta.dirname, '..', 'scripts', 'release-windows.mjs');
const git = (cwd: string, ...args: string[]) => execFileSync('git', ['-c', 'user.name=t', '-c', 'user.email=t@invalid', ...args], { cwd, stdio: 'pipe' });
const pair = () => {
  const { publicKey, privateKey } = generateKeyPairSync('ec', { namedCurve: 'prime256v1' });
  return { spki: publicKey.export({ type: 'spki', format: 'der' }), pem: privateKey.export({ type: 'pkcs8', format: 'pem' }) };
};
const keyId = (spki: Buffer) => createHash('sha256').update(spki).digest('hex').slice(0, 16);

// The command locates its checkout from its own path, so each case runs a copy of it
// inside a throwaway repository. Every refusal comes before any build starts.
function fixture(root: string, committed: Buffer | null) {
  const repo = join(root, 'repo');
  mkdirSync(join(repo, 'scripts'), { recursive: true });
  mkdirSync(join(repo, 'installer'));
  copyFileSync(source, join(repo, 'scripts', 'release-windows.mjs'));
  writeFileSync(join(repo, 'package.json'), '{"version":"1.2.3"}');
  git(repo, 'init', '-q');
  if (committed) writeFileSync(join(repo, 'installer', 'release-key.spki'), committed);
  git(repo, 'add', '-A');
  git(repo, 'commit', '-q', '-m', 'fixture');
  return repo;
}
const release = (repo: string, ...args: string[]) => spawnSync(process.execPath, [join(repo, 'scripts', 'release-windows.mjs'), ...args], { cwd: repo, encoding: 'utf8' });

test('windows release command refuses before building and publishes nothing', () => {
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
      writeFileSync(join(dir, 'release', 'yahad-windows-1.2.3.json'), JSON.stringify({ keyId: manifestKey }));
      return dir;
    };
    const good = keyId(committed);
    assert.equal(requireBuiltKey(build(good, good), committed).manifestName, 'yahad-windows-1.2.3.json');
    assert.throws(() => requireBuiltKey(build('0123456789abcdef', good), committed), /trusts key 0123456789abcdef/);
    assert.throws(() => requireBuiltKey(build(null, good), committed), /trusts key none/);
    assert.throws(() => requireBuiltKey(build(good, '0123456789abcdef'), committed), /manifest names key/);
  } finally { rmSync(root, { recursive: true, force: true }); }
});
