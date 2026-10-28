import type { Store, User } from './store.js';
import type { StoredMessage } from './connectors/types.js';

/** Local, per-account unread state. Existing history starts at zero: provider read
 * receipts cannot tell us which family account viewed an old message. */
export function initializeChatReadState(store: Store) {
  store.db.exec(`CREATE TABLE IF NOT EXISTS chat_unread (
    family_id TEXT NOT NULL, user_id TEXT NOT NULL, group_id TEXT NOT NULL, message_id TEXT NOT NULL,
    PRIMARY KEY(family_id,user_id,group_id,message_id),
    FOREIGN KEY(family_id,user_id) REFERENCES users(family_id,id) ON DELETE CASCADE,
    FOREIGN KEY(family_id,group_id,message_id) REFERENCES messages(family_id,group_id,id) ON DELETE CASCADE
  )`);
}
export function recordUnread(store: Store, familyId: string, message: StoredMessage, isNew: boolean, live: boolean) {
  if (message.deleted || message.fromMe) {
    store.run('DELETE FROM chat_unread WHERE family_id=? AND group_id=? AND message_id=?', familyId, message.groupId, message.id);
    return;
  }
  if (!isNew || !live) return;
  // Only accounts with access at receipt time accrue unread messages. Granting
  // access later does not fabricate an unread history or expose another account's state.
  store.run(`INSERT OR IGNORE INTO chat_unread SELECT family_id,id,?,? FROM users
    WHERE family_id=? AND (role='parent' OR EXISTS (SELECT 1 FROM grants
      WHERE grants.family_id=users.family_id AND grants.user_id=users.id AND grants.group_id=?))`,
    message.groupId, message.id, familyId, message.groupId);
}
export function unreadCount(store: Store, user: User, group: string): number {
  if (!store.allowed(user, group)) return 0;
  return Number(store.one('SELECT count(*) AS n FROM chat_unread WHERE family_id=? AND user_id=? AND group_id=?', user.familyId, user.id, group)?.n || 0);
}
export function markLocallyViewed(store: Store, user: User, group: string, ids: string[]): number {
  if (!store.allowed(user, group)) return 0;
  let changed = 0;
  store.tx(() => {
    for (const id of new Set(ids)) changed += Number(store.run(
      'DELETE FROM chat_unread WHERE family_id=? AND user_id=? AND group_id=? AND message_id=?',
      user.familyId, user.id, group, id).changes);
  });
  return changed;
}
