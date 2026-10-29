import { test } from 'node:test';
import assert from 'node:assert/strict';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { readFileSync } from 'node:fs';
import { ChatCelebration } from '../web/ChatCelebration.js';
import {
  createCelebrationController, celebrationFlair, CELEBRATION_PARTICLES,
  CELEBRATION_DURATION_MS, CELEBRATION_COOLDOWN_MS, type CelebrationBurst,
} from '../web/chatCelebrationState.js';

function harness() {
  let time = 0;
  let allowed = true;
  let nextId = 0;
  const timers = new Map<number, () => void>();
  const changes: (CelebrationBurst | null)[] = [];
  const control = createCelebrationController({
    allowed: () => allowed, flair: 'heart', now: () => time,
    onChange: burst => changes.push(burst),
    schedule(callback, delay) {
      assert.equal(delay, CELEBRATION_DURATION_MS);
      timers.set(++nextId, callback);
      return nextId as unknown as ReturnType<typeof setTimeout>;
    },
    cancel(id) { timers.delete(id as unknown as number); },
  });
  return { control, timers, changes, setAllowed: (value: boolean) => { allowed = value; },
    advance: (value: number) => { time += value; } };
}

test('effects do not run on mount, opt-out, hidden tab or reduced-motion denial', () => {
  const h = harness();
  assert.deepEqual(h.changes, []);
  h.setAllowed(false);
  h.control.celebrate();
  assert.deepEqual(h.changes, []);
  assert.equal(h.timers.size, 0);
});

test('send bursts are bounded to one timer, expire, and throttle repeated sends', () => {
  const h = harness();
  h.control.celebrate();
  for (let i = 0; i < 100; i++) h.control.celebrate();
  assert.equal(h.timers.size, 1);
  assert.equal(h.changes.filter(Boolean).length, 1);
  assert.deepEqual(h.changes.at(-1), { id: 1, flair: 'heart' });
  const expiry = [...h.timers.values()][0];
  h.timers.clear();
  expiry();
  assert.equal(h.changes.at(-1), null);
  h.advance(CELEBRATION_COOLDOWN_MS - 1);
  h.control.celebrate();
  assert.equal(h.timers.size, 0);
  h.advance(1);
  h.control.celebrate();
  assert.equal(h.timers.size, 1);
  assert.deepEqual(h.changes.at(-1), { id: 2, flair: 'heart' });
});

test('preference/visibility cancellation and unmount cancel all pending work', () => {
  const h = harness();
  h.control.celebrate();
  h.control.clear();
  assert.equal(h.timers.size, 0);
  assert.equal(h.changes.at(-1), null);
  h.advance(CELEBRATION_COOLDOWN_MS);
  h.control.celebrate();
  const staleCallback = [...h.timers.values()][0];
  h.control.dispose();
  const count = h.changes.length;
  assert.equal(h.timers.size, 0);
  staleCallback();
  h.advance(CELEBRATION_COOLDOWN_MS);
  h.control.celebrate();
  h.control.clear();
  assert.equal(h.changes.length, count);
});

test('decorations reuse personal flair, stay hidden to assistive tech, and contain no controls', () => {
  assert.equal(renderToStaticMarkup(React.createElement(ChatCelebration, { burst: null })), '');
  for (const flair of ['flower', 'heart', 'sun'] as const) {
    const html = renderToStaticMarkup(React.createElement(ChatCelebration, { burst: { id: 1, flair } }));
    assert.equal((html.match(/class="chat-celebration-particle"/g) || []).length, CELEBRATION_PARTICLES);
    assert.match(html, /aria-hidden="true"/);
    assert.doesNotMatch(html, /button|tabindex|aria-live|audio|status/);
  }
  assert.equal(celebrationFlair('unknown'), 'flower');
});

test('CSS protects pointer interaction and reduced-motion independently of JavaScript', () => {
  const css = readFileSync(new URL('../web/chatCelebration.css', import.meta.url), 'utf8');
  assert.match(css, /pointer-events: none/);
  assert.match(css, /prefers-reduced-motion: reduce/);
  assert.match(css, /\.chat-celebration \{ display: none; \}/);
  assert.doesNotMatch(css, /infinite|position: fixed/);
});
