# WhatsApp message compatibility

Scope: local fixes based on Yahad 0.5.3, integrated commit `0bf1e9d4d71221fcb77c6aa2b2213aaf4ea0a5ae`, with `@whiskeysockets/baileys@7.0.0-rc14` unchanged. This is a defined capability map, not a claim of complete WhatsApp parity.

## Current behavior

| Feature | Receive / display | Send / act |
|---|---|---|
| Text, standard images/video/audio/documents, static/animated WebP stickers | Supported subject to download, MIME, size and browser codec limits | Existing file/voice/sticker paths retained |
| Quoted replies | Same-conversation IDs survive reverse arrival; missing/deleted/restricted originals show an unavailable-original placeholder; quoted payloads are never imported | Requires an available original. Rechecked after asynchronous preparation, immediately before provider submission. Changed/unavailable originals fail with `409 ORIGINAL_UNAVAILABLE` before sending the main message |
| Emoji reactions | Add/change/remove; historical snapshots; pending reactions can precede the target; source timestamps prevent older updates from replacing newer ones | One valid Unicode emoji sequence or removal; includes flags, skin tones and ZWJ sequences supported by Node 24. Actions require an available original |
| Edits | Handles `editedMessage` and protocol edit envelopes; original position stays stable; stale edits do not replace newer ones | Existing own-text and local 15-minute window restrictions remain |
| Deletions | Missing originals receive a persistent content-free tombstone; later replay cannot restore them; existing media/reactions/quotes are cleared | Existing ownership/access rules remain; an unavailable original is an explicit conflict |
| Temporary / view-once | Intentionally unavailable in Yahad. Nested envelopes, content flags and provider ephemeral metadata are inspected before raw serialization/download | No quote/reaction/edit/delete action on retained placeholders |
| Regular text poll V1/V2/V3 | Question/options shown in a read-only card; optional counts from received authenticated votes, explicitly incomplete | No creation, voting or per-message action UI/API |
| Poll V4/V5, quiz/image variants and image options | Specific unsupported-variant explanation; no question, answer or media projection | Not enabled |
| Poll vote / result snapshot | Recognized with an explicit unavailable-details explanation | No inferred vote counts, results, or “zero votes” |
| Round video (PTV) | Received through existing video player, subject to media availability, MIME, size and codec limits; conflicting PTV/media fields rejected | Recording/sending as a round video not added |
| Contact cards / arrays | Receive-only text: bounded vCard FN/TEL subset (name and phone numbers), no raw/card import, photos, links or notes | Sending/importing contacts not enabled |
| Static location | Receive-only name, address and explicit finite coordinates as text; no map/URL/thumbnail load | Sharing a location not enabled |
| Live location | Explicit unavailable-tracking explanation, no coordinate projection | Not enabled |
| Textual buttons/lists/hydrated templates and common replies | Bounded read-only text/labels; explicit inactive-actions notice | No interactive commands, callbacks or CTA actions |
| Lottie, media/carousel/commerce interactive variants and unverified envelopes | Specific fallback / unsupported | Not enabled |

The public DTO includes optional per-message `actions` so the UI does not offer an action that the API cannot perform. The API is authoritative, including for old clients. Raw payloads, reaction timestamps, edit timestamps and original-time bookkeeping are not exposed in the public DTO. `mine` for reactions still means the linked family WhatsApp account, not an individual child.

## Ordering, retention and persistence

