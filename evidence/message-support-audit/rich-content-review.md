# Independent review: rich message compatibility

Reviewer: /root/message_fix_review, read-only source and synthetic in-memory checks; no real data/provider/network.

Each capability was reviewed before product expansion. No remaining identified blocker was reported for the final source.

- PTV: fixed mismatched media selection that could validate one field and download another; reject PTV combined with any other media at normalization and source validation. Tests cover all five media combinations and bad URL/path.
- Contact/location: bounded minimal projection, no raw/URLs/media/encoded-card guesses; precise oversized-contact fallback instead of a false temporary label; read-only API/adapter/UI guards; safe edits and deletion.
- Poll origin: persist original creator evidence even without a key. Key recovery cannot adopt a different creator or an unbound legacy origin. Late aliases require overlapping authenticated evidence, matching content/count/fromMe and same key for an already keyed origin.
- Poll ordering: durable withdrawal tombstones and source-time/event-ID ordering; explicit provider alias bridges merge identities without losing the newest choice.
- Pending work: unresolved ciphertext survives first-origin/duplicate delivery until alias/key enrichment or TTL; known stale/invalid work can be discarded. Duplicate no-op origins make no write/notify.
- Atomicity: queued-vote deletion and state persistence are in one outer transaction for initial origin and context enrichment; notifications follow commit. Synthetic write failures at both boundaries preserve queue/state for retry.
- Rollback privacy: secrets are in separate sealed poll_contexts rows, never regular message JSON; old-binary INSERT/UPDATE triggers discard private context/pending work, while current Store restores only allowed state/work in the same SAVEPOINT. Old DTO cannot spread secret fields.
- Interactive text: bounded text/labels only; callback IDs/native JSON/CTA targets/media excluded; linked-device masked templates omitted; own nested media privacy scanning avoids quoted payloads; no active actions.
- Final read-path review: ordinary message and private poll-state lookup no longer performs unnecessary DELETE statements. Query-only SQLite reads pass; erasure stays in atomic write/trigger/cascade paths. Separate fifth commit avoids rewriting prior work.

Receive-only counts are partial observations, not complete live results. The vote sender remains fixed unavailable. Synthetic cryptographic fixtures use official pinned primitives and do not prove WhatsApp interoperability.
