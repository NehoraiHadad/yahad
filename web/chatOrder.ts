export type OrderedChat = { id: string; lastMessageTime?: number; last?: { time: number } };
/** Newest retained message first, with deterministic empty/tied ordering. */
export function compareChatActivity(a: OrderedChat, b: OrderedChat): number {
  const time = (chat: OrderedChat) => {
    const value = chat.lastMessageTime ?? chat.last?.time ?? 0;
    return Number.isFinite(value) ? value : 0;
  };
  return time(b) - time(a) || (a.id < b.id ? -1 : a.id > b.id ? 1 : 0);
}
