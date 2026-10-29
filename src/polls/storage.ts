import type {Store} from '../store.js';
import type {StoredMessage} from '../connectors/types.js';
import {open,seal} from '../crypto.js';
import type {PollState} from './types.js';
const scope=(f:string,g:string,id:string)=>`${f}:${g}:poll-context:${id}`;
export function initializePollContexts(store:Store){
 store.db.exec(`CREATE TABLE IF NOT EXISTS poll_contexts (
  family_id TEXT NOT NULL,group_id TEXT NOT NULL,id TEXT NOT NULL,body BLOB NOT NULL,
  PRIMARY KEY(family_id,group_id,id),
  FOREIGN KEY(family_id,group_id,id) REFERENCES messages(family_id,group_id,id) ON DELETE CASCADE)`);
 // Older binaries cannot identify deleted/restricted encrypted JSON in SQL.
 // Any message rewrite discards private state conservatively; current Store
 // saves validated state and pending work again inside the same savepoint.
 for(const event of ['INSERT','UPDATE OF body'])store.db.exec(`
  CREATE TRIGGER IF NOT EXISTS clear_poll_private_on_${event.startsWith('INSERT')?'insert':'update'} AFTER ${event} ON messages
  BEGIN
   DELETE FROM poll_contexts WHERE family_id=NEW.family_id AND group_id=NEW.group_id AND id=NEW.id;
   DELETE FROM pending_poll_votes WHERE family_id=NEW.family_id AND group_id=NEW.group_id AND target=NEW.id;
  END`);
}
/** Keep this out of the message JSON, including for rollback to older binaries
 * whose public DTO spreads fields they do not know. Both tables share the Store
 * savepoint; neither secret nor actor identity is a plaintext index. */
export function savePollState(store:Store,f:string,m:StoredMessage){
 if(m.kind!=='poll'||m.deleted||m.restricted||!m.pollState){
  store.run('DELETE FROM poll_contexts WHERE family_id=? AND group_id=? AND id=?',f,m.groupId,m.id);return;
 }
 store.run(`INSERT INTO poll_contexts VALUES(?,?,?,?) ON CONFLICT(family_id,group_id,id) DO UPDATE SET body=excluded.body`,
  f,m.groupId,m.id,seal(store.groupKey(f,m.groupId),JSON.stringify(m.pollState),scope(f,m.groupId,m.id)));
}
export function loadPollState(store:Store,f:string,m:StoredMessage):PollState|undefined{
 if(m.kind!=='poll'||m.deleted||m.restricted){savePollState(store,f,m);return;}
 const row=store.one('SELECT body FROM poll_contexts WHERE family_id=? AND group_id=? AND id=?',f,m.groupId,m.id);
 return row?JSON.parse(open(store.groupKey(f,m.groupId),row.body,scope(f,m.groupId,m.id)).toString()):undefined;
}
