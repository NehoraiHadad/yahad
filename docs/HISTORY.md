# History ingestion and recovery — 0.3.1 candidate

This fixes application-side loss and reporting. It does not guarantee that a
phone or the unofficial provider will return every old message. No live provider
calls, relink, Pi installation, or automatic-update activation were performed.

## What changed

The old synchronous admission loop stopped accepting after 512 queued messages.
A single 600-message event could therefore silently lose 88 entries. The adapter
now normalizes approved groups only and admits batches into one sequential
consumer. Limits are 2,048 pending messages, 16 MiB of accounted payload/overhead,
8 batches, and 256 KiB per normalized message. A 600-message synthetic batch is
processed in full. Excess count/bytes/batches and consumer failures are reported
as incomplete/overloaded; rejected content is not written to a spool or database.
Provider event listeners cannot pause upstream delivery, so this is bounded local
backpressure, not a promise that any arbitrarily large provider event is lossless.

Processing rechecks both connector generation and the group's retention-key epoch.
Revoking all grants and re-approving a group cannot admit old queued data under a
new key. Media closures retain sanitized message data and recheck authorization.
A consumer that stalls for 30 seconds invalidates its work and reports failure;
its capacity stays reserved until the operation unwinds, avoiding orphan workers.
Disposal retires processing markers and releases waiting batch buffers on
stop/reconnect; connector state changes clear obsolete operational status. A shared family
budget spans replacement connectors, so repeated reconnects cannot accumulate
additional stuck consumers. If an old transport never unwinds, subsequent work
reports overload rather than spawning more workers; a controlled app restart may
be needed to release that transport. No automatic relink is attempted.
The provider library itself may allocate a history payload before our listener;
its upstream memory behavior is outside this queue's limits and needs Pi testing.

## Supported request contract

The [provider history guide](https://baileys.wiki/advanced/history-sync) describes
asynchronous history events and distinguishes limited initial sync from disabling
history entirely. We keep `syncFullHistory:false` and explicitly allow history
notifications, then filter every message against current group grants.

The [pinned rc14 README](https://github.com/WhiskeySockets/Baileys/blob/v7.0.0-rc14/README.md#query-chat-history-groups-too)
requires an original message key and timestamp for on-demand history. The server
selects the oldest usable locally stored anchor within a bounded scan of the
first 2,048 chronological records. It rejects deleted, unsupported, pending,
failed, missing/malformed raw keys, mismatched group/ID/direction, and invalid
timestamps. Both original outgoing and received messages are valid anchors.
Clients never choose the anchor. If the scan limit is reached, that limitation
is shown explicitly rather than pretending that no anchor can exist anywhere.

The timestamp argument preserves the pinned README's original message-timestamp
convention (seconds, derived from stored milliseconds). The pinned implementation
assigns that argument to a wire field named `oldestMsgTimestampMs`; the name and
documentation differ. This is an unresolved upstream contract ambiguity, not a
proven application defect. No unverified unit change was made. Verify actual
phone behavior during the separate owner-operated acceptance gate.

The [pinned receive implementation](https://github.com/WhiskeySockets/Baileys/blob/v7.0.0-rc14/src/Socket/messages-recv.ts)
returns a request ID from `fetchMessageHistory`; it does not return restored
messages. The [pinned history processing implementation](https://github.com/WhiskeySockets/Baileys/blob/v7.0.0-rc14/src/Utils/process-message.ts)
emits `messaging-history.set` with optional `peerDataRequestSessionId` metadata.
The application uses only an exact matching value when that field is present,
including a bounded early-response buffer. Missing/different IDs remain
unattributed; neither temporal proximity nor a live message proves restoration.
This field's presence and echo behavior still need real-account acceptance.

## Honest status and privacy

The API reports requesting, waiting, receiving, partial, timeout, failure or
overload. It always reports `complete:false`. Submission has a 10-second bound;
waiting for attributable data has a 45-second bound. Repeated pending requests
reuse the state, retries have a 15-second cooldown, and each connector can have
at most two unresolved underlying submissions. A deliberate replacement
connector starts with fresh slots; old completions cannot modify its counters.
All groups in one family also share a six-submissions-per-minute limit.
Once an admitted batch is processing, response timeout does not mark it finished
or permit a duplicate request. The request stays blocked until that batch drains
or fails/unwinds. Exact-ID empty replies are zero-stored partial responses;
missing or unrelated IDs are not treated as a response.

Counters distinguish entries offered/processed, new history records actually
stored (duplicates excluded), exact-ID-attributed records, and the authoritative
current database row count. A matching batch ending means only that this batch
was handled, never that the account's entire history was restored. Additional
matching chunks can increase the stored count. Operational tracking is volatile
and bounded to 256 group entries; at saturation it reports overload without
crashing, and expired entries are reclaimed. Restart begins at unknown/partial.

All routes require the current authorized family/group and CSRF on POST, and
recheck authorization after an asynchronous request. Revocation/disconnect clears
tracking and invalidates queued/late work. Unapproved group content is never
retained to manufacture an anchor later.

## A group approved after connection can legitimately start empty

Earlier unapproved history was deliberately discarded. `presenceSubscribe` does
not fetch history, and the pinned API offers no supported anchor-free request.
The UI therefore explains that no usable original anchor exists and disables the
request. Future permitted live/history messages may provide an anchor; then a
manual older-history request becomes available. We do not invent a message ID,
request arbitrary private chats, clear linked-device credentials, or silently
relink the account to trigger another sync.

## Acceptance before automatic updates

Use the existing [one-time owner runbook](AUTOMATIC-UPDATES-SETUP.md); its live
paths remain `/var/lib/yachad/live` and `/etc/yachad/whatsapp.key`. Preserve the
existing link, data, master key and Caddy CA. No schema migration is introduced.
The 0.3.1 source candidate still requires owner merge/signing/release/deployment
approval. On a disposable copy and then an owner-approved test group, check
initial history, approval after connection, a genuine anchor, asynchronous
history delivery/counts, duplicate chunks, timeouts and sustained Pi memory.
Only after those checks and the existing updater/restore gates should the owner
consider switching from notify mode to auto mode. A submitted request is never
an acceptance receipt.
