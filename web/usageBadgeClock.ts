export function formatUsageTime(ms: number) {
  const seconds = Math.ceil(Math.max(0, ms) / 1000), pad = (n: number) => String(n).padStart(2, '0');
  const minutes = Math.floor(seconds / 60);
  return minutes >= 60 ? `${Math.floor(minutes / 60)}:${pad(minutes % 60)}:${pad(seconds % 60)}` : `${pad(minutes)}:${pad(seconds % 60)}`;
}

/** Display projection only. Server snapshots replace the estimate; pausing
 * freezes it. This clock never grants a lease or submits elapsed usage. */
export function createUsageBadgeClock() {
  let previous: object | null = null, base: number | null = null, at = 0, running = false;
  const value = (now: number) => base === null ? null : Math.max(0, base - (running ? Math.max(0, now - at) : 0));
  return { update(snapshot: { remainingMs: number | null }, active: boolean, now: number): number | null {
    const current = value(now);
    base = snapshot !== previous ? snapshot.remainingMs : current;
    previous = snapshot; at = now; running = active;
    return value(now);
  } };
}
