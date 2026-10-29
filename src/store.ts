import { conversationKind } from './conversation-id.js';
import { DatabaseSync, backup } from 'node:sqlite';
import { initializeChatReadState, recordUnread } from './chat-read-state.js';
import { Diagnostics } from './diagnostics.js';
import { DATABASE_SCHEMA, USERS_TABLE, migrateAccountNames } from './db.js';
import { migrateUsage, migrateUsageCalendar, migrateUsageAdmission } from './usage-schema.js';
import { UsageBudgets } from './usage.js';
import { ChildAccessError, migrateChildAccess } from './child-access.js';
import { normalizeAccountName, accountNameKey } from './account-name.js';
import {
  mkdirSync,
  chmodSync,
  readFileSync,
  writeFileSync,
  unlinkSync,
  existsSync,
  statSync,
} from 'node:fs';
import { join } from 'node:path';
import { randomBytes, randomUUID } from 'node:crypto';
import { open, seal, hash, token, passwordHash } from './crypto.js';
import type { Config } from './config.js';
import type { StoredMessage, ChatGroup } from './connectors/types.js';
export interface User {
  id: string;
  familyId: string;
  role: 'parent' | 'child';
  name: string;
}
export interface Session {
  user: User;
  sid: string;
  csrf: string;
  expires: number;
  last: number;
}
export class Store {
  db: DatabaseSync;
  config: Config;
  readonly diagnostics: Diagnostics;
  readonly usage: UsageBudgets;
  constructor(config: Config, options: { usageClock?: () => number } = {}) {
    this.config = config;
    this.diagnostics = new Diagnostics(config);
    mkdirSync(config.dataDir, { recursive: true, mode: 0o700 });
    chmodSync(config.dataDir, 0o700);
    mkdirSync(join(config.dataDir, 'media'), { mode: 0o700, recursive: true });
    this.db = new DatabaseSync(join(config.dataDir, 'app.sqlite'));
    chmodSync(join(config.dataDir, 'app.sqlite'), 0o600);
    try {
      this.db.exec('PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; PRAGMA secure_delete=ON; PRAGMA busy_timeout=5000;');
      const metadataExists = this.one("SELECT 1 FROM sqlite_master WHERE type='table' AND name='instance_metadata'");
      const previousConnector = metadataExists && this.one('SELECT value FROM instance_metadata WHERE key=?', 'connector')?.value;
      if (previousConnector && previousConnector !== config.connector)
        throw new Error('Use a separate data directory when switching from demo to real WhatsApp');
      migrateAccountNames(this.db);
      this.db.exec(`
 CREATE TABLE IF NOT EXISTS families(id TEXT PRIMARY KEY,name TEXT NOT NULL,linked INTEGER NOT NULL DEFAULT 0);
 ${USERS_TABLE.replace('CREATE TABLE users', 'CREATE TABLE IF NOT EXISTS users')};
 CREATE TABLE IF NOT EXISTS credential_attempts(key TEXT PRIMARY KEY,n INTEGER NOT NULL,until INTEGER NOT NULL);
 CREATE TABLE IF NOT EXISTS sessions(id TEXT PRIMARY KEY,family_id TEXT NOT NULL,user_id TEXT NOT NULL,csrf TEXT NOT NULL,expires INTEGER NOT NULL,last INTEGER NOT NULL,FOREIGN KEY(family_id,user_id) REFERENCES users(family_id,id) ON DELETE CASCADE);
 CREATE TABLE IF NOT EXISTS groups(family_id TEXT NOT NULL REFERENCES families(id) ON DELETE CASCADE,id TEXT NOT NULL,title TEXT NOT NULL,PRIMARY KEY(family_id,id));
 CREATE TABLE IF NOT EXISTS grants(family_id TEXT NOT NULL,user_id TEXT NOT NULL,group_id TEXT NOT NULL,PRIMARY KEY(family_id,user_id,group_id),FOREIGN KEY(family_id,user_id) REFERENCES users(family_id,id) ON DELETE CASCADE,FOREIGN KEY(family_id,group_id) REFERENCES groups(family_id,id) ON DELETE CASCADE);
 CREATE TABLE IF NOT EXISTS group_keys(family_id TEXT NOT NULL,group_id TEXT NOT NULL,key BLOB NOT NULL,PRIMARY KEY(family_id,group_id),FOREIGN KEY(family_id,group_id) REFERENCES groups(family_id,id) ON DELETE CASCADE);
 CREATE TABLE IF NOT EXISTS messages(family_id TEXT NOT NULL,group_id TEXT NOT NULL,id TEXT NOT NULL,time INTEGER NOT NULL,body BLOB NOT NULL,PRIMARY KEY(family_id,group_id,id),FOREIGN KEY(family_id,group_id) REFERENCES groups(family_id,id) ON DELETE CASCADE);
 CREATE INDEX IF NOT EXISTS message_order ON messages(family_id,group_id,time,id);
 CREATE TABLE IF NOT EXISTS media(family_id TEXT NOT NULL,group_id TEXT NOT NULL,id TEXT NOT NULL,message_id TEXT NOT NULL,size INTEGER NOT NULL,PRIMARY KEY(family_id,group_id,id),FOREIGN KEY(family_id,group_id,message_id) REFERENCES messages(family_id,group_id,id) ON DELETE CASCADE);
 CREATE TABLE IF NOT EXISTS auth(family_id TEXT NOT NULL REFERENCES families(id) ON DELETE CASCADE,key TEXT NOT NULL,value BLOB NOT NULL,PRIMARY KEY(family_id,key));
 CREATE TABLE IF NOT EXISTS family_connections(family_id TEXT PRIMARY KEY REFERENCES families(id) ON DELETE CASCADE,state TEXT NOT NULL,changed INTEGER NOT NULL);
 CREATE TABLE IF NOT EXISTS identity_labels(family_id TEXT NOT NULL,user_id TEXT NOT NULL,value BLOB NOT NULL,PRIMARY KEY(family_id,user_id),FOREIGN KEY(family_id,user_id) REFERENCES users(family_id,id) ON DELETE CASCADE);
 CREATE TABLE IF NOT EXISTS instance_metadata(key TEXT PRIMARY KEY,value TEXT NOT NULL);
 CREATE TABLE IF NOT EXISTS schema_version(version INTEGER NOT NULL); INSERT INTO schema_version SELECT ${DATABASE_SCHEMA} WHERE NOT EXISTS(SELECT 1 FROM schema_version);`);
      migrateUsage(this.db);
      migrateUsageCalendar(this.db);
      migrateUsageAdmission(this.db);
      migrateChildAccess(this.db);
      this.usage = new UsageBudgets(this, options.usageClock);
      this.usage.restart();
      this.run('INSERT OR IGNORE INTO instance_metadata VALUES(?,?)', 'connector', config.connector);
      if (this.one('SELECT version FROM schema_version')?.version !== DATABASE_SCHEMA)
        throw new Error('Unsupported database schema');
      // Upgrade existing catalogs as well as newly synced groups. Previously an
      // ungranted group had no key; creating one cannot recover discarded history.
      this.tx(() => {
        for (const row of this.all('SELECT family_id,id FROM groups'))
          this.ensureGroupKey(row.family_id, row.id);
      });
      initializeChatReadState(this);
      this.cleanOrphans();
    } catch (error) { this.db.close(); throw error; }
  }
  one(sql: string, ...args: any[]): any {
    return this.db.prepare(sql).get(...args);
  }
  all(sql: string, ...args: any[]): any[] {
    return this.db.prepare(sql).all(...args);
  }
  run(sql: string, ...args: any[]) {
    return this.db.prepare(sql).run(...args);
  }
  tx<T>(f: () => T, familyId?: string): T {
    this.db.exec('BEGIN IMMEDIATE');
    try {
      const result = f();
      if (familyId) this.diagnostics.measure(familyId, 'db_commit', () => this.db.exec('COMMIT'));
      else this.db.exec('COMMIT');
      return result;
    } catch (e) {
      this.db.exec('ROLLBACK');
      throw e;
    }
  }
  async createFamily(
    name: string,
    parent: { name: string; password: string },
    child?: { name: string; password: string },
    firstRun = false,
  ) {
    const fid = randomUUID(),
      pid = randomUUID(),
      cid = randomUUID();
    const ph = await passwordHash(parent.password),
      ch = child ? await passwordHash(child.password) : undefined;
    this.tx(() => {
      // Recheck after hashing, inside the transaction, to serialize first-run claims.
      if (firstRun && this.one('SELECT 1 FROM families LIMIT 1')) throw new Error('Already configured');
      this.run('INSERT INTO families(id,name) VALUES(?,?)', fid, name);
      for (const u of [
        { ...parent, name: normalizeAccountName(parent.name), id: pid, role: 'parent', pw: ph },
        ...(child ? [{ ...child, name: normalizeAccountName(child.name), id: cid, role: 'child', pw: ch! }] : []),
      ])
        this.run(
          'INSERT INTO users(id,family_id,name,name_key,role,password) VALUES(?,?,?,?,?,?)',
          u.id,
          fid,
          u.name,
          accountNameKey(u.name),
          u.role,
          u.pw,
        );
    });
    return { familyId: fid, parentId: pid, childId: cid };
  }
  user(row: any): User {
    return {
      id: row.id,
      familyId: row.family_id,
      role: row.role,
      name: row.name,
    };
  }
  createSession(user: User) {
    if (user.role === 'child' && this.childAccess(user.familyId, user.id).paused)
      throw new ChildAccessError(403, 'CHILD_ACCESS_PAUSED');
    const sid = token(),
      csrf = token(),
      now = Date.now();
    this.run(
      'INSERT INTO sessions VALUES(?,?,?,?,?,?)',
      hash(sid),
      user.familyId,
      user.id,
      csrf,
      now + this.config.sessionMs,
      now,
    );
    return { sid, csrf };
  }
  session(sid: string, activity = false): Session | undefined {
    const row = this.one(
      'SELECT u.*,s.csrf,s.expires,s.last FROM sessions s JOIN users u ON u.family_id=s.family_id AND u.id=s.user_id WHERE s.id=?',
      hash(sid),
    );
    if (!row) return;
    if (row.role === 'child' && this.childAccess(row.family_id, row.id).paused) return;
    if (
      row.expires < Date.now() ||
      (row.role === 'child' && row.last + this.config.childIdleMs < Date.now())
    ) {
      this.run('DELETE FROM sessions WHERE id=?', hash(sid));
      return;
    }
    if (activity) this.run('UPDATE sessions SET last=? WHERE id=?', Date.now(), hash(sid));
    return { user: this.user(row), sid, csrf: row.csrf, expires: row.expires, last: row.last };
  }
  allowed(user: User, g: string) {
    if (!this.knownGroup(user.familyId, g)) return false;
    if (user.role === 'parent') return true;
    if (this.childAccess(user.familyId, user.id).paused) return false;
    return !!this.one(
      'SELECT 1 FROM grants WHERE family_id=? AND user_id=? AND group_id=?',
      user.familyId,
      user.id,
      g,
    );
  }
  knownGroup(f: string, g: string) {
    return !!this.one('SELECT 1 FROM groups WHERE family_id=? AND id=?', f, g);
  }
  childAccess(f: string, c: string) {
    const row = this.one('SELECT paused,revision FROM child_access WHERE family_id=? AND child_id=?', f, c);
    return { paused: !!row?.paused, revision: Number(row?.revision || 0) };
  }
  setChildAccess(f: string, c: string, paused: boolean, revision: number) {
    // Usage settlement, policy transition and session deletion commit together.
    return this.tx(() => {
      if (!this.one("SELECT 1 FROM users WHERE family_id=? AND id=? AND role='child'", f, c))
        throw new ChildAccessError(404, 'NOT_FOUND');
      const current = this.childAccess(f, c);
      if (current.revision !== revision) {
        if (current.revision === revision + 1 && current.paused === paused) return current;
        throw new ChildAccessError(409, 'CHILD_ACCESS_CHANGED');
      }
      if (current.paused === paused) return current;
      this.usage.revokeUserInTransaction(f, c);
      this.run(`INSERT INTO child_access VALUES(?,?,?,?) ON CONFLICT(family_id,child_id)
        DO UPDATE SET paused=excluded.paused,revision=excluded.revision`, f, c, Number(paused), revision + 1);
      this.run('DELETE FROM sessions WHERE family_id=? AND user_id=?', f, c);
      return { paused, revision: revision + 1 };
    });
  }
  retained(f: string, g: string) {
    // The family parent's access is independent of child grants. Unknown groups
    // (including arbitrary JIDs and groups known only to another family) stay out.
    return this.knownGroup(f, g);
  }
  private ensureGroupKey(f: string, g: string) {
    if (!this.one('SELECT 1 FROM group_keys WHERE family_id=? AND group_id=?', f, g))
      this.run('INSERT INTO group_keys VALUES(?,?,?)', f, g,
        seal(this.config.masterKey, randomBytes(32), `${f}:key:${g}`));
  }
  groups(f: string): ChatGroup[] {
    return this.all('SELECT g.id,g.title,COALESCE((SELECT MAX(m.time) FROM messages m WHERE m.family_id=g.family_id AND m.group_id=g.id),0) AS lastMessageTime FROM groups g WHERE g.family_id=? ORDER BY lastMessageTime DESC,g.id ASC', f).map((x) => ({
      id: x.id,
      kind: conversationKind(x.id),
      lastMessageTime: x.lastMessageTime,
      title: open(this.config.masterKey, x.title, `${f}:title:${x.id}`).toString(),
    }));
  }
  syncGroups(f: string, groups: ChatGroup[]) {
    this.tx(() => {
      for (const g of groups)
        if (conversationKind(g.id)) {
          this.run(
            'INSERT INTO groups VALUES(?,?,?) ON CONFLICT(family_id,id) DO UPDATE SET title=excluded.title',
            f,
            g.id,
            seal(this.config.masterKey, g.title.slice(0, 200), `${f}:title:${g.id}`),
          );
          this.ensureGroupKey(f, g.id);
        }
    });
  }
  groupKey(f: string, g: string) {
    const row = this.one('SELECT key FROM group_keys WHERE family_id=? AND group_id=?', f, g);
    if (!row) throw new Error('No retained group');
    return open(this.config.masterKey, row.key, `${f}:key:${g}`);
  }
  grant(f: string, u: string, g: string, on: boolean) {
    if (
      !this.one('SELECT 1 FROM users WHERE family_id=? AND id=? AND role=?', f, u, 'child') ||
      !this.one('SELECT 1 FROM groups WHERE family_id=? AND id=?', f, g)
    )
      throw new Error('Unknown child or group');
    if (on) {
      this.run('INSERT OR IGNORE INTO grants VALUES(?,?,?)', f, u, g);
    } else {
      this.run('DELETE FROM grants WHERE family_id=? AND user_id=? AND group_id=?', f, u, g);
      this.run('DELETE FROM chat_unread WHERE family_id=? AND user_id=? AND group_id=?', f, u, g);
      // Revoking a child never revokes the parent or erases the parent’s copy.
    }
  }
  getMessage(f: string, g: string, id: string): StoredMessage | undefined {
    if (!this.retained(f, g)) return;
    const row = this.diagnostics.measure(f, 'db_read', () => this.one(
      'SELECT body FROM messages WHERE family_id=? AND group_id=? AND id=?', f, g, id));
    return row
      ? JSON.parse(open(this.groupKey(f, g), row.body, `${f}:${g}:${id}`).toString())
      : undefined;
  }
  putMessage(f: string, m: StoredMessage, live = true) {
    if (!this.retained(f, m.groupId)) return false;
    this.db.exec('SAVEPOINT message_with_unread');
    try {
      const isNew = !this.one('SELECT 1 FROM messages WHERE family_id=? AND group_id=? AND id=?', f, m.groupId, m.id);
      this.diagnostics.measure(f, 'db_write', () => this.run(
        'INSERT INTO messages VALUES(?,?,?,?,?) ON CONFLICT(family_id,group_id,id) DO UPDATE SET body=excluded.body,time=excluded.time',
        f,
        m.groupId,
        m.id,
        m.time,
        seal(this.groupKey(f, m.groupId), JSON.stringify(m), `${f}:${m.groupId}:${m.id}`),
      ));
      recordUnread(this, f, m, isNew, live);
      this.db.exec('RELEASE message_with_unread');
    } catch (error) {
      this.db.exec('ROLLBACK TO message_with_unread; RELEASE message_with_unread');
      throw error;
    }
    return true;
  }
  messages(f: string, g: string, cursor?: { time: number; id: string }, limit = 40) {
    if (!this.retained(f, g)) return [];
    const rows = cursor
      ? this.all(
          'SELECT id FROM messages WHERE family_id=? AND group_id=? AND (time<? OR (time=? AND id<?)) ORDER BY time DESC,id DESC LIMIT ?',
          f,
          g,
          cursor.time,
          cursor.time,
          cursor.id,
          limit,
        )
      : this.all(
          'SELECT id FROM messages WHERE family_id=? AND group_id=? ORDER BY time DESC,id DESC LIMIT ?',
          f,
          g,
          limit,
        );
    return rows.map((x) => this.getMessage(f, g, x.id)!).reverse();
  }
  mediaPath(f: string, g: string, id: string) {
    return join(this.config.dataDir, 'media', hash(`${f}:${g}:${id}`));
  }
  putMedia(f: string, g: string, id: string, messageId: string, bytes: Buffer) {
    if (!this.retained(f, g)) return false;
    const used = Number(
      this.one('SELECT coalesce(sum(size),0) AS n FROM media WHERE family_id=?', f)?.n,
    );
    if (bytes.length > this.config.maxMedia || used + bytes.length > this.config.maxStorage)
      throw new Error('Media storage limit');
    const p = this.mediaPath(f, g, id);
    const encrypted = seal(this.groupKey(f, g), bytes, `${f}:${g}:media:${id}`);
    this.diagnostics.measure(f, 'media_write', () => writeFileSync(p, encrypted, { mode: 0o600 }), encrypted.length);
    try {
      this.run('INSERT INTO media VALUES(?,?,?,?,?)', f, g, id, messageId, bytes.length);
    } catch (e) {
      unlinkSync(p);
      throw e;
    }
    return true;
  }
  getMedia(f: string, g: string, id: string) {
    if (!this.retained(f, g)) return;
    const row = this.one(
      'SELECT message_id,size FROM media WHERE family_id=? AND group_id=? AND id=?',
      f,
      g,
      id,
    );
    if (!row) return;
    const m = this.getMessage(f, g, row.message_id);
    if (!m || m.deleted) return;
    const p = this.mediaPath(f, g, id);
    if (!existsSync(p)) return;
    return {
      message: m,
      bytes: open(this.groupKey(f, g), this.diagnostics.measure(f, 'media_read', () => readFileSync(p), row.size || statSync(p).size), `${f}:${g}:media:${id}`),
    };
  }
  deleteMedia(f: string, g: string, id: string) {
    this.run('DELETE FROM media WHERE family_id=? AND group_id=? AND id=?', f, g, id);
    const p = this.mediaPath(f, g, id);
    if (existsSync(p)) unlinkSync(p);
  }
  purgeGroup(f: string, g: string) {
    const media = this.all('SELECT id FROM media WHERE family_id=? AND group_id=?', f, g);
    this.tx(() => {
      this.run('DELETE FROM media WHERE family_id=? AND group_id=?', f, g);
      this.run('DELETE FROM messages WHERE family_id=? AND group_id=?', f, g);
      this.run('DELETE FROM group_keys WHERE family_id=? AND group_id=?', f, g);
    });
    for (const m of media) {
      const p = this.mediaPath(f, g, m.id);
      if (existsSync(p)) unlinkSync(p);
    }
    this.db.exec('PRAGMA wal_checkpoint(TRUNCATE)');
  }
  identityLabel(f: string, u: string) {
    const row = this.one('SELECT value FROM identity_labels WHERE family_id=? AND user_id=?', f, u);
    return row ? open(this.config.masterKey, row.value, `${f}:identity:${u}`).toString() : '';
  }
  setIdentityLabel(f: string, u: string, label: string) {
    if (!label) this.run('DELETE FROM identity_labels WHERE family_id=? AND user_id=?', f, u);
    else
      this.run(
        'INSERT INTO identity_labels VALUES(?,?,?) ON CONFLICT(family_id,user_id) DO UPDATE SET value=excluded.value',
        f,
        u,
        seal(this.config.masterKey, label, `${f}:identity:${u}`),
      );
  }
  authGet(f: string, key: string) {
    const r = this.one('SELECT value FROM auth WHERE family_id=? AND key=?', f, key);
    return r ? open(this.config.masterKey, r.value, `${f}:auth:${key}`).toString() : undefined;
  }
  authSet(f: string, key: string, value: string | null) {
    if (value === null) this.diagnostics.measure(f, 'db_write', () => this.run('DELETE FROM auth WHERE family_id=? AND key=?', f, key));
    else
      this.diagnostics.measure(f, 'db_write', () => this.run(
        'INSERT INTO auth VALUES(?,?,?) ON CONFLICT(family_id,key) DO UPDATE SET value=excluded.value',
        f,
        key,
        seal(this.config.masterKey, value, `${f}:auth:${key}`),
      ));
  }
  eraseChats(f: string) {
    for (const { id } of this.groups(f)) this.purgeGroup(f, id);
    this.tx(() => {
      this.run('DELETE FROM grants WHERE family_id=?', f);
      this.run('DELETE FROM groups WHERE family_id=?', f);
      this.run('DELETE FROM auth WHERE family_id=?', f);
      this.run('UPDATE families SET linked=0 WHERE id=?', f);
    });
    this.db.exec('PRAGMA wal_checkpoint(TRUNCATE)');
  }
  cleanOrphans() {
    /* Remove encrypted orphan files left by an interrupted transaction. */ const dir = join(
      this.config.dataDir,
      'media',
    );
    const keep = new Set(
      this.all('SELECT family_id,group_id,id FROM media').map((x) =>
        hash(`${x.family_id}:${x.group_id}:${x.id}`),
      ),
    );
    for (const name of requireFiles(dir)) if (!keep.has(name)) unlinkSync(join(dir, name));
  }
  close() {
    this.db.close();
  }
}
import { readdirSync as requireFiles } from 'node:fs';
