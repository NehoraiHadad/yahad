import {decryptPollVote,sha256} from '@whiskeysockets/baileys';
import {hash} from '../crypto.js';
import {pollDisplay} from '../poll-display.js';
import type {StoredMessage} from '../connectors/types.js';
import type {PollContext,EncryptedPollVote,PollState,PollBallot,ObservedPollResults} from './types.js';
export const pollActorLimit=512;
const canonical=(s:unknown,min:number,max=min):s is string=>typeof s==='string' && s.length<=Math.ceil(max/3)*4 &&
 Buffer.from(s,'base64').length>=min && Buffer.from(s,'base64').length<=max && Buffer.from(s,'base64').toString('base64')===s;
const aliases=(xs:unknown):xs is string[]=>Array.isArray(xs)&&xs.length>0&&xs.length<=2&&
 Array.from(xs).every(x=>typeof x==='string'&&/^\d{1,64}@(lid|s\.whatsapp\.net)$/.test(x))&&new Set(xs).size===xs.length&&new Set(xs.map(x=>x.split('@')[1])).size===xs.length;
const id=(x:unknown):x is string=>typeof x==='string'&&x.length>0&&x.length<=180;
const stamp=(x:unknown):x is number=>typeof x==='number'&&Number.isSafeInteger(x)&&x>0;
const fingerprint=(text:string,options:string[])=>hash(JSON.stringify([text,options]));
export function validPollVote(event:EncryptedPollVote):boolean{
 return !!event && id(event.target)&&id(event.id)&&stamp(event.time)&&event.time<=Date.now()+300_000&&
  aliases(event.voters)&&canonical(event.payload,16,8192)&&canonical(event.iv,12);
}
export function newPollState(context:PollContext|undefined,text:string,options:string[]|undefined):PollState|undefined{
 if(!context || context.secret!==undefined&&!canonical(context.secret,32)||!aliases(context.creators)||!options || !pollDisplay(text,options) ||
  new Set(options).size!==options.length || !Number.isInteger(context.selectable)||context.selectable<1||context.selectable>options.length)return;
 return {secret:context.secret,creators:[...context.creators],selectable:context.selectable,fingerprint:fingerprint(text,options),ballots:[]};
}
function validState(m:StoredMessage):m is StoredMessage & {pollState:PollState;poll:{options:string[]}}{
 const s=m.pollState,options=m.poll?.options;
 return m.kind==='poll'&&!m.deleted&&!m.restricted&&!!s&&!!options&&
  canonical(s.secret,32)&&!!newPollState(s,m.text,options)&&s.fingerprint===fingerprint(m.text,options)&&Array.isArray(s.ballots)&&s.ballots.length<=pollActorLimit&&
  Array.from(s.ballots).every(b=>b&&aliases(b.aliases)&&stamp(b.time)&&id(b.id)&&Array.isArray(b.selected)&&b.selected.length<=s.selectable&&
   Array.from(b.selected).every(i=>Number.isInteger(i)&&i>=0&&i<options.length)&&new Set(b.selected).size===b.selected.length)&&
  new Set(s.ballots.flatMap(b=>b.aliases)).size===s.ballots.reduce((n,b)=>n+b.aliases.length,0);
}
const newer=(a:PollBallot,b:PollBallot)=>a.time>b.time||a.time===b.time&&a.id>b.id;

/** Uses the official authenticated decryptor only. At most four contexts, all
 * from actual provider identities. Authentication failure never becomes a vote. */
export function applyPollVote(m:StoredMessage,event:EncryptedPollVote,outcome?:{resolved:boolean}):boolean{
 if(!validState(m)||!validPollVote(event)||event.target!==m.id)return false;
 const state=m.pollState,known=new Map(m.poll.options.map((name,index)=>[sha256(Buffer.from(name)).toString('hex'),index]));
 let selected:number[]|undefined;
 for(const creator of state.creators){
  for(const voter of event.voters){
   try{
    const decoded=decryptPollVote({encPayload:Buffer.from(event.payload,'base64'),encIv:Buffer.from(event.iv,'base64')},
     {pollEncKey:Buffer.from(state.secret!,'base64'),pollMsgId:m.id,pollCreatorJid:creator,voterJid:voter});
    if(outcome)outcome.resolved=true;
    const hashes=decoded.selectedOptions||[];
    if(hashes.length>state.selectable)return false;
    const indexes:number[]=[];
    for(const value of hashes){const index=value.length===32?known.get(Buffer.from(value).toString('hex')):undefined;if(index===undefined||indexes.includes(index))return false;indexes.push(index);}
    selected=indexes;break;
   }catch{/* No keys, identities, payload or crypto errors are logged. */}
  }
  if(selected!==undefined)break;
 }
 if(selected===undefined)return false;
 const related=state.ballots.filter(b=>b.aliases.some(a=>event.voters.includes(a)));
 const merged=[...new Set([...event.voters,...related.flatMap(b=>b.aliases)])].sort();
 if(!aliases(merged))return false; // Conflicting mappings need provider investigation.
 if(!related.length&&state.ballots.length>=pollActorLimit){const changed=!state.limited;state.limited=true;return changed;}
 let winner:PollBallot={aliases:merged,id:event.id,time:event.time,selected};
 for(const previous of related)if(!newer(winner,previous))winner={...previous,aliases:merged};
 if(related.length===1&&JSON.stringify(winner)===JSON.stringify(related[0]))return false;
 const next=state.ballots.filter(b=>!related.includes(b));next.push(winner);
 const changed=JSON.stringify(state.ballots)!==JSON.stringify(next);
 state.ballots=next;return changed;
}
export function observedPollResults(m:StoredMessage):ObservedPollResults|undefined{
 if(!validState(m)||!m.pollState.ballots.length)return;
 const counts=m.poll.options.map(()=>0);
 for(const b of m.pollState.ballots)for(const index of b.selected)counts[index]++;
 return {counts,partial:true};
}

/** A missing key never removes origin binding. Legacy rows without a pinned
 * origin cannot recover crypto context merely by matching display text. */
export function recoverPollState(previous:PollState|undefined,context:PollContext|undefined,text:string,options:string[]|undefined):PollState|undefined{
 const next=newPollState(context,text,options);
 if(!previous||!next||!aliases(previous.creators)||!Array.isArray(previous.ballots)||
   !next.creators.some(a=>previous.creators.includes(a))||previous.selectable!==next.selectable||previous.fingerprint!==next.fingerprint)return;
 // A late provider alias may enrich an already keyed origin only with the same
 // secret. A missing-key origin stays pinned while its first key is recovered.
 if(previous.secret ? previous.secret!==next.secret : !next.secret||previous.ballots.length)return;
 const creators=[...new Set([...previous.creators,...next.creators])].sort();
 return aliases(creators)?{...previous,secret:next.secret,creators}:undefined;
}
