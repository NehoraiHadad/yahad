import { areJidsSameUser, type GroupMetadata, type MessageUserReceipt } from '@whiskeysockets/baileys';
import type { Receipt, RecipientState } from './types.js';

/** WhatsApp group semantics: two ticks once every recipient has the message,
 * read once every recipient has read it. Recipients are the members other than
 * us when the first receipt arrives; members who join later never received it. */
const rank: Record<RecipientState, number> = { pending: 0, delivered: 1, read: 2 };
const same = (a?: string | null, b?: string | null) => !!a && !!b && areJidsSameUser(a, b);
const identities = (p: GroupMetadata['participants'][number]) => [p.id, p.lid, p.phoneNumber];

export function receiptLevel(receipt: MessageUserReceipt): Exclude<RecipientState, 'pending'> | undefined {
  if (receipt.readTimestamp != null || receipt.playedTimestamp != null) return 'read';
  if (receipt.receiptTimestamp != null) return 'delivered';
}
export function initialRecipients(meta: GroupMetadata, self: (string | undefined)[]): Record<string, RecipientState> {
  const states: Record<string, RecipientState> = {};
  for (const p of meta.participants)
    if (!identities(p).some(j => self.some(s => same(j, s)))) states[p.id] = 'pending';
  return states;
}
/** Resolves a receipt's user (LID or phone JID) to the participant key; undefined for us or non-members. */
export function recipientOf(meta: GroupMetadata, user: string, self: (string | undefined)[]) {
  if (self.some(s => same(user, s))) return;
  return meta.participants.find(p => identities(p).some(j => same(j, user)))?.id;
}
export function applyRecipientReceipt(states: Record<string, RecipientState>, recipient: string, level: Exclude<RecipientState, 'pending'>) {
  const current = states[recipient];
  if (current === undefined || rank[level] <= rank[current]) return;
  return { ...states, [recipient]: level };
}
export function groupReceiptStatus(states: Record<string, RecipientState>): Receipt | undefined {
  const values = Object.values(states);
  if (!values.length) return;
  if (values.every(v => v === 'read')) return 'read';
  if (values.every(v => v !== 'pending')) return 'delivered';
  // A member's receipt proves the server accepted the message.
  return 'sent';
}
