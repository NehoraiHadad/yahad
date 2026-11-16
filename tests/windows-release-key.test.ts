import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { createHash, createPublicKey, verify } from 'node:crypto';
import { existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';
import { verifyRelease } from '../scripts/windows-release-key.mjs';

const script = join(import.meta.dirname, '..', 'scripts', 'windows-release-key.mjs');
const run = (...args: string[]) => execFileSync(process.execPath, [script, ...args], { stdio: 'pipe' });
const keyIdOf = (spki: Buffer) => createHash('sha256').update(spki).digest('hex').slice(0, 16);
const releaseManifest = (keyId: string, extra: Record<string, unknown> = {}) =>
  Buffer.from(JSON.stringify({ format: 2, product: 'yahad-windows-release', version: '1.0.0', keyId, ...extra }));

test('windows release key tool signs the v2-prefixed raw manifest with a P-256 P1363 signature', () => {
  const root = mkdtempSync(join(tmpdir(), 'yahad-release-key-'));
  try {
    const dir = join(root, 'key');
    run('keygen', dir);
    const manifest = join(root, 'manifest.json');
    const spki = readFileSync(join(dir, 'key.spki'));
    const keyId = keyIdOf(spki);
    const bytes = releaseManifest(keyId);
    writeFileSync(manifest, bytes);
    run('sign', join(dir, 'key.pem'), manifest);
    const signature = readFileSync(`${manifest}.sig`);
    assert.equal(signature.length, 64);
    assert.equal(spki.length, 91);
    const key = createPublicKey({ key: spki, format: 'der', type: 'spki' });
    assert.equal(verify('sha256', Buffer.concat([Buffer.from('yahad-release-v2\n'), bytes]), { key, dsaEncoding: 'ieee-p1363' }, signature), true);
    // Neither the old prefix nor no prefix verifies.
    assert.equal(verify('sha256', Buffer.concat([Buffer.from('yahad-release-v1\n'), bytes]), { key, dsaEncoding: 'ieee-p1363' }, signature), false);
    assert.equal(verify('sha256', bytes, { key, dsaEncoding: 'ieee-p1363' }, signature), false);
    // The helper the release command uses agrees, and returns the manifest it accepted.
    assert.equal(verifyRelease(spki, bytes, signature)?.keyId, keyId);
    assert.equal(verifyRelease(spki, releaseManifest(keyId, { version: '1.0.1' }), signature), null);
    assert.equal(verifyRelease(spki, bytes, signature.subarray(0, 63)), null);
    // An existing private key is never overwritten.
    assert.notEqual(spawnSync(process.execPath, [script, 'keygen', dir]).status, 0);
    assert.notEqual(spawnSync(process.execPath, [script, 'unknown']).status, 0);
    // Nor is one created inside a checkout.
    assert.notEqual(spawnSync(process.execPath, [script, 'keygen', join(import.meta.dirname, 'never-a-key')]).status, 0);
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test('windows release key tool refuses to sign anything but format 2 of yahad-windows-release naming its key', () => {
  const root = mkdtempSync(join(tmpdir(), 'yahad-release-key-refuse-'));
  try {
    const dir = join(root, 'key');
    run('keygen', dir);
    const keyId = keyIdOf(readFileSync(join(dir, 'key.spki')));
    const manifest = join(root, 'manifest.json');
    const refused = (bytes: Buffer, pattern: RegExp) => {
      writeFileSync(manifest, bytes);
      const result = spawnSync(process.execPath, [script, 'sign', join(dir, 'key.pem'), manifest], { encoding: 'utf8' });
      assert.notEqual(result.status, 0);
      assert.match(result.stderr, pattern);
      assert.equal(existsSync(`${manifest}.sig`), false, 'nothing is signed');
    };
    // The previous format, whose signature a v2 verifier would never accept anyway.
    refused(Buffer.from(JSON.stringify({ format: 1, product: 'yahad-windows', version: '1.0.0', keyId })), /Only format 2 of yahad-windows-release is signed/);
    refused(Buffer.from(JSON.stringify({ format: 2, product: 'yahad-windows', version: '1.0.0', keyId })), /Only format 2 of yahad-windows-release is signed/);
    refused(Buffer.from(JSON.stringify({ product: 'yahad-windows-release', keyId })), /Only format 2/);
    refused(Buffer.from('not json'), /not JSON/);
    refused(releaseManifest('0000000000000000'), /does not name this key/);
    refused(Buffer.from('null'), /Only format 2/);
  } finally { rmSync(root, { recursive: true, force: true }); }
});
