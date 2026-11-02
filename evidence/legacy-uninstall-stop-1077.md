# Legacy uninstall: stopped service code 1077

The old uninstall path called `RequireCleanStop`, which accepts only Win32 zero.
SCM code 1077 is `ERROR_SERVICE_NEVER_STARTED`: no start attempts since the last
boot, not proof that the service never ran or completed a prior cleanup.
See [Microsoft's error-code definition](https://learn.microsoft.com/en-us/windows/win32/debug/system-error-codes--1000-1299-)
and [service status fields](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/ns-winsvc-service_status_process).

The removal-only predicate now permits stopped 1/PID 0/specific 0 with Win32 zero
or 1077. It still calls the exact configured account/image/type ownership guard.
The product checks it after observed stop, again while retaining its original
protected root/data lease before publishing intent, and inside the dedicated
removal-only service deletion. Receipt repair's original strict clean-stop,
general create rollback/Delete and configuration guards remain unchanged.

`Directory.Exists` could conflate inaccessible data with missing data. The
removal-only presence helper now accepts absence only for explicit file/path
not-found exceptions, refuses files/reparse points and propagates access/I/O
errors. A failed lease acquisition is never changed into success.

Eight grouped synthetic cases cover both accepted statuses, active/transition/
PID/service-specific/failed stop refusal, unchanged strict receipt repair,
wrong account/image/type, true absence versus denied/I/O/link/file, actual
private lease contention and changed status while held, and existing locked/
hardlinked/interrupted removal protections. No real SCM or family data is used.
Independent source review found no concrete remaining gate/placement blocker.

Final exact commit, test counts and package hashes belong in generated delivery
evidence. The earlier e4f17ca source was independently validated on Linux:
Node 478 pass/7 Windows skips; Python 3.12 and 3.13 each 451 pass/57 root skips;
typecheck/build and loopback ACK/no-resubmit passed. This new delta changes only
Windows Setup/test code and documentation; it does not claim a new Linux run,
native installation/removal, ACL change or live acceptance.
