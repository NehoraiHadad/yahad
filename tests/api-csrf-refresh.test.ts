import test from 'node:test';
import assert from 'node:assert/strict';

// The client recovers a replaced session's CSRF token once, and only for the account
// that started the request. Two requests refused together while another window signed
// in as someone else must never be resent under the new account.
const events: string[] = [];
const sent: { url: string; method: string; token?: string }[] = [];
let meAccount = 'account-b';
(globalThis as any).window = { dispatchEvent: (event: Event) => { events.push(event.type); if (event.type === 'session-changed') api.setViewerAccount(meAccount); return true; } };
(globalThis as any).fetch = async (url: string, init: RequestInit = {}) => {
  const method = init.method ?? 'GET', token = (init.headers as Record<string, string> | undefined)?.['X-CSRF-Token'];
  sent.push({ url, method, token });
  if (String(url).endsWith('/me')) return new Response(JSON.stringify({ user: { id: meAccount }, csrf: `token-${meAccount}` }), { status: 200 });
  if (token === `token-${meAccount}`) return new Response(JSON.stringify({ ok: true }), { status: 200 });
  return new Response(JSON.stringify({ error: 'CSRF_DENIED' }), { status: 403 });
};
const api = await import('../web/api.ts');

test('a CSRF refresh never resends a request under an account that did not start it', async () => {
  api.setViewerAccount('account-a'); api.setCsrf('token-stale'); sent.length = 0; events.length = 0;
  const results = await Promise.allSettled([api.api('/x', 'POST', { n: 1 }), api.api('/x', 'POST', { n: 2 })]);
  assert.deepEqual(results.map(r => r.status), ['rejected', 'rejected']);
  assert.equal(sent.filter(r => r.method === 'POST').length, 2, 'each refused write was sent once and never resent');
  assert.ok(events.includes('session-changed'));
});

test('the same account recovers its token and resends exactly once', async () => {
  meAccount = 'account-a'; api.setViewerAccount('account-a'); api.setCsrf('token-stale'); sent.length = 0;
  assert.deepEqual(await api.api('/x', 'POST', { n: 3 }), { ok: true });
  assert.deepEqual(sent.filter(r => r.method === 'POST').map(r => r.token), ['token-stale', 'token-account-a']);
});
