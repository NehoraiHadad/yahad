// Windows payload server build. Bundles each fixed entry into one self-contained
// ESM file so the family machine extracts, ACL-copies and hashes three files
// instead of src/ plus a production node_modules tree and a runtime TS loader.
// Linux remains source-based and does not use this script.
//
// Use: node scripts/build-server.mjs --payload NEW_DIR [--root CHECKOUT]
// --root holds src/, tsconfig.json and a lockfile-restored node_modules (default:
// this checkout). Writes NEW_DIR/app/server/*.mjs, NEW_DIR/legal/server-bundle.json
// (provenance + third-party inventory) and NEW_DIR/legal/ThirdPartyNotices-server.txt.
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { createRequire, isBuiltin } from 'node:module';
import { existsSync, lstatSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { basename, dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const args = process.argv.slice(2), option = name => { const at = args.indexOf(name); return at >= 0 ? args[at + 1] : undefined; };
const payload = option('--payload'), root = resolve(option('--root') ?? join(dirname(fileURLToPath(import.meta.url)), '..'));
if (!payload || args.length !== (option('--root') ? 4 : 2)) throw new Error('Use: node scripts/build-server.mjs --payload NEW_DIR [--root CHECKOUT]');
// Entry → output stem. The migration and supervisor are separate processes with
// fixed payload paths, so they are never split into shared chunks.
const ENTRIES = { 'src/main.ts': 'main', 'src/native-migrate-v1.ts': 'native-migrate-v1', 'src/native-supervisor.ts': 'native-supervisor' };
// Optional packages, left unresolved and never shipped, each allowed only through the
// import kind its caller guards. Baileys reaches its media helpers through
// `import(...).catch()`: link previews are disabled, image/video thumbnails come
// from FFmpeg and profile pictures are never set, so sharp's native libvips is dead
// weight. ws's native accelerators and debug's colour probe are require()d in
// try/catch and are absent from the lockfile.
const OPTIONAL = { 'audio-decode': 'dynamic-import', jimp: 'dynamic-import', 'link-preview-js': 'dynamic-import', sharp: 'dynamic-import',
  bufferutil: 'require-call', 'utf-8-validate': 'require-call', 'supports-color': 'require-call' };
const EXTERNAL = Object.keys(OPTIONAL).sort();
// Bundled CommonJS still calls require() for builtins and reads __dirname.
const BANNER = "import { createRequire as __yahadRequire } from 'node:module'; import { fileURLToPath as __yahadPath } from 'node:url'; import { dirname as __yahadDirname } from 'node:path'; " +
  'const require = __yahadRequire(import.meta.url); const __filename = __yahadPath(import.meta.url); const __dirname = __yahadDirname(__filename);';
const NOTICE = /^(?:licen[cs]e|copying|notice|copyright)/i;
const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');
const fail = message => { throw new Error('Server bundle refused: ' + message); };

// The CLI, not the JS API: the API's service process is detached and outlives this
// script, so a caller deleting the build tree right after exit races a still-running
// esbuild.exe that Windows refuses to delete. The CLI shim waits for the binary.
const cli = createRequire(join(root, 'package.json')).resolve('esbuild/bin/esbuild');
const esbuild = args => {
  const run = spawnSync(process.execPath, [cli, ...args], { cwd: root, encoding: 'utf8', stdio: ['ignore', 'pipe', 'inherit'] });
  if (run.error || run.status !== 0) fail(`esbuild exited ${run.status ?? run.error}`);
  return run.stdout;
};
const version = esbuild(['--version']).trim();
const pinned = JSON.parse(readFileSync(join(root, 'package.json'), 'utf8')).devDependencies?.esbuild;
if (version !== pinned) fail(`esbuild ${version} is not the pinned ${pinned}`);
const app = resolve(payload, 'app'), legal = resolve(payload, 'legal');
if (existsSync(join(app, 'server'))) fail('app/server already exists');
const staging = mkdtempSync(join(tmpdir(), 'yahad-server-bundle-'));
try {
esbuild([...Object.entries(ENTRIES).map(([source, stem]) => `${stem}=${source}`),
  '--bundle', '--platform=node', '--format=esm', '--target=node24', `--outdir=${staging}`, '--out-extension:.js=.mjs',
  ...EXTERNAL.map(name => `--external:${name}`), `--banner:js=${BANNER}`, '--legal-comments=external',
  `--metafile=${join(staging, 'meta.json')}`, '--log-level=warning', '--charset=utf8']);
const result = { metafile: JSON.parse(readFileSync(join(staging, 'meta.json'), 'utf8')) };
const built = path => readFileSync(resolve(root, path));

// Every input is checked-in source or a lockfile-restored package; nothing else.
const packages = new Map(), sources = new Map();
for (const input of Object.keys(result.metafile.inputs)) {
  if (/^src\/[A-Za-z0-9/_.-]+\.ts$/.test(input) && !input.includes('..')) { sources.set(input, sha256(readFileSync(join(root, input)))); continue; }
  const match = /^((?:node_modules\/(?:@[^/]+\/)?[^/@][^/]*\/)+)/.exec(input);
  if (!match || input.includes('/../')) fail('unexpected input ' + input);
  const path = match[1].slice(0, -1);
  if (packages.has(path)) continue;
  const manifest = JSON.parse(readFileSync(join(root, path, 'package.json'), 'utf8'));
  if (!manifest.name || !manifest.version || !path.endsWith('node_modules/' + manifest.name)) fail('package identity ' + path);
  const notices = readdirSync(join(root, path)).filter(name => NOTICE.test(name) && lstatSync(join(root, path, name)).isFile()).sort();
  packages.set(path, { path, name: manifest.name, version: manifest.version,
    declaredLicense: typeof manifest.license === 'string' ? manifest.license : manifest.license?.type ?? null,
    notices: notices.map(name => { const bytes = readFileSync(join(root, path, name)); return { name, sha256: sha256(bytes), text: bytes.toString('utf8') }; }) });
}
for (const source of Object.keys(ENTRIES)) if (!sources.has(source)) fail('entry not bundled ' + source);

// Optional packages are tolerated only in the server and only behind their guarded
// import kind, so startup never needs them. The migration and supervisor run with
// Node builtins alone.
const outputs = [], noticesByOutput = [];
for (const [path, output] of Object.entries(result.metafile.outputs)) {
  if (path.endsWith('.LEGAL.txt')) continue;
  const stem = basename(path, '.mjs'), entry = output.entryPoint;
  if (ENTRIES[entry] !== stem) fail('unexpected output ' + path);
  for (const item of output.imports) {
    if (!item.external) fail('internal chunk import ' + item.path);
    if (isBuiltin(item.path) || stem === 'main' && OPTIONAL[item.path] === item.kind) continue;
    fail(`${stem} imports ${item.kind} ${item.path}`);
  }
  const contents = built(path);
  outputs.push({ path: 'app/server/' + stem + '.mjs', entry, contents, size: contents.length, sha256: sha256(contents),
    sources: Object.keys(output.inputs).filter(name => sources.has(name)).sort(),
    packages: [...new Set(Object.keys(output.inputs).filter(name => !sources.has(name)).map(name => [...packages.keys()].find(p => name.startsWith(p + '/'))))].sort() });
  const legalFile = existsSync(resolve(root, path) + '.LEGAL.txt') && { text: built(path + '.LEGAL.txt').toString('utf8') };
  if (legalFile) noticesByOutput.push(`Extracted legal comments in ${stem}.mjs\n\n${legalFile.text}`);
}
if (outputs.length !== Object.keys(ENTRIES).length) fail('missing output');
outputs.sort((a, b) => a.path.localeCompare(b.path));

mkdirSync(join(app, 'server'), { recursive: true }); mkdirSync(legal, { recursive: true });
for (const output of outputs) writeFileSync(join(payload, output.path), output.contents, { flag: 'wx' });
const inventory = [...packages.values()].sort((a, b) => a.path.localeCompare(b.path));
const rule = '='.repeat(78);
const notices = ['Third-party software bundled into app/server/*.mjs. Each package keeps its own license;',
  'the texts below are copied verbatim from the package root as published to npm.', '',
  ...inventory.flatMap(item => [rule, `${item.name} ${item.version} (${item.path})`, `Declared license: ${item.declaredLicense ?? 'none declared'}`, rule, '',
    ...(item.notices.length ? item.notices.flatMap(n => [`--- ${n.name} ---`, n.text.trimEnd(), '']) : ['No license file is included in the published package.', ''])]),
  ...noticesByOutput];
writeFileSync(join(legal, 'ThirdPartyNotices-server.txt'), notices.join('\n') + '\n', { flag: 'wx' });
writeFileSync(join(legal, 'server-bundle.json'), JSON.stringify({
  format: 'yahad-server-bundle', schema: 1, bundler: { name: 'esbuild', version }, external: EXTERNAL, outputs: outputs.map(({ contents, ...output }) => output),
  sources: Object.fromEntries([...sources].sort()),
  packages: inventory.map(({ notices, ...item }) => ({ ...item, noticeFiles: notices.map(({ name, sha256 }) => ({ name, sha256 })) })),
  notices: 'legal/ThirdPartyNotices-server.txt', completeLicenseReview: false,
  scope: 'Every npm package with code in the server bundle, with its package-root legal files; not a complete transitive legal audit.',
}, null, 2) + '\n', { flag: 'wx' });
console.log(JSON.stringify({ outputs: outputs.map(o => ({ path: o.path, size: o.size })), sources: sources.size, packages: inventory.length }));
} finally { rmSync(staging, { recursive: true, force: true }); }
