import { test } from 'node:test';
import assert from 'node:assert/strict';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { FamilySetup } from '../web/FamilySetup.js';
import { readBootstrap } from '../src/bootstrap.js';

test('family setup has one Hebrew-capable account name and accessible passphrase labels', () => {
  const html = renderToStaticMarkup(React.createElement(FamilySetup, { onDone() {} }));
  assert.match(html,/aria-labelledby="family-form-title"/);
  assert.match(html,/autoComplete="new-password"/);
  assert.match(html,/autoCapitalize="none"/);
  assert.match(html,/spellCheck="false"/);
  assert.match(html,/minLength="4"/);
  assert.ok(html.includes('יצירה ובחירת קבוצות'));
  assert.ok(html.includes('שם אחד שמופיע באפליקציה וגם משמש לכניסה'));
  assert.ok(html.includes('dir="auto"'));
  assert.equal((html.match(/autoComplete="username"/g) || []).length, 1);
  assert.ok(!html.includes('שם לתצוגה'));
  assert.ok(!html.includes('family-username'));
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

test('password settings are a labeled dialog with explicit non-submit close and confirmation', async () => {
  const { AccountSettings } = await import('../web/AccountSettings.js');
  const html = renderToStaticMarkup(React.createElement(AccountSettings, {
    children: [{ id: 'synthetic-child', name: 'child2' }], onClose() {},
  }));
  assert.match(html, /role="dialog" aria-modal="true" aria-label="הגדרות חשבון"/);
  assert.match(html, /<button type="button" class="icon-button" aria-label="סגירה"/);
  assert.match(html, /autoComplete="current-password"/);
  assert.equal((html.match(/autoComplete="new-password"/g) || []).length, 2);
  assert.ok(html.includes('child2'));
});
