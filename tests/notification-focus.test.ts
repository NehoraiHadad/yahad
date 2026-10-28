import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { restoreNotificationFocus } from '../web/notificationFocus';

test('manual notification dismissal restores only a still-valid visible control without stealing focus', () => {
  const oldDocument = Object.getOwnPropertyDescriptor(globalThis, 'document');
  const oldStyle = Object.getOwnPropertyDescriptor(globalThis, 'getComputedStyle');
  let focused = 0, hidden = false, disabled = false, rectangles = true, visibility = 'visible', modal: any = null;
  const body = {}, close = {}, other = {};
  const target = { isConnected: true, tabIndex: 0, matches: (selector: string) => selector === ':disabled' ? disabled : true,
    closest: () => hidden ? {} : null, getClientRects: () => rectangles ? [{}] : [], focus: () => { focused++; } };
  const doc = { body, activeElement: close, querySelector: () => modal };
  Object.defineProperty(globalThis, 'document', { configurable: true, value: doc });
  Object.defineProperty(globalThis, 'getComputedStyle', { configurable: true, value: () => ({ visibility, display: 'block' }) });
  const restore = () => restoreNotificationFocus(target as unknown as HTMLElement, close as HTMLElement);
  try {
    restore(); assert.equal(focused, 1);
    doc.activeElement = body; restore(); assert.equal(focused, 2, 'removed close button may leave body focused');
    doc.activeElement = other; restore(); assert.equal(focused, 2, 'never steals a newer focus');
    doc.activeElement = close;
    target.isConnected = false; restore(); target.isConnected = true;
    hidden = true; restore(); hidden = false;
    disabled = true; restore(); disabled = false;
    rectangles = false; restore(); rectangles = true;
    visibility = 'hidden'; restore(); visibility = 'visible';
    target.tabIndex = -1; restore(); target.tabIndex = 0;
    modal = { contains: () => false }; restore();
    assert.equal(focused, 2, 'removed/revoked/hidden/disabled/background-modal targets stay unfocused');
    modal = { contains: () => true }; restore(); assert.equal(focused, 3);
  } finally {
    for (const [key, descriptor] of [['document', oldDocument], ['getComputedStyle', oldStyle]] as const) {
      if (descriptor) Object.defineProperty(globalThis, key, descriptor); else Reflect.deleteProperty(globalThis, key);
    }
  }
});

test('auto-expiry and unmount do not restore focus, only explicit close does', () => {
  const source = readFileSync(new URL('../web/ErrorToast.tsx', import.meta.url), 'utf8');
  assert.match(source, /setTimeout\(\(\) => dismiss.current\(\), 8000\)/);
  assert.equal(source.match(/restoreNotificationFocus\(target, closeButton\)/g)?.length, 1);
  assert.match(source, /onClick=\{event => \{[\s\S]*queueMicrotask\(\(\) => restoreNotificationFocus\(target, closeButton\)\)/);
  assert.match(source, /removeEventListener\('focusin', remember\)/);
});
