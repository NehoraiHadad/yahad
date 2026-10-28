# Native app portability increment

Current dependency/lifecycle/access checkpoint: [Private runtime packaging and stage-one handoff](NATIVE-RUNTIME-PACKAGING.md). Earlier counts below describe their respective increments.

This follows the initial installer feasibility slice. It adds actual Windows mock-app execution and build-only Linux candidate packaging. It is **not a production installer or a complete Windows port**.

Working directory: `C:/projects/yahad-native-install`, a separate worktree of the official clone at `C:/projects/yahad`. The latter directory was inspected empty before cloning. The verified local bundle imported `15e05a0b61267f9f8486bc7d72059221fe0dfcb0` on the same `feat/native-installer-slice` branch, based on released v0.5.2. The original task checkout and its artifacts remain preserved.

## App-facing platform boundary

`src/platform-files.ts` selects the actual OS automatically; no environment switch disables safety checks. `src/config.ts`, `src/bootstrap.ts`, `src/lock.ts` and `src/main.ts` now use that boundary.

Linux retains the data lock directory/heartbeat protocol and key/capability mode checks. File reads now open first with no-follow/nonblocking flags, validate the same descriptor's regular-file type, link count, size and permissions, then read. Capability root ownership and 15-minute expiry remain required. No Linux runtime acceptance was performed in this increment.

Windows uses the app-private **self-contained `native/Yahad.Platform.exe`**, with a bounded JSON protocol over redirected private pipes. No shell, PowerShell execution-policy change, runtime download or installer action is involved. The helper environment omits .NET startup hooks/profiler variables and development runtime roots. Missing/failed helpers fail closed; there is no fallback that ignores permissions or uses POSIX chmod as a substitute.

The Windows helper:

- Opens each path component with `OPEN_REPARSE_POINT`, holding ancestor directory handles without delete sharing until the operation finishes. This refuses junctions/symlinks and prevents ancestor/leaf rename while validating and reading.
- Reads owner/DACL from the **opened handle**, rejects null/unknown/broad allowed ACLs, hardlinks, special/noncanonical/UNC/ADS/device paths and volumes without persistent ACL support. It reads secret bytes from that same handle with a size bound; exceptions, paths and bytes never enter diagnostic output.
- Accepts private master-key/data ownership by the app identity, administrators or SYSTEM. A setup capability additionally requires administrator/SYSTEM ownership and read-only app access; app-owned capabilities are rejected. The positive administrator-owned capability case is an ACL-policy fixture, not a test that created an administrator-owned secret on the working machine.
- Rejects running as shared LocalSystem/LocalService/NetworkService identities. Actual service provisioning must use the reviewed isolated identity; no service account was created here.
- Validates existing data descendants, then holds a kernel-exclusive `process.lock` file and duplicates its noninheritable handle into the same-identity Node owner using minimal process/token rights. Helper death leaves the duplicate locked until Node exits; normal release closes the exact duplicate through the retained process handle. Parent EOF/exit releases the lease. Unexpected helper exit stops Node. Directory traversal holds its ancestor chain during recursion and has count/depth limits.

The Windows lock is a file; the retained Linux proper-lockfile protocol uses a directory at the same name. They are deliberately incompatible rather than concurrently writable. Existing foreign lock types are refused, never deleted as “stale.” An eventual Windows updater/restorer must use this same native lease protocol. Copying a live store between OSes is not supported.

`src/runtime-shutdown.ts` performs one bounded close → lock-release sequence for POSIX signals or an inherited Node parent IPC message. IPC disconnect also closes the app. There is no public shutdown endpoint. This is exercised with an actual forked mock app; the .NET SCM host still needs its native lifecycle/IPC integration and Job Object containment. Setting up a Windows service before login is not yet delivered.

`Store`/database/schema/recovery code was not changed. Existing Store chmod calls remain; under the tested Windows main entry point the root/descendants have already passed the native ACL boundary, and newly created SQLite/media files inherit that private root's ACL. Direct internal `new Store()` test APIs do not establish managed production provisioning. Backup/restore still contains POSIX checks and is **not accepted for Windows**; its format, backup policy and recovery barriers must be preserved in a coordinated later adapter. No quota or schema version was implemented here.

## Dependencies: current evidence and remaining requirement

| Component | Current handling | Still required for family installation |
| --- | --- | --- |
| .NET | Platform helper, fixture host and WPF UI publish with their native runtime included | Signed bootstrap/service distribution, private extraction/cache directories, Windows DLL/VC runtime closure and security servicing policy |
| Node 24 | Official Windows x64 ZIP checksum-verified into private task tooling; real app launched by absolute executable path | Put the pinned runtime inside versioned app payloads, validate exact bytes and architecture, never use a global Node install or alter PATH |
| npm/production modules | Locked dependencies restored on the build machine with lifecycle scripts disabled; app tests and web build succeeded | Target-specific audited production export/SBOM; no npm install on the parent's computer |
| ffmpeg | Existing synthetic media/security tests passed on the development host, but those are not clean-machine evidence | Private verified target binary plus required DLL/library closure, exact build/source/license notices and codec acceptance |
| Caddy/TLS | Not executed or installed in this increment | Private target-specific proxy, isolated identity, explicit trust/consent and tablet flow; no global replacement |
| Python/OpenSSL | Linux engine candidate requires private runtime entries; no Python is needed by the new Windows ACL helper | Linux ABI/stdlib/library closure and helper path integration. Evaluate Windows requirements from the final native updater/TLS design; do not fall back to globally installed tools |

