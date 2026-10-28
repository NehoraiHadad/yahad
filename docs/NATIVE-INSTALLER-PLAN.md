# Next-release native installation plan

Current dependency/lifecycle/access checkpoint: [Private runtime packaging and stage-one handoff](NATIVE-RUNTIME-PACKAGING.md). Earlier counts below describe their respective increments.

Current continuation: see [native app portability and dependency status](NATIVE-PORTABILITY.md). The feasibility-only results below describe the first increment.

Status: feasibility implementation, not production acceptance. The released v0.5.2 and its installer remain unchanged. No release version/schema change is made here. Quotas, session accounting, schema migration and recovery compatibility belong to the separate quota task.

## Experience and technology decisions

The intended path is a Hebrew RTL download page → one OS-specific download → native setup with clear consent → local browser family onboarding → optional tablet setup. A private computer remains the server; it must be awake. Setup must never promise tablet access while the server sleeps.

Use **WPF on self-contained .NET 10, Windows x64** for the first Windows implementation. The available host initially had no .NET SDK, but an official SDK ZIP was downloaded and hash-verified into the task workspace without installing it. The SDK includes WPF targeting/runtime packs, enabling a compiled native desktop slice now. WPF avoids requiring the Windows App SDK/WebView2 deployment path just to display an installer. It supports RTL, native accessibility, keyboard focus, scaling and vector rendering. The visual language follows the app's green `#157C69`, sage/cream surfaces and message-heart motif; the drawn mark is a temporary vector adaptation, pending an authoritative brand asset. There is no white image background.

Publish each executable with its .NET runtime. Keep trimming off for WPF. Code signing and executable timestamping are release gates; no certificate purchase, credential creation or signing-key enrollment was performed. Unsigned development executables are not an acceptable public-download experience; do not instruct parents to bypass SmartScreen or AV warnings.

Use a **separate native service host** for lifecycle, with an unprivileged service identity and Windows Job Object containment. The current slice implements and runs containment, not SCM registration. For full hosting, prefer Microsoft's `Microsoft.Extensions.Hosting.WindowsServices` integration rather than maintaining a handwritten SCM dispatcher. Stop/shutdown handlers must return promptly and pass cancellation to a worker. A restricted local named pipe must ask the app to close Fastify/SQLite and release its data lock, with a deadline before job termination. Do not substitute Windows `SIGTERM` assumptions or routine forced termination for graceful shutdown. A running service before login is the target, not yet a verified promise.

For packaging, keep MSI/bootstrapper authoring as the next explicit build-tool decision: WiX has an Open Source Maintenance Fee for qualifying use; Inno Setup's published license permits commercial use with conditions while its site requests commercial licenses. Neither tool was installed or purchased. A WPF build/publish proves the UI/runtime path independently of that unresolved distribution decision. Do not create a homegrown privileged file copier as a shortcut. The selected engine must offer owned-component registration, repair, rollback, reboot resume and keep-data uninstall; reject it if it cannot preserve the ownership split below. The current self-contained EXE is a fixture executable, not a disguised MSI or full installer.

## File ownership and authority

| Owner | Proposed Windows location | Rule |
| --- | --- | --- |
| Bootstrap installer | `%ProgramFiles%/Yahad/Host` | Fixed service host, setup/repair executable, uninstall registration. Administrator-owned. App cannot replace it. |
| Application updater | `%ProgramData%/Yahad/Packages/<version>` | Verified immutable app payload, private Node 24 runtime and production dependencies per OS/architecture. Installer does not repair/rollback this tree after handoff. |
| Application updater | `%ProgramData%/Yahad/UpdateState` | Active-version record, policy, pins, request/status protocol, journal and recovery barrier; separate authority from app service. |
| App service identity | `%ProgramData%/Yahad/Data` | Database/media/auth data. No installer overwrite, cleanup or rollback writes. |
| Owner + tightly scoped services | `%ProgramData%/Yahad/Secrets` | Master/auth/bootstrap/backup secrets; specific SID DACLs, no generic Users/LocalService broad access. |
| Recovery component | `%ProgramData%/Yahad/Backups` | Encrypted backups and restore staging with distinct rights; retention must preserve existing policy. |

