# Synthetic usage-budget verification

Next-version feature branch, based on released v0.5.2 commit `4c41d5ef80ea349d0f678ee9c572e4975a2d776d`. Tested locally on Windows with Node 24.22.1, Python 3.13.7 and generated mock data. No real WhatsApp, household data, Pi, machine security changes, Actions, publication or deployment were used.

| Check | Observed result |
| --- | --- |
| TypeScript | `tsc --noEmit` passed |
| Production client | Vite build passed |
| Full Node suite after review | 305 tests: 299 passed, 4 failed, 2 skipped |
| Focused quota/admission/lifecycle tests | 30 passed, zero failures/skips (final client UX recheck); previous badge/parent/draft selection also passed |
| Baseline comparison | Same four failures reproduced against untouched `4c41d5e`: 14 passed / 4 failed in the corresponding 18-test subset |
| Production update smoke | `SMOKE_OK`, fresh storage and sequential schema-1/2/3/4 to 5 fixtures; schema-3/4 retain timezone/policy/usage/bonus, schema-4 preserves scheduled calendar transition |
| Portable Python selection | 16 passed: schema manifest/transition/health contracts, recovery and release archive/schema assertions |
| Other Python limits | Schema-reader assertions completed but SQLite cleanup hit Windows `WinError 32`; broad updater adapter encounters POSIX fixture/import limits. No Linux lifecycle acceptance claimed. |
| Browser, synthetic desktop 1280x720 | Parent chose Israel explicitly, saved zero daily quota, granted one minute today. Child badge visibly counted 00:51, 00:47, 00:40, 00:26, 00:16, 00:01, then expiry overlay 00:00. Four-minute parent API bonus restored same session/draft automatically, with 03:55 on badge and zero messages sent. Parent showed daily quota still zero, total bonus five minutes. |
| Calendar UI | Hebrew dropdown, blank until explicit choice, finite quota disabled until confirmation; Jerusalem reset label. Later NY change displayed 29 Oct 2026 00:00 target time and preservation of current allowance/bonus until then. |
| Pause / accessibility | Unit lifecycle and monotonic-clock tests verify focus/hidden pause, freeze/resume without paused debit, server bonus reconciliation. Expiry overlay visibly says paused. Browser timer `aria-live` observed `off`; threshold announcements are separate. Creating an IAB tab did not reproduce a hidden event, so no physical-device pause claim. |

Exact baseline-reproduced failures (security checks were not weakened):

1. `tests/shutdown-sse.test.ts:89`: **open authenticated SSE drains before HTTP shutdown and immediately permits a backup** — `Use a dedicated private backup directory with mode 0700` (`scripts/backup-lib.ts:40`).
2. `tests/update-mode.test.ts:83`: **request publication is complete/private and wakes without overwriting a competing request** — assertion `438 !== 384` (`0666` versus `0600`).
3. `tests/update-mode.test.ts:95`: **queue collision preserves a symlink target and cleans temporary files** — `EPERM: operation not permitted, symlink` for the synthetic `preserve-fixture` -> `update-request.json`.
4. `tests/update-target-binding.test.ts:47`: **HTTP install echoes the displayed root identity in a private atomic queue and repeated clicks preserve it** — assertion `438 !== 384` (`0666` versus `0600`).

Exact skips, both in `tests/connector-backup.test.ts`, reason **Requires Linux private file modes; run on Linux before activation**:

- Line 198: **authenticated encrypted backup + restore of SQLite, encrypted media and WA auth; tamper/wrong password/retention/lock**.
- Line 376: **documented setup CLI creates real separate accounts and a protected random key without printing passwords**.

Tests cover arbitrary/zero/unlimited quotas, separate child/family ACLs, union billing across tabs/devices, duplicate/reordered heartbeats, stale pause races, server-clock/DST/midnight/rollback, restart and bounded network tails, central content/media/range/action denial, asynchronous send/media races, SSE renewal, migrations/rollback, mounted draft/attachment and microphone interruption, five-minute idle, double clicks/lost bonus responses/day binding/wrong-child stale handlers, explicit timezone setup and scheduled transitions/calendar-era bonus replay protection.

Current screenshots contain only generated mock data:

