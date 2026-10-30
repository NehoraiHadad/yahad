import test from 'node:test';
import assert from 'node:assert/strict';
import { randomBytes } from 'node:crypto';
import { resolve } from 'node:path';
import { loadConfig } from '../src/config.js';
import { readBootstrap } from '../src/bootstrap.js';
import type { PlatformFiles } from '../src/platform-files.js';

test('native file-boundary injection and LAN origin policy survive the combined foundation without OS effects', () => {
  const original = process.env;
  const key = randomBytes(32);
  const token = randomBytes(32).toString('base64url');
  const now = Date.now();
  const reads: string[] = [];
  const files: PlatformFiles = {
    readMasterKey(path) { reads.push(path); return Buffer.from(key.toString('base64')); },
    readSetupCapability(path) {
      reads.push(path); return Buffer.from(JSON.stringify({ token, expiresAt: now + 60000 }));
    },
    async acquireDataLock() { throw new Error('No data lock or native helper may run in this injected test'); },
    setupCapabilityPath() { return resolve('synthetic-native-state/Secrets/setup-capability.json'); },
  };
  try {
    process.env = {
      MASTER_KEY_FILE: resolve('synthetic-native-state/master-key'), DATA_DIR: resolve('synthetic-native-state/data'),
      APP_ORIGIN: 'https://192.168.4.25', APP_ADDITIONAL_ORIGINS: 'http://127.0.0.1:47123',
      COOKIE_SECURE: 'true', HOST: '127.0.0.1', PORT: '47123', CONNECTOR: 'mock', ALLOW_REAL_WHATSAPP: 'no',
    };
    const config = loadConfig(files);
    assert.deepEqual(config.masterKey, key);
    assert.equal(config.origin, 'https://192.168.4.25');
    assert.deepEqual(config.additionalOrigins, ['http://127.0.0.1:47123']);
    assert.equal(config.secureCookie, true);
    assert.equal(config.host, '127.0.0.1');
    assert.deepEqual(readBootstrap(files.setupCapabilityPath(), now, files), { token, expiresAt: now + 60000 });
    assert.deepEqual(reads, [process.env.MASTER_KEY_FILE, files.setupCapabilityPath()]);
    process.env.APP_ADDITIONAL_ORIGINS = 'http://192.168.4.25';
    assert.throws(() => loadConfig(files), /exact HTTPS or loopback origins/);
    process.env.APP_ADDITIONAL_ORIGINS = 'https://synthetic-machine.test-tailnet.ts.net';
    process.env.COOKIE_SECURE = 'false';
    assert.throws(() => loadConfig(files), /Insecure cookies allowed only on localhost|require secure cookies/);
  } finally { process.env = original; }
});
