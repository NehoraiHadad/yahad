# Runtime startup budget regression

The installed diagnostic source `76548f5` reported Runtime failure while awaiting
readiness. Authority remained running; no application listener remained. Its event
does not reveal the complete child-startup cause.

Source investigation found a separate, reproducible defect: `TrackedLauncher`
received `lifecycleDeadline` (20 seconds in production) for readiness, even though
`ServiceController` and observed admission have a distinct startup budget. The
fix passes `startDeadline`. The existing overall start token still bounds total
admission, launch and readiness. No startup limit was removed; graceful stop,
forced cleanup, owner verification and update privileges are unchanged.

Focused evidence:

- A process-free case with readiness after 150ms, startup budget 2000ms and stop
  budget 50ms failed before the fix with `readiness_timeout` (88ms observation).
  It passed after the fix and disposed its session.
- The actual frozen mock application was launched through native Job Objects,
  data-lease handoff and the ready pipe, without SCM. The final run became ready
  after 24892ms, returned HTTP health, stopped gracefully and removed its validated
  owned fixture. Total time was 26842ms; no forced cleanup was needed.
- Eight existing startup-observation cases passed, retaining denial, timeout,
  late-completion, grant-disposal and quiescence checks.
- Both affected native test projects built successfully. Independent source
  review found no remaining issue after correcting failure-path cleanup.

Reproduce after a native test build:

```powershell
./installer/Yahad.FirstInstall.Tests/bin/Release/net10.0-windows/Yahad.FirstInstall.Tests.exe --startup-budget-only
./installer/Yahad.FirstInstall.Tests/bin/Release/net10.0-windows/Yahad.FirstInstall.Tests.exe --packaged-native-start-only C:/path/to/prepared-full C:/path/to/local-report.json
dotnet tests/windows-updater/bin/Release/net10.0-windows/Yahad.Production.Tests.dll --startup-observation-only
```

The second case requires the actual prepared full payload, not a mock binary;
see [packaging](../installer/README.md). Its fresh data uses only the mock connector.
Raw logs/reports stay under ignored local output.

Limits: the first standalone package run exceeded its 20-second test threshold;
later standalone runs passed. Cold-start duration is variable. The budget defect
is demonstrated, but complete causality for the installed failure remains
unverified until the updated host is accepted under the actual service accounts.
No installed service was restarted, no installation/uninstallation/update was
performed, and no family data or real WhatsApp account was accessed by these tests.