- `badge-active.jpg`: compact warning badge and unsent draft.
- `badge-expired.jpg`: zero-time overlay, authenticated session retained.
- `badge-extended.jpg`: same draft after automatic extension, without autosend.
- `calendar-parent.jpg`: parent zero recurring quota, five bonus minutes, future timezone-change explanation.

Earlier `parent.jpg`, `expired.jpg`, `draft-resumed.jpg` are historical synthetic screenshots from commit 7528370; the current screenshots supersede their calendar/badge presentation.

Ignored local logs: `.local/review-final-tests.log`, `.local/badge-parent-tests.log`, `.local/calendar-full.log`, `.local/calendar-python.log`, `.local/baseline-windows.log`, and prior verification logs. An earlier broad Windows updater adapter attempt ended with 351 tests, 7 failures, 135 errors and 130 skips; one stale schema assertion was subsequently corrected and many POSIX fixture/import limits remain. This is not passing Linux acceptance.

Remaining integration checks: full Linux updater/backup/rollback/services/ownership/symlink suite, independent code review, target-device performance and real-device focus/suspend. No installer work is included. Existing installer branch must consume the integrated schema-5 contract. Nothing is released in v0.5.2.


## Tab-admission regression fix

Independent Linux validation reported by the parent for the preceding snapshot `5386dce`: 298/298 JavaScript tests, typecheck/build and schema smoke [1,2,3] passed; Python aggregate 422 tests, 365 passed / 57 root-gated skips, no failures. That result belongs to the preceding snapshot, not this new fix. Its independent reviewer then reproduced the lifetime 32-mount cap; the passing old suite did not cover it.

The fix uses POST `/api/usage/open` to allocate a fresh server-issued UUID in one of at most 32 rows per session. Admission settles usage first and reuses a paused/expired identity. A heartbeat only updates an existing admitted row, and every replay for a replaced identity is rejected as `USAGE_TAB_EXPIRED`, including arbitrarily higher sequence numbers. Retained identities keep their ordering protection. Pending admission (`seq=-1`) reserves capacity for no more than 15 seconds but never bills or authorizes content. Restart invalidates pending admission without losing aggregate usage or sequence tombstones. A client silently reacquires admission and revalidates conversation permissions. Capacity failures have specific Hebrew copy; other server refusals are not mislabeled offline and clear stale quota presentation.

New regressions prove 64 sequential mounts/pause cycles (64,000ms total billed and more than 35 minutes still available), then replay every retired identity both active and paused without reinsertion. Other cases cover exactly 32 genuinely simultaneous leases, the 33rd admission blocked, capacity recovered by pause/expiry, session/child isolation, union billing, lost admissions expiring without charge, restart followed by 40 further mounts, one client admission in flight, late response after unmount, hidden mounts requesting no slot, accurate error reasons and renewal permission activation.

Schema 5 is necessary because schema-4 code includes pending reservation deadlines in its old lease settlement. The transactional 4-to-5 migration only bumps policy compatibility and preserves rows. The actual previous `5386dce:src/db.ts` guard was executed against an in-memory schema-5 fixture and rejected it before mutation: `SCHEMA4_BINARY_REJECTS_SCHEMA5`. Historical migrations remain pinned to 2, 3 and 4. Recovery, release packaging and all four smoke sources agree on schema 5.

Actual browser QA with the matching production build and disposable mock session showed the Hebrew window-capacity explanation, then automatic return to the same signed-in child without login after capacity became available. `tab-capacity.jpg` and `tab-recovered.jpg` contain only synthetic data. They are browser checks, not physical-screen/device acceptance. The final small UX adjustment hides stale quota status on a server refusal; focused lifecycle tests and a rebuilt browser capture verify it.

Final local logs: `.local/tab-admission-final.log` (full suite), `.local/tab-admission-focused-final.log` (final UX recheck), `.local/tab-admission-types.log`, `.local/tab-admission-build.log`, `.local/tab-admission-smoke5.log`, `.local/tab-admission-python.log`. An earlier full run was deliberately stopped when the new schema boundary was added; it is not used as the final result. Independent Linux validation of this new schema-5 commit remains pending.
