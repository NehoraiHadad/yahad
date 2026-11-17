import { test } from 'node:test';
import assert from 'node:assert/strict';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { HomeAddress } from '../web/HomeAddress';
import { isLoopbackOrigin, sharedAddress, tabletAddress } from '../web/siteAddress';
import { parseHomeNetwork } from '../src/home-network';

const fingerprint = 'AB:AB:AB:AB:AB:AB:AB:AB:AB:AB:AB:AB:AB:AB:AB:AB:AB:AB:AB:AB:AB:AB:AB:AB:AB:AB:AB:AB:AB:AB:AB:AB';
const lan = 'https://192.168.4.25', loopback = 'http://127.0.0.1:47123';
const plain = { joinPage: 'http://192.168.4.25/', certificateSha256: fingerprint };
const secure = { joinPage: 'https://192.168.4.25/home-network', certificateSha256: fingerprint };
const render = (props: React.ComponentProps<typeof HomeAddress>) => renderToStaticMarkup(React.createElement(HomeAddress, props));

test('the shared address is the server origin unless it is reachable from its computer only', () => {
  assert.equal(sharedAddress(lan), lan);
  assert.equal(sharedAddress('https://family.example.test/'), 'https://family.example.test');
  for (const origin of [undefined, '', 'not a url', loopback, 'http://localhost:3000', 'http://[::1]:3000', 'http://app.localhost'])
    assert.equal(sharedAddress(origin), null, String(origin));
  assert.equal(isLoopbackOrigin('http://127.4.0.1:1'), true);
  assert.equal(isLoopbackOrigin(lan), false);
});

test('a tablet is given the address the parent is on, unless that is the loopback entry', () => {
  assert.equal(tabletAddress(loopback, lan), lan);
  assert.equal(tabletAddress('https://test-device.test-tailnet.ts.net', lan), 'https://test-device.test-tailnet.ts.net');
  assert.equal(tabletAddress(lan, lan), lan);
  assert.equal(tabletAddress(loopback, undefined), null);
  assert.equal(tabletAddress(loopback, loopback), null);
});

test('the server accepts only a join page the home-network proxy serves for its own address', () => {
  assert.equal(parseHomeNetwork(undefined, undefined, lan), undefined);
  assert.deepEqual(parseHomeNetwork(plain.joinPage, fingerprint, lan), plain);
  assert.deepEqual(parseHomeNetwork(secure.joinPage, fingerprint, lan), secure);
  for (const [page, sha, origin] of [
    [plain.joinPage, undefined, lan], [undefined, fingerprint, lan], [plain.joinPage, fingerprint.toLowerCase(), lan],
    [plain.joinPage, fingerprint.slice(3), lan], ['http://192.168.4.26/', fingerprint, lan], ['http://192.168.4.25', fingerprint, lan],
    ['https://192.168.4.25/', fingerprint, lan], ['http://192.168.4.25/', fingerprint, 'http://192.168.4.25'],
    ['https://evil.example/home-network', fingerprint, lan]] as const)
    assert.throws(() => parseHomeNetwork(page, sha, origin), String([page, sha, origin]));
});

test('on the loopback entry the parent sees the home address, the plain join page and the fingerprint', () => {
  const html = render({ siteOrigin: lan, homeNetwork: plain, currentOrigin: loopback });
  assert.match(html, /aria-labelledby="home-address-title"/);
  assert.match(html, /הכתובת שבדפדפן כאן פועלת רק במחשב הזה/);
  assert.match(html, /כתובת יחד<input readOnly="" dir="ltr" value="https:\/\/192.168.4.25"/);
  assert.match(html, /דף ההצטרפות<input readOnly="" dir="ltr" value="http:\/\/192.168.4.25\/"/);
  assert.ok(html.includes('value="' + fingerprint + '"'));
  assert.match(html, /להוריד את תעודת הבית ולבדוק אותה/);
  assert.doesNotMatch(html, /גם לפני דף ההצטרפות/);
  assert.equal((html.match(/העתקה/g) || []).length, 3);
});

test('an HTTPS-only join page is described as coming after the browser warning', () => {
  const html = render({ siteOrigin: lan, homeNetwork: secure, currentOrigin: loopback });
  assert.match(html, /value="https:\/\/192.168.4.25\/home-network"/);
  assert.match(html, /אזהרת אבטחה גם לפני דף ההצטרפות/);
});

test('without the server record there are no join instructions, and away from the loopback entry no card', () => {
  const html = render({ siteOrigin: lan, currentOrigin: loopback });
  assert.match(html, /value="https:\/\/192.168.4.25"/);
  assert.doesNotMatch(html, /דף ההצטרפות|טביעת האצבע|רשת ביתית/);
  for (const props of [{ siteOrigin: lan, homeNetwork: plain, currentOrigin: lan },
    { siteOrigin: lan, homeNetwork: plain, currentOrigin: 'https://test-device.test-tailnet.ts.net' },
    { siteOrigin: loopback, currentOrigin: loopback }, { currentOrigin: loopback }])
    assert.equal(render(props), '', JSON.stringify(props));
});
