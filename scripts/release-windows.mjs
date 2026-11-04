#!/usr/bin/env node
// Maintainer command: builds the Windows release from this clean checkout and signs
// it with the key on this PC. It writes three files into a new local directory and
// prints the `gh release create` command to run by hand; it never publishes.
//
//   node scripts/release-windows.mjs <key.pem> --out <new-dir> [--node-dir D] [--dotnet EXE]
//        [--npm-cache D] [--vendors D] [--package-cache D] [--python EXE] [--pwsh EXE]
//
// The build is installer/package.ps1 (the same one CI runs) and the signature is
// scripts/windows-release-key.mjs sign (the same one CI runs). See installer/README.md.
import { createHash, createPrivateKey, createPublicKey, verify } from 'node:crypto';
import { copyFileSync, existsSync, mkdirSync, readFileSync, readdirSync } from 'node:fs';
import { dirname, isAbsolute, join, relative, resolve } from 'node:path';
import { execFileSync, spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const REPOSITORY = 'NehoraiHadad/yahad'; // fixed in installer/Yahad.Updater/GitHubReleaseSource.cs
const COMMITTED_KEY = 'installer/release-key.spki';
const prefix = Buffer.from('yahad-release-v1\n', 'ascii');
const keyIdOf = (spki) => createHash('sha256').update(spki).digest('hex').slice(0, 16);

class Refusal extends Error {}
const refuse = (message) => { throw new Refusal(message); };

function insideCheckout(path) {
  for (let at = resolve(path); ; at = dirname(at)) {
    if (existsSync(join(at, '.git'))) return at;
    if (dirname(at) === at) return null;
  }
}

const git = (repo, ...args) => execFileSync('git', args, { cwd: repo, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });

// The public key the Updater will trust is the one committed at HEAD, not whatever is on disk.
export function committedKey(repo) {
  try { git(repo, 'cat-file', '-e', `HEAD:${COMMITTED_KEY}`); } catch {
    refuse(`${COMMITTED_KEY} is not committed. Create the key once and commit its public half first (installer/README.md, "Release signing").`);
  }
  const spki = execFileSync('git', ['show', `HEAD:${COMMITTED_KEY}`], { cwd: repo, stdio: ['ignore', 'pipe', 'pipe'] });
  if (spki.length !== 91) refuse(`${COMMITTED_KEY} is not a DER P-256 public key (expected 91 bytes, found ${spki.length}).`);
  return spki;
}

export function requireKeyMatches(keyFile, spki) {
  let derived;
  try { derived = createPublicKey(createPrivateKey(readFileSync(keyFile))).export({ type: 'spki', format: 'der' }); } catch (error) {
    refuse(`Cannot read ${keyFile} as a private key: ${error.message}`);
  }
  if (!derived.equals(spki)) refuse(`The public half of ${keyFile} (key id ${keyIdOf(derived)}) is not the committed ${COMMITTED_KEY} (key id ${keyIdOf(spki)}).`);
}

// What the finished package says it trusts, read from files it wrote, not from what was asked for.
export function requireBuiltKey(packageDirectory, spki) {
  const expected = keyIdOf(spki);
  const verification = JSON.parse(readFileSync(join(packageDirectory, 'verify-package.log'), 'utf8'));
  if (verification.releaseKeyId !== expected) refuse(`The built Setup trusts key ${verification.releaseKeyId ?? 'none'}, not the committed key ${expected}.`);
  const manifests = readdirSync(join(packageDirectory, 'release')).filter((name) => /^yahad-windows-.+\.json$/.test(name));
  if (manifests.length !== 1) refuse(`Expected one release manifest in ${packageDirectory}/release, found ${manifests.length}.`);
  const manifest = JSON.parse(readFileSync(join(packageDirectory, 'release', manifests[0]), 'utf8'));
  if (manifest.keyId !== expected) refuse(`The release manifest names key ${manifest.keyId}, not the committed key ${expected}.`);
  return { manifestName: manifests[0], manifest };
}

function parse(argv) {
  const options = {}; const positional = [];
  const named = new Set(['out', 'node-dir', 'dotnet', 'npm-cache', 'vendors', 'package-cache', 'python', 'pwsh']);
  for (let i = 0; i < argv.length; i++) {
    if (argv[i].startsWith('--')) {
      const name = argv[i].slice(2);
      if (!named.has(name) || i + 1 >= argv.length) refuse(`Unknown or incomplete option ${argv[i]}`);
      options[name] = argv[++i];
    } else positional.push(argv[i]);
  }
  if (positional.length !== 1 || !options.out) refuse('Usage: node scripts/release-windows.mjs <key.pem> --out <new-dir> [--node-dir D] [--dotnet EXE] [--npm-cache D] [--vendors D] [--package-cache D] [--python EXE] [--pwsh EXE]');
  return { keyFile: positional[0], options };
}

function main(repo, argv) {
  const { keyFile: keyArgument, options } = parse(argv);
  const keyFile = resolve(keyArgument);
  if (!existsSync(keyFile)) refuse(`Key file not found: ${keyFile}`);
  const checkout = insideCheckout(keyFile);
  if (checkout) refuse(`Refusing a signing key inside the checkout ${checkout}. Keep it outside any repository.`);
  const spki = committedKey(repo);
  requireKeyMatches(keyFile, spki);
  if (git(repo, 'status', '--porcelain').trim()) refuse('The working tree is dirty. Commit or discard every change; a release is built from HEAD only.');
  const commit = git(repo, 'rev-parse', 'HEAD').trim();

  const local = (...parts) => join(repo, '.local', ...parts);
  const tools = existsSync(local('tools')) ? readdirSync(local('tools')) : [];
  const node24 = tools.filter((name) => /^node-v24\..*-win-x64$/.test(name)).sort().at(-1);
  const nodeDirectory = options['node-dir'] ?? (node24 && local('tools', node24));
  const pwsh = options.pwsh ?? (existsSync(local('tools', 'pwsh', 'pwsh.exe')) ? local('tools', 'pwsh', 'pwsh.exe') : 'pwsh');
  const inputs = {
    NodeDirectory: nodeDirectory,
    NpmCache: options['npm-cache'] ?? local('npm-cache'),
    VendorDirectory: options.vendors ?? local('vendors'),
    DotNet: options.dotnet ?? local('tools', 'dotnet', 'dotnet.exe'),
    PackageCache: options['package-cache'] ?? local('nuget-cache'),
    Python: options.python ?? 'python'
  };
  if (!inputs.NodeDirectory) refuse('No Node 24 directory found under .local/tools; pass --node-dir.');

  const out = resolve(options.out);
  if (existsSync(out)) refuse(`Output directory ${out} already exists; choose a new one.`);
  const inside = relative(repo, out);
  if (!inside.startsWith('..') && !isAbsolute(inside) && spawnSync('git', ['check-ignore', '-q', out], { cwd: repo }).status !== 0) {
    refuse(`Output ${out} is inside the checkout and not git-ignored; it would dirty the tree. Use a directory outside the repository or under .local/.`);
  }
  if (existsSync(join(repo, 'installer', 'artifacts', 'Yahad.Platform.Bundle'))) {
    refuse('installer/artifacts/Yahad.Platform.Bundle exists from an earlier build; package.ps1 needs a clean checkout (a fresh clone of HEAD).');
  }

  const version = JSON.parse(readFileSync(join(repo, 'package.json'), 'utf8')).version;
  const packageDirectory = join(out, 'package');
  mkdirSync(out, { recursive: true });
  const args = ['-NoProfile', '-NonInteractive', '-File', join(repo, 'installer', 'package.ps1')];
  // Python is a command name found on PATH, so it is passed as given.
  for (const [name, value] of Object.entries(inputs)) args.push(`-${name}`, name === 'Python' ? value : resolve(value));
  args.push('-ReleaseKey', join(repo, COMMITTED_KEY), '-Output', packageDirectory);
  console.log(`Building ${version} from ${commit} ...`);
  if (spawnSync(pwsh, args, { cwd: repo, stdio: 'inherit' }).status !== 0) refuse('installer/package.ps1 failed; nothing was signed.');
  if (git(repo, 'rev-parse', 'HEAD').trim() !== commit || git(repo, 'status', '--porcelain').trim()) refuse('The checkout changed during the build; nothing was signed.');

  const { manifestName, manifest } = requireBuiltKey(packageDirectory, spki);
  if (manifest.version !== version || manifest.sourceCommit !== commit) refuse(`The manifest says ${manifest.version} at ${manifest.sourceCommit}, expected ${version} at ${commit}.`);
  const assets = join(out, 'assets');
  mkdirSync(assets);
  const manifestFile = join(assets, manifestName);
  const setupFile = join(assets, manifest.setup.name);
  copyFileSync(join(packageDirectory, 'release', manifestName), manifestFile);
  copyFileSync(join(packageDirectory, 'release', manifest.setup.name), setupFile);
  const setupBytes = readFileSync(setupFile);
  if (setupBytes.length !== manifest.setup.bytes || createHash('sha256').update(setupBytes).digest('hex') !== manifest.setup.sha256) refuse('The Setup does not match its manifest; nothing was signed.');

  execFileSync(process.execPath, [join(repo, 'scripts', 'windows-release-key.mjs'), 'sign', keyFile, manifestFile], { stdio: 'inherit' });
  // The check the Updater makes, with the committed key, before anything can be uploaded.
  const signature = readFileSync(`${manifestFile}.sig`);
  const trusted = createPublicKey({ key: spki, format: 'der', type: 'spki' });
  if (signature.length !== 64 || !verify('sha256', Buffer.concat([prefix, readFileSync(manifestFile)]), { key: trusted, dsaEncoding: 'ieee-p1363' }, signature)) {
    refuse('The signature does not verify against the committed key; do not publish.');
  }

  const files = [manifestFile, `${manifestFile}.sig`, setupFile];
  console.log(`\nSigned release ${version} written to ${assets} (nothing was published).`);
  console.log('Push the commit first, then review the assets and run:\n');
  console.log(`gh release create v${version} ${files.map((file) => `"${file}"`).join(' ')} --repo ${REPOSITORY} --target ${commit} --title "Yahad ${version}" --notes "Windows release ${version}"`);
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try { main(resolve(dirname(fileURLToPath(import.meta.url)), '..'), process.argv.slice(2)); } catch (error) {
    if (error instanceof Refusal) { console.error(`REFUSED: ${error.message}`); process.exit(1); }
    throw error;
  }
}
