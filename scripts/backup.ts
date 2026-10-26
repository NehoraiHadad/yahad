import { loadEnvFile } from 'node:process';
import { resolve } from 'node:path';
import { loadConfig } from '../src/config.js';
import { ask, closePrompt } from './prompt.js';
import { createBackup, restoreBackup } from './backup-lib.js';
try {
  loadEnvFile('.env');
} catch {}
const [action, arg, target] = process.argv.slice(2);
try {
  const pass = await ask('Backup passphrase, 18+ characters (not the app password): ', true);
  if (action === 'create') {
    if (!arg) throw new Error('Output directory required');
    const config = loadConfig();
    const path = await createBackup(
      config.dataDir,
      process.env.MASTER_KEY_FILE!,
      resolve(arg),
      pass,
    );
    console.log('Encrypted backup created:', path);
  } else if (action === 'restore') {
    if (!arg || !target) throw new Error('Archive and new target required');
    const out = await restoreBackup(resolve(arg), resolve(target), pass);
    console.log(
      'Authenticated restore complete. All browser sessions invalidated. Configure DATA_DIR and MASTER_KEY_FILE from the restored directory. Start in mock mode and review grants before real connection.',
    );
  } else throw new Error('Use create OUT_DIR or restore ARCHIVE NEW_DIR');
} catch {
  console.error(
    'Backup operation failed. Check passphrase, paths, permissions, and that the service is stopped. No backup contents logged.',
  );
  process.exitCode = 1;
} finally { closePrompt(); }
