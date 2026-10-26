import { IngestionQueue } from './ingestion.js';
import makeWASocket, {
  BufferJSON,
  initAuthCreds,
  proto,
  makeCacheableSignalKeyStore,
  DisconnectReason,
  normalizeMessageContent,
  downloadMediaMessage,
  downloadContentFromMessage,
  generateMessageIDV2,
  getAudioDuration,
  type WASocket,
  type WAMessage,
  type AuthenticationState,
  type SignalDataTypeMap,
  type SignalDataSet,
  type GroupMetadata,
} from '@whiskeysockets/baileys';
import pino from 'pino';
import { thumbnail } from '../media.js';
import type {
  Connector,
  ConnectorHooks,
  Capabilities,
  Incoming,
  Outgoing,
  StoredMessage,
  Receipt,
} from './types.js';
const logger = pino({ level: 'silent' });
const stringify = (x: unknown) => JSON.stringify(x, BufferJSON.replacer);
const parse = (s: string) => JSON.parse(s, BufferJSON.reviver);
export function encryptedAuth(h: ConnectorHooks): { state: AuthenticationState; save: () => void } {
  const creds = h.authGet('creds');
  const state: AuthenticationState = {
    creds: creds ? parse(creds) : initAuthCreds(),
    keys: {
      get: async (type, ids) => {
        const result: Record<string, any> = {};
        for (const id of ids) {
          const v = h.authGet(`signal:${type}:${id}`);
          if (v) {
            const data = parse(v);
            result[id] =
              type === 'app-state-sync-key'
                ? proto.Message.AppStateSyncKeyData.fromObject(data)
                : data;
          }
        }
        return result;
      },
      set: async (data: SignalDataSet) => {
        const batch: Record<string, string | null> = {};
        for (const [type, entries] of Object.entries(data))
          for (const [id, value] of Object.entries(entries || {}))
            batch[`signal:${type}:${id}`] = value == null ? null : stringify(value);
        h.authBatch(batch);
      },
    },
  };
  return { state, save: () => h.authSet('creds', stringify(state.creds)) };
}
const receipt = (n: number | null | undefined): Receipt =>
  n === 4 || n === 5
    ? 'read'
    : n === 3
      ? 'delivered'
      : n === 2
        ? 'sent'
        : n === 1
          ? 'pending'
          : n === 0
            ? 'failed'
            : 'unknown';
