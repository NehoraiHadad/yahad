# 0.3.2 candidate validation - 2026-10-26

Branch `improve/update-mode-control`, based on merged/released 0.3.1 commit
`040f2c574231f3432b444e07162e2fbc285dfac5`. Final exact SHA is in the Draft PR.
No merge, new release, Pi execution or credential provisioning is included.

## Passed

Local Ubuntu WSL Linux x86_64, Node **24.19.0**, npm **11.17.0**, Python 3.12.3,
OpenSSL; source copied to a new temporary Linux directory, no live data mounted.
Application commands ran as the ordinary local Linux user; Python ownership/
lock/config cases ran as root inside temporary fixtures. Systemd/build/network
effects in worker lifecycle cases are injected, not actual service operations.

| Check | Result |
| --- | --- |
| Locked `npm ci --ignore-scripts --no-audit --no-fund` | passed |
| TypeScript typecheck | passed |
| Full app suite | **70 passed, 0 failed, 0 skipped** |
| Final Linux Python worker + owner-setup suite | **63 passed, 0 failed, 0 skipped** |
| Production Vite build | passed, `index-DDnY9nS5.js` |
| Production-only prune + update smoke | **SMOKE_OK** |
| Windows Node 24.22.1 focused API suite | **4 passed, 0 failed, 0 skipped** |
| Diff whitespace check | passed |
| Two-asset HTTPS packaging + manifest/hash/safe extraction | **HTTPS_PACKAGE_OK**, no key/network |

[Full app/build/smoke output](mode-control-app-0.3.2.txt) and
[final Python output](github-https-python-0.3.2.txt) preserve concrete receipts.
Application sources are unchanged from tested commit `04b5436d`; the later
owner-approved HTTPS trust change touches the root worker, release packaging,
guarded installer, Python tests and docs. The application/build/smoke receipts
remain applicable without unnecessary repetition. [HTTPS validation details](github-https-validation.md)
distinguish the final 42 worker and 21 owner-setup tests from the prior 34-worker
receipt retained for history.

Four new API cases cover designated-parent/family/role/session/CSRF checks,
strict mode-only input, old-worker compatibility, root-disabled/stale gates,
pending queue/root intent, non-overwrite behavior, rejection visibility and
confirmed effective mode. Fourteen new Python cases cover private/no-follow
queue handling, invalid/future/nonfinite/expired/replayed inputs, bounded replay
retention without evicting live IDs, mode-only root writes, closed local policy,
changed operator after journal/restart, recovery priority, intent write failure,
interruption before queue removal, config commit before acknowledgment, failed
acknowledgment persistence and retry, and changed-latest first-install refusal.

An initial API fixture double-closed its database; fixed the fixture and reran.
An old worker request fixture created a public-mode queue file; corrected it to
the private 0600 mode already used by the real API. Production ownership checks
were retained. Final results above have no failures or skips.

## Browser checks

Chrome, loopback-only mock app, synthetic parent/family and simulated worker
acknowledgments. Verified the unchecked auto switch, enable confirmation,
requested-auto pending with the effective switch still off, applied-auto only
after acknowledgment, pending-notify with switch still on, confirmed notify,
closed local setup gate, pending write failure without false success, explicit
rejection, current/available versions and lifecycle controls. Mobile viewport
390x844 showed no horizontal overflow (`clientWidth=scrollWidth=375`, excluding
scrollbar); viewport restored. No real provider or root config used in browser QA.

The final production build was also checked in Chrome with a separate synthetic
child/family and five fixture messages. Static WebP and generated animated WebP
both decoded (`naturalWidth` 256 and 16 respectively). Missing media, an old
empty-text record and a broken image URL each showed an explanation. No real
chat text, names or screenshot were used. See [sticker diagnosis and scope](../docs/STICKER-RECEPTION.md).

Two independent internal reviews covered root authority, mode-only persistence,
replay/recovery and UI pending semantics. Operator-journal binding and atomic
acknowledgment failures were fixed with regressions. Exact-SHA acknowledgments
are recorded with delivery after commit.

## Owner installer draft and compatibility gate

The consolidated `deploy/install-updater.py` draft was exercised in **21 isolated Linux
fixture tests**: real file modes under umask077, fixed-repository HTTPS verification, two
preparation passes preserving synthetic live env/key/database, no overwrite,
link/FIFO refusal, hidden escrow, default maintenance pause, uncertain stop,
cleanup-failure restoration, immutable streamed backup export, effective service
override rejection and acceptance gates. Service/account/network effects are
mocked. The installer entry point deliberately returns DRAFT_ONLY before any
action pending stable v0.3.2 publication/read-back and installer activation approval. It is not
certified for active Pi installation. No actual owner backup secret was created.

The new app with the published old 0.3.1 worker leaves the mode switch unavailable.
Root helpers do not self-update with the app. The matching mode-capable worker and
one-time local policy/setup must be provisioned before UI control is promised.
`installVersionPin` is root-only and optional; it protects the first manual target
and is cleared locally only after acceptance before normal automatic updates.

## Never run / pending

No Pi access, root provisioning, service stop/restart, timer enablement, live
WhatsApp action, new persistent signing key, backup secret, GitHub credential,
Actions enablement, merge or new release publication. Existing live paths remain
`/var/lib/yachad/live` and `/etc/yachad/whatsapp.key`. Actual Pi ARM64/native
systemd/interruption, off-device restore and live group/history/media acceptance
remain pending. The WSL Node version above is within the supported Node24 range;
it is not a claim of exact Pi24.22.1 native validation.

Remaining activation gate: review/approve this candidate, publish matching stable
v0.3.2 with manifest and archive, verify read-back, then approve/review installer
activation. The new owner profile requires no signing key or token; existing
signature-pinned installations keep their policy. Local maintenance and acceptance
gates remain required. The requested final automatic outcome is already authorized;
no redundant global auto-mode approval is introduced.
