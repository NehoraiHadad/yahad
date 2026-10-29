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
| Regular text poll V1/V2/V3 | Question and options shown in a read-only card; explicit notice that voting/results are unavailable | No creation, voting or per-message action UI/API |
| Poll V4/V5, quiz/image variants and image options | Specific unsupported-variant explanation; no question, answer or media projection | Not enabled |
| Poll vote / result snapshot | Recognized with an explicit unavailable-details explanation | No inferred vote counts, results, or “zero votes” |
| Round video (PTV) | Received through existing video player, subject to media availability, MIME, size and codec limits; conflicting PTV/media fields rejected | Recording/sending as a round video not added |
| Contact cards / arrays | Receive-only text: bounded vCard FN/TEL subset (name and phone numbers), no raw/card import, photos, links or notes | Sending/importing contacts not enabled |
| Static location | Receive-only name, address and explicit finite coordinates as text; no map/URL/thumbnail load | Sharing a location not enabled |
| Live location | Explicit unavailable-tracking explanation, no coordinate projection | Not enabled |
| Lottie, business interactive types and unverified envelopes | Remain unsupported | Not enabled |

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

Normal V1/V2/V3 text polls retain only the question in `text` and a new `poll.options` array of plain strings. Provider raw, encryption keys, correct answers, context and vote/results data are not retained. The public API and UI validate the same allowlist again. The question also supplies existing previews, search and incoming quote snippets; options have no vote controls or counts.

Product display bounds are 2,048 UTF-16 code units for the question, 2–32 options and 512 code units per option. These are application safety bounds, not a claim about every WhatsApp version. Missing, invalid, sparse or oversized values get an explanation rather than a truncated poll. The queue byte budget includes all option text, including UTF-8 multibyte characters.

Newer V4/V5 envelopes, non-text/image polls, quizzes/correct-answer payloads, vote updates and result snapshots keep explicit fallbacks. Temporary/view-once checks run first, including inside V4/image-option wrappers. Deletion/restriction clears question/options; replay cannot resurrect them. A legacy generic placeholder can become a visible poll only when fresh permitted provider content supplies the question/options. Merely opening an old placeholder never recovers content.

The poll kind is receive-only: it is excluded from outgoing types and blocked at the real adapter even for runtime-invalid input. Poll actions are disabled because the original raw is intentionally not retained. Creation, voting and results require separate work.

This is an additive runtime table and optional encrypted-body metadata. Database schema version, dependency lockfile, installer, network access mechanism, permissions and deployment configuration are unchanged. Ordinary SQLite backups include the added table.

## Poll capability map and separate path forward

The real connector reports `polls: { identify: true, display: true, create: false, vote: false, results: false }` for the product. The pinned library's [tagged README](https://github.com/WhiskeySockets/Baileys/blob/v7.0.0-rc14/README.md#poll-message) documents creating polls upstream, but Yahad does not expose it. The bounded read-only display above does not imply working voting/results.

The pinned [event processor](https://github.com/WhiskeySockets/Baileys/blob/v7.0.0-rc14/src/Utils/process-message.ts#L689) comments out the poll-vote decryption/dispatch path. The [send-content types](https://github.com/WhiskeySockets/Baileys/blob/v7.0.0-rc14/src/Types/Message.ts) expose poll creation but no high-level vote send branch. The presence of protobuf types or a standalone helper does not establish a usable feature.

A separate poll project should:

1. Define receive-details, create, receive-results, vote and removal as separate capabilities. Define unavailable states explicitly.
2. For results/voting, evaluate a maintained upstream implementation/version that supports the required events and verified PN/LID behavior. Compare migration, compatibility, privacy and maintenance costs in an isolated branch before changing the pinned dependency.
3. Add deterministic synthetic fixtures for creation variants, multi-choice limits, changed/removed votes, duplicates, out-of-order history, restart, missing origins, revocation and temporary messages.
4. Arrange separately authorized provider/device testing before claiming interoperability.

This change does not re-enable commented upstream code, add custom vote cryptography, alter `node_modules`, switch libraries or connect a real account.

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
