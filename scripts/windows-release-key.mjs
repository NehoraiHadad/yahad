#!/usr/bin/env node
// Maintainer tool for Windows release signatures. Generates a key pair and signs
// a release manifest; it never publishes and holds no key of its own. The private
// key stays with whoever ran keygen; see docs/GITHUB-RUNTIME-UPDATES.md.
//   keygen <dir>              writes <dir>/key.pem (PKCS#8) and <dir>/key.spki (DER)
//   sign <key.pem> <manifest> writes <manifest>.sig (64-byte IEEE P1363 r||s)
import { createHash, createPrivateKey, createPublicKey, generateKeyPairSync, sign } from 'node:crypto';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';

const prefix = Buffer.from('yahad-release-v1\n', 'ascii');
const [command, ...args] = process.argv.slice(2);

// A private key has no business anywhere near a checkout it could be committed from.
function requireOutsideRepository(directory) {
  for (let at = resolve(directory); ; at = dirname(at)) {
    if (existsSync(join(at, '.git'))) throw new Error(`Refusing to create a key inside the repository ${at}`);
    if (dirname(at) === at) return;
  }
}

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
  // A manifest that names another key would be refused on every machine; say so here.
  const keyId = createHash('sha256').update(createPublicKey(key).export({ type: 'spki', format: 'der' })).digest('hex').slice(0, 16);
  if (JSON.parse(bytes.toString('utf8')).keyId !== keyId) throw new Error(`Manifest does not name this key (${keyId})`);
  const signature = sign('sha256', Buffer.concat([prefix, bytes]), { key, dsaEncoding: 'ieee-p1363' });
  if (signature.length !== 64) throw new Error('Unexpected signature length');
  writeFileSync(`${args[1]}.sig`, signature);
} else {
  console.error('Usage: windows-release-key.mjs keygen <dir> | sign <key.pem> <manifest>');
  process.exit(2);
}
