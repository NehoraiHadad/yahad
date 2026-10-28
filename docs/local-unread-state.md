# Local unread counts

Unread badges belong to the signed-in Yahad account, not to the shared WhatsApp
account and not to whichever child is selected by a parent filter. Incoming live
messages accrue counts only for accounts authorized at receipt time. Outgoing,
replayed duplicate, historical import, edited and deleted messages do not create
new unread entries. Revocation clears that child's entries; regrant starts fresh.

The additive `chat_unread` table is created on startup alongside schema-2 storage.
It does not reinterpret old provider receipts: existing retained history starts
with a zero local count. Rows persist across restarts and cascade when their
account or retained message is removed. Message storage and unread entry creation
share a savepoint, including callers already inside a transaction.

Only an authenticated, CSRF-protected `POST /api/groups/:g/viewed` acknowledges the
explicit locally displayed IDs for that account. GET/search/history do not mark
read. The UI waits for rendered message rows intersecting the chat viewport in a
visible, focused tab; hidden tabs and search views do not acknowledge. This new
endpoint never invokes provider read receipts. The UI preserves the existing capability-gated provider receipt as a separate request, only for the same rendered visible rows and only while that view remains active. Provider failure does not roll back local viewing. Counts may differ from WhatsApp and
from another family account; this is deliberate.

Chat rows, approval pickers and child summaries order by the latest retained
message time, descending, with stable ID ties and zero for empty chats. The count
is local unread, not a promise that the provider supplied complete history.
