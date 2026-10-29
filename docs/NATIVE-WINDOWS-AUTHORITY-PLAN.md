> Current owner-selected target: **ASUSHADAD**, an ordinary working computer.
> A VM is optional, not required. See [current endpoint checkpoint and normal-host proposal](NATIVE-ENDPOINT-COMPOSITION.md); it supersedes VM-only recommendations below. No installation/security change is yet approved.

# Native Windows authority adapter plan

Planning snapshot. The [code-only adapter follow-up](NATIVE-WINDOWS-ADAPTERS.md)
implements bounded automatic delegation and supersedes the initial manual-only
scope below. All real-machine execution gates remain closed.

Status: design only, based on reviewed source `b1ea1bd5be33693fa7d3e2f7e375d3d1a1d0444d`.
No adapter, service, credential, listener or machine permission is installed by
this document. Production admission stays denied. The accompanying single VM
manifest is a blocked proposal, not authorization to run an existing proposal.

## Decision

Use a separate passwordless virtual service account for the updater/committer:
`NT SERVICE\Yahad.Acceptance.Authority`. Retain
`NT SERVICE\Yahad.Acceptance` for the application host and its Node children.
These are fixed acceptance-build names; this plan does not rename a released
service or allocate a production service name. Both run as separate processes,
without administrator membership, with only `SeChangeNotifyPrivilege` retained.
The authority receives narrowly scoped file and runtime-service-object rights,
not machine-wide administration. Virtual accounts avoid a manually stored service
password; they are not inherently network-isolated. Microsoft documents their
management and network behavior in [service account guidance](https://learn.microsoft.com/en-us/sql/database-engine/configure-windows/configure-windows-service-accounts-and-permissions?view=sql-server-ver17).
SCM supports [removing unneeded token privileges](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/ns-winsvc-service_required_privileges_infow).

The trusted owner channel is a local native companion under the Windows user
chosen during installation. It talks to the authority through an explicitly
secured, local-only named pipe. The existing parent web UI continues initiating
requests and displaying progress. A web request, its family/user IDs, app service
SID, or possession of a browser cookie is not the privileged owner approval.
The companion displays the authority's exact operation summary and obtains a
fresh affirmative action. The authority obtains the Windows subject from the
connection's OS token and matches the registered owner, never from JSON.

This first adapter supports manual exact-target activation and explicit recovery.
An automatic-update request must remain blocked unless a separately implemented,
owner-approved protected delegation policy permits that exact operation. Preserve
the existing policy/pins and report mode locked if unsupported; do not silently
change auto to notify or infer consent from an app-writable mode request.
The architectural choice needs no owner-designed protocol. Future unattended
policy support is a separate implementation/acceptance gate.

## Existing seams and necessary changes

The current `/api/parent/updates` route in `src/server.ts` checks parent session,
origin and CSRF. `src/updates.ts` queues a bounded request bound to an opaque
version/fingerprint and exposes worker status; it does not presently require
fresh password entry for an update. `web/UpdatesPanel.tsx` already confirms the
target, polls and handles uncertain outcomes. Reuse those paths and the existing
family/WhatsApp setup. No second user-setup wizard is proposed.

`ProtectedStateTransactionWriter` already binds the operation, CAS, nonce,
pending/commit receipts and actual consumer. Its private-root restriction is
intentional. `WindowsFileBoundary` currently accepts a single application SID
and admin/System-owned bootstrap records. Merely pointing that writer at
ProgramData, or granting two accounts access, will fail those checks. Implement
the following explicit role changes before enabling any real entry:

| File/change | Concrete responsibility |
| --- | --- |
| `installer/Yahad.Authority/Program.cs`, `AuthorityService.cs` (new executable) | Separate SCM entry, exact own-token checks, bounded operation processing, stop/recovery state; no generic execution or shell endpoint. |
| `installer/Yahad.Authority/InstalledAuthority.cs` | Read fixed admin/System-owned `installation.json`; verify installation ID, fixed service identities, owner Windows SID and bootstrap provenance. No client-selected root/SID/image. |
| `installer/Yahad.Install.Core/InstalledFileRoles.cs`, `WindowsFileBoundary.cs` | Exact per-class owner/DACL validation, no-follow/single-link/pinned paths, executable immutability, explicit authority/runtime data access; retain existing secret checks and negative tests. |
| `installer/Yahad.Authority/OwnerPipe.cs`, `WindowsPipePeer.cs` | Native local-only named pipe, bounded frames/timeouts, kernel peer token/process/session verification and separate submit/status/approve roles. |
| `installer/Yahad.Authority/OwnerOperationAuthentication.cs` | Implement existing `IOwnerOperationAuthentication` from the authority's durable approval ledger, including expiry, original approval identity/time, scoped reconciliation and revocation. |
| `installer/Yahad.Owner/Program.cs`, `ApprovalWindow.cs` | Unelevated RTL operation confirmation/status window; no embedded browser/native bridge, URI action handler, tray autostart or new family setup. Open explicitly from an installed shortcut for the first adapter. |
| `installer/Yahad.Authority/RuntimeService.cs`, `RuntimeJob.cs` | Exact SCM service handle and retained kernel outer job; gate runtime launch, observe root/tree death, never accept a `stopped: true` report. |
| `installer/Yahad.ServiceHost/Program.cs`, `ServiceAuthorization.cs`, `ServiceRuntime.cs` | Authenticate authority, wait before app launch, expose bounded lifecycle control, retain existing app job/data-lock handoff and failure contracts. Default denial remains until the installed authority path is complete. |
| `installer/Yahad.ServiceHost/ProtectedStateTransaction.cs`, `InstalledActivation.cs` | Reuse existing transaction engine under verified installed roles and real approval adapter; keep the private synthetic constructor. Read dynamic control state only after the immutable admin anchor. |
| `installer/Yahad.Install.Core/NativeSqliteInspector.cs`, `NativeRecovery.cs` | Use installer-owned fixed private probe runtime and fixed SQL; exact role-aware leases, backup receipt verification and new-directory restore. Never execute app-provided code as authority. |
| `src/updates.ts`, new `src/windows-update-client.ts`; `web/UpdatesPanel.tsx`, `web/updateProgress.ts` | Windows request/status transport and `awaiting_local_approval` state, keeping parent/CSRF/target binding and duplicate/uncertain-response behavior. No schema, quota, store or family-onboarding edits. |
| `installer/Yahad.Setup/MachineInstall.cs`, `MachineInstallPlan.cs`, packaging/tests | Declarative two-service/ACL provisioning and receipts; privileged executor separate from a non-mutating planner. No running installer is authorized in this task. |

## Identity, files and bootstrap ownership

The elevated installer owns Program Files bootstrap/service/companion/probe
binaries and their dependency closure. Only System/Administrators can replace
them. App updates never replace the authority, host, companion or probe runtime;
bootstrap repair/update requires an elevated installer. Production release intake
must establish the payload digest under the existing update policy/pins. The
developer archive's `releaseSignatureVerified:false` cannot enable real intake.
No new updater signing key or enrollment is proposed.

Create immutable ProgramData `installation.json` under System/Administrators,
read-only to both services. It anchors the installation and fixed roles. Place
mutable activation/bootstrap/epoch/owner/web state under authority ownership:
authority Modify, runtime Read, System/Administrators FullControl. The runtime
cannot write an approval, epoch, payload manifest or protected delegation policy.
An authority-owned dynamic `bootstrap.json` is accepted only after that immutable
anchor; the existing admin-only bootstrap rule must not be globally relaxed.

The authority owns payload publication and its staging/receipt directories;
runtime has ReadAndExecute on admitted payloads only. Active data permits runtime
Modify and authority's specifically required read/lease/recovery access. Give the
authority writable access to the lock and its own restore/backup directories;
do not grant arbitrary Modify on live SQLite solely for activation. Final restore
directories receive the exact admitted runtime data ACL before publication.
Master keys need explicit authority/runtime Read and no ordinary-user access.
Each file class gets a closed SID/mask policy, not a global multi-user exception.
The ordinary owner uses IPC and has no direct protected-state write permission.

Before UAC elevation, the unelevated install UI establishes its Windows user via
an OS-authenticated connection. The elevated bootstrap displays and confirms that
owner SID; it must not accidentally register an over-the-shoulder administrator
credential's SID as the parent. It authorizes only installation of the verified
local package and initial startup. Then open the existing web setup/login.
Family creation remains in the app. Later family/user IDs are request/status
filters; the registered Windows owner independently authorizes privileged work.
Rebinding the Windows owner requires explicit elevated repair, never a web POST.

## Local authentication and operation flow

The authority creates two fixed-installation-scoped endpoints: requests from the
exact runtime account and approvals/status from the registered Windows owner.
Use `PIPE_REJECT_REMOTE_CLIENTS`, first-instance collision refusal, explicit
DACLs, overlapped cancellation and noninheritable handles. Grant client data
read/write/synchronize rights individually; avoid generic write granting pipe
instance creation. These details follow Microsoft's [pipe ACL guidance](https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-security-and-access-rights)
and [CreateNamedPipe contract](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-createnamedpipea).

Authenticate both directions. Retain the actual client process handle/token,
creation identity, expected SID and session. The companion verifies the server
against the exact SCM authority PID/token and immutable installed image. PID
alone is not a credential. Clients connect with SecurityIdentification SQOS;
the server uses identification only, checks every API return and reverts on the
same thread before any await or file operation. No SeImpersonate privilege is
needed for identification, per [ImpersonateNamedPipeClient](https://learn.microsoft.com/en-us/windows/win32/api/namedpipeapi/nf-namedpipeapi-impersonatenamedpipeclient).
Failed impersonation/reversion fails closed; failed reversion terminates the
authority rather than executing work in the wrong context.

1. The existing parent route submits only a request. The authority independently
   validates the worker-issued target, installed release intake, policy/pins and
   selected data; it derives the full `ProtectedOperation` itself. Reject paths,
   executables, supplied owner/scope claims and unknown fields. Maximum frame is
   32 KiB, one in-flight mutation per installation, ten-minute request/approval
   window; immutable operation digests identify all retries/status queries.
2. The native owner window reads the authoritative summary: current/target
   version, exact selection, schema compatibility, backup/recovery and expected
   interruption. It submits only challenge ID + exact digest + approve/deny.
   The broker verifies the connection token against the anchored Windows SID.
   Cancellation, wrong SID, modified digest or expiry cannot record approval.
3. Store the approval durably inside the authority-only ledger before stopping
   the app: subject from token, scope, installation/digest, challenge/nonce,
   original approval time and expiry. The authentication adapter reads that
   record, not request fields. Fresh reconciliation requires the same owner and
   operation and preserves original approval history. No approval is inferred
   because a process restarted. Check revocation/expiry before every mutation.
4. Observe quiescence, hold the actual data lease, obtain/verify the actual backup
   receipt, and use the existing writer/consumer. Keep pending until consistent
   publication and receipt. Never restore into active data. Report recovery
   required on unknown cleanup; retain all owned resources for an observed retry.
5. Restart only the admitted runtime, observe health and publish a bounded
   authority-owned status record. Lost replies are reconciled by digest/status,
   not blind resubmission. The native window stays available while the app is
   stopped; the web panel reconnects afterward. A tablet may request work but
   requires confirmation at the home computer for this first adapter.

This is a Windows user boundary. It does not prove human presence against malware
already running as the registered owner, or separate two people sharing that
same Windows account. An approved native image/PID check is defense in depth,
not a claim that same-user process injection is impossible. A compromised app
account still has its ordinary data-writing authority, but cannot mint protected
approval or change bootstrap services.

## Concrete lifecycle observation

Create and retain the outer no-breakaway, kill-on-close job in the authority.
Start the exact demand-start runtime through SCM. Its installed native host
performs an authenticated startup handshake and waits before spawning Node.
It grants only the authority the process rights needed for assignment/query/exit
observation; the authority verifies the SCM PID, process handle and token, then
assigns that waiting host to the outer job before allowing app launch. Keep the
existing host's inner app job and continuous Store lease. The authority never
obtains quiescence by trusting a job handle supplied by an app request.

SCM grants authority only query/start/stop/interrogate for that one runtime
service, excluding change-config/delete/write-DACL/write-owner. The authority
cannot alter its own service registration. [Service access rights](https://learn.microsoft.com/en-us/windows/win32/services/service-security-and-access-rights)
support that split. Installer-owned service DACLs also prevent the runtime or
ordinary owner from independently starting/stopping/reconfiguring those services.

Before commit/restore require the retained host exit, observed empty outer job
and exclusive data lease. Graceful stop failure is reported even when forced
cleanup succeeds. Unknown observation retains authority and suppresses success.
Authority crash closes the noninheritable sole job owner and kills the managed
tree. On restart, reconcile the exact recorded generation and actual process/job
state; missing handles after a crash are not proof of an empty tree. If the
generation cannot be reconciled, remain recovery-required and require a clean
VM reboot/owner recovery observation; do not synthesize a fresh empty job as proof.
Job objects are process-tree containment, not a complete malicious-code sandbox
against external Windows process-creation brokers. No such sandbox claim is made.

Cross-service process access and nested SCM job assignment are real acceptance
risks, not established facts. Windows requires specific process access for
[AssignProcessToJobObject](https://learn.microsoft.com/en-us/windows/win32/api/jobapi2/nf-jobapi2-assignprocesstojobobject)
and documents [nested jobs and child inheritance](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects).
If the exact VM token/job combination refuses assignment, fail before Node starts;
do not add Debug/Impersonate/AssignPrimaryToken privileges as an automatic fallback.

## Minimal implementation order and acceptance

First implement the fixed installed role reader/ACL validation and real approval
ledger adapter, preserving default denial. Then implement the OS peer adapter,
native companion and gated runtime/outer-job adapter. Finally connect the Windows
request/status branch in the existing update UI and package the exact executables.
Do not start with more generic transaction classes. Keep the Linux adapter intact.

Safe development can compile the real P/Invoke adapters, evaluate fabricated
token/security-descriptor snapshots, and test the actual writer/consumer using
private synthetic roots and an in-memory transport. Test wrong SID/role/digest,
replay, revocation/expiry, partial ledger/records, failed token queries/revert,
PID reuse, service-config drift, startup gate ordering and cleanup uncertainty.
Existing temporary child/job tests can cover nonprivileged containment. Under
the current restriction, do not open even local pipe listeners or exercise real
cross-account impersonation; those are VM gates. Compilation or simulated token
tests cannot prove service-token, ACL or local owner-channel authentication.

The companion manifest `installer/windows/authority-vm-acceptance.json` is the
only proposed real-machine test for this milestone. It intentionally has null
new-artifact/VM identities and `executionEligible:false`: those binaries do not
exist yet, and the old b1ea1bd host cannot stand in for them. After implementation,
freeze every executable/dependency hash and the manifest hash; identify a clean
disposable Windows 11 x64 VM, snapshot and two existing test-user SIDs. Then ask
for approval of that one concrete manifest. No VM has been identified through
current access. This plan needs no host-change approval now and authorizes none.
