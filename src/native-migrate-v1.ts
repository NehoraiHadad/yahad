// Fixed offline migration entry. No Store, configuration, ChatService, server or
// provider import: only the already-existing schema migrations run on a clone.
import { DatabaseSync } from 'node:sqlite';
import { createHash } from 'node:crypto';
import { closeSync, fsyncSync, lstatSync, openSync, readFileSync, writeFileSync } from 'node:fs';
import { basename, dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { migrateAccountNames } from './db.js';
import { migrateUsage, migrateUsageCalendar, migrateUsageAdmission } from './usage-schema.js';
import { migrateChildAccess } from './child-access.js';
import { DATABASE_SCHEMA, DATABASE_MIGRATIONS_FROM } from './database-schema.js';

const id = /^[a-f0-9]{32}$/, digest = /^[a-f0-9]{64}$/;
const same = (left: string, right: string) => process.platform === 'win32'
  ? resolve(left).toLowerCase() === resolve(right).toLowerCase() : resolve(left) === resolve(right);
function required(value: unknown): asserts value { if (!value) throw new Error('NATIVE_MIGRATION_REFUSED'); }
function regular(path: string, maximum: number) {
  const stat = lstatSync(path);
  required(stat.isFile() && !stat.isSymbolicLink() && stat.nlink === 1 && stat.size > 0 && stat.size <= maximum);
}
function directories(path: string, root: string) {
  let current = resolve(path);
  while (true) {
    const stat = lstatSync(current); required(stat.isDirectory() && !stat.isSymbolicLink());
    if (same(current, root)) return;
    const parent = dirname(current); required(parent !== current); current = parent;
  }
}
function schema(db: DatabaseSync) {
  const rows = db.prepare('SELECT version FROM schema_version').all();
  required(rows.length === 1 && Number.isSafeInteger(rows[0].version));
  return Number(rows[0].version);
}
function integrity(db: DatabaseSync) {
  const rows = db.prepare('PRAGMA integrity_check').all();
  required(rows.length === 1 && Object.values(rows[0]).length === 1 && Object.values(rows[0])[0] === 'ok');
  required(db.prepare('PRAGMA foreign_key_check').all().length === 0);
}
export function runNativeMigration(requestFile: string) {
  required(Number(process.versions.node.split('.')[0]) === 24);
  const entry = fileURLToPath(import.meta.url), payload = resolve(dirname(entry), '../..'), workspace = dirname(payload);
  const control = resolve(workspace, '../..'), work = join(workspace, 'work');
  required(basename(dirname(workspace)) === 'migration-staging' && same(requestFile, join(work, 'request.json')));
  directories(work, control); regular(requestFile, 8192);
  const request = JSON.parse(readFileSync(requestFile, 'utf8'));
  const fields = ['format', 'installationId', 'operationId', 'sourceDataId', 'cloneOperationId', 'newDataId', 'sourceSchema', 'targetSchema', 'payloadDigest', 'entryDigest', 'dataDirectory', 'resultFile', 'nonce'];
  required(request && typeof request === 'object' && !Array.isArray(request) && Object.keys(request).length === fields.length && fields.every(name => Object.hasOwn(request, name)));
  required(request.format === 1 && ['installationId', 'operationId', 'sourceDataId', 'cloneOperationId', 'newDataId', 'nonce'].every(name => typeof request[name] === 'string' && id.test(request[name])));
  required(basename(workspace) === request.operationId && request.sourceDataId !== request.newDataId && digest.test(request.payloadDigest) && digest.test(request.entryDigest));
  required(request.targetSchema === DATABASE_SCHEMA && Number.isSafeInteger(request.sourceSchema) && request.sourceSchema < request.targetSchema &&
    (DATABASE_MIGRATIONS_FROM as readonly number[]).includes(request.sourceSchema));
  required(same(request.resultFile, join(work, 'result.json')) && same(request.dataDirectory,
    join(control, 'datasets', request.sourceDataId, 'restore-' + request.cloneOperationId, 'completed', 'data')));
  required(createHash('sha256').update(readFileSync(entry)).digest('hex') === request.entryDigest);
  directories(request.dataDirectory, control);
  const database = join(request.dataDirectory, 'app.sqlite'); regular(database, 256 * 1024 * 1024);
  let db: DatabaseSync | undefined;
  try {
    db = new DatabaseSync(database);
    db.exec('PRAGMA foreign_keys=ON; PRAGMA trusted_schema=OFF; PRAGMA synchronous=FULL;');
    required(schema(db) === request.sourceSchema); integrity(db);
    migrateAccountNames(db); migrateUsage(db); migrateUsageCalendar(db); migrateUsageAdmission(db); migrateChildAccess(db);
    required(schema(db) === request.targetSchema); integrity(db);
  } finally { db?.close(); }
  // This is a diagnostic result. The Authority independently inspects SQLite,
  // data/key/policy closure, the actual token and the empty owned process tree.
  const result = { format: 1, operationId: request.operationId, nonce: request.nonce,
    sourceSchema: request.sourceSchema, targetSchema: request.targetSchema, payloadDigest: request.payloadDigest };
  const fd = openSync(request.resultFile, 'wx', 0o600);
  try { writeFileSync(fd, JSON.stringify(result)); fsyncSync(fd); } finally { closeSync(fd); }
}
if (process.argv[1] && same(fileURLToPath(import.meta.url), fileURLToPath(pathToFileURL(resolve(process.argv[1]))))) {
  try {
    required(process.argv.length === 4 && process.argv[2] === '--request'); runNativeMigration(resolve(process.argv[3]));
  } catch { process.stderr.write('NATIVE_MIGRATION_REFUSED\n'); process.exitCode = 1; }
}
