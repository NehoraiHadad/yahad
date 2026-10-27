import { test } from 'node:test';
import assert from 'node:assert/strict';
import { api, ApiError, ApiNetworkError, isTransientApiError } from '../web/api.js';

test('background retry classification includes disconnects and server downtime, never auth or input rejection', () => {
  assert.equal(isTransientApiError(new ApiNetworkError('Failed to fetch')), true);
  assert.equal(isTransientApiError(new TypeError('Unexpected response shape')), false);
  for (const status of [408, 429, 500, 502, 503, 504])
    assert.equal(isTransientApiError(new ApiError('temporary', status, 'REQUEST_FAILED')), true);
  for (const status of [400, 401, 403, 404, 409])
    assert.equal(isTransientApiError(new ApiError('rejected', status, 'PARENT_ONLY')), false);
  assert.equal(isTransientApiError(new Error('unexpected client failure')), false);
});

test('API retains status and error code for callers without changing translated messages', async () => {
  const original = globalThis.fetch;
  try {
    globalThis.fetch = (async () => new Response(JSON.stringify({ error: 'PARENT_ONLY' }), { status: 403 })) as typeof fetch;
    await assert.rejects(api('/parent/overview'), e => e instanceof ApiError && e.status === 403 &&
      e.code === 'PARENT_ONLY' && e.message === 'הפעולה זמינה להורה בלבד.' && !isTransientApiError(e));
    globalThis.fetch = (async () => new Response('temporarily unavailable', { status: 503 })) as typeof fetch;
    await assert.rejects(api('/parent/overview'), e => e instanceof ApiError && e.status === 503 && isTransientApiError(e));
    globalThis.fetch = (async () => { throw new TypeError('Failed to fetch'); }) as typeof fetch;
    await assert.rejects(api('/parent/overview'), e => e instanceof ApiNetworkError && isTransientApiError(e));
  } finally { globalThis.fetch = original; }
});
