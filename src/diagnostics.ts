import { monitorEventLoopDelay, performance } from 'node:perf_hooks';
import { freemem, totalmem } from 'node:os';
import { mkdirSync, readFileSync, statSync, existsSync } from 'node:fs';
import { writeFile, rename, unlink, statfs, stat } from 'node:fs/promises';
import { join } from 'node:path';
import { randomUUID } from 'node:crypto';
import { z } from 'zod';
import { hash, open, seal } from './crypto.js';
import type { Config } from './config.js';

// Closed vocabulary: never accept SQL, paths, request URLs, messages, user IDs or provider errors.
export const operations = ['db_read', 'db_write', 'db_commit', 'media_read', 'media_write',
  'api', 'audio_encode', 'connector', 'media_download'] as const;
export type Operation = typeof operations[number];
export const errorKinds = ['io', 'no_space', 'readonly', 'busy', 'permission', 'internal'] as const;
type ErrorKind = typeof errorKinds[number];
const bounds = [1, 5, 20, 50, 100, 250, 1000, 5000, 30000];
const hourMs = 3600000;
const number = z.number().finite().nonnegative().max(Number.MAX_SAFE_INTEGER);
const aggregateSchema = z.object({ count: number.int(), totalMs: number, maxMs: number,
  histogram: z.array(number.int()).length(bounds.length + 1), bytes: number.int(), byteMs: number,
  errors: z.partialRecord(z.enum(errorKinds), number.int()),
}).strict();
const bucketSchema = z.object({ hour: number.int(), metrics: z.partialRecord(z.enum(operations), aggregateSchema) }).strict();
const savedSchema = z.object({ version: z.literal(1), buckets: z.array(bucketSchema).max(168) }).strict();
type Aggregate = z.infer<typeof aggregateSchema>;
type Bucket = z.infer<typeof bucketSchema>;
type Family = { buckets: Bucket[]; dirty: boolean; revision: number; lastSavedAt: number | null };

export function classifyFailure(error: unknown): ErrorKind {
  const e = error as { code?: unknown; errcode?: unknown } | undefined;
  const code = e?.code;
  const sqlite = typeof e?.errcode === 'number' ? e.errcode & 255 : 0;
  if (code === 'ENOSPC' || code === 'EDQUOT' || sqlite === 13) return 'no_space';
  if (code === 'EROFS' || sqlite === 8) return 'readonly';
  if (code === 'EIO' || sqlite === 10) return 'io';
  if (code === 'EBUSY' || sqlite === 5 || sqlite === 6) return 'busy';
  if (code === 'EACCES' || code === 'EPERM' || sqlite === 3) return 'permission';
  return 'internal';
}
function empty(): Aggregate {
  return { count: 0, totalMs: 0, maxMs: 0, histogram: bounds.map(() => 0).concat(0),
    bytes: 0, byteMs: 0, errors: {} };
}
function add(target: Aggregate, source: Aggregate) {
  target.count += source.count;
  target.totalMs += source.totalMs;
  target.maxMs = Math.max(target.maxMs, source.maxMs);
  target.bytes += source.bytes;
  target.byteMs += source.byteMs;
  source.histogram.forEach((n, i) => target.histogram[i] += n);
  for (const key of errorKinds) if (source.errors[key])
    target.errors[key] = (target.errors[key] || 0) + source.errors[key]!;
}
function present(a: Aggregate) {
  let seen = 0, p95UpperMs: number | null = null;
  for (let i = 0; i < a.histogram.length; i++) {
    seen += a.histogram[i];
    if (seen >= Math.ceil(a.count * .95)) { p95UpperMs = bounds[i] ?? null; break; }
  }
  return { count: a.count, meanMs: a.count ? a.totalMs / a.count : 0, maxMs: a.maxMs,
    p95UpperMs, errors: { ...a.errors }, bytes: a.bytes,
    // Application completion speed (OS cache included), not a sustained SD-card benchmark.
    mibPerSecond: a.bytes >= 65536 && a.byteMs > 0 ? a.bytes / 1048576 / (a.byteMs / 1000) : null };
}

