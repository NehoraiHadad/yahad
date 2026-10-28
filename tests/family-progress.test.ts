import { test } from 'node:test';
import assert from 'node:assert/strict';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { FamilyProgress, ChildGroupSummary, familyProgress, type FamilyOverview } from '../web/FamilyProgress.js';

const overview = (): FamilyOverview => ({ state: 'connected', children: [
  { id: 'a', name: 'ילד לדוגמה' },
  { id: 'b', name: 'ילדה לדוגמה' },
], groups: [{ id: 'g1', title: 'משפחה' }, { id: 'g2', title: 'משפחה' }, { id: 'g3', title: 'כיתה' }],
  grants: [{ user_id: 'a', group_id: 'g1' }, { user_id: 'a', group_id: 'g2' }, { user_id: 'b', group_id: 'g3' }] });

test('setup is derived from persisted overview after interruptions/reload, never local completion flags', () => {
  const data = overview();
  assert.equal(familyProgress(data).next, 'login');
  assert.equal(familyProgress(data).completed, 3);
  data.children.push({ id: 'c', name: 'חדש' });
  const interrupted = familyProgress(JSON.parse(JSON.stringify(data)));
  assert.equal(interrupted.next, 'groups');
  assert.equal(interrupted.pendingChild?.id, 'c');
  assert.equal(interrupted.completed, 2);
  data.grants.push({ user_id: 'c', group_id: 'g3' });
  assert.equal(familyProgress(data).next, 'login');
  data.grants = data.grants.filter(grant => grant.user_id !== 'c');
  assert.equal(familyProgress(data).next, 'groups');
});

test('zero groups, zero children and unknown/stale grants cannot mark a child ready', () => {
  const data = overview();
  data.grants = [{ user_id: 'a', group_id: 'removed' }, { user_id: 'other', group_id: 'g1' }];
  assert.ok(familyProgress(data).children.every(child => !child.ready));
  data.groups = [];
  assert.equal(familyProgress(data).next, 'groups');
  data.children = [];
  assert.equal(familyProgress(data).next, 'child');
  assert.equal(familyProgress(data).completed, 1);
});

test('reconnection is distinct from saved grants and readiness is never tablet connection evidence', () => {
  const data = overview();
  for (const state of ['idle', 'qr', 'connecting', 'reconnecting', 'needs_pairing', 'conflict']) {
    data.state = state;
    assert.equal(familyProgress(data).next, 'connect');
    assert.ok(familyProgress(data).children.every(child => !child.ready));
    assert.equal(familyProgress(data).children[0].groups.length, 2);
  }
  data.state = 'connected'; data.disconnect = 'pending';
  assert.equal(familyProgress(data).next, 'connect');
  const html = renderToStaticMarkup(React.createElement(FamilyProgress, { data: overview(), busy: false, stale: false, onAdd() {}, onSelect() {} }));
  assert.ok(html.includes('מוכנות לכניסה אינה אישור שהטאבלט התחבר'));
  assert.ok(html.includes('href="#family-tablet"'));
});

test('summary lists authorized titles per child, preserves duplicate titles and escapes synthetic content', () => {
  const data = overview();
  data.groups[0].title = '<script>synthetic</script>';
  data.grants.push({ user_id: 'a', group_id: 'g1' }); // repeated approval cannot duplicate a group
  const progress = familyProgress(data);
  assert.deepEqual(progress.children[0].groups.map(g => g.id).sort(), ['g1', 'g2']);
  assert.deepEqual(progress.children[1].groups.map(g => g.id), ['g3']);
  const html = renderToStaticMarkup(React.createElement(ChildGroupSummary, { data, selected: 'b', busy: true, onSelect() {} }));
  assert.ok(html.includes('&lt;script&gt;synthetic&lt;/script&gt;'));
  assert.ok(!html.includes('<script>'));
  assert.ok(html.includes('aria-pressed="true"'));
  assert.equal((html.match(/disabled=""/g) || []).length, 2);
  assert.ok(!html.includes('/messages'));
  assert.ok(!html.includes('/login'));
});

test('empty and stale setup surfaces next action without enabling duplicate account creation', () => {
  const data = overview(); data.children = []; data.grants = [];
  const html = renderToStaticMarkup(React.createElement(FamilyProgress, { data, busy: false, stale: true, onAdd() {}, onSelect() {} }));
  assert.ok(html.includes('הצעד הבא: הוספת ילד או ילדה'));
  assert.ok(html.includes('disabled=""'));
  assert.ok(html.includes('המידע ממתין לרענון'));
});

test('stale summaries do not claim current login readiness', () => {
  const html = renderToStaticMarkup(React.createElement(ChildGroupSummary, {
    data: overview(), selected: 'a', busy: false, stale: true, onSelect() {},
  }));
  assert.ok(!html.includes('מוכן לכניסה בטאבלט'));
  assert.ok(html.includes('ממתינים לאימות מול השרת'));
});
