import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import ts from 'typescript';

test('client UUIDs work with getRandomValues when randomUUID is unavailable on a LAN HTTP origin', () => {
  const exports: any = {}; let calls = 0;
  const code = ts.transpileModule(readFileSync(new URL('../web/clientId.ts', import.meta.url), 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
  }).outputText;
  vm.runInNewContext(code, { exports, crypto: { getRandomValues(bytes: Uint8Array) { bytes.fill(++calls); return bytes; } } });
  const a = exports.newClientId(), b = exports.newClientId();
  assert.match(a, /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/);
  assert.notEqual(a, b); assert.equal(calls, 2);
});
