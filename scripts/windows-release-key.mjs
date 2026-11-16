#!/usr/bin/env node
// Maintainer tool for Windows release signatures. Generates a key pair and signs
// a release manifest; it never publishes and holds no key of its own. The private
// key stays with whoever ran keygen; see docs/GITHUB-RUNTIME-UPDATES.md.
//   keygen <dir>              writes <dir>/key.pem (PKCS#8) and <dir>/key.spki (DER)
//   sign <key.pem> <manifest> writes <manifest>.sig (64-byte IEEE P1363 r||s)
import { createHash, createPrivateKey, createPublicKey, generateKeyPairSync, sign, verify } from 'node:crypto';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

// ReleaseSignature.Prefix (installer/Yahad.Install.Core/ReleaseManifest.cs). A signature over any other
// manifest family never verifies as a release.
export const PREFIX = Buffer.from('yahad-release-v2\n', 'ascii');
export const PRODUCT = 'yahad-windows-release';
export const keyIdOf = (spki) => createHash('sha256').update(spki).digest('hex').slice(0, 16);

// Only format 2 of yahad-windows-release naming this key is signed, or accepted as signed.
export function requireReleaseManifest(bytes, expectedKeyId) {
  let manifest;
  try { manifest = JSON.parse(bytes.toString('utf8')); } catch { throw new Error('The manifest is not JSON'); }
  if (manifest === null || typeof manifest !== 'object' || manifest.format !== 2 || manifest.product !== PRODUCT) {
    throw new Error(`Only format 2 of ${PRODUCT} is signed`);
  }
  // A manifest that names another key would be refused on every machine; say so here.
  if (manifest.keyId !== expectedKeyId) throw new Error(`Manifest does not name this key (${expectedKeyId})`);
  return manifest;
}

// The Updater's check: a 64-byte P1363 signature over PREFIX + manifest bytes, then the shape above.
// Returns the parsed manifest, or null when the signature does not verify.
export function verifyRelease(spki, manifestBytes, signature) {
  const trusted = createPublicKey({ key: spki, format: 'der', type: 'spki' });
  if (signature.length !== 64 || !verify('sha256', Buffer.concat([PREFIX, manifestBytes]), { key: trusted, dsaEncoding: 'ieee-p1363' }, signature)) return null;
  return requireReleaseManifest(manifestBytes, keyIdOf(spki));
}

// A private key has no business anywhere near a checkout it could be committed from.
function requireOutsideRepository(directory) {
  for (let at = resolve(directory); ; at = dirname(at)) {
    if (existsSync(join(at, '.git'))) throw new Error(`Refusing to create a key inside the repository ${at}`);
    if (dirname(at) === at) return;
  }
}

function main([command, ...args]) {
  if (command === 'keygen' && args.length === 1) {
    requireOutsideRepository(args[0]);
    const { publicKey, privateKey } = generateKeyPairSync('ec', { namedCurve: 'prime256v1' });
    mkdirSync(args[0], { recursive: true, mode: 0o700 });
    // flag wx: an existing private key is never overwritten.
    writeFileSync(join(args[0], 'key.pem'), privateKey.export({ type: 'pkcs8', format: 'pem' }), { flag: 'wx', mode: 0o600 });
    writeFileSync(join(args[0], 'key.spki'), publicKey.export({ type: 'spki', format: 'der' }), { flag: 'wx' });
  } else if (command === 'sign' && args.length === 2) {
    const key = createPrivateKey(readFileSync(args[0]));
    if (key.asymmetricKeyType !== 'ec' || key.asymmetricKeyDetails?.namedCurve !== 'prime256v1') throw new Error('P-256 key required');
    const bytes = readFileSync(args[1]);
    requireReleaseManifest(bytes, keyIdOf(createPublicKey(key).export({ type: 'spki', format: 'der' })));
    const signature = sign('sha256', Buffer.concat([PREFIX, bytes]), { key, dsaEncoding: 'ieee-p1363' });
    if (signature.length !== 64) throw new Error('Unexpected signature length');
    writeFileSync(`${args[1]}.sig`, signature);
  } else {
    console.error('Usage: windows-release-key.mjs keygen <dir> | sign <key.pem> <manifest>');
    process.exit(2);
  }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) main(process.argv.slice(2));
