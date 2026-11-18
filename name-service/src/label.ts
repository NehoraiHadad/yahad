// One Durable Object per label (idFromName(label)). It holds the label's state and makes every
// Cloudflare call for that label. Calls are serialized by an explicit queue: a Durable Object's
// input gate does not hold other requests back while this one waits on fetch(), so without the
// queue two calls for the same label (or a call and the expiry) could interleave.

import { DurableObject } from "cloudflare:workers";
import { BUDGET_NAME } from "./budget";
import { CloudflareError, DnsClient, type DnsRecord, type RecordType } from "./cloudflare";
import {
  ADDRESS_WRITES_PER_LABEL_PER_DAY,
  CHALLENGE_WRITES_PER_LABEL_PER_DAY,
  EXPIRY_MS,
  readSettings,
  type Env,
  type Settings,
} from "./env";
import { digestsEqual, secondsUntilNextUtcDay, utcDay } from "./format";

export interface LabelState {
  label: string;
  secretSha256: string;
  created: number;
  lastSeen: number;
  /** The UTC day lastSeen was last reported to the Budget index. */
  reportedDay: string;
  /** Tombstone: the credential is dead for good. */
  deleted: boolean;
  /** Tombstoned but DNS records may still exist; the cron retries. */
  cleanupPending: boolean;
  address: string | null;
  aRecordId: string | null;
  txtRecords: DnsRecord[];
  quota: { day: string; address: number; challenge: number };
}

/** What the Worker turns into an HTTP response. `code` is a short machine-readable reason. */
export interface Outcome {
  status: number;
  code?: string;
  retryAfter?: number;
}

export type ExpiryOutcome = "live" | "cleanup" | "gone";

const STATE_KEY = "state";
const OK: Outcome = { status: 204 };

class BudgetExhausted extends Error {
  constructor() {
    super("budget_exhausted");
  }
}

class NotConfigured extends Error {
  constructor() {
    super("not_configured");
  }
}

export class LabelObject extends DurableObject<Env> {
  private queue: Promise<unknown> = Promise.resolve();

  /** Stores a new label. "exists" if this object already holds one (live or tombstoned). */
  init(label: string, secretSha256: string, now: number): Promise<"created" | "exists"> {
    return this.serialized(async () => {
      if (this.load() !== undefined) return "exists";
      this.save({
        label,
        secretSha256,
        created: now,
        lastSeen: now,
        reportedDay: utcDay(now),
        deleted: false,
        cleanupPending: false,
        address: null,
        aRecordId: null,
        txtRecords: [],
        quota: { day: utcDay(now), address: 0, challenge: 0 },
      });
      return "created";
    });
  }

  setAddress(secretSha256: string, now: number, ipv4: string): Promise<Outcome> {
    return this.authenticated(secretSha256, now, false, async (state, settings) => {
      if (state.address === ipv4 && state.aRecordId !== null) return OK;
      const limited = this.takeQuota(state, "address", now);
      if (limited) return limited;
      await this.syncA(state, settings, now, ipv4);
      state.address = ipv4;
      this.save(state);
      return OK;
    });
  }

  setChallenge(secretSha256: string, now: number, values: string[]): Promise<Outcome> {
    return this.authenticated(secretSha256, now, false, (state, settings) =>
      this.changeChallenge(state, settings, now, values),
    );
  }

  clearChallenge(secretSha256: string, now: number): Promise<Outcome> {
    return this.authenticated(secretSha256, now, false, (state, settings) =>
      this.changeChallenge(state, settings, now, []),
    );
  }

  /** Removes the A and TXT records and leaves a tombstone. Idempotent. */
  deleteName(secretSha256: string, now: number): Promise<Outcome> {
    return this.authenticated(secretSha256, now, true, async (state, settings) => {
      if (!state.deleted) await this.tombstone(state, settings, now);
      else if (state.cleanupPending) await this.cleanup(state, settings, now);
      return OK;
    });
  }

  /** Cron: expires the label if unseen for too long, and finishes pending cleanups. */
  expire(label: string, now: number): Promise<ExpiryOutcome> {
    return this.serialized(async () => {
      const settings = readSettings(this.env);
      if (settings === null) throw new NotConfigured();
      const state = this.load();
      if (state === undefined) {
        // Admitted by the Budget but never stored here (init failed): nothing to remove.
        await this.budget().setStatus(label, "gone");
        return "gone";
      }
      if (state.deleted) {
        if (state.cleanupPending) return (await this.cleanup(state, settings, now)) ? "gone" : "cleanup";
        await this.budget().setStatus(label, "gone");
        return "gone";
      }
      if (now - state.lastSeen > EXPIRY_MS) {
        return (await this.tombstone(state, settings, now)) ? "gone" : "cleanup";
      }
      // The index was behind (a report was lost): correct it.
      await this.budget().touch(state.label, state.lastSeen);
      return "live";
    });
  }

