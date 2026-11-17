import { test } from 'node:test';
import assert from 'node:assert/strict';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { HomeAddress } from '../web/HomeAddress';
import { homeJoinPage, isLoopbackOrigin, sharedAddress } from '../web/siteAddress';

const render = (siteOrigin: string | undefined, currentOrigin: string) =>
  renderToStaticMarkup(React.createElement(HomeAddress, { siteOrigin, currentOrigin }));

test('the shared address is the server origin unless it is reachable from its computer only', () => {
  assert.equal(sharedAddress('https://192.168.4.25'), 'https://192.168.4.25');
  assert.equal(sharedAddress('https://family.example.test/'), 'https://family.example.test');
  for (const origin of [undefined, '', 'not a url', 'http://127.0.0.1:47123', 'http://localhost:3000', 'http://[::1]:3000', 'http://app.localhost'])
    assert.equal(sharedAddress(origin), null, String(origin));
  assert.equal(isLoopbackOrigin('http://127.4.0.1:1'), true);
  assert.equal(isLoopbackOrigin('https://192.168.4.25'), false);
});

test('only Windows home-network mode (HTTPS on a private IPv4) has a join page', () => {
  for (const origin of ['https://10.0.0.5', 'https://172.16.1.2', 'https://172.31.1.2', 'https://192.168.4.25'])
    assert.equal(homeJoinPage(origin), origin + '/home-network');
  for (const origin of ['http://192.168.4.25', 'https://172.32.1.2', 'https://8.8.8.8', 'https://192.169.1.1',
    'https://family.example.test', 'https://test-device.test-tailnet.ts.net'])
    assert.equal(homeJoinPage(origin), null, origin);
});

test('on the loopback entry the parent sees the home address, the join page and why the browser address differs', () => {
  const html = render('https://192.168.4.25', 'http://127.0.0.1:47123');
  assert.match(html, /aria-labelledby="home-address-title"/);
  assert.match(html, /הכתובת שבדפדפן כאן פועלת רק במחשב הזה/);
  assert.match(html, /כתובת יחד<input readOnly="" dir="ltr" value="https:\/\/192.168.4.25"/);
  assert.match(html, /value="https:\/\/192.168.4.25\/home-network"/);
  assert.match(html, /לאותה רשת ביתית/);
  assert.equal((html.match(/העתקה/g) || []).length, 2);
});

test('another deployment shows its address without home-network instructions, and a local-only one shows nothing', () => {
  const html = render('https://family.example.test', 'https://family.example.test');
  assert.match(html, /value="https:\/\/family.example.test"/);
  assert.doesNotMatch(html, /הכתובת שבדפדפן כאן|home-network|רשת ביתית/);
  assert.equal(render('http://127.0.0.1:47123', 'http://127.0.0.1:47123'), '');
  assert.equal(render(undefined, 'http://127.0.0.1:47123'), '');
});
