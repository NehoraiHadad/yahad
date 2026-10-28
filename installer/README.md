# Native easy-install feasibility slice

Development only, isolated from the released installer and application. This is **not an installer for a family computer**. It does not register services, alter firewall/CA settings, open onboarding, or run the Yahad app. Production packages are deliberately refused.

Baseline: `v0.5.2`, commit `4c41d5ef80ea349d0f678ee9c572e4975a2d776d`, tree `f4dde9cc1dc2d2abd374929a436d425bbc2e9f63`.

## Run as a developer

Build prerequisite: .NET 10 SDK on Windows x64. The family-facing distribution will not require an SDK or separately installed .NET, Node or Python. Build caches, process-local settings and fixtures stay under `installer/.scratch`; no user NuGet configuration is read. The script restores its build-only environment variables when it returns or throws.

```powershell
./installer/build.ps1
./installer/build.ps1 -Publish
# Or -DotNet C:/path/to/private/dotnet.exe
./installer/artifacts/Yahad.Setup/Yahad.Setup.exe
```

`-Publish` produces self-contained, single-file Windows x64 executables. Native framework libraries extract into the process's temporary directory when needed; “single file” does not mean “no temporary extraction.” There is no npm installation on the destination machine. Current executables contain only the .NET fixture UI/host, **not Node, ffmpeg, Caddy or the app**.

The GUI's start button runs real fixture verification/staging. `--preview=consent`, `--preview=error`, and `--preview=ready` show labeled visual states without asserting a test happened. The window uses `asInvoker` and never requests elevation. Closing during work requests cancellation; resumption in the same window verifies the same package and journal. Cross-launch recovery UI is a later increment; the core recovery API is tested.

`Yahad.Host.Slice.exe --fixture-child C:/path/to/a/fixture/directory` starts only a waiting copy of itself in a Windows Job Object. Ctrl+C disposes the job. This is a native process containment spike, **not SCM service support**. The test harness also exercises grandchildren and unexpected death of the job owner.

## Implemented boundaries

- Exact manifest/ZIP membership, target/kind gates, bounded sizes and decompression, SHA-256 byte checks, duplicate/case/path/device/ADS/symlink rejection.
- Private Windows DACL on creation, owner/ACL/reparse validation, exclusive operation file lock, flushed atomic journal replacement, new payload publication without overwrite.
- Cancellation and interrupted-stage recovery. A published payload is revalidated before a resumed run can report readiness; changed bytes remain untouched. Abandoned staging directories remain for review.
- Native suspended process creation, job assignment before execution, `KILL_ON_JOB_CLOSE`, shell-free argument quoting and deterministic handle disposal.
- Pure private-LAN address planning with explicit adapter selection, and a temporary loopback bind probe. No security or network configuration mutations.

The fixture directory is a user-owned test boundary, not an elevated security boundary. The private DACL means a restricted AppContainer/sandbox token cannot use it even when the same user owns the folder. Tests must run as an ordinary unrestricted user. Do not weaken the ACL to accommodate a sandbox. Production privileged path traversal needs handle-relative, no-follow operations and independent administrator-owned package validation before this code can be reused there.

See [architecture and delivery plan](../docs/NATIVE-INSTALLER-PLAN.md), [acceptance matrix](../docs/NATIVE-INSTALLER-ACCEPTANCE.md), and [validation evidence](../evidence/native-installer/validation.md).
