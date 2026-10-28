import { test } from 'node:test';
import assert from 'node:assert/strict';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { readFileSync } from 'node:fs';
import { ChildManagement } from '../web/ChildManagement';
import { AccountSettings } from '../web/AccountSettings';
import { ChildActionGuard, confirmedChildGrant, reconcileChildSelection } from '../web/childManagementState';
import type { FamilyOverview } from '../web/FamilyProgress';

const overview = (): FamilyOverview => ({ state: 'connected', children: [
  { id: 'a', name: 'יעל לדוגמה' }, { id: 'b', name: 'נועם לדוגמה' },
], groups: [{ id: 'one', title: 'משפחה סינתטית' }, { id: 'two', title: 'כיתה סינתטית' }],
grants: [{ user_id: 'a', group_id: 'one' }, { user_id: 'b', group_id: 'two' }] });
const props = () => ({ data: overview(), selected: 'a', busy: false, stale: false, createdChild: '',
  onSelect() {}, onAdd() {}, onSettings() {}, onRefresh() {}, onRetry() {}, onGrant: async () => true });

test('child workspace identifies roster, selected name, login name, per-child counts and direct settings', () => {
  const html = renderToStaticMarkup(React.createElement(ChildManagement, props()));
  assert.match(html, /aria-label="בחירת ילד או ילדה"/);
  assert.equal((html.match(/class="child-card"/g) || []).length, 2);
  assert.match(html, /id="selected-child-title" dir="auto">יעל לדוגמה/);
  assert.match(html, /שם לכניסה: <b dir="auto">יעל לדוגמה/);
  assert.match(html, /השיחות של <bdi>יעל לדוגמה<\/bdi>/);
  assert.match(html, /aria-label="אישור שיחה משפחה סינתטית עבור יעל לדוגמה" checked=""/);
  assert.doesNotMatch(html, /aria-label="אישור שיחה כיתה סינתטית עבור יעל לדוגמה" checked/);
  for (const text of ['הוספת ילד או ילדה', 'שינוי שם', 'איפוס סיסמה', 'מאושרות (1)', 'להוספה (1)']) assert.ok(html.includes(text), text);
  assert.ok(!html.includes('האפליקציה מוחקת'));
});

test('switching selected child renders only that child grants, without copying sibling approvals', () => {
  const html = renderToStaticMarkup(React.createElement(ChildManagement, { ...props(), selected: 'b' }));
  assert.match(html, /id="selected-child-title" dir="auto">נועם לדוגמה/);
  assert.match(html, /aria-label="אישור שיחה כיתה סינתטית עבור נועם לדוגמה" checked=""/);
  assert.doesNotMatch(html, /aria-label="אישור שיחה משפחה סינתטית עבור נועם לדוגמה" checked/);
});

test('loading, failed load, zero children, created child and stale snapshots have explicit safe states', () => {
  const render = (overrides: Partial<React.ComponentProps<typeof ChildManagement>>) => renderToStaticMarkup(React.createElement(ChildManagement, { ...props(), ...overrides }));
  assert.ok(render({ data: null }).includes('טוענים את הילדים והשיחות שלהם'));
  const failed = render({ data: null, stale: true });
  assert.ok(failed.includes('לא הצלחנו לטעון את הילדים')); assert.ok(failed.includes('ניסיון נוסף'));
  const empty = render({ data: { ...overview(), children: [], grants: [] }, selected: '' });
  assert.ok(empty.includes('הוספת החשבון הראשון')); assert.ok(!empty.includes('אישור שיחה'));
  assert.ok(render({ createdChild: 'a' }).includes('החשבון של <bdi>יעל לדוגמה</bdi> נוצר ונבחר'));
  const stale = render({ stale: true });
  assert.ok(!stale.includes('מוכן לכניסה בטאבלט'));
  assert.equal((stale.match(/type="checkbox"[^>]*disabled=""/g) || []).length, 2);
  assert.match(stale, /disabled=""[^>]*>[\s\S]*?הוספת ילד או ילדה/);
});

test('selection survives reorder/rename/refresh but chooses a pending known child if removed', () => {
  const data = overview();
  assert.equal(reconcileChildSelection(data, 'b'), 'b');
  data.children.reverse(); data.children[0].name = 'שם חדש';
  assert.equal(reconcileChildSelection(data, 'b'), 'b');
  data.children.push({ id: 'new', name: 'חדש' });
  assert.equal(reconcileChildSelection(data, 'missing'), 'new');
  data.grants.push({ user_id: 'new', group_id: 'removed-group' });
  assert.equal(reconcileChildSelection(data, 'missing'), 'new');
  assert.equal(reconcileChildSelection({ ...data, children: [] }, 'a'), '');
});

