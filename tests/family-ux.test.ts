import { test } from 'node:test';
import assert from 'node:assert/strict';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { FamilySetup, suggestUsername } from '../web/FamilySetup.js';
import { readBootstrap } from '../src/bootstrap.js';

test('family setup suggests an editable safe username and gives passphrase autofill accessible labels', () => {
  assert.equal(suggestUsername('Alice Rose'), 'alicerose');
  assert.equal(suggestUsername('אבג'), 'child');
  const html = renderToStaticMarkup(React.createElement(FamilySetup, { onDone() {} }));
  assert.match(html,/aria-labelledby="family-form-title"/);
  assert.match(html,/autoComplete="new-password"/);
  assert.match(html,/autoCapitalize="none"/);
  assert.match(html,/spellCheck="false"/);
  assert.match(html,/minLength="14"/);
  assert.ok(html.includes('יצירה ובחירת קבוצות'));
  assert.equal(readBootstrap(undefined), undefined);
});

test('first-parent setup offers an explicit login exit without submitting account details', () => {
  const html = renderToStaticMarkup(React.createElement(FamilySetup, {
    setupToken: 'synthetic-private-capability', onDone() {}, onClose() {},
  }));
  assert.match(html, /aria-label="מעבר לכניסה"/);
  assert.match(html, /<button type="button">מעבר לכניסה<\/button>/);
  assert.ok(!html.includes('synthetic-private-capability'));
  assert.ok(!html.includes('>אחר כך</button>'));
});

test('child username suggestions avoid visible sibling and parent names', () => {
  assert.equal(suggestUsername('אבג', ['parent', 'child']), 'child2');
  assert.equal(suggestUsername('אבג', ['child', 'child2', 'child3']), 'child4');
  assert.equal(suggestUsername('Alice Rose', ['alicerose', 'alicerose2']), 'alicerose3');
  assert.equal(suggestUsername('Alice', ['ALICE']), 'alice2');
  const html = renderToStaticMarkup(React.createElement(FamilySetup, {
    existingUsernames: ['child', 'child2'], onDone() {},
  }));
  assert.match(html, /id="family-username"[^>]*value="child3"/);
});

test('password settings are a labeled dialog with explicit non-submit close and confirmation', async () => {
  const { AccountSettings } = await import('../web/AccountSettings.js');
  const html = renderToStaticMarkup(React.createElement(AccountSettings, {
    children: [{ id: 'synthetic-child', username: 'child2' }], onClose() {},
  }));
  assert.match(html, /role="dialog" aria-modal="true" aria-label="הגדרות חשבון"/);
  assert.match(html, /<button type="button" class="icon-button" aria-label="סגירה"/);
  assert.match(html, /autoComplete="current-password"/);
  assert.equal((html.match(/autoComplete="new-password"/g) || []).length, 2);
  assert.ok(html.includes('child2'));
});