export class Diagnostics {
  readonly enabled: boolean;
  readonly retentionDays: number;
  readonly flushSeconds: number;
  private families = new Map<string, Family>();
  private forgotten = new Set<string>();
  private flushTask?: Promise<void>;
  private timer?: ReturnType<typeof setInterval>;
  private saveTimer?: ReturnType<typeof setInterval>;
  private loop = monitorEventLoopDelay({ resolution: 20 });
  private startedAt = Date.now();
  private lastSample = performance.now();
  private lastCpu = process.cpuUsage();
  private sample: { sampledAt: number; cpuPercent: number; eventLoopP95Ms: number | null;
    rssBytes: number; heapBytes: number; hostAvailableBytes: number; hostTotalBytes: number } | null = null;
  private persistenceError: ErrorKind | 'integrity' | null = null;
  private directory: string;
  constructor(private config: Config, private now = () => Date.now()) {
    this.enabled = config.diagnosticsEnabled ?? true;
    this.retentionDays = config.diagnosticsRetentionDays ?? 7;
    this.flushSeconds = config.diagnosticsFlushSeconds ?? 300;
    this.directory = join(config.dataDir, 'diagnostics');
  }
  private path(f: string) { return join(this.directory, hash(f) + '.enc'); }
  private prune(family: Family) {
    const cutoff = Math.floor(this.now() / hourMs) * hourMs - (this.retentionDays * 24 - 1) * hourMs;
    const before = family.buckets.length;
    family.buckets = family.buckets.filter(b => b.hour >= cutoff && b.hour <= this.now()).slice(-168);
    if (before !== family.buckets.length) { family.dirty = true; family.revision++; }
  }
  private family(f: string): Family {
    let family = this.families.get(f);
    if (!family) {
      family = { buckets: [], dirty: false, revision: 0, lastSavedAt: null };
      const path = this.path(f);
      if (this.enabled && existsSync(path)) {
        try {
          const st = statSync(path);
          if (!st.isFile() || st.size > 512 * 1024) throw new Error('Invalid diagnostics snapshot');
          const parsed = savedSchema.parse(JSON.parse(open(this.config.masterKey, readFileSync(path),
            `${f}:diagnostics:v1`).toString()));
          family.buckets = parsed.buckets;
          family.lastSavedAt = st.mtimeMs;
        } catch { this.persistenceError = 'integrity'; }
      }
      this.families.set(f, family);
    }
    this.prune(family);
    return family;
  }
  start(familyIds: string[]) {
    if (!this.enabled || this.timer) return;
    for (const f of familyIds) this.family(f);
    this.loop.enable();
    this.lastSample = performance.now();
    this.lastCpu = process.cpuUsage();
    this.timer = setInterval(() => {
      const now = performance.now(), cpu = process.cpuUsage(), memory = process.memoryUsage();
      this.sample = { sampledAt: this.now(),
        cpuPercent: (cpu.user - this.lastCpu.user + cpu.system - this.lastCpu.system) / ((now - this.lastSample) * 10),
        eventLoopP95Ms: this.loop.count ? this.loop.percentile(95) / 1e6 : null,
        rssBytes: memory.rss, heapBytes: memory.heapUsed, hostAvailableBytes: freemem(), hostTotalBytes: totalmem() };
      this.loop.reset(); this.lastCpu = cpu; this.lastSample = now;
    }, 15000);
    this.timer.unref();
    this.saveTimer = setInterval(() => { void this.flush(); }, this.flushSeconds * 1000);
    this.saveTimer.unref();
  }
  record(f: string, operation: Operation, ms: number, error?: unknown, bytes = 0) {
    if (!this.enabled || this.forgotten.has(f) || !operations.includes(operation) || !Number.isFinite(ms) || ms < 0) return;
    const family = this.family(f), hour = Math.floor(this.now() / hourMs) * hourMs;
    let bucket = family.buckets.find(b => b.hour === hour);
    if (!bucket) { bucket = { hour, metrics: {} }; family.buckets.push(bucket); }
    const a = bucket.metrics[operation] ||= empty();
    a.count++; a.totalMs += ms; a.maxMs = Math.max(a.maxMs, ms);
    a.histogram[bounds.findIndex(bound => ms <= bound) === -1 ? bounds.length : bounds.findIndex(bound => ms <= bound)]++;
    if (error !== undefined) { const kind = classifyFailure(error); a.errors[kind] = (a.errors[kind] || 0) + 1; }
    else if (bytes >= 65536 && Number.isSafeInteger(bytes)) { a.bytes += bytes; a.byteMs += ms; }
    family.dirty = true; family.revision++;
  }
  measure<T>(f: string, operation: Operation, action: () => T, bytes = 0): T {
    if (!this.enabled) return action();
    const start = performance.now();
    try { const value = action(); this.record(f, operation, performance.now() - start, undefined, bytes); return value; }
    catch (e) { this.record(f, operation, performance.now() - start, e); throw e; }
  }
  async measureAsync<T>(f: string, operation: Operation, action: () => Promise<T>, bytes = 0): Promise<T> {
    if (!this.enabled) return action();
    const start = performance.now();
    try { const value = await action(); this.record(f, operation, performance.now() - start, undefined, bytes); return value; }
    catch (e) { this.record(f, operation, performance.now() - start, e); throw e; }
  }
  async flush(): Promise<void> {
    if (!this.enabled) return;
    if (this.flushTask) return this.flushTask;
    this.flushTask = this.save();
    try { await this.flushTask; } finally { this.flushTask = undefined; }
  }
  private async save() {
    for (const [f, family] of this.families) {
      this.prune(family);
      if (!family.dirty || this.forgotten.has(f)) continue;
      const path = this.path(f), temporary = path + '.' + randomUUID() + '.tmp', revision = family.revision;
      try {
        mkdirSync(this.directory, { recursive: true, mode: 0o700 });
        if (family.buckets.length) {
          const encrypted = seal(this.config.masterKey, JSON.stringify({ version: 1, buckets: family.buckets }), `${f}:diagnostics:v1`);
          await writeFile(temporary, encrypted, { mode: 0o600, flag: 'wx' });
          if (this.forgotten.has(f)) { await unlink(temporary); continue; }
          await rename(temporary, path);
        } else await unlink(path).catch(e => { if (e.code !== 'ENOENT') throw e; });
        family.lastSavedAt = this.now();
        if (revision === family.revision) family.dirty = false;
        this.persistenceError = null;
      } catch (error) { this.persistenceError = classifyFailure(error); }
      finally { await unlink(temporary).catch(() => {}); }
    }
  }
  async clear(f: string) {
    this.forgotten.add(f);
    if (this.flushTask) await this.flushTask;
    this.families.delete(f);
    try { await unlink(this.path(f)); } catch (e) {
      if ((e as NodeJS.ErrnoException).code !== 'ENOENT') { this.persistenceError = classifyFailure(e); throw e; }
    }
    this.forgotten.delete(f);
  }
  async forget(f: string) { await this.clear(f); this.forgotten.add(f); }
  async report(f: string) {
    const family = this.enabled ? this.family(f) : { buckets: [], lastSavedAt: null };
    const totals: Partial<Record<Operation, Aggregate>> = {};
    for (const b of family.buckets) for (const op of operations) if (b.metrics[op]) add(totals[op] ||= empty(), b.metrics[op]!);
    const metrics = operations.flatMap(operation => totals[operation] ? [{ operation, ...present(totals[operation]!) }] : []);
    const fs = await statfs(this.config.dataDir).catch(() => null);
    const sizes = await Promise.all(['app.sqlite', 'app.sqlite-wal'].map(name => stat(join(this.config.dataDir, name)).then(s => s.size).catch(() => null)));
    const memory = process.memoryUsage();
    return { version: 1, enabled: this.enabled, generatedAt: this.now(), retentionDays: this.retentionDays,
      flushSeconds: this.flushSeconds, lastSavedAt: family.lastSavedAt, persistenceError: this.persistenceError,
      process: { uptimeSeconds: Math.round((Date.now() - this.startedAt) / 1000),
        rssBytes: memory.rss, heapBytes: memory.heapUsed, sample: this.sample },
      storage: { freeBytes: fs ? Number(fs.bavail) * Number(fs.bsize) : null,
        totalBytes: fs ? Number(fs.blocks) * Number(fs.bsize) : null, databaseBytes: sizes[0], walBytes: sizes[1] },
      metrics,
      hours: family.buckets.map(b => ({ hour: b.hour, metrics: operations.flatMap(operation => b.metrics[operation] ? [{ operation, ...present(b.metrics[operation]!) }] : []) })),
    };
  }
  async stop() {
    if (this.timer) clearInterval(this.timer);
    if (this.saveTimer) clearInterval(this.saveTimer);
    this.timer = undefined; this.saveTimer = undefined;
    this.loop.disable();
    await this.flush();
  }
}
export type DiagnosticsReport = Awaited<ReturnType<Diagnostics['report']>>;
