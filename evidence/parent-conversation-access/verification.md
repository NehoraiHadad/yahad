# Parent conversation access — synthetic validation

Baseline: local `e5accd0cbf22493d5cbac07dbfa5fdb0204c40f9`, exact published 0.5.0 tree `e8c3e6471ad6c4e96205fda378bbbb0bd26fa5c9`.

## Contract

- A parent can open, read, search, send, reply, react, use media and request available history for known groups in their own family, independently of child grants.
- A child still requires their own grant; family isolation, known-group boundaries, original-message ownership and privileged settings remain enforced server-side.
- Parent use creates no child grants. Child revocation denies subsequent child access immediately and sends a permission invalidation; it does not erase the parent's copy.
- Retention now covers every known same-family group. Startup initializes missing legacy keys without rotating existing keys. Group removal or explicit erase still purges local content/media/keys. Removal/re-add invalidates old ingestion/history/send generations.
- The filter narrows the parent's own list, not their identity. Full permission refresh remains fail-closed and retryable; ordinary message invalidation does not reset the composer. Repeated All clicks are inert.

## Verification scope

Synthetic API/security and executable React-handler tests cover ungranted-parent access, positive send/repeat/reply/reaction/edit/delete/read/typing/upload/download, child and foreign/unknown/private-JID negatives, family-isolated media, SSE scope, grant/revoke, legacy keys, retained parent content, removal/re-add races, async send response reauthorization, refresh failure/retry and repeated filter selection.

Final validation on Node v24.19.0: `npm run typecheck` passed; `npm test` passed 183/183 with zero failures/skips; `npm run build` passed; `git diff --check` passed. Independent review found no must-fix authorization issue and independently passed the 24 focused API/state/filter tests. Exact commit/tree are reported in the task handoff.

## Explicit limits

- Browser launch was attempted once with Chromium's sandbox enabled and failed before page creation: `socket() failed: Operation not permitted (EPERM)` / process singleton fatal; crashpad also reported missing database. No browser assertions or screenshots were produced; no escalation or sandbox bypass was attempted. Render/handler tests are not browser-layout QA.
- No real provider connection, WhatsApp data, desktop/Pi/remote writes, publication, deployment, updater/manual-package changes or schema-version changes occurred.
- Previously discarded/purged messages are not locally recoverable. Startup key initialization is not history recovery. History remains partial; requesting older messages requires a usable original provider anchor and does not prove delivery or completeness. Initial pre-catalog events can still be absent.
- All known-group retention increases local storage scope compared with child-grant-only retention. Existing media limits remain, and there is still no automatic age-based deletion of text.
