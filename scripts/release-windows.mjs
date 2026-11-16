#!/usr/bin/env node
// Maintainer commands for the Windows release and the one-release-both-platforms publish.
//
//   build <key.pem> --out <new-dir>   build and sign Windows from THIS clean checkout
//   install                           npm run windows:install: build in a throwaway clone, launch Setup
//   publish [--version X.Y.Z] [--dry-run]   npm run release:publish: Linux + Windows assets, then GitHub
//
// A release is nine assets (docs/WINDOWS-APP-PACKAGE-PLAN.md section 10). `publish` also enforces the
// infrastructure version rule: inputs changed since the latest release's infrastructure => the version
// must be bumped; unchanged => its build is reused from a git worktree at that release's commit.
//
// Options (all subcommands): --key <key.pem> (default ~/yahad-release-key/key.pem), --node-dir,
// --dotnet, --npm-cache, --vendors, --package-cache, --infra-cache, --python, --pwsh. Inputs default to .local/
// of the checkout that runs the command.
//
// The Windows build is installer/package.ps1 and the signature is
// scripts/windows-release-key.mjs sign (both also used by CI); the Linux assets are
// scripts/release.mjs package-https and scripts/release-updater.mjs package-github. Only `publish`, after the typed confirmation, pushes or publishes.
import { createHash, createPrivateKey, createPublicKey } from 'node:crypto';
import { inflateRawSync } from 'node:zlib';
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { homedir, tmpdir } from 'node:os';
import { dirname, isAbsolute, join, relative, resolve } from 'node:path';
import { execFileSync, spawnSync } from 'node:child_process';
import { createInterface } from 'node:readline/promises';
import { fileURLToPath } from 'node:url';
import { keyIdOf, verifyRelease } from './windows-release-key.mjs';

const REPOSITORY = 'NehoraiHadad/yahad'; // fixed in installer/Yahad.Updater/GitHubReleaseSource.cs and deploy/updater.py
const COMMITTED_KEY = 'installer/release-key.spki';
// The Pi (GitHub-HTTPS trust) needs these two to update the app: deploy/updater-transport.py:26 and
// deploy/updater.py:229.
const APP_ASSETS = ['yachad-release.json', 'yachad-app.zip'];
// Enrolled machines take their root worker from this pair (deploy/updater-transport.py:25). Everything
// else ignores it: direct workers only look up the assets they require (deploy/updater.py:678).
const UPDATER_ASSETS = ['yachad-updater.json', 'yachad-updater.zip'];
const LINUX_ASSETS = [...APP_ASSETS, ...UPDATER_ASSETS];
// The landing page links releases/latest/download/ this name. The updaters select the versioned file.
const STABLE_SETUP = 'Yahad.Setup.exe';
// The Windows names, as ReleaseManifest.ManifestName / SignatureName / SetupName / AppArchiveName.
export const windowsNames = (version) => ({
  manifest: `yahad-windows-release-${version}.json`,
  signature: `yahad-windows-release-${version}.json.sig`,
  setup: `Yahad.Setup-${version}.exe`,
  app: `yahad-windows-app-${version}.zip`
});
const INFRASTRUCTURE_PROPS = 'installer/Directory.Build.props';
const INFRA_INPUTS = 'installer/windows/infra_inputs.py';
const sha256Of = (bytes) => createHash('sha256').update(bytes).digest('hex');

export class Refusal extends Error {}
const refuse = (message) => { throw new Refusal(message); };

function insideCheckout(path) {
  for (let at = resolve(path); ; at = dirname(at)) {
    if (existsSync(join(at, '.git'))) return at;
    if (dirname(at) === at) return null;
  }
}

