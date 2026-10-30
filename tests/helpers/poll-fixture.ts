/** Synthetic test-only encryption fixture, using pinned upstream primitives and
 * the exact derivation in its decryptPollVote. Never imported by product code.
 * Passing these tests is not evidence of WhatsApp wire interoperability. */
import {aesEncryptGCM,hmacSign,sha256,proto} from '@whiskeysockets/baileys';
import {raw,group} from './message-fixture.js';
export const secret=Buffer.alloc(32,7),creator='101@lid',voter='202@lid';
export const choices=['First','Second'];
export function poll(id='poll',extra:any={},keyExtra:any={}){
 return raw(id,{pollCreationMessageV3:{name:'Synthetic question',options:choices.map(optionName=>({optionName})),selectableOptionsCount:1,...extra},messageContextInfo:{messageSecret:secret}},
  {key:{id,remoteJid:group,fromMe:false,participant:creator,...keyExtra}});
}
export function vote(id:string,selected:string[],time=Date.now()-1000,options:{target?:string;creator?:string;voter?:string;voterAlt?:string;key?:Buffer;keyExtra?:any}={}){
 const target=options.target||'poll',author=options.creator||creator,actor=options.voter||voter;
 const sign=Buffer.concat([Buffer.from(target),Buffer.from(author),Buffer.from(actor),Buffer.from('Poll Vote'),Buffer.from([1])]);
 const key=hmacSign(sign,hmacSign(options.key||secret,Buffer.alloc(32),'sha256'),'sha256');
 const encIv=Buffer.alloc(12,3),plaintext=proto.Message.PollVoteMessage.encode({selectedOptions:selected.map(x=>sha256(Buffer.from(x)))}).finish();
 const encPayload=aesEncryptGCM(plaintext,key,encIv,Buffer.from(`${target}\0${actor}`));
 return raw(id,{pollUpdateMessage:{pollCreationMessageKey:{id:target,remoteJid:group},vote:{encPayload,encIv},senderTimestampMs:time}},
  {key:{id,remoteJid:group,fromMe:false,participant:actor,participantAlt:options.voterAlt,...options.keyExtra}});
}