  /** For tests and operations. Never includes anything secret beyond the stored digest. */
  inspect(): Promise<LabelState | undefined> {
    return this.serialized(async () => this.load());
  }

  // --- request plumbing ---

  private serialized<T>(work: () => Promise<T>): Promise<T> {
    const run = this.queue.then(work, work);
    this.queue = run.catch(() => undefined);
    return run;
  }

  private authenticated(
    secretSha256: string,
    now: number,
    allowTombstone: boolean,
    work: (state: LabelState, settings: Settings) => Promise<Outcome>,
  ): Promise<Outcome> {
    return this.serialized(async () => {
      const settings = readSettings(this.env);
      if (settings === null) return { status: 503, code: "not_configured" };
      const state = this.load();
      if (state === undefined || !digestsEqual(state.secretSha256, secretSha256)) {
        return { status: 401, code: "unauthorized" };
      }
      try {
        if (state.deleted) {
          if (!allowTombstone) return { status: 410, code: "gone" };
          return await work(state, settings);
        }
        if (now - state.lastSeen > EXPIRY_MS) {
          await this.tombstone(state, settings, now);
          return allowTombstone ? OK : { status: 410, code: "gone" };
        }
        await this.markSeen(state, now);
        return await work(state, settings);
      } catch (error) {
        if (error instanceof BudgetExhausted) {
          return { status: 503, code: "budget_exhausted", retryAfter: secondsUntilNextUtcDay(now) };
        }
        if (error instanceof CloudflareError) {
          console.error(`label ${state.label}: ${error.message}`);
          return { status: 502, code: "upstream_error" };
        }
        throw error;
      }
    });
  }

  private async markSeen(state: LabelState, now: number): Promise<void> {
    state.lastSeen = Math.max(state.lastSeen, now);
    this.save(state);
    const day = utcDay(state.lastSeen);
    if (state.reportedDay === day) return;
    try {
      await this.budget().touch(state.label, state.lastSeen);
      state.reportedDay = day;
      this.save(state);
    } catch {
      // Reported again on the next call; the cron also corrects the index from here.
    }
  }

  private takeQuota(state: LabelState, kind: "address" | "challenge", now: number): Outcome | null {
    const day = utcDay(now);
    if (state.quota.day !== day) state.quota = { day, address: 0, challenge: 0 };
    const cap = kind === "address" ? ADDRESS_WRITES_PER_LABEL_PER_DAY : CHALLENGE_WRITES_PER_LABEL_PER_DAY;
    if (state.quota[kind] >= cap) {
      return { status: 429, code: "label_daily_limit", retryAfter: secondsUntilNextUtcDay(now) };
    }
    // Counted before calling Cloudflare: an attempt that fails still counts.
    state.quota[kind] += 1;
    this.save(state);
    return null;
  }

  // --- DNS ---

  private aName(state: LabelState, settings: Settings): string {
    return `${state.label}.${settings.zoneName}`;
  }

  private txtName(state: LabelState, settings: Settings): string {
    return `_acme-challenge.${state.label}.${settings.zoneName}`;
  }

  private async changeChallenge(
    state: LabelState,
    settings: Settings,
    now: number,
    values: string[],
  ): Promise<Outcome> {
    const current = state.txtRecords.map((record) => record.content);
    const unchanged = current.length === values.length && values.every((value) => current.includes(value));
    if (unchanged) return OK;
    const limited = this.takeQuota(state, "challenge", now);
    if (limited) return limited;
    await this.syncTxt(state, settings, now, values);
    return OK;
  }

  private async syncA(state: LabelState, settings: Settings, now: number, ipv4: string): Promise<void> {
    const dns = new DnsClient(settings.zoneId, settings.apiToken);
    const name = this.aName(state, settings);
    if (state.aRecordId === null) {
      // The id is unknown (first write, or a crash right after a create): adopt what exists.
      const found = await dns.list("A", name);
      const keep = found.find((record) => record.content === ipv4) ?? found[0];
      for (const extra of found) {
        if (extra === keep) continue;
        await this.reserve(settings, now);
        await dns.remove(extra.id);
      }
      if (keep !== undefined) {
        state.aRecordId = keep.id;
        this.save(state);
        if (keep.content === ipv4) return;
      }
    }
    if (state.aRecordId !== null) {
      await this.reserve(settings, now);
      if (await dns.update(state.aRecordId, "A", name, ipv4)) return;
      state.aRecordId = null;
      this.save(state);
    }
    await this.reserve(settings, now);
    state.aRecordId = await this.createOrAdopt(dns, "A", name, ipv4);
    this.save(state);
  }

