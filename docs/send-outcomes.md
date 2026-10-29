# Outgoing messages at access boundaries

The server distinguishes a confirmed connector acknowledgement, a proven failure
before provider handoff, and an unknown outcome. A child quota expiry or revoked
session still denies the HTTP content response, but cannot turn a matching provider
acknowledgement into a retryable failure. The family parent retains that message
and its media while the conversation retention generation remains current.

## Stable identity and reconciliation

POST messages/media uses the existing client UUID to derive the provider message
ID. Its private SHA-256 fingerprint binds logical text, original reply ID, raw file
bytes, filename, MIME and voice/sticker mode. A different payload cannot reuse the
same ID. The fingerprint and acknowledgement flag are encrypted in the existing
message body and omitted from public messages; no message text/input log is added.

Pending provider work and asynchronous upload preparation are serialized by
family/conversation/ID, including across sessions for the same child. Preparation
also binds the author and raw fingerprint. Concurrent requests get SEND_PENDING
or SEND_ID_CONFLICT; they do not submit again. A retained confirmed message is an
idempotent result. Legacy failed and abandoned pending rows are uncertain unless
non-submission was explicitly proven. These states survive restart with the
encrypted message; transient reservations are released in finally blocks.

The connector calls beforeSend after asynchronous preparation, immediately before
handoff. Session, quota, conversation generation and original quoted-message
checks remain there. SendNotSubmittedError proves preparation rejection only when
handoff did not start. Ordinary errors and missing/mismatched acknowledgements
remain uncertain. Provider echoes can later confirm uncertain rows. A matching
acknowledgement is separate from delivery/read receipts: missing receipts stay
unknown and are never invented.

GET /api/groups/:g/outgoing/:clientId returns only state: not_found, pending,
unknown, failed or confirmed. It requires the current session, quota, conversation
permission and original author. It never contacts the provider. A proven failure
response may include sendOutcome=not_submitted only after a fresh same-ID state
check, so asynchronous validation cannot issue a stale safe-retry hint.

## Child experience

The mounted draft survives quota/network suspension. Unknown sends freeze the
draft and its identity. The child can explicitly check status or close the draft
without sending; there is no automatic POST retry on reconnect, renewal or status
checks. Confirmed status clears the draft; proven non-submission unlocks it for a
manual retry. Changing any safe draft payload rotates its ID before sending.
Copied message edits are still cleared on permission suspension. Permission for
the microphone resolving during a send, uncertainty or suspension stops tracks.

Unknown and missing records do not prove that nothing reached the provider, so
they never unlock a resend. Deliberately forgetting retained history purges its
dedupe metadata as well; this is not a permanent provider-wide idempotency ledger.
A still-running request reserves its ID even if its row was purged. After a purge,
the UI keeps a missing outcome uncertain instead of resubmitting it. A parent pause
still revokes sessions and requires login after resume; a time bonus does not undo
a pause. In-memory drafts are not persisted across refresh/logout/account changes.

## Integration and compatibility

Based on c9e7478e27ae30de1f9954912b8b2ac62640330e. Database schema remains 6 and
release metadata is unchanged. Added fields are optional encrypted message-body
metadata; older code already does not resubmit status=unknown records. No new
table, quota policy or migration is introduced. This is a local candidate only.

Synthetic tests cover exact deadlines, delayed preparation/ACK, parent bonus,
paused sessions before/after handoff, two tabs, request conflicts, restart, client
disconnect on isolated loopback, late echoes, quote changes, media bytes, identity
markers, retention generation, safe hints, receipt accuracy and draft/voice races.
No real provider, family database or machine installation is needed.
