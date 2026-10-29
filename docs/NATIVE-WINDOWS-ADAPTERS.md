# Windows authority adapters: code-only checkpoint

The concrete adapters compile, and the private-root test path connects the
catalog, owner ledger, runtime gate, existing transaction writer and actual
installed consumer. Neither production entry is enabled. No service, pipe
listener, token impersonation, installed role ACL or owner delegation is created
on this host. Synthetic principals exist only in test inputs/private storage.

## Implemented path

`Yahad.Authority` is a real compiled executable whose normal entry rejects use;
only `--describe` is supported. `InstalledAuthorityReader` contains the fixed
installed anchor/token reader. `InstalledFileRoles` checks exact owners, service
SIDs, protected DACLs and per-class masks. Role-aware read and exclusive-data-
lease methods preserve the existing pinned path, no-follow and single-link
checks. Existing single-identity readers retain their original defaults.

The bounded protocol accepts request IDs, actions and exact target digests; it
rejects unknown/duplicate fields and frames over 32 KiB. Owner answers contain
challenge ID, operation digest and approve/deny only. Native peer code implements
synchronous identification and mandatory reversion using actual Windows APIs.
Its production factory fails fast on unconfirmed reversion. Tests inject OS
observations and never call that native API or open a pipe.

The private owner ledger uses exclusive file leases and flushed create-only
records. A partial record fails closed. Principal and scope come from the trusted
peer boundary, not client fields. Challenges expire and are single-use; denials,
revocation and lost/partial records cannot mint approval. Reconciliation retains
the original approval identity/time. Manual interrupted work requires a fresh
native reconciliation approval. The writer rechecks the live approval before
each protected-record publication, including the final commit receipt.

Manual work creates one native challenge. The native-status branch of the
existing parent panel submits directly instead of presenting a second web
confirmation. Legacy workers retain their existing web confirmation. An
`awaiting_local_approval` status remains active and prevents duplicate submissions;
lost request replies are not automatically retried. The new server transport is
trusted in-process dependency injection, with no request/environment switch to
enable it. Default production composition remains unchanged and unconfigured.

## Automatic updates preserved through bounded delegation

The owner can approve one exact delegation document in synthetic tests. Its
scope binds installation, owner, data selection, current key/policy/pin digests,
committed recovery selection, schema, HTTPS origin, channel, version range,
maximum payload bytes, expiry (at most 90 days) and operation count (at most 100).
Registration checks the actual current activation/bootstrap/epoch/web anchors;
even an owner confirmation cannot use this path to replace existing key/policy/
pin anchors. No delegation is registered by default or on this host.

A routine update must increase version within those bounds and preserve the
same data, key, policy/pins, origin, schema and recovery receipt. Recovery scope,
new selection/receipt, downgrade and bound changes require a separate approval.
Budget is durably reserved before grant; failure never refunds it implicitly.
Reopened ledgers retain usage. A still-valid, unrevoked delegation can renew the
same interrupted operation for bounded reconciliation without another native
prompt or extra operation budget. Revocation/expiry still blocks it.

The writer now distinguishes a new `recover` operation from routine activation
of an already committed recovery selection. It permits only the identical old
recovery proof for `activate`, and uses committed-evidence checks rather than
requiring an unchanged original restored database after legitimate Store writes.
New recovery still requires the actual producer receipt and plaintext checks.

The existing Linux queue, automatic mode and policy/pins are not changed. Native
set-mode is modeled as a policy request; it does not optimistically change the
observed mode or create delegation. No repeated UAC is part of routine updates.

## Runtime gating and evidence limits

`RuntimeAuthorityGate` refuses launch until the exact generation is assigned and
observed in the authority's job. Stale/dead hosts and replayed generations are
refused. Quiescence requires both retained-host exit and observed empty job.
`GatedRuntimeStartAuthorizer` wraps the actual existing controller/Store launch.
The native kernel adapter compiles real assignment, membership, job-query and
process-wait calls; tests use synthetic observations for cross-service behavior.

Integration tests use real private files, actual SQLite/backup receipts and the
existing mutable Store/process adapters, with synthetic authorization. They cover
manual approval, wrong principal/digest/replay, durable reconstruction, revocation
during publication, delegated repeats/budget/expiry, every scope expansion,
automatic interrupted reconciliation and already-restored families after writes.
Web tests cover native/legacy confirmation selection, automatic mode, pending
status, exact targets and uncertain replies. No visual UI or real token/service
authentication acceptance is claimed.

Remaining production code: pipe listener/connection lifetime and mutual endpoint
authentication composition, native confirmation window, trusted release-intake
catalog publisher, installed-role writer/consumer factory and actual SCM/outer-
job bootstrap composition. The installed anchor reader and OS adapters are
compiled but not exercised under actual service accounts. The private catalog's
version/channel/intake facts are synthetic trusted inputs, not verified public
release intake. Developer package signatures cannot substitute for that step.

After those pieces are built, freeze all new hashes into the one blocked
`installer/windows/authority-vm-acceptance.json` proposal. Identify a disposable
Windows VM/snapshot and test SIDs, then obtain approval for that exact manifest.
No VM or prior approval is assumed. Actual ACL/token/identification, SCM and
cross-account/nested-job behavior, native confirmation, clean installation,
reboot/power-loss recovery, full dependency-bundle rebuild and HTTPS/LAN handoff
remain unaccepted. The older app/service archives predate these source changes
and must not be presented as the new complete installer.