test('confirmation needs the exact child/group in a fresh server snapshot, including revoke', () => {
  const data = overview(), grant = { childId: 'a', groupId: 'one', allowed: true };
  assert.equal(confirmedChildGrant(data, grant, false), true);
  assert.equal(confirmedChildGrant(data, grant, true), false);
  assert.equal(confirmedChildGrant(data, { ...grant, childId: 'b' }, false), false);
  assert.equal(confirmedChildGrant(data, { ...grant, allowed: false }, false), false);
  data.grants = data.grants.filter(item => item.user_id !== 'a');
  assert.equal(confirmedChildGrant(data, { ...grant, allowed: false }, false), true);
  assert.equal(confirmedChildGrant(data, { ...grant, childId: 'deleted', allowed: false }, false), false);
  assert.equal(confirmedChildGrant(data, { ...grant, groupId: 'deleted', allowed: false }, false), false);
});

test('repeated clicks are rejected synchronously and stale completions cannot finish newer work', () => {
  const guard = new ChildActionGuard(), first = guard.begin()!;
  assert.equal(guard.begin(), null);
  guard.invalidate(); // switching account or unmounting/cancelling
  const second = guard.begin()!;
  assert.equal(guard.finish(first), false);
  assert.equal(guard.begin(), null);
  assert.equal(guard.finish(second), true);
  assert.notEqual(guard.begin(), null);
});

test('direct password action opens the selected child password form, never the parent form', () => {
  const html = renderToStaticMarkup(React.createElement(AccountSettings, {
    children: overview().children, initialChildId: 'b', initialMode: 'password', onClose() {},
  }));
  assert.match(html, /<option value="b" selected="">נועם לדוגמה/);
  assert.ok(html.includes('סיסמה חדשה'));
  assert.ok(!html.includes('סיסמה נוכחית'));
  assert.equal((html.match(/autoComplete="new-password"/g) || []).length, 2);
});

test('a removed initial child never silently falls back to changing the parent account', () => {
  const html = renderToStaticMarkup(React.createElement(AccountSettings, {
    children: overview().children, initialChildId: 'removed-child', initialMode: 'password', onClose() {},
  }));
  assert.ok(html.includes('החשבון הזה כבר אינו זמין'));
  assert.ok(!html.includes('סיסמה נוכחית'));
  assert.ok(!html.includes('הגדרות החשבון שלי'));
  assert.match(html, /class="primary" disabled=""/);
});

test('long synthetic names are escaped and scoped layout preserves responsive native controls', () => {
  const p = props(); p.data.children[0].name = '<script>synthetic</script>';
  const html = renderToStaticMarkup(React.createElement(ChildManagement, p));
  assert.ok(html.includes('&lt;script&gt;synthetic&lt;/script&gt;'));
  assert.ok(!html.includes('<script>'));
  const css = readFileSync(new URL('../web/child-management.css', import.meta.url), 'utf8');
  assert.match(css, /@media\(max-width:760px\)/);
  assert.match(css, /grid-template-columns:1fr/);
  assert.match(css, /text-align:start/);
  assert.match(css, /focus-visible/);
  assert.match(css, /max-height:min\(720px,75dvh\)/);
  assert.match(css, /max-height:min\(360px,45dvh\)/);
  assert.match(html, /<ul tabindex="0" aria-label="רשימת הילדים">/);
  const source = readFileSync(new URL('../web/main.tsx', import.meta.url), 'utf8');
  assert.ok(source.indexOf("import './style.css'") < source.indexOf("import './child-management.css'"));
  assert.ok(source.includes('key={child} childId={child} disabled={busy || overviewStale}'));
});

test('maximum-sized family keeps every child reachable in a bounded keyboard-scrollable roster', () => {
  const p = props(); p.data.children = Array.from({ length: 20 }, (_, i) => ({ id: `child-${i}`, name: `ילד סינתטי ${i}` }));
  p.selected = 'child-19';
  const html = renderToStaticMarkup(React.createElement(ChildManagement, p));
  assert.equal((html.match(/class="child-card"/g) || []).length, 20);
  assert.match(html, /הרשימה נגללת להצגת כל הילדים/);
  assert.match(html, /id="selected-child-title" dir="auto">ילד סינתטי 19/);
});
