#!/usr/bin/env node
// Maintainer commands for the Windows release and the one-release-both-platforms publish.
//
//   build <key.pem> --out <new-dir>   build and sign Windows from THIS clean checkout
//   install                           npm run windows:install: build in a throwaway clone, launch Setup
//   publish [--version X.Y.Z] [--dry-run]   npm run release:publish: Linux + Windows assets, then GitHub
//
// Options (all subcommands): --key <key.pem> (default ~/yahad-release-key/key.pem), --node-dir,
// --dotnet, --npm-cache, --vendors, --package-cache, --python, --pwsh. Inputs default to .local/
// of the checkout that runs the command.
//
// The Windows build is installer/package.ps1 and the signature is
// scripts/windows-release-key.mjs sign (both also used by CI); the Linux assets are
// scripts/release.mjs package-https. Only `publish`, after the typed confirmation, pushes or publishes.
import { createHash, createPrivateKey, createPublicKey, verify } from 'node:crypto';
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { homedir, tmpdir } from 'node:os';
import { dirname, isAbsolute, join, relative, resolve } from 'node:path';
import { execFileSync, spawnSync } from 'node:child_process';
import { createInterface } from 'node:readline/promises';
import { fileURLToPath } from 'node:url';

const REPOSITORY = 'NehoraiHadad/yahad'; // fixed in installer/Yahad.Updater/GitHubReleaseSource.cs and deploy/updater.py
const COMMITTED_KEY = 'installer/release-key.spki';
// The Pi (GitHub-HTTPS trust) needs these two and nothing else: deploy/updater-transport.py:26 and
// deploy/updater.py:229. The yachad-updater.* pair is optional there (updater-transport.py:182-186).
const LINUX_ASSETS = ['yachad-release.json', 'yachad-app.zip'];
const prefix = Buffer.from('yahad-release-v1\n', 'ascii');
const keyIdOf = (spki) => createHash('sha256').update(spki).digest('hex').slice(0, 16);
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
  const manifests = readdirSync(join(packageDirectory, 'release')).filter((name) => /^yahad-windows-.+\.json$/.test(name));
  if (manifests.length !== 1) refuse(`Expected one release manifest in ${packageDirectory}/release, found ${manifests.length}.`);
  const manifest = JSON.parse(readFileSync(join(packageDirectory, 'release', manifests[0]), 'utf8'));
  if (manifest.keyId !== expected) refuse(`The release manifest names key ${manifest.keyId}, not the committed key ${expected}.`);
  return { manifestName: manifests[0], manifest };
}

// The check the Updater makes, with the committed key.
function signatureVerifies(manifestFile, spki) {
  const signature = readFileSync(`${manifestFile}.sig`);
  const trusted = createPublicKey({ key: spki, format: 'der', type: 'spki' });
  return signature.length === 64 && verify('sha256', Buffer.concat([prefix, readFileSync(manifestFile)]), { key: trusted, dsaEncoding: 'ieee-p1363' }, signature);
}

// Exactly the five assets, each consistent with its own manifest. A release missing
// any one of them would stall one platform's updater.
export function collectReleaseAssets(directory, version, spki) {
  const windowsManifest = `yahad-windows-${version}.json`;
  const setup = `Yahad.Setup-${version}.exe`;
  const expected = [...LINUX_ASSETS, windowsManifest, `${windowsManifest}.sig`, setup];
  const found = existsSync(directory) ? readdirSync(directory) : [];
  const missing = expected.filter((name) => !found.includes(name) || statSync(join(directory, name)).size === 0);
  if (missing.length) refuse(`Release asset set incomplete; missing or empty: ${missing.join(', ')}.`);
  const extra = found.filter((name) => !expected.includes(name));
  if (extra.length) refuse(`Unexpected release assets: ${extra.join(', ')}.`);
  const read = (name) => readFileSync(join(directory, name));
  const linux = JSON.parse(read('yachad-release.json').toString('utf8'));
  const zip = read('yachad-app.zip');
  if (linux.version !== version || linux.archive !== 'yachad-app.zip' || linux.bytes !== zip.length || linux.sha256 !== sha256Of(zip)) refuse('yachad-release.json does not describe yachad-app.zip at this version.');
  const windows = JSON.parse(read(windowsManifest).toString('utf8'));
  const setupBytes = read(setup);
  if (windows.version !== version || windows.keyId !== keyIdOf(spki) || windows.setup.name !== setup || windows.setup.bytes !== setupBytes.length || windows.setup.sha256 !== sha256Of(setupBytes)) refuse(`${windowsManifest} does not describe ${setup} at this version and key.`);
  if (!signatureVerifies(join(directory, windowsManifest), spki)) refuse(`${windowsManifest}.sig does not verify against the committed key.`);
  return expected.map((name) => ({ name, path: join(directory, name), bytes: statSync(join(directory, name)).size }));
}

