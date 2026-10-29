# Committed activation and observed runtime health

This checkpoint fixes both findings in the independent `499c677` review and
connects committed activation to the existing controller, admission, launcher and
health adapters. Production remains disabled; no real service, pipe listener or
impersonation is instantiated.

## Accounting and status corrections

The authority previously read byte offset 36 from
`JOBOBJECT_BASIC_ACCOUNTING_INFORMATION`, which is lifetime `TotalProcesses`.
`ActiveProcesses` is at offset 40. `JobAccounting` now defines the Windows
structure once, with a typed native query and `TreeEmpty` property. Both
`JobProcess` and `NativeRuntimeGateKernel` use that implementation. Tests check
size/offsets, nonzero total with zero active, nonzero active, and actual accounting
after a temporary Node job exits. No magic byte offset remains in the authority.
[Microsoft documents the structure and distinction](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_basic_accounting_information).

Commit and resume previously reported `healthy` without launching a new runtime.
They now return `awaiting_runtime`. `CommitAndStartAsync` and
`ReconcileAndStartAsync` explicitly perform the next step; ordinary commit still
does not start anything implicitly. Missing runtime composition leaves the
committed state waiting. Resume failures remain recovery-required.

## Concrete health coordination

`RuntimeHealthCoordinator` receives an installed-binding authorizer factory,
retains one exact existing `ServiceController` and its execution task, and allows
only one runtime start per instance. `InstalledBindingStartAuthorizer` resolves
the exact returned installation binding. An exact-release barrier rejects a grant
for any other committed release before the launcher can create a child.

`healthy` requires the actual controller's RUNNING milestone, which follows
recovery/admission, retained session creation and the existing launcher's awaited
health check. A completed commit record, stale status, UI field or termination
request cannot generate that milestone. An exit before observed health is a
failure. An exit after health updates the state to failed/recovery-required;
normal requested shutdown becomes stopped.

Admission/start/health failure, health timeout or cancellation requests controller
stop and awaits bounded cleanup. Cancellation becomes cancelled only after the
owned execution finishes and retained resources are reconciled. Unknown cleanup
remains recovery-required, retaining the exact controller/task/session/grant.
There is no generic rollback or active-database overwrite.

`ServiceController` now retains unresolved runtime lifecycle and grant fields in
addition to admission probes/tasks. Its existing explicit cleanup retry reconciles
all of them before releasing the grant. Successful termination requests are still
not exit/tree observations. Failed disposal remains retained and retryable. The
health coordinator keeps the controller alive until that observed retry succeeds;
it never interprets an old false-quiescence result as a healthy runtime.

The web status schema and progress UI recognize awaiting-runtime, starting-runtime,
quiescing-runtime and confirmed cancellation. They cannot recycle an old success
journal into a new healthy result, and active phases reject duplicate requests.
The existing native/legacy confirmation behavior and bounded automatic delegation
are preserved.

## Test scope and remaining gates

Tests connect the real private-root writer/consumer, actual schema-5 Store, private
Node/CLR helpers, actual jobs and file leases, with file/in-memory control transport
and synthetic authorization. They cover successful commit/start/health/stop,
wrong release, denied admission, creation failure, unhealthy readiness, timeout,
cancellation, unknown cleanup with a held data lease and explicit observed retry,
late admission after timeout, and resumed activation followed by actual readiness.
Failure-only observation injection cannot manufacture successful cleanup.

The health used here is the existing admitted launcher's readiness contract.
Synthetic non-listening Store workers exercise it; this is not browser or second-
device reachability acceptance. Native SCM/identity/ACL/pipe bootstrap composition,
owner confirmation window, trusted release-intake publisher and installed-role
factories remain unconfigured. No new owner capability or automatic delegation is
issued on the machine. Older binaries do not contain this fix; the rebuilt
disabled authority inventory records the new source independently.

The blocked VM proposal still requires a fully populated artifact manifest,
identified isolated Windows guest/snapshot and explicit approval before execution.
A read-only host inventory can identify possible routes; a running virtualization
service or installed tool alone does not prove a usable, authorized guest.