The bootstrap engine owns its host plus service registration, not app payloads. On a new install it passes the initial version to the updater through an explicit transaction, then records that handoff. On reinstall it detects existing state, policy and identity and requests health/repair through the updater; it never drops a bundled older app into the active slot. A bootstrap upgrade may not implicitly change an app version or schema. Use a shared **named kernel mutex or locked handle with a reviewed DACL**, not two unrelated updater/installer locks. The current fixture lock demonstrates exclusion only within one test session.

Windows updater work is a substantial adapter, not a port of commands: replace `fcntl/flock`, `pwd/runuser`, uid/chown, `systemctl`, active symlink switching, memfd seals and `ExecStopPost` with explicit Windows lock/identity/ACL/service/immutable-handle/recovery equivalents. Use an ACL-protected atomic version record rather than privileged symlinks. Startup must refuse pending recovery before opening writable application data. Backup restore must be isolated and authenticated before activation. Journal durability across power loss, volume behavior and AV locks requires machine tests; `Flush(true)` and atomic rename in fixture tests do not establish power-loss acceptance.

Normal uninstall removes only owned bootstrap integration after coordinating with the updater and stopping its jobs. It keeps Data, Secrets, Backups, policy/pins and a reinstall receipt by default. It never removes shared runtimes or unrelated services. Permanent removal is a separate explicit authenticated owner operation with a concrete backup/data warning and no default selection. No uninstall implementation is shipped in this slice.

## Runtime portability audit and app integration gate

Keep the existing Fastify/React/SQLite app. Build the web assets and install audited production dependencies on the **build machine**, with platform/architecture-specific native modules verified. Include private Node 24 and ffmpeg/Caddy as appropriate; never run `npm install` on the family's machine. Pin archive URLs/hashes and dependency lockfiles in the build manifest. Hashes prove consistency, not independent publisher authenticity. Preserve the existing HTTPS-only update trust option and policy/pins; do not enroll updater signing keys against the owner's preference.

Before launching the app natively, audit at least:

| Existing assumption | Required native equivalent |
| --- | --- |
| `src/config.ts` master-key POSIX mode check | Verify expected owner and DACL, regular file and no reparse ancestry using a reviewed native boundary. Never just skip mode validation on Windows. |
| `src/bootstrap.ts` root uid, POSIX mode and `O_NOFOLLOW` | Admin-owned time-limited setup capability; scoped service read access and Windows no-follow/handle validation. The fragment `#setup=…` is already consumed by the frontend; preserve its secret handling and one-owner invariant. |
| `src/store.ts`, `src/lock.ts` chmod and filesystem lock | Windows ACL initialization/verification and reviewed cross-process data lock, including updater/restorer interoperability. Coordinate any store edits with quota work. |
| `src/main.ts` POSIX signals and `/etc` defaults | Explicit ProgramData paths plus graceful private IPC stop. No catch-all weakening of startup safety. |
| Root update worker/sandbox model | Separate updater identity, service-control rights, read-only app launch handles and durable pending-recovery gate. |

No files in `src`, `web`, schema/recovery scripts or existing `deploy` were changed. This avoids the quota task's store/db/recovery ownership entirely. The schema is treated as an app contract, never hard-coded to 2 in the new installer tests.

## Network, browser and TLS

Start local setup on a numeric loopback address on an available exclusive port. Browser actions must only use an endpoint obtained from the local host's verified startup result, not an arbitrary URL from downloaded metadata. Reuse the existing short-lived setup capability fragment; do not log it, persist it to download-site analytics, put it in query strings or expose it to a public listener. Verify Origin/Host, capability consumption, expiry, pre-existing parent lockout, and simultaneous setup requests before opening a real onboarding URL.

Offer private LAN setup separately. Enumerate up adapters, display all RFC1918 IPv4 choices and require explicit selection even if one seems likely; VPN/private adapters cannot be reliably inferred. Recheck selection before binding. The implemented logic rejects public, loopback, link-local, noncanonical numeric and IPv6 addresses; IPv6 support is not claimed. A preflight bind is advisory because another process may win the race; listener failure must produce recovery UI and never kill/reconfigure another app. No router/UPnP/public exposure. Any eventual firewall rule must be owned, application-specific, private-profile and local-subnet scoped, with explained owner consent; preserve unrelated rules. Domain/public-profile handling needs a supported policy, not an automatic exception.