- Pending reactions use an additive `pending_message_reactions` SQLite table, scoped to family and conversation. The event body is encrypted with the conversation key; the actor index is derived using that key. Actor identity is not a plaintext index.
- Pending reactions expire after seven days and are bounded to 256 per conversation and 2,048 per family. Overflow/expiry can omit an old reaction; this is not an unlimited history guarantee. Active reaction timestamp state is bounded to 2,048 actors per message.
- Conversation purge clears pending rows before discarding the conversation key. Child-grant changes do not merge families or conversations. Deletion tombstones remain for the conversation's retention lifetime so old history cannot resurrect content.
- PN and LID identity aliases are not guessed or merged. Conflicting updates with an identical source timestamp use arrival order. Reliable alias resolution requires provider identity evidence; unavailable history cannot be reconstructed.
- An edit's provider time is separate from the original timestamp/key needed for history requests. Correcting stored timestamp/key metadata never restores old text or media. An edit arriving before its original is not a usable history anchor until original metadata arrives.
- Media completion reads current stored state, preserving concurrent reactions, receipts and author metadata. It cannot overwrite deletion/temporary-content decisions; acknowledgements reuse existing media instead of downloading it again.
- Legacy rows with identifiable temporary/view-once markers in their retained raw are scrubbed lazily when requested: content/raw/media are removed and a restricted placeholder is stored. No database-wide scan, content recovery or family-data inspection was performed during development. An old snapshot is also filtered before public projection and is ineligible for outgoing actions.
- Generic unsupported poll records may become readable only on authorized same-ID redelivery supplying valid content. Previously discarded poll payloads and quotes are not reconstructed.

### Read-only poll display

Normal V1/V2/V3 text polls retain only the question in `text` and a new `poll.options` array of plain strings. Provider raw, correct answers and generic context are not retained. Minimal private vote-decryption context and observed ballots are isolated in the encrypted `poll_contexts` table described below. The public API and UI validate the same allowlist again. The question also supplies existing previews, search and incoming quote snippets; options have no vote controls. Optional observed counts do not claim complete results.

Product display bounds are 2,048 UTF-16 code units for the question, 2–32 options and 512 code units per option. These are application safety bounds, not a claim about every WhatsApp version. Missing, invalid, sparse or oversized values get an explanation rather than a truncated poll. The queue byte budget includes all option text, including UTF-8 multibyte characters.

Newer V4/V5 envelopes, non-text/image polls, quizzes/correct-answer payloads, unverifiable vote updates and result snapshots keep explicit fallbacks. Temporary/view-once checks run first, including inside V4/image-option wrappers. Deletion/restriction clears question/options; replay cannot resurrect them. A legacy generic placeholder can become a visible poll only when fresh permitted provider content supplies the question/options. Merely opening an old placeholder never recovers content.

The poll kind is receive-only: it is excluded from outgoing types and blocked at the real adapter even for runtime-invalid input. Poll actions are disabled because the original raw is intentionally not retained. Creation, casting votes and complete results remain unavailable; observed receive-only counts are detailed below.

This is an additive runtime table and optional encrypted-body metadata. Database schema version, dependency lockfile, installer, network access mechanism, permissions and deployment configuration are unchanged. Ordinary SQLite backups include the added table.

## Poll capability map and separate path forward

The real connector reports `polls: { identify: true, display: true, observedResults: true, create: false, vote: false, results: false }` for the product. The pinned library's [tagged README](https://github.com/WhiskeySockets/Baileys/blob/v7.0.0-rc14/README.md#poll-message) documents creating polls upstream, but Yahad does not expose it. The bounded read-only display above does not imply working voting/results.

