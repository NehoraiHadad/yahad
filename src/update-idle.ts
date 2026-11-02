import { createHash, randomUUID } from 'node:crypto';
import { closeSync, existsSync, fsyncSync, lstatSync, openSync, readFileSync, readdirSync, renameSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

const uuid = /^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/;
const generation = /^[a-f0-9]{32}$/;
/** Runtime-owned safety state, never an authorization grant. A lost browser
 * acknowledgement remains busy across restart; elapsed time cannot prove idle. */
export class UpdateIdleFence {
  private readonly path: string;
  private readonly leases = new Map<string, { owner: string; generation: string }>();
  private requests = 0;
  private held: string | undefined;
  private refused = false;
  private readonly busySources: Array<() => boolean> = [];
  constructor(directory: string, private readonly extraBusy: () => boolean = () => false) {
    this.path = join(directory, 'update-idle.json');
    try {
      if (readdirSync(directory).some(name => name.startsWith('update-idle.json.partial-'))) throw new Error();
      if (!existsSync(this.path)) return;
      const stat = lstatSync(this.path);
      if (!stat.isFile() || stat.isSymbolicLink() || stat.nlink !== 1 || stat.size > 1024 * 1024) throw new Error();
      const state = JSON.parse(readFileSync(this.path, 'utf8'));
      if (state.format !== 2 || !Array.isArray(state.leases) || state.leases.length > 4096) throw new Error();
      for (const entry of state.leases) {
        if (!Array.isArray(entry) || entry.length !== 3 || !uuid.test(entry[0]) || !/^[a-f0-9]{64}$/.test(entry[1]) || !uuid.test(entry[2]) || this.leases.has(entry[0])) throw new Error();
        this.leases.set(entry[0], { owner: entry[1], generation: entry[2] });
      }
    } catch { this.refused = true; }
  }
  get preparing() { return this.held !== undefined; }
  get busy() { return this.refused || this.leases.size > 0 || this.requests > 0 || this.extraBusy() || this.busySources.some(source => source()); }
  observeBusy(source: () => boolean) { this.busySources.push(source); }
  inventory(owner: string) {
    const fingerprint = createHash('sha256').update(owner).digest('hex');
    return [...this.leases].filter(([, value]) => value.owner === fingerprint).map(([id, value]) => ({ id, generation: value.generation }));
  }
  settle(owner: string, entries: Array<{ id: string; generation: string }>) {
    if (this.refused || this.held || this.requests > 1 || this.extraBusy() || this.busySources.some(source => source())) throw new Error('UPDATE_IDLE_REFUSED');
    const fingerprint = createHash('sha256').update(owner).digest('hex');
    if (entries.length > 128 || new Set(entries.map(entry => entry.id)).size !== entries.length) throw new Error('UPDATE_IDLE_REFUSED');
    for (const entry of entries) {
      if (!uuid.test(entry.id) || !uuid.test(entry.generation)) throw new Error('UPDATE_IDLE_REFUSED');
      const prior = this.leases.get(entry.id);
      if (prior && prior.owner !== fingerprint) throw new Error('UPDATE_IDLE_REFUSED');
    }
    for (const entry of entries) if (this.leases.get(entry.id)?.generation === entry.generation) this.leases.delete(entry.id);
    try { this.persist(); } catch { this.refused = true; throw new Error('UPDATE_IDLE_REFUSED'); }
  }
  lease(id: string, owner: string, action: 'open' | 'release', currentGeneration = id, previousGeneration?: string) {
    if (!uuid.test(id) || !uuid.test(currentGeneration) || previousGeneration !== undefined && !uuid.test(previousGeneration) || !owner || this.refused || this.held) throw new Error('UPDATE_IDLE_REFUSED');
    const fingerprint = createHash('sha256').update(owner).digest('hex');
    const prior = this.leases.get(id);
    if (prior && prior.owner !== fingerprint) throw new Error('UPDATE_IDLE_REFUSED');
    if (action === 'open') {
      if (prior?.generation === currentGeneration) return;
      if (prior ? previousGeneration !== prior.generation : previousGeneration !== undefined) throw new Error('UPDATE_IDLE_REFUSED');
      if (!prior && this.leases.size >= 4096) throw new Error('UPDATE_IDLE_REFUSED');
      this.leases.set(id, { owner: fingerprint, generation: currentGeneration });
    } else {
      if (!prior || prior.generation !== currentGeneration) return;
      this.leases.delete(id);
    }
    try { this.persist(); } catch { this.refused = true; throw new Error('UPDATE_IDLE_REFUSED'); }
  }
  enterRequest() {
    if (this.held) throw new Error('UPDATE_PREPARING');
    this.requests++;
    let ended = false;
    return () => { if (!ended) { ended = true; this.requests--; } };
  }
  prepare(id: string) {
    if (!generation.test(id)) return false;
    if (this.held) return this.held === id;
    if (this.busy) return false;
    // Node executes this check and fence installation in one synchronous turn.
    this.held = id;
    return true;
  }
  cancel(id: string) { if (this.held === id) this.held = undefined; return this.held === undefined; }
  private persist() {
    const temporary = this.path + '.partial-' + randomUUID();
    const fd = openSync(temporary, 'wx', 0o600);
    try { writeFileSync(fd, JSON.stringify({ format: 2, leases: [...this.leases].map(([id, value]) => [id, value.owner, value.generation]) })); fsyncSync(fd); }
    finally { closeSync(fd); }
    renameSync(temporary, this.path);
  }
}
