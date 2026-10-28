/** Local, disposable mock-only visual QA. Never used by application startup. */
import { mkdtempSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { randomBytes } from 'node:crypto';
import { createApp } from '../src/server.js';
import type { Config } from '../src/config.js';
const dataDir = mkdtempSync(join(tmpdir(), 'yahad-usage-preview-'));
const config: Config = { dataDir, masterKey: randomBytes(32), connector: 'mock', realAllowed: false,
  host: '127.0.0.1', port: 4173, origin: 'http://127.0.0.1:4173', secureCookie: false,
  maxMedia: 1048576, maxStorage: 64 * 1048576, childIdleMs: 900000, sessionMs: 3600000, diagnosticsEnabled: false };
const { app, store, service } = await createApp(config, { boot: false });
const family = await store.createFamily('משפחת הדגמה', { name: 'הורה הדגמה', password: randomBytes(24).toString('hex') },
  { name: 'ילד הדגמה', password: randomBytes(24).toString('hex') });
await service.connect(family.familyId);
for (const g of store.groups(family.familyId)) store.grant(family.familyId, family.childId, g.id, true);
app.get('/__qa/unlock.js', async (_req, reply) => reply.type('application/javascript')
  .send("localStorage.removeItem('yachad-locked');location.replace('/');"));
app.get<{ Params: { role: string } }>('/__qa/:role', async (req, reply) => {
  const id = req.params.role === 'parent' ? family.parentId : family.childId;
  const session = store.createSession(store.user(store.one('SELECT * FROM users WHERE id=?', id)));
  reply.setCookie('sid', session.sid, { httpOnly: true, sameSite: 'strict', path: '/' });
  return reply.type('text/html').send('<!doctype html><meta charset="utf-8"><title>Synthetic usage QA</title><script src="/__qa/unlock.js"></script>');
});
// Synthetic-only controls permit checking an open child's suspension without
// switching the child's account or handling any real session credentials.
app.post<{ Params: { action: string } }>('/__qa/control/:action', async req => {
  if (req.params.action === 'stop') store.usage.setPolicy(family.familyId, family.childId, 0);
  else {
    const status = store.usage.status(family.familyId, family.childId);
    store.usage.bonus(family.familyId, family.childId, randomBytes(24).toString('hex'), status.day, 4);
  }
  service.notify(family.familyId, undefined, 'usage'); return { ok: true };
});
await app.listen({ host: config.host, port: config.port });
console.log('SYNTHETIC_USAGE_QA http://127.0.0.1:4173/__qa/parent or /__qa/child');
for (const signal of ['SIGINT', 'SIGTERM'] as const) process.once(signal, () => void app.close().then(() => {
  rmSync(dataDir, { recursive: true, force: true }); process.exit(0);
}));
