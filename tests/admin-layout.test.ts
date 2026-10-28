import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { GroupPicker } from '../web/GroupPicker.js';

// Structural guards, not browser geometry/visual acceptance. In a long list,
// absolute checkboxes must remain inside the list's overflow clipping chain.
const css = readFileSync(new URL('../web/style.css', import.meta.url), 'utf8')
  .replace(/\/\*[\s\S]*?\*\//g, '');
function rules(selector: string): string[] {
  return [...css.matchAll(/([^{}]+)\{([^{}]*)\}/g)]
    .filter(([, selectors]) => selectors.split(',').some(value => value.trim() === selector))
    .map(([, , declarations]) => declarations);
}
function property(selector: string, name: string): string | undefined {
  const values = rules(selector).flatMap(rule => rule.split(';'))
    .map(declaration => declaration.split(':').map(value => value.trim()))
    .filter(([key]) => key === name);
  return values.at(-1)?.[1];
}

test('admin checkbox containing block stays within each scrollable approval row', () => {
  assert.equal(property('.approval-row input', 'position'), 'absolute');
  assert.equal(property('.approval-row', 'position'), 'relative',
    'Without a positioned row the invisible checkbox can escape list clipping and extend page scrolling');
  assert.equal(property('.approval-row input', 'width'), '1px');
  assert.equal(property('.approval-row input', 'height'), '1px');
  assert.notEqual(property('.approval-row', 'overflow'), 'hidden', 'Do not clip the visible keyboard focus ring');
  assert.match(rules('.approval-row input:focus-visible + .custom-check').join(''), /outline:\s*3px/);
});

test('admin group list keeps bounded internal scrolling on desktop and narrow screens', () => {
  const listRules = rules('.scrollable-group-list');
  assert.ok(listRules.length >= 2, 'Keep desktop and narrow-screen limits');
  for (const rule of listRules.filter(rule => /max-height:/.test(rule)))
    assert.match(rule, /max-height:\s*min\(\d+px,\s*\d+dvh\)/);
  assert.equal(property('.scrollable-group-list', 'overflow-y'), 'auto');
  assert.notEqual(property('.parent-page', 'overflow'), 'hidden', 'Real admin content must stay reachable');
});

test('long, filtered, and cleared group lists retain native labelled keyboard controls', () => {
  const groups = Array.from({ length: 180 }, (_, i) => ({ id: `synthetic-${i}@g.us`, title: `שיחה ${i}` }));
  const render = (visible: typeof groups) => renderToStaticMarkup(React.createElement(GroupPicker, {
    groups: visible, grants: [{ user_id: 'synthetic-child', group_id: groups[179].id }],
    childId: 'synthetic-child', busy: false, loading: false, onToggle() {},
  }));
  for (const visible of [groups, [groups[179]], [], groups]) {
    const html = render(visible);
    assert.equal((html.match(/<label class="approval-row /g) || []).length, visible.length);
    assert.equal((html.match(/<input type="checkbox" aria-label="אישור שיחה /g) || []).length, visible.length);
    assert.match(html, /class="approval-list scrollable-group-list"[^>]*tabindex="0"/);
    assert.doesNotMatch(html, /<input[^>]*(?:hidden=|tabindex="-1"|disabled="")/);
    assert.equal((html.match(/checked=""/g) || []).length, visible.length ? 1 : 0);
  }
});

// Tablet instructions must remain available on the device being set up.
test('child login details are not hidden by responsive styles', () => {
  for (const rule of rules('.tablet-login-field'))
    assert.doesNotMatch(rule, /display:\s*none|visibility:\s*hidden/);
  assert.match(rules('.tablet-login-field input').join(''), /min-width:\s*0/);
  assert.match(rules('.tablet-login-field input').join(''), /width:\s*100%/);
  assert.match(rules('.tablet-setup bdi').join(''), /overflow-wrap:\s*anywhere/);
  assert.match(rules('.tablet-setup-steps').join(''), /grid-template-columns:\s*1fr/);
});
