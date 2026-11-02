# Native startup failure and fenced teardown

The exact c92376e standalone installed through publication, then failed at
FullStart/UPDATE_SERVICE_START_FAILED at22:39:36 Asia/Jerusalem. Runtime
remained stopped1077; Authority stopped1067 and RecoveryA/B stopped1066.
The underlying managed startup exception was not retained by the old catch.
Its cause remains unknown; this change does not claim to have repaired it.

Scoped SCM events show the two recovery services repeatedly starting each
other after handled failures, exceeding180failures per slot. Their generic
service-specific1004 is ServiceFailure.StartFailed; the displayed Windows
description "Invalid flags" is not evidence of an original native flags error.
The verified c923 product uninstaller subsequently fenced all four services:
all are stopped/PID0, DISABLED, with empty failure actions/reset0. It then
refused full removal. No manual service configuration, ACL changes or manual
deletion were used. The installation is not accepted as successful.

The new source gives A/B no automatic action for a reported noncrash refusal.
For an actual unreported crash, each has one peer launch followed by explicit
NONE, retaining the existing86400second failure-count reset. Explicit
authenticated starts remain possible after automatic failover is exhausted.
Primary Authority behavior is unchanged. The installer reads back the exact
command, reset, action count, kinds/delays and noncrash flag. This uses the
documented [SCM action ordering](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/ns-winsvc-service_failure_actionsw)
and [noncrash flag](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/ns-winsvc-service_failure_actions_flag).
The latter's documented reboot applicability is not a reason to request a
reboot here: fresh helper services start with the disabled flag, and the
terminal action independently bounds old-flag behavior. Native verification
of the new policy remains a separate acceptance step.

Startup catches now report a fixed local Application event with only the
compiled role/source, process number, exact public-code allowlist, numeric
error identifiers and up to three bounded application method names. No
exception text, paths, arguments, tokens or data are emitted. The
[event-source handle API](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-registereventsourcew)
falls back to Application without creating an event-source registry key.
Logging denial never repairs permissions, masks the original failure or
authorizes recovery. Synthetic SCM adapters do not invoke the native writer.

Removal now has a separate fenced STOPPED/PID0 observation; ordinary StopAsync
still requires its previous clean-exit semantics. A stopped failed service is
not treated as writer quiescence. Before intent/deletion, the full uninstaller
retains the existing serial lease, fences every service, acquires the exact
current-data exclusive lease, reruns existing durable journal/handoff/admission
checks, and rechecks fixed configuration/protection/status. Both leases remain
held through SCM deletion, absent-registration confirmation and durable
services_removed. They are released for guarded subtree deletion, because the
held locks themselves prevent that deletion.

Closed-inventory inspection validates only those exact retained gate objects
through metadata handles: canonical parent identity, single-link gate file ID
and actual role ACL. It does not reopen their contents under FILE_SHARE_NONE
and does not exempt other files named process.lock. Foreign/closed/changed
objects and validator failures refuse normally. There is no process scanner,
legacy migration or alternate ownership/receipt path.

The focused source build passed with no warnings/errors. Ten focused cases
passed, including the private real-file exclusive-gate/second-writer test and
the bounded mutual-crash model. Independent actual-diff privacy/minimality and
teardown reviews found no actionable blocker. Final frozen regressions,
packaging and native acceptance are recorded separately; none is inferred
from these unit results. Original Notify/localonly/noapply/noLAN/noWA/noreboot
boundaries remain unchanged.