const git = (repo, ...args) => execFileSync('git', args, { cwd: repo, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
const dirty = (repo) => git(repo, 'status', '--porcelain').trim() !== '';

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
  const manifests = readdirSync(join(packageDirectory, 'release')).filter((name) => /^yahad-windows-release-.+\.json$/.test(name));
  if (manifests.length !== 1) refuse(`Expected one release manifest in ${packageDirectory}/release, found ${manifests.length}.`);
  const manifest = JSON.parse(readFileSync(join(packageDirectory, 'release', manifests[0]), 'utf8'));
  if (manifest.format !== 2 || manifest.product !== 'yahad-windows-release') refuse(`${manifests[0]} is not format 2 of yahad-windows-release.`);
  if (manifest.keyId !== expected) refuse(`The release manifest names key ${manifest.keyId}, not the committed key ${expected}.`);
  return { manifestName: manifests[0], manifest };
}

// ---- zip reading -------------------------------------------------------------------------------
// Node has no zip reader and the repository has no zip dependency, so this reads exactly what the
// builders write: stored or deflated members, no zip64, no data descriptors.
const LOCAL_SIGNATURE = 0x04034b50;
function zipMember(buffer, headerOffset, label) {
  if (headerOffset < 0 || headerOffset + 30 > buffer.length || buffer.readUInt32LE(headerOffset) !== LOCAL_SIGNATURE) refuse(`${label}: no zip member at offset ${headerOffset}.`);
  const flags = buffer.readUInt16LE(headerOffset + 6);
  const method = buffer.readUInt16LE(headerOffset + 8);
  const compressed = buffer.readUInt32LE(headerOffset + 18);
  const size = buffer.readUInt32LE(headerOffset + 22);
  const start = headerOffset + 30 + buffer.readUInt16LE(headerOffset + 26) + buffer.readUInt16LE(headerOffset + 28);
  if ((flags & 0x08) !== 0 || (flags & 0x01) !== 0) refuse(`${label}: zip member with a data descriptor or encryption is not supported.`);
  if (start + compressed > buffer.length) refuse(`${label}: zip member is truncated.`);
  const data = buffer.subarray(start, start + compressed);
  const bytes = method === 0 ? data : method === 8 ? inflateRawSync(data) : refuse(`${label}: zip compression method ${method} is not supported.`);
  if (bytes.length !== size) refuse(`${label}: zip member size does not match its header.`);
  return bytes;
}

// name -> { size, offset } from the central directory.
export function zipDirectory(buffer, label) {
  let end = -1;
  for (let at = buffer.length - 22; at >= Math.max(0, buffer.length - 22 - 65535); at--) {
    if (buffer.readUInt32LE(at) === 0x06054b50) { end = at; break; }
  }
  if (end < 0) refuse(`${label} is not a zip archive.`);
  const count = buffer.readUInt16LE(end + 10);
  let at = buffer.readUInt32LE(end + 16);
  if (count === 0xffff || at === 0xffffffff) refuse(`${label}: zip64 is not supported.`);
  const entries = new Map();
  for (let i = 0; i < count; i++) {
    if (at + 46 > buffer.length || buffer.readUInt32LE(at) !== 0x02014b50) refuse(`${label}: damaged zip directory.`);
    const nameLength = buffer.readUInt16LE(at + 28);
    const name = buffer.toString('utf8', at + 46, at + 46 + nameLength);
    if (entries.has(name)) refuse(`${label}: duplicate zip member ${name}.`);
    entries.set(name, { size: buffer.readUInt32LE(at + 24), offset: buffer.readUInt32LE(at + 42) });
    at += 46 + nameLength + buffer.readUInt16LE(at + 30) + buffer.readUInt16LE(at + 32);
  }
  return entries;
}
const zipRead = (buffer, entries, name, label) => zipMember(buffer, entries.get(name)?.offset ?? -1, `${label}!${name}`);

// The distributable Setup is a compressed single-file bundle (EnableCompressionInSingleFile,
// installer/Yahad.Setup/Yahad.Setup.csproj), so its embedded package is not visible in the .exe bytes.
// The Setup itself is the authority: `--verify-package` opens its own embedded package, checks the zip
// hash, every member and both tree records against the embedded manifest, and prints that identity
// (installer/Yahad.Setup/SetupEntry.cs). Run on the release file whose SHA-256 the manifest names, so
// the report is bound to those bytes. Setup requires administrator rights; RunAsInvoker starts this
// read-only audit with the caller's own token, as installer/package.ps1 does.
export function auditSetup(setupFile) {
  const run = spawnSync(setupFile, ['--verify-package'], { encoding: 'utf8', windowsHide: true, timeout: 120000, env: { ...process.env, __COMPAT_LAYER: 'RunAsInvoker' } });
  if (run.error || run.status !== 0) refuse(`${basenameOf(setupFile)} --verify-package failed (${run.error?.message ?? `exit ${run.status}`}).`);
  try { return JSON.parse(run.stdout); } catch { refuse(`${basenameOf(setupFile)} --verify-package printed no report.`); }
}

// What the Setup's embedded package says of itself against the signed manifest: every field both carry
// (UpgradeHost.RequireAgrees), the key it trusts, and the source commit it was compiled from.
function checkSetupReport(report, manifest, name) {
  const infra = manifest.infrastructure; const app = manifest.app;
  const sorted = (list) => JSON.stringify([...(list ?? [])].sort((a, b) => a - b));
  const differs = [
    ['infrastructure.version', report.infrastructure?.version, infra.version],
    ['infrastructure.contractMin', report.infrastructure?.contractMin, infra.contractMin],
    ['infrastructure.contractMax', report.infrastructure?.contractMax, infra.contractMax],
    ['infrastructure.sourceCommit', report.infrastructure?.sourceCommit, infra.sourceCommit],
    ['infrastructure.treeSha256', report.infrastructure?.treeSha256, infra.treeSha256],
    ['setupSourceCommit', report.setupSourceCommit, infra.sourceCommit],
    ['coreSourceCommit', report.coreSourceCommit, infra.sourceCommit],
    ['app.version', report.app?.version, manifest.version],
    ['app.sourceCommit', report.app?.sourceCommit, manifest.sourceCommit],
    ['app.treeSha256', report.app?.treeSha256, app.treeSha256],
    ['app.requiresContract', report.app?.requiresContract, app.requiresContract],
    ['app.databaseSchema', report.app?.databaseSchema, app.databaseSchema],
    ['app.databaseMigrationsFrom', sorted(report.app?.databaseMigrationsFrom), sorted(app.databaseMigrationsFrom)],
    ['minimumFrom.app', report.minimumFrom?.app, manifest.minimumFrom?.app],
    ['minimumFrom.infrastructure', report.minimumFrom?.infrastructure, manifest.minimumFrom?.infrastructure],
    ['releaseKeyId', report.releaseKeyId, manifest.keyId]
  ].filter(([, seen, signed]) => seen !== signed).map(([field]) => field);
  if (differs.length) refuse(`The package embedded in ${name} disagrees with the signed manifest on ${differs.join(', ')}.`);
}

// The app archive against its manifest: bytes and hash first, then exactly app-tree.json plus app/ members,
// app-tree.json hashing to app.treeSha256 and listing exactly those members at their recorded sizes.
function checkAppArchive(zip, manifest, name) {
  const archive = manifest.app?.archive;
  if (archive?.name !== name || archive.bytes !== zip.length || archive.sha256 !== sha256Of(zip)) refuse(`The manifest does not describe ${name} (size or SHA-256 differs).`);
  const entries = zipDirectory(zip, name);
  const treeBytes = zipRead(zip, entries, 'app-tree.json', name);
  if (sha256Of(treeBytes) !== manifest.app.treeSha256) refuse(`app-tree.json inside ${name} does not hash to the manifest's app.treeSha256.`);
  const outside = [...entries.keys()].filter((member) => member !== 'app-tree.json' && !member.startsWith('app/'));
  if (outside.length) refuse(`${name} holds members outside app-tree.json and app/: ${outside.slice(0, 3).join(', ')}.`);
  const listed = new Map(JSON.parse(treeBytes.toString('utf8')).files.map((file) => [file.path, file.size]));
  const members = [...entries.keys()].filter((member) => member !== 'app-tree.json');
  if (members.length !== listed.size || members.some((member) => entries.get(member).size !== listed.get(member))) refuse(`${name} does not match its app-tree.json (member names or sizes differ).`);
}

// The Windows half of a release in `directory`, each file consistent with the signed manifest. `audit`
// reads the Setup's own report of its embedded package (auditSetup).
export function checkWindowsRelease(directory, version, spki, audit = auditSetup) {
  const names = windowsNames(version);
  const read = (name) => readFileSync(join(directory, name));
  const manifestBytes = read(names.manifest);
  let manifest;
  try { manifest = verifyRelease(spki, manifestBytes, read(names.signature)); } catch (error) { refuse(`${names.manifest}: ${error.message}.`); }
  if (!manifest) refuse(`${names.signature} does not verify against the committed key.`);
  if (manifest.version !== version) refuse(`${names.manifest} is for ${manifest.version}, not ${version}.`);
  const setupBytes = read(names.setup);
  const setup = manifest.infrastructure?.setup;
  if (setup?.name !== names.setup || setup.bytes !== setupBytes.length || setup.sha256 !== sha256Of(setupBytes)) refuse(`${names.manifest} does not describe ${names.setup} (name, size or SHA-256 differs).`);
  checkAppArchive(read(names.app), manifest, names.app);
  checkSetupReport(audit(join(directory, names.setup)), manifest, names.setup);
  return manifest;
}

// Exactly the nine assets, each consistent with its own manifest. A release missing any one of them would
// stall one platform's updater.
export function collectReleaseAssets(directory, version, spki, audit = auditSetup) {
  const names = windowsNames(version);
  const expected = [...LINUX_ASSETS, names.manifest, names.signature, names.app, names.setup, STABLE_SETUP];
  const found = existsSync(directory) ? readdirSync(directory) : [];
  const missing = expected.filter((name) => !found.includes(name) || statSync(join(directory, name)).size === 0);
  if (missing.length) refuse(`Release asset set incomplete; missing or empty: ${missing.join(', ')}.`);
  const extra = found.filter((name) => !expected.includes(name));
  if (extra.length) refuse(`Unexpected release assets: ${extra.join(', ')}.`);
  const read = (name) => readFileSync(join(directory, name));
  const linux = JSON.parse(read('yachad-release.json').toString('utf8'));
  const zip = read('yachad-app.zip');
  if (linux.version !== version || linux.archive !== 'yachad-app.zip' || linux.bytes !== zip.length || linux.sha256 !== sha256Of(zip)) refuse('yachad-release.json does not describe yachad-app.zip at this version.');
  const runtime = JSON.parse(read('yachad-updater.json').toString('utf8'));
  const runtimeZip = read('yachad-updater.zip');
  if (runtime.kind !== 'yachad-updater' || runtime.version !== runtimeVersionFor(version) || runtime.archive !== 'yachad-updater.zip' || runtime.bytes !== runtimeZip.length || runtime.sha256 !== sha256Of(runtimeZip)) refuse(`yachad-updater.json does not describe yachad-updater.zip at runtime ${runtimeVersionFor(version)}.`);
  checkWindowsRelease(directory, version, spki, audit);
  if (!read(STABLE_SETUP).equals(read(names.setup))) refuse(`${STABLE_SETUP} is not the same file as ${names.setup}.`);
  return expected.map((name) => ({ name, path: join(directory, name), bytes: statSync(join(directory, name)).size }));
}

// An enrolled machine refuses a runtime at or below the one it accepted unless the bytes are identical
// (deploy/updater-supervisor.py:456-458), and that refusal stops its app update too
// (updater-supervisor.py:622-624, deploy/updater.py:881). The bytes cannot be repeated: the zip differs
// between zlib builds. So the runtime version is derived from the app version, which every release must
// raise (chooseVersion): each release's pair is above all earlier ones, whether or not deploy/updater.py
// changed.
export function runtimeVersionFor(appVersion) {
  const [major, minor, patch] = parts(appVersion);
  return `${major + 1}.${minor}.${patch}`;
}

// A machine enrolled by the bootstrap starts at its initial runtime, so a pair must also be above that.
export function requireRuntimeAboveBootstrap(runtimeVersion, bootstrapSource) {
  const initial = /^INITIAL_RUNTIME_VERSION = '([0-9.]+)'$/m.exec(bootstrapSource)?.[1];
  if (!initial || !isVersion(initial)) refuse('deploy/upgrade-worker-github.py names no INITIAL_RUNTIME_VERSION.');
  if (compareVersions(runtimeVersion, initial) <= 0) refuse(`Runtime ${runtimeVersion} is not above the bootstrap's initial runtime ${initial}; enrolled machines would refuse it and stop updating.`);
}

// ---- options -----------------------------------------------------------------------------------
const VALUE_OPTIONS = ['out', 'key', 'version', 'node-dir', 'dotnet', 'npm-cache', 'vendors', 'package-cache', 'infra-cache', 'python', 'pwsh'];
function parse(argv, { flags = [], positional: wanted = 0 } = {}) {
  const options = {}; const positional = [];
  for (let i = 0; i < argv.length; i++) {
    if (!argv[i].startsWith('--')) { positional.push(argv[i]); continue; }
    const name = argv[i].slice(2);
    if (flags.includes(name)) options[name] = true;
    else if (VALUE_OPTIONS.includes(name) && i + 1 < argv.length) options[name] = argv[++i];
    else refuse(`Unknown or incomplete option ${argv[i]}`);
  }
  if (positional.length !== wanted) refuse(`Unexpected arguments: ${positional.join(' ')}`);
  return { options, positional };
}

// Build inputs come from the checkout that runs the command, never from a throwaway clone.
export function resolveInputs(home, options) {
  const local = (...parts) => join(home, '.local', ...parts);
  const tools = existsSync(local('tools')) ? readdirSync(local('tools')) : [];
  const node24 = tools.filter((name) => /^node-v24\..*-win-x64$/.test(name)).sort().at(-1);
  const nodeDirectory = options['node-dir'] ?? (node24 && local('tools', node24));
  if (!nodeDirectory) refuse('No Node 24 directory found under .local/tools; pass --node-dir.');
  return {
    pwsh: options.pwsh ?? (existsSync(local('tools', 'pwsh', 'pwsh.exe')) ? local('tools', 'pwsh', 'pwsh.exe') : 'pwsh'),
    NodeDirectory: resolve(nodeDirectory),
    NpmCache: resolve(options['npm-cache'] ?? local('npm-cache')),
    VendorDirectory: resolve(options.vendors ?? local('vendors')),
    DotNet: resolve(options.dotnet ?? local('tools', 'dotnet', 'dotnet.exe')),
    PackageCache: resolve(options['package-cache'] ?? local('nuget-cache')),
    // Built infrastructure, one folder per infrastructure version (installer/package.ps1 -InfraCache).
    InfraCache: resolve(options['infra-cache'] ?? local('infra-cache')),
    Python: options.python ?? 'python' // a command name found on PATH
  };
}

const defaultKey = () => join(homedir(), 'yahad-release-key', 'key.pem');
function requireUsableKey(options) {
  const keyFile = resolve(options.key ?? defaultKey());
  if (!existsSync(keyFile)) refuse(`Signing key not found: ${keyFile}. Create it once with: node scripts/windows-release-key.mjs keygen <dir> (or pass --key).`);
  const checkout = insideCheckout(keyFile);
  if (checkout) refuse(`Refusing a signing key inside the checkout ${checkout}. Keep it outside any repository.`);
  return keyFile;
}

// ---- the Windows build -------------------------------------------------------------------------
// Builds `repo` (a clean checkout of a committed state) and writes <out>/assets with the four
// Windows files (manifest, signature, app archive, versioned Setup). Used directly by `build` and inside
// the throwaway clones of install/publish. The infrastructure cache is inputs.InfraCache. `infra` is
// { source, sourceCommit, digest } when an earlier infrastructure is reused or the digest is known: a
// checkout at that commit (package.ps1 -InfraSource) and the input digest the build must report.
export function buildSigned({ repo, keyFile, out, inputsOf, infra = {} }) {
  const spki = committedKey(repo);
  requireKeyMatches(keyFile, spki);
  if (dirty(repo)) refuse('The working tree is dirty. Commit or discard every change; a release is built from HEAD only.');
  const commit = git(repo, 'rev-parse', 'HEAD').trim();
  const resolvedOut = resolve(out);
  if (existsSync(resolvedOut)) refuse(`Output directory ${resolvedOut} already exists; choose a new one.`);
  const inside = relative(repo, resolvedOut);
  if (!inside.startsWith('..') && !isAbsolute(inside) && spawnSync('git', ['check-ignore', '-q', resolvedOut], { cwd: repo }).status !== 0) {
    refuse(`Output ${resolvedOut} is inside the checkout and not git-ignored; it would dirty the tree. Use a directory outside the repository or under .local/.`);
  }
  const inputs = inputsOf(); // after every refusal above, so those need no build tools
  const version = JSON.parse(readFileSync(join(repo, 'package.json'), 'utf8')).version;
  const packageDirectory = join(resolvedOut, 'package');
  mkdirSync(resolvedOut, { recursive: true });
  const args = ['-NoProfile', '-NonInteractive', '-File', join(repo, 'installer', 'package.ps1')];
  for (const name of ['NodeDirectory', 'NpmCache', 'VendorDirectory', 'DotNet', 'PackageCache', 'Python']) args.push(`-${name}`, inputs[name]);
  args.push('-InfraCache', inputs.InfraCache);
  if (infra.source) args.push('-InfraSource', infra.source);
  args.push('-ReleaseKey', join(repo, COMMITTED_KEY), '-Output', packageDirectory);
  console.log(`Building ${version} from ${commit} ...`);
  if (spawnSync(inputs.pwsh, args, { cwd: repo, stdio: 'inherit' }).status !== 0) refuse('installer/package.ps1 failed; nothing was signed.');
  if (git(repo, 'rev-parse', 'HEAD').trim() !== commit || dirty(repo)) refuse('The checkout changed during the build; nothing was signed.');

  const { manifestName, manifest } = requireBuiltKey(packageDirectory, spki);
  if (manifest.version !== version || manifest.sourceCommit !== commit) refuse(`The manifest says ${manifest.version} at ${manifest.sourceCommit}, expected ${version} at ${commit}.`);
  if (infra.sourceCommit && manifest.infrastructure.sourceCommit !== infra.sourceCommit) refuse(`The manifest's infrastructure comes from ${manifest.infrastructure.sourceCommit}, not the reused ${infra.sourceCommit}.`);
  if (infra.digest) {
    const recorded = JSON.parse(readFileSync(join(packageDirectory, 'package-build.json'), 'utf8')).infraInputDigest;
    if (recorded !== infra.digest) refuse(`The build recorded infrastructure input digest ${recorded}, expected ${infra.digest}.`);
  }
  const names = windowsNames(version);
  if (manifestName !== names.manifest) refuse(`The build wrote ${manifestName}, expected ${names.manifest}.`);
  const built = join(packageDirectory, 'release');
  const unexpected = readdirSync(built).filter((name) => ![names.manifest, names.app, names.setup].includes(name));
  if (unexpected.length) refuse(`The build left unexpected files in ${built}: ${unexpected.join(', ')}.`);
  const assets = join(resolvedOut, 'assets');
  mkdirSync(assets);
  for (const name of [names.manifest, names.app, names.setup]) {
    if (!existsSync(join(built, name))) refuse(`The build did not produce ${name}.`);
    copyFileSync(join(built, name), join(assets, name));
  }
  const manifestFile = join(assets, names.manifest);
  execFileSync(process.execPath, [join(repo, 'scripts', 'windows-release-key.mjs'), 'sign', keyFile, manifestFile], { stdio: 'inherit' });
  checkWindowsRelease(assets, version, spki);
  return { version, commit, spki, keyId: keyIdOf(spki), assets, manifestFile, setupFile: join(assets, names.setup) };
}

// ---- throwaway clones --------------------------------------------------------------------------
// A canonical LF checkout of exactly this commit (release.mjs refuses CRLF in deploy/), in a
// temp folder that is always removed.
function cloneHead(repo, directory) {
  const sha = git(repo, 'rev-parse', 'HEAD').trim();
  execFileSync('git', ['clone', '-q', '--no-checkout', '--no-hardlinks', '-c', 'core.autocrlf=false', '-c', 'core.longpaths=true', repo, directory], { stdio: 'inherit' });
  git(directory, 'checkout', '-q', '--detach', sha);
  return sha;
}
const removeTree = (directory) => rmSync(directory, { recursive: true, force: true, maxRetries: 5, retryDelay: 500 });
async function withTemporaryDirectory(work) {
  const root = mkdtempSync(join(tmpdir(), 'yahad-release-'));
  try { return await work(root); } finally { removeTree(root); }
}
function npm(inputs, cwd, ...args) {
  const cli = join(inputs.NodeDirectory, 'node_modules', 'npm', 'bin', 'npm-cli.js');
  execFileSync(join(inputs.NodeDirectory, 'node.exe'), [cli, ...args], { cwd, stdio: 'inherit' });
}
const installDependencies = (inputs, clone) => npm(inputs, clone, 'ci', '--offline', '--cache', inputs.NpmCache, '--no-audit', '--no-fund');

// ---- windows:install ---------------------------------------------------------------------------
function shellExecute(file) {
  // Start-Process is ShellExecute, so Setup's requireAdministrator manifest brings up the single UAC prompt.
  const quoted = file.replaceAll("'", "''");
  const result = spawnSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', `Start-Process -FilePath '${quoted}'`], { stdio: 'inherit' });
  if (result.status !== 0) refuse(`Could not launch ${file}`);
}

export async function install({ repo, options, build = buildClonedWindows, launch = shellExecute }) {
  if (process.platform !== 'win32') refuse('windows:install runs on Windows.');
  if (dirty(repo)) refuse('The working tree is dirty. Commit or discard every change first; the installer is built from HEAD.');
  const keyFile = requireUsableKey(options);
  const inputs = resolveInputs(repo, options);
  const destination = join(repo, '.local', 'windows-install');
  const built = await withTemporaryDirectory(async (root) => {
    const clone = join(root, 'repo');
    cloneHead(repo, clone);
    const result = await build({ clone, root, keyFile, inputs });
    // Copied out before the clone goes: the Setup is self-contained.
    mkdirSync(destination, { recursive: true });
    const target = join(destination, basenameOf(result.setupFile));
    rmSync(target, { force: true });
    copyFileSync(result.setupFile, target);
    return { ...result, setupFile: target };
  });
  console.log(`Setup ${built.version} (${built.commit.slice(0, 12)}) is at ${built.setupFile}. Launching it; accept the one UAC prompt.`);
  launch(built.setupFile);
  return built;
}
const basenameOf = (path) => path.split(/[\\/]/).at(-1);

async function buildClonedWindows({ clone, root, keyFile, inputs }) {
  installDependencies(inputs, clone);
  return buildSigned({ repo: clone, keyFile, out: join(root, 'windows'), inputsOf: () => inputs });
}

// ---- release:publish ---------------------------------------------------------------------------
function gh(args) {
  if (process.platform !== 'win32') return execFileSync('gh', args, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'inherit'] });
  // A .cmd shim is only found through cmd.exe, so quote each argument for it ourselves.
  const line = ['gh', ...args.map((a) => (/[\s&|<>^%"]/.test(a) ? `"${a}"` : a))].join(' ');
  return execFileSync(process.env.ComSpec ?? 'cmd.exe', ['/d', '/s', '/c', `"${line}"`], { encoding: 'utf8', windowsVerbatimArguments: true, stdio: ['ignore', 'pipe', 'inherit'] });
}
const parts = (version) => version.split('.').map(Number);
export const isVersion = (value) => /^(0|[1-9]\d{0,4})\.(0|[1-9]\d{0,4})\.(0|[1-9]\d{0,4})$/.test(value);
export function compareVersions(a, b) {
  const [x, y] = [parts(a), parts(b)];
  for (let i = 0; i < 3; i++) if (x[i] !== y[i]) return x[i] < y[i] ? -1 : 1;
  return 0;
}

// Only a clean main exactly equal to origin/main may be released: the Pi must never receive an unmerged branch.
export function requireReleasable(repo) {
  if (dirty(repo)) refuse('The working tree is dirty. Commit or discard every change first.');
  const branch = git(repo, 'branch', '--show-current').trim();
  if (branch !== 'main') refuse(`Releases come from main; this checkout is on ${branch || 'a detached HEAD'}.`);
  try { git(repo, 'fetch', '-q', 'origin', 'main'); } catch (error) { refuse(`Cannot fetch origin/main: ${error.stderr ?? error.message}`); }
  const head = git(repo, 'rev-parse', 'HEAD').trim();
  const remote = git(repo, 'rev-parse', 'origin/main').trim();
  if (head !== remote) {
    const behind = Number(git(repo, 'rev-list', '--count', 'HEAD..origin/main'));
    const ahead = Number(git(repo, 'rev-list', '--count', 'origin/main..HEAD'));
    refuse(`main is not equal to origin/main (${ahead} ahead, ${behind} behind). Push or pull first; only what is on origin/main may be released.`);
  }
  return head;
}

export function chooseVersion(current, latest, requested) {
  if (!isVersion(current) || !isVersion(latest)) refuse(`Unusable version: package.json ${current}, latest release ${latest}.`);
  const [major, minor, patch] = parts(current);
  const version = requested ?? `${major}.${minor}.${patch + 1}`;
  if (!isVersion(version)) refuse(`${version} is not a version of the form X.Y.Z.`);
  if (compareVersions(version, latest) <= 0) refuse(`Version ${version} is not above the latest release ${latest}. Pass --version X.Y.Z.`);
  return version;
}

// package.json and package-lock.json carry the version; nothing else changes.
function setVersion(clone, version) {
  for (const file of ['package.json', 'package-lock.json']) {
    const path = join(clone, file);
    const value = JSON.parse(readFileSync(path, 'utf8'));
    value.version = version;
    if (value.packages?.['']) value.packages[''].version = version;
    writeFileSync(path, `${JSON.stringify(value, null, 2)}\n`);
  }
}

// ---- infrastructure version rule (plan section 10) ---------------------------------------------
// The infrastructure input digest is computed by installer/windows/infra_inputs.py, the function
// installer/package.ps1 uses, from git object ids, so any commit can be digested without a checkout.
export function inputDigest(repo, python, commit) {
  const out = execFileSync(python, [INFRA_INPUTS, 'digest', '--commit', commit], { cwd: repo, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
  if (!/^[0-9a-f]{32,}$/.test(out)) refuse(`${INFRA_INPUTS} printed no hex digest for ${commit}.`);
  return out;
}

export function infrastructureVersionAt(repo, commit) {
  const props = git(repo, 'show', `${commit}:${INFRASTRUCTURE_PROPS}`);
  const version = /<YahadInfrastructureVersion>([^<]+)<\/YahadInfrastructureVersion>/.exec(props)?.[1];
  if (!version || !isVersion(version)) refuse(`${INFRASTRUCTURE_PROPS} at ${commit} names no usable YahadInfrastructureVersion.`);
  return version;
}

// head: { version, digest } of the commit being released; baseline: the latest release's signed
// infrastructure { version, sourceCommit }, or null when no Windows release exists yet; digestAt(commit).
//   changed inputs  -> the infrastructure version must be above the baseline's (a new build)
//   same inputs     -> the version must equal the baseline's (the baseline's build is reused)
export function planInfrastructure({ head, baseline, digestAt }) {
  if (!baseline) return { mode: 'first', digest: head.digest };
  const order = compareVersions(head.version, baseline.version);
  if (order < 0) refuse(`Infrastructure version ${head.version} is below the latest release's ${baseline.version}.`);
  if (digestAt(baseline.sourceCommit) !== head.digest) {
    if (order === 0) refuse(`Infrastructure inputs changed since ${baseline.version} (${baseline.sourceCommit}) but YahadInfrastructureVersion is still ${head.version}. Bump it in ${INFRASTRUCTURE_PROPS}.`);
    return { mode: 'build', digest: head.digest };
  }
  if (order !== 0) refuse(`YahadInfrastructureVersion is ${head.version} but no infrastructure input changed since ${baseline.version} (${baseline.sourceCommit}). Restore ${baseline.version}.`);
  return { mode: 'reuse', digest: head.digest, sourceCommit: baseline.sourceCommit };
}

// The latest release's infrastructure identity, read from its signed manifest. A latest release without a
// Windows release manifest (the format before this layout) leaves nothing to compare with: null.
function latestBaseline({ tag, version, spki, scratch }) {
  const names = windowsNames(version);
  const listed = gh(['api', `repos/${REPOSITORY}/releases/latest`, '--jq', '.assets[].name']).split(/\r?\n/);
  if (!listed.includes(names.manifest)) return null;
  gh(['release', 'download', tag, '--repo', REPOSITORY, '--pattern', names.manifest, '--pattern', names.signature, '--dir', scratch]);
  let manifest;
  try { manifest = verifyRelease(spki, readFileSync(join(scratch, names.manifest)), readFileSync(join(scratch, names.signature))); } catch (error) { refuse(`The latest release's ${names.manifest}: ${error.message}.`); }
  if (!manifest) refuse(`The latest release's ${names.signature} does not verify against the committed key.`);
  return { version: manifest.infrastructure.version, sourceCommit: manifest.infrastructure.sourceCommit };
}

async function planFromLatest({ repo, inputs, spki, latest }) {
  const head = git(repo, 'rev-parse', 'HEAD').trim();
  const digest = inputDigest(repo, inputs.Python, head);
  return withTemporaryDirectory(async (scratch) => planInfrastructure({
    head: { version: infrastructureVersionAt(repo, head), digest },
    baseline: latestBaseline({ tag: `v${latest}`, version: latest, spki, scratch }),
    digestAt: (commit) => inputDigest(repo, inputs.Python, commit)
  }));
}

// A reused infrastructure is packaged from a checkout at its own commit: a git worktree under the
// git-ignored .local/, removed again whatever happens.
export async function withInfrastructureSource(repo, plan, work) {
  if (plan.mode !== 'reuse') return work({ digest: plan.digest });
  const root = join(repo, '.local', 'infra-source');
  if (spawnSync('git', ['check-ignore', '-q', root], { cwd: repo }).status !== 0) refuse(`${root} is not git-ignored; the infrastructure worktree would dirty the checkout.`);
  try { git(repo, 'cat-file', '-e', `${plan.sourceCommit}^{commit}`); } catch { refuse(`The infrastructure commit ${plan.sourceCommit} is not in this checkout; fetch origin.`); }
  const source = join(root, plan.sourceCommit.slice(0, 12));
  const remove = () => {
    try { git(repo, 'worktree', 'remove', '--force', source); } catch { /* not registered */ }
    rmSync(source, { recursive: true, force: true, maxRetries: 5, retryDelay: 500 });
    git(repo, 'worktree', 'prune');
  };
  remove(); // a leftover from an interrupted run
  mkdirSync(root, { recursive: true });
  git(repo, '-c', 'core.autocrlf=false', '-c', 'core.longpaths=true', 'worktree', 'add', '--detach', source, plan.sourceCommit);
  try { return await work({ source, sourceCommit: plan.sourceCommit, digest: plan.digest }); } finally { remove(); }
}

async function stageRelease({ clone, root, keyFile, inputs, infra }) {
  installDependencies(inputs, clone);
  npm(inputs, clone, 'run', 'build');
  const linux = join(root, 'linux');
  execFileSync(process.execPath, [join(clone, 'scripts', 'release.mjs'), 'package-https', linux], { cwd: clone, stdio: 'inherit' });
  const runtime = runtimeVersionFor(JSON.parse(readFileSync(join(clone, 'package.json'), 'utf8')).version);
  requireRuntimeAboveBootstrap(runtime, readFileSync(join(clone, 'deploy', 'upgrade-worker-github.py'), 'utf8'));
  const updater = join(root, 'updater');
  execFileSync(process.execPath, [join(clone, 'scripts', 'release-updater.mjs'), 'package-github', runtime, updater], { cwd: clone, stdio: 'inherit' });
  const windows = buildSigned({ repo: clone, keyFile, out: join(root, 'windows'), inputsOf: () => inputs, infra });
  const assets = join(root, 'assets');
  mkdirSync(assets);
  for (const name of APP_ASSETS) copyFileSync(join(linux, name), join(assets, name));
  for (const name of UPDATER_ASSETS) copyFileSync(join(updater, name), join(assets, name));
  for (const name of readdirSync(windows.assets)) copyFileSync(join(windows.assets, name), join(assets, name));
  const version = JSON.parse(readFileSync(join(clone, 'package.json'), 'utf8')).version;
  copyFileSync(join(assets, windowsNames(version).setup), join(assets, STABLE_SETUP));
  return { assets, spki: windows.spki };
}

const typed = async (question) => {
  const rl = createInterface({ input: process.stdin, output: process.stdout });
  try { return (await rl.question(question)).trim(); } finally { rl.close(); }
};

export async function publish({ repo, options, confirm = typed, stage = stageRelease, plan = planFromLatest, audit = auditSetup, log = console.log }) {
  requireReleasable(repo);
  const keyFile = requireUsableKey(options);
  const inputs = resolveInputs(repo, options);
  let latest;
  try { latest = gh(['api', `repos/${REPOSITORY}/releases/latest`, '--jq', '.tag_name']).trim().replace(/^v/, ''); } catch { refuse('Cannot read the latest release with gh; run `gh auth status`.'); }
  const current = JSON.parse(readFileSync(join(repo, 'package.json'), 'utf8')).version;
  const version = chooseVersion(current, latest, options.version);
  const tag = `v${version}`;
  if (git(repo, 'ls-remote', '--tags', 'origin', `refs/tags/${tag}`).trim()) refuse(`Tag ${tag} already exists on origin.`);
  const originUrl = git(repo, 'remote', 'get-url', 'origin').trim();
  const identity = ['user.name', 'user.email'].map((key) => { try { return git(repo, 'config', key).trim(); } catch { return ''; } });
  if (identity.some((value) => !value)) refuse('git user.name and user.email must be set.');

  return withTemporaryDirectory(async (root) => {
    const clone = join(root, 'repo');
    cloneHead(repo, clone);
    setVersion(clone, version);
    git(clone, '-c', `user.name=${identity[0]}`, '-c', `user.email=${identity[1]}`, 'commit', '-q', '-am', `Release ${version}`);
    git(clone, '-c', `user.name=${identity[0]}`, '-c', `user.email=${identity[1]}`, 'tag', '-a', tag, '-m', `Yahad ${version}`);
    const commit = git(clone, 'rev-parse', 'HEAD').trim();
    const spki = committedKey(repo);
    const infrastructure = await plan({ repo, inputs, spki, latest });
    const { assets } = await withInfrastructureSource(repo, infrastructure, (infra) => stage({ clone, root, keyFile, inputs, infra }));
    const files = collectReleaseAssets(assets, version, spki, audit);

    log(`\nRelease ${version} (latest is ${latest})`);
    log(`  commit   ${commit}  (on top of origin/main)`);
    log(`  key id   ${keyIdOf(spki)}`);
    log(`  runtime  ${runtimeVersionFor(version)}  (root updater, for enrolled machines)`);
    log(`  infra    ${infrastructure.mode === 'reuse' ? `reused from ${infrastructure.sourceCommit}` : infrastructure.mode === 'build' ? 'new version, built from this commit' : 'first Windows release, built from this commit'}`);
    for (const file of files) log(`  asset    ${file.name}  ${(file.bytes / 1048576).toFixed(2)} MiB`);
    if (options['dry-run']) { log('\n--dry-run: nothing was pushed or published; the temporary clone is removed.'); return { version, commit, files, published: false }; }
    const answer = await confirm(`\nType ${version} to push ${tag} to origin/main and publish: `);
    if (answer !== version) refuse('Confirmation did not match; nothing was pushed or published.');

    git(clone, 'push', '--atomic', originUrl, 'HEAD:refs/heads/main', `refs/tags/${tag}`);
    const create = ['release', 'create', tag, ...files.map((file) => file.path), '--repo', REPOSITORY, '--verify-tag', '--latest', '--title', `Yahad ${version}`, '--generate-notes'];
    try { gh(create); } catch {
      // The tag is already public, so keep the signed assets for a hand retry.
      const kept = join(repo, '.local', `release-${version}`);
      mkdirSync(kept, { recursive: true });
      const keptFiles = files.map((file) => { copyFileSync(file.path, join(kept, file.name)); return join(kept, file.name); });
      const retry = ['release', 'create', tag, ...keptFiles, ...create.slice(3 + files.length)];
      refuse(`${tag} was pushed but gh release create failed. The assets are kept in ${kept}; retry with: gh ${retry.map((a) => (/\s/.test(a) ? `"${a}"` : a)).join(' ')}`);
    }
    const listed = gh(['api', `repos/${REPOSITORY}/releases/latest`, '--jq', '.assets[].name']).split(/\r?\n/).filter(Boolean).sort();
    const wanted = files.map((file) => file.name).sort();
    if (listed.join() !== wanted.join()) refuse(`Published, but /releases/latest lists ${listed.join(', ') || 'no assets'}; expected ${wanted.join(', ')}.`);
    log(`\nPublished ${tag} with all ${wanted.length} assets. Run \`git pull --ff-only\` to bring your main up to date.`);
    return { version, commit, files, published: true };
  });
}

// ---- command line ------------------------------------------------------------------------------
async function main(repo, argv) {
  const [command, ...rest] = argv;
  if (command === 'build') {
    const { options, positional } = parse(rest, { positional: 1 });
    if (!options.out) refuse('Usage: node scripts/release-windows.mjs build <key.pem> --out <new-dir> [options]');
    const keyFile = resolve(positional[0]);
    if (!existsSync(keyFile)) refuse(`Key file not found: ${keyFile}`);
    const checkout = insideCheckout(keyFile);
    if (checkout) refuse(`Refusing a signing key inside the checkout ${checkout}. Keep it outside any repository.`);
    const built = buildSigned({ repo, keyFile, out: options.out, inputsOf: () => resolveInputs(repo, options) });
    console.log(`\nSigned release ${built.version} written to ${built.assets} (nothing was published).`);
  } else if (command === 'install') {
    await install({ repo, options: parse(rest).options });
  } else if (command === 'publish') {
    await publish({ repo, options: parse(rest, { flags: ['dry-run'] }).options });
  } else {
    refuse('Usage: node scripts/release-windows.mjs build <key.pem> --out <dir> | install | publish [--version X.Y.Z] [--dry-run]');
  }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try { await main(resolve(dirname(fileURLToPath(import.meta.url)), '..'), process.argv.slice(2)); } catch (error) {
    if (error instanceof Refusal) { console.error(`REFUSED: ${error.message}`); process.exit(1); }
    throw error;
  }
}
