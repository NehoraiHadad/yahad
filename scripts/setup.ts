import { loadEnvFile } from 'node:process';
import { existsSync, mkdirSync, writeFileSync, chmodSync } from 'node:fs';
import { resolve, join } from 'node:path';
import { randomBytes } from 'node:crypto';
import { ask } from './prompt.js';
import { loadConfig } from '../src/config.js';
import { Store } from '../src/store.js';
import { acquireDataLock } from '../src/lock.js';
try {
  loadEnvFile('.env');
} catch {}
try {
  const data = resolve(process.env.DATA_DIR || './data');
  const keyPath = resolve(process.env.MASTER_KEY_FILE || './.local/master.key');
  if (!existsSync(keyPath)) mkdirSync(resolve(keyPath, '..'), { recursive: true, mode: 0o700 });
  if (!existsSync(keyPath))
    writeFileSync(keyPath, randomBytes(32).toString('base64'), { mode: 0o600, flag: 'wx' });
  chmodSync(keyPath, 0o600);
  process.env.DATA_DIR = data;
  process.env.MASTER_KEY_FILE = keyPath;
  process.env.APP_ORIGIN ||= 'http://localhost:3000';
  process.env.COOKIE_SECURE ||= 'false';
  const config = loadConfig();
  const release = await acquireDataLock(data);
  const store = new Store(config);
  try {
    const name = await ask('Family label (local only): ');
    const parent = await ask('Parent username (unique): '),
      child = await ask('Child username (unique): ');
    for (const u of [parent, child])
      if (!/^[a-z0-9._-]{3,60}$/.test(u))
        throw new Error('Use 3–60 lowercase letters, numbers, dot, dash, underscore');
    if (parent === child) throw new Error('Usernames must differ');
    const pp = await ask('Parent password, 14+ characters: ', true),
      cp = await ask('Child passphrase, 14+ characters: ', true);
    if (pp === cp) throw new Error('Use separate passwords');
    await store.createFamily(
      name || 'Family',
      { username: parent, password: pp },
      { username: child, password: cp },
    );
    if (process.env.SETUP_WRITE_ENV !== 'false' && !existsSync('.env'))
      writeFileSync(
        '.env',
        `DATA_DIR=${data}\nMASTER_KEY_FILE=${keyPath}\nAPP_ORIGIN=${config.origin}\nCOOKIE_SECURE=${config.secureCookie}\nCONNECTOR=mock\nALLOW_REAL_WHATSAPP=no\nHOST=127.0.0.1\nPORT=3000\n`,
        { mode: 0o600, flag: 'wx' },
      );
    console.log(
      'Family created. Keep the passwords in your password manager. Start with npm run build && npm start.',
    );
  } finally {
    store.close();
    await release();
  }
} catch {
  console.error(
    'Setup did not complete. Check names, password length, permissions, and that the service is stopped.',
  );
  process.exitCode = 1;
}
