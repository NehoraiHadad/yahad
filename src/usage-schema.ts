import type { DatabaseSync } from 'node:sqlite';

// Schema 3 is a policy boundary: schema-2 binaries must reject this database.
export function migrateUsage(db: DatabaseSync) {
  db.exec('BEGIN IMMEDIATE');
  try {
    db.exec(`
      CREATE TABLE IF NOT EXISTS usage_families(family_id TEXT PRIMARY KEY REFERENCES families(id) ON DELETE CASCADE,timezone TEXT NOT NULL DEFAULT 'UTC');
      CREATE TABLE IF NOT EXISTS usage_policy(family_id TEXT NOT NULL,child_id TEXT NOT NULL,minutes INTEGER CHECK(minutes IS NULL OR minutes BETWEEN 0 AND 1000000),cursor INTEGER NOT NULL,PRIMARY KEY(family_id,child_id),FOREIGN KEY(family_id,child_id) REFERENCES users(family_id,id) ON DELETE CASCADE);
      CREATE TABLE IF NOT EXISTS usage_days(family_id TEXT NOT NULL,child_id TEXT NOT NULL,day TEXT NOT NULL,used_ms INTEGER NOT NULL DEFAULT 0 CHECK(used_ms>=0),bonus_ms INTEGER NOT NULL DEFAULT 0 CHECK(bonus_ms>=0),PRIMARY KEY(family_id,child_id,day),FOREIGN KEY(family_id,child_id) REFERENCES users(family_id,id) ON DELETE CASCADE);
      CREATE TABLE IF NOT EXISTS usage_leases(family_id TEXT NOT NULL,child_id TEXT NOT NULL,session_hash TEXT NOT NULL,tab TEXT NOT NULL,seq INTEGER NOT NULL,until INTEGER NOT NULL,PRIMARY KEY(family_id,child_id,session_hash,tab),FOREIGN KEY(family_id,child_id) REFERENCES users(family_id,id) ON DELETE CASCADE);
      CREATE INDEX IF NOT EXISTS usage_session ON usage_leases(session_hash);
      CREATE TABLE IF NOT EXISTS usage_bonus(family_id TEXT NOT NULL,child_id TEXT NOT NULL,request_id TEXT NOT NULL,day TEXT NOT NULL,minutes INTEGER NOT NULL,PRIMARY KEY(family_id,child_id,request_id),FOREIGN KEY(family_id,child_id) REFERENCES users(family_id,id) ON DELETE CASCADE);
      UPDATE schema_version SET version=3 WHERE version=2;
    `);
    if (db.prepare('PRAGMA foreign_key_check').all().length) throw new Error('Usage migration would break references');
    db.exec('COMMIT');
  } catch (error) {
    if (db.isTransaction) db.exec('ROLLBACK');
    throw error;
  }
}

/** A scheduled calendar is an enforcement policy that schema-3 code must reject. */
export function migrateUsageCalendar(db: DatabaseSync) {
  db.exec('BEGIN IMMEDIATE');
  try {
    db.exec(`CREATE TABLE IF NOT EXISTS usage_calendar(
      family_id TEXT PRIMARY KEY REFERENCES families(id) ON DELETE CASCADE,
      era INTEGER NOT NULL DEFAULT 0 CHECK(era>=0),
      pending_timezone TEXT,pending_at INTEGER,frozen_day TEXT,
      CHECK((pending_timezone IS NULL AND pending_at IS NULL AND frozen_day IS NULL) OR
        (pending_timezone IS NOT NULL AND pending_at IS NOT NULL AND frozen_day IS NOT NULL))
    ); UPDATE schema_version SET version=4 WHERE version=3;`);
    if (db.prepare('PRAGMA foreign_key_check').all().length) throw new Error('Calendar migration would break references');
    db.exec('COMMIT');
  } catch (error) {
    if (db.isTransaction) db.exec('ROLLBACK');
    throw error;
  }
}

/** Pending admissions are not billable leases. Schema-4 code would include their
 * reservation deadlines in settlement, so it must refuse this policy version. */
export function migrateUsageAdmission(db: DatabaseSync) {
  db.exec('BEGIN IMMEDIATE');
  try {
    db.exec('UPDATE schema_version SET version=5 WHERE version=4');
    db.exec('COMMIT');
  } catch (error) {
    if (db.isTransaction) db.exec('ROLLBACK');
    throw error;
  }
}
