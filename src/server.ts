import Fastify from 'fastify';
import { performance } from 'node:perf_hooks';
import cookie from '@fastify/cookie';
import multipart from '@fastify/multipart';
import serveStatic from '@fastify/static';
import { resolve } from 'node:path';
import { existsSync, readFileSync } from 'node:fs';
import { z } from 'zod';
import QRCode from 'qrcode';
import { hash, verifyPassword, passwordHash } from './crypto.js';
import { timingSafeEqual, randomUUID } from 'node:crypto';
import type { Config } from './config.js';
import { Store, type Session } from './store.js';
import { ChatService, AppError, deny } from './service.js';
import { voiceToOpus } from './media.js';
import { updateStatus, requestUpdate } from './updates.js';
import type { ConnectorHooks, Connector, StoredMessage, Kind } from './connectors/types.js';
const textSchema = z.string().trim().max(12000);
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
  options: { store?: Store; factory?: (h: ConnectorHooks) => Connector; boot?: boolean;
    bootstrap?: { token: string; expiresAt: number } } = {},
) {
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
  const loginAttempts = new Map<string, { n: number; until: number }>();
  let activeLogins = 0;
  const sends = new Map<string, number[]>();
  const pending = new Set<string>();
  const activeUploads = new Set<string>();
  app.decorateRequest('session', null as unknown as Session);
  app.setErrorHandler((error, req, reply) => {
    const e = error as any;
    const status =
      e instanceof AppError
        ? e.status
        : e instanceof z.ZodError
          ? 400
          : e.statusCode === 413 || e.code === 'FST_REQ_FILE_TOO_LARGE'
            ? 413
            : 500;
    reply.code(status).send({
      error:
        e instanceof AppError
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
    if (origin && origin !== config.origin) throw new AppError(403, 'ORIGIN_DENIED');
    if (req.url === '/api/login' || req.url === '/api/setup') return;
    const s = store.session(req.cookies.sid || '');
    if (!s) throw new AppError(401, 'SIGN_IN_REQUIRED');
    req.session = s;
    if (!['GET', 'HEAD'].includes(req.method)) {
      if (origin !== config.origin || req.headers['x-csrf-token'] !== s.csrf)
        throw new AppError(403, 'CSRF_DENIED');
      if (req.url !== '/api/logout') store.session(s.sid, true);
    }
  });
  const parent = (s: Session) => {
    if (s.user.role !== 'parent') throw new AppError(403, 'PARENT_ONLY');
  };
  const group = (s: Session, g: string) => service.requireGroup(s.user, g);
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
  let settingUp = false;
  app.post('/api/setup', async (req) => {
    const capability = req.headers['x-setup-token'];
    const bootstrap = options.bootstrap;
    if (req.headers.origin !== config.origin) throw new AppError(403, 'ORIGIN_DENIED');
    if (!bootstrap || Date.now() > bootstrap.expiresAt || typeof capability !== 'string' ||
        !timingSafeEqual(Buffer.from(hash(capability)), Buffer.from(hash(bootstrap.token))) ||
        store.one('SELECT 1 FROM families LIMIT 1')) throw new AppError(403, 'SETUP_CLOSED');
    if (settingUp) throw new AppError(409, 'SETUP_BUSY');
    const data = z.object({ name: z.string().trim().min(1).max(80),
      username: z.string().trim().toLowerCase().regex(/^[a-z0-9._-]{3,60}$/),
      password: z.string().min(14).max(256), confirmation: z.string().max(256) }).strict().parse(req.body);
    if (data.password !== data.confirmation) throw new AppError(400, 'PASSWORD_MISMATCH');
    settingUp = true;
    try {
      await store.createFamily(data.name, data, undefined, true);
      // Consume the capability only after durable creation; restarting never reopens setup.
      options.bootstrap = undefined;
      return { ok: true };
    } finally { settingUp = false; }
  });
  app.post('/api/login', async (req, reply) => {
    if (req.headers.origin !== config.origin) throw new AppError(403, 'ORIGIN_DENIED');
    const input = z
      .object({ username: z.string().min(1).max(80), password: z.string().max(256) })
      .parse(req.body);
    const now = Date.now();
    if (loginAttempts.size > 10000) {
      for (const [k, v] of loginAttempts) if (v.until < now) loginAttempts.delete(k);
      if (loginAttempts.size > 10000) throw new AppError(429, 'SLOW_DOWN');
    }
    const keys = [`ip:${req.ip}`, `user:${input.username.toLowerCase()}`];
    for (const k of keys) {
      const a = loginAttempts.get(k);
      if (a && a.until > now && a.n >= 8) throw new AppError(429, 'SLOW_DOWN');
    }
    if (activeLogins >= 3) throw new AppError(429, 'SLOW_DOWN');
    for (const k of keys) {
      const a = loginAttempts.get(k);
      loginAttempts.set(k, { n: a && a.until > now ? a.n + 1 : 1, until: now + 15 * 60_000 });
    }
    const row = store.one('SELECT * FROM users WHERE username=?', input.username.toLowerCase());
    activeLogins++;
    let valid = false;
    try {
      valid = await verifyPassword(
        input.password,
        row?.password || '00000000000000000000000000000000:' + '00'.repeat(64),
      );
    } finally {
      activeLogins--;
    }
    if (!row || !valid) throw new AppError(401, 'INVALID_CREDENTIALS');
    if (req.cookies.sid) store.run('DELETE FROM sessions WHERE id=?', hash(req.cookies.sid));
    const session = store.createSession(store.user(row));
    for (const k of keys) loginAttempts.delete(k);
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
  app.post('/api/logout', async (req, reply) => {
    store.run('DELETE FROM sessions WHERE id=?', hash(req.session.sid));
    service.notify(req.session.user.familyId, undefined, 'session');
    reply.clearCookie('sid', { path: '/' });
    return { ok: true };
  });
  app.post('/api/password', async (req) => {
    const data = z
      .object({ current: z.string().max(256), next: z.string().min(14).max(256), confirmation: z.string().max(256) })
      .parse(req.body);
    if (data.next !== data.confirmation) throw new AppError(400, 'PASSWORD_MISMATCH');
    const s = req.session,
      row = store.one(
        'SELECT password FROM users WHERE family_id=? AND id=?',
        s.user.familyId,
        s.user.id,
      );
    if (!(await verifyPassword(data.current, row.password)))
      throw new AppError(403, 'WRONG_PASSWORD');
    const next = await passwordHash(data.next);
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
  app.get('/api/groups', async (req) => {
    const s = req.session,
      f = s.user.familyId;
    return store
      .groups(f)
      .filter((g) => store.allowed(s.user, g.id))
      .map((g) => ({
        ...g,
        last: (() => {
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
    attachment?: { bytes: Buffer; mime: string; filename: string; kind: Kind },
  ) {
    current(s, g);
    const f = s.user.familyId;
    const c = service.connector(f);
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
    if (reply) capable(s, 'reply');
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
      current(s, g);
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
              id: markerId,
              groupId: g,
              text: marker.text,
              kind: 'text',
              reply,
            });
          } catch {
            service.update(f, g, markerId, { status: 'failed' });
            throw new Error('Marker send failed');
          }
          current(s, g);
          await service.ingest(f, { ...sentMarker, id: markerId });
          marker = store.getMessage(f, g, markerId);
        }
        if (!wireReply) wireReply = marker;
      }
      current(s, g);
      const sent = await c.send({
        id,
        groupId: g,
        text: wireText,
        kind: attachment?.kind || 'text',
        bytes: attachment?.bytes,
        mime: attachment?.mime,
        filename: attachment?.filename,
        reply: wireReply,
      });
      current(s, g);
      await service.ingest(f, {
        ...sent,
        id,
        bytes: attachment?.bytes,
        mime: attachment?.mime || sent.mime,
        filename: attachment?.filename || sent.filename,
        replyId: wireReply?.id,
        status: sent.status,
      });
      return { message: service.public(f, store.getMessage(f, g, id)!) };
    } catch {
      service.update(f, g, id, { status: 'failed' });
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
      let kind: Kind = 'document';
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
      const { emoji } = z
        .object({ emoji: z.enum(['', '💚', '❤️', '👍', '😂', '✨', '🌸', '🎉', '🙏']) })
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
      return { ok: true, version: String(info.version), databaseSchema: 1 };
    } catch { return reply.code(503).send({ ok: false }); }
  });
  app.get('/api/parent/overview', async (req) => {
    const s = req.session;
    parent(s);
    const f = s.user.familyId;
    return {
      children: store
        .all('SELECT id,display,username FROM users WHERE family_id=? AND role=?', f, 'child')
        .map((u) => ({ ...u, senderLabel: store.identityLabel(f, u.id) })),
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
    return updateStatus(config, req.session.user);
  });
  app.post('/api/parent/updates', async req => {
    parent(req.session);
    const { action } = z.object({ action: z.enum(['check', 'install']) }).strict().parse(req.body);
    const state = await requestUpdate(config, req.session.user, action);
    if (state === 'unavailable') throw new AppError(409, 'UPDATES_UNAVAILABLE');
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
  app.post('/api/parent/child-password', async (req) => {
    parent(req.session);
    const data = z
      .object({ childId: z.uuid(), password: z.string().min(14).max(256), confirmation: z.string().max(256) })
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
    store.run('UPDATE users SET password=? WHERE family_id=? AND id=?', pw, f, data.childId);
    store.run('DELETE FROM sessions WHERE family_id=? AND user_id=?', f, data.childId);
    service.notify(f, undefined, 'session');
    return { ok: true };
  });
  let addingChild = false;
  app.post('/api/parent/children', async (req) => {
    parent(req.session);
    const data = z.object({ username: z.string().trim().toLowerCase().regex(/^[a-z0-9._-]{3,60}$/),
      display: z.string().trim().min(1).max(40).refine(x => !/[\x00-\x1f\u202a-\u202e\u2066-\u2069]/.test(x)),
      password: z.string().min(14).max(256), confirmation: z.string().max(256) }).strict().parse(req.body);
    if (data.password !== data.confirmation) throw new AppError(400, 'PASSWORD_MISMATCH');
    if (addingChild) throw new AppError(429, 'SLOW_DOWN');
    addingChild = true;
    try {
      const pw = await passwordHash(data.password), f = req.session.user.familyId, id = randomUUID();
      store.tx(() => {
        if (store.one('SELECT 1 FROM users WHERE username=?', data.username)) throw new AppError(409, 'USERNAME_UNAVAILABLE');
        if (store.one("SELECT count(*) AS n FROM users WHERE family_id=? AND role='child'", f).n >= 20)
          throw new AppError(409, 'CHILD_LIMIT');
        store.run('INSERT INTO users(id,family_id,username,display,role,password) VALUES(?,?,?,?,?,?)',
          id, f, data.username, data.display, 'child', pw);
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
    if (!(await verifyPassword(password, row.password))) throw new AppError(403, 'WRONG_PASSWORD');
    await service.disconnect(s.user.familyId, true);
    await store.diagnostics.forget(s.user.familyId);
    store.run('DELETE FROM families WHERE id=?', s.user.familyId);
    service.notify(s.user.familyId, undefined, 'session');
    return { ok: true };
  });
  // SSE carries invalidations only, never chat text or IDs from other families/groups.
  const streams = new Set<() => void>();
  app.get('/api/events', async (req, reply) => {
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
    const end = () => {
      if (closed) return;
      closed = true;
      clearInterval(heartbeat);
      service.listeners.delete(listener);
      streams.delete(end);
      reply.raw.end();
    };
    const emit = (event: string, data: unknown) => {
      if (!store.session(s.sid)) {
        reply.raw.write(`event: expired\ndata: {}\n\n`);
        end();
        return;
      }
      if (reply.raw.writableLength > 65536) {
        end();
        return;
      }
      reply.raw.write(`event: ${event}\ndata: ${JSON.stringify(data)}\n\n`);
    };
    const listener = (f: string, g: string | undefined, type: string) => {
      if (f !== s.user.familyId) return;
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
    emit('ready', {});
  });
  const cleanup = setInterval(() => {
    store.run('DELETE FROM sessions WHERE expires<?', Date.now());
    for (const [k, v] of loginAttempts) if (v.until < Date.now()) loginAttempts.delete(k);
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
  app.addHook('onClose', async () => {
    clearInterval(cleanup);
    for (const end of streams) end();
    await service.close();
    await store.diagnostics.stop();
    if (!options.store) store.close();
  });
  if (options.boot !== false) await service.boot();
  return { app, store, service };
}
