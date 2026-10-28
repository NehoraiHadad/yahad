# Per-child daily usage budgets (next-version feature branch)

Based on released v0.5.2, `4c41d5ef80ea349d0f678ee9c572e4975a2d776d`. The application version is deliberately unchanged for parent-coordinated integration; assign the next release version before packaging. No installer implementation or release assets are changed here.

## Parent and child behavior

In the selected child's workspace, the parent can enter any whole number of minutes from 0 to 1,000,000, select unlimited, inspect today's used/remaining/bonus minutes, and add minutes for today only. Existing children and newly created children are unlimited until the parent saves a limit. Zero means no base allowance; a same-day bonus can still provide time. All metered use during the current calendar day, including use while unlimited, appears in the daily summary and counts if a limit is subsequently set.

The parent explicitly selects the family timezone from a Hebrew dropdown before metering or enabling a finite quota. Its first named option is Israel/Jerusalem with automatic daylight saving; there is no preselected silent UTC choice and no IANA text entry. Unconfigured unlimited children remain usable without being metered. An old limited schema-3 family whose calendar was never explicitly chosen is suspended with a parent-setup explanation until the parent chooses. Existing recorded usage/bonus is retained.

Timezone is not permanently fixed. Once use or a bonus exists, a change is scheduled for the first midnight in the target zone at or after the old calendar's next midnight. The current allowance/bonus is retained through that boundary, extending the transition period rather than granting two resets within a short day. The parent sees the effective target date/time and this explanation. At the boundary a new calendar-era/day key begins; old bonus IDs cannot be replayed into the new period even when a visible local date repeats. Repeated requests for the same pending change retain its boundary; later changes first settle every child, then compact the completed calendar rule. Ordinary periods reset at family local midnight, including DST. Browser locale/timezone never chooses a billing day.

The child sees a compact MM:SS remaining-time indicator (H:MM:SS for longer allowances). Its display projects server snapshots using a monotonic clock only while active and freezes while paused. Visual ticks have `aria-live="off"`; a separate polite warning changes only at the two-minute and one-minute thresholds. Exhaustion pauses chat access while keeping the session and quota controls available. A parent extension can unlock the same open window without login and without changing the recurring quota. Each renewal must obtain both a live server lease and a fresh conversation-permission snapshot before content is revealed again. A confirmed revocation removes the conversation and its local draft.

An unsent local draft, attachment and recorded voice blob remain mounted through quota/network suspension; an active microphone recording is stopped. Renewal never sends a draft. Copied message-edit text is cleared during permission checks. Logout, account changes, leaving the conversation and refresh destroy local drafts; this feature adds no plaintext draft storage.

## Signals and billing contract

Browser signals are not proof of physical screen power or human attention. The browser sends an active signal only while visible, focused, online and within five minutes of recent pointer, keyboard, touch or scrolling activity. Reading counts during those five minutes without constant typing. Afterward, an explicit resume overlay is required. Hidden, blur and pagehide signals pause best effort.

The client first requests a server-issued per-mount tab UUID, then sends only that UUID, a strictly increasing sequence and an active boolean. It never sends elapsed usage, a day, or a clock for billing. Heartbeats run every five seconds; the server grants at most a 15-second lease, additionally bounded by session expiry and remaining budget. A missing response, offline signal or expired local lease hides cached content. A response must reconcile before reopening it. A fabricated browser client can lie about visibility/attention; this is an app usage budget, not a tamper-proof device-management agent.

Each policy has a durable settlement cursor. Under `BEGIN IMMEDIATE`, the server first settles the union of the child's outstanding leases, splits the interval at family midnight, and caps charges at that day's allowance plus bonus. Since each activated lease starts at the previous settlement cursor or earlier, the union ends at the maximum activated lease expiry, not the sum. Parent/sibling/family identities are never billed together. Session hashes and tab IDs bind leases. Retained identities preserve sequence after pause/expiry so duplicate or reordered requests cannot renew or cancel a newer lease.

Admission retains at most 32 rows per authenticated session, covering live leases and pending admissions, rather than limiting the lifetime number of mounts. It settles first, then replaces one paused/expired identity with a freshly generated UUID. Heartbeats cannot insert identities: every heartbeat for a replaced UUID is rejected, even with a higher sequence, so removing its tombstone does not permit replay. A pending admission uses `seq=-1` and a reservation deadline of at most 15 seconds; it grants no content lease and is excluded from settlement. Lost admission responses expire without billing. Pending admission is invalidated on restart; existing lease sequences are retained. The first valid heartbeat activates a reservation. Mounts serialize admission; hidden mounts request no slot. After replacement, the client obtains fresh admission and reconciles permissions automatically. At 32 genuinely live/reserved slots, the UI explains that another window must be closed and retries without logout. Other server refusals are distinguished from network failure. This changes the unreleased browser/server heartbeat protocol; deploy their matching build together, and reload older feature-preview clients.

