# Installed native admission and mutable Store handoff

The installed resolver and actual process/lease adapters are implemented and
exercised by isolated tests. The parameterless production resolver remains
explicitly denied until a real privileged committer identity and authenticated
owner channel are configured. Neither files nor CLI fields can enable it.
No production state is provisioned, and no keys are enrolled by this change.

## Installed authority boundary

The fixed ProgramData bootstrap descriptor supplies installation identity and an
exact activation-record SHA256. The resolver reads `activation.json`,
`epoch.json`, `owner-intent.json` and `web.json` through no-follow, single-link,
admin/System-owned, application-read-only handles. It retains those handles
through the application lifecycle. No CLI or environment option changes the
production root, boundary implementation or trust mode.

The epoch anchor must identify the same installation, exact activation digest
and epoch. Activation binds an exact offline `bundle.json` digest, selected data,
schema range, key/policy/pin file digests, owner-intent digest and web-config
digest. Owner intent must match installation, epoch, release, selection and
operation. Approval time cannot be in the future; an expiry, when present, is
checked again before mutable startup. A committed intent with explicit null expiry is a
persistent start capability for that exact epoch/selection, until the protected
authority revokes or replaces it. It is not a live browser session or a fresh
approval for another release.

The host consumes an OS-protected owner capability. A concrete private-root
transaction writer now validates and publishes those same records, including
interrupted reconciliation, against the actual consumer. It is exercised only
with synthetic state and an injected authentication boundary. It is not exposed
through the production entry, client JSON, environment or CLI. The remaining
authority decision is the real Windows committer identity, its protected file
ownership and the authenticated owner channel. See
[the transaction contract](NATIVE-PROTECTED-TRANSACTIONS.md). A UI Boolean or an
application-writable file cannot supply owner identity or scope.

The protected manifest digest is a local installed trust anchor. It is **not** a
claim that a publisher signature or GitHub transport was verified by this host.
The existing independently authorized distribution/update intake must establish
that digest. The code does not enroll an updater signing key, substitute a new
signature profile, or change update policy. A trusted administrator can replace
all protected state; this is not an anti-rollback boundary against that same
administrator or disk-snapshot restoration.

## Exact payload and selected data

The resolver consumes the actual Windows packager's full manifest format,
verifies member closure, bounded paths/count/bytes and every member hash, and
retains private file/ancestor handles. Node, FFmpeg, Caddy and the platform helper
must occupy the fixed private paths. The observed SQLite schema must match the
admitted range and the payload's schema ceiling. No schema version is changed.

An actual exclusive `process.lock` lease is obtained before source data is read.
No SQLite WAL/SHM/rollback journal is ignored or removed. The immutable SQLite
inspection applies only while the lease and no-write pins freeze the database.
Unknown or crash-left sidecars require a separate explicit recovery operation.
This slice does not create an initial database or reconcile an interrupted
activation; uncommitted/fresh/unknown state fails closed.

`web.json` supplies a secure external origin and private backend port. The host
starts the backend on loopback, with private FFmpeg and a clean environment. Real
provider activation remains disabled in this bounded adapter. Proxy/TLS startup,
browser handoff and second-device reachability are separate remaining installer
adapters; backend service readiness alone does not establish them.

## Actual backup/restore receipt bridge

`NativeRecoveryAdmission` consumes the existing native recovery producer's
authenticated backup envelope and chained restore journal, not synthetic
replacement signatures. It derives the same versioned authentication domain from
the selected master key, observes context/policy/pin identity, receipt digest,
ciphertext hashes, journal chain/complete state and restored member hashes.
The selected directory must be that operation's new `completed` directory.
Pre-commit `recovered` state requires exact original plaintext hashes and matching
schema, then returns `INSTALLED_RECOVERY_COMMIT_REQUIRED` without launching. The
protected publisher must commit the selection before mutable execution. After
that independent `committed` capability exists, the reader authenticates the
archive and complete journal while allowing active data to change normally. It
continues to check identity/policy/pins and the actual supported schema. This
avoids comparing a live mutable database to its original backup on every boot.
Tests start and stop the real restored Store twice against the same committed
capability without rewriting it.

The bridge performs no restore writes and never overwrites active data. It keeps
source/catalog/selected file handles through admission. The existing producer
still exposes a fixture-root API; promotion of its write executor to an
installed recovery authority remains separate. The bridge implements a real
reader for its actual output format. Its MAC establishes integrity in that key
domain, not an independent owner principal; the protected owner/epoch boundary
is still mandatory.

## Continuous mutable lease

The host releases read-only database pins only at its final mutable-start
barrier, while continuously retaining the exclusive OS gate. `JobProcess`
creates Node suspended and assigns its Job Object before any app code executes.
An exact retained child-process handle receives a noninheritable duplicate of
the gate before `ResumeThread`. A flushed private per-launch descriptor binds
the child PID, handle, data directory and random launch nonce.

The real TypeScript `acquireDataLock` path asks the private platform helper to
validate that descriptor, same-user process, actual file identity and observed
writer exclusion. It does not close/reopen the gate. The child retains its
duplicate until process exit, including after Store.close(). The host retains
its copy until full-job quiescence. An old supervisor/fork path cannot adopt a
descriptor for a different PID; the production launcher runs main.ts directly.
The app's local native-control client supplies readiness and shutdown on that
same root process. No public control endpoint is introduced.

