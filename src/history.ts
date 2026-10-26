import type { HistoryNotice, StoredMessage } from './connectors/types.js';
import type { Store } from './store.js';
export type HistoryPhase = 'partial' | 'requesting' | 'waiting' | 'receiving' | 'timeout' | 'failed' | 'overloaded';
type State = { phase: HistoryPhase; received: number; processed: number; stored: number; requestStored: number;
  correlated: boolean; incomplete: boolean; startedAt?: number; deadline?: number; providerId?: string; attempt?: symbol;
  early?: {id:string;stored:number;ended:boolean}[]; processing?:Set<string> };

/** Bounded, content-free operational state. Lost/restarted state is always partial,
 * never proof of completion. Provider request IDs are never exposed to clients.
 */
export class HistoryTracker {
  private entries = new Map<string, State>();
  constructor(private store: Store, private now = () => Date.now()) {}
  private key(f: string, g: string) { return JSON.stringify([f,g]); }
  private expire(value:State) {
    if (!value.processing?.size && value.deadline && this.now()>value.deadline && ['requesting','waiting','receiving'].includes(value.phase)) {
      value.phase='timeout';value.incomplete=true;value.deadline=undefined;value.attempt=undefined;
    }
  }
  state(f: string, g: string) {
    const key = this.key(f,g);
    let value = this.entries.get(key);
    if (!value) {
      if (this.entries.size >= 256) {
        for(const state of this.entries.values())this.expire(state);
        const removable = [...this.entries].find(([,v]) => !v.processing?.size&&!['requesting','waiting','receiving'].includes(v.phase));
        // Never throw through the provider EventEmitter. A non-resident group
        // reports overload and authoritative DB counts until a slot is free.
        if (!removable) return {phase:'overloaded',received:0,processed:0,stored:0,requestStored:0,correlated:false,incomplete:true,capacity:true} as State & {capacity:true};
        this.entries.delete(removable[0]);
      }
      value = {phase:'partial',received:0,processed:0,stored:0,requestStored:0,correlated:false,incomplete:false};
      this.entries.set(key,value);
    }
    this.expire(value);
    return value;
  }
  forget(f: string, g?: string) {
    for (const key of this.entries.keys()) { const [family,group] = JSON.parse(key); if (family===f && (!g || group===g)) this.entries.delete(key); }
  }
  notice(f: string, notice: HistoryNotice) {
    const s = this.state(f,notice.groupId);
    if(notice.history&&notice.batchId&&(notice.admitted||0)>0&&!notice.final){s.processing||=new Set();s.processing.add(notice.batchId);}
    if(notice.finished&&notice.batchId)s.processing?.delete(notice.batchId);
    const matched = !!s.providerId && s.providerId === notice.requestId;
    if (s.phase==='requesting' && s.attempt && notice.history && notice.requestId) {
      s.early ||= [];
      let event=s.early.find(x=>x.id===notice.requestId);
      if (!event && s.early.length<8) {event={id:notice.requestId,stored:0,ended:false};s.early.push(event);}
      if(event && notice.final) event.ended=true;
    }
    if (!notice.final) s.received += notice.received;
    if (notice.final) s.processed += notice.processed;
    if (notice.phase==='overload' || notice.phase==='error') {
      s.phase=notice.phase==='overload'?'overloaded':'failed'; s.incomplete=true; s.deadline=undefined; s.attempt=undefined;
    } else if (notice.history && matched && s.attempt && ['waiting','receiving'].includes(s.phase)) {
      s.correlated=true; s.phase=notice.phase==='end'?'partial':'receiving';
      if(notice.phase==='start')s.deadline=undefined;
      if (notice.phase==='end') { s.deadline=undefined; s.attempt=undefined; }
    } else if (notice.history && !s.attempt && !['overloaded','failed','timeout'].includes(s.phase)) {
      s.phase=notice.final?'partial':'receiving'; s.deadline=notice.final?undefined:this.now()+45000;
    }
  }
  stored(f: string,g: string, requestId?: string) {
    const s=this.state(f,g); s.stored++;
    if (s.providerId && s.providerId===requestId) { s.requestStored++; s.correlated=true; }
    else if(s.phase==='requesting' && s.attempt && requestId) {
      const event=s.early?.find(x=>x.id===requestId); if(event)event.stored++;
    }
  }
  empty(f:string,id:string){
    const groups:string[]=[];
    for(const [key,s] of this.entries){const [family,g]=JSON.parse(key);if(family!==f||!s.attempt)continue;
      if(s.providerId===id){s.correlated=true;s.phase='partial';s.deadline=undefined;s.attempt=undefined;groups.push(g);}
      else if(s.phase==='requesting'){s.early||=[];if(!s.early.some(x=>x.id===id)&&s.early.length<8)s.early.push({id,stored:0,ended:true});}
    }
    return groups;
  }
  anchor(f: string,g: string, valid: (m: StoredMessage)=>boolean) {
    const rows=this.store.all('SELECT id FROM messages WHERE family_id=? AND group_id=? ORDER BY time,id LIMIT 2049',f,g);
    for (const row of rows.slice(0,2048)) { const m=this.store.getMessage(f,g,row.id); if (m && valid(m)) return {message:m,limited:false}; }
    return {message:undefined,limited:rows.length>2048};
  }
  view(f: string,g: string, reason?: string) {
    const s=this.state(f,g);
    if('capacity' in s)reason='capacity';
    return {phase:s.phase,received:s.received,processed:s.processed,stored:s.stored,requestStored:s.requestStored,
      correlated:s.correlated,incomplete:s.incomplete,complete:false as const,reason:reason||null,
      processing:!!s.processing?.size,
      requestable:!reason && !s.processing?.size && !['requesting','waiting','receiving'].includes(s.phase),
      totalStored:this.store.one('SELECT count(*) AS n FROM messages WHERE family_id=? AND group_id=?',f,g).n};
  }
  begin(f: string,g: string) {
    const s=this.state(f,g);
    if(s.processing?.size)return;
    if('capacity' in s)throw new Error('History capacity');
    if (['requesting','waiting','receiving'].includes(s.phase)) return;
    if (s.startedAt !== undefined && this.now()-s.startedAt<15000) throw new Error('History cooldown');
    const running=[...this.entries].filter(([key,v])=>JSON.parse(key)[0]===f && v.deadline && v.deadline>this.now()).length;
    if (running>=2) throw new Error('History capacity');
    s.attempt=Symbol(); s.phase='requesting'; s.startedAt=this.now(); s.deadline=this.now()+10000;
    s.providerId=undefined; s.requestStored=0; s.correlated=false; s.early=[];
    return {state:s,attempt:s.attempt};
  }
}
