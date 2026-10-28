# Per-child daily usage budgets (next-version feature branch)

Based on released v0.5.2, `4c41d5ef80ea349d0f678ee9c572e4975a2d776d`. The application version is deliberately unchanged for parent-coordinated integration; assign the next release version before packaging. No installer implementation or release assets are changed here.

## Parent and child behavior

In the selected child's workspace, the parent can enter any whole number of minutes from 0 to 1,000,000, select unlimited, inspect today's used/remaining/bonus minutes, and add minutes for today only. Existing children and newly created children are unlimited until the parent saves a limit. Zero means no base allowance; a same-day bonus can still provide time. All metered use during the current calendar day, including use while unlimited, appears in the daily summary and counts if a limit is subsequently set.

The family calendar defaults to UTC, is shown to parent and child, and can be configured using an IANA zone such as `Asia/Jerusalem` before the first recorded use or bonus in that family. It is fixed after that point. This intentional restriction prevents changing a reset boundary to recover time or replay a bonus; timezone changes after metering has begun are a future migration/product decision. Browser locale and timezone never choose a quota day. Resets follow calendar midnight in the stored family timezone, including 23/25-hour DST days.

The child sees a remaining-time indicator and a warning during the final two minutes. Exhaustion pauses chat access while keeping the session and quota controls available. A parent extension can unlock the same open window. Each renewal must obtain both a live server lease and a fresh conversation-permission snapshot before content is revealed again. A confirmed revocation removes the conversation and its local draft.

An unsent local draft, attachment and recorded voice blob remain mounted through quota/network suspension; an active microphone recording is stopped. Renewal never sends a draft. Copied message-edit text is cleared during permission checks. Logout, account changes, leaving the conversation and refresh destroy local drafts; this feature adds no plaintext draft storage.

## Signals and billing contract

Browser signals are not proof of physical screen power or human attention. The browser sends an active signal only while visible, focused, online and within five minutes of recent pointer, keyboard, touch or scrolling activity. Reading counts during those five minutes without constant typing. Afterward, an explicit resume overlay is required. Hidden, blur and pagehide signals pause best effort.

The client sends only a per-mount tab UUID, a strictly increasing sequence and an active boolean. It never sends elapsed usage, a day, or a clock for billing. Heartbeats run every five seconds; the server grants at most a 15-second lease, additionally bounded by session expiry and remaining budget. A missing response, offline signal or expired local lease hides cached content. A response must reconcile before reopening it. A fabricated browser client can lie about visibility/attention; this is an app usage budget, not a tamper-proof device-management agent.

Each policy has a durable settlement cursor. Under `BEGIN IMMEDIATE`, the server first settles the union of the child's outstanding leases, splits the interval at family midnight, and caps charges at that day's allowance plus bonus. Since each lease starts at the previous settlement cursor or earlier, the union ends at the maximum lease expiry, not the sum. Parent/sibling/family identities are never billed together. Session hashes and tab IDs bind leases; tombstones preserve sequence after pause/expiry so duplicate or reordered requests cannot renew or cancel a newer lease. At most 32 tab identities per authenticated session are retained; signing in again recovers if this bound is reached.

Policy changes, bonus grants, session revocation and graceful shutdown settle before changing authority. An abrupt browser crash, lost pause, network loss or process crash can charge at most the last 15-second lease tail. Graceful shutdown settles immediately; restart settles only any bounded outstanding tail and clears leases. A backward server clock fails closed until it catches up; a forward jump cannot charge beyond the persisted lease. Client clocks cannot add time or reset a day.

The ledger contains family/child IDs, calendar day, aggregate used/bonus milliseconds, policy, sequence, lease expiry and hashed session identity. It contains no message contents, raw session tokens, interaction text or raw input logs. Expired-session lease rows are settled before periodic removal. Bonus request IDs are persisted with their exact child, minutes and calendar day; retries cannot apply the grant twice or regrant yesterday's bonus today.

## Server integration points

Clock faults block child content while keeping startup, parent recovery, logout and session revocation available. Revocation/restart uses an already-settled high-water mark when the clock is behind, clears leases, and never grants future usage. A bonus retry acknowledged after midnight explains that it belongs to the previous day; it does not claim a new bonus today.

- `src/usage.ts`: settlement, policy, bonus, family timezone, session and restart operations.
- `src/usage-schema.ts`: transactional schema-2 to 3 migration and composite-family foreign keys.
- `src/server.ts`: central `/api/groups` request/response gates and group authorization; async send/media/photo rechecks; typed `403 USAGE_EXHAUSTED` / `USAGE_PAUSED`, distinct from logout `401`.
- `GET /api/usage`, `POST /api/usage/heartbeat`: authenticated-child status and lease signals.
- `GET/PUT /api/parent/usage/:child`, `POST /api/parent/usage/:child/bonus`, `PUT /api/parent/usage-timezone`: same-family parent management.
- `/api/me`, activity, logout/password recovery and restricted SSE remain reachable. Suspended SSE omits group invalidations but retains ready/heartbeat/quota controls and session expiration.
- `web/useUsageBudget.ts`, `web/UsageBudget.tsx`, `web/ChildUsageSettings.tsx`: client lifecycle, overlays and parent controls. `web/main.tsx` binds permission snapshots to the current lease activation while preserving the mounted Composer.

Requests cover lists, messages, search, history, photos, media/ranges, sends/uploads, edits/deletes, reactions, read/viewed and typing. After asynchronous work, routes recheck before later side effects or disclosure. An already-started provider operation cannot be recalled when the budget changes; a signature-marker send that began while authorized may complete, but a later attachment send is blocked. Provider ingestion for the parent's retained history continues independently of child access.

## Schema and release coordination

`DATABASE_SCHEMA` is 3 and migration sources are `[1,2]`. Historical account-name migration is pinned to 2; Store then runs the separate usage migration. Existing accounts, grants, messages, credentials and sessions are preserved, with unlimited policy. The production update smoke has synthetic fixtures for both sources. Recovery now requires schema 3. Packaging imports the shared schema contract; the release manifest assertion is updated. Schema-2 application/recovery binaries refuse a schema-3 database rather than ignoring its policy.

Do not downgrade code against this migrated database. Use the updater's existing tested migration/backup recovery contract. Linux lifecycle, ownership, symlink, service and full rollback acceptance must still be run on Linux before release; Windows tests do not establish that acceptance. The installer task is separate and must consume the eventual integrated schema-3 release contract.

## Reproduction

Use Node 24 with the lockfile. `npm run typecheck`, `npm run build`, `npm test`, and:

```text
node --import tsx scripts/update-smoke.ts --database-schema 3 --database-migrations-from "[1,2]"
node --import tsx --test --test-concurrency=1 tests/usage-budget.test.ts tests/usage-client.test.ts tests/usage-parent.test.ts tests/chat-catalog-transitions.test.ts tests/offline-draft.test.ts
```

`tests/usage-preview.ts` is an optional disposable, loopback-only mock preview on port 4173 after building. It exposes test-only account entry and stop/bonus controls, never production startup. Its storage and credentials are freshly generated, and cleaned on normal shutdown. It must not be deployed.

The feature workspace stays at `C:\Users\nehor\Documents\Codex\2026-10-28\task-2\yahad`. At a clean integration checkpoint, fetch this local branch into the owner's preferred `C:\projects\yahad` checkout and inspect/cherry-pick the reported commits on a feature branch. Do not move uncommitted work, mix installer changes into this branch, or use the released data directory for smoke tests.