The pinned [event processor](https://github.com/WhiskeySockets/Baileys/blob/v7.0.0-rc14/src/Utils/process-message.ts#L689) comments out the poll-vote decryption/dispatch path. The [send-content types](https://github.com/WhiskeySockets/Baileys/blob/v7.0.0-rc14/src/Types/Message.ts) expose poll creation but no high-level vote send branch. The presence of protobuf types or a standalone helper does not establish a usable feature.

### Received vote counts and safe persistence

The pinned release does export the official `decryptPollVote` helper. Only its automatic pollUpdateMessage -> messages.update handler is commented out. Yahad now calls that helper from its own scoped receive path; no upstream commented code was re-enabled and no production vote encryption was written.

Only validated V1/V2/V3 text polls can pin an origin: provider creator identities, the selectable count, content fingerprint and, if supplied, the 32-byte message secret. This minimal state is sealed under the conversation key in a separate `poll_contexts` table. The regular messages.body JSON never contains it, including for rollback to older DTO implementations. No secret, creator/voter identity, ciphertext, answer or raw context is exposed publicly.

Origin identities stay pinned even if the key is missing. Recovery/enrichment requires overlapping provider-observed creator identities, matching content/selectable count/fromMe and, for a keyed origin, the same secret. A legacy row without a reliable origin can display fresh supplied content but cannot silently adopt new crypto context. PN/LID aliases are normalized without device suffixes; no phone-to-LID guessing or remote identity lookup occurs. At most two explicit identities per actor, with at most four creator/voter pairs, are tried through authenticated GCM decryption. Neither LID/LID nor mixed-mode addresses are assumed universally.

Only authenticated hashes of known unique options within the selectable limit count. Source time and a deterministic event-ID tie-break prevent stale replacement. Withdrawal remains a durable empty ballot. Later provider alias evidence can merge two previously unlinked records while keeping the newest selection. Unknown aliases can therefore temporarily count as distinct; this is one reason results remain explicitly partial.

The encrypted pending_poll_votes table handles missing origin/key/alias and reverse history arrival. It keeps at most 256 events per conversation, 2,048 per family for seven days, with 8,192-byte ciphertext and 12-byte IV bounds. It retains unresolved ciphertext for later authenticated enrichment, removes processed/stale/invalid/expired work, and never exposes payloads. Active ballot state is capped at 512 identities; capacity preserves existing withdrawal tombstones rather than evicting them and allowing stale resurrection. Overflow/expiry/missing history can omit votes.

Initial origin persistence and alias/key recovery consume pending ciphertext and store ballots in one SQLite transaction, with notifications after commit. Duplicate unchanged origins do not rewrite state or notify. Private rows and pending work clear on deletion, restriction, accepted edits, group purge or key destruction. Conservative message INSERT/UPDATE triggers also erase private state under older binaries; the current Store restores only permitted state and work within its SAVEPOINT. Rolling back can consequently lose observed results on an old-code rewrite, which is preferable to retaining deleted private state. No installer/schema-version/dependency change is required; SQLite backups include the additive tables/triggers.

The UI shows no zero count before any vote has authenticated. When counts exist it says they include only received/decrypted votes and may be incomplete. A received withdrawal can legitimately yield observed zeros. Poll snapshots, unverified variants, missing keys and missing provider events cannot establish complete live results. History-attached plaintext pollUpdates are not blindly trusted/imported as decrypted ballots. Tests are synthetic; live TypeScript/WhatsApp interoperability has not been claimed.

### Sending boundary and remaining interoperability work

An internal `PollVoteSending` boundary is present but its implementation is fixed unavailable and always rejects. There is no vote endpoint/control, raw-proto sender, custom production encryption or relay workaround. The ordinary connector now also rejects runtime-invalid outgoing content kinds before transport. [PR #2870](https://github.com/WhiskeySockets/Baileys/pull/2870) proposes official vote generation, encryption and stanza/retry markers, but was open/unreleased when checked; its author reports live Rust-port checks, not the TypeScript path. ACK alone is not proof of delivery. No unmerged dependency was adopted.

Before activating a sender: use a maintained official release/verified implementation, test binary stanza and retry markers, then separately authorized paired-device TypeScript tests for PN/LID chat/group modes, own/received/history polls, multiple choices and withdrawal. The linked family account has one vote, so any future UI must state whose account votes and that changing it can replace a family member's choice. Poll creation is available in the upstream high-level generator but is not exposed by this product stage.

## Verification

Focused synthetic suites also include `tests/poll-display.test.ts`. Poll cases cover protobuf defaults, variant fallbacks, strict projection, ephemeral wrappers/metadata, reverse arrival/history, edits/deletions, child grants and family isolation, invalid/oversized values, queue byte budgets, XSS-safe React rendering and absence of outgoing poll actions. Existing message suites cover reverse arrival, independent Store reload, queue encryption/bounds/expiry, stale updates, media races, history anchors, old restricted snapshots and API errors before provider submission. `tests/security.test.ts` expects a safe unavailable quote placeholder rather than silently dropping the relationship; foreign embedded content remains excluded.

Independent code review identified and then verified fixes for stale quote snapshots during preparation, media completion overwriting reactions, edit/history-anchor metadata, legacy temporary raw, and pending-media acknowledgement merging. The reviewer used source inspection and in-memory synthetic checks only and reported no remaining blocker in those changes.

Use Node 24 and run:

```powershell
node --import tsx --test --test-concurrency=1 tests/poll-display.test.ts tests/message-retention.test.ts tests/message-ordering.test.ts tests/message-actions.test.ts
node --import tsx --test --test-concurrency=1 tests/*.test.ts
node node_modules/typescript/bin/tsc --noEmit
node node_modules/vite/bin/vite.js build
```

Run results and environment-specific limits are recorded in `evidence/message-support-audit/`. No live WhatsApp or real family data was used. Changes are local commits only and have not been pushed, installed or published.

### Contact and location display

Contact display accepts vCard 2.1/3.0/4.0 FN/TEL text fields, basic line folding and escaped punctuation. It intentionally rejects encoded FN/TEL fields (including quoted-printable/base64), missing phone/name, duplicate FN, malformed cards and phone values outside the plain display subset. Limits: 16 contacts, 16 KiB per input card, 256 code units per name, eight distinct phone numbers per card and 80 code units per phone; total projected text is at most 16,384 code units. Unsupported cards keep a specific explanation and discard raw. No contact is imported into an address book.

Static locations require explicit latitude and longitude fields, finite values in [-90,90]/[-180,180], a name <=256 and address <=1,024 code units. Missing protobuf fields cannot silently turn into (0,0). Location URLs, thumbnails, context, speed and other metadata are not retained. A live-location envelope or isLive flag receives an explicit tracking-unavailable explanation without coordinates.

These kinds retain only plain text in the existing encrypted message body. The service strips raw/media/download/reply payloads again; the public projection and React renderer bound text and escape it. Temporary markers, including per-card expiration in an array, still run before projection. Existing family/conversation/child access gates apply. Generic placeholders can upgrade only upon fresh authorized provider redelivery. Receive-only kinds are excluded from outgoing types and per-message actions at UI/API/connector levels. Edits cannot smuggle a different format or poll metadata into them; deletion and restriction remain irreversible across replay.

### Textual interactive messages

Known buttonsMessage, listMessage, hydrated template, basic interactive body text and common button/template/list/interactive response formats now project plain text as a receive-only interactive kind. The UI states that buttons/actions are unavailable. It does not parse native-flow JSON or retain callback IDs, selected row IDs, URLs, phone CTA targets, generic context, previews or nested media. Labels are text, not clickable commands. Unsupported media headers, carousel/commerce/product lists, unverified highly structured templates, linked-device-masked templates, ambiguous variants and malformed/oversized content receive a precise fallback without content/raw.

Bounds: 256 code units per title/label, 4,096 per body, 512 per footer/description, at most ten buttons/sections and 32 list rows, and the shared 16,384-code-unit text cap. Sparse/missing/invalid structures fail closed. Own nested button/template/interactive header/footer media, template children and carousel cards are inspected for temporary markers before projection. Embedded quoted payloads remain excluded from both traversal and persistence. Existing receive-only, family/child, edit/deletion and safe React text rules apply. No business interaction or vote sending is activated.

### Remaining boundaries

This is a concrete, bounded compatibility implementation, not complete WhatsApp parity. Lottie animated stickers need a reviewed supported decoder/player path; no random fork or decoder dependency was adopted. View-once/temporary content intentionally remains unavailable. Live-location tracking, media/carousel/commerce interactive actions, unverified poll V4/V5/image/quiz variants, complete poll history/results and casting votes have no verified end-to-end implementation here. Poll creation exists in the official library but has no product compose/send flow in this stage. Standard media still depends on download availability and browser codecs. These limits are explicit; unavailable payloads are not reconstructed from previously discarded content.
