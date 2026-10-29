import { conversationKind } from './conversation-id.js';
import { applyReactions, queueReaction, pendingReactions, clearPendingReactions } from './message-reactions.js';
import { messageActions } from './message-actions.js';
import { rawAtOriginalTime, boundedEditTime } from './message-edits.js';
import { temporaryMessageText, restrictedStoredMessage, restrictedPlaceholder } from './connectors/message-retention.js';
import { pollDisplay, invalidPollText } from './poll-display.js';
import { GroupPhotoCache } from './group-photos.js';
import { randomUUID, randomBytes } from 'node:crypto';
import { Store, type User } from './store.js';
import { MockConnector } from './connectors/mock.js';
import { BaileysConnector } from './connectors/baileys.js';
import { HistoryTracker } from './history.js';
import type {
  Connector,
  ConnectorHooks,
  ConnectionState,
  Incoming,
  StoredMessage,
  Capabilities,
} from './connectors/types.js';
export class AppError extends Error {
  constructor(
    public status: number,
    public code: string,
  ) {
    super(code);
  }
}
export const deny = () => new AppError(404, 'NOT_FOUND');
export interface PublicMessage extends Omit<StoredMessage, 'raw' | 'replyId' | 'reactions' | 'reactionTimes' | 'editTime' | 'originalTimeKnown'> {
  reply?: { id: string; text: string; sender: string; available?: boolean };
  reactions: { emoji: string; count: number; mine: boolean }[];
  actions?: {reply:boolean;reactions:boolean;edit:boolean;delete:boolean};
}
export class ChatService {
  readonly histories: HistoryTracker;
  private photos = new GroupPhotoCache();
  connectors = new Map<string, Connector>();
  states = new Map<string, { state: ConnectionState; qr?: string }>();
  listeners = new Set<(family: string, group: string | undefined, type: string) => void>();
  private connectionOps = new Set<string>();
  private generations = new Map<string, number>();
  private historyCalls = new WeakMap<Connector,number>();
  private ingestionBudgets = new Map<string,{active:number}>();
  private historyRates = new Map<string,number[]>();
  constructor(
    readonly store: Store,
    readonly factory?: (h: ConnectorHooks) => Connector,
  ) {
    this.histories = new HistoryTracker(store);
    for (const r of store.all('SELECT family_id,state FROM family_connections'))
      this.states.set(r.family_id, { state: r.state === 'connected' ? 'reconnecting' : r.state });
  }
  notify(f: string, g: string | undefined, type = 'refresh') {
    for (const fn of this.listeners) fn(f, g, type);
  }
  async boot() {
    for (const row of this.store.all('SELECT id FROM families WHERE linked=1'))
      await this.connect(row.id);
  }
  requireGroup(user: User, g: string) {
    if (!this.store.allowed(user, g)) throw deny();
  }
  requirePhotoGroup(user: User, g: string) {
    this.requireGroup(user, g);
  }
  async groupPhoto(user: User, g: string) {
    this.requirePhotoGroup(user, g);
    const c = this.connectors.get(user.familyId);
    if (!c?.groupPhoto || this.states.get(user.familyId)?.state !== 'connected') return;
    const photo = await this.photos.get(c, JSON.stringify([user.familyId, g]), () => {
      // A queued request may outlive its original grant or connection.
      this.requirePhotoGroup(user, g);
      if (this.connectors.get(user.familyId) !== c || this.states.get(user.familyId)?.state !== 'connected')
        return Promise.resolve(undefined);
      return c.groupPhoto!(g);
    });
    this.requirePhotoGroup(user, g);
    return this.connectors.get(user.familyId) === c && this.states.get(user.familyId)?.state === 'connected' ? photo : undefined;
  }
  connector(f: string) {
    const c = this.connectors.get(f);
    if (!c) throw new AppError(409, 'NOT_CONNECTED');
    return c;
  }
  async connect(f: string) {
    if (this.connectionOps.has(f)) throw new AppError(409, 'CONNECTION_BUSY');
    this.connectionOps.add(f);
    try { await this.connectFamily(f); } finally { this.connectionOps.delete(f); }
  }
  private async connectFamily(f: string) {
    if (this.store.one('SELECT value FROM instance_metadata WHERE key=?', 'disconnect-history:' + f)?.value === 'deleting')
      throw new AppError(409, 'DELETION_INCOMPLETE');
    const resetAuth = this.states.get(f)?.state === 'needs_pairing';
    if (this.connectors.has(f)) {
      const state = this.states.get(f)?.state;
      if (state === 'needs_pairing' || state === 'conflict' || state === 'idle') {
        this.photos.clear(this.connectors.get(f)!);
        await this.connectors.get(f)!.stop();
        this.connectors.delete(f);
      } else return;
    }
    const generation = (this.generations.get(f) || 0) + 1;
    this.generations.set(f, generation);
    const active = () => this.generations.get(f) === generation;
    this.store.run('DELETE FROM instance_metadata WHERE key=?', 'disconnect:' + f);
    this.store.run('DELETE FROM instance_metadata WHERE key=?', 'disconnect-history:' + f);
    const hooks: ConnectorHooks = {
      discoverGroups: chats => {
        if (!active()) return;
        this.reconcileGroups(f, chats.filter(c => conversationKind(c.id) === 'group'));
      },
      discoverDirect: chats => {
        if (!active()) return;
        const safe = chats.filter(c => conversationKind(c.id) === 'direct');
        if (!safe.length) return;
        const existing = new Map(this.store.groups(f).map(c => [c.id, c.title]));
        this.store.syncGroups(f, safe.map(c => ({...c, title: c.title === 'שיחה פרטית' ? existing.get(c.id) || c.title : c.title})));
        this.notify(f, undefined, 'groups');
      },
      historyEmpty:id=>{if(active())for(const g of this.histories.empty(f,id))if(this.store.retained(f,g))this.notify(f,g);},
      retentionToken: g => active() && this.store.retained(f,g) ? this.store.groupKey(f,g).toString('hex') : undefined,
      history: notice => {
        if (!active() || !this.store.retained(f,notice.groupId)) return;
        this.histories.notice(f,notice); this.notify(f,notice.groupId);
      },
      maxMedia: this.store.config.maxMedia,
      allowed: (g) => active() && this.store.retained(f, g),
      incoming: async m => {
        const permitted = () => active() && (!m.guard || m.guard());
        if (!permitted()) return;
        const existed = this.store.getMessage(f,m.groupId,m.id);
        await this.ingest(f,m,permitted);
        if (m.history && permitted() && !existed && this.store.getMessage(f,m.groupId,m.id))
          this.histories.stored(f,m.groupId,m.history.requestId);
      },
      state: (state, qr) => {
        if (!active()) return;
        if(['connecting','reconnecting','needs_pairing','conflict'].includes(state))this.histories.forget(f);
        this.states.set(f, { state, qr });
        this.store.run(
          'INSERT INTO family_connections VALUES(?,?,?) ON CONFLICT(family_id) DO UPDATE SET state=excluded.state,changed=excluded.changed',
          f,
          state,
          Date.now(),
        );
        if (['reconnecting', 'needs_pairing', 'conflict'].includes(state))
          this.store.diagnostics.record(f, 'connector', 0, { code: 'CONNECTION_CHANGED' });
        if (state === 'needs_pairing') {
          this.store.run('DELETE FROM auth WHERE family_id=?', f);
          this.store.run('UPDATE families SET linked=0 WHERE id=?', f);
        }
        this.notify(f, undefined, 'connection');
      },
      update: (g, id, patch) => { if (active()) this.update(f, g, id, patch); },
      typing: (g, active) => {
        if (this.generations.get(f) === generation && this.store.retained(f, g)) this.notify(f, g, active ? 'typing' : 'paused');
      },
      authGet: (key) => active() ? this.store.authGet(f, key) : undefined,
      authSet: (key, v) => { if (active()) this.store.authSet(f, key, v); },
      authBatch: (data) =>
        this.store.tx(() => {
          if (!active()) return;
          for (const [k, v] of Object.entries(data)) this.store.authSet(f, k, v);
        }, f),
      getMessage: (g, id) => active() ? this.store.getMessage(f, g, id) : undefined,
    };
    if (resetAuth) {
      this.store.run('DELETE FROM auth WHERE family_id=?', f);
    }
    let ingestionBudget=this.ingestionBudgets.get(f);
    if(!ingestionBudget){ingestionBudget={active:0};this.ingestionBudgets.set(f,ingestionBudget);}
    const c =
      this.factory?.(hooks) ||
      (this.store.config.connector === 'mock'
        ? new MockConnector(hooks)
        : new BaileysConnector(hooks,undefined,ingestionBudget));
    this.connectors.set(f, c);
    this.store.run('UPDATE families SET linked=1 WHERE id=?', f);
    try {
      await this.store.diagnostics.measureAsync(f, 'connector', () => c.start());
      if (this.states.get(f)?.state === 'connected') await this.syncGroups(f);
    } catch {
      this.states.set(f, { state: 'needs_pairing' });
      this.notify(f, undefined, 'connection');
      throw new AppError(502, 'CONNECT_FAILED');
    }
  }
  async syncGroups(f: string) {
    const connector = this.connector(f), generation = this.generations.get(f);
    const groups = await connector.groups();
    // A refresh that finishes after unlink must not revoke retained groups.
    if (this.connectors.get(f) !== connector || this.generations.get(f) !== generation) return;
    this.reconcileGroups(f, groups);
  }
  private reconcileGroups(f: string, groups: import('./connectors/types.js').ChatGroup[]) {
    this.store.syncGroups(f, groups);
    const ids = new Set(groups.map((g) => g.id));
    for (const g of this.store.groups(f))
      if (conversationKind(g.id) === 'group' && !ids.has(g.id)) {
        for (const u of this.store.all(
          'SELECT user_id FROM grants WHERE family_id=? AND group_id=?',
          f,
          g.id,
        ))
          this.store.grant(f, u.user_id, g.id, false);
        // Membership loss, not child-grant removal, ends family retention.
        this.store.purgeGroup(f, g.id);
        this.store.run('DELETE FROM groups WHERE family_id=? AND id=?', f, g.id);
        this.histories.forget(f, g.id);
        this.notify(f, g.id, 'revoked');
      }
    this.notify(f, undefined, 'groups');
  }
  async grant(f: string, u: string, g: string, on: boolean) {
    this.store.grant(f, u, g, on);
    const photoConnector = this.connectors.get(f);
    if (photoConnector) this.photos.clear(photoConnector);
    this.notify(f, g, 'revoked');
    if (on) await this.connectors.get(f)?.approved?.(g);
    if (!this.store.retained(f,g)) this.histories.forget(f,g);
  }
  historyStatus(f: string,g: string) {
    const c=this.connectors.get(f);
    let reason = !c || this.states.get(f)?.state !== 'connected' ? 'offline' :
      !c.capabilities.historyRequest || !c.historyAnchor ? 'unsupported' : undefined;
    const anchor = !reason ? this.histories.anchor(f,g,m=>c!.historyAnchor!(g,m)) : undefined;
    if (!reason && !anchor?.message) reason = anchor?.limited ? 'anchor_scan_limit' : 'no_anchor';
    return this.histories.view(f,g,reason);
  }
  async requestHistory(f: string,g: string) {
    if (!this.store.retained(f,g)) throw deny();
    const status=this.historyStatus(f,g);
    if (!status.requestable) return status;
    const c=this.connector(f), generation=this.generations.get(f), key=this.store.groupKey(f,g).toString('hex');
    if((this.historyCalls.get(c)||0)>=2)throw new AppError(429,'HISTORY_WAIT');
    const anchor=this.histories.anchor(f,g,m=>c.historyAnchor!(g,m)).message;
    if (!anchor) return this.historyStatus(f,g);
    const recent=(this.historyRates.get(f)||[]).filter(time=>time>Date.now()-60000);
    if(recent.length>=6)throw new AppError(429,'HISTORY_WAIT');
    let request: ReturnType<HistoryTracker['begin']>;
    try { request=this.histories.begin(f,g); } catch { throw new AppError(429,'HISTORY_WAIT'); }
    if (!request) return this.historyStatus(f,g);
    this.historyRates.set(f,[...recent,Date.now()]);
    const {state,attempt}=request;
    let timer: ReturnType<typeof setTimeout> | undefined;
    try {
      this.historyCalls.set(c,(this.historyCalls.get(c)||0)+1);
      const operation=Promise.resolve().then(()=>c.history(g,anchor)).finally(()=>this.historyCalls.set(c,Math.max(0,(this.historyCalls.get(c)||1)-1)));
      const id=await Promise.race([operation,new Promise<never>((_,reject)=>{timer=setTimeout(()=>reject(new Error('timeout')),10000);})]);
      if (generation!==this.generations.get(f) || !this.store.retained(f,g) || this.store.groupKey(f,g).toString('hex')!==key) throw deny();
      if (state.attempt===attempt && state.phase==='requesting') {
        if (typeof id!=='string' || !id || id.length>256) throw new Error('request');
        state.providerId=id; state.phase='waiting'; state.deadline=Date.now()+45000;
        const early=state.early?.find(x=>x.id===id);
        if(early){state.correlated=true;state.requestStored=early.stored;if(early.ended){state.phase='partial';state.deadline=undefined;state.attempt=undefined;}}
        state.early=undefined;
      }
    } catch (error) {
      if (state.attempt===attempt) {state.phase=(error as Error).message==='timeout'?'timeout':'failed';state.incomplete=true;state.deadline=undefined;state.attempt=undefined;}
      if (error instanceof AppError) throw error;
    } finally {clearTimeout(timer);}
    this.notify(f,g);
    return this.historyStatus(f,g);
  }
  async ingest(f: string, input: Incoming, active: () => boolean = () => true) {
    const g = input.groupId;
    if (!active() || !this.store.retained(f, g)) return;
    if(input.kind==='poll' && !input.restricted){
      const display=pollDisplay(input.text,input.poll?.options);
      input={...input,kind:display?'poll':'unsupported',text:display?.text||invalidPollText,poll:display?.poll,
        unsupportedReason:display?undefined:'poll-invalid',raw:undefined,bytes:undefined,download:undefined,
        mime:undefined,filename:undefined,replyId:undefined};
    }
    if (input.reaction && !input.restricted) {
      const target = this.store.getMessage(f, g, input.reaction.target);
      const event={...input.reaction,time:input.reaction.time??input.time};
      if (!target) { queueReaction(this.store,f,g,event); return; }
      if (target.deleted || target.restricted) return;
      applyReactions(target,[event]);
      this.store.putMessage(f,target,false);this.notify(f,g);
      return;
    }
    const existing = this.store.getMessage(f, g, input.id);
    if (existing?.deleted) return;
    if (input.restricted) {
      clearPendingReactions(this.store,f,g,input.id);
      if (existing?.mediaId) this.store.deleteMedia(f, g, existing.mediaId);
      this.store.putMessage(f, { id: input.id, groupId: g, sender: existing?.sender || input.sender.slice(0,80),
        fromMe: existing?.fromMe ?? input.fromMe, time: existing?.time ?? Date.now(),
        kind: 'unsupported', text: temporaryMessageText,
        restricted: true, status: existing?.status || input.status || 'unknown', reactions: {} }, !input.history);
      this.notify(f,g);
      return;
    }
    if (input.deleted) {
      this.update(f, g, input.id, { deleted: true, text: '' });
      return;
    }
    // Never recover content that a prior provider envelope marked temporary.
    if (existing?.restricted) return;
    if (input.edited && existing) {
      const editTime=boundedEditTime(input.editTime??input.time);
      if(editTime<(existing.editTime??0))return;
      if(input.kind==='unsupported' || input.kind==='poll' || existing.kind==='poll'){
        if(existing.mediaId)this.store.deleteMedia(f,g,existing.mediaId);
        const mismatched=existing.kind==='poll' && input.kind!=='poll' && input.kind!=='unsupported';
        this.store.putMessage(f,{...existing,kind:input.kind==='poll'?'poll':'unsupported',
          text:mismatched?'עדכון לסקר בפורמט שלא אומת אינו מוצג כאן. אפשר לפתוח אותו ב־WhatsApp.':input.text.slice(0,65536),
          poll:input.kind==='poll'?input.poll:undefined,raw:undefined,mediaId:undefined,
          replyId:undefined,mime:undefined,filename:undefined,unsupportedReason:mismatched?'poll-variant':input.unsupportedReason,edited:true,editTime},false);
        this.notify(f,g);return;
      }
      this.update(f, g, input.id, { text: input.text.slice(0,65536), edited: true, editTime,
        raw: rawAtOriginalTime(input.raw,existing.time,existing.originalTimeKnown===false?undefined:existing.raw) });
      return;
    }
    if (existing?.edited && !input.edited) {
      if(input.historicalReactions?.length)applyReactions(existing,input.historicalReactions);
      // An edit can arrive before its original. Recover original ordering and
      // sender metadata without replacing the edited content with old history.
      if ((input.raw || input.kind==='poll') && Number.isFinite(input.time) && input.time <= existing.time) {
        this.store.putMessage(f,{...existing,time:Math.max(0,input.time),sender:input.sender.slice(0,80),
          raw:rawAtOriginalTime(existing.raw,input.time,input.raw),originalTimeKnown:true},false);
        this.notify(f,g);
      }
      return;
    }
    const repairMissingMedia=existing&&!existing.mediaId&&['sticker','image','video','audio','document'].includes(input.kind)&&
      (existing.kind===input.kind||(existing.kind==='text'&&!existing.text))&&!!(input.bytes||input.download);
    const explainLegacyBlank=existing?.kind==='text'&&!existing.text&&input.kind==='unsupported'&&!!input.text;
    const explainUnsupported=existing?.kind==='unsupported'&&input.kind==='unsupported'&&!!input.unsupportedReason&&existing.unsupportedReason!==input.unsupportedReason;
    const upgradePoll=existing?.kind==='unsupported'&&input.kind==='poll'&&(!existing.unsupportedReason||existing.unsupportedReason==='poll');
    if (existing && input.historicalReactions?.length) {
      applyReactions(existing,input.historicalReactions);
      this.store.putMessage(f,existing,false);
      this.notify(f,g);
    }
    if (
      existing &&
      !repairMissingMedia && !explainLegacyBlank && !explainUnsupported && !upgradePoll &&
      existing.status !== 'pending' &&
      existing.status !== 'failed' &&
      (!input.fromMe || !!existing.raw || existing.kind==='poll')
    )
      return;
    const key = this.store.groupKey(f, g).toString('hex');
    let bytes = input.bytes;
    if (!existing?.mediaId && !bytes && input.download) {
      try {
        bytes = await this.store.diagnostics.measureAsync(f, 'media_download', () => input.download!());
      } catch {
        /* Metadata remains, unavailable media is honest. */
      }
    }
    if (!active() || !this.store.retained(f, g) || this.store.groupKey(f, g).toString('hex') !== key) return;
    const current=this.store.getMessage(f,g,input.id);
    if(current?.deleted || current?.restricted || current?.edited && !input.edited)return;
    const previous=current||existing;
    const m: StoredMessage = {
      id: input.id,
      groupId: g,
      sender: input.sender.slice(0, 80),
      fromMe: input.fromMe,
      time: previous?.time ?? (Number.isFinite(input.time)
        ? Math.max(0, Math.min(input.time, Date.now() + 300000))
        : Date.now()),
      kind: previous?.mediaId ? previous.kind : input.kind,
      poll:input.kind==='poll'?input.poll:undefined,
      unsupportedReason: input.unsupportedReason,
      text:
        input.text.length > 65536
          ? input.text.slice(0, 65536) + '\n[ההודעה קוצרה בגלל מגבלת גודל]'
          : input.text,
      status:
        previous &&
        ['sent', 'delivered', 'read'].indexOf(previous.status) >
          ['sent', 'delivered', 'read'].indexOf(input.status || 'unknown')
          ? previous.status
          : input.status || 'unknown',
      reactions: previous?.reactions || {},
      reactionTimes: previous?.reactionTimes,
      raw: input.kind === 'unsupported' || input.kind==='poll' ? undefined : input.raw || previous?.raw,
      clientId: previous?.clientId,
      authorUserId: previous?.authorUserId,
      authorLabel: previous?.authorLabel,
      edited: input.edited,
      editTime: input.edited ? boundedEditTime(input.editTime??input.time) : previous?.editTime,
      originalTimeKnown: input.edited ? previous?.originalTimeKnown ?? !!previous : previous?.originalTimeKnown,
      replyId: input.replyId && input.replyId.length<=180 && input.replyId!==input.id ? input.replyId : undefined,
      mediaId: previous?.mediaId,
      mime: previous?.mediaId ? previous.mime : input.mime,
      filename: input.filename?.replace(/[\r\n\\/]/g, '_').slice(0, 160) || previous?.filename,
    };
    applyReactions(m,[...pendingReactions(this.store,f,g,m.id),...(input.historicalReactions||[])]);
    this.store.putMessage(f, m, !input.history);
    clearPendingReactions(this.store,f,g,m.id);
    if (!m.mediaId && bytes && bytes.length <= this.store.config.maxMedia) {
      const mediaId = randomUUID();
      try {
        const { fileTypeFromBuffer } = await import('file-type');
        if (!active()) return;
        const type = await fileTypeFromBuffer(bytes);
        if (!active() || !this.store.retained(f, g) || this.store.groupKey(f, g).toString('hex') !== key) return;
        const safe = [
          'image/jpeg',
          'image/png',
          'image/webp',
          'image/gif',
          'video/mp4',
          'audio/ogg',
          'audio/mpeg',
          'audio/mp4',
          'audio/webm',
          'video/webm',
        ];
        const latest=this.store.getMessage(f,g,m.id);
        if (!latest || latest.deleted || latest.restricted || latest.mediaId || latest.kind!==input.kind)return;
        Object.assign(m,latest);
        m.mime=(type?.mime || 'application/octet-stream').split(';')[0];
        if(m.kind==='sticker'&&m.mime!=='image/webp'){
          m.kind='unsupported';m.text='המדבקה אינה בקובץ WebP נתמך — אפשר לפתוח אותה ב־WhatsApp.';m.raw=undefined;m.mime=undefined;
          this.store.putMessage(f,m);this.notify(f,g);return;
        }
        if (m.kind !== 'document' && !safe.includes(m.mime)) {
          m.kind = 'document';
          m.filename = m.filename || 'attachment';
        }
        this.store.putMedia(f, g, mediaId, m.id, bytes);
        m.mediaId = mediaId;
        this.store.putMessage(f, m);
      } catch {
        /* Full store or unavailable media must not fail text sync. */
      }
    }
    this.notify(f, g);
  }
  update(f: string, g: string, id: string, patch: Partial<StoredMessage>) {
    let m = this.store.getMessage(f, g, id);
    if (!m) {
      if (!patch.deleted || !this.store.retained(f,g)) return;
      m={id,groupId:g,sender:'',fromMe:false,time:Date.now(),kind:'text',text:'',status:'unknown',reactions:{}};
    }
    const allowed: Partial<StoredMessage> = {};
    for (const k of ['text', 'edited', 'editTime', 'deleted', 'status', 'reactions', 'raw'] as const)
      if (patch[k] !== undefined && (!m.restricted || k === 'deleted' || k === 'status'))
        (allowed as any)[k] = patch[k];
    const ranks: Record<string, number> = {
      unknown: -1,
      failed: -1,
      pending: 0,
      sent: 1,
      delivered: 2,
      read: 3,
    };
    if (
      allowed.status &&
      ((allowed.status === 'failed' && ranks[m.status] >= 1) ||
        (allowed.status !== 'failed' && ranks[allowed.status] < ranks[m.status]))
    )
      delete allowed.status;
    Object.assign(m, allowed);
    if (m.deleted) {
      if (m.mediaId) this.store.deleteMedia(f, g, m.mediaId);
      m.text = '';
      m.poll = undefined;
      m.raw = undefined;
      m.mediaId = undefined;
      m.reactions = {};
      m.reactionTimes = undefined;
      m.replyId = undefined;
      clearPendingReactions(this.store,f,g,id);
    }
    this.store.putMessage(f, m);
    this.notify(f, g);
  }
  public(f: string, m: StoredMessage): PublicMessage {
    if(!m.deleted && restrictedStoredMessage(m))m=restrictedPlaceholder(m);
    const { raw, replyId, reactions, reactionTimes, editTime, originalTimeKnown, poll, ...safe } = m;
    const display=!m.deleted && !m.restricted && m.kind==='poll'?pollDisplay(m.text,poll?.options):undefined;
    const target = replyId ? this.store.getMessage(f, m.groupId, replyId) : undefined;
    const aggregated = new Map<string, { emoji: string; count: number; mine: boolean }>();
    for (const [actor, emoji] of Object.entries(reactions)) {
      const r = aggregated.get(emoji) || { emoji, count: 0, mine: false };
      r.count++;
      r.mine ||= actor === 'self';
      aggregated.set(emoji, r);
    }
    return {
      ...safe,
      ...(m.kind==='poll'&&!m.deleted&&!m.restricted ? display ? {poll:display.poll} : {kind:'unsupported' as const,text:invalidPollText,unsupportedReason:'poll-invalid' as const} : {}),
      actions:messageActions(m,this.connectors.get(f)),
      reactions: [...aggregated.values()],
      ...(replyId
        ? {
            reply: {
              id: replyId,
              text: target && !target.deleted && !target.restricted ? target.text.slice(0,100) || 'קובץ מצורף' : 'ההודעה המקורית אינה זמינה',
              sender: target && !target.deleted && !target.restricted ? target.sender : '',
              available: !!target && !target.deleted && !target.restricted,
            },
          }
        : {}),
    };
  }
  async disconnect(f: string, erase: boolean) {
    if (this.connectionOps.has(f)) throw new AppError(409, 'CONNECTION_BUSY');
    this.connectionOps.add(f);
    try {
    const c = this.connectors.get(f);
    if (c) this.photos.clear(c);
    this.histories.forget(f);
    const previousResult = this.store.one('SELECT value FROM instance_metadata WHERE key=?', 'disconnect:' + f)?.value;
    const eraseLocal = () => {
      this.store.run('INSERT OR REPLACE INTO instance_metadata VALUES(?,?)', 'disconnect-history:' + f, 'deleting');
      this.store.eraseChats(f);
      this.store.run('INSERT OR REPLACE INTO instance_metadata VALUES(?,?)', 'disconnect-history:' + f, 'deleted');
    };
    if (!c && !this.store.one('SELECT linked FROM families WHERE id=?', f)?.linked && previousResult) {
      if (erase) { eraseLocal(); this.notify(f, undefined, 'reset'); }
      return { remoteRevoked: previousResult === 'confirmed', history: erase ? 'deleted' as const : 'retained' as const };
    }
    this.generations.set(f, (this.generations.get(f) || 0) + 1);
    this.connectors.delete(f);
    // Commit local revocation before waiting for the network: a crash/reboot
    // must never auto-reconnect an intentionally disconnected family.
    this.store.tx(() => {
      this.store.run('DELETE FROM auth WHERE family_id=?', f);
      this.store.run('UPDATE families SET linked=0 WHERE id=?', f);
      this.store.run('INSERT INTO family_connections VALUES(?,?,?) ON CONFLICT(family_id) DO UPDATE SET state=excluded.state,changed=excluded.changed', f, 'idle', Date.now());
      this.store.run('INSERT OR REPLACE INTO instance_metadata VALUES(?,?)', 'disconnect:' + f, 'unconfirmed');
      this.store.run('INSERT OR REPLACE INTO instance_metadata VALUES(?,?)', 'disconnect-history:' + f, erase ? 'deleting' : 'retained');
    });
    this.states.set(f, { state: 'idle' });
    this.notify(f, undefined, 'reset');
    let remoteRevoked = false;
    // Start stopping the socket immediately, but complete explicit local deletion
    // before waiting on remote logout. Interrupted deletion stays visibly pending.
    const stopped = Promise.resolve(c?.stop(true)).catch(() => undefined);
    if (erase) eraseLocal();
    remoteRevoked = (await stopped)?.remoteRevoked === true;
    this.store.run('INSERT OR REPLACE INTO instance_metadata VALUES(?,?)', 'disconnect:' + f,
      this.store.config.connector === 'mock' ? 'mock' : remoteRevoked ? 'confirmed' : 'unconfirmed');
    this.notify(f, undefined, 'connection');
    return { remoteRevoked, history: erase ? 'deleted' as const : 'retained' as const };
    } finally { this.connectionOps.delete(f); }
  }
  async close() {
    for (const [family, connector] of this.connectors) {
      this.photos.clear(connector);
      const previous = this.states.get(family)?.state;
      await connector.stop();
      if (previous === 'needs_pairing' || previous === 'conflict') {
        this.states.set(family, { state: previous });
        this.store.run(
          'UPDATE family_connections SET state=?,changed=? WHERE family_id=?',
          previous,
          Date.now(),
          family,
        );
      }
    }
  }
}
