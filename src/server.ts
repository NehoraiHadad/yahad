import type { WindowsUpdateTransport } from './windows-update-client.js';
import {readOnlyKind} from './rich-content.js';
import Fastify from 'fastify';
import { CredentialThrottle } from './credential-throttle.js';
import { performance } from 'node:perf_hooks';
import cookie from '@fastify/cookie';
import multipart from '@fastify/multipart';
import serveStatic from '@fastify/static';
import { resolve } from 'node:path';
import { existsSync, readFileSync } from 'node:fs';
import { z } from 'zod';
import { DATABASE_SCHEMA } from './db.js';
import { normalizeAccountName, accountNameKey } from './account-name.js';
import QRCode from 'qrcode';
import { hash, verifyPassword, passwordHash } from './crypto.js';
import { timingSafeEqual, randomUUID } from 'node:crypto';
import type { Config } from './config.js';
import { parseAdditionalOrigins } from './allowed-origins.js';
import { Store, type Session } from './store.js';
import { UsageError } from './usage.js';
import { ChildAccessError } from './child-access.js';
import { unreadCount, markLocallyViewed } from './chat-read-state.js';
import { ChatService, AppError, deny } from './service.js';
import { voiceToOpus } from './media.js';
import { validReaction } from './message-actions.js';
import { updateStatus, requestUpdate, updateTargetSchema } from './updates.js';
import type { ConnectorHooks, Connector, StoredMessage, SendKind } from './connectors/types.js';
const textSchema = z.string().trim().max(12000);
const accountNameSchema = z.string().max(240).transform((value, ctx) => {
  try { return normalizeAccountName(value); }
  catch { ctx.addIssue({ code: 'custom', message: 'Invalid account name' }); return z.NEVER; }
});
const sendSchema = z.object({
  clientId: z.uuid(),
  text: textSchema.default(''),
  replyId: z.string().max(180).optional(),
});
declare module 'fastify' {
  interface FastifyRequest {
    session: Session;
  }
}
export async function createApp(
  config: Config,
  options: { windowsUpdates?: WindowsUpdateTransport; store?: Store; factory?: (h: ConnectorHooks) => Connector; boot?: boolean;
    bootstrap?: { token: string; expiresAt: number } } = {},
) {
  const acceptedOrigins = new Set([config.origin,
    ...parseAdditionalOrigins((config.additionalOrigins ?? []).join(','), config.secureCookie)]);
  const acceptedOrigin = (origin: string | undefined) => origin !== undefined && acceptedOrigins.has(origin);
  const app = Fastify({
    logger: false,
    bodyLimit: 100_000,
    ...(config.tls ? { https: config.tls } : {}),
  });
  const store = options.store || new Store(config);
  const service = new ChatService(store, options.factory);
  store.diagnostics.start(store.all('SELECT id FROM families').map(x => x.id));
  const timings = new WeakMap<object, number>();
  app.addHook('onRequest', async req => { timings.set(req, performance.now()); });
  app.addHook('onResponse', async (req, reply) => {
    if (!req.session || req.url.startsWith('/api/parent/diagnostics') || req.url.startsWith('/api/parent/updates') || req.url.startsWith('/api/events')) return;
    store.diagnostics.record(req.session.user.familyId, 'api', performance.now() - (timings.get(req) ?? performance.now()),
      reply.statusCode >= 500 ? { code: 'API_FAILURE' } : undefined);
  });
  await app.register(cookie);
  await app.register(multipart, {
    limits: { fileSize: config.maxMedia, files: 1, fields: 5, fieldSize: 15000, parts: 7 },
  });
  const credentialThrottle = new CredentialThrottle(store);
  let activeLogins = 0;
  const limitedVerify = async (password: string, stored: string) => {
    if (activeLogins >= 3) throw new AppError(429, 'SLOW_DOWN');
    activeLogins++;
    try { return await verifyPassword(password, stored); }
    finally { activeLogins--; }
  };
  const sends = new Map<string, number[]>();
  const pending = new Set<string>();
  const activeUploads = new Set<string>();
  app.decorateRequest('session', null as unknown as Session);
  app.setErrorHandler((error, req, reply) => {
    const e = error as any;
    const status =
      e instanceof AppError || e instanceof UsageError || e instanceof ChildAccessError
        ? e.status
        : e instanceof z.ZodError
          ? 400
          : e.statusCode === 413 || e.code === 'FST_REQ_FILE_TOO_LARGE'
            ? 413
            : 500;
    reply.code(status).send({
      error:
        e instanceof AppError || e instanceof UsageError || e instanceof ChildAccessError
          ? e.code
          : status === 400
            ? 'INVALID_INPUT'
            : status === 413
              ? 'FILE_TOO_LARGE'
              : 'REQUEST_FAILED',
    });
  });
  app.addHook('onRequest', async (req, reply) => {
    reply
      .header('Cache-Control', 'no-store, max-age=0')
      .header('X-Content-Type-Options', 'nosniff')
      .header('Referrer-Policy', 'no-referrer')
      .header('Cross-Origin-Resource-Policy', 'same-origin')
      .header('Permissions-Policy', 'microphone=(self), camera=(), geolocation=()')
      .header(
        'Content-Security-Policy',
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' blob: data:; media-src 'self' blob:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'; object-src 'none'",
      );
    if (!req.url.startsWith('/api/')) return;
    const origin = req.headers.origin;
    if (origin && !acceptedOrigin(origin)) throw new AppError(403, 'ORIGIN_DENIED');
    if (req.url === '/api/login' || req.url === '/api/setup' || req.url === '/api/setup/status') return;
    const s = store.session(req.cookies.sid || '');
    if (!s) throw new AppError(401, 'SIGN_IN_REQUIRED');
    req.session = s;
    if (!['GET', 'HEAD'].includes(req.method)) {
      if (!acceptedOrigin(origin) || req.headers['x-csrf-token'] !== s.csrf)
        throw new AppError(403, 'CSRF_DENIED');
      if (req.url !== '/api/logout') store.session(s.sid, true);
    }
  });
  const parent = (s: Session) => {
    if (s.user.role !== 'parent') throw new AppError(403, 'PARENT_ONLY');
  };
  app.addHook('preHandler', async req => {
    if (req.url.split('?')[0].startsWith('/api/groups')) store.usage.require(req.session);
  });
  app.addHook('onSend', async (req, reply, payload) => {
    if (reply.statusCode < 400 && req.url.split('?')[0].startsWith('/api/groups') && req.session) {
      if (!store.session(req.session.sid)) throw new AppError(401, 'SIGN_IN_REQUIRED');
      store.usage.require(req.session);
    }
    return payload;
  });
  const group = (s: Session, g: string) => { store.usage.require(s); return service.requireGroup(s.user, g); };
  const current = (s: Session, g: string) => {
    if (!store.session(s.sid)) throw new AppError(401, 'SIGN_IN_REQUIRED');
    group(s, g);
  };
  const capable = (s: Session, key: string) => {
    if (!(service.connector(s.user.familyId).capabilities as any)[key])
      throw new AppError(409, 'UNSUPPORTED');
  };
  const rateSend = (s: Session) => {
    const k = `${s.user.familyId}:${s.user.id}`,
      times = (sends.get(k) || []).filter((t) => t > Date.now() - 60_000);
    if (times.length >= 30) throw new AppError(429, 'SLOW_DOWN');
    times.push(Date.now());
    sends.set(k, times);
  };
  const getMessage = (s: Session, g: string, id: string) => {
    group(s, g);
    const m = store.getMessage(s.user.familyId, g, id);
    if (!m || m.deleted) throw deny();
    return m;
  };
  const own = (s: Session, g: string, id: string) => {
    const m = getMessage(s, g, id);
    if (
      !m.fromMe ||
      (s.user.role === 'child' && m.authorUserId !== s.user.id) ||
      m.status === 'pending' ||
      m.status === 'failed'
    )
      throw new AppError(403, 'OWN_MESSAGE_ONLY');
    return m;
  };
  const requireOriginal = (s: Session,m: StoredMessage) => {
    const c=service.connector(s.user.familyId);
    if (m.restricted || m.kind==='unsupported' || readOnlyKind(m.kind) || c.originalAvailable && !c.originalAvailable(m))
      throw new AppError(409,'ORIGINAL_UNAVAILABLE');
  };
  let settingUp = false;
  app.get('/api/setup/status', async () => ({
    state: store.one('SELECT 1 FROM families LIMIT 1') ? 'complete' :
      options.bootstrap && Date.now() <= options.bootstrap.expiresAt ? 'ready' : 'closed',
  }));
  app.post('/api/setup', async (req, reply) => {
    const capability = req.headers['x-setup-token'];
    const bootstrap = options.bootstrap;
    if (!acceptedOrigin(req.headers.origin)) throw new AppError(403, 'ORIGIN_DENIED');
    if (!bootstrap || Date.now() > bootstrap.expiresAt || typeof capability !== 'string' ||
        !timingSafeEqual(Buffer.from(hash(capability)), Buffer.from(hash(bootstrap.token))) ||
        store.one('SELECT 1 FROM families LIMIT 1')) throw new AppError(403, 'SETUP_CLOSED');
    if (settingUp) throw new AppError(409, 'SETUP_BUSY');
    const data = z.object({ familyName: z.string().trim().min(1).max(80),
      name: accountNameSchema,
      password: z.string().min(4).max(256), confirmation: z.string().max(256) }).strict().parse(req.body);
    if (data.password !== data.confirmation) throw new AppError(400, 'PASSWORD_MISMATCH');
    settingUp = true;
    try {
      const family = await store.createFamily(data.familyName, data, undefined, true);
      // Consume the capability only after durable creation; restarting never reopens setup.
      options.bootstrap = undefined;
      // The capability authorizes this first parent; continue directly into the
      // authenticated UI without asking them to type the new passphrase again.
      const session = store.createSession(store.user(store.one('SELECT * FROM users WHERE id=?', family.parentId)));
      reply.setCookie('sid', session.sid, { httpOnly: true, sameSite: 'strict', secure: config.secureCookie,
        path: '/', maxAge: Math.floor(config.sessionMs / 1000) });
      return { ok: true };
    } finally { settingUp = false; }
  });
  app.post('/api/login', async (req, reply) => {
    if (!acceptedOrigin(req.headers.origin)) throw new AppError(403, 'ORIGIN_DENIED');
    const input = z
      .object({ name: accountNameSchema, password: z.string().max(256) }).strict()
      .parse(req.body);
    const row = store.one('SELECT * FROM users WHERE name_key=?', accountNameKey(input.name));
    const accessRevision = row?.role === 'child' ? store.childAccess(row.family_id, row.id).revision : 0;
    if (activeLogins >= 3) throw new AppError(429, 'SLOW_DOWN');
    const credentialKey = row ? `account:${row.family_id}:${row.id}` : `unknown:${accountNameKey(input.name)}`;
    if (!credentialThrottle.reserve(credentialKey, req.ip)) {
      reply.header('Retry-After', '900');
      throw new AppError(429, 'LOGIN_WAIT');
    }
    const valid = await limitedVerify(input.password,
      row?.password || '00000000000000000000000000000000:' + '00'.repeat(64));
    if (!row || !valid) throw new AppError(401, 'INVALID_CREDENTIALS');
    // Password verification yields. Do not finish a stale-name login after a
    // concurrent rename (or accept a password replaced while verification ran).
    const authenticated = store.one('SELECT * FROM users WHERE family_id=? AND id=? AND name_key=? AND password=?',
      row.family_id, row.id, accountNameKey(input.name), row.password);
    if (!authenticated) throw new AppError(401, 'INVALID_CREDENTIALS');
    if (authenticated.role === 'child') {
      const access = store.childAccess(authenticated.family_id, authenticated.id);
      if (access.paused) throw new AppError(403, 'CHILD_ACCESS_PAUSED');
      if (access.revision !== accessRevision) throw new AppError(401, 'SIGN_IN_REQUIRED');
    }
    if (req.cookies.sid) {
      store.usage.pauseSession(req.cookies.sid);
      store.run('DELETE FROM sessions WHERE id=?', hash(req.cookies.sid));
    }
    const session = store.createSession(store.user(authenticated));
    credentialThrottle.clear(credentialKey);
    reply.setCookie('sid', session.sid, {
      httpOnly: true,
      secure: config.secureCookie,
      sameSite: 'strict',
      path: '/',
      maxAge: config.sessionMs / 1000,
    });
    return { ok: true };
  });
  app.get('/api/me', async (req) => {
    const s = req.session;
    return {
      user: s.user,
      csrf: s.csrf,
      connector: config.connector,
      capabilities: service.connectors.get(s.user.familyId)?.capabilities || {},
      connection: service.states.get(s.user.familyId)?.state || 'idle',
      idleMs: s.user.role === 'child' ? config.childIdleMs : null,
      maxMedia: config.maxMedia,
      senderLabel: store.identityLabel(s.user.familyId, s.user.id),
    };
  });
  app.post('/api/activity', async () => ({ ok: true }));
  app.get('/api/usage', async req => {
    if (req.session.user.role !== 'child') throw new AppError(403, 'CHILD_ONLY');
    return store.usage.status(req.session.user.familyId, req.session.user.id, req.session.sid);
  });
  app.post('/api/usage/open', async req => {
    if (req.session.user.role !== 'child') throw new AppError(403, 'CHILD_ONLY');
    z.object({}).strict().parse(req.body);
    return store.usage.open(req.session);
  });
  app.post('/api/usage/heartbeat', async req => {
    if (req.session.user.role !== 'child') throw new AppError(403, 'CHILD_ONLY');
    const data = z.object({ tab: z.uuid(), seq: z.number().int().min(1).max(Number.MAX_SAFE_INTEGER), active: z.boolean() }).strict().parse(req.body);
    return store.usage.heartbeat(req.session, data.tab, data.seq, data.active);
  });
  app.get<{ Params: { c: string } }>('/api/parent/usage/:c', async req => {
    parent(req.session);
    return store.usage.status(req.session.user.familyId, req.params.c);
  });
  app.put<{ Params: { c: string } }>('/api/parent/usage/:c', async req => {
    parent(req.session);
    const { minutes } = z.object({ minutes: z.number().int().min(0).max(1000000).nullable() }).strict().parse(req.body);
    const status = store.usage.setPolicy(req.session.user.familyId, req.params.c, minutes);
    service.notify(req.session.user.familyId, undefined, 'usage');
    return status;
  });
  app.post<{ Params: { c: string } }>('/api/parent/usage/:c/bonus', async req => {
    parent(req.session);
    const data = z.object({ requestId: z.uuid(), day: z.string().regex(/^\d{4}-\d{2}-\d{2}$/), dayKey: z.string().max(100).optional(), minutes: z.number().int().min(1).max(1000000) }).strict().parse(req.body);
    const status = store.usage.bonus(req.session.user.familyId, req.params.c, data.requestId, data.day, data.minutes, data.dayKey);
    service.notify(req.session.user.familyId, undefined, 'usage');
    return status;
  });
  app.put('/api/parent/usage-timezone', async req => {
    parent(req.session);
    const { timezone } = z.object({ timezone: z.string().min(1).max(100) }).strict().parse(req.body);
    store.usage.setTimezone(req.session.user.familyId, timezone);
    service.notify(req.session.user.familyId, undefined, 'usage');
    return { ok: true };
  });
  app.post('/api/logout', async (req, reply) => {
    store.usage.pauseSession(req.session.sid);
    store.run('DELETE FROM sessions WHERE id=?', hash(req.session.sid));
    service.notify(req.session.user.familyId, undefined, 'session');
    reply.clearCookie('sid', { path: '/' });
    return { ok: true };
  });
  app.post('/api/password', async (req) => {
    const data = z
      .object({ current: z.string().max(256), next: z.string().min(4).max(256), confirmation: z.string().max(256) })
      .parse(req.body);
    if (data.next !== data.confirmation) throw new AppError(400, 'PASSWORD_MISMATCH');
    const s = req.session,
      row = store.one(
        'SELECT password FROM users WHERE family_id=? AND id=?',
        s.user.familyId,
        s.user.id,
      );
    if (!credentialThrottle.reserve(`account:${s.user.familyId}:${s.user.id}`, req.ip))
      throw new AppError(429, 'LOGIN_WAIT');
    if (!(await limitedVerify(data.current, row.password)))
      throw new AppError(403, 'WRONG_PASSWORD');
    const next = await passwordHash(data.next);
    store.usage.revokeUser(s.user.familyId, s.user.id);
    credentialThrottle.clear(`account:${s.user.familyId}:${s.user.id}`);
    store.run(
      'UPDATE users SET password=? WHERE family_id=? AND id=?',
      next,
      s.user.familyId,
      s.user.id,
    );
    store.run('DELETE FROM sessions WHERE family_id=? AND user_id=?', s.user.familyId, s.user.id);
    service.notify(s.user.familyId, undefined, 'session');
    return { ok: true };
  });
  app.get<{ Params: { g: string } }>('/api/groups/:g/photo', async (req, reply) => {
    const s = req.session;
    const photo = await service.groupPhoto(s.user, req.params.g);
    if (!store.session(s.sid)) throw new AppError(401, 'SIGN_IN_REQUIRED');
    service.requirePhotoGroup(s.user, req.params.g);
    store.usage.require(s);
    if (!photo) throw deny();
    return reply.type(photo.mime).send(photo.bytes);
  });
  app.get('/api/groups', async (req) => {
    const s = req.session,
      f = s.user.familyId;
    return store
      .groups(f)
      // A single authorization policy governs lists and every content route.
      .filter((g) => store.allowed(s.user, g.id))
      .map((g) => ({
        ...g,
        canRead: store.allowed(s.user, g.id),
        unreadCount: unreadCount(store, s.user, g.id),
        last: (() => {
          if (!store.allowed(s.user, g.id)) return undefined;
          const m = store.messages(f, g.id, undefined, 1)[0];
          return m ? service.public(f, m) : undefined;
        })(),
      }));
  });
  app.get<{ Params: { g: string }; Querystring: { cursor?: string; q?: string } }>(
    '/api/groups/:g/messages',
    async (req) => {
      const s = req.session,
        g = req.params.g;
      group(s, g);
      let cursor;
      if (req.query.cursor) {
        try {
          cursor = z
            .object({ time: z.number(), id: z.string().max(180) })
            .parse(JSON.parse(Buffer.from(req.query.cursor, 'base64url').toString()));
        } catch {
          throw new AppError(400, 'BAD_CURSOR');
        }
      }
      const items = store.messages(s.user.familyId, g, cursor, 41);
      const more = items.length > 40;
      if (more) items.shift();
      return {
        messages: items.map((m) => service.public(s.user.familyId, m)),
        cursor: more
          ? Buffer.from(JSON.stringify({ time: items[0].time, id: items[0].id })).toString(
              'base64url',
            )
          : null,
        history: 'partial',
      };
    },
  );
  app.get<{ Params: { g: string }; Querystring: { q: string } }>(
    '/api/groups/:g/search',
    async (req) => {
      const s = req.session,
        g = req.params.g;
      group(s, g);
      const q = z.string().min(1).max(120).parse(req.query.q).toLocaleLowerCase();
      const result: StoredMessage[] = [];
      let cursor;
      let scanned = 0;
      while (scanned < 2000 && result.length < 40) {
        const page = store.messages(s.user.familyId, g, cursor, 100);
        if (!page.length) break;
        for (const m of page)
          if (!m.deleted && m.text.toLocaleLowerCase().includes(q)) result.push(m);
        cursor = { time: page[0].time, id: page[0].id };
        scanned += page.length;
        if (page.length < 100) break;
      }
      return {
        messages: result.slice(-40).map((m) => service.public(s.user.familyId, m)),
        limited: scanned >= 2000,
      };
    },
  );
  async function send(
    s: Session,
    g: string,
    data: z.infer<typeof sendSchema>,
    attachment?: { bytes: Buffer; mime: string; filename: string; kind: SendKind },
  ) {
    current(s, g);
    const f = s.user.familyId;
    const c = service.connector(f);
    const retentionToken = store.groupKey(f, g).toString('hex');
    const sameRetention = () => store.retained(f, g) && store.groupKey(f, g).toString('hex') === retentionToken;
    const currentSend = () => {
      current(s, g);
      if (!sameRetention()) throw deny();
    };
    const currentQuote = (quoted?: StoredMessage) => {
      currentSend();
      if (!quoted)return;
      const latest=store.getMessage(f,g,quoted.id);
      if (!latest || latest.deleted || latest.restricted || latest.raw!==quoted.raw || latest.text!==quoted.text)
        throw new AppError(409,'ORIGINAL_UNAVAILABLE');
      requireOriginal(s,latest);
    };
    if (service.states.get(f)?.state !== 'connected') throw new AppError(409, 'NOT_CONNECTED');
    if (attachment) capable(s, attachment.kind);
    if (!attachment && !data.text) throw new AppError(400, 'EMPTY_MESSAGE');
    const id = 'FC' + data.clientId.replaceAll('-', '').toUpperCase(),
      requestKey = `${f}:${g}:${id}`;
    const existing = store.getMessage(f, g, id);
    if (existing && existing.status !== 'failed') return { message: service.public(f, existing) };
    if (pending.has(requestKey)) throw new AppError(409, 'SEND_PENDING');
    rateSend(s);
    const reply = data.replyId ? getMessage(s, g, data.replyId) : undefined;
    if (reply) { capable(s, 'reply'); requireOriginal(s,reply); }
    const label = s.user.role === 'child' ? store.identityLabel(f, s.user.id) : '';
    const needsMarker = !!label && (attachment?.kind === 'audio' || attachment?.kind === 'sticker');
    const wireText = label && !needsMarker ? `${label}: ${data.text}` : data.text;
    const pendingMessage: StoredMessage = {
      id,
      groupId: g,
      sender: 'אני',
      fromMe: true,
      time: Date.now(),
      kind: attachment?.kind || 'text',
      text: wireText,
      status: 'pending',
      reactions: {},
      clientId: data.clientId,
      replyId: reply?.id,
      authorUserId: s.user.id,
      authorLabel: label || undefined,
    };
    store.putMessage(f, pendingMessage);
    pending.add(requestKey);
    service.notify(f, g);
    try {
      currentSend();
      let wireReply = reply;
      if (needsMarker) {
        const markerId = id + 'L';
        let marker = store.getMessage(f, g, markerId);
        if (!marker || marker.status === 'failed' || marker.status === 'pending') {
          marker = {
            id: markerId,
            groupId: g,
            sender: 'אני',
            fromMe: true,
            time: Date.now() - 1,
            kind: 'text',
            text: `${label} · ${attachment?.kind === 'audio' ? 'הודעה קולית' : 'מדבקה'}`,
            status: 'pending',
            reactions: {},
            authorUserId: s.user.id,
            authorLabel: label,
          };
          store.putMessage(f, marker);
          let sentMarker;
          try {
            sentMarker = await c.send({
              beforeSend: () => currentQuote(reply),
              id: markerId,
              groupId: g,
              text: marker.text,
              kind: 'text',
              reply,
            });
          } catch (error) {
            if (sameRetention()) service.update(f, g, markerId, { status: 'failed' });
            if (error instanceof UsageError || error instanceof AppError && error.code==='ORIGINAL_UNAVAILABLE') throw error;
            throw new Error('Marker send failed');
          }
          currentSend();
          await service.ingest(f, { ...sentMarker, id: markerId });
          marker = store.getMessage(f, g, markerId);
        }
        if (!wireReply) wireReply = marker;
      }
      currentSend();
      const sent = await c.send({
        beforeSend: () => currentQuote(wireReply),
        id,
        groupId: g,
        text: wireText,
        kind: attachment?.kind || 'text',
        bytes: attachment?.bytes,
        mime: attachment?.mime,
        filename: attachment?.filename,
        reply: wireReply,
      });
      currentSend();
      await service.ingest(f, {
        ...sent,
        id,
        bytes: attachment?.bytes,
        mime: attachment?.mime || sent.mime,
        filename: attachment?.filename || sent.filename,
        replyId: wireReply?.id,
        status: sent.status,
      });
      currentSend();
      return { message: service.public(f, store.getMessage(f, g, id)!) };
    } catch (error) {
      if (sameRetention())
        service.update(f, g, id, { status: 'failed' });
      if (error instanceof UsageError || error instanceof AppError && error.code==='ORIGINAL_UNAVAILABLE') throw error;
      throw new AppError(502, 'SEND_UNCERTAIN');
    } finally {
      pending.delete(requestKey);
    }
  }
  app.post<{ Params: { g: string } }>('/api/groups/:g/messages', async (req) =>
    send(req.session, req.params.g, sendSchema.parse(req.body)),
  );
  app.post<{ Params: { g: string } }>('/api/groups/:g/media', async (req) => {
    const s = req.session,
      g = req.params.g;
    group(s, g);
    if (activeUploads.size >= 2) throw new AppError(429, 'SLOW_DOWN');
    activeUploads.add(req.id);
    try {
      const fields: Record<string, string> = {};
      let bytes: Buffer | undefined,
        filename = '',
        mime = '';
      for await (const part of req.parts()) {
        if (part.type === 'file') {
          filename = part.filename.replace(/[\r\n\\/]/g, '_').slice(0, 160);
          mime = part.mimetype;
          bytes = await part.toBuffer();
        } else fields[part.fieldname] = String(part.value);
      }
      if (!bytes?.length) throw new AppError(400, 'NO_FILE');
      const data = sendSchema.parse({
        clientId: fields.clientId,
        text: fields.text || '',
        replyId: fields.replyId || undefined,
      });
      const { fileTypeFromBuffer } = await import('file-type');
      const type = await fileTypeFromBuffer(bytes);
      const detected = (type?.mime || 'application/octet-stream').split(';')[0];
      let kind: SendKind = 'document';
      if (fields.voice === 'true') {
        capable(s, 'audio');
        if (
          ![
            'audio/webm',
            'video/webm',
            'audio/ogg',
            'audio/mp4',
            'video/mp4',
            'audio/mpeg',
            'audio/wav',
          ].includes(detected)
        )
          throw new AppError(400, 'INVALID_AUDIO');
        try {
          const recording = bytes;
          bytes = await store.diagnostics.measureAsync(s.user.familyId, 'audio_encode', () => voiceToOpus(recording));
        } catch {
          throw new AppError(422, 'AUDIO_ENCODING_FAILED');
        }
        kind = 'audio';
        mime = 'audio/ogg; codecs=opus';
        filename = 'voice.ogg';
      } else if (fields.sticker === 'true') {
        if (detected !== 'image/webp') throw new AppError(400, 'WEBP_REQUIRED');
        kind = 'sticker';
        mime = detected;
      } else {
        mime = detected;
        if (['image/png', 'image/jpeg', 'image/webp', 'image/gif'].includes(detected))
          kind = 'image';
        if (detected === 'video/mp4') kind = 'video';
      }
      current(s, g);
      return await send(s, g, data, { bytes, mime, filename, kind });
    } finally {
      activeUploads.delete(req.id);
    }
  });
  app.get<{ Params: { g: string; id: string } }>('/api/groups/:g/media/:id', async (req, reply) => {
    const s = req.session,
      g = req.params.g;
    group(s, g);
    const media = store.getMedia(s.user.familyId, g, req.params.id);
    if (!media) throw deny();
    const m = media.message;
    const inline =
      m.kind !== 'document' &&
      [
        'image/png',
        'image/jpeg',
        'image/webp',
        'image/gif',
        'video/mp4',
        'audio/ogg',
        'audio/mpeg',
        'audio/mp4',
        'audio/webm',
        'video/webm',
      ].includes(m.mime || '');
    reply
      .type(inline ? m.mime! : 'application/octet-stream')
      .header(
        'Content-Disposition',
        `${inline ? 'inline' : 'attachment'}; filename="attachment"; filename*=UTF-8''${encodeURIComponent(m.filename || 'attachment').replaceAll("'", '%27')}`,
      );
    reply.header('Accept-Ranges', 'bytes');
    const range = req.headers.range;
    if (range) {
      const match = /^bytes=(\d*)-(\d*)$/.exec(range);
      let start = 0,
        end = media.bytes.length - 1;
      if (!match || (!match[1] && !match[2]))
        return reply.code(416).header('Content-Range', `bytes */${media.bytes.length}`).send();
      if (!match[1]) start = Math.max(0, media.bytes.length - Number(match[2]));
      else {
        start = Number(match[1]);
        if (match[2]) end = Math.min(end, Number(match[2]));
      }
      if (
        start > end ||
        start >= media.bytes.length ||
        !Number.isSafeInteger(start) ||
        !Number.isSafeInteger(end)
      )
        return reply.code(416).header('Content-Range', `bytes */${media.bytes.length}`).send();
      reply.code(206).header('Content-Range', `bytes ${start}-${end}/${media.bytes.length}`);
      return reply.send(media.bytes.subarray(start, end + 1));
    }
    return reply.send(media.bytes);
  });
  app.post<{ Params: { g: string; id: string } }>(
    '/api/groups/:g/messages/:id/reaction',
    async (req) => {
      const s = req.session,
        g = req.params.g;
      capable(s, 'reactions');
      const m = getMessage(s, g, req.params.id);
      requireOriginal(s,m);
      const { emoji } = z
        .object({ emoji: z.string().max(64).refine(validReaction) })
        .parse(req.body);
      rateSend(s);
      await service.connector(s.user.familyId).react(g, m, emoji);
      return { ok: true };
    },
  );
  app.patch<{ Params: { g: string; id: string } }>('/api/groups/:g/messages/:id', async (req) => {
    const s = req.session,
      g = req.params.g;
    capable(s, 'edit');
    const m = own(s, g, req.params.id),
      c = service.connector(s.user.familyId);
      requireOriginal(s,m);
    if (m.kind !== 'text' || Date.now() - m.time > c.capabilities.editWindowMs)
      throw new AppError(409, 'EDIT_WINDOW_CLOSED');
    const { text } = z.object({ text: textSchema.min(1) }).parse(req.body);
    rateSend(s);
    const wireText = m.authorLabel
      ? `${m.authorLabel}: ${text.startsWith(m.authorLabel + ': ') ? text.slice(m.authorLabel.length + 2) : text}`
      : text;
    await c.edit(g, m, wireText);
    return { ok: true };
  });
  app.delete<{ Params: { g: string; id: string } }>('/api/groups/:g/messages/:id', async (req) => {
    const s = req.session,
      g = req.params.g;
    capable(s, 'delete');
    const m = own(s, g, req.params.id);
      requireOriginal(s,m);
    rateSend(s);
    await service.connector(s.user.familyId).delete(g, m);
    return { ok: true };
  });
  app.post<{ Params: { g: string } }>('/api/groups/:g/typing', async (req) => {
    const s = req.session,
      g = req.params.g;
    group(s, g);
    capable(s, 'typing');
    const { active } = z.object({ active: z.boolean() }).parse(req.body);
    await service.connector(s.user.familyId).presence(g, active);
    return { ok: true };
  });
  // This endpoint changes only this authenticated account's local unread state.
  // It never sends a WhatsApp read receipt or marks unseen messages in bulk.
  app.post<{ Params: { g: string } }>('/api/groups/:g/viewed', async (req) => {
    const s = req.session, g = req.params.g;
    group(s, g);
    const { ids } = z.object({ ids: z.array(z.string().min(1).max(180)).max(80) }).parse(req.body);
    for (const id of ids) getMessage(s, g, id);
    if (markLocallyViewed(store, s.user, g, ids)) service.notify(s.user.familyId, g);
    return { ok: true, unreadCount: unreadCount(store, s.user, g) };
  });
  app.post<{ Params: { g: string } }>('/api/groups/:g/read', async (req) => {
    const s = req.session,
      g = req.params.g;
    group(s, g);
    capable(s, 'read');
    const { ids } = z.object({ ids: z.array(z.string().max(180)).max(80) }).parse(req.body);
    const messages = ids.map((id) => getMessage(s, g, id));
    await service.connector(s.user.familyId).read(g, messages);
    return { ok: true };
  });
  app.get<{ Params: { g: string } }>('/api/groups/:g/history', async req => {
    group(req.session,req.params.g);
    return service.historyStatus(req.session.user.familyId,req.params.g);
  });
  app.post<{ Params: { g: string } }>('/api/groups/:g/history', async (req) => {
    const s = req.session,
      g = req.params.g;
    group(s, g);
    z.object({}).strict().parse(req.body || {});
    const status=await service.requestHistory(s.user.familyId,g);
    current(s,g);
    return status;
  });
  app.get('/health/ready', async (_req, reply) => {
    try {
      store.one('SELECT 1');
      const info = JSON.parse(readFileSync(resolve('package.json'), 'utf8'));
      return { ok: true, version: String(info.version), databaseSchema: DATABASE_SCHEMA };
    } catch { return reply.code(503).send({ ok: false }); }
  });
  app.get('/api/parent/overview', async (req) => {
    const s = req.session;
    parent(s);
    const f = s.user.familyId;
    return {
      children: store
        .all('SELECT id,name FROM users WHERE family_id=? AND role=?', f, 'child')
        .map(u => {
          const access = store.childAccess(f, u.id);
          return { ...u, senderLabel: store.identityLabel(f, u.id), paused: access.paused, accessRevision: access.revision };
        }),
      groups: store.groups(f),
      grants: store.all('SELECT user_id,group_id FROM grants WHERE family_id=?', f),
      state: service.states.get(f)?.state || 'idle',
      connector: config.connector,
      linked: !!store.one('SELECT linked FROM families WHERE id=?', f)?.linked,
      disconnect: store.one('SELECT value FROM instance_metadata WHERE key=?', 'disconnect:' + f)?.value || null,
      disconnectHistory: store.one('SELECT value FROM instance_metadata WHERE key=?', 'disconnect-history:' + f)?.value || null,
    };
  });
  app.get('/api/parent/updates', async req => {
    parent(req.session);
    return updateStatus(config, req.session.user, options.windowsUpdates);
  });
  app.post('/api/parent/updates', async req => {
    parent(req.session);
    const request = z.discriminatedUnion('action',[
      z.object({action:z.literal('check')}).strict(),
      z.object({action:z.literal('install'),target:updateTargetSchema.optional()}).strict(),
      z.object({action:z.literal('set_mode'),mode:z.enum(['notify','auto'])}).strict(),
    ]).parse(req.body);
    const state = await requestUpdate(config, req.session.user, request.action, 'mode' in request?request.mode:undefined,
      'target' in request?request.target:undefined, options.windowsUpdates);
    if (state === 'unavailable') throw new AppError(409, 'UPDATES_UNAVAILABLE');
    if (state === 'target_changed') throw new AppError(409, 'UPDATES_TARGET_CHANGED');
    if (state === 'mode_locked') throw new AppError(409, 'UPDATES_MODE_LOCKED');
    return { state };
  });
  app.get('/api/parent/diagnostics', async (req, reply) => {
    parent(req.session);
    if ((req.query as any)?.download === '1')
      reply.header('Content-Disposition', 'attachment; filename="yachad-diagnostics.json"');
    return store.diagnostics.report(req.session.user.familyId);
  });
  app.delete('/api/parent/diagnostics', async req => {
    parent(req.session);
    await store.diagnostics.clear(req.session.user.familyId);
    return { ok: true };
  });
  app.get('/api/parent/qr', async (req) => {
    parent(req.session);
    const state = service.states.get(req.session.user.familyId);
    return {
      state: state?.state || 'idle',
      qr: state?.qr ? await QRCode.toDataURL(state.qr, { margin: 2, width: 280 }) : null,
    };
  });
  app.post('/api/parent/connect', async (req) => {
    parent(req.session);
    await service.connect(req.session.user.familyId);
    return { ok: true };
  });
  app.post('/api/parent/sync-groups', async (req) => {
    parent(req.session);
    await service.syncGroups(req.session.user.familyId);
    return { ok: true };
  });
  app.put('/api/parent/grant', async (req) => {
    parent(req.session);
    const data = z
      .object({ childId: z.uuid(), groupId: z.string().max(180), allowed: z.boolean() })
      .parse(req.body);
    try {
      await service.grant(req.session.user.familyId, data.childId, data.groupId, data.allowed);
    } catch (e) {
      if (e instanceof AppError) throw e;
      throw new AppError(400, 'INVALID_GRANT');
    }
    return { ok: true };
  });
  app.put('/api/parent/identity', async (req) => {
    const s = req.session;
    parent(s);
    const data = z
      .object({
        childId: z.uuid(),
        label: z
          .string()
          .trim()
          .max(40)
          .refine((x) => !/[\x00-\x1f\u202a-\u202e\u2066-\u2069]/.test(x)),
      })
      .parse(req.body);
    if (
      !store.one(
        'SELECT 1 FROM users WHERE family_id=? AND id=? AND role=?',
        s.user.familyId,
        data.childId,
        'child',
      )
    )
      throw deny();
    store.setIdentityLabel(s.user.familyId, data.childId, data.label);
    service.notify(s.user.familyId, undefined, 'identity');
    return { ok: true };
  });
  app.put('/api/parent/child-access', async req => {
    parent(req.session);
    const data = z.object({ childId: z.uuid(), paused: z.boolean(), revision: z.number().int().min(0).max(Number.MAX_SAFE_INTEGER - 1) }).strict().parse(req.body);
    const access = store.setChildAccess(req.session.user.familyId, data.childId, data.paused, data.revision);
    service.notify(req.session.user.familyId, undefined, 'session');
    return { ok: true, childId: data.childId, ...access };
  });
  app.put('/api/parent/child-profile', async (req) => {
    const s = req.session;
    parent(s);
    const data = z.object({ childId: z.uuid(), name: accountNameSchema }).strict().parse(req.body);
    const child = store.one('SELECT id FROM users WHERE family_id=? AND id=? AND role=?',
      s.user.familyId, data.childId, 'child');
    if (!child) throw deny();
    const key = accountNameKey(data.name);
    store.tx(() => {
      if (store.one('SELECT 1 FROM users WHERE name_key=? AND id!=?', key, child.id))
        throw new AppError(409, 'NAME_UNAVAILABLE');
      // Only the name changes. Durable IDs, credentials, sessions and grants stay intact.
      store.run('UPDATE users SET name=?,name_key=? WHERE family_id=? AND id=? AND role=?',
        data.name, key, s.user.familyId, child.id, 'child');
    });
    service.notify(s.user.familyId, undefined, 'identity');
    return { ok: true, child: { id: child.id, name: data.name } };
  });
  app.post('/api/parent/child-password', async (req) => {
    parent(req.session);
    const data = z
      .object({ childId: z.uuid(), password: z.string().min(4).max(256), confirmation: z.string().max(256) })
      .parse(req.body);
    if (data.password !== data.confirmation) throw new AppError(400, 'PASSWORD_MISMATCH');
    const f = req.session.user.familyId;
    if (
      !store.one(
        'SELECT 1 FROM users WHERE family_id=? AND id=? AND role=?',
        f,
        data.childId,
        'child',
      )
    )
      throw deny();
    const pw = await passwordHash(data.password);
    store.usage.revokeUser(f, data.childId);
    store.run('UPDATE users SET password=? WHERE family_id=? AND id=?', pw, f, data.childId);
    credentialThrottle.clear(`account:${f}:${data.childId}`);
    store.run('DELETE FROM sessions WHERE family_id=? AND user_id=?', f, data.childId);
    service.notify(f, undefined, 'session');
    return { ok: true };
  });
  let addingChild = false;
  app.post('/api/parent/children', async (req) => {
    parent(req.session);
    const data = z.object({ name: accountNameSchema,
      password: z.string().min(4).max(256), confirmation: z.string().max(256) }).strict().parse(req.body);
    if (data.password !== data.confirmation) throw new AppError(400, 'PASSWORD_MISMATCH');
    if (addingChild) throw new AppError(429, 'SLOW_DOWN');
    addingChild = true;
    try {
      const pw = await passwordHash(data.password), f = req.session.user.familyId, id = randomUUID();
      store.tx(() => {
        if (store.one('SELECT 1 FROM users WHERE name_key=?', accountNameKey(data.name))) throw new AppError(409, 'NAME_UNAVAILABLE');
        if (store.one("SELECT count(*) AS n FROM users WHERE family_id=? AND role='child'", f).n >= 20)
          throw new AppError(409, 'CHILD_LIMIT');
        store.run('INSERT INTO users(id,family_id,name,name_key,role,password) VALUES(?,?,?,?,?,?)',
          id, f, data.name, accountNameKey(data.name), 'child', pw);
      });
      service.notify(f, undefined, 'identity');
      return { ok: true, id };
    } finally { addingChild = false; }
  });
  app.post('/api/parent/disconnect', async (req) => {
    parent(req.session);
    const { erase } = z.object({ erase: z.boolean() }).parse(req.body);
    return { ok: true, ...await service.disconnect(req.session.user.familyId, erase) };
  });
  app.delete('/api/parent/family', async (req) => {
    const s = req.session;
    parent(s);
    const { password } = z.object({ password: z.string().max(256) }).parse(req.body);
    const row = store.one(
      'SELECT password FROM users WHERE family_id=? AND id=?',
      s.user.familyId,
      s.user.id,
    );
    if (!credentialThrottle.reserve(`account:${s.user.familyId}:${s.user.id}`, req.ip))
      throw new AppError(429, 'LOGIN_WAIT');
    if (!(await limitedVerify(password, row.password))) throw new AppError(403, 'WRONG_PASSWORD');
    await service.disconnect(s.user.familyId, true);
    await store.diagnostics.forget(s.user.familyId);
    store.run('DELETE FROM families WHERE id=?', s.user.familyId);
    service.notify(s.user.familyId, undefined, 'session');
    return { ok: true };
  });
  // SSE carries invalidations only, never chat text or IDs from other families/groups.
  const streams = new Set<(force?: boolean) => void>();
  let shuttingDown = false;
  app.get('/api/events', async (req, reply) => {
    if (shuttingDown) throw new AppError(503, 'SERVER_SHUTTING_DOWN');
    const s = req.session;
    const same = [...streams].length;
    if (same >= 64) throw new AppError(429, 'TOO_MANY_STREAMS');
    reply.hijack();
    reply.raw.writeHead(200, {
      'Content-Type': 'text/event-stream',
      'Cache-Control': 'no-store',
      Connection: 'keep-alive',
      'X-Accel-Buffering': 'no',
      'X-Content-Type-Options': 'nosniff',
    });
    let closed = false;
    const end = (force = false) => {
      if (!closed) {
        closed = true;
        clearInterval(heartbeat);
        service.listeners.delete(listener);
        if (!reply.raw.destroyed) reply.raw.end();
      }
      // SSE contains disposable invalidations, not message deliveries. Do not
      // let even an already-ended response's buffered flush block shutdown.
      if (force) reply.raw.destroy();
    };
    const emit = (event: string, data: unknown) => {
      if (closed || shuttingDown) return;
      if (!store.session(s.sid)) {
        reply.raw.write(`event: expired\ndata: {}\n\n`);
        end();
        return;
      }
      if (reply.raw.writableLength > 65536) {
        end();
        return;
      }
      if (s.user.role === 'child' && !['ready', 'heartbeat', 'usage'].includes(event)) {
        try { store.usage.require(s); }
        catch { return; }
      }
      reply.raw.write(`event: ${event}\ndata: ${JSON.stringify(data)}\n\n`);
    };
    const listener = (f: string, g: string | undefined, type: string) => {
      if (f !== s.user.familyId) return;
      if (type === 'usage') { emit('usage', {}); return; }
      if (type === 'revoked' || type === 'reset' || type === 'session') {
        emit('refresh', {});
        return;
      }
      if (g && !store.allowed(s.user, g)) return;
      emit(type === 'typing' || type === 'paused' ? type : 'refresh', g ? { group: g } : {});
    };
    service.listeners.add(listener);
    streams.add(end);
    const heartbeat = setInterval(() => emit('heartbeat', {}), 10000);
    req.raw.on('close', end);
    // Logical end is not transport close: retain shutdown reachability until
    // buffered writes finish or the socket is destroyed.
    reply.raw.once('close', () => {
      streams.delete(end);
      req.raw.off('close', end);
      end();
    });
    emit('ready', {});
  });
  const cleanup = setInterval(() => {
    store.run('DELETE FROM sessions WHERE expires<?', Date.now());
    try { store.usage.cleanExpiredSessions(); } catch { /* Retry on the next sweep after a server clock/storage fault. */ }
    store.run('DELETE FROM credential_attempts WHERE until<=?', Date.now());
    for (const [k, v] of sends) if (!v.some((t) => t > Date.now() - 60000)) sends.delete(k);
  }, 60000);
  cleanup.unref();
  if (existsSync(resolve('dist/index.html'))) {
    await app.register(serveStatic, { root: resolve('dist'), prefix: '/', cacheControl: false });
    app.setNotFoundHandler((req, reply) =>
      req.url.startsWith('/api/')
        ? reply.code(404).send({ error: 'NOT_FOUND' })
        : reply.type('text/html').send(readFileSync(resolve('dist/index.html'))),
    );
  }
  app.addHook('preClose', async () => {
    shuttingDown = true;
    clearInterval(cleanup);
    // Settle a graceful stop immediately; abrupt interruption retains only the
    // bounded lease tail. A clock fault must not prevent transport shutdown.
    try { store.usage.restart(); } catch { /* Fail closed at the persisted lease expiry. */ }
    // onClose runs only AFTER HTTP connections drain. Closing long-lived SSE
    // there deadlocks shutdown and can leave a fresh data lock after SIGKILL.
    for (const end of streams) end(true);
  });
  app.addHook('onClose', async () => {
    await service.close();
    await store.diagnostics.stop();
    if (!options.store) store.close();
  });
  if (options.boot !== false) await service.boot();
  return { app, store, service };
}
