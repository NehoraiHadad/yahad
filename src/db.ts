import type { DatabaseSync } from 'node:sqlite';
import { normalizeAccountName, accountNameKey } from './account-name.js';
import { DATABASE_SCHEMA } from './database-schema.js';
export { DATABASE_SCHEMA } from './database-schema.js';
export const USERS_TABLE = `CREATE TABLE users(id TEXT NOT NULL,family_id TEXT NOT NULL REFERENCES families(id) ON DELETE CASCADE,name TEXT NOT NULL,name_key TEXT NOT NULL UNIQUE,role TEXT NOT NULL CHECK(role IN ('parent','child')),password TEXT NOT NULL,PRIMARY KEY(family_id,id))`;

/** A single atomic transition. Existing login names win; legacy display labels are not identities. */
export function migrateAccountNames(db: DatabaseSync) {
  if (!db.prepare("SELECT 1 FROM sqlite_master WHERE type='table' AND name='schema_version'").get()) {
    if (db.prepare("SELECT 1 FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' LIMIT 1").get())
      throw new Error('Missing database schema version');
    return;
  }
  const versions = db.prepare('SELECT version FROM schema_version').all();
  if (versions.length !== 1 || ![1, 2, DATABASE_SCHEMA].includes(Number(versions[0].version)))
    throw new Error('Unsupported database schema');
  if (Number(versions[0].version) >= 2) return;
  // Rebuilding the parent table must not cascade-delete sessions/grants. SQLite
  // only allows switching FK enforcement outside a transaction.
  db.exec('PRAGMA foreign_keys=OFF');
  try {
    db.exec('BEGIN IMMEDIATE');
    db.exec(USERS_TABLE.replace('CREATE TABLE users(', 'CREATE TABLE users_next('));
    const insert = db.prepare('INSERT INTO users_next(id,family_id,name,name_key,role,password) VALUES(?,?,?,?,?,?)');
    for (const user of db.prepare('SELECT id,family_id,username,role,password FROM users').all()) {
      const name = normalizeAccountName(String(user.username));
      insert.run(user.id, user.family_id, name, accountNameKey(name), user.role, user.password);
    }
    db.exec('DROP TABLE users; ALTER TABLE users_next RENAME TO users; UPDATE schema_version SET version=2;');
    if (db.prepare('PRAGMA foreign_key_check').all().length) throw new Error('Migration would break account references');
    db.exec('COMMIT');
  } catch (error) {
    if (db.isTransaction) db.exec('ROLLBACK');
    throw error;
  } finally {
    db.exec('PRAGMA foreign_keys=ON');
  }
}