The mock app test gives its child an **empty PATH** and removes development .NET roots/startup hooks and NODE_OPTIONS. Explicit `where.exe` checks cannot locate Node, Python, dotnet, ffmpeg or Caddy through that PATH. The app still starts using the absolute private Node executable and self-contained helper, serves loopback health, rejects a second writer on a different port, closes through private IPC and releases its lease. The test also restarts the same synthetic store and exercises IPC disconnect.

This proves independence from development PATH for the paths exercised. It does **not** simulate a freshly installed OS, prove all media/TLS dependencies exist, or establish that a single installer already bundles them. No global runtime, environment setting or unrelated service was removed or replaced.

## Linux candidate builder

`installer/linux/package_bundle.py` creates deterministic gzip/tar **runtime candidates** for `linux-x64` and `linux-arm64`. These are not release assets accepted by the existing updater. They have `kind: native-runtime-candidate` and `runtimeAcceptance: not-performed`.

Build-machine input consists of `app/` (built frontend, sources and prepared production modules), `runtime/` and `legal/`. It requires Node/Python/ffmpeg/Caddy/OpenSSL executables, the target's esbuild binary, exact app/lockfile agreement, runtime version/hash/source records, an SBOM and license/provenance files. All ELF files are checked for target 64-bit architecture; mixed-architecture native modules are refused. Symlinks/reparse points, hardlinks, private-state files and existing output replacement are refused. Tar ownership/timestamps/modes are deterministic; the final candidate appears with a no-replace hardlink after content hashes have been rechecked during streaming. Build output must support hardlinks.

The current checkout's Linux deploy engine is copied into `engine/deploy/` as build input. Its safety/recovery code is retained; the builder does not run it. Its system paths have **not** yet been adapted to bundled private dependencies. Target glibc/OpenSSL/loader/library closure, Python stdlib and native-extension closure, distro dependency choices, polkit launcher, installation and recovery remain gates. Debian/Ubuntu amd64 and physical Pi arm64 require separate actual-machine acceptance.

The runtime lock format is a JSON object with `schema: 1`, exact `target`, and five `components` entries: `path` from the builder's RUNTIMES list, explicit `version`, lowercase executable `sha256`, and public `https://` source URL without credentials/query/fragment. Node must be pinned to `24.x.y`. Checksums/provenance establish build consistency, not independent publisher authenticity or ABI completeness. No updater signing key is enrolled.

Developer-only invocation, with Python 3.11+ on the build machine:

```sh
python installer/linux/package_bundle.py --prepared /path/to/prepared-linux-x64 --target linux-x64 --output /path/to/candidates/yahad-linux-x64.tar.gz
```

## UI execution and remaining acceptance

The previous owner-approved exact-path Computer Use launch returned Visual Studio Installer rather than a Yahad window. This increment inspected the supported app-selection API: it exposes an app ID or explicit executable path but no additional exact-match selection flag, and no Yahad window/returned app ID is available to bind. No guessed identifier, alternate route, unrelated installer interaction or further launch attempt was made. Visual execution, screenshots, RTL resizing and accessibility remain unverified because reliable exact-target dispatch is unresolved. The approval itself is not pending.

Still required: a production Windows dependency bundle and chosen installer engine; actual SCM service/identity/ACL provisioning and graceful host IPC; signed host/payload validation and updater authority handoff; backup/restore/recovery integration; default keep-data uninstall/reinstall; LAN/TLS/tablet consent; clean supported Windows x64 and separate Linux amd64/Pi arm64 acceptance; independent code review. No live services, firewall/CA/security changes, family data, real WhatsApp, manual Actions run, publication or deployment occurred.

Official API details were checked against [Microsoft CreateFileW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew) and [handle-based GetAccessControl](https://learn.microsoft.com/en-us/dotnet/api/system.io.filesystemaclextensions.getaccesscontrol?view=net-10.0); the implementation uses GetSecurityInfo on the held handle. Machine behavior is supported only by the recorded synthetic checks, not by those references.

The service host must provide administrator-provisioned, app-private temporary/extraction directories for self-contained native libraries and a sanitized environment before any secret read. Development tests use task-local temporary directories; global Temp/cache trust and a clean OS DLL dependency closure are not established here.

Additional native handle references: [DuplicateHandle](https://learn.microsoft.com/en-us/windows/win32/api/handleapi/nf-handleapi-duplicatehandle), [OpenProcessToken](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-openprocesstoken), and [minimal process rights](https://learn.microsoft.com/en-us/windows/win32/procthread/process-security-and-access-rights).
