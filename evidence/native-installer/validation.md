# Native installer feasibility validation — 2026-10-28

Source implementation: `81d403b7ffab956ed2ba88f8373fb555f8c8133d`, branch `feat/native-installer-slice`. Architecture/acceptance commit: `10a0337`. Isolated workspace: `C:/Users/nehor/Documents/Codex/2026-10-28/task/yahad`.

Verified released baseline commit `4c41d5ef80ea349d0f678ee9c572e4975a2d776d` and tree `f4dde9cc1dc2d2abd374929a436d425bbc2e9f63` before branch creation. No AGENTS.md or checkout .agents/.codex instruction directory was present. No release/tag/current installation was changed. Source changes are confined to new installer, plan and evidence paths; no quota/schema/store/db/recovery app files are touched.

## Completed checks

- Built Core, WPF Setup, native containment Host.Slice and the test harness with **0 warnings, 0 errors** on Windows x64, OS build 26100. .NET SDK 10.0.403 / runtime 10.0.14 was downloaded from Microsoft's release metadata, checked against its SHA-512 and extracted into adjacent task-local `tooling/dotnet`. No SDK was installed in the system.
- **36 passed, 0 failed** in the final synthetic harness run. See [machine-readable results](test-results.json). This includes a separate process holding the operation lock, actual Windows private DACL rejection, actual junction rejection, argument quoting (Hebrew/quotes/trailing slashes), exact early-exit status, child/grandchild termination on job close and on owner-process crash, occupied loopback port, plus archive/cancel/recovery cases.
- `build.ps1 -Publish` succeeded for both self-contained single-file win-x64 executables. [Artifact sizes and SHA-256](artifacts.json) identify local outputs. They are ignored build products, not release downloads. **Interactive GUI execution is not verified.** The test harness directly exercises the shared native containment implementation; this does not establish the published host's clean-machine runtime behavior.
- Local source review corrected bounded decompression, exact process-handle lifetime (instead of PID-based exit inference), dangling reparse checks, JSON package diagnostics, explicit cross-process exclusion, accessible action names and build-environment restoration. This was self-review, not an independent reviewer approval.
- `git diff --check` and staged whitespace checks passed. Existing application tests were not run; application/deploy files were not modified and the application was not launched natively.

## Environment restrictions resolved

The initial build attempted to probe the owner's NuGet configuration and was refused by the filesystem sandbox. The build now uses task-local NuGet config/cache and process-local APPDATA, restoring all environment values on return; it does not read the owner's NuGet configuration. Initial fixture execution under the restricted sandbox token could create a private DACL directory but could not subsequently access it because the sandbox's additional token restriction was not granted. The approved nonprivileged fixture execution outside that token passed. The DACL was not weakened to make the sandbox pass. Fixture files remain under ignored `.scratch/temp` for inspection; no recursive cleanup touched junction targets.

## Exact remaining UI launch blocker

Action: Computer Use `cua.computer.launch_app` targeting `C:/Users/nehor/Documents/Codex/2026-10-28/task/yahad/installer/artifacts/Yahad.Setup/Yahad.Setup.exe`, solely to inspect the compiled native window and obtain screenshots.

Initial result: **`Computer Use app approval timed out`**. A following read-only `cua.listWindows()` returned no Yahad.Setup window. No bypass or alternate launch path was attempted. This was a timed-out Computer Use approval, **not an automatic sandbox-review denial**.

The parent then supplied the owner's explicit approval for opening the built Yahad.Setup for visual testing, with permission to retry once after reconciliation. A fresh window inventory again showed no Yahad.Setup. The same exact `launch_app` path was retried once. Its returned inventory showed **Visual Studio Installer**, with executable `C:/Program Files (x86)/Microsoft Visual Studio/Installer/setup.exe`, rather than a Yahad.Setup window. A second read-only inventory and a process-name/path check confirmed Visual Studio Installer present and no Yahad.Setup process/window observed. No recent Yahad.Setup .NET Runtime event was found. This suggests launch routing selected another installed setup program, but the tool did not provide an execution trace, so the exact target's execution outcome remains **unverified**. No further retry, alternate launch, interaction with Visual Studio Installer, service installation or system change was attempted.

The approval itself is resolved; the remaining blocker is correct executable dispatch by Computer Use. No screenshots, resizing, RTL runtime checks, GUI fixture interaction or accessibility pass can be claimed. Further visual QA requires resolving the launcher target and authorizing any additional launch attempt. The native core fixture tests and build results are independent of this UI-tool issue.

## Remaining delivery gates

This is a runnable compiled fixture slice, **not the next production installer**. Missing: actual app portability/safety adapters and bundled Node 24 dependencies; SCM service lifecycle and graceful IPC; Windows updater/recovery adapter and package-authority handoff; chosen packaging-engine integration; install/repair/reboot-resume/keep-data uninstall; Linux bundles and launcher; TLS/LAN/tablet trust UX; download website; Authenticode signing and independent code review.

Actual services, firewall/CA/security changes, persistence and credential enrollment on the working computer were not authorized and were not attempted. Their acceptance needs disposable Windows/Linux machines or a separately specific owner authorization. No real WhatsApp, family data, paid service, manual Actions run, PR, push, release, Pages enablement or deployment occurred. Refer to [the acceptance matrix](../../docs/NATIVE-INSTALLER-ACCEPTANCE.md) for precise fixture-versus-machine distinctions, including Windows x64, Linux amd64 and Pi arm64 as separate gates.

## Project-directory handoff

The owner prefers `C:/projects/yahad` for future tasks. Current work remains in its original isolated task checkout; nothing was moved mid-task. Once this branch is clean, a task with access to the preferred project can clone the repository there if absent and fetch the **local** `feat/native-installer-slice` ref from this checkout (or the companion Git bundle), then check it out after inspecting that project's status/instructions. Never reset or overwrite another task's uncommitted work. There is no need to publish the branch to transfer it locally.
