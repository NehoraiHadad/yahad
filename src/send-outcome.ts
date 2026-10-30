import { createHash } from 'node:crypto';
import type { StoredMessage } from './connectors/types.js';

/** Only the connector may prove that it rejected work before provider handoff. */
export class SendNotSubmittedError extends Error {
  constructor(error: unknown) { super(error instanceof Error ? error.message : 'Not submitted', { cause: error }); }
}
export const confirmedSend = (status: string | undefined) => ['sent', 'delivered', 'read'].includes(status || '');
export const acknowledgedSend = (message: StoredMessage | undefined) => !!message && (message.sendAcknowledged === true || confirmedSend(message.status));
export type SendState = 'not_found' | 'pending' | 'unknown' | 'failed' | 'confirmed';
export function sendState(message: StoredMessage | undefined, inFlight: boolean): SendState {
  if (!message) return inFlight ? 'pending' : 'not_found';
  if (acknowledgedSend(message)) return 'confirmed';
  if (inFlight) return 'pending';
  if (message.status === 'failed' && message.retrySafe === true) return 'failed';
  // Legacy failed rows and abandoned pending rows may already have reached the
  // provider. Neither a refresh nor a process restart proves non-submission.
  return 'unknown';
}
export function sendRequestHash(data: { text: string; replyId?: string }, attachment?: {
  bytes: Buffer; filename: string; mime: string; kind?: string; voice?: boolean; sticker?: boolean;
}) {
  return createHash('sha256').update(JSON.stringify({ version: 1, text: data.text, replyId: data.replyId || null,
    attachment: attachment ? { filename: attachment.filename, mime: attachment.mime, kind: attachment.kind || null,
      voice: !!attachment.voice, sticker: !!attachment.sticker,
      bytes: createHash('sha256').update(attachment.bytes).digest('hex') } : null })).digest('hex');
}
