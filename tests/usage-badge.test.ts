import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createUsageBadgeClock, formatUsageTime } from '../web/usageBadgeClock';

test('time badge formats minutes/seconds, longer allowances and zero without negative time', () => {
  assert.equal(formatUsageTime(37000), '00:37'); assert.equal(formatUsageTime(37 * 60000), '37:00');
  assert.equal(formatUsageTime(3600000), '1:00:00'); assert.equal(formatUsageTime(1), '00:01');
  assert.equal(formatUsageTime(-10), '00:00');
});
test('badge countdown freezes on pause, resumes without charging pause time and reconciles server bonuses', () => {
  const clock = createUsageBadgeClock(), status = { remainingMs: 120000 };
  assert.equal(clock.update(status, true, 1000), 120000);
  assert.equal(clock.update(status, true, 4000), 117000);
  assert.equal(clock.update(status, false, 5000), 116000);
  assert.equal(clock.update(status, false, 25000), 116000);
  assert.equal(clock.update(status, true, 30000), 116000);
  assert.equal(clock.update(status, true, 31000), 115000);
  assert.equal(clock.update({ remainingMs: 240000 }, true, 32000), 240000);
  assert.equal(clock.update({ remainingMs: 0 }, false, 33000), 0);
  assert.equal(clock.update({ remainingMs: null }, false, 34000), null);
});
test('visual badge ticks are silent and warning announcements change only at thresholds', () => {
  const source = readFileSync(new URL('../web/UsageBudget.tsx', import.meta.url), 'utf8');
  assert.match(source, /role="timer" aria-live="off"/);
  assert.match(source, /remaining <= 60000/);
  assert.match(source, /remaining <= 120000/);
  assert.match(source, /className="sr-only" role="status" aria-live="polite"/);
});
