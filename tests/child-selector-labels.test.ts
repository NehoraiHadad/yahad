import { test } from 'node:test';
import assert from 'node:assert/strict';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { AccountSettings } from '../web/AccountSettings.js';
import { ChildGroupSummary, FamilyProgress, type FamilyOverview } from '../web/FamilyProgress.js';

const children = [
  { id: 'unique-id', display: 'יעל', username: 'synthetic_unique' },
  { id: 'duplicate-a', display: 'נועם', username: 'synthetic_a' },
  { id: 'duplicate-b', display: 'נועם', username: 'synthetic_b' },
  { id: 'fallback-id', display: '', username: 'synthetic_fallback' },
];
const overview = (): FamilyOverview => ({ state: 'connected', children, groups: [], grants: [] });

test('account selector shows display names, disambiguates duplicates and preserves account IDs', () => {
  const html = renderToStaticMarkup(React.createElement(AccountSettings, { children, onClose() {} }));
  assert.match(html, /<option value="unique-id">יעל<\/option>/);
  assert.match(html, /<option value="duplicate-a">נועם \(synthetic_a\)<\/option>/);
  assert.match(html, /<option value="duplicate-b">נועם \(synthetic_b\)<\/option>/);
  assert.match(html, /<option value="fallback-id">synthetic_fallback<\/option>/);
  assert.ok(!html.includes('synthetic_unique'));
});

test('child summary selection buttons and headings use consistent unambiguous labels', () => {
  const html = renderToStaticMarkup(React.createElement(ChildGroupSummary, {
    data: overview(), selected: 'duplicate-b', busy: false, onSelect() {},
  }));
  assert.ok(html.includes('<h3>יעל</h3>'));
  assert.ok(!html.includes('synthetic_unique'));
  for (const label of ['יעל', 'נועם (synthetic_a)', 'נועם (synthetic_b)', 'synthetic_fallback']) {
    assert.ok(html.includes(`בחירת קבוצות עבור ${label}`), label);
  }
  assert.equal((html.match(/aria-pressed="true"/g) || []).length, 1);
});

test('setup next action identifies the pending child when display names collide', () => {
  const data = overview();
  data.groups = [{ id: 'group', title: 'קבוצה סינתטית' }];
  data.grants = [{ user_id: 'unique-id', group_id: 'group' }];
  const html = renderToStaticMarkup(React.createElement(FamilyProgress, {
    data, busy: false, stale: false, onAdd() {}, onSelect() {},
  }));
  assert.ok(html.includes('הצעד הבא: בחירת קבוצות עבור נועם (synthetic_a)'));
});
