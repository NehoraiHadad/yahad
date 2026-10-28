import { test } from 'node:test';
import assert from 'node:assert/strict';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { AccountSettings } from '../web/AccountSettings.js';
import { ChildGroupSummary, FamilyProgress, type FamilyOverview } from '../web/FamilyProgress.js';

const children = [
  { id: 'unique-id', name: 'יעל' },
  { id: 'second-id', name: 'נועם לוי' },
  { id: 'third-id', name: 'נועם כהן' },
];
const overview = (): FamilyOverview => ({ state: 'connected', children, groups: [], grants: [] });

test('account selector shows the sole canonical name and preserves account IDs', () => {
  const html = renderToStaticMarkup(React.createElement(AccountSettings, { children, onClose() {} }));
  for (const child of children) assert.ok(html.includes(`<option value="${child.id}">${child.name}</option>`));
  assert.ok(!html.includes('שם לתצוגה'));
});

test('child summary selection buttons and headings use consistent unambiguous labels', () => {
  const html = renderToStaticMarkup(React.createElement(ChildGroupSummary, {
    data: overview(), selected: 'third-id', busy: false, onSelect() {},
  }));
  assert.ok(html.includes('<h3>יעל</h3>'));
  for (const label of ['יעל', 'נועם לוי', 'נועם כהן']) {
    assert.ok(html.includes(`בחירת שיחות עבור ${label}`), label);
  }
  assert.equal((html.match(/aria-pressed="true"/g) || []).length, 1);
});

test('setup next action identifies the pending child by the same canonical name', () => {
  const data = overview();
  data.groups = [{ id: 'group', title: 'שיחה סינתטית' }];
  data.grants = [{ user_id: 'unique-id', group_id: 'group' }];
  const html = renderToStaticMarkup(React.createElement(FamilyProgress, {
    data, busy: false, stale: false, onAdd() {}, onSelect() {},
  }));
  assert.ok(html.includes('הצעד הבא: בחירת שיחות עבור נועם כהן'));
});
