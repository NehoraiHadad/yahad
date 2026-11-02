# Validation and release limits

Use [development commands](DEVELOPMENT.md) to reproduce checks. Results belong to
an exact commit, environment and artifact. Historical counts in evidence reports
do not prove that a later installer works.

The native installer is a **development candidate**. Authority startup progressed
on ASUS, but Runtime startup failed. Commit
`76548f5ef93c2537fc005ade237c5d3c3d53e1d9` adds bounded startup diagnostics;
it is not a confirmed Runtime fix. Clean installation and native update acceptance
are still required before presenting the package as ready.

The subsequent local attempt with that build stopped during readiness: Authority
ran, Runtime stopped and no application listener remained. Investigation found
that readiness reused the 20-second lifecycle/stop budget despite the separate
bounded startup budget. The source now uses startup's budget; overall cancellation
still caps authorization + launch + readiness, and stop/cleanup limits are unchanged.
A 150ms-readiness/2000ms-start/50ms-stop regression failed before the change and
passed afterward. The packaged mock app reached health through its actual native
job, data-lease and ready channel in about 25 seconds, then stopped and cleaned up.
Eight existing startup-observation cases also passed. This demonstrates a budget
defect; it does not prove it was the only cause of the installed failure. The
updated host has **not** been installed or verified under the real service accounts.

Subsequent test teardown change `4775332` owns jobs, awaits pending starts,
cleans validated fixture roots and releases the shared Store compiler cache.
Fifteen focused cases passed, including actual Node launch-race and synthetic
Store startup. One symbolic-link case was blocked by Windows error 1314
(missing privilege); it did not pass. No system privilege change was used.

Earlier independent Linux regression on `e4f17ca` passed application tests,
typecheck/build and Python deployment tests. Root-only and Windows-only cases
were explicitly skipped. That is source/platform evidence, not acceptance of
the current Windows executable.

Before Windows release, validate the exact final package: clean install/uninstall,
service/file/IPC isolation, health, app and updater replacement, Notify refusing
automatic application, interruption/recovery and schema compatibility. Preserve
unknown send outcomes without resend and later user data during rollback.

Live WhatsApp, another device over LAN, certificate trust, microphone recording,
reboot/sleep recovery and root-only scenarios have not all been verified here.
Document unperformed cases. Unsigned installer distribution was chosen explicitly;
integrity, privacy, test and [license](DISTRIBUTION-LICENSE-REVIEW.md) requirements remain.

Raw logs, screenshots and generated manifests belong in ignored local output or
scoped PR attachments. Commit reusable tests and concise findings. Earlier tracked
output remains recoverable from Git history.
