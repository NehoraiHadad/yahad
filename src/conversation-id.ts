/** Provider-owned conversation identifiers only. Never accept status, broadcasts,
 * newsletters, device-qualified identities or arbitrary client-supplied targets. */
export function conversationKind(id: string): 'group' | 'direct' | undefined {
  if (/^[0-9]+(?:-[0-9]+)?@g\.us$/.test(id)) return 'group';
  if (/^[0-9]{1,32}@(s\.whatsapp\.net|lid)$/.test(id)) return 'direct';
}
