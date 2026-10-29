import { test } from 'node:test';
import assert from 'node:assert/strict';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { ChildTabletSetup } from '../web/ChildTabletSetup';

const child = { id: 'synthetic-a', name: 'ילד לדוגמה', groups: [{ id: 'g', title: 'קבוצה לדוגמה' }], ready: true };
const props = { child, loaded: true, stale: false, busy: false, origin: 'https://family.example.test', onPassword() {}, openChat() {} };
const render = (changes: Partial<React.ComponentProps<typeof ChildTabletSetup>> = {}) =>
  renderToStaticMarkup(React.createElement(ChildTabletSetup, { ...props, ...changes }));

test('tablet handoff groups three instructions and labels site address separately from parent navigation', () => {
  const html = render();
  assert.match(html, /aria-labelledby="tablet-setup-title"/);
  assert.match(html, /<ol class="tablet-setup-steps"/);
  assert.equal((html.match(/<li>/g) || []).length, 3);
  assert.match(html, /כתובת יחד<input readOnly="" dir="ltr" value="https:\/\/family.example.test"/);
  assert.match(html, /שם לכניסה<input readOnly="" dir="auto" value="ילד לדוגמה"/);
  assert.match(html, /לא בסיסמת ההורה/);
  assert.match(html, /אינו מאשר שהטאבלט התחבר/);
  assert.match(html, /<footer class="tablet-parent-navigation">/);
  assert.match(html, /ולא מחברת את הטאבלט/);
});

test('stale, loading and missing child states never expose actionable old credentials', () => {
  for (const changes of [{ stale: true }, { loaded: false }, { child: undefined }]) {
    const html = render(changes);
    assert.doesNotMatch(html, /tablet-login-field|איפוס הסיסמה של|החשבון מוכן לכניסה/);
    assert.match(html, /לשיחות של ההורה/);
  }
  assert.match(render({ child: undefined }), /ניהול הילדים והשיחות/);
  assert.match(render({ stale: true }), /ממתינים לאימות/);
});

test('missing grants and disconnected account expose correct next step without claiming readiness', () => {
  const noGroups = render({ child: { ...child, groups: [], ready: false } });
  assert.match(noGroups, /בחרו לפחות שיחה/);
  assert.match(noGroups, /href="#children-title"/);
  const disconnected = render({ child: { ...child, ready: false } });
  assert.match(disconnected, /href="#family-connection"/);
  assert.doesNotMatch(disconnected, /החשבון מוכן לכניסה/);
});

test('selected child replacement changes login identity and password action atomically', () => {
  let target = '';
  const replacement = { ...child, id: 'synthetic-b', name: 'שם אחר <test>' };
  const tree = ChildTabletSetup({ ...props, child: replacement, onPassword: id => { target = id; } });
  const children = React.Children.toArray(tree.props.children);
  const steps = children[1] as React.ReactElement<any>;
  const secondStep = React.Children.toArray(steps.props.children)[1] as React.ReactElement<any>;
  const reset = React.Children.toArray(secondStep.props.children).find(node =>
    React.isValidElement(node) && node.type === 'button') as React.ReactElement<any>;
  reset.props.onClick();
  assert.equal(target, replacement.id);
  const html = render({ child: replacement, busy: true });
  assert.match(html, /value="שם אחר &lt;test&gt;"/);
  assert.doesNotMatch(html, /value="ילד לדוגמה"/);
  assert.match(html, /class="text-button" disabled=""/);
});

test('loopback origins are clearly marked as unusable on another device', () => {
  for (const origin of ['http://localhost:3000', 'https://127.0.0.1', 'http://[::1]:3000']) {
    const html = render({ origin });
    assert.match(html, /הכתובת במכשיר הזה בלבד/);
    assert.match(html, /בטאבלט צריך לפתוח את כתובת השרת ברשת הביתית/);
    assert.doesNotMatch(html, /פותחים בדפדפן בטאבלט את הכתובת הזו/);
  }
});

test('paused child directs the parent to access management instead of WhatsApp connection', () => {
  const html = render({ child: { ...child, paused: true, ready: false } });
  assert.match(html, /הגישה לחשבון מושהית/);
  assert.match(html, /href="#family-groups"/);
  assert.doesNotMatch(html, /href="#family-connection"/);
  assert.doesNotMatch(html, /החשבון מוכן לכניסה/);
});