/** Only call AFTER checking the stable remote group JID. No embedded quoted text, contacts, previews, or URLs are projected. */
function stripEmbeddedContext(value: any): any {
  if (
    value === null ||
    typeof value !== 'object' ||
    Buffer.isBuffer(value) ||
    value instanceof Uint8Array
  )
    return value;
  if (Array.isArray(value)) return value.map(stripEmbeddedContext);
  const out: Record<string, any> = {};
  for (const [k, v] of Object.entries(value))
    if (
      ![
        'contextInfo',
        'quotedMessage',
        'externalAdReply',
        'contactMessage',
        'contactsArrayMessage',
        'locationMessage',
        'liveLocationMessage',
      ].includes(k)
    )
      out[k] = stripEmbeddedContext(v);
  return out;
}
export function normalizeIncoming(raw: WAMessage): Incoming | undefined {
  const groupId = raw.key.remoteJid,
    id = raw.key.id;
  if (!groupId?.endsWith('@g.us') || !id) return;
  const content = normalizeMessageContent(raw.message);
  if (!content) return;
  const base: Incoming = {
    id,
    groupId,
    sender: raw.key.fromMe ? 'אני' : (raw.pushName || 'משתתף/ת').slice(0, 80),
    fromMe: !!raw.key.fromMe,
    time: Number(raw.messageTimestamp || Date.now() / 1000) * 1000,
    kind: 'unsupported',
    text: 'סוג הודעה זה עדיין אינו נתמך',
    status: receipt(raw.status),
    raw: stringify(stripEmbeddedContext(raw)),
  };
  if (content.reactionMessage) {
    const r = content.reactionMessage;
    if (r.key?.remoteJid && r.key.remoteJid !== groupId) return;
    return {
      ...base,
      kind: 'text',
      text: '',
      reaction: {
        target: r.key?.id || '',
        actor: raw.key.fromMe ? 'self' : raw.key.participant || 'unknown',
        emoji: r.text || '',
      },
    };
  }
  if (content.protocolMessage) {
    const p = content.protocolMessage;
    if (p.key?.remoteJid && p.key.remoteJid !== groupId) return;
    if (p.type === proto.Message.ProtocolMessage.Type.REVOKE)
      return { ...base, id: p.key?.id || id, deleted: true, text: '' };
    if (p.type === proto.Message.ProtocolMessage.Type.MESSAGE_EDIT && p.editedMessage) {
      const nested = normalizeIncoming({
        ...raw,
        key: { ...raw.key, id: p.key?.id || id },
        message: p.editedMessage,
      });
      return nested ? { ...nested, edited: true } : undefined;
    }
    return;
  }
  // View-once and disappearing content is deliberately never retained.
  if (
    raw.message?.viewOnceMessage ||
    raw.message?.viewOnceMessageV2 ||
    raw.message?.viewOnceMessageV2Extension ||
    raw.message?.ephemeralMessage
  )
    return {
      ...base,
      text: 'הודעה זמנית או לצפייה חד־פעמית — לפתיחה ב־WhatsApp בלבד',
      raw: undefined,
    };
  let body: any;
  if (content.lottieStickerMessage || content.stickerMessage?.isLottie)
    return {...base, text:'מדבקה בפורמט שאינו נתמך כאן — אפשר לפתוח אותה ב־WhatsApp.',raw:undefined};
  if (typeof content.conversation === 'string') {
    base.kind = 'text';
    base.text = content.conversation || '';
  } else if (content.extendedTextMessage) {
    body = content.extendedTextMessage;
    base.kind = 'text';
    base.text = body.text || '';
  } else {
    const pairs = [
      ['imageMessage', 'image'],
      ['videoMessage', 'video'],
      ['documentMessage', 'document'],
      ['audioMessage', 'audio'],
      ['stickerMessage', 'sticker'],
    ] as const;
    for (const [field, kind] of pairs)
      if (content[field]) {
        body = content[field];
        base.kind = kind;
        base.text = body.caption || '';
        base.mime = body.mimetype;
        base.filename = body.fileName;
        break;
      }
  }
  if (body?.contextInfo?.expiration)
    return {
      ...base,
      kind: 'unsupported',
      text: 'הודעה זמנית — לפתיחה ב־WhatsApp בלבד',
      raw: undefined,
    };
  // Resolve only an existing message from THIS group in the application layer. Never copy quoted payload.
  const ctx = body?.contextInfo;
  if (ctx?.stanzaId && (!ctx.remoteJid || ctx.remoteJid === groupId)) base.replyId = ctx.stanzaId;
  return base;
}
export function validateMediaSource(raw: WAMessage) {
  const content = normalizeMessageContent(raw.message);
  const body: any =
    content?.imageMessage ||
    content?.videoMessage ||
    content?.audioMessage ||
    content?.documentMessage ||
    content?.stickerMessage;
  if (!body) throw new Error('No media');
  if (body.url) {
    const u = new URL(body.url);
    if (
      u.protocol !== 'https:' ||
      u.username ||
      u.password ||
      u.port ||
      !(u.hostname === 'whatsapp.net' || u.hostname.endsWith('.whatsapp.net'))
    )
      throw new Error('Untrusted media source');
  }
  if (
    body.directPath &&
    (!body.directPath.startsWith('/') ||
      body.directPath.startsWith('//') ||
      /[\\\r\n]/.test(body.directPath))
  )
    throw new Error('Invalid media path');
  if (!body.directPath && !body.url) throw new Error('Missing media source');
}
export async function incomingMediaStream(raw:WAMessage,reuploadRequest:WASocket['updateMediaMessage'],
  transport={message:downloadMediaMessage,direct:downloadContentFromMessage}) {
  validateMediaSource(raw);
  const sticker=normalizeMessageContent(raw.message)?.stickerMessage;
  // Sanitized plain objects omit protobuf's inherited url property. The pinned
  // high-level helper rejects those even though the supported directPath exists.
  if(sticker?.directPath&&!sticker.url)
    return transport.direct(sticker,'sticker',{});
  return transport.message(raw,'stream',{}, {logger,reuploadRequest});
}
export class BaileysConnector implements Connector {
  readonly capabilities: Capabilities = {
    reply: true,
    reactions: true,
    image: true,
    video: true,
    document: true,
    sticker: true,
    audio: true,
    edit: true,
    delete: true,
    typing: true,
    read: true,
    receipts: true,
    historyRequest: true,
    editWindowMs: 15 * 60_000,
  };
  sock?: WASocket;
  ingestion?: IngestionQueue;
  stopping = false;
  timer?: ReturnType<typeof setTimeout>;
  attempt = 0;
  generation = 0;
  cache = new Map<string, { at: number; value: GroupMetadata }>();
  constructor(
    readonly hooks: ConnectorHooks,
    readonly socketFactory = makeWASocket,
    private ingestionBudget = {active:0},
  ) {}
  async start() {
    if (this.sock) return;
    this.stopping = false;
    await this.connect();
  }
  async connect() {
    this.ingestion?.dispose();
    const generation = ++this.generation;
    this.hooks.state(this.attempt ? 'reconnecting' : 'connecting');
    const auth = encryptedAuth(this.hooks);
    const sock = this.socketFactory({
      auth: { creds: auth.state.creds, keys: makeCacheableSignalKeyStore(auth.state.keys, logger) },
      logger,
      markOnlineOnConnect: false,
      syncFullHistory: false,
      shouldSyncHistoryMessage: () => true,
      generateHighQualityLinkPreview: false,
      shouldIgnoreJid: (jid) =>
        !jid.endsWith('@g.us') && !jid.endsWith('@s.whatsapp.net') && !jid.endsWith('@lid'),
      getMessage: async (key) => {
        if (!key.remoteJid || !key.id || !this.hooks.allowed(key.remoteJid)) return;
        const m = this.hooks.getMessage(key.remoteJid, key.id);
        return m?.raw ? parse(m.raw).message : undefined;
      },
      cachedGroupMetadata: async (jid) => {
        const x = this.cache.get(jid);
        return x && Date.now() - x.at < 300_000 ? x.value : undefined;
      },
      connectTimeoutMs: 30_000,
      defaultQueryTimeoutMs: 30_000,
    });
    this.sock = sock;
    const valid = () => !this.stopping && generation === this.generation;
    sock.ev.on('creds.update', () => {
      if (valid()) auth.save();
    });
    sock.ev.on('connection.update', (u) => {
      if (!valid()) return;
      if (u.qr) this.hooks.state('qr', u.qr);
      if (u.connection === 'open') {
        this.attempt = 0;
        auth.save();
        this.hooks.state('connected');
      }
      if (u.connection === 'close') {
        this.sock = undefined;
        const code = (u.lastDisconnect?.error as any)?.output?.statusCode;
        if (
          [
            DisconnectReason.loggedOut,
            DisconnectReason.badSession,
            DisconnectReason.multideviceMismatch,
            DisconnectReason.forbidden,
          ].includes(code)
        ) {
          this.stopping = true;
          this.hooks.state('needs_pairing');
          return;
        }
        if (code === DisconnectReason.connectionReplaced) {
          this.stopping = true;
          this.hooks.state('conflict');
          return;
        }
        this.hooks.state('reconnecting');
        const wait =
          code === DisconnectReason.restartRequired
            ? 200
            : Math.min(60_000, 1000 * 2 ** Math.min(this.attempt++, 6)) + Math.random() * 500;
        this.timer = setTimeout(() => {
          if (valid()) void this.connect().catch(() => this.hooks.state('needs_pairing'));
        }, wait);
      }
    });
    const queue = new IngestionQueue(async m => { if (valid() && this.hooks.allowed(m.groupId)) await this.hooks.incoming(m); },
      g => this.hooks.allowed(g), valid, notice => this.hooks.history?.(notice),undefined,this.ingestionBudget);
    this.ingestion = queue;
    const prepare = function* (this: BaileysConnector, messages: WAMessage[], history: boolean, requestId?: string): Generator<Incoming> {
      for (const raw of messages) {
        if (!valid() || !raw?.key?.remoteJid || !this.hooks.allowed(raw.key.remoteJid)) continue;
        try {
          const m = normalizeIncoming(raw);
          if (!m) continue;
          const retention = this.hooks.retentionToken?.(m.groupId);
          m.guard = () => valid() && this.hooks.allowed(m.groupId) && (!this.hooks.retentionToken || this.hooks.retentionToken(m.groupId) === retention);
          if (history) m.history = { requestId };
          // Retain only sanitized raw data in media closures, never embedded quotes.
          if (m.raw && Buffer.byteLength(m.raw) <= 256 * 1024 && ['image','video','document','sticker','audio'].includes(m.kind)) {
            const safeRaw = parse(m.raw);
            m.download = async () => {
              if (!m.guard!()) throw new Error('Revoked');
              const stream = await incomingMediaStream(safeRaw,sock.updateMediaMessage);
              const chunks: Buffer[] = []; let size = 0;
              for await (const chunk of stream) {
                size += chunk.length;
                if (size > this.hooks.maxMedia || !m.guard!()) { stream.destroy(); throw new Error('Media unavailable'); }
                chunks.push(chunk);
              }
              return Buffer.concat(chunks);
            };
          }
          yield m;
        } catch { this.hooks.history?.({groupId:raw.key.remoteJid,phase:'error',received:1,processed:0,history,requestId}); }
      }
    }.bind(this);
    const process = (messages: WAMessage[], history = false, requestId?: string) => queue.submit(prepare(messages, history, requestId), history, requestId);
    sock.ev.on('messages.upsert', event => { if (!(event as any).requestId) process(event.messages); });
    sock.ev.on('messaging-history.set', event => {
      const id=typeof event.peerDataRequestSessionId==='string'&&event.peerDataRequestSessionId.length<=256?event.peerDataRequestSessionId:undefined;
      if(valid()&&!event.messages.length&&id)this.hooks.historyEmpty?.(id);
      process(event.messages,true,id);
    });
    sock.ev.on('messages.update', (events) => {
      for (const { key, update } of events) {
        if (!valid() || !key.remoteJid || !key.id || !this.hooks.allowed(key.remoteJid)) continue;
        if (update.message === null)
          this.hooks.update(key.remoteJid, key.id, { deleted: true, text: '' });
        else if (update.message)
          void Promise.resolve(process([{
            key,
            message: update.message,
            messageTimestamp: update.messageTimestamp,
          } as WAMessage])).catch(() => {});
        if (update.status !== undefined)
          this.hooks.update(key.remoteJid, key.id, { status: receipt(update.status) });
      }
    });
    sock.ev.on('messages.delete', (event) => {
      if ('keys' in event)
        for (const key of event.keys)
          if (valid() && key.remoteJid && key.id && this.hooks.allowed(key.remoteJid))
            this.hooks.update(key.remoteJid, key.id, { deleted: true, text: '' });
    });
    sock.ev.on('messages.reaction', (events) => {
      for (const { key, reaction } of events) {
        if (!valid() || !key.remoteJid || !this.hooks.allowed(key.remoteJid)) continue;
        void this.hooks
          .incoming({
            id: 'reaction',
            groupId: key.remoteJid,
            sender: '',
            fromMe: !!reaction.key?.fromMe,
            time: Date.now(),
            kind: 'text',
            text: '',
            reaction: {
              target: key.id || '',
              actor: reaction.key?.fromMe ? 'self' : reaction.key?.participant || 'unknown',
              emoji: reaction.text || '',
            },
          })
          .catch(() => {});
      }
    });
    sock.ev.on('presence.update', (event) => {
      if (valid() && this.hooks.allowed(event.id))
        this.hooks.typing(
          event.id,
          Object.values(event.presences).some(
            (p) => p.lastKnownPresence === 'composing' || p.lastKnownPresence === 'recording',
          ),
        );
    });
    sock.ev.on('groups.update', (events) => {
      for (const e of events) if (e.id) this.cache.delete(e.id);
    });
    sock.ev.on('group-participants.update', (event) => {
      this.cache.delete(event.id);
    });
  }
  require() {
    if (!this.sock) throw new Error('Disconnected');
    return this.sock;
  }
  key(m: StoredMessage) {
    if (!m.raw) throw new Error('Original message unavailable');
    return parse(m.raw).key;
  }
  async groups() {
    const groups = await this.require().groupFetchAllParticipating();
    return Object.values(groups).map((g) => ({ id: g.id, title: g.subject }));
  }
  async send(m: Outgoing) {
    const sock = this.require();
    const content: any =
      m.kind === 'text'
        ? { text: m.text, linkPreview: null }
        : m.kind === 'audio'
          ? { audio: m.bytes, mimetype: m.mime, ptt: true }
          : m.kind === 'sticker'
            ? { sticker: m.bytes }
            : m.kind === 'document'
              ? { document: m.bytes, mimetype: m.mime, fileName: m.filename, caption: m.text }
              : { [m.kind]: m.bytes, caption: m.text, mimetype: m.mime };
    if (m.kind === 'audio' && m.bytes) {
      const seconds = await getAudioDuration(m.bytes);
      if (!Number.isFinite(seconds)) throw new Error('Audio duration unavailable');
      content.seconds = seconds;
      content.waveform = new Uint8Array(0);
    }
    if ((m.kind === 'image' || m.kind === 'video') && m.bytes)
      content.jpegThumbnail = await thumbnail(m.bytes);
    const quoted = m.reply?.raw ? parse(m.reply.raw) : undefined;
    const sent = await sock.sendMessage(m.groupId, content, { messageId: m.id, quoted });
    if (!sent) throw new Error('No acknowledgement');
    return normalizeIncoming(sent)!;
  }
  async react(g: string, m: StoredMessage, emoji: string) {
    await this.require().sendMessage(g, { react: { text: emoji, key: this.key(m) } });
  }
  async edit(g: string, m: StoredMessage, text: string) {
    await this.require().sendMessage(g, { text, edit: this.key(m), linkPreview: null });
    this.hooks.update(g, m.id, { text, edited: true });
  }
  async delete(g: string, m: StoredMessage) {
    await this.require().sendMessage(g, { delete: this.key(m) });
    this.hooks.update(g, m.id, { text: '', deleted: true });
  }
  async presence(g: string, active: boolean) {
    await this.require().sendPresenceUpdate(active ? 'composing' : 'paused', g);
  }
  async read(g: string, m: StoredMessage[]) {
    const keys = m.filter((x) => !x.fromMe && x.raw).map((x) => this.key(x));
    if (keys.length) await this.require().readMessages(keys);
  }
  historyAnchor(g: string, m: StoredMessage) {
    if (m.groupId !== g || m.deleted || m.kind === 'unsupported' || ['pending','failed'].includes(m.status) || !m.raw || !Number.isFinite(m.time) || m.time <= 0) return false;
    try {
      const parsed=parse(m.raw);
      if(typeof parsed?.key?.fromMe!=='boolean')return false;
      const raw = proto.WebMessageInfo.fromObject(parsed), key = raw.key;
      const time = Number(raw.messageTimestamp) * 1000;
      return !!key && key.remoteJid === g && key.id === m.id && typeof key.fromMe === 'boolean' && key.fromMe === m.fromMe && Number.isFinite(time) && time > 0 && Math.abs(time - m.time) < 1000;
    } catch { return false; }
  }
  async history(g: string, m: StoredMessage) {
    if (!this.hooks.allowed(g) || !this.historyAnchor(g,m)) throw new Error('History anchor unavailable');
    return this.require().fetchMessageHistory(40, this.key(m), Math.floor(m.time / 1000));
  }
  async approved(g: string) {
    await this.require().presenceSubscribe(g);
  }
  async stop(logout = false) {
    this.ingestion?.dispose();
    this.stopping = true;
    ++this.generation;
    clearTimeout(this.timer);
    const sock = this.sock;
    this.sock = undefined;
    this.cache.clear();
    let unlinkRequested = false;
    if (sock) {
      try {
        if (logout) {
          let timer: ReturnType<typeof setTimeout> | undefined;
          try {
            unlinkRequested = await Promise.race([
              sock.logout().then(() => true, () => false),
              new Promise<false>(resolve => { timer = setTimeout(() => resolve(false), 8000); }),
            ]);
          } finally { clearTimeout(timer); }
        }
      } finally { sock.end(undefined); }
    }
    this.hooks.state('idle');
    // This Baileys version awaits sendNode, not a server acknowledgment. A
    // resolved logout is only a sent request; the phone must verify revocation.
    if (logout) return { remoteRevoked: false, unlinkRequested };
  }
}
