import { conversationKind } from '../conversation-id.js';
import { fetchGroupPhoto } from '../group-photos.js';
import { IngestionQueue } from './ingestion.js';
import { restrictedContent } from './message-retention.js';
import { editedEnvelope, historicalReactions, reactionActor, reactionTime } from './message-updates.js';
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
  if (!groupId || !conversationKind(groupId) || !id) return;
  const content = normalizeMessageContent(raw.message);
  if (!content) return;
  const restricted = !!raw.key.isViewOnce || restrictedContent(raw.message);
  const base: Incoming = {
    id,
    groupId,
    sender: raw.key.fromMe ? 'אני' : (raw.pushName || 'משתתף/ת').slice(0, 80),
    fromMe: !!raw.key.fromMe,
    time: Number(raw.messageTimestamp || Date.now() / 1000) * 1000,
    kind: 'unsupported',
    text: 'סוג הודעה זה עדיין אינו נתמך',
    status: receipt(raw.status),
    edited: editedEnvelope(raw.message) || undefined,
    historicalReactions: restricted ? undefined : historicalReactions(raw),
    raw: restricted ? undefined : stringify(stripEmbeddedContext(raw)),
  };
  if (restricted) {
    const p = content.protocolMessage;
    if (p?.key?.remoteJid && p.key.remoteJid !== groupId) return;
    return { ...base, restricted: true,
      id: p?.type === proto.Message.ProtocolMessage.Type.MESSAGE_EDIT ? p.key?.id || id : id,
      text: 'הודעה זמנית או לצפייה חד־פעמית — לפתיחה ב־WhatsApp בלבד' };
  }
  if (content.pollCreationMessage || content.pollCreationMessageV2 || content.pollCreationMessageV3)
    return {...base,raw:undefined,unsupportedReason:'poll',text:'סקר ב־WhatsApp — פרטי הסקר, ההצבעה והתוצאות זמינים כרגע ב־WhatsApp בלבד'};
  if (content.pollUpdateMessage)
    return {...base,raw:undefined,unsupportedReason:'poll-vote',text:'עדכון הצבעה בסקר — פרטי ההצבעה זמינים כרגע ב־WhatsApp בלבד'};
  if (content.pollResultSnapshotMessage)
    return {...base,raw:undefined,unsupportedReason:'poll-results',text:'תוצאות סקר — זמינות כרגע ב־WhatsApp בלבד'};
  if (content.reactionMessage) {
    const r = content.reactionMessage;
    if (r.key?.remoteJid && r.key.remoteJid !== groupId) return;
    return {
      ...base,
      kind: 'text',
      text: '',
      reaction: {
        target: r.key?.id || '',
        actor: reactionActor(raw.key,groupId),
        emoji: r.text || '',
        time: reactionTime(r.senderTimestampMs,base.time),
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
  // Resolve only an existing message from THIS group in the application layer. Never copy quoted payload.
  const ctx = body?.contextInfo;
  if (ctx?.stanzaId && (!ctx.remoteJid || ctx.remoteJid === groupId)) base.replyId = ctx.stanzaId;
  return base;
}
export function validateMediaSource(raw: WAMessage) {
  if (raw.key.isViewOnce || restrictedContent(raw.message)) throw new Error('Temporary media unavailable');
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
    polls: {identify:true,create:false,vote:false,results:false},
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
    let membershipReady = false, membershipPromise: Promise<void> | undefined;
    const discoverMembership = () => membershipPromise ||= this.groups().then(() => {
      if (valid()) membershipReady = true;
    }).finally(() => { membershipPromise = undefined; });
    // Register provider-discovered direct chats before admitting their messages.
    // No client API can use this path, and no child grants are created.
    const discover = (chats: {id?: string | null; name?: string | null; conversation?: string | null}[]) => {
      if (!valid()) return;
      const safe = chats.filter(c => c.id && conversationKind(c.id) === 'direct')
        .map(c => ({id: c.id!, title: (c.name || c.conversation || 'שיחה פרטית').slice(0,200), kind: 'direct' as const}));
      if (safe.length) this.hooks.discoverDirect?.(safe);
    };
    sock.ev.on('chats.upsert', chats => discover(chats));
    sock.ev.on('chats.update', chats => discover(chats));
    const contactNames = (contacts: {id: string; name?: string; notify?: string}[]) =>
      discover(contacts.filter(c => this.hooks.allowed(c.id)).map(c => ({id:c.id,name:c.name || c.notify})));
    sock.ev.on('contacts.upsert', contactNames);
    sock.ev.on('contacts.update', contacts => contactNames(contacts.filter(c => typeof c.id === 'string') as {id:string;name?:string;notify?:string}[]));

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
        void discoverMembership().catch(() => {});
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
        if (!valid() || !raw?.key?.remoteJid) continue;
        if (conversationKind(raw.key.remoteJid) === 'direct' && !this.hooks.allowed(raw.key.remoteJid))
          discover([{id:raw.key.remoteJid, name:raw.key.fromMe ? undefined : raw.pushName}]);
        if (!this.hooks.allowed(raw.key.remoteJid)) continue;
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
    let deferredHistory = false;
    const processWithMembership = (messages: WAMessage[], history: boolean, requestId?: string) => {
      if (!valid()) return;
      if (!this.hooks.discoverGroups || !messages.some(m => m.key?.remoteJid && conversationKind(m.key.remoteJid) === 'group' && (!membershipReady || !this.hooks.allowed(m.key.remoteJid))))
        return process(messages, history, requestId);
      // Only one bounded initial-history batch may wait for membership discovery.
      // Never authorize a group merely because old history mentions it.
      const accepted: WAMessage[] = [];
      let bytes = 0;
      for (const m of messages) {
        if (!m.key?.remoteJid || !conversationKind(m.key.remoteJid)) continue;
        const size = Buffer.byteLength(stringify(m));
        if (deferredHistory || accepted.length >= 2048 || bytes + size > 16 * 1024 * 1024) {
          if (m.key?.remoteJid) this.hooks.history?.({groupId:m.key.remoteJid,phase:'overload',received:1,processed:0,history,requestId});
          continue;
        }
        accepted.push(m); bytes += size;
      }
      if (!accepted.length) return;
      deferredHistory = true;
      void discoverMembership().then(() => { if (valid()) process(accepted, history, requestId); })
        .catch(() => { if (valid()) for (const m of accepted) if (m.key?.remoteJid) this.hooks.history?.({groupId:m.key.remoteJid,phase:'error',received:1,processed:0,history,requestId}); })
        .finally(() => { deferredHistory = false; });
    };
    // Baileys append includes offline/replayed history; only explicit notify is
    // live arrival. Retain append without inventing local unread notifications.
    sock.ev.on('messages.upsert', event => { if (!event.requestId) processWithMembership(event.messages, event.type !== 'notify'); });
    sock.ev.on('messaging-history.set', event => {
      discover(event.chats || []);
      contactNames(event.contacts || []);
      const id=typeof event.peerDataRequestSessionId==='string'&&event.peerDataRequestSessionId.length<=256?event.peerDataRequestSessionId:undefined;
      if(valid()&&!event.messages.length&&id)this.hooks.historyEmpty?.(id);
      processWithMembership(event.messages,true,id);
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
              actor: reactionActor(reaction.key,key.remoteJid),
              emoji: reaction.text || '',
              time: reactionTime(reaction.senderTimestampMs,Date.now()),
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
    if (!this.originalAvailable(m)) throw new Error('Original message unavailable');
    return parse(m.raw!).key;
  }
  originalAvailable(m: StoredMessage) {
    if (!m.raw || m.deleted || m.restricted || m.kind==='unsupported') return false;
    try {
      const original=parse(m.raw);
      return !!original.message && original.key?.id===m.id && original.key?.remoteJid===m.groupId;
    } catch { return false; }
  }
  async groups() {
    const sock = this.require(), generation = this.generation;
    const groups = await sock.groupFetchAllParticipating();
    const result = Object.values(groups).filter(g => conversationKind(g.id) === 'group').map(g => ({id:g.id,title:g.subject,kind:'group' as const}));
    if (this.sock !== sock || this.stopping || this.generation !== generation) throw new Error('Stale conversation catalog');
    this.hooks.discoverGroups?.(result);
    return result;
  }
  async groupPhoto(group: string) {
    if (!conversationKind(group)) return;
    const sock = this.require();
    try {
      const url = await sock.profilePictureUrl(group, 'preview', 5000);
      if (!url || this.sock !== sock) return;
      const photo = await fetchGroupPhoto(url);
      return this.sock === sock ? photo : undefined;
    } catch { return; }
  }
  async send(m: Outgoing) {
    if (m.reply && !this.originalAvailable(m.reply)) throw new Error('Original message unavailable');
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
    m.beforeSend?.();
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
