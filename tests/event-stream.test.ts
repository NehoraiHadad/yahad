import { test } from 'node:test';
import assert from 'node:assert/strict';
import { resilientEventStream } from '../web/eventStream.js';

class FakeStream extends EventTarget {
  readyState = 0;
  onerror: ((event: Event) => void) | null = null;
  closed = 0;
  close() { this.readyState = 2; this.closed++; }
  error(state: number) { this.readyState = state; this.onerror?.(new Event('error')); }
}
function fixture() {
  const streams: FakeStream[] = [];
  const stream = resilientEventStream(() => { const s = new FakeStream(); streams.push(s); return s; });
  let ready = 0, errors = 0, expired = 0;
  stream.addEventListener('ready', () => ready++);
  stream.addEventListener('expired', () => expired++);
  stream.onerror = () => errors++;
  return { streams, stream, counts: () => ({ ready, errors, expired }) };
}
test('CLOSED error reconnects once; new ready recovers and obsolete stream events cannot change state', t => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const f = fixture(), old = f.streams[0];
  old.error(2); old.error(2);
  assert.equal(f.counts().ready, 0);
  t.mock.timers.tick(2000);
  assert.equal(f.streams.length, 2);
  f.streams[1].dispatchEvent(new Event('ready'));
  old.error(2); old.dispatchEvent(new Event('ready')); old.dispatchEvent(new Event('expired'));
  assert.deepEqual(f.counts(), { ready: 1, errors: 2, expired: 0 });
  t.mock.timers.tick(2000); assert.equal(f.streams.length, 2);
  f.stream.close();
});
test('CONNECTING uses native retry; unmount cancels CLOSED recovery', t => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const f = fixture();
  f.streams[0].error(0); t.mock.timers.tick(2000);
  assert.equal(f.streams.length, 1);
  f.streams[0].error(2); f.stream.close(); t.mock.timers.tick(2000);
  assert.equal(f.streams.length, 1);
  f.streams[0].dispatchEvent(new Event('ready'));
  assert.equal(f.counts().ready, 0);
});
test('session expiry is terminal and cancels any pending recovery', t => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const f = fixture();
  f.streams[0].error(2); f.streams[0].dispatchEvent(new Event('expired'));
  t.mock.timers.tick(2000);
  assert.deepEqual(f.counts(), { ready: 0, errors: 1, expired: 1 });
  assert.equal(f.streams.length, 1);
});
