import type { DatabaseSync } from 'node:sqlite';

export class ChildAccessError extends Error {
  constructor(readonly status: number, readonly code: string) { super(code); }
}

/** A policy boundary: older binaries must refuse a database containing pauses. */
export function migrateChildAccess(db: DatabaseSync) {
  db.exec('BEGIN IMMEDIATE');
  try {
    db.exec(`CREATE TABLE IF NOT EXISTS child_access(
      family_id TEXT NOT NULL,child_id TEXT NOT NULL,
      paused INTEGER NOT NULL CHECK(paused IN (0,1)),
      revision INTEGER NOT NULL CHECK(revision>=0),
      PRIMARY KEY(family_id,child_id),
      FOREIGN KEY(family_id,child_id) REFERENCES users(family_id,id) ON DELETE CASCADE
    ); UPDATE schema_version SET version=6 WHERE version=5;`);
    if (db.prepare('PRAGMA foreign_key_check').all().length) throw new Error('Child access migration would break references');
    db.exec('COMMIT');
  } catch (error) {
    if (db.isTransaction) db.exec('ROLLBACK');
    throw error;
  }
}
