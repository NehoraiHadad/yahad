import { test } from 'node:test';
import assert from 'node:assert/strict';
import { DatabaseSync } from 'node:sqlite';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { randomBytes } from 'node:crypto';
import { migrateAccountNames, DATABASE_SCHEMA } from '../src/db.js';
import { normalizeAccountName, accountNameKey } from '../src/account-name.js';
import { Store } from '../src/store.js';
import type { Config } from '../src/config.js';

// Raw v1 fixtures intentionally do not depend on the current Store schema.
// Every account, encrypted-looking byte sequence and credential here is synthetic.
function legacy(db = new DatabaseSync(':memory:'), duplicateKey = false) {
  db.exec(`PRAGMA foreign_keys=ON;
    CREATE TABLE schema_version(version INTEGER NOT NULL); INSERT INTO schema_version VALUES(1);
    CREATE TABLE families(id TEXT PRIMARY KEY,name TEXT NOT NULL,linked INTEGER NOT NULL DEFAULT 0);
    CREATE TABLE users(id TEXT NOT NULL,family_id TEXT NOT NULL REFERENCES families(id) ON DELETE CASCADE,username TEXT NOT NULL UNIQUE,display TEXT NOT NULL,role TEXT NOT NULL,password TEXT NOT NULL,PRIMARY KEY(family_id,id));
    CREATE TABLE sessions(id TEXT PRIMARY KEY,family_id TEXT NOT NULL,user_id TEXT NOT NULL,csrf TEXT NOT NULL,expires INTEGER NOT NULL,last INTEGER NOT NULL,FOREIGN KEY(family_id,user_id) REFERENCES users(family_id,id) ON DELETE CASCADE);
    CREATE TABLE groups(family_id TEXT NOT NULL REFERENCES families(id),id TEXT NOT NULL,title BLOB NOT NULL,PRIMARY KEY(family_id,id));
    CREATE TABLE grants(family_id TEXT NOT NULL,user_id TEXT NOT NULL,group_id TEXT NOT NULL,PRIMARY KEY(family_id,user_id,group_id),FOREIGN KEY(family_id,user_id) REFERENCES users(family_id,id) ON DELETE CASCADE,FOREIGN KEY(family_id,group_id) REFERENCES groups(family_id,id));
    CREATE TABLE messages(family_id TEXT NOT NULL,group_id TEXT NOT NULL,id TEXT NOT NULL,time INTEGER NOT NULL,body BLOB NOT NULL,PRIMARY KEY(family_id,group_id,id),FOREIGN KEY(family_id,group_id) REFERENCES groups(family_id,id));
    CREATE TABLE auth(family_id TEXT NOT NULL REFERENCES families(id),key TEXT NOT NULL,value BLOB NOT NULL,PRIMARY KEY(family_id,key));
    CREATE TABLE identity_labels(family_id TEXT NOT NULL,user_id TEXT NOT NULL,value BLOB NOT NULL,PRIMARY KEY(family_id,user_id),FOREIGN KEY(family_id,user_id) REFERENCES users(family_id,id) ON DELETE CASCADE);
    CREATE TABLE instance_metadata(key TEXT PRIMARY KEY,value TEXT NOT NULL);
    INSERT INTO families VALUES('family-a','Synthetic family',1);
    INSERT INTO users VALUES('parent-id','family-a','parent','הורה','parent','synthetic-parent-password-hash');
    INSERT INTO users VALUES('child-a','family-a','adi','אני','child','synthetic-child-a-password-hash');
    INSERT INTO users VALUES('child-b','family-a','${duplicateKey ? 'ADI' : 'tamar'}','אני','child','synthetic-child-b-password-hash');
    INSERT INTO sessions VALUES('session-parent','family-a','parent-id','synthetic-csrf-parent',1000,100);
    INSERT INTO sessions VALUES('session-child','family-a','child-a','synthetic-csrf-child',1000,100);
    INSERT INTO groups VALUES('family-a','group-a',x'010203');
    INSERT INTO grants VALUES('family-a','child-a','group-a');
    INSERT INTO grants VALUES('family-a','child-b','group-a');
    INSERT INTO messages VALUES('family-a','group-a','message-a',123,x'040506');
    INSERT INTO auth VALUES('family-a','synthetic-connector-key',x'070809');
    INSERT INTO identity_labels VALUES('family-a','child-a',x'0a0b0c');
    INSERT INTO instance_metadata VALUES('connector','mock');`);
  return db;
}
const rows = (db: DatabaseSync, table: string) => db.prepare(`SELECT * FROM ${table} ORDER BY rowid`).all().map(row => ({ ...row }));
const retainedTables = ['families', 'sessions', 'groups', 'grants', 'messages', 'auth', 'identity_labels', 'instance_metadata'];
const snapshot = (db: DatabaseSync) => Object.fromEntries(retainedTables.map(table => [table, rows(db, table)]));
const noTemporaryTable = (db: DatabaseSync) => assert.equal(db.prepare("SELECT 1 FROM sqlite_master WHERE name='users_next'").get(), undefined);
const foreignKeysOn = (db: DatabaseSync) => assert.equal(db.prepare('PRAGMA foreign_keys').get()?.foreign_keys, 1);

