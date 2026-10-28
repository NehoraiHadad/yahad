/** Safe synthetic data only. Local environment variables must supply BOTH passwords; never hardcoded. */
import { loadEnvFile } from 'node:process';
import { existsSync, mkdirSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { randomBytes } from 'node:crypto';
import { loadConfig } from '../src/config.js';
import { Store } from '../src/store.js';
import { ChatService } from '../src/service.js';
import { acquireDataLock } from '../src/lock.js';
try {
  loadEnvFile('.env');
} catch {}
if (process.env.CONNECTOR && process.env.CONNECTOR !== 'mock') throw new Error('Demo is mock-only');
if (!process.env.DEMO_PARENT_PASSWORD || !process.env.DEMO_CHILD_PASSWORD)
  throw new Error(
    'Set unique local DEMO_PARENT_PASSWORD and DEMO_CHILD_PASSWORD; 14+ characters each',
  );
if (process.env.DEMO_PARENT_PASSWORD === process.env.DEMO_CHILD_PASSWORD)
  throw new Error('Use separate demo passwords');
mkdirSync('.local', { recursive: true, mode: 0o700 });
if (!existsSync('.local/master.key'))
  writeFileSync('.local/master.key', randomBytes(32).toString('base64'), { mode: 0o600 });
process.env.MASTER_KEY_FILE ||= resolve('.local/master.key');
process.env.DATA_DIR ||= resolve('./data');
process.env.APP_ORIGIN ||= 'http://localhost:3000';
process.env.COOKIE_SECURE ||= 'false';
process.env.CONNECTOR = 'mock';
const config = loadConfig();
const release = await acquireDataLock(config.dataDir);
const store = new Store(config);
try {
  if (store.one('SELECT 1 FROM families')) throw new Error('Demo needs an empty data directory');
  const family = await store.createFamily(
    'משפחת דוגמה',
    { name: 'הורה לדוגמה', password: process.env.DEMO_PARENT_PASSWORD },
    { name: 'ילד לדוגמה', password: process.env.DEMO_CHILD_PASSWORD },
  );
  const service = new ChatService(store);
  await service.connect(family.familyId);
  for (const g of ['120363001@g.us', '120363002@g.us', '120363003@g.us'])
    await service.grant(family.familyId, family.childId, g, true);
  await service.close();
  if (!existsSync('.env'))
    writeFileSync(
      '.env',
      `DATA_DIR=${config.dataDir}\nMASTER_KEY_FILE=${process.env.MASTER_KEY_FILE}\nAPP_ORIGIN=${config.origin}\nCOOKIE_SECURE=${config.secureCookie}\nCONNECTOR=mock\nALLOW_REAL_WHATSAPP=no\nHOST=127.0.0.1\n`,
      { mode: 0o600 },
    );
  console.log(
    'Synthetic demo ready. Account names for display and sign-in: הורה לדוגמה / ילד לדוגמה. Use the locally supplied passwords.',
  );
} finally {
  store.close();
  await release();
}