For LAN HTTPS, retain the reviewed private-CA model with an isolated proxy identity and key ACLs. Explain separately what trust means. Local browser trust needs explicit OS action; each tablet needs its own public-root certificate installation and fingerprint verification. The installer cannot silently trust a certificate on another device. Never copy a CA private key or tell parents to bypass browser warnings. Certificate SANs must match the selected IP/name; adapter/IP changes need a repair path and cannot silently reset trust. Test sleep/wake, DHCP/IP changes, reboot, port reuse and multi-adapter behavior before advertising this path.

## Linux route

Reuse the current reviewed `deploy/install.py` and updater recovery/receipt/service architecture. Do not discard its backups, safe ownership checks or policies to achieve a prettier launcher. The easy-install successor should ship **separate signed/verified per-target bundles** plus a small graphical launcher that explains the one polkit consent, feeds a strict structured plan to the reviewed root helper, and opens the same local browser wizard. There is no Linux implementation or machine acceptance in this Windows feasibility increment.

First target candidates: Debian 12/13 amd64 with systemd, Ubuntu 24.04 LTS amd64 with systemd, and Raspberry Pi OS 64-bit Bookworm arm64. Each needs a separately built/tested dependency closure, including glibc/OpenSSL/ffmpeg/Caddy and Python installer/updater runtime. A Node archive alone does not remove those dependencies. Choose bundled private dependencies or versioned distro dependencies after checking actual target ABI/licensing; do not claim an offline single-file Linux installation until that closure is tested. A `.deb`/polkit experience may still ask for a system password and needs a GUI-less Pi alternative with one documented command. Alpine/musl, non-systemd distros, Pi armhf, Windows ARM64 and macOS are outside the first boundary.

## Build, legal and distribution gates

Include the exact Node distribution LICENSE and third-party notices, npm dependency notices/SBOM, .NET notices and the exact ffmpeg build configuration/source correspondence. FFmpeg's enabled components change LGPL/GPL obligations; reject `--enable-nonfree` redistribution and review the actual codec/build choices before shipping. Preserve Caddy/Go module notices. System CA/private keys never belong in an app archive. Shipping .NET inside the bootstrapper and Node inside versioned app payloads keeps maintenance ownership clear; both require a security servicing policy.

Only after packaging, signing and disposable-machine acceptance should a static download page be added. It should offer explicit Windows x64/Linux amd64/Pi arm64 choices, verified release links, size/version, support boundaries, trust instructions and an accessible recovery/help path. Never show a download button as working before its matching artifact exists. GitHub Pages is a suitable possible host; no Pages config, workflow, deployment or manual Actions run was created.

## Increment sequence

1. **Done in this branch:** native WPF fixture UI, private DACL staging/journal recovery, archive validation, native process containment tests, network selection logic, self-contained publish path and acceptance plan.
2. Port app startup/key/bootstrap/locking/shutdown safety with coordinated app changes; run mock connector and synthetic data under bundled Node 24. This is required before the UI can say “Yahad is running.”
3. Native service lifecycle and recovery adapter, installer/updater ownership contract, strict privileged helper and packaging engine. Exercise install/repair/keep-data uninstall in disposable Windows snapshots.
4. Bundle build dependency closure and existing Linux helper integration; test amd64 and Pi arm64 separately.
5. TLS/LAN/tablet consent flow, real failure/reboot testing, signed artifacts, reviewed download-site implementation and coordinated Draft PR/publication.

## Official sources checked during feasibility

- [Microsoft: Windows service hosting](https://learn.microsoft.com/en-us/dotnet/core/extensions/windows-service)
- [Microsoft: self-contained single-file deployment and per-RID builds](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)
- [Microsoft: Job Objects](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects)
- [Microsoft: suspended process creation flags](https://learn.microsoft.com/en-us/windows/win32/procthread/process-creation-flags)
- [Microsoft: service control handler deadlines](https://learn.microsoft.com/en-us/windows/win32/services/service-control-handler-function)
- [Microsoft: service user accounts](https://learn.microsoft.com/en-us/windows/win32/services/service-user-accounts)
- [WiX: Open Source Maintenance Fee](https://docs.firegiant.com/wix/osmf/)
- [Inno Setup features](https://jrsoftware.org/isinfo.php) and [published license](https://jrsoftware.org/files/is/license.txt)
- [Node release support](https://nodejs.org/en/about/previous-releases)
- [FFmpeg build/license requirements](https://www.ffmpeg.org/legal.html)

These sources guide implementation choices; they do not establish that this installer has passed Windows or Linux acceptance.
