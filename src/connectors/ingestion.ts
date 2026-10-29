import type { Incoming, HistoryNotice } from './types.js';

/** Local consumer backpressure. Provider EventEmitter does not await listeners:
 * admission is therefore bounded and rejected work is explicitly incomplete.
 * Only already-authorized, normalized messages may enter this queue.
 */
export class IngestionQueue {
  private batches: { messages: Incoming[]; bytes: number; notices: HistoryNotice[]; guards: Map<string,()=>boolean> }[] = [];
  private count = 0;
  private bytes = 0;
  private draining = false;
  private idle: (() => void)[] = [];
  private disposed=false;
  private serial=0;
  constructor(private consume: (message: Incoming) => Promise<void>, private allowed: (group: string) => boolean,
    private valid: () => boolean, private report: (notice: HistoryNotice) => void,
    readonly limits = { messages: 2048, bytes: 16 * 1024 * 1024, batches: 8 }, private budget={active:0}) {}

  dispose() {
    if(this.disposed)return;
    this.disposed=true;
    for(const batch of this.batches){
      if(this.valid())for(const notice of batch.notices)
        if(this.allowed(notice.groupId)&&batch.guards.get(notice.groupId)?.())
          this.report({...notice,phase:'error',received:0,final:true,finished:true});
      batch.messages.length=0;
    }
    this.batches=[];this.count=0;this.bytes=0;
    for(const resolve of this.idle.splice(0))resolve();
  }

  submit(input: Iterable<Incoming>, history: boolean, requestId?: string) {
    if(this.disposed)return;
    const messages: Incoming[] = [], totals = new Map<string, HistoryNotice>();
    const guards=new Map<string,()=>boolean>(),batchId=String(++this.serial);
    let bytes = 0;
    for (const message of input) {
      if (!this.valid() || !this.allowed(message.groupId)) continue;
      let notice = totals.get(message.groupId);
      if (!notice) {
        notice = { groupId: message.groupId, phase: 'start', received: 0, processed: 0, history, requestId, batchId,admitted:0 };
        // Bound report metadata too; an oversized event never grows an unbounded map.
        if (totals.size >= 256) { this.report({ ...notice, phase: 'overload', received: 1 }); continue; }
        totals.set(message.groupId, notice);
      }
      notice.received++;
      const size = Buffer.byteLength(message.raw || '') + Buffer.byteLength(message.text) +
        (message.poll ? Buffer.byteLength(JSON.stringify(message.poll)) : 0) + 256;
      if (size > 256 * 1024 || this.count + messages.length >= this.limits.messages ||
          this.bytes + bytes + size > this.limits.bytes || this.batches.length >= this.limits.batches || (this.budget.active>0&&!this.draining)) {
        notice.phase = 'overload'; continue;
      }
      messages.push(message); bytes += size;notice.admitted!++;
      if(!guards.has(message.groupId))guards.set(message.groupId,message.guard||(()=>true));
    }
    for (const notice of totals.values()) this.report({ ...notice });
    if (!messages.length) return;
    this.count += messages.length; this.bytes += bytes;
    this.batches.push({ messages, bytes, notices: [...totals.values()],guards });
    void this.drain();
  }

  async settled() { if (!this.disposed&&this.draining) await new Promise<void>(resolve => this.idle.push(resolve)); }
  private async drain() {
    if (this.draining) return;
    this.draining = true;
    try {
      while (this.batches.length) {
        const batch = this.batches[0];
        for (const message of batch.messages) {
          if (this.valid() && this.allowed(message.groupId) && (!message.guard || message.guard())) {
            const notice = batch.notices.find(n => n.groupId === message.groupId)!;
            const previous=message.guard; let cancelled=false;
            message.guard=()=>!this.disposed&&!cancelled&&(!previous||previous());
            let timer:ReturnType<typeof setTimeout>|undefined;
            this.budget.active++;
            const operation=Promise.resolve().then(async()=>{
              if(this.valid()&&this.allowed(message.groupId)&&message.guard!())await this.consume(message);
            }).finally(()=>this.budget.active--);
            try {
              await Promise.race([operation,new Promise<never>((_,reject)=>{timer=setTimeout(()=>{cancelled=true;reject(new Error('Ingestion timeout'));},30000);})]);
              notice.processed++;
            }
            catch {
              notice.phase = 'error';
              if(this.valid()&&this.allowed(message.groupId)&&(!previous||previous()))
                this.report({...notice,phase:'error',received:0,processed:0,final:true});
              // Do not spawn unbounded orphan consumers when a transport hangs.
              // Capacity stays reserved until the cancelled operation unwinds.
              if(cancelled)await operation.catch(()=>{});
            }
            finally {clearTimeout(timer);}
          }
          if(this.disposed)return;
          this.count--;
        }
        this.bytes -= batch.bytes; this.batches.shift();
        if (this.valid()) for (const notice of batch.notices)
          if (this.allowed(notice.groupId) && batch.guards.get(notice.groupId)?.())
            this.report({ ...notice, final: true, finished:true, phase: notice.phase === 'start' ? 'end' : notice.phase });
      }
    } finally { this.draining = false; for (const resolve of this.idle.splice(0)) resolve(); }
  }
}
