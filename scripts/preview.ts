/** DEVELOPMENT QA HARNESS ONLY. Fresh disposable synthetic family; automatic test sessions.
 * Never used by npm start or systemd. Refuses real WhatsApp. No user data imported.
 */
import { readFileSync, mkdirSync, rmSync, existsSync } from 'node:fs';
import { resolve } from 'node:path';
import { randomBytes } from 'node:crypto';
import { createApp } from '../src/server.js';
import type { Config } from '../src/config.js';
if (process.env.NODE_ENV === 'production' || process.env.CONNECTOR === 'baileys')
  throw new Error('QA harness is mock-only and never for production');
const firstRun = process.env.QA_FIRST_RUN === '1';
const dataDir = resolve(firstRun ? '.local/qa-first-run' : '.local/qa-data');
rmSync(dataDir, { force: true, recursive: true });
const config: Config = {
  dataDir,
  masterKey: randomBytes(32),
  connector: 'mock',
  realAllowed: false,
  host: '0.0.0.0',
  port: firstRun ? 4174 : 4173,
  origin: firstRun ? 'http://127.0.0.1:4174' : 'http://127.0.0.1:4173',
  secureCookie: false,
  maxMedia: 16 * 1024 ** 2,
  maxStorage: 256 * 1024 ** 2,
  childIdleMs: 15 * 60000,
  sessionMs: 12 * 3600000,
};
const setup = randomBytes(32).toString('base64url');
const { app, store, service } = await createApp(config, firstRun ? { bootstrap: { token: setup, expiresAt: Date.now()+900000 } } : {});
const secret = randomBytes(32).toString('hex');
if (!firstRun) {
const f = await store.createFamily(
  'משפחת דוגמה',
  { name: 'הורה לדוגמה', password: secret },
  { name: 'ילד לדוגמה', password: randomBytes(32).toString('hex') },
);
await service.connect(f.familyId);
for (const g of ['120363001@g.us', '120363002@g.us', '120363003@g.us'])
  await service.grant(f.familyId, f.childId, g, true);
app.addHook('onRequest', async () => {
  if (!firstRun && existsSync('.local/preview-origin')) {
    config.origin = readFileSync('.local/preview-origin', 'utf8').trim();
    config.secureCookie = config.origin.startsWith('https:');
  }
});
app.addHook('onSend', async (req, reply, payload) => {
  reply.header(
    'Content-Security-Policy',
    "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' blob: data:; media-src 'self' blob:; connect-src 'self'; frame-ancestors 'self'; base-uri 'none'; form-action 'self'; object-src 'none'",
  );
  return payload;
});
app.get('/__qa/reset-lock.js', async (_req, reply) => reply.type('application/javascript').send("localStorage.removeItem('yachad-locked');"));
app.get<{ Params: { size: string }; Querystring: { role?: string; reset?: string } }>(
  '/__qa/frame/:size',
  async (req, reply) => {
    const role = req.query.role === 'parent' ? 'parent' : 'child';
    const u = store.user(
      store.one(
        'SELECT * FROM users WHERE family_id=? AND id=?',
        f.familyId,
        role === 'parent' ? f.parentId : f.childId,
      ),
    );
    const session = store.createSession(u);
    reply.setCookie('sid', session.sid, {
      httpOnly: true,
      sameSite: 'strict',
      secure: config.secureCookie,
      path: '/',
    });
    const mobile = req.params.size === 'mobile',
      width = mobile ? 390 : req.params.size === 'desktop' ? 1280 : 1024,
      height = mobile ? 844 : 768;
    return reply
      .type('text/html')
      .send(
        `<!doctype html><html><head><meta charset="UTF-8">${req.query.reset === '1' ? '<script src="/__qa/reset-lock.js"></script>' : ''}<title>Yachad · ${mobile ? '390 × 844' : '1024 × 768'} QA</title><style>body{margin:0;background:#e7ece7;font-family:Arial;display:flex;flex-direction:column;align-items:center;padding:16px;gap:12px}p{margin:0;color:#6b7f71;font-size:12px}iframe{border:0;box-shadow:0 8px 35px #2342;border-radius:16px;background:white;flex:none}</style></head><body><p>LOCAL QA · synthetic data · ${width} × ${height} CSS pixels · ${role}</p><iframe title="הממשק הנבדק" src="/" width="${width}" height="${height}" allow="microphone"></iframe></body></html>`,
      );
  },
);
} else app.get('/__qa/setup', async (_req, reply) => reply.redirect('/#setup='+setup));
await app.listen({ host: config.host, port: config.port });
console.log(
  `Disposable mock QA harness ready on port ${config.port}. Entry: ${firstRun ? '/__qa/setup' : '/__qa/frame/tablet or /__qa/frame/mobile'}`,
);
for (const signal of ['SIGTERM', 'SIGINT'] as const)
  process.once(signal, () => void app.close().then(() => process.exit(0)));
