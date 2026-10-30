import {jidNormalizedUser,type WAMessage,type proto} from '@whiskeysockets/baileys';
import {restrictedMessage} from '../connectors/message-retention.js';
import type {PollContext,EncryptedPollVote} from './types.js';

type Self={id?:string;lid?:string}|undefined;
/** Provider-supplied identities only. Never derive an LID from a phone number,
 * look up strangers, or assume a PN/LID pair from equal numeric local parts. */
export function identities(values:unknown[]):string[]{
 const result:string[]=[];
 for(const value of values){
  if(typeof value!=='string' || !/^\d{1,64}(?::\d{1,5})?@(lid|s\.whatsapp\.net)$/.test(value))continue;
  const jid=jidNormalizedUser(value);if(!result.includes(jid))result.push(jid);
 }
 return result.length<=2&&new Set(result.map(x=>x.split('@')[1])).size===result.length?result:[];
}
function author(raw:WAMessage,self:Self):string[]{
 if(raw.key.fromMe)return identities([self?.id,self?.lid]);
 const group=raw.key.remoteJid?.endsWith('@g.us');
 return identities(group?[raw.key.participant,raw.key.participantAlt]:[raw.key.remoteJid,raw.key.remoteJidAlt]);
}
export function pollContext(raw:WAMessage,content:proto.IMessage,self:Self):PollContext|undefined{
 if(restrictedMessage(raw))return;
 const secret=content.messageContextInfo?.messageSecret;
 const body=content.pollCreationMessage||content.pollCreationMessageV2||content.pollCreationMessageV3;
 const creators=author(raw,self),selectable=body?.selectableOptionsCount||body?.options?.length;
 if(!creators.length || !selectable)return;
 return {secret:secret instanceof Uint8Array&&secret.length===32?Buffer.from(secret).toString('base64'):undefined,creators,selectable};
}
export function pollVote(raw:WAMessage,content:proto.IMessage,self:Self):EncryptedPollVote|undefined{
 if(restrictedMessage(raw))return;
 const update=content.pollUpdateMessage,target=update?.pollCreationMessageKey;
 if(!update || !target?.id || target.id.length>180 || !raw.key.id || raw.key.id.length>180 ||
   target.remoteJid && target.remoteJid!==raw.key.remoteJid)return;
 const voters=author(raw,self),time=Number(update.senderTimestampMs),payload=update.vote?.encPayload,iv=update.vote?.encIv;
 if(!voters.length || update.senderTimestampMs==null || !Number.isSafeInteger(time) || time<=0 || time>Date.now()+300_000 ||
   !(payload instanceof Uint8Array) || payload.length<16 || payload.length>8192 || !(iv instanceof Uint8Array) || iv.length!==12)return;
 return {target:target.id,id:raw.key.id,time,voters,payload:Buffer.from(payload).toString('base64'),iv:Buffer.from(iv).toString('base64')};
}
