# Independent review of child access pause / resume

Reviewed commit: `a0c1e3b144de7476f923ff345ef1cfd506f1e2f7`.
Base: `586c8d489904be0cb7819bb2a7fa6d22d63d07de`.
Date: 2026-10-29. Worktree: `C:\projects\yahad-child-pause`.

An explicitly requested separate agent (`review_child_pause`) reviewed the code
and surrounding contracts without editing tracked files. The implementing agent
applied the fixes below. The reviewer inspected the fixes and independently ran
the four relevant regressions, all passing. No further blocking findings were
reported in the pause/resume scope.

## Findings and fixes

| ID | Severity | Finding in reviewed commit | Fix | Evidence |
| --- | --- | --- | --- | --- |
| PAUSE-R1 | P2 | A usage heartbeat authenticated before pause could resume after the parent transition, renew a zeroed lease and bill paused time. A synthetic reproduction returned 200 and added 5,000 ms while the account was paused and its session deleted. `src/usage.ts`, `src/server.ts` usage handlers. | `open` and `heartbeat` revalidate the originating SID inside their mutation transaction, before settlement or lease allocation/renewal. A pause followed by resume cannot make that deleted SID valid again. | New HTTP regressions hold the request in preHandler after authentication, pause (with/without resume), then release. Both admission and heartbeat return 401, create no lease and add no paused time. |
| PAUSE-R2 | P2 | `BaileysConnector.send` awaited audio duration or thumbnail preparation after the server authorization check. A pause could finish before the subsequent `sock.sendMessage` began. `src/connectors/baileys.ts`. | Optional synchronous `Outgoing.beforeSend` is supplied by both server message/marker paths. Baileys calls it after local media preparation immediately before provider submission; Mock calls it before scheduling effects. It closes over the original SID. | The actual adapter with stub transport starts image preparation while authorized, is paused during the await, and performs zero provider submissions. Server regressions cover delayed preparation with pause and pause/resume. |
| PAUSE-R3 | P2 | `tests/test_release_line_endings.py` still expected schema 5 / migration sources 1–4, so the new schema-6 packager contract would fail its test. | Updated assertion to schema 6 / sources 1–5. | Both offline packaging tests passed, including exact emitted manifest fields and rejection of noncanonical privileged source before output. |

The first regression run after introducing `beforeSend` exposed an interaction
with the existing signature-marker catch: it converted a quota `UsageError` into
502. The catch now preserves `UsageError`, retaining the existing typed 403
contract. The existing photo/marker expiry regression passed in the rerun and in
the independent reviewer's run. This is preservation of existing behavior, not
a change to the separately pending end-of-timer UX bug.

## Other reviewed areas

- Schema 6 is a policy boundary; old schema-5 code refuses the upgraded database.
  Migration sources, fresh database initialization, rollback on migration/storage
  failure and the matching local parent recovery script remain consistent.
- Parent role, family ownership, strict request body, origin and CSRF are enforced.
  Child, foreign-parent, missing session, wrong CSRF and wrong origin cases fail.
- Access revisions serialize transitions and permit safe duplicate retries without
  revoking newly created sessions. A stale opposite request cannot undo a later
  transition. Login checks its pre-verification revision again after the await.
- Pause settles the existing usage union, revokes leases and sessions atomically,
  preserving password hash, grants, sender signature, policy, consumed time and
  bonuses. Exhaustion survives pause/resume; a bonus does not bypass a pause.
- SSE immediately expires revoked child sessions. Successful pending content
  responses recheck the session; old cookies stay invalid after resume.

## Final validation

The reviewer independently ran **4/4** relevant regressions. The implementation
rerun of child access, actual connector/stub transport and usage tests passed
**41 tests**, with **2 existing Linux-only skips** and no failures. Python
recovery plus offline packaging passed **12/12**. TypeScript, migration smoke
(schemas 1–5 to 6), the actual older-schema rejection proof and frontend build
were rerun successfully. The final broad suite passed **296 tests**, with
**2 existing skips**, **0 failures** and **0 cancellations** (298 tests total),
excluding only the five unchanged environment-dependent files detailed below.

All test families, messages and provider transports were synthetic. No installer
or real household data was accessed. The main checkout and first-install worktree
were checked clean; the latter remains at
`bd4c50093df7ffee0446c6730f38684b88bda812`.

## Eighteen baseline failures

[Per-test table](child-access-environment-failures.md) and
[machine-readable evidence](child-access-environment-failures.json) include all
18 exact test names, baseline/feature failure excerpts, source hashes and log
hashes. The exported base archive's embedded commit matches the base above;
seven relevant source/test files were byte-compared against `git show` and the
feature checkout. The baseline result was **6 passed / 18 failed**, across the
five files explicitly excluded from the passing broad suite. Those tests were
not relaxed or edited.

A separate fresh synthetic probe found `rename=EPERM`, `link=EPERM`,
`symlink=EPERM`, and diagnostics persistence reported `permission`. The two direct
socket failures log loopback `EACCES`; the third fails during cleanup before its
outgoing response exists. The report distinguishes direct errors from inferred
secondary cleanup causes. These findings support the environment explanation
without claiming that the complete suite is green on Windows.

## Integration and remaining limits

The handoff directory `.local/handoff` contains a Git bundle and numbered
binary-capable format-patch files against the exact base. Import into a separate
integration branch/worktree and reconcile any overlapping CONNECTION-01 changes
in `src/server.ts`, `web/main.tsx`, `web/FamilyProgress.tsx` or `web/style.css`.
Do not apply this over an installer payload or an active household checkout.

The candidate retains package version 0.5.3 while developing schema 6. Before any
publication, the integrator must choose a new release version and perform the
normal release checks. No push, PR, merge, release, Actions or deployment was
performed here. Installer work, permissions and real WhatsApp remain untouched.

Once an operation has been handed to the provider it cannot be recalled reliably.
The new guard closes the local preparation window before that handoff. Revoked
session cancellation of a send still uses the conservative existing 502
`SEND_UNCERTAIN` response; this is a documented UX limit, not an authorization
bypass. The separate end-of-timer bug was not changed. Linux updater execution,
real provider behavior, native installation and physical tablets remain outside
this synthetic validation.