  private async syncTxt(state: LabelState, settings: Settings, now: number, values: string[]): Promise<void> {
    const dns = new DnsClient(settings.zoneId, settings.apiToken);
    const name = this.txtName(state, settings);
    if (state.txtRecords.length === 0) {
      state.txtRecords = await dns.list("TXT", name);
      this.save(state);
    }
    const kept = state.txtRecords.filter((record) => values.includes(record.content));
    const spare = state.txtRecords.filter((record) => !values.includes(record.content));
    const missing = values.filter((value) => !kept.some((record) => record.content === value));
    const persist = () => {
      state.txtRecords = [...kept, ...spare];
      this.save(state);
    };
    for (const value of missing) {
      const reuse = spare[0];
      if (reuse !== undefined) {
        await this.reserve(settings, now);
        const updated = await dns.update(reuse.id, "TXT", name, value);
        spare.shift();
        if (updated) {
          kept.push({ id: reuse.id, content: value });
          persist();
          continue;
        }
        persist();
      }
      await this.reserve(settings, now);
      kept.push({ id: await this.createOrAdopt(dns, "TXT", name, value), content: value });
      persist();
    }
    while (spare.length > 0) {
      const record = spare[0];
      await this.reserve(settings, now);
      await dns.remove(record.id);
      spare.shift();
      persist();
    }
  }

  private async createOrAdopt(dns: DnsClient, type: RecordType, name: string, content: string): Promise<string> {
    try {
      return await dns.create(type, name, content);
    } catch (error) {
      // A create can fail because the record already exists (an earlier create whose id was
      // lost): adopt it by name and content.
      if (!(error instanceof CloudflareError)) throw error;
      const match = (await dns.list(type, name)).find((record) => record.content === content);
      if (match === undefined) throw error;
      return match.id;
    }
  }

  /** Marks the label deleted, then removes its records. True when nothing is left at Cloudflare. */
  private async tombstone(state: LabelState, settings: Settings, now: number): Promise<boolean> {
    state.deleted = true;
    state.cleanupPending = true;
    state.address = null;
    this.save(state);
    await this.budget().setStatus(state.label, "cleanup");
    return this.cleanup(state, settings, now);
  }

  /**
   * Deletes every A and TXT record of the label: what is stored and what Cloudflare lists by name.
   * On a budget or API failure the tombstone stays "cleanup" and the cron tries again.
   */
  private async cleanup(state: LabelState, settings: Settings, now: number): Promise<boolean> {
    const dns = new DnsClient(settings.zoneId, settings.apiToken);
    try {
      const ids = new Set<string>();
      if (state.aRecordId !== null) ids.add(state.aRecordId);
      for (const record of state.txtRecords) ids.add(record.id);
      for (const record of await dns.list("A", this.aName(state, settings))) ids.add(record.id);
      for (const record of await dns.list("TXT", this.txtName(state, settings))) ids.add(record.id);
      for (const id of ids) {
        await this.reserve(settings, now);
        await dns.remove(id);
        if (state.aRecordId === id) state.aRecordId = null;
        state.txtRecords = state.txtRecords.filter((record) => record.id !== id);
        this.save(state);
      }
    } catch (error) {
      if (error instanceof BudgetExhausted || error instanceof CloudflareError) {
        console.error(`label ${state.label}: cleanup deferred (${error.message})`);
        return false;
      }
      throw error;
    }
    state.cleanupPending = false;
    state.aRecordId = null;
    state.txtRecords = [];
    this.save(state);
    await this.budget().setStatus(state.label, "gone");
    return true;
  }

  private async reserve(settings: Settings, now: number): Promise<void> {
    if (!(await this.budget().reserveWrites(1, now, settings.cfWritesPerDay))) throw new BudgetExhausted();
  }

  // --- storage ---

  private budget() {
    return this.env.BUDGET.get(this.env.BUDGET.idFromName(BUDGET_NAME));
  }

  private load(): LabelState | undefined {
    return this.ctx.storage.kv.get<LabelState>(STATE_KEY);
  }

  private save(state: LabelState): void {
    this.ctx.storage.kv.put(STATE_KEY, state);
  }
}
