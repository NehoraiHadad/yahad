# First-install synthetic Store fixture diagnostics

The 9548166 Windows run reported 34 passes and nine failures, matching the frozen
b459ab5 baseline by case name. Equal names did not explain their cause.

Instrumentation exposed a child startup failure: `spawnSync` of the retained
`Yahad.Platform.exe` returned `ENOENT`, although the file existed. The native
image paths under the deeply nested workspace fixture directory were 262–263
characters. Node's non-namespaced Windows process launch exceeded MAX_PATH.
Using a shorter private `TEMP` location allowed the same helper bytes and real
Store startup to succeed. No permissions or system settings were changed.

There was also a test bug: Store scenarios shared one fixture. The first case
moved `app.sqlite`, failed before restoring it, and subsequent cases inherited
the missing database. They then failed on file moves or schema probes, including
the WAL case. The fixture now:

- Creates a separate private synthetic fixture for each Store case.
- Rejects an excessively long native-image path before copying/starting it.
- Captures errors from the synthetic worker and reports admission, launch,
  health and barrier failures without changing production error handling.
- Clears an old worker diagnostic only when the caller resets after observed
  quiescence, so retry diagnostics cannot refer to the previous attempt.

`Yahad.FirstInstall.Tests --store-integration-only NODE REPO HELPER OUTPUT` runs
the ten Store scenarios separately from package, removal and WPF checks. Select
a short **private, task-owned** temporary directory through the invoking test
process's `TEMP`/`TMP` environment. Do not change machine-wide variables,
permissions, service identities or Windows long-path policy.

This focused mode deliberately does not run the unrelated hardlink-removal case.
A diagnostic broad run encountered `CreateHardLinkW` error 5 (access denied) in
its private synthetic fixture. That operation was not retried or escalated.
Report the focused run as ten Store cases, never as a full 43-case pass.

These are nonprivileged synthetic process/job/lease checks. They do not establish
SCM installation, service-account ACL behavior, actual-machine acceptance, or
second-device LAN/WhatsApp behavior. The project release gates remain unchanged.
