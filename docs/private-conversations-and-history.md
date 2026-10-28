# Conversation catalog and history

The existing family-scoped conversation catalog now includes provider-discovered
one-to-one chats as well as current groups. The legacy table and API names
(`groups`, `/api/groups`) remain stable; responses add `kind` and
`lastMessageTime`. No database version bump is required.

Only authenticated connector chat/history/message events discover direct chats.
Contacts alone do not create chats. Broadcast/status/newsletter and device JIDs
are excluded. Numeric phone and LID identities remain separate unless the
provider supplies the same conversation ID; this change does not guess identity
links or merge messages/grants across IDs.

Parents can access the known same-family catalog. Children start with no access
to new chats and need explicit grants. Existing authorization, encryption,
media isolation, read/send/reply/reaction/edit/delete and history checks apply
to both kinds. A group-membership refresh cannot erase direct conversations
merely because they are absent from the group-only endpoint.

History is partial, not a full-phone backup. Earlier versions could discard
ungranted messages; upgrading cannot recreate them. A request for older messages
requires an eligible locally stored original message key. Zero local messages
and `no_anchor` therefore do not establish a provider failure. A later delivered
message/history batch may provide an anchor; recovery of all old messages is
not promised.

Initial group history previously could race catalog initialization. The adapter
now obtains current authenticated membership before admitting a bounded batch
(up to 2048 messages / 16 MiB, one waiting batch). Historical group IDs alone
do not authorize retention. Stopped/stale connectors cannot repopulate catalogs.
Overload or failed processing is never marked complete.

All validation uses synthetic fixtures. No live WhatsApp account, family
messages, or Raspberry Pi installation was used for these tests.
