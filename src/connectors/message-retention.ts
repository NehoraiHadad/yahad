import type { proto, WAMessage } from '@whiskeysockets/baileys';
import type { StoredMessage } from './types.js';

const wrappers = ['documentWithCaptionMessage', 'editedMessage', 'associatedChildMessage',
  'groupStatusMessage', 'groupStatusMessageV2', 'pollCreationMessageV4', 'pollCreationOptionImageMessage'] as const;

export const temporaryMessageText='הודעה זמנית או לצפייה חד־פעמית — לפתיחה ב־WhatsApp בלבד';
export function restrictedMessage(raw: WAMessage): boolean {
  return !!raw?.key?.isViewOnce || Number(raw?.ephemeralDuration)>0 || Number(raw?.ephemeralStartTimestamp)>0 ||
    !!raw?.ephemeralOutOfSync || restrictedContent(raw?.message);
}
export function restrictedStoredMessage(message: StoredMessage): boolean {
  if(message.restricted)return true;
  try { return !!message.raw && restrictedMessage(JSON.parse(message.raw)); } catch { return false; }
}
export function restrictedPlaceholder(message: StoredMessage): StoredMessage {
  return {id:message.id,groupId:message.groupId,sender:message.sender,fromMe:message.fromMe,time:message.time,
    kind:'unsupported',text:temporaryMessageText,restricted:true,status:message.status,reactions:{}};
}

/** Inspect only the message being admitted, never embedded quotes or previews.
 * Fail closed before Baileys unwraps away the retention markers. */
export function restrictedContent(message: proto.IMessage | null | undefined, depth = 0): boolean {
  if (!message) return false;
  if (depth >= 8) return true;
  if (message.ephemeralMessage || message.viewOnceMessage || message.viewOnceMessageV2 || message.viewOnceMessageV2Extension)
    return true;
  for (const value of Object.values(message)) {
    if (value && typeof value === 'object' &&
      ((value as any).viewOnce || (value as any).contextInfo?.expiration)) return true;
  }
  for (const field of wrappers)
    if (message[field] && restrictedContent(message[field]?.message, depth + 1)) return true;
  return restrictedContent(message.protocolMessage?.editedMessage, depth + 1);
}
