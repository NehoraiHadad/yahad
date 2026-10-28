# Synthetic usage-budget verification

Next-version feature branch, based on released v0.5.2 commit `4c41d5ef80ea349d0f678ee9c572e4975a2d776d`. Tested locally on Windows with Node 24.22.1, Python 3.13.7 and generated mock data. No real WhatsApp, household data, Pi, machine security changes, Actions, publication or deployment were used.

| Check | Observed result |
| --- | --- |
| TypeScript | `tsc --noEmit` passed |
| Production client | Vite build passed |
| Full Node suite after review | 298 tests: 292 passed, 4 failed, 2 skipped |
| Focused badge/parent/lifecycle/draft tests | 17 passed, zero failures/skips |
| Baseline comparison | Same four failures reproduced against untouched `4c41d5e`: 14 passed / 4 failed in the corresponding 18-test subset |
| Production update smoke | `SMOKE_OK`, fresh storage and sequential schema-1/2/3 to 4 fixtures; schema-3 retains timezone/policy/usage/bonus |
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

Remaining integration checks: full Linux updater/backup/rollback/services/ownership/symlink suite, independent code review, target-device performance and real-device focus/suspend. No installer work is included. Existing installer branch must consume the integrated schema-4 contract. Nothing is released in v0.5.2.
