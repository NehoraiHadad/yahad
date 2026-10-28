import { test } from 'node:test';
import assert from 'node:assert/strict';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { readFileSync } from 'node:fs';
import { familyProgress } from '../web/FamilyProgress';
import { ChildManagement } from '../web/ChildManagement';
import { FamilySetup } from '../web/FamilySetup';
import { ErrorToast } from '../web/ErrorToast';
import { readChildSelection, saveChildSelection, reconcileChildSelection } from '../web/childManagementState';
import { recoverDialogFocus } from '../web/useDialogFocus';

const data = { state: 'connected', children: [{ id: 'z', name: 'תמר' }, { id: 'b', name: 'אור' }, { id: 'a', name: 'אור' }], groups: [{ id: 'g', title: 'סינתטי' }], grants: [{ user_id: 'a', group_id: 'g' }] };

test('roster ordering is deterministic across server order and uses IDs to break name ties', () => {
  assert.deepEqual(familyProgress(data).children.map(x => x.id), ['a', 'b', 'z']);
  assert.deepEqual(familyProgress({ ...data, children: [...data.children].reverse() }).children.map(x => x.id), ['a', 'b', 'z']);
  assert.deepEqual(data.children.map(x => x.id), ['z', 'b', 'a'], 'does not mutate server snapshot');
});

test('child name is bidi isolated in mixed Hebrew copy and singular grant counts are grammatical', () => {
  const html = renderToStaticMarkup(React.createElement(ChildManagement, {
    data: { ...data, children: [{ id: 'a', name: 'Synthetic Jr.' }] }, selected: 'a', busy: false, stale: false, createdChild: 'a',
    onSelect() {}, onAdd() {}, onSettings() {}, onRefresh() {}, onRetry() {}, onGrant: async () => true,
  }));
  assert.match(html, /השיחות של <bdi>Synthetic Jr\.<\/bdi>/);
  assert.match(html, /שיחה אחת מאושרת/);
  assert.doesNotMatch(html, /1 שיחות מאושרות/);
  assert.match(html, /id="children-title" tabindex="-1"/);
});

test('setup consistently labels password and ties name errors to name input', () => {
  const html = renderToStaticMarkup(React.createElement(FamilySetup, { onDone() {}, onClose() {} }));
  assert.match(html, /for="family-password">סיסמה/);
  assert.match(html, /for="family-confirmation">אישור הסיסמה/);
  assert.match(html, /aria-invalid="false" aria-describedby="account-name-help family-error"/);
  assert.match(readFileSync(new URL('../web/style.css', import.meta.url), 'utf8'), /\.family-form > \.error \{ color:#b14151; \}/);
});

test('toast has a keyboard-operable explicit dismiss button and a bounded lifetime', () => {
  const html = renderToStaticMarkup(React.createElement(ErrorToast, { message: 'synthetic error', onDismiss() {} }));
  assert.match(html, /role="alert"/);
  assert.match(html, /<button type="button" class="icon-button" aria-label="סגירת הודעה"/);
  const source = readFileSync(new URL('../web/ErrorToast.tsx', import.meta.url), 'utf8');
  assert.match(source, /setTimeout\(\(\) => dismiss.current\(\), 8000\)/);
  assert.match(source, /clearTimeout\(timer\)/);
});

test('focus recovers from body or a disabled submitting control without stealing an external focus target', () => {
  const previous = Object.getOwnPropertyDescriptor(globalThis, 'document');
  const body = {}, field = { disabled: false, focused: 0, focus() { this.focused++; } }, disabled = { disabled: true }, outside = {};
  const dialog = { contains: (element: unknown) => element === field || element === disabled, querySelector: () => field };
  Object.defineProperty(globalThis, 'document', { configurable: true, value: { body } });
  try {
    recoverDialogFocus(dialog as unknown as HTMLElement, body as Element);
    assert.equal(field.focused, 1);
    recoverDialogFocus(dialog as unknown as HTMLElement, disabled as unknown as Element);
    assert.equal(field.focused, 2);
    recoverDialogFocus(dialog as unknown as HTMLElement, field as unknown as Element);
    recoverDialogFocus(dialog as unknown as HTMLElement, outside as Element);
    assert.equal(field.focused, 2);
  } finally {
    if (previous) Object.defineProperty(globalThis, 'document', previous);
    else Reflect.deleteProperty(globalThis, 'document');
  }
});

test('desktop add button overrides inherited full width while mobile retains full width', () => {
  const css = readFileSync(new URL('../web/child-management.css', import.meta.url), 'utf8');
  assert.match(css, /\.children-heading \.primary \{ flex:0 0 auto; width:auto;/);
  assert.match(css, /@media\(max-width:760px\)[\s\S]*\.children-heading \.primary \{ width:100%;/);
});

test('remembered selection is account scoped, reconciled with server children, and storage failure is harmless', () => {
  const values = new Map<string, string>();
  const storage = { getItem: (key: string) => values.get(key) || null, setItem: (key: string, value: string) => { values.set(key, value); } };
  saveChildSelection('parent-a', 'z', storage);
  assert.equal(readChildSelection('parent-a', storage), 'z');
  assert.equal(readChildSelection('parent-b', storage), '');
  assert.equal(reconcileChildSelection(data, readChildSelection('parent-a', storage)), 'z');
  assert.equal(reconcileChildSelection({ ...data, children: [] }, 'z'), '');
  assert.equal(readChildSelection('parent-a', { getItem() { throw new Error('blocked'); } }), '');
  assert.doesNotThrow(() => saveChildSelection('parent-a', 'z', { setItem() { throw new Error('blocked'); } }));
});


test('account heading isolates and wraps long names without shrinking its close control', () => {
  const source = readFileSync(new URL('../web/AccountSettings.tsx', import.meta.url), 'utf8');
  assert.match(source, /החשבון של <bdi>\{childLabel\(selected\)\}<\/bdi>/);
  const css = readFileSync(new URL('../web/style.css', import.meta.url), 'utf8');
  assert.match(css, /\.account-settings \.modal-top h2 \{ display:block; flex:1; min-width:0; overflow-wrap:anywhere;/);
  assert.match(css, /\.account-settings \.modal-top \.icon-button \{ flex:0 0 auto;/);
});


test('error notification reserves layout space rather than overlaying composer or navigation', () => {
  const css = readFileSync(new URL('../web/style.css', import.meta.url), 'utf8');
  const source = readFileSync(new URL('../web/main.tsx', import.meta.url), 'utf8');
  assert.match(source, /className="app-shell"[\s\S]*<ErrorToast[\s\S]*className="app-content"/);
  assert.match(css, /\.app-shell \{ height:100dvh; display:flex; flex-direction:column; overflow:hidden;/);
  assert.match(css, /\.app-content \{ flex:1; min-height:0; overflow:auto;/);
  assert.match(css, /\.app-content > \.chat-app \{ height:100%;/);
  assert.match(css, /\.app-shell:has\(\.chat-app\) \{ height:calc\(100dvh - 50px\);/);
  const notice = css.slice(css.indexOf('.toast {'), css.indexOf('.error {'));
  assert.doesNotMatch(notice, /position:\s*(fixed|absolute)|bottom:|transform:/);
  assert.match(notice, /flex: 0 0 auto/);
  assert.match(notice, /max-height:min\(20dvh, 10rem\); overflow:auto; overflow-wrap:anywhere/);
});
