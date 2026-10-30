import type {Store} from '../store.js';
import type {StoredMessage} from '../connectors/types.js';
import type {EncryptedPollVote} from './types.js';
import {hash,open,seal} from '../crypto.js';
import {applyPollVote,validPollVote} from './receive.js';
const ttl=7*24*60*60_000;
const scope=(f:string,g:string,target:string,slot:string)=>`${f}:${g}:poll-vote:${target}:${slot}`;
export function initializePollVotes(store:Store){
 store.db.exec(`CREATE TABLE IF NOT EXISTS pending_poll_votes (
  family_id TEXT NOT NULL,group_id TEXT NOT NULL,target TEXT NOT NULL,slot TEXT NOT NULL,received INTEGER NOT NULL,body BLOB NOT NULL,
  PRIMARY KEY(family_id,group_id,target,slot),FOREIGN KEY(family_id,group_id) REFERENCES group_keys(family_id,group_id) ON DELETE CASCADE)`);
}
export function clearPollVotes(store:Store,f:string,g:string,target:string){
 store.run('DELETE FROM pending_poll_votes WHERE family_id=? AND group_id=? AND target=?',f,g,target);
}
export function acceptPollVote(store:Store,f:string,g:string,event:EncryptedPollVote):boolean{
 if(!store.retained(f,g)||!validPollVote(event))return false;
 const message=store.getMessage(f,g,event.target);
 if(message?.deleted||message?.restricted){clearPollVotes(store,f,g,event.target);return false;}
 if(message?.kind==='poll'&&message.pollState?.secret){
  const outcome={resolved:false};
  if(applyPollVote(message,event,outcome)){store.putMessage(f,message,false);return true;}
  if(outcome.resolved)return false;
  // A later authenticated alias enrichment may make this decryptable.
  // Stale/invalid retries are bounded and never contribute to the count.
 }
 if(message&&message.kind!=='poll'&&message.kind!=='unsupported')return false;
 const safe={target:event.target,id:event.id,time:event.time,voters:[...event.voters],payload:event.payload,iv:event.iv};
 const key=store.groupKey(f,g),slot=hash(key.toString('hex')+':'+event.id);
 // Preserve the first authenticated provider event with this ID; never replace
 // it with replayed ciphertext. Different choices carry different event IDs.
 store.tx(()=>{
  store.run('DELETE FROM pending_poll_votes WHERE family_id=? AND received<=?',f,Date.now()-ttl);
  store.run('INSERT OR IGNORE INTO pending_poll_votes VALUES(?,?,?,?,?,?)',f,g,event.target,slot,Date.now(),seal(key,JSON.stringify(safe),scope(f,g,event.target,slot)));
  store.run(`DELETE FROM pending_poll_votes WHERE rowid IN (SELECT rowid FROM pending_poll_votes
   WHERE family_id=? AND group_id=? ORDER BY received DESC,rowid DESC LIMIT -1 OFFSET 256)`,f,g);
  store.run(`DELETE FROM pending_poll_votes WHERE rowid IN (SELECT rowid FROM pending_poll_votes
   WHERE family_id=? ORDER BY received DESC,rowid DESC LIMIT -1 OFFSET 2048)`,f);
 });
 return false;
}
export function drainPollVotes(store:Store,f:string,g:string,message:StoredMessage):boolean{
 if(!store.retained(f,g)||message.deleted||message.restricted||!message.pollState?.secret)return false;
 const key=store.groupKey(f,g);let changed=false;
 for(const row of store.all('SELECT slot,body FROM pending_poll_votes WHERE family_id=? AND group_id=? AND target=? AND received>? ORDER BY received,rowid',f,g,message.id,Date.now()-ttl)){
  const event:EncryptedPollVote=JSON.parse(open(key,row.body,scope(f,g,message.id,row.slot)).toString());
  const outcome={resolved:false};
  changed=applyPollVote(message,event,outcome)||changed;
  if(outcome.resolved||!validPollVote(event))store.run(
   'DELETE FROM pending_poll_votes WHERE family_id=? AND group_id=? AND target=? AND slot=?',f,g,message.id,row.slot);
 }
 store.run('DELETE FROM pending_poll_votes WHERE family_id=? AND group_id=? AND target=? AND received<=?',f,g,message.id,Date.now()-ttl);
 return changed;
}
