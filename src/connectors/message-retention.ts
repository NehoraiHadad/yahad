import type { proto } from '@whiskeysockets/baileys';

const wrappers = ['documentWithCaptionMessage', 'editedMessage', 'associatedChildMessage',
  'groupStatusMessage', 'groupStatusMessageV2'] as const;

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