test('single-name normalization supports Hebrew and canonical uniqueness without accepting invisible controls', () => {
  assert.equal(normalizeAccountName('  עדי   כהן  '), 'עדי כהן');
  assert.equal(normalizeAccountName('לי'), 'לי');
  assert.equal(normalizeAccountName('e\u0301di'), 'édi');
  assert.equal(accountNameKey('  ÉDI  '), accountNameKey('e\u0301di'));
  assert.equal(normalizeAccountName('A._-9'), 'A._-9');
  assert.equal(normalizeAccountName('נ'.repeat(60)), 'נ'.repeat(60));
  for (const name of ['', '   ', '._-', 'נ'.repeat(61), 'a\nb', '\ta', 'a\u0000b', 'a\u202eb', 'a\u2066b', 'a\u200bb', 'a\u00a0b', '🙂'])
    assert.throws(() => normalizeAccountName(name), /Invalid account name/, JSON.stringify(name));
});

test('v1 migration keeps unique login names and all durable identity references, not duplicate legacy display labels', () => {
  const db = legacy();
  try {
    const before = snapshot(db), users = rows(db, 'users');
    migrateAccountNames(db);
    assert.equal(db.prepare('SELECT version FROM schema_version').get()?.version, 2);
    assert.deepEqual(rows(db, 'users'), users.map(({ username, display: _display, ...user }) => ({
      ...user, name: username, name_key: accountNameKey(String(username)),
    })));
    const columns = db.prepare('PRAGMA table_info(users)').all().map(column => column.name);
    assert.ok(!columns.includes('username')); assert.ok(!columns.includes('display'));
    assert.deepEqual(snapshot(db), before, 'sessions, grants, password-independent data and connector bytes remain unchanged');
    assert.deepEqual(db.prepare('PRAGMA foreign_key_check').all(), []);
    foreignKeysOn(db); noTemporaryTable(db);
    assert.throws(() => db.prepare('INSERT INTO grants VALUES(?,?,?)').run('family-a', 'missing', 'group-a'), /FOREIGN KEY/);
    const migrated = rows(db, 'users');
    migrateAccountNames(db);
    assert.deepEqual(rows(db, 'users'), migrated, 'second startup is idempotent');
    assert.deepEqual(snapshot(db), before);
  } finally { db.close(); }
});

test('normalized-name collision rolls back completely rather than choosing aliases or losing accounts', () => {
  const db = legacy(undefined, true);
  try {
    const users = rows(db, 'users'), before = snapshot(db);
    assert.throws(() => migrateAccountNames(db), /UNIQUE/);
    assert.equal(db.prepare('SELECT version FROM schema_version').get()?.version, 1);
    assert.deepEqual(rows(db, 'users'), users); assert.deepEqual(snapshot(db), before);
    assert.equal(db.isTransaction, false); foreignKeysOn(db); noTemporaryTable(db);
  } finally { db.close(); }
});

test('failure after dropping the old table rolls back rows and schema, then a clean retry succeeds', () => {
  const db = legacy();
  try {
    const users = rows(db, 'users'), before = snapshot(db);
    const interrupted = {
      prepare: db.prepare.bind(db),
      get isTransaction() { return db.isTransaction; },
      exec(sql: string) {
        if (sql.includes('DROP TABLE users; ALTER TABLE users_next')) {
          db.exec('DROP TABLE users');
          throw new Error('Synthetic interruption after DROP');
        }
        db.exec(sql);
      },
    } as unknown as DatabaseSync;
    assert.throws(() => migrateAccountNames(interrupted), /Synthetic interruption/);
    assert.equal(db.prepare('SELECT version FROM schema_version').get()?.version, 1);
    assert.deepEqual(rows(db, 'users'), users); assert.deepEqual(snapshot(db), before);
    assert.equal(db.isTransaction, false); foreignKeysOn(db); noTemporaryTable(db);
    migrateAccountNames(db);
    assert.equal(db.prepare('SELECT version FROM schema_version').get()?.version, 2);
    assert.deepEqual(snapshot(db), before);
    assert.deepEqual(db.prepare('PRAGMA foreign_key_check').all(), []);
  } finally { db.close(); }
});

