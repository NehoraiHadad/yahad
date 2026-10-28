import { test } from 'node:test';
import assert from 'node:assert/strict';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { filterGroups, GroupPicker } from '../web/GroupPicker.js';

test('group search preserves stable IDs, duplicates and granted state through filter and clear', () => {
  const groups = [{ id: 'a@g.us', title: 'משפחה' }, { id: 'b@g.us', title: 'School' }, { id: 'c@g.us', title: 'משפחה' }];
  const grants = [{ user_id: 'child', group_id: 'a@g.us' }, { user_id: 'other-child', group_id: 'b@g.us' }];
  assert.deepEqual(filterGroups(groups, '  SCHOOL  ').map(g => g.id), ['b@g.us']);
  assert.deepEqual(filterGroups(groups, 'משפחה').map(g => g.id), ['a@g.us', 'c@g.us']);
  assert.deepEqual(filterGroups(groups, '  '), groups);
  assert.deepEqual(filterGroups(groups, 'missing'), []);
  // Render with the same persisted grants while one selection is hidden, then
  // clear the filter. Filtering must never rewrite grants or grant the sibling's groups.
  const render = (query: string) => renderToStaticMarkup(React.createElement(GroupPicker, {
    groups: filterGroups(groups, query), grants, childId: 'child', busy: false, loading: false, onToggle() {} }));
  assert.equal((render('School').match(/checked=""/g) || []).length, 0);
  assert.equal((render('').match(/checked=""/g) || []).length, 1);
  assert.deepEqual(grants.map(g => g.group_id), ['a@g.us','b@g.us']);
  const empty = renderToStaticMarkup(React.createElement(GroupPicker, { groups: [], grants: [], childId: '', busy: false, loading: false, onToggle() {} }));
  assert.ok(empty.includes('אין שיחות ברשימה'));
  assert.ok(empty.includes('הוסיפו חשבון ילד'));
});
