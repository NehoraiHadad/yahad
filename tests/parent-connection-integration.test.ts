import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import vm from 'node:vm';
import ts from 'typescript';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { ConnectionGuidance } from '../web/ConnectionGuidanceView';

// Execute the real Parent render with deterministic hooks, then inspect its
// connection disclosure and the real guidance component's rendered output.
// This is not browser geometry/focus QA.
function parent(sample: { failed?: boolean; online?: boolean; state?: string; qr?: string | null; paused?: boolean } = {}) {
  const source = readFileSync(new URL('../web/main.tsx', import.meta.url), 'utf8');
  const file = ts.createSourceFile('main.tsx', source, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX);
  const declaration = file.statements.find(node => ts.isFunctionDeclaration(node) && node.name?.text === 'Parent');
  assert.ok(declaration);
  const context: Record<string, any> = {};
  const tags = (node: ts.Node) => {
    if ((ts.isJsxOpeningElement(node) || ts.isJsxSelfClosingElement(node)) && ts.isIdentifier(node.tagName) && /^[A-Z]/.test(node.tagName.text))
      context[node.tagName.text] = () => null;
    ts.forEachChild(node, tags);
  };
  tags(declaration);
  let index = 0;
  const child = { id: 'a', name: 'Synthetic child', paused: !!sample.paused, accessRevision: sample.paused ? 1 : 0 };
  const data = { state: sample.state || 'connected', children: [child], groups: [], grants: [] };
  const values: Record<number, unknown> = { 4: !!sample.failed, 5: !!sample.failed, 6: data, 7: sample.qr ?? null };
  const module = { exports: {} };
  Object.assign(context, {
    module, exports: module.exports, require: createRequire(new URL('../web/main.tsx', import.meta.url)),
    useState(initial: any) { const i = index++; return [i in values ? values[i] : typeof initial === 'function' ? initial() : initial, () => {}]; },
    useRef: (value: unknown) => ({ current: value }), useEffect() {}, useCallback: (fn: unknown) => fn,
    useBrowserOnline: () => sample.online !== false, readChildSelection: () => 'a',
    familyProgress: () => ({ completed: 3, children: [child] }), location: { origin: 'http://localhost:3000' },
  });
  const code = ts.transpileModule(declaration.getText(file) + '\nmodule.exports=Parent;', {
    compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS, jsx: ts.JsxEmit.ReactJSX },
  }).outputText;
  vm.runInNewContext(code, context);
  const tree = context.module.exports({ me: { user: { id: 'synthetic-parent' }, connector: 'mock' }, logout() {}, openChat() {}, onError() {} });
  const nodes = (node: any): any[] => !node || typeof node !== 'object' ? [] : [node, ...[node.props?.children].flat(Infinity).flatMap(nodes)];
  const all = nodes(tree), details = all.find(node => node.type === 'details' && node.props.className === 'parent-connection-details');
  const guidance = all.find(node => node.type === context.ConnectionGuidance);
  const summary = nodes(details).find(node => node.type === 'summary');
  const pill = nodes(details).find(node => node.props?.className?.startsWith('state-pill'));
  return { details, guidance, summary, pill, all, context,
    html: () => renderToStaticMarkup(React.createElement(ConnectionGuidance, guidance.props)) };
}

test('a failed overview or offline browser exposes guidance despite the last connected state', () => {
  for (const sample of [{ failed: true }, { online: false }]) {
    const h = parent(sample);
    assert.equal(h.details.props.open, true);
    const summary = renderToStaticMarkup(h.summary);
    assert.match(summary, /נדרשת בדיקה/); assert.doesNotMatch(summary, /מחובר/);
    assert.ok(!h.pill.props.className.split(' ').includes('connected'));
    assert.equal(h.all.some(node => node.props?.className === 'connected-note'), false);
    assert.match(h.html(), new RegExp(sample.failed ? 'server_no_answer' : 'device_offline'));
  }
});

test('a healthy connected overview remains quiet and collapsed', () => {
  const h = parent();
  assert.equal(h.details.props.open, false);
  assert.match(renderToStaticMarkup(h.summary), /מחובר/);
  assert.ok(h.pill.props.className.split(' ').includes('connected'));
  assert.equal(h.html(), '');
});

test('Parent passes actual QR visibility and retains child access management while QR is unavailable', () => {
  const missing = parent({ state: 'qr', paused: true });
  assert.equal(missing.details.props.open, true);
  assert.equal(missing.guidance.props.input.qrVisible, false);
  assert.match(missing.html(), /qr_pending/);
  assert.match(missing.html(), /קוד ה־QR עדיין לא זמין/);
  assert.doesNotMatch(missing.html(), /סרקו|מכשירים מקושרים|לקשר מכשיר/);
  assert.equal(missing.all.some(node => node.type === 'img' && node.props.src), false);
  const controls = missing.all.find(node => node.type === missing.context.ChildManagement);
  assert.equal(typeof controls.props.onAccess, 'function');
  assert.equal(controls.props.data.children[0].paused, true);
  const visible = parent({ state: 'qr', qr: 'data:synthetic-qr' });
  assert.equal(visible.guidance.props.input.qrVisible, true);
  assert.match(visible.html(), /scan_qr/);
  assert.ok(visible.all.some(node => node.type === 'img' && node.props.src === 'data:synthetic-qr'));
});