test('unsupported schemas and invalid references fail before committing any account migration', () => {
  for (const version of [0, DATABASE_SCHEMA + 1]) {
    const db = legacy();
    try {
      db.prepare('UPDATE schema_version SET version=?').run(version);
      const users = rows(db, 'users'), before = snapshot(db);
      assert.throws(() => migrateAccountNames(db), /Unsupported database schema/);
      assert.deepEqual(rows(db, 'users'), users); assert.deepEqual(snapshot(db), before);
      assert.equal(db.prepare('SELECT version FROM schema_version').get()?.version, version);
      foreignKeysOn(db); noTemporaryTable(db);
    } finally { db.close(); }
  }
  const db = legacy();
  try {
    db.exec("PRAGMA foreign_keys=OFF; INSERT INTO sessions VALUES('orphan','family-a','missing','synthetic',1000,100); PRAGMA foreign_keys=ON;");
    const users = rows(db, 'users'), before = snapshot(db);
    assert.throws(() => migrateAccountNames(db), /Migration would break account references/);
    assert.deepEqual(rows(db, 'users'), users); assert.deepEqual(snapshot(db), before);
    assert.equal(db.prepare('SELECT version FROM schema_version').get()?.version, 1);
    foreignKeysOn(db); noTemporaryTable(db);
  } finally { db.close(); }
});

test('missing schema metadata is accepted only for a truly empty database', () => {
  const empty = new DatabaseSync(':memory:');
  try {
    migrateAccountNames(empty);
    assert.deepEqual(empty.prepare("SELECT name FROM sqlite_master WHERE type='table'").all(), []);
  } finally { empty.close(); }
  const db = legacy();
  try {
    db.exec('DROP TABLE schema_version');
    const users = rows(db, 'users'), before = snapshot(db);
    assert.throws(() => migrateAccountNames(db), /schema/i);
    assert.deepEqual(rows(db, 'users'), users); assert.deepEqual(snapshot(db), before);
    assert.equal(db.prepare("SELECT 1 FROM sqlite_master WHERE name='schema_version'").get(), undefined);
    noTemporaryTable(db); foreignKeysOn(db);
  } finally { db.close(); }
  const unknown = new DatabaseSync(':memory:');
  try {
    unknown.exec('CREATE TABLE unrelated(value TEXT); INSERT INTO unrelated VALUES(\'synthetic\')');
    assert.throws(() => migrateAccountNames(unknown), /schema/i);
    assert.equal(unknown.prepare('SELECT value FROM unrelated').get()?.value, 'synthetic');
    noTemporaryTable(unknown);
  } finally { unknown.close(); }
});

test('wrong connector startup rejects before migrating a legacy data directory', () => {
  const dir = mkdtempSync(join(tmpdir(), 'yachad-name-preflight-'));
  const path = join(dir, 'app.sqlite'), original = legacy(new DatabaseSync(path));
  const users = rows(original, 'users'), before = snapshot(original);
  original.close();
  const config: Config = { dataDir: dir, masterKey: randomBytes(32), connector: 'baileys', realAllowed: false,
    host: '127.0.0.1', port: 3000, origin: 'http://localhost:3000', secureCookie: false,
    maxMedia: 1024 ** 2, maxStorage: 64 * 1024 ** 2, childIdleMs: 900000, sessionMs: 3600000, diagnosticsEnabled: false };
  try {
    assert.throws(() => new Store(config), /separate data directory/);
    const after = new DatabaseSync(path);
    try {
      assert.equal(after.prepare('SELECT version FROM schema_version').get()?.version, 1,
        'a rejected connector mode must not upgrade the database');
      assert.deepEqual(rows(after, 'users'), users); assert.deepEqual(snapshot(after), before); noTemporaryTable(after);
    } finally { after.close(); }
  } finally { rmSync(dir, { recursive: true, force: true }); }
});
