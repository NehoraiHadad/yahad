import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import vm from 'node:vm';
import ts from 'typescript';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { ConnectionGuidance } from '../web/ConnectionGuidanceView';
import { familyProgress, type FamilyOverview } from '../web/FamilyProgress';

// Execute the real Parent render with deterministic hooks, then inspect its
// connection disclosure and the real guidance component's rendered output.
// This is not browser geometry/focus QA.
function parent(sample: { failed?: boolean; stale?: boolean; online?: boolean; loading?: boolean; state?: string; qr?: string | null; paused?: boolean; children?: FamilyOverview['children']; groups?: FamilyOverview['groups']; linked?: boolean; disconnect?: string } = {}) {
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
  const data = sample.loading ? null : { state: sample.state || 'connected', children: sample.children ?? [child], groups: sample.groups ?? [], grants: [], linked: sample.linked, disconnect: sample.disconnect };
  const values: Record<number, unknown> = { 4: !!sample.failed || !!sample.stale, 5: !!sample.failed, 6: data, 7: sample.qr ?? null };
  const module = { exports: {} };
  Object.assign(context, {
    module, exports: module.exports, require: createRequire(new URL('../web/main.tsx', import.meta.url)),
    useState(initial: any) { const i = index++; return [i in values ? values[i] : typeof initial === 'function' ? initial() : initial, () => {}]; },
    useRef: (value: unknown) => ({ current: value }), useEffect() {}, useCallback: (fn: unknown) => fn,
    useBrowserOnline: () => sample.online !== false, readChildSelection: () => 'a',
    familyProgress, location: { origin: 'http://localhost:3000' },
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


test('first setup renders connection before all child/group controls and waits for a confirmed connection', () => {
  for (const state of ['idle', 'connecting', 'qr', 'reconnecting', 'needs_pairing', 'conflict']) {
    const h = parent({ state, children: [], linked: true });
    assert.equal(h.all.some(node => node.type === h.context.ChildManagement), false, state);
    assert.equal(h.all.some(node => node.type === h.context.ChildTabletSetup), false, state);
    assert.ok(h.all.some(node => node.props?.['aria-label'] === 'השלבים הבאים'), state);
    assert.equal(h.details.props.open, true, state);
    const connect = h.all.find(node => node.type === 'button' && node.props.onClick?.toString().includes('/parent/connect'));
    assert.equal(connect.props.disabled, ['connecting', 'reconnecting', 'qr'].includes(state), state);
  }
  const connected = parent({ state: 'connected', children: [] });
  const childIndex = connected.all.findIndex(node => node.type === connected.context.ChildManagement);
  assert.ok(childIndex > connected.all.indexOf(connected.details));
  assert.equal(connected.all.some(node => node.props?.['aria-label'] === 'השלבים הבאים'), false);
  assert.equal(connected.all.find(node => node.type === connected.context.FamilyProgress).props.data.state, 'connected');
});

test('reload and screen re-entry derive setup from server evidence; a linking attempt is not a completed connection', () => {
  for (let entry = 0; entry < 2; entry++) {
    for (const sample of [
      { state: 'idle', children: [] },
      { state: 'connected', children: [] },
      { state: 'reconnecting', children: [], linked: true },
      { state: 'qr', children: [], linked: true },
      { state: 'connecting', children: [], linked: true },
      { state: 'needs_pairing', children: [{ id: 'saved', name: 'Saved synthetic child' }] },
      { state: 'idle', children: [], groups: [{ id: 'saved-group', title: 'Synthetic saved catalog' }] },
    ]) {
      const h = parent(sample);
      assert.equal(h.all.some(node => node.type === h.context.ChildManagement), sample.state === 'connected' || !!sample.children.length || !!sample.groups?.length);
    }
  }
});

test('existing family management remains available through every disconnected state', () => {
  for (const state of ['idle', 'connecting', 'qr', 'reconnecting', 'needs_pairing', 'conflict']) {
    const h = parent({ state });
    const management = h.all.find(node => node.type === h.context.ChildManagement);
    assert.ok(management, state);
    assert.equal(management.props.stale, false, state);
    assert.equal(typeof management.props.onAccess, 'function');
    assert.ok(h.all.indexOf(h.details) < h.all.indexOf(management));
  }
});

test('loading and stale reads do not enable connection mutations or first-child setup', () => {
  for (const sample of [{ loading: true }, { state: 'idle', children: [], failed: true }, { state: 'idle', children: [], online: false }]) {
    const h = parent(sample);
    assert.equal(h.all.some(node => node.type === h.context.ChildManagement), false);
    const connect = h.all.find(node => node.type === 'button' && node.props.onClick?.toString().includes('/parent/connect'));
    assert.equal(connect.props.disabled, true);
  }
  const existing = parent({ state: 'needs_pairing', failed: true });
  assert.equal(existing.all.find(node => node.type === existing.context.ChildManagement).props.stale, true);
});
