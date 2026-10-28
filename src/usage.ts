import type { Store, Session } from './store.js';
import { hash } from './crypto.js';

export const USAGE_LEASE_MS = 15_000;
export const USAGE_HEARTBEAT_MS = 5_000;
export const USAGE_IDLE_MS = 5 * 60_000;
export class UsageError extends Error {
  readonly status = 403;
  constructor(readonly code: string) { super(code); }
}
const formatters = new Map<string, Intl.DateTimeFormat>();
export function usageDay(now: number, timezone: string) {
  let formatter = formatters.get(timezone);
  if (!formatter) {
    formatter = new Intl.DateTimeFormat('en-CA', { timeZone: timezone, year: 'numeric', month: '2-digit', day: '2-digit' });
    formatters.set(timezone, formatter);
  }
  const parts = formatter.formatToParts(now);
  return ['year', 'month', 'day'].map(type => parts.find(part => part.type === type)!.value).join('-');
}
export function nextUsageDay(now: number, timezone: string) {
  const day = usageDay(now, timezone);
  let lo = Math.floor(now), hi = lo + 36 * 3600_000;
  while (hi - lo > 1) {
    const mid = Math.floor((lo + hi) / 2);
    if (usageDay(mid, timezone) === day) lo = mid; else hi = mid;
  }
  return hi;
}
export type UsageStatus = {
  day: string; timezone: string; minutes: number | null; usedMs: number; bonusMs: number;
  remainingMs: number | null; exhausted: boolean; serverNow: number; resetAt: number;
  leaseUntil: number; heartbeatMs: number; idleMs: number;
};

/** Only server-clock intervals are billed. Every mutation first settles the union
 * of leases under BEGIN IMMEDIATE. Never accept client time or elapsed duration. */