[DuplicateHandle documents that the duplicate refers to the same object](https://learn.microsoft.com/en-us/windows/win32/api/handleapi/nf-handleapi-duplicatehandle).
[ResumeThread controls when the suspended child begins execution](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-resumethread).

## Measured full-payload startup

The rebuilt app payload is source `146857134c5ef326d3451ac5085b2da14edbef0d`:
9,301 files, 204 PE modules, 501,525,346 payload bytes. Its full verification
initially took about 177 seconds, exposing the old 25-second service admission
budget as insufficient. That earlier service archive is superseded.

Installed verification now runs on a cancellable worker, with a five-minute hard
admission/start budget. Checkpoints are emitted only after actual file reads or
observed enumeration work, at most once per second. No independent timer claims
progress for a stalled verifier. Checkpoint publication is serialized and detached
before terminal status; publication failure cannot become readiness. Health and
graceful-stop budgets remain separate and short. No global SCM timeout or other
machine setting is changed.

The actual full payload then passed the real controller and read-only start
barrier in **160.711 seconds with 149 START_PENDING checkpoints**. This test used
a simulated no-network launcher after genuine admission, not the app HTTP entry
or native SCM. Separate tests still launch the actual mutable Store through the
same lease/process adapters. Missing/expired authority and cancellation continue
to fail closed. Slow hardware, cold boot and actual SCM timing require VM tests.

The final host is paired with the unchanged, verified app/helper payload from
`1468571`. Later source changes affect host startup orchestration and its optional
Core lifecycle budget; the app helper's lease protocol and app sources are
unchanged. Host and app artifact source identities are recorded separately.

[Microsoft requires pending checkpoints to reflect actual initialization work](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-setservicestatus).

## Evidence and acceptance limits

The dedicated production-adapter suite uses real Node/CLR/platform helper, real
Store schema-5 writes, actual OS file leases/jobs and actual encrypted
backup/restore output. Its control transport is a non-listening file adapter.
Private test records replace only the privileged control-file reader, since no
machine/admin ownership is changed. A separate negative test uses the real
admin boundary and rejects current-user-owned fixture records. Test payloads use
small explicitly synthetic entries and source imports; they are not clean-machine
proof of the final full dependency bundle.

The full combined app payload and self-contained service package are rebuilt
separately from the exact committed source. Their inventories, extraction checks,
runtime hashes and non-listening smoke checks establish developer artifact
consistency, not release intake authentication or machine installation success.

No SCM registration/start, service account, machine ACL change, production
credential/trust state, network listener, VM configuration or publication is
performed. Actual service-token/ACL/pipe behavior, privileged capability issuance,
initial install, update commit/reboot/crash reconciliation, provider configuration,
TLS/LAN access and uninstall remain unaccepted. No usable disposable Windows VM
has been verified through current access. Existing web family/WhatsApp setup is
reused rather than duplicated in native setup.

## Failed creation/handoff cleanup correction

After successful CreateProcess, `JobProcess.Start` immediately owns the exact
root handle, including failures before job assignment. A failed assignment,
handoff callback, handoff publication or resume requests termination of both the
job and exact root, then waits for a signaled root and an observed empty job.
Termination API success alone is never treated as those observations. Cleanup is
bounded to five seconds and does not depend on a captured UI synchronization
context.

If observation fails or times out, `JobStartQuiescenceUnconfirmedException`
retains the exact process/job owner. The native service launcher transfers it,
along with the image pin, handoff descriptor and control channel, into a failed
session. Health then fails after AppLifecycle has stored that ownership. Its
existing cleanup retries and recovery-required path therefore apply. Continued
uncertainty yields QuiescenceUnknown without STOPPED or a released data grant.
Other callers of JobProcess.Start must likewise retain/reconcile this exception's
owned process rather than treating disposal or a termination request as proof.

Five additional non-listening tests cover a throwing callback after duplicating
the gate, failure before assignment, failure after actual handoff publication,
and unavailable root/tree observations. The latter verify that reservations and
handoff file pins remain held until a separate explicit retry obtains actual OS
observations. Failure injection is internal, absent from production configuration
and can only deny observations; it cannot manufacture successful observations.
No services, accounts, machine ACLs, production state or network listeners are
created by these tests. The later transaction milestone implements private-root publication and
reconciliation; real protected issuer identity and owner-channel acceptance
remain blocked.

Windows documents [TerminateProcess](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-terminateprocess) as asynchronous; the process handle must be observed through [WaitForSingleObject](https://learn.microsoft.com/en-us/windows/win32/api/synchapi/nf-synchapi-waitforsingleobject) before treating exit as complete.

## Admission and recovery probe ownership

The frozen SQLite admission probe and recovery inspector use the same concrete
runner. If creation or cleanup cannot observe root exit and an empty job, the
exception carries the exact retained session. Installed admission transfers its
reservation to it. The controller adopts a recovery-only lifecycle, retains late
authorization work and suppresses STOPPED while ownership remains unresolved.
An explicit cleanup retry must obtain real process/job observations before
disposing reservations. Recovery also keeps its inspector, source lease and
private plaintext scratch until the same observed retry; scratch is removed
before lease/key release. No probe plaintext is exported with evidence.

Three actual Node-probe regressions cover unavailable root/tree observation and
the recovery caller. Internal fault injection can deny an observation but cannot
synthesize an empty tree. These changes were checkpointed separately at
`59277382a8a55f8078afd1a6a5b69c2307efde00`.
