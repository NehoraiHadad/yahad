// Runs without access to household data, secrets or a real WhatsApp account.
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { randomBytes } from 'node:crypto';
import { Store } from '../src/store.js';
import { createApp } from '../src/server.js';
if (process.versions.node.split('.')[0] !== '24') throw new Error('Node 24 required');
const dir = mkdtempSync(join(tmpdir(), 'yachad-release-'));
try {
  const config = { dataDir: dir, masterKey: randomBytes(32), connector: 'mock' as const,
    realAllowed:false, host:'127.0.0.1',port:3000,origin:'http://localhost:3000',secureCookie:false,
    maxMedia:1048576,maxStorage:64*1048576,childIdleMs:60000,sessionMs:3600000,diagnosticsEnabled:false };
  const { app, store } = await createApp(config, { boot: false });
  try {
    const response = await app.inject({url:'/health/ready'});
    if(response.statusCode !== 200 || !response.json().ok) throw new Error('Readiness failed');
    if(store.one('PRAGMA synchronous')?.synchronous !== 2) throw new Error('Durability changed');
    await import('@whiskeysockets/baileys');
  } finally { await app.close(); }
  process.stdout.write('SMOKE_OK\n');
} finally { rmSync(dir,{recursive:true,force:true}); }
