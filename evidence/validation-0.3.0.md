# 0.3.0 validation receipt — 2026-10-26

Repository: `NehoraiHadad/yahad`, branch `improve/upgrade-onboarding`.
Base inspected: `d40b83db984cef03d4cdf7a5fe8a90f3a38ef3f0`, including the account
recovery commit after baseline `ff8728bd499d98a33afe1f0359a86f30837be2b3`.
Remote inspection found only main and no open PR before work; main was checked
again before delivery. The final GitHub head SHA is recorded in the draft PR.

## Passed

Disposable Linux **amd64** container, official `node:24.22.1-bookworm`, digest
`sha256:6449e19d8184d84062525b6a7524def03146c2cb04a7f08b076680d0e8e4240a`.
Source mounted read-only and copied into temporary container storage. Python,
OpenSSL and ffmpeg installed inside that disposable container. No host/Pi data,
keys or credentials mounted. Container removed after completion.

| Check | Result |
| --- | --- |
| `npm ci --ignore-scripts --no-audit --no-fund` | passed, locked dependencies |
| `npm run typecheck` | passed |
| `npm test` | **40 passed, 0 failed, 0 skipped** |
| `python3 -m unittest discover -s tests -p 'test_*.py' -v` | **20 passed, 0 failed, 0 skipped**, including root fixtures |
| `npm run build` | passed |
| `npm prune --omit=dev --ignore-scripts --no-audit --no-fund` followed by `node --import tsx scripts/update-smoke.ts` | `SMOKE_OK` |
| Final Windows Node 24 type check/build after UI label/button placement | passed |
| `git diff --check` | passed |

The Python checks cover real Ed25519 verification/tamper detection, unsafe ZIPs,
the unprivileged dependency exporter, real kernel lock contention and release
after process death, Node retaining the inherited lock, and parent-reset data
preservation. Updater install/rollback tests inject build/network/systemctl
effects into root-owned temporary fixture trees; they do not run actual systemd.

Application tests cover single-claim/expired bootstrap, parent-only child
creation, password confirmation/session revocation, buffered Unicode input and
terminal multiline-paste non-echo, stable group IDs/search/hidden grant state,
idempotent approvals, and authorization/family isolation. Disconnect tests cover
provider send/failure/timeout, local revocation before network wait, retained
history, separately explicit deletion, late QR/auth/media/group-refresh events,
reboot no-reconnect, deliberate relink, and pending-deletion reconnect refusal.
The pinned Baileys logout only sends an unlink request; tests explicitly refuse
to call that remote revocation confirmation.

## Browser checks with synthetic local data

Local mock harness only; it is outside the repository and is not shipped.
Checked first-run form labels/layout, parent child-creation entry, group search,
clear, no-results state, approval saved while filtered, and preserved approval
count across filters. Used 54 synthetic groups. Measured list `overflow-y:auto`,
360 px high at 390×844 mobile and 420 px at 1024×768 tablet; the mobile parent
grid became one column with no horizontal overflow. Restored browser viewport.
Confirmed explicit mock disconnect dialog, then disconnected the mock and saw
the retained-history status and existing group approval. No real account used.

## Platform restrictions and remaining gates

Initial Windows sandbox attempts hit `EPERM` in Vite/diagnostics and interpreter
resolution/network restrictions. Authorized local reruns outside the sandbox
resolved those restrictions. Two Windows application cases remain explicitly
POSIX-only (private-mode backup and setup). The portable Windows Python harness
does not emulate Linux ownership/flock/systemd acceptance. Those Linux/root
fixture cases were subsequently run successfully in the isolated Linux container;
there are **no skips in the authoritative Linux receipts above**.

**Never run / still pending:** actual Pi ARM64 installation, native systemd boot
ordering/recovery/timers, filesystem durability under real power loss, deployment
against the live store, real provider unlink/relink or end-to-end group/media
acceptance, and an owner-operated off-device restore drill. Earlier owner-reported
Pi 0.2.0 health/sign-in/test results are not acceptance of this 0.3.0 candidate.

Two independent internal reviews covered updater/systemd/trust and account/UI/
disconnect behavior. Findings were fixed with regression tests: privileged
builder path races/signed-source preservation, secret TTY paste echo, recovery
Unicode/status reporting, stale media/group refresh after disconnect, accurate
remote unlink semantics, and interrupted-deletion reconnect state. Final SHA
acknowledgments are recorded with the delivery receipts.

No Pi access, deployment, live WhatsApp operation, Actions activation, persistent
signing-key generation, security/credential changes or secret publication was
performed. Owner approval is still required for merge, signing trust/backup
secret provisioning, signed release publication and notify→auto activation. See
[the practical owner runbook](../docs/AUTOMATIC-UPDATES-SETUP.md).
