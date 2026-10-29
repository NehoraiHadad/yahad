> Historical account of checkpoint 6de1892. The current installed entry and its remaining authority gates are described in [NATIVE-INSTALLED-ADMISSION.md](NATIVE-INSTALLED-ADMISSION.md).

# Native activation admission: bounded fixture contract

This checkpoint adds a **test-profile admission resolver**, linked to the existing
service controller. It does not enable production activation. `Program.cs` still
constructs `UnconfiguredActivationResolver`, which denies application startup.
No production signing key, owner credential, trust enrollment or host service was
created. The previously packaged service binary from source `615b291` predates
the stop-status correction and this checkpoint; it must be rebuilt and identified
before any proposed machine acceptance test.

## Observations and authorization

`SignedFixtureAdmissionResolver` accepts independently supplied pinned public
keys for release, control and owner documents. The fixed
`yahad-fixture-admission-v1` ECDSA test profile separates signature purposes and
rejects unknown signer IDs, duplicate JSON fields and unknown properties. Test
private signing keys exist only in memory. Synthetic data master keys remain
inside private test directories, outside the source transfer.

The resolver binds a supplied exact head digest and installation identity to the
release, data selection, committed intent, expiring owner authorization, policy
and update pin. A caller-supplied minimum epoch models an independent replay
floor. These are **fixture anchors**, not a durable production anti-rollback or
owner-authentication implementation. Envelopes cannot introduce trusted keys.

Admission observes an empty retained Windows job and obtains a real exclusive
data lease. It pins private paths and source files, verifies complete payload
membership and file hashes, observes the selected master-key identity, then
reads the actual SQLite schema and integrity result. Limits include 1 MiB signed
documents, 64 payload files, 1,024 data members, 256 MiB per file and 512 MiB per
payload/data tree. These test limits do not promise support for production-sized
media collections.

The read-only reservation retains the lease and file pins through the service
controller's lifecycle. Immediately before starting, it checks owner expiry,
pending publication evidence, job emptiness, SQLite sidecars and data/payload
membership again. It prevents modification of retained files; it does not confer
a mutable application's writer lease. Tests use a simulated read-only launcher
and actual OS lease/job/file observations. No real Yahad Store is launched.

## Interrupted activation and SQLite

Incomplete signed phases and pending/partial publication files block admission.
The check follows Windows case-insensitive filename semantics. An actual killed
test publisher demonstrates preservation and refusal of crash-left evidence.
There is no activation publisher or automatic reconciliation in this slice.

Any observed SQLite WAL, SHM or rollback journal blocks admission without
checkpointing, deleting, repairing or replacing data. Tests kill a real SQLite
writer after a committed WAL transaction and verify the files remain unchanged.
Only a frozen database with the exclusive lease, no sidecars, an observed empty
job and retained no-write pins is inspected using a read-only immutable URI.
[SQLite documents that immutable mode skips locking and change detection](https://sqlite.org/uri.html).
The pinned [Node 24.22.2 source enables SQLite URI handling](https://raw.githubusercontent.com/nodejs/node/v24.22.2/src/node_sqlite.cc).
The inspector uses a contained job and explicit clean environment; it writes a
small schema report in a pinned private observations directory, never a copy of
the source database. A clean WAL-mode database is tested separately from a
database with outstanding WAL contents.

For recovered data, signed synthetic backup and recovery documents must match
the actual selected tree, schema, master key, policy and update pin, with a
complete phase and owner recovery scope. A second synthetic data selection is
admitted without changing the previous data set. These documents are **not**
observations from the production backup/restore executor or its MAC receipts.

## Stop status correction

`fcd5f48` fixes the audited cancellation path. Cancellation before running can be
an expected startup STOP. A timeout in graceful stop, root exit or tree exit
after running reports `StopFailed` (Win32 1066 / service-specific 1006), even
when forced cleanup succeeds. Unconfirmed quiescence retains the existing
recovery barrier. Six STOP/SHUTDOWN stuck-phase regressions were added; all 40
service-host contract tests pass without SCM invocation or network listeners.

## Remaining production adapters and acceptance

- A production release-verification bridge, protected installation/head identity
  and durable epoch anchor. This fixture signature profile is not the updater's
  release format and is never selected by the production entry point.
- An authenticated owner command channel and durable activation publisher with
  restart reconciliation. A UI Boolean cannot become authorization or evidence.
- A bridge from verified native backup/restore completion receipts to admission,
  including a real WAL reconciliation operation under exclusive ownership.
- A mutable Store writer-lease handoff, exact runtime/path ownership and complete
  production payload inventory, while retaining containment and clean launch
  environment. The externally supplied private Node path is a trusted test-tool
  input in this slice, not production runtime selection.
- Actual SCM identity/token behavior and service installation acceptance on a
  disposable Windows machine; no such machine is verified available through
  current access. No SCM registration/start, machine ACL changes, network or
  certificate settings, VM setup or publication occurred here.

Run `installer/test-admission.ps1` with explicit private SDK, Node and package
cache paths. Retained JSON reports distinguish synthetic authorization and
simulated launcher behavior from actual native file/lease/process observations.
The source checkpoint is not family-ready or release-ready. The existing web
family/WhatsApp onboarding remains outside this installer slice.
