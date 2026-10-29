# Eighteen baseline environment failures

Baseline: `586c8d489904be0cb7819bb2a7fa6d22d63d07de`. The exported ZIP comment and every listed test/source byte were checked against `git show`.
The five test files, `src/updates.ts`, and `src/diagnostics.ts` are identical in the baseline and feature worktree.
Baseline result: **6 passed / 18 failed** across 24 tests. Every baseline failure below has the same test title and symptom in the initial feature run.

Independent fresh synthetic filesystem probe on the same sandbox: `rename=EPERM`, `link=EPERM`, `symlink=EPERM`, `diagnosticsPersistenceError=permission`.
The two direct socket failures report `connect EACCES 127.0.0.1`; the third socket test fails in cleanup before its outgoing response exists.

[Machine-readable evidence with both failure excerpts, source hashes and raw-log hashes](child-access-environment-failures.json)

| # | File | Test | Observed symptom (baseline and feature) |
| --- | --- | --- | --- |
| 1 | `diagnostics-updates.test.ts` | diagnostics isolate families, sanitize failures, retain bounded encrypted history across restart | persisted snapshot count 0 instead of 2 |
| 2 | `diagnostics-updates.test.ts` | parent-only diagnostics/export/reset and operator-only update queue enforce sessions, family isolation and CSRF | update request was not published: queued/pending state absent or HTTP 500 |
| 3 | `shutdown-sse.test.ts` | open authenticated SSE drains before HTTP shutdown and immediately permits a backup | EACCES on loopback connect |
| 4 | `shutdown-sse.test.ts` | shutdown rejects SSE reconnects while preClose is running | EACCES on loopback connect |
| 5 | `shutdown-sse.test.ts` | already-ended backpressured SSE transport remains reachable during shutdown | cleanup reads outgoing.destroyed before outgoing was initialized |
| 6 | `update-mode.test.ts` | mode control requires designated parent, session, CSRF and strict mode-only input | update request was not published: queued/pending state absent or HTTP 500 |
| 7 | `update-mode.test.ts` | queued and root-journal pending requests never optimistically change applied mode or overwrite requests | ENOENT reading update-request.json after queue publication failed |
| 8 | `update-mode.test.ts` | root rejection is visible and a fresh request can be queued without changing effective mode | update request was not published: queued/pending state absent or HTTP 500 |
| 9 | `update-mode.test.ts` | request publication is complete/private and wakes without overwriting a competing request | update request was not published: queued/pending state absent or HTTP 500 |
| 10 | `update-mode.test.ts` | queue collision preserves a symlink target and cleans temporary files | EPERM creating symlink |
| 11 | `update-mode.test.ts` | root request journal exposes only actual stages and prevents concurrent submissions while running | update request was not published: queued/pending state absent or HTTP 500 |
| 12 | `update-mode.test.ts` | wake-link ENOSPC cannot report failure after an install request was published | update request was not published: queued/pending state absent or HTTP 500 |
| 13 | `update-mode.test.ts` | expired authenticated work is visible as a matching terminal failure, not success or continuing work | update request was not published: queued/pending state absent or HTTP 500 |
| 14 | `update-recovery-gate.test.ts` | HTTP recovery POSTs are conflict responses, GET is available, and normal terminal work remains queueable | update request was not published: queued/pending state absent or HTTP 500 |
| 15 | `update-target-binding.test.ts` | HTTP install echoes the displayed root identity in a private atomic queue and repeated clicks preserve it | update request was not published: queued/pending state absent or HTTP 500 |
| 16 | `update-target-binding.test.ts` | lost install response followed by reload and duplicate request retains the one durable target and request ID | ENOENT reading update-request.json after queue publication failed |
| 17 | `update-target-binding.test.ts` | older status and invalid root target remain readable but cannot silently queue an unbound install | update request was not published: queued/pending state absent or HTTP 500 |
| 18 | `update-target-binding.test.ts` | failed target capability needs a fresh check and recovery barriers dominate even a matching target | update request was not published: queued/pending state absent or HTTP 500 |

These tests were not weakened or edited. Environment restrictions were not changed.
The broad passing suite excludes all five files explicitly; the six passing tests in those files are not counted in that passing-suite total.
Original logs remain in `.local/all-node-tests.log` and `.local/baseline-environment-tests.log`; their SHA-256 values are recorded in the JSON.
The probe is additional evidence, not a claim that every secondary error was independently reproduced outside its test fixture.