// ---- options -----------------------------------------------------------------------------------
const VALUE_OPTIONS = ['out', 'key', 'version', 'node-dir', 'dotnet', 'npm-cache', 'vendors', 'package-cache', 'python', 'pwsh'];
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
// Builds `repo` (a clean checkout of a committed state) and writes <out>/assets with the three
// Windows files. Used directly by `build` and inside the throwaway clones of install/publish.
export function buildSigned({ repo, keyFile, out, inputsOf }) {
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
  if (existsSync(join(repo, 'installer', 'artifacts', 'Yahad.Platform.Bundle'))) {
    refuse('installer/artifacts/Yahad.Platform.Bundle exists from an earlier build; package.ps1 needs a clean checkout. Use `npm run windows:install`, which builds in a fresh clone.');
  }
  const inputs = inputsOf(); // after every refusal above, so those need no build tools
  const version = JSON.parse(readFileSync(join(repo, 'package.json'), 'utf8')).version;
  const packageDirectory = join(resolvedOut, 'package');
  mkdirSync(resolvedOut, { recursive: true });
  const args = ['-NoProfile', '-NonInteractive', '-File', join(repo, 'installer', 'package.ps1')];
  for (const name of ['NodeDirectory', 'NpmCache', 'VendorDirectory', 'DotNet', 'PackageCache', 'Python']) args.push(`-${name}`, inputs[name]);
  args.push('-ReleaseKey', join(repo, COMMITTED_KEY), '-Output', packageDirectory);
  console.log(`Building ${version} from ${commit} ...`);
  if (spawnSync(inputs.pwsh, args, { cwd: repo, stdio: 'inherit' }).status !== 0) refuse('installer/package.ps1 failed; nothing was signed.');
  if (git(repo, 'rev-parse', 'HEAD').trim() !== commit || dirty(repo)) refuse('The checkout changed during the build; nothing was signed.');

  const { manifestName, manifest } = requireBuiltKey(packageDirectory, spki);
  if (manifest.version !== version || manifest.sourceCommit !== commit) refuse(`The manifest says ${manifest.version} at ${manifest.sourceCommit}, expected ${version} at ${commit}.`);
  const assets = join(resolvedOut, 'assets');
  mkdirSync(assets);
  const manifestFile = join(assets, manifestName);
  const setupFile = join(assets, manifest.setup.name);
  copyFileSync(join(packageDirectory, 'release', manifestName), manifestFile);
  copyFileSync(join(packageDirectory, 'release', manifest.setup.name), setupFile);
  const setupBytes = readFileSync(setupFile);
  if (setupBytes.length !== manifest.setup.bytes || sha256Of(setupBytes) !== manifest.setup.sha256) refuse('The Setup does not match its manifest; nothing was signed.');
  execFileSync(process.execPath, [join(repo, 'scripts', 'windows-release-key.mjs'), 'sign', keyFile, manifestFile], { stdio: 'inherit' });
  if (!signatureVerifies(manifestFile, spki)) refuse('The signature does not verify against the committed key; do not publish.');
  return { version, commit, spki, keyId: keyIdOf(spki), assets, manifestFile, setupFile };
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

async function stageRelease({ clone, root, keyFile, inputs }) {
  installDependencies(inputs, clone);
  npm(inputs, clone, 'run', 'build');
  const linux = join(root, 'linux');
  execFileSync(process.execPath, [join(clone, 'scripts', 'release.mjs'), 'package-https', linux], { cwd: clone, stdio: 'inherit' });
  const windows = buildSigned({ repo: clone, keyFile, out: join(root, 'windows'), inputsOf: () => inputs });
  const assets = join(root, 'assets');
  mkdirSync(assets);
  for (const name of LINUX_ASSETS) copyFileSync(join(linux, name), join(assets, name));
  for (const name of readdirSync(windows.assets)) copyFileSync(join(windows.assets, name), join(assets, name));
  return { assets, spki: windows.spki };
}

const typed = async (question) => {
  const rl = createInterface({ input: process.stdin, output: process.stdout });
  try { return (await rl.question(question)).trim(); } finally { rl.close(); }
};

export async function publish({ repo, options, confirm = typed, stage = stageRelease, log = console.log }) {
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
    const { assets, spki } = await stage({ clone, root, keyFile, inputs });
    const files = collectReleaseAssets(assets, version, spki);

    log(`\nRelease ${version} (latest is ${latest})`);
    log(`  commit   ${commit}  (on top of origin/main)`);
    log(`  key id   ${keyIdOf(spki)}`);
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
