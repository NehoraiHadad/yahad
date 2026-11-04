import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { createHash, createPublicKey, verify } from 'node:crypto';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';

const script = join(import.meta.dirname, '..', 'scripts', 'windows-release-key.mjs');
const run = (...args: string[]) => execFileSync(process.execPath, [script, ...args], { stdio: 'pipe' });

test('windows release key tool signs the prefixed raw manifest with a P-256 P1363 signature', () => {
  const root = mkdtempSync(join(tmpdir(), 'yahad-release-key-'));
  try {
    const dir = join(root, 'key');
    run('keygen', dir);
    const manifest = join(root, 'manifest.json');
    const spki = readFileSync(join(dir, 'key.spki'));
    // The tool refuses a manifest that names another key.
    writeFileSync(manifest, JSON.stringify({ keyId: '0000000000000000' }));
    assert.notEqual(spawnSync(process.execPath, [script, 'sign', join(dir, 'key.pem'), manifest]).status, 0);
    const bytes = Buffer.from(JSON.stringify({ keyId: createHash('sha256').update(spki).digest('hex').slice(0, 16) }));
    writeFileSync(manifest, bytes);
    run('sign', join(dir, 'key.pem'), manifest);
    const signature = readFileSync(`${manifest}.sig`);
    assert.equal(signature.length, 64);
    assert.equal(spki.length, 91);
    const key = createPublicKey({ key: spki, format: 'der', type: 'spki' });
    const message = Buffer.concat([Buffer.from('yahad-release-v1\n'), bytes]);
    assert.equal(verify('sha256', message, { key, dsaEncoding: 'ieee-p1363' }, signature), true);
    assert.equal(verify('sha256', bytes, { key, dsaEncoding: 'ieee-p1363' }, signature), false);
    // An existing private key is never overwritten.
    assert.notEqual(spawnSync(process.execPath, [script, 'keygen', dir]).status, 0);
    assert.notEqual(spawnSync(process.execPath, [script, 'unknown']).status, 0);
    // Nor is one created inside a checkout.
    assert.notEqual(spawnSync(process.execPath, [script, 'keygen', join(import.meta.dirname, 'never-a-key')]).status, 0);
  } finally { rmSync(root, { recursive: true, force: true }); }
});