Policy changes, bonus grants, session revocation and graceful shutdown settle before changing authority. An abrupt browser crash, lost pause, network loss or process crash can charge at most the last 15-second lease tail. Graceful shutdown settles immediately; restart settles only any bounded outstanding tail and clears leases. A backward server clock fails closed until it catches up; a forward jump cannot charge beyond the persisted lease. Client clocks cannot add time or reset a day.

The ledger contains family/child IDs, calendar day, aggregate used/bonus milliseconds, policy, sequence, lease expiry and hashed session identity. It contains no message contents, raw session tokens, interaction text or raw input logs. Expired-session lease rows are settled before periodic removal. Bonus request IDs are persisted with their exact child, minutes and calendar day; retries cannot apply the grant twice or regrant yesterday's bonus today.

## Server integration points

Clock faults block child content while keeping startup, parent recovery, logout and session revocation available. Revocation/restart uses an already-settled high-water mark when the clock is behind, clears leases, and never grants future usage. A bonus retry acknowledged after midnight explains that it belongs to the previous day; it does not claim a new bonus today.

- `src/usage.ts`: settlement, policy, bonus, family timezone, session and restart operations.
- `src/usage-schema.ts`: sequential transactional schema-2 to 3 to 4 to 5 migrations and composite-family foreign keys.
- `src/server.ts`: central `/api/groups` request/response gates and group authorization; async send/media/photo rechecks; typed `403 USAGE_EXHAUSTED` / `USAGE_PAUSED`, distinct from logout `401`.
- `GET /api/usage`, `POST /api/usage/open`, `POST /api/usage/heartbeat`: authenticated-child status, bounded server-issued tab admission and lease signals.
- `GET/PUT /api/parent/usage/:child`, `POST /api/parent/usage/:child/bonus`, `PUT /api/parent/usage-timezone`: same-family parent management.
- `/api/me`, activity, logout/password recovery and restricted SSE remain reachable. Suspended SSE omits group invalidations but retains ready/heartbeat/quota controls and session expiration.
- `web/useUsageBudget.ts`, `web/UsageBudget.tsx`, `web/ChildUsageSettings.tsx`: client lifecycle, overlays and parent controls. `web/main.tsx` binds permission snapshots to the current lease activation while preserving the mounted Composer.

Requests cover lists, messages, search, history, photos, media/ranges, sends/uploads, edits/deletes, reactions, read/viewed and typing. After asynchronous work, routes recheck before later side effects or disclosure. An already-started provider operation cannot be recalled when the budget changes; a signature-marker send that began while authorized may complete, but a later attachment send is blocked. Provider ingestion for the parent's retained history continues independently of child access.

## Schema and release coordination

`DATABASE_SCHEMA` is 5 and migration sources are `[1,2,3,4]`. Historical account-name migration is pinned to 2, historical usage migration to 3, calendar migration to 4, then admission policy to 5. Schema 5 prevents schema-4 binaries from interpreting pending admission reservations as billable content leases. The 4-to-5 migration only advances the policy boundary; existing lease sequences, calendars, usage, accounts, grants, messages, credentials and sessions are preserved. Schema-3/4 smoke fixtures retain explicit timezone, used time, bonus and policy; schema-4 also preserves a scheduled calendar transition. Recovery requires schema 5. Packaging imports the shared schema contract. Intermediate schema-2/3/4 binaries refuse schema 5.

Do not downgrade code against this migrated database. Use the updater's existing tested migration/backup recovery contract. Linux lifecycle, ownership, symlink, service and full rollback acceptance must still be run on Linux before release; Windows tests do not establish that acceptance. The installer task is separate and must consume the eventual integrated schema-5 release contract.

## Reproduction

Use Node 24 with the lockfile. `npm run typecheck`, `npm run build`, `npm test`, and:

```text
node --import tsx scripts/update-smoke.ts --database-schema 5 --database-migrations-from "[1,2,3,4]"
node --import tsx --test --test-concurrency=1 tests/usage-budget.test.ts tests/usage-client.test.ts tests/usage-parent.test.ts tests/chat-catalog-transitions.test.ts tests/offline-draft.test.ts
```

`tests/usage-preview.ts` is an optional disposable, loopback-only mock preview on port 4173 after building. It exposes test-only account entry and stop/bonus controls, never production startup. Its storage and credentials are freshly generated, and cleaned on normal shutdown. It must not be deployed.

The feature workspace stays at `C:\Users\nehor\Documents\Codex\2026-10-28\task-2\yahad`. At a clean integration checkpoint, fetch this local branch into the owner's preferred `C:\projects\yahad` checkout and inspect/cherry-pick the reported commits on a feature branch. Do not move uncommitted work, mix installer changes into this branch, or use the released data directory for smoke tests.
