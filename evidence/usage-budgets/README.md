# Synthetic usage-budget verification

Tested locally on Windows with Node 24.22.1, Python 3.13.7 and generated mock data. No real WhatsApp, household data, Pi, machine security changes, deployment, Actions or publication were used.

| Check | Observed result |
| --- | --- |
| TypeScript | `tsc --noEmit` passed |
| Production client build | Vite build passed |
| Complete frozen Node suite | 287 tests: 281 passed, 4 failed, 2 skipped |
| Four remaining Node failures against untouched `4c41d5e` | Exactly the same four failures reproduced (14 passed / 4 failed in the corresponding 18-test baseline subset) |
| Update smoke | `SMOKE_OK` for fresh storage and sequential schema-1/schema-2 to 3 fixtures |
| Released binary policy boundary | Actual baseline account-migration code rejects an in-memory schema-3 fixture (`BASELINE_REJECTS_SCHEMA3`) |
| Portable Python selection | 16 passed: schema manifest/transition/health contracts, recovery, release archive/schema assertions |
| Additional Python attempt | Schema-reader assertions completed, but its temporary SQLite file could not be removed on Windows (`WinError 32`); full Windows updater adapter also encounters POSIX-only fixture/import limitations. No Linux lifecycle acceptance is claimed. |
| Browser UI, 1280×720 desktop viewport | Parent saved 37 minutes; child indicator rendered; zero policy masked chats; parent bonus restored the same unsent draft without sending. These are browser checks, not physical-screen or tablet checks. |

The four Node failures are existing POSIX assumptions on Windows, not relaxed here:

- `shutdown-sse.test.ts`: private backup directory requires mode `0700`.
- `update-mode.test.ts`: request-file mode is reported as `0666` rather than `0600` on Windows.
- `update-mode.test.ts`: synthetic symlink creation returns `EPERM` without OS symlink privileges.
- `update-target-binding.test.ts`: the same `0666` versus `0600` mode assertion.

New tests exercise arbitrary/zero/unlimited policy, per-child and cross-family ACLs, union billing across sessions/tabs, ordered/duplicate/stale heartbeats, invalid elapsed-time input, exact expiry, offline/crash/restart tails, clock rollback with available logout/revocation/startup, midnight/DST, bonus retry/day binding and correct next-day feedback, central content/range/action gates, asynchronous photo and attachment-marker races, SSE quota control, migration rollback, graceful shutdown, client idle/focus/network behavior, LAN HTTP UUID compatibility, parent double-click/lost-response/unmount guards, permission renewal and existing draft/microphone interruption tests.

The screenshots contain only generated local mock data:

- `parent.jpg`: saved parent policy and daily summary.
- `expired.jpg`: child quota overlay while the session stays signed in.
- `draft-resumed.jpg`: restored local draft with zero messages in the conversation; no autosend.

Raw local logs remain in the ignored `.local/` directory: `verified-tests.log` (final full suite), `baseline-windows.log`, `final-focused.log`, `clock-tests.log`, `parent-verified.log`, `build-verified.log`, `quota-python.log` (the attempted reader fixture) and `quota-python-selected.log`. An earlier broad Windows updater adapter attempt ended with 351 tests, 7 failures, 135 errors and 130 skips; its `python-tests.log` is retained. That run included a since-corrected stale schema-2 packaging assertion and many POSIX-specific fixture/import limitations, and is not claimed as passing acceptance. Full Linux updater/rollback acceptance, performance on the target device and real-device focus/suspend behavior remain integration checks.
