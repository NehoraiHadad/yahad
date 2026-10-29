import type { WAMessage, WAMessageKey, proto } from '@whiskeysockets/baileys';
import { conversationKind } from '../conversation-id.js';
import type { ReactionUpdate } from './types.js';

export function reactionActor(key: WAMessageKey | null | undefined, group: string) {
  return key?.fromMe ? 'self' : key?.participant || (conversationKind(group)==='direct' ? group : 'unknown');
}
export function reactionTime(value: unknown, fallback = 0) {
  const n=Number(value);return value!=null && Number.isFinite(n) && n>=0 ? n : fallback;
}
export function historicalReactions(raw: WAMessage): ReactionUpdate[] {
  const g=raw.key.remoteJid!;
  return (raw.reactions||[]).slice(0,2048)
    .filter(r=>!r.key?.remoteJid || r.key.remoteJid===g)
    .map(r=>({target:raw.key.id!,actor:reactionActor(r.key,g),emoji:r.text||'',time:reactionTime(r.senderTimestampMs)}));
}
export function editedEnvelope(message: proto.IMessage | null | undefined,depth=0): boolean {
  if(!message || depth>=8)return false;
  if(message.editedMessage)return true;
  return [message.documentWithCaptionMessage,message.associatedChildMessage,message.groupStatusMessage,message.groupStatusMessageV2]
    .some(wrapper=>editedEnvelope(wrapper?.message,depth+1));
}
