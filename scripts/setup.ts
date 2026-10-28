import { loadEnvFile } from 'node:process';
import { existsSync, mkdirSync, writeFileSync, chmodSync } from 'node:fs';
import { resolve, join } from 'node:path';
import { randomBytes } from 'node:crypto';
import { ask, closePrompt } from './prompt.js';
import { createApp } from '../src/server.js';
import { token } from '../src/crypto.js';
import { loadConfig } from '../src/config.js';
import { Store } from '../src/store.js';
import { acquireDataLock } from '../src/lock.js';
import { normalizeAccountName, accountNameKey } from '../src/account-name.js';
try {
  loadEnvFile('.env');
} catch {}
try {
  const data = resolve(process.env.DATA_DIR || './data');
  const keyPath = resolve(process.env.MASTER_KEY_FILE || './.local/master.key');
  if (existsSync(join(data, 'app.sqlite')) && !existsSync(keyPath))
    throw new Error('Existing database requires its original master key');
  if (!existsSync(keyPath)) mkdirSync(resolve(keyPath, '..'), { recursive: true, mode: 0o700 });
  if (!existsSync(keyPath))
    writeFileSync(keyPath, randomBytes(32).toString('base64'), { mode: 0o600, flag: 'wx' });
  chmodSync(keyPath, 0o600);
  process.env.DATA_DIR = data;
  process.env.MASTER_KEY_FILE = keyPath;
  process.env.APP_ORIGIN ||= 'http://localhost:3000';
  process.env.COOKIE_SECURE ||= new URL(process.env.APP_ORIGIN).protocol === 'https:' ? 'true' : 'false';
  const config = loadConfig();
  const release = await acquireDataLock(data);
  const store = new Store(config);
  try {
    if (store.one('SELECT 1 FROM families LIMIT 1')) throw new Error('Already configured');
    if (process.env.SETUP_WRITE_ENV !== 'false' && !existsSync('.env'))
      writeFileSync('.env', `DATA_DIR=${data}\nMASTER_KEY_FILE=${keyPath}\nAPP_ORIGIN=${config.origin}\nCOOKIE_SECURE=${config.secureCookie}\nCONNECTOR=${config.connector}\nALLOW_REAL_WHATSAPP=${process.env.ALLOW_REAL_WHATSAPP || 'no'}\nHOST=${config.host}\nPORT=${config.port}\n`, { mode: 0o600, flag: 'wx' });
    if (process.argv.includes('--web')) {
      closePrompt();
      const secret = token();
      const { app } = await createApp(config, { store, boot: false,
        bootstrap: { token: secret, expiresAt: Date.now() + 15 * 60_000 } });
      await app.listen({ host: config.host, port: config.port });
      console.log(`Open this private setup link locally (expires in 15 minutes): ${config.origin}/#setup=${secret}`);
      console.log('After creating the parent, stop setup with Ctrl+C and start the normal service. No WhatsApp connection is started here.');
      await new Promise<void>(resolve => {
        const timer = setTimeout(resolve, 15 * 60_000);
        for (const sig of ['SIGINT', 'SIGTERM'] as const) process.once(sig, () => { clearTimeout(timer); resolve(); });
      });
      await app.close();
    } else {
    const name = await ask('Family label (local only): ');
    const parent = normalizeAccountName(await ask('Parent name (also used to sign in): ')),
      child = normalizeAccountName(await ask('Child name (also used to sign in): '));
    if (accountNameKey(parent) === accountNameKey(child)) throw new Error('Account names must differ');
    const pp = await ask('Parent password, 14+ characters: ', true),
      cp = await ask('Child passphrase, 14+ characters: ', true);
    if (pp !== await ask('Confirm parent password: ', true) || cp !== await ask('Confirm child passphrase: ', true))
      throw new Error('Passwords do not match');
    if (pp === cp) throw new Error('Use separate passwords');
    await store.createFamily(
      name || 'Family',
      { name: parent, password: pp },
      { name: child, password: cp },
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
    }
  } finally {
    closePrompt();
    store.close();
    await release();
  }
} catch {
  closePrompt();
  console.error(
    'Setup did not complete. Check names, password length, permissions, and that the service is stopped.',
  );
  process.exitCode = 1;
}
