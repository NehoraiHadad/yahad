import { test } from 'node:test';
import assert from 'node:assert/strict';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { explainConnection, type ConnectionInput } from '../web/connectionGuidance.js';
import { ConnectionGuidance } from '../web/ConnectionGuidanceView.js';

const base: ConnectionInput = { overviewFailed: false, browserOnline: true, state: 'connected' };
const x = (o: Partial<ConnectionInput>) => explainConnection({ ...base, ...o });

test('failed overview read never claims the server is down and never asks for QR', () => {
  for (const state of ['connected', 'needs_pairing', 'qr', 'conflict', undefined]) {
    const r = x({ overviewFailed: true, state });
    assert.equal(r.code, 'server_no_answer');
    assert.equal(r.needsQr, false);
    assert.equal(r.recoversAlone, true);
    assert.match(r.detail, /ייתכן/);
    assert.match(r.detail, /אי אפשר לדעת/);
    assert.doesNotMatch(r.detail, /חסומ/); // no enforcement claim: the link button is not disabled by a failed read
    assert.match(r.steps.join(' '), /אל תקשרו מחדש/);
  }
});
test('browser offline beats a failed read and is not blamed on WhatsApp', () => {
  const r = x({ browserOnline: false, overviewFailed: true });
  assert.equal(r.code, 'device_offline');
  assert.match(r.detail, /לא בהכרח/);
});
test('first connect and interrupted connection are distinct; neither promises an outcome', () => {
  const c = x({ state: 'connecting' }), r = x({ state: 'reconnecting' });
  assert.deepEqual([c.code, c.needsQr, c.recoversAlone], ['connecting', false, true]);
  assert.deepEqual([r.code, r.needsQr, r.recoversAlone], ['reconnecting', false, true]);
  assert.doesNotMatch(c.detail, /נקטע/);
  assert.match(r.detail, /הניסיונות ממשיכים/);
  assert.match(r.detail, /אין כאן הבטחה/);
  assert.doesNotMatch(r.detail, /אם זה לא יצליח/);
});
test('qr, needs_pairing and idle need a QR scan', () => {
  assert.deepEqual([x({ state: 'qr', qrVisible: true }).code, x({ state: 'qr', qrVisible: true }).needsQr], ['scan_qr', true]);
  assert.deepEqual([x({ state: 'needs_pairing' }).code, x({ state: 'needs_pairing' }).needsQr], ['relink_required', true]);
  assert.deepEqual([x({ state: 'idle' }).code, x({ state: 'idle' }).needsQr], ['not_linked', true]);
});

test('a missing QR image waits for the existing retry and hides scan instructions until it is visible', () => {
  for (const qrVisible of [false, undefined]) {
    const input = { ...base, state: 'qr', qrVisible };
    const r = explainConnection(input);
    assert.deepEqual([r.code, r.recoversAlone, r.needsQr], ['qr_pending', true, false]);
    assert.equal(r.detail, 'קוד ה־QR עדיין לא זמין. המסך ינסה לטעון אותו שוב אוטומטית.');
    assert.deepEqual(r.steps, []);
    const html = renderToStaticMarkup(React.createElement(ConnectionGuidance, { input, loaded: true }));
    assert.match(html, /data-connection-code="qr_pending"/);
    assert.doesNotMatch(html, /סרקו|מכשירים מקושרים|לקשר מכשיר/);
  }
  const visible = x({ state: 'qr', qrVisible: true });
  assert.equal(visible.code, 'scan_qr');
  assert.ok(visible.steps.length > 0);
});
test('relink copy matches code: children kept, unseen groups pruned on sync, same account', () => {
  const r = x({ state: 'needs_pairing' });
  assert.match(r.detail, /חשבונות הילדים לא נמחקים/);
  assert.match(r.detail, /קבוצות שהחשבון המקושר כבר לא רואה יוסרו/);
  assert.match(r.steps.join(' '), /אותו חשבון/);
  assert.doesNotMatch(r.detail, /ההרשאות נשארות|שיחות והרשאות נשארים/);
});
test('conflict is a no-auto-reset state that does not demand a QR', () => {
  const r = x({ state: 'conflict' });
  assert.deepEqual([r.code, r.needsQr, r.recoversAlone], ['replaced', false, false]);
  assert.match(r.detail, /לא מתבצע איפוס אוטומטי/);
});
test('explicit disconnect wins over a stale connected state; unknown state is neutral', () => {
  assert.equal(x({ disconnected: true }).code, 'disconnected_by_parent');
  const r = x({ state: 'weird' });
  assert.equal(r.code, 'unknown'); assert.match(r.detail, /לא נעשה שום שינוי אוטומטי/);
});
test('component renders nothing when healthy or still loading, and renders each problem state', () => {
  const html = (input: ConnectionInput, loaded = true) => renderToStaticMarkup(React.createElement(ConnectionGuidance, { input, loaded }));
  assert.equal(html(base), '');
  assert.equal(html({ ...base, state: undefined }, false), '');
  for (const state of ['connecting', 'qr', 'needs_pairing', 'conflict', 'idle'])
    assert.match(html({ ...base, state }), new RegExp(`data-connection-code="${explainConnection({ ...base, state }).code}"`));
  assert.match(html({ ...base, overviewFailed: true }, false), /server_no_answer/);
});
