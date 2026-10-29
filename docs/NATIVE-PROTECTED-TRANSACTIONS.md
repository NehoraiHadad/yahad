# Protected-state transaction writer: private-root implementation

`ProtectedStateTransactionWriter` publishes the actual installed admission
records. Tests use the real consumer, Windows exclusive file leases, a retained
real Job Object, Node SQLite inspection and authenticated encrypted backup
receipts. The writer is internal and restricted to a private current-user-owned
synthetic root. It is not exposed as a service operation, CLI or request endpoint.
The production resolver's default entry remains explicitly denied while the
privileged committer identity and owner channel are unconfigured.

## Binding and authority

An operation binds format, operation kind, installation ID, expected epoch and
activation digest, exact release manifest digest, selected data ID and schema
range, master-key/policy/pin digests, web configuration, optional recovery
source/backup/operation/receipt identity, expiry and one-use GUID nonce. Only
install, activate and recover are supported. Install requires epoch zero, absent
control state and no existing commit history. Activate/recover compare the exact
old head, epoch anchor, bootstrap, owner intent and web digest before publication.

The authentication boundary receives the canonical operation SHA256 and required
scope; it returns a verified principal, installation, operation digest, scope,
original approval time and current authorization lifetime. Those claims never
come from client operation fields. The unconfigured implementation rejects every
request. Synthetic tests inject an independent approval registry; there is no
production issuer, credential, signing key or sample production capability.

Commit authority is a real exclusive `.authority/process.lock` lease. An actual
retained job must be observed empty, and the consumer must admit the exact
candidate payload and selected data. Its actual data lease and file pins remain
held across record publication. A probe with uncertain child ownership carries
the authority and data reservation into the explicit cleanup retry path rather
than releasing them. Neither a caller Boolean nor successful termination request
can substitute for observed quiescence.

## Durable protocol and reconciliation

Before changing a control record, write `pending.json` using a same-directory
exclusive partial file, WriteThrough, Flush(true) and MoveFileEx with write-through.
Pending contains the authenticated operation and exact before/after bytes for
five fixed records: owner intent, web, activation, epoch and bootstrap. Reserve a
durable one-use nonce, then publish those records individually. The ordinary
consumer refuses pending or partial state, so it cannot start on an intermediate
combination. Write an exact pending-digest/activation-digest/epoch commit receipt
before deleting pending. Keys, SQLite data, policy and payload files are not
modified by this writer.

Resume acquires the same exclusive authority and requires fresh `reconcile`
authentication for the identical operation and principal. The trusted boundary
must preserve the original approval time in its authenticated history. Resume
regenerates the complete after-state and validates every visible record as an
exact before/after member. It revalidates payload, selected data, actual receipt
and leases through the same consumer, including when a completed commit receipt
already exists. A completed receipt requires all five after-records; it cannot be
used to replay over older state.

A complete pending partial can be promoted only after those binding/authority
checks and an observed empty job. A truncated pending record is preserved and
rejected. Known control partials for the authenticated nonce are privately
quarantined before deterministic replacement. Foreign partials, corrupt nonce or
commit receipts, conflicting visible bytes and missing authorization fail closed;
they are not silently discarded or converted into success. They can require
authenticated operator repair. Quarantine and test scratch stay private and are
excluded from source/evidence transfers.

Operation expiry limits the initial commit window to ten minutes. Expired
interrupted work needs fresh reconciliation authority. The resulting exact
epoch/selection start capability has explicit null expiry; revocation or a later
protected epoch replaces it. It is not a browser login session or approval for a
different release. Epoch comparison prevents stale requests within this authority
root; it does not defeat an administrator restoring the entire root or VM image.

## Evidence limits and next real integration gate

Tests inject exceptions after flushed pending, nonce, each control publication,
partial activation and commit receipt. They cover exact replay, stale bindings,
wrong principal/scope, expiry, authority/data lease contention, a busy real job,
truncated state, complete pending promotion, old-state replay after completion,
corrupt selected SQLite after completion, and actual encrypted recovery output.
These are actual filesystem/process tests with synthetic authorization and data;
exception injection is not evidence of power-loss durability or Windows service
identity behavior.

Still required: choose and implement the privileged committer's real Windows
identity and authenticated owner channel, provision protected machine records
under that authority, and verify service-token/ACL/IPC boundaries on a disposable
Windows machine. The writer cannot be enabled by supplying fabricated records.
No SCM registration, accounts, machine ACL changes, production credentials,
listeners, certificate trust changes, VM configuration or publication occurred.
Clean-machine install, boot/reboot and actual power-loss recovery, AV/disk-full
behavior, update/rollback/uninstall and HTTPS/LAN handoff remain unaccepted.
