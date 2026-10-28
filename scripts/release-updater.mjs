// Offline packaging only. The explicit signing key authorizes root worker code,
// independently of app-release GitHub HTTPS or app-release signatures.
import { createHash, createPrivateKey, sign } from 'node:crypto';
import { existsSync, lstatSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { resolve, join } from 'node:path';
import { execFileSync } from 'node:child_process';

const [command, version, output, keyFile, ...extra] = process.argv.slice(2);
if (command !== 'package' || !/^\d{1,5}\.\d{1,5}\.\d{1,5}$/.test(version || '') || !output || !keyFile || extra.length) {
  throw new Error('Use: node scripts/release-updater.mjs package VERSION OUT_DIR PRIVATE_UPDATER_KEY');
}
const keyInfo = lstatSync(keyFile);
if (!keyInfo.isFile() || keyInfo.isSymbolicLink() || keyInfo.mode & 0o077 || keyInfo.size > 8192) {
  throw new Error('Use a regular private Ed25519 updater key (0600), never an application trust credential');
}
const key = createPrivateKey(readFileSync(keyFile));
if (key.asymmetricKeyType !== 'ed25519') throw new Error('Updater signing requires Ed25519');
const source = resolve('deploy/updater.py');
const info = lstatSync(source);
const worker = readFileSync(source);
if (!info.isFile() || info.isSymbolicLink() || worker.length > 1024 * 1024 || worker.includes(13)) {
  throw new Error('Use the reviewed regular canonical-LF worker source, at most 1 MiB');
}
const dir = resolve(output);
if (existsSync(dir)) throw new Error('Use a new empty updater output directory');
mkdirSync(dir, { recursive: true, mode: 0o700 });
const archive = join(dir, 'yachad-updater.zip');
execFileSync('python3', ['-c', `
import pathlib,stat,sys,zipfile
entry=zipfile.ZipInfo('updater.py')
entry.create_system=3
entry.external_attr=(stat.S_IFREG|0o644)<<16
entry.compress_type=zipfile.ZIP_DEFLATED
with zipfile.ZipFile(sys.argv[2],'x') as out:
 out.writestr(entry,pathlib.Path(sys.argv[1]).read_bytes())
`, source, archive], { stdio: ['ignore', 'ignore', 'ignore'] });
const bytes = readFileSync(archive);
if (bytes.length > 2 * 1024 * 1024) throw new Error('Updater archive exceeds 2 MiB');
const manifest = Buffer.from(JSON.stringify({
  format: 1, kind: 'yachad-updater', version, archive: 'yachad-updater.zip', bytes: bytes.length,
  sha256: createHash('sha256').update(bytes).digest('hex'), supervisorProtocol: 1,
  workerProtocol: 2, helperProtocol: 1, configFormat: 1, stateFormat: 1, appManifestFormats: [1, 2],
}) + '\n');
writeFileSync(join(dir, 'yachad-updater.json'), manifest, { mode: 0o600, flag: 'wx' });
writeFileSync(join(dir, 'yachad-updater.sig'), sign(null, manifest, key), { mode: 0o600, flag: 'wx' });
console.log(`Updater ${version} packaged locally. No key was provisioned, runtime activated, release published, or host changed.`);
