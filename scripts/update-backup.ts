// Called as the unprivileged application user, only while systemd has stopped the app.
// Passphrase arrives through stdin from the root-only updater, never argv/env/logs.
import { createBackup } from './backup-lib.js';
const [data, key, destination] = process.argv.slice(2);
try {
  let bytes = 0, chunks: Buffer[] = [];
  for await (const b of process.stdin) {
    bytes += b.length;
    if (bytes > 1024) throw new Error('Invalid input');
    chunks.push(b);
  }
  const pass = Buffer.concat(chunks).toString('utf8').trim();
  if (!data || !key || !destination) throw new Error('Missing paths');
  await createBackup(data, key, destination, pass);
  process.stdout.write('BACKUP_OK\n');
} catch { process.stderr.write('UPDATE_BACKUP_FAILED\n'); process.exitCode = 1; }
