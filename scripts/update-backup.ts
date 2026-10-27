// Called as the unprivileged application user, only while systemd has stopped the app.
// Passphrase arrives through stdin from the root-only updater, never argv/env/logs.
import { createBackup, restoreBackup } from './backup-lib.js';
import { mkdtempSync, rmSync } from 'node:fs';
import { join } from 'node:path';
const [data, key, destination, verify] = process.argv.slice(2);
try {
  let bytes = 0, chunks: Buffer[] = [];
  for await (const b of process.stdin) {
    bytes += b.length;
    if (bytes > 1024) throw new Error('Invalid input');
    chunks.push(b);
  }
  const pass = Buffer.concat(chunks).toString('utf8').trim();
  if (!data || !key || !destination) throw new Error('Missing paths');
  const archive = await createBackup(data, key, destination, pass);
  if (verify === '--verify') {
    const temporary = mkdtempSync(join(destination, 'install-restore-'));
    try { await restoreBackup(archive, join(temporary, 'restored'), pass); }
    finally { rmSync(temporary, { recursive: true, force: true }); }
  } else if (verify !== undefined) throw new Error('Unknown backup option');
  process.stdout.write('BACKUP_OK\n');
} catch { process.stderr.write('UPDATE_BACKUP_FAILED\n'); process.exitCode = 1; }