export class UsageBudgets {
  constructor(readonly store: Store, readonly clock: () => number = Date.now) {}
  private child(f: string, c: string) {
    if (!this.store.one("SELECT 1 FROM users WHERE family_id=? AND id=? AND role='child'", f, c))
      throw new UsageError('CHILD_NOT_FOUND');
  }
  timezone(f: string) { return this.store.one('SELECT timezone FROM usage_families WHERE family_id=?', f)?.timezone || 'UTC'; }
  private policy(f: string, c: string, now: number) {
    this.child(f, c);
    this.store.run('INSERT OR IGNORE INTO usage_policy VALUES(?,?,NULL,?)', f, c, now);
    return this.store.one('SELECT * FROM usage_policy WHERE family_id=? AND child_id=?', f, c);
  }
  private settle(f: string, c: string, now: number) {
    const p = this.policy(f, c, now), zone = this.timezone(f);
    if (now < p.cursor) throw new UsageError('USAGE_CLOCK');
    // All leases start at/before the previous settlement cursor. Their union is
    // therefore a single interval ending at the latest expiry (never their sum).
    const until = this.store.one('SELECT MAX(until) AS n FROM usage_leases WHERE family_id=? AND child_id=?', f, c)?.n || 0;
    let start = p.cursor, end = Math.min(now, until);
    while (start < end) {
      const day = usageDay(start, zone), stop = Math.min(end, nextUsageDay(start, zone));
      this.store.run('INSERT OR IGNORE INTO usage_days(family_id,child_id,day) VALUES(?,?,?)', f, c, day);
      const d = this.store.one('SELECT * FROM usage_days WHERE family_id=? AND child_id=? AND day=?', f, c, day);
      const charge = p.minutes === null ? stop - start : Math.min(stop - start, Math.max(0, p.minutes * 60000 + d.bonus_ms - d.used_ms));
      this.store.run('UPDATE usage_days SET used_ms=used_ms+? WHERE family_id=? AND child_id=? AND day=?', charge, f, c, day);
      start = stop;
    }
    this.store.run('UPDATE usage_policy SET cursor=? WHERE family_id=? AND child_id=?', now, f, c);
    return p;
  }
  private snapshot(f: string, c: string, now: number, sid?: string): UsageStatus {
    const timezone = this.timezone(f), day = usageDay(now, timezone);
    const p = this.store.one('SELECT minutes FROM usage_policy WHERE family_id=? AND child_id=?', f, c);
    const d = this.store.one('SELECT * FROM usage_days WHERE family_id=? AND child_id=? AND day=?', f, c, day);
    const usedMs = d?.used_ms || 0, bonusMs = d?.bonus_ms || 0, minutes = p?.minutes ?? null;
    const remainingMs = minutes === null ? null : Math.max(0, minutes * 60000 + bonusMs - usedMs);
    const leaseUntil = sid ? this.store.one('SELECT MAX(until) AS n FROM usage_leases WHERE family_id=? AND child_id=? AND session_hash=?', f, c, hash(sid))?.n || 0 : 0;
    return { day, timezone, minutes, usedMs, bonusMs, remainingMs, exhausted: remainingMs === 0,
      serverNow: now, resetAt: nextUsageDay(now, timezone), leaseUntil, heartbeatMs: USAGE_HEARTBEAT_MS, idleMs: USAGE_IDLE_MS };
  }
  status(f: string, c: string, sid?: string) {
    const now = this.clock();
    return this.store.tx(() => { this.settle(f, c, now); return this.snapshot(f, c, now, sid); });
  }
  heartbeat(s: Session, tab: string, seq: number, active: boolean) {
    const f = s.user.familyId, c = s.user.id, now = this.clock(), sh = hash(s.sid);
    return this.store.tx(() => {
      this.settle(f, c, now);
      const previous = this.store.one('SELECT seq FROM usage_leases WHERE family_id=? AND child_id=? AND session_hash=? AND tab=?', f, c, sh, tab);
      if (!previous && this.store.one('SELECT COUNT(*) AS n FROM usage_leases WHERE session_hash=?', sh).n >= 32)
        throw new UsageError('USAGE_TAB_LIMIT');
      // Tombstones retain sequence even after pause/expiry; stale pause can never
      // cancel a newer activation, nor can replay extend the same lease.
      if (!previous || seq > previous.seq) {
        const status = this.snapshot(f, c, now);
        const until = active && !status.exhausted ? Math.min(now + USAGE_LEASE_MS, s.expires, now + (status.remainingMs ?? USAGE_LEASE_MS)) : 0;
        this.store.run(`INSERT INTO usage_leases VALUES(?,?,?,?,?,?) ON CONFLICT(family_id,child_id,session_hash,tab) DO UPDATE SET seq=excluded.seq,until=excluded.until`, f, c, sh, tab, seq, until);
      }
      return this.snapshot(f, c, now, s.sid);
    });
  }
  require(s: Session) {
    if (s.user.role !== 'child') return;
    const status = this.status(s.user.familyId, s.user.id, s.sid);
    if (status.exhausted) throw new UsageError('USAGE_EXHAUSTED');
    if (status.minutes !== null && status.leaseUntil <= status.serverNow) throw new UsageError('USAGE_PAUSED');
  }
  setPolicy(f: string, c: string, minutes: number | null) {
    if (minutes !== null && (!Number.isSafeInteger(minutes) || minutes < 0 || minutes > 1000000)) throw new UsageError('INVALID_INPUT');
    const now = this.clock();
    return this.store.tx(() => {
      this.settle(f, c, now);
      this.store.run('UPDATE usage_policy SET minutes=? WHERE family_id=? AND child_id=?', minutes, f, c);
      // Reconcile every active device before it can reveal content again.
      this.store.run('UPDATE usage_leases SET until=0 WHERE family_id=? AND child_id=?', f, c);
      return this.snapshot(f, c, now);
    });
  }
  bonus(f: string, c: string, requestId: string, day: string, minutes: number) {
    if (!Number.isSafeInteger(minutes) || minutes < 1 || minutes > 1000000) throw new UsageError('INVALID_INPUT');
    const now = this.clock();
    return this.store.tx(() => {
      this.settle(f, c, now);
      const existing = this.store.one('SELECT * FROM usage_bonus WHERE family_id=? AND child_id=? AND request_id=?', f, c, requestId);
      if (existing) {
        if (existing.day !== day || existing.minutes !== minutes) throw new UsageError('BONUS_CONFLICT');
      } else {
        if (day !== usageDay(now, this.timezone(f))) throw new UsageError('BONUS_DAY_CHANGED');
        this.store.run('INSERT INTO usage_bonus VALUES(?,?,?,?,?)', f, c, requestId, day, minutes);
        this.store.run(`INSERT INTO usage_days VALUES(?,?,?,0,?) ON CONFLICT(family_id,child_id,day) DO UPDATE SET bonus_ms=bonus_ms+excluded.bonus_ms`, f, c, day, minutes * 60000);
      }
      return this.snapshot(f, c, now);
    });
  }
  setTimezone(f: string, timezone: string) {
    try { new Intl.DateTimeFormat('en', { timeZone: timezone }).format(); } catch { throw new UsageError('INVALID_TIMEZONE'); }
    const now = this.clock();
    this.store.tx(() => {
      const children = this.store.all("SELECT id FROM users WHERE family_id=? AND role='child'", f);
      for (const c of children) this.settle(f, c.id, now);
      // Family calendar is fixed once metering starts. This prevents moving the
      // reset boundary backwards and reusing a bonus on another calendar day.
      if (timezone !== this.timezone(f) && this.store.one('SELECT 1 FROM usage_days WHERE family_id=? AND (used_ms>0 OR bonus_ms>0) LIMIT 1', f))
        throw new UsageError('TIMEZONE_IN_USE');
      this.store.run('INSERT INTO usage_families VALUES(?,?) ON CONFLICT(family_id) DO UPDATE SET timezone=excluded.timezone', f, timezone);
    });
  }
  pauseSession(sid: string) {
    const now = this.clock(), sh = hash(sid);
    this.store.tx(() => {
      for (const row of this.store.all('SELECT DISTINCT l.family_id,l.child_id,p.cursor FROM usage_leases l JOIN usage_policy p USING(family_id,child_id) WHERE session_hash=?', sh))
        this.settle(row.family_id, row.child_id, Math.max(now, row.cursor));
      this.store.run('UPDATE usage_leases SET until=0 WHERE session_hash=?', sh);
    });
  }
  revokeUser(f: string, c: string) {
    const now = this.clock();
    this.store.tx(() => {
      const policy = this.store.one('SELECT cursor FROM usage_policy WHERE family_id=? AND child_id=?', f, c);
      // Revocation must remain available during a clock rollback. Its existing
      // high-water mark was already settled; it never grants new content time.
      if (policy) this.settle(f, c, Math.max(now, policy.cursor));
      this.store.run('UPDATE usage_leases SET until=0 WHERE family_id=? AND child_id=?', f, c);
    });
  }
  restart() {
    const now = this.clock();
    this.store.tx(() => {
      for (const row of this.store.all('SELECT family_id,child_id,cursor FROM usage_policy'))
        this.settle(row.family_id, row.child_id, Math.max(now, row.cursor));
      this.store.run('UPDATE usage_leases SET until=0');
      this.store.run('DELETE FROM usage_leases WHERE session_hash NOT IN (SELECT id FROM sessions)');
    });
  }
  cleanExpiredSessions() {
    const now = this.clock();
    this.store.tx(() => {
      for (const row of this.store.all('SELECT DISTINCT family_id,child_id FROM usage_leases WHERE session_hash NOT IN (SELECT id FROM sessions)'))
        this.settle(row.family_id, row.child_id, now);
      this.store.run('DELETE FROM usage_leases WHERE session_hash NOT IN (SELECT id FROM sessions)');
    });
  }
}
