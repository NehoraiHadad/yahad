// The singleton Budget Durable Object: global counters and caps, and the index of labels the cron
// walks. Every method body is synchronous SQL with no await, so each call is atomic.

import { DurableObject } from "cloudflare:workers";
import { NEW_LABELS_PER_DAY, type Env } from "./env";
import { utcDay } from "./format";

export const BUDGET_NAME = "global";

/** live: in use. cleanup: tombstoned, DNS records may remain. gone: nothing left at Cloudflare. */
export type LabelStatus = "live" | "cleanup" | "gone";

export type RegistrationOutcome = "reserved" | "collision" | "capacity" | "daily_limit";

export class Budget extends DurableObject<Env> {
  private readonly sql: SqlStorage;

  constructor(ctx: DurableObjectState, env: Env) {
    super(ctx, env);
    this.sql = ctx.storage.sql;
    this.sql.exec(`CREATE TABLE IF NOT EXISTS counters (
      day TEXT NOT NULL, kind TEXT NOT NULL, used INTEGER NOT NULL,
      PRIMARY KEY (day, kind))`);
    // Rows are kept after a label is gone, so a label is never handed out twice.
    this.sql.exec(`CREATE TABLE IF NOT EXISTS labels (
      label TEXT PRIMARY KEY, status TEXT NOT NULL, last_seen INTEGER NOT NULL)`);
    this.sql.exec(`CREATE INDEX IF NOT EXISTS labels_by_status ON labels (status, last_seen)`);
  }

  /**
   * Admits a new label: it must be unused, the live labels (including those still being
   * cleaned up, whose records still count against the zone) must be under the cap, and the
   * day's registrations under NEW_LABELS_PER_DAY.
   */
  reserveRegistration(label: string, now: number, liveLabelsCap: number): RegistrationOutcome {
    if (this.sql.exec("SELECT 1 FROM labels WHERE label = ?", label).toArray().length > 0) {
      return "collision";
    }
    const live = this.sql
      .exec<{ n: number }>("SELECT COUNT(*) AS n FROM labels WHERE status != 'gone'")
      .one().n;
    if (live >= liveLabelsCap) return "capacity";
    const day = utcDay(now);
    if (this.used(day, "register") >= NEW_LABELS_PER_DAY) return "daily_limit";
    this.add(day, "register", 1);
    this.sql.exec("INSERT INTO labels (label, status, last_seen) VALUES (?, 'live', ?)", label, now);
    return "reserved";
  }

  /** Reserves Cloudflare API writes for today. False (nothing reserved) when it would exceed the cap. */
  reserveWrites(count: number, now: number, perDay: number): boolean {
    const day = utcDay(now);
    if (this.used(day, "cf_write") + count > perDay) return false;
    this.add(day, "cf_write", count);
    return true;
  }

  /** Records that a label was seen. Never moves lastSeen backwards or revives a tombstone. */
  touch(label: string, lastSeen: number): void {
    this.sql.exec(
      "UPDATE labels SET last_seen = MAX(last_seen, ?) WHERE label = ? AND status = 'live'",
      lastSeen,
      label,
    );
  }

  /**
   * Undoes a reservation whose label could not be created (its Label Object already existed, or creating
   * it failed): the label becomes a tombstone, so it is never handed out again, and the registration it
   * took from `now`'s day is given back. Idempotent: only a label still live is given back.
   */
  refuseRegistration(label: string, now: number): void {
    if (this.sql.exec("SELECT 1 FROM labels WHERE label = ? AND status = 'live'", label).toArray().length === 0) return;
    this.sql.exec("UPDATE labels SET status = 'gone' WHERE label = ?", label);
    this.sql.exec(
      "UPDATE counters SET used = MAX(used - 1, 0) WHERE day = ? AND kind = 'register'",
      utcDay(now),
    );
  }

  setStatus(label: string, status: LabelStatus): void {
    this.sql.exec("UPDATE labels SET status = ? WHERE label = ?", status, label);
  }

  /**
   * Labels the cron should look at: live labels last seen before the cutoff, and tombstones whose
   * records are not yet removed. Also drops counters older than yesterday.
   */
  dueForExpiry(cutoff: number, limit: number, now: number): string[] {
    const yesterday = utcDay(now - 24 * 60 * 60 * 1000);
    this.sql.exec("DELETE FROM counters WHERE day < ?", yesterday);
    return this.sql
      .exec<{ label: string }>(
        `SELECT label FROM labels
         WHERE status = 'cleanup' OR (status = 'live' AND last_seen < ?)
         ORDER BY last_seen LIMIT ?`,
        cutoff,
        limit,
      )
      .toArray()
      .map((row) => row.label);
  }

  /** For tests and operations: the index row of one label. */
  describe(label: string): { status: LabelStatus; lastSeen: number } | null {
    const rows = this.sql
      .exec<{ status: LabelStatus; last_seen: number }>(
        "SELECT status, last_seen FROM labels WHERE label = ?",
        label,
      )
      .toArray();
    return rows.length === 0 ? null : { status: rows[0].status, lastSeen: rows[0].last_seen };
  }

  usedToday(kind: "register" | "cf_write", now: number): number {
    return this.used(utcDay(now), kind);
  }

  private used(day: string, kind: string): number {
    const rows = this.sql
      .exec<{ used: number }>("SELECT used FROM counters WHERE day = ? AND kind = ?", day, kind)
      .toArray();
    return rows.length === 0 ? 0 : rows[0].used;
  }

  private add(day: string, kind: string, count: number): void {
    this.sql.exec(
      `INSERT INTO counters (day, kind, used) VALUES (?, ?, ?)
       ON CONFLICT (day, kind) DO UPDATE SET used = used + excluded.used`,
      day,
      kind,
      count,
    );
  }
}
