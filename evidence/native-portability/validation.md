# Native portability validation — 2026-10-28

Worktree: `C:/projects/yahad-native-install`, branch `feat/native-installer-slice`. Official repository clone: `C:/projects/yahad`. The latter was inspected empty before cloning. Baseline commit/tree were reverified; local bundle import recovered `15e05a0b61267f9f8486bc7d72059221fe0dfcb0`. No existing checkout was reset or overwritten; the old task checkout is preserved.

Validated source increments:

- `5e4eb7b`: rename the React component to `ChatCelebrationView.tsx`, resolving the actual case-insensitive Windows TypeScript resolution failure with `chatCelebration.ts`. Only the view import and its test import changed.
- `0852cad431dbe36a772ed374d28f38de208291ed`: app platform boundary, native ACL/handle reads, process-owned native data leases, startup/shutdown integration and native mock-app tests.
- `8157434c49050f608843e71ba3653433b331cbaa`: deterministic target-specific Linux runtime candidate packaging and synthetic tests.

## Results and limits

| Check | Observed result | Boundary |
| --- | --- | --- |
| .NET build/publish | Four projects built without warnings/errors; Setup, Host.Slice and Platform published self-contained for win-x64 | No SCM registration or clean-machine runtime acceptance |
| Final .NET harness | **50 passed, 0 failed**; copied machine-readable result | Synthetic files/processes, real Windows ACL/path/handle APIs |
| Final native Node test run | **7 passed, 0 failed, 0 skipped**, 37.2 seconds | Actual private Node 24.22.2, Windows helper and mock app; synthetic data only |
| Combined app/security/onboarding/celebration run | **44 passed, 0 failed, 0 skipped**; TAP retained | Before final process-owned-lease strengthening; final native 7 rerun covers that strengthening |
| Linux candidate builder | **14 passed, 0 failed** | Synthetic ELF headers, runtime/lock/license records; no Linux executable or installation ran |
| TypeScript | Full `npm run typecheck` passed after component rename; also passed after final lease implementation | Windows checkout/type resolution |
| Web production build | `npm run build` passed, 1621 modules | Build result, not browser/installer UI acceptance |
| Whitespace | `git diff --check` and staged checks passed | Source hygiene only |

Node was downloaded from official Node distribution metadata/checksums into private task tooling; .NET SDK/runtime was likewise private. npm dependencies were restored from the committed lockfile with lifecycle scripts disabled. No global runtime or persistent PATH setting was installed, modified or removed.

The final native tests demonstrate actual key reads through the ACL boundary, refusal of app-owned setup capabilities, two-process data exclusion, failure with a missing helper, parent-death cleanup, and retention of an exclusive lease after the helper is killed while its Node owner stays alive. The last test starts `src/main.ts` with a real mock connector on loopback, checks health, rejects a writer using a different port, closes through inherited IPC, restarts the same store, handles IPC disconnect, and reacquires the lease afterward.

The app child has an empty PATH; `where.exe` cannot find Node, Python, dotnet, ffmpeg or Caddy through it. Development .NET roots/startup hooks/shared store and NODE_OPTIONS are removed. The test deliberately launches Node by its absolute private executable path. This is **PATH independence for tested behavior**, not proof that a family-facing installer has bundled all dependencies, that media/TLS works without other installed components, or that a clean supported OS has passed installation. Full Windows dependency/DLL closure, verified ffmpeg/Caddy distribution and Linux runtime ABI/stdlib/library closure remain gates.

An intermediate native test used a 5-second deadline for a second cold Node startup and timed out under the development host's load. The deadline was aligned to the bounded startup allowance; the second process uses a different port so a bind conflict cannot mask missing lock exclusion. The final run passed. No failure was hidden or converted to a skip.

## Review and untouched boundaries

Self-review found and corrected a critical lease-lifetime gap: a lock owned solely by the helper could disappear before Node observed helper death. The helper now duplicates a noninheritable file handle into the same-identity Node owner, retaining an exact process handle to close it during normal release. The lock survives helper death and closes when Node exits. The dedicated helper-crash test proves this with a living owner and a refused second writer. Minimal duplicate/query/synchronize rights and a token SID check are used; unrelated processes are not targeted.

Other reviewed points: no-follow pinned ancestor chains, same-handle ACL/read validation, hardlink rejection, private existing data descendants, strict administrator-owned/read-only bootstrap policy, shared service-identity rejection, sanitized helper environment, bounded protocol/error output, explicit helper EOF/release and bounded shutdown. This is self-review; independent review remains required before production security/installer acceptance.

`src/store.ts`, `src/db.ts`, `src/database-schema.ts`, all quota APIs/UI and `scripts/recover-account.py` are unchanged. No database migration/version was added. The only web edit is the celebration component rename/import adjustment. Existing backup/restore still has POSIX-specific checks and has not been accepted on Windows. The native updater, recovery barrier and installer/app authority handoff are not implemented by this increment.

## GUI blocker and host safety

No additional GUI launch was attempted in this increment. Supported Computer Use documentation exposes an app ID or explicit executable path, but no extra exact-match control. The previous owner-approved explicit-path launch returned Visual Studio Installer, with no observed Yahad process/window. No reliable Yahad app identifier/window is available to bind; no guessed selector or alternate route was used. Visual execution remains **unverified**, not denied, and no approval is currently pending. No screenshot, RTL/resizing or accessibility result is claimed. The unrelated Visual Studio Installer was not interacted with.

No services, account enrollment, firewall/CA/security settings, real provider/family data, manual Actions run, push/PR/release/website deployment or shared-runtime removal occurred. Remaining production gates are listed in [the implementation notes](../../docs/NATIVE-PORTABILITY.md) and [acceptance plan](../../docs/NATIVE-INSTALLER-ACCEPTANCE.md).
