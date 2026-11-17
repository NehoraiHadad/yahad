import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

// installer/ci-build-upgrade-set.ps1 builds the CI upgrade variants by patching
// source text at fixed anchors. An anchor that drifts fails only deep inside the
// Windows CI run; this reads the same anchors from the script and checks each
// still occurs exactly once in the file it patches.
const root = new URL('../', import.meta.url);
const read = (path: string) => readFileSync(new URL(path, root), 'utf8');
const script = read('installer/ci-build-upgrade-set.ps1');
const count = (text: string, anchor: string) => text.split(anchor).length - 1;
const anchor = (pattern: RegExp, name: string) => {
  const found = script.match(pattern);
  assert.ok(found, `${name} anchor is no longer declared in the script`);
  return found[1];
};

test('every CI upgrade-set source anchor occurs exactly once', () => {
  const anchors: [string, string][] = [
    ['src/server.ts', anchor(/\$ready = '([^']+)'/, 'readiness')],
    ['src/main.ts', anchor(/ReplaceOnce \(Join-Path \$Tree 'src\/main\.ts'\) "([^"]+)"/, 'startup exit')],
    ['src/store.ts', anchor(/BumpSchema \$Tree \$schema '([^']+)'/, 'migration')],
  ];
  const help = read('deploy/lan-help.html');
  if (!help.includes('<meta name="yahad-ci-app"')) anchors.push(['deploy/lan-help.html', anchor(/ReplaceOnce \$page '([^']+)'/, 'help page')]);
  for (const [path, text] of anchors) assert.equal(count(read(path), text), 1, `${path}: ${text}`);
});

test('the unhealthy variant refers only to names in scope at its anchor', () => {
  const ready = anchor(/\$ready = '([^']+)'/, 'readiness');
  const guard = anchor(/ReplaceOnce \(Join-Path \$Tree 'src\/server\.ts'\) \$ready "if \((\w+)\)/, 'unhealthy guard');
  const server = read('src/server.ts'), at = server.indexOf(ready);
  const route = server.slice(server.lastIndexOf("app.get('/health/ready'", at), at);
  assert.match(route, new RegExp(`\\b(const|let) ${guard}\\b`), `${guard} is not declared in the readiness route`);
});
