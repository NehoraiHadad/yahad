# 0.3.1 candidate validation receipt - 2026-10-26

Repository: `NehoraiHadad/yahad`, branch `improve/upgrade-onboarding`, Draft PR 1.
This extends the reviewed 0.3.0 commit
`1db8623f9ac75071dadf64ac17a94f4f462782ab`; all earlier updater, onboarding,
group-picker and disconnect work is preserved. Remote inspection before the
final commit found PR 1 open/draft/unmerged at that commit and main unchanged at
`d40b83db984cef03d4cdf7a5fe8a90f3a38ef3f0`. The delivered exact head SHA is
recorded in the PR and delivery response; this receipt is part of its tree.

## Final source validation

Disposable Linux **amd64**, official `node:24.22.1-bookworm`, image digest
`sha256:6449e19d8184d84062525b6a7524def03146c2cb04a7f08b076680d0e8e4240a`.
Read-only source mount copied into container-local `/work`; Python, OpenSSL and
ffmpeg installed inside the disposable container. No host/Pi data or keys were
mounted. All commands below completed with exit status 0; container removed.

| Check | Result |
| --- | --- |
| `npm ci --ignore-scripts --no-audit --no-fund` | passed |
| `npm run typecheck` | passed |
| `npm test` | **60 passed, 0 failed, 0 skipped, 0 canceled** |
| `python3 -m unittest discover -s tests -p 'test_*.py' -v` | **20 passed, 0 failed, 0 skipped**, including root fixtures |
| `npm run build` | passed; 1,585 modules transformed |
| Production-only dependency prune and `node --import tsx scripts/update-smoke.ts` | **SMOKE_OK** |
| Windows Node 24 focused `tests/history.test.ts` | **20 passed, 0 failed, 0 skipped** |
| Windows Node 24 typecheck and final production build | passed |
| `git diff --check` | passed |

[Full final Linux command output](linux-validation-0.3.1.txt) is retained with
this receipt. The build's final JS asset is `index-CzmGmTWU.js`. Documentation
and receipts added afterward do not change the tested source/dependency tree.

Initial sandboxed Windows Vite resolution returned `EPERM`; an authorized local
filesystem execution passed. This restriction was not treated as a test pass.
The authoritative Linux run above has no skipped ownership or POSIX cases.
Root lifecycle fixtures inject build/network/systemctl effects; actual systemd
and ARM64 hardware are not exercised by a container.

## History regression coverage

Twenty new focused cases cover authorized 600-message batches (all 600 handled),
3,000-message overload (2,048 admitted and explicit incompleteness), byte bounds,
consumer failure/stall/cancellation, disposal and reconnect, shared unresolved
consumer bounds, permission revoke/regrant epochs, duplicate chunks and records,
server-selected outgoing/received original anchors, malformed/deleted/unsupported/
pending anchors, bounded scans, family/group isolation, CSRF and strict client
input, failed/timed-out submissions and replies, request/rate limits, exact-ID
early/empty replies and actual stored counts distinct from request submission.

No unsupported anchor-free request is invented for a group approved after
connection. Status remains partial, including after a matching batch ends;
`fetchMessageHistory` returning an ID is not treated as restored content.

## Browser checks

Chrome against a loopback mock-only harness outside the repository, temporary
synthetic families/groups and synthetic anchors. No provider network connection.
Verified an empty newly approved group explains the missing anchor and disables
the request; a usable anchor enables it; submission shows waiting with zero new
stored records and a disabled repeat button; injected failure shows incomplete
status; 45 stored messages retain pending status while another local page exists
and after loading that older page. At 390x844, the empty explanation and button
fit without horizontal overflow (`clientWidth=scrollWidth=390`). Browser viewport
was restored. Earlier group-picker/onboarding/disconnect checks remain documented
in [the 0.3.0 receipt](validation-0.3.0.md).

Two independent internal reviewers inspected the final history changes and
reported no remaining concrete must-fix findings after cancellation and pagination
fixes. Exact-commit acknowledgments are recorded with the delivery response.

## Never run / owner acceptance pending

No Pi access, deployment, real provider history/unlink/relink call, Actions
activation, persistent signing-key generation, credential/security change or
secret publication. Actual Pi ARM64, native systemd/boot/interruption/durability,
off-device restore and live group/media/history acceptance remain pending.
The pinned README's seconds convention and the implementation's wire-field name
disagree; no unverified timestamp-unit change was made. Real phone timestamp
behavior and optional request-ID echo must be verified by the owner separately.
Provider allocation before our listener and sustained Pi memory also need real
acceptance. No claim of full WhatsApp history recovery is made.

Owner merge, signing/trust and backup-secret provisioning, signed release,
deployment and notify-to-auto activation remain separate approval gates. Follow
[the practical one-time runbook](../docs/AUTOMATIC-UPDATES-SETUP.md) and
[history limitations/acceptance](../docs/HISTORY.md). Existing live paths remain
`DATA_DIR=/var/lib/yachad/live`, `MASTER_KEY_FILE=/etc/yachad/whatsapp.key`.
