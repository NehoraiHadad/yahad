import type { Store } from './store.js';
import type { ReactionUpdate, StoredMessage } from './connectors/types.js';
import { hash, open, seal } from './crypto.js';

const ttl = 7 * 24 * 60 * 60_000;
/** Additive encrypted state for reactions received before their target.
 * IDs have the same scope as messages; actor identity exists only in ciphertext. */
export function initializePendingReactions(store: Store) {
  store.db.exec(`CREATE TABLE IF NOT EXISTS pending_message_reactions (
    family_id TEXT NOT NULL, group_id TEXT NOT NULL, target TEXT NOT NULL,
    slot TEXT NOT NULL, received INTEGER NOT NULL, body BLOB NOT NULL,
    PRIMARY KEY(family_id,group_id,target,slot),
    FOREIGN KEY(family_id,group_id) REFERENCES groups(family_id,id) ON DELETE CASCADE
  )`);
}
function scope(f: string,g: string,target: string,slot: string) { return `${f}:${g}:reaction:${target}:${slot}`; }
export function pendingReactions(store: Store,f: string,g: string,target: string): ReactionUpdate[] {
  if (!store.retained(f,g)) return [];
  const key=store.groupKey(f,g);
  return store.all('SELECT slot,body FROM pending_message_reactions WHERE family_id=? AND group_id=? AND target=? AND received>?',f,g,target,Date.now()-ttl)
    .map(r=>JSON.parse(open(key,r.body,scope(f,g,target,r.slot)).toString()));
}
export function queueReaction(store: Store,f: string,g: string,event: ReactionUpdate) {
  if (!store.retained(f,g) || !event.target || event.target.length>180 || !event.actor || event.actor.length>256) return;
  const key=store.groupKey(f,g),slot=hash(key.toString('hex')+':'+event.actor);
  const previous=store.one('SELECT body FROM pending_message_reactions WHERE family_id=? AND group_id=? AND target=? AND slot=?',f,g,event.target,slot);
  if (previous && JSON.parse(open(key,previous.body,scope(f,g,event.target,slot)).toString()).time > (event.time||0)) return;
  store.tx(()=>{
    store.run('DELETE FROM pending_message_reactions WHERE family_id=? AND received<=?',f,Date.now()-ttl);
    store.run(`INSERT INTO pending_message_reactions VALUES(?,?,?,?,?,?) ON CONFLICT(family_id,group_id,target,slot)
      DO UPDATE SET received=excluded.received,body=excluded.body`,f,g,event.target,slot,Date.now(),seal(key,JSON.stringify(event),scope(f,g,event.target,slot)));
    // Bound each conversation and the family, including many never-seen targets.
    store.run(`DELETE FROM pending_message_reactions WHERE rowid IN (SELECT rowid FROM pending_message_reactions
      WHERE family_id=? AND group_id=? ORDER BY received DESC,rowid DESC LIMIT -1 OFFSET 256)`,f,g);
    store.run(`DELETE FROM pending_message_reactions WHERE rowid IN (SELECT rowid FROM pending_message_reactions
      WHERE family_id=? ORDER BY received DESC,rowid DESC LIMIT -1 OFFSET 2048)`,f);
  });
}
export function clearPendingReactions(store: Store,f: string,g: string,target: string) {
  store.run('DELETE FROM pending_message_reactions WHERE family_id=? AND group_id=? AND target=?',f,g,target);
}
export function applyReactions(message: StoredMessage,events: ReactionUpdate[]) {
  const reactions={...message.reactions},times={...message.reactionTimes};
  for(const event of events){
    if (!event.actor || event.actor.length>256) continue;
    const time=Number.isFinite(event.time) ? Math.max(0,Math.min(event.time!,Date.now()+300000)) : 0;
    if (time<(times[event.actor]??-1)) continue;
    if(event.emoji)reactions[event.actor]=event.emoji.slice(0,64);else delete reactions[event.actor];
    times[event.actor]=time;
  }
  const actors=Object.keys(times);
  if(actors.length>2048)for(const actor of actors.sort((a,b)=>times[b]-times[a]).slice(2048)){
    delete times[actor];delete reactions[actor];
  }
  message.reactions=reactions;message.reactionTimes=times;
}
