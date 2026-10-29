# Private runtime packaging and stage-one handoff

Latest private FFmpeg/Caddy and recovery checkpoint: [Private dependency and recovery checkpoint](NATIVE-DEPENDENCIES-CHECKPOINT.md).

This increment adds an auditable Windows runtime **candidate**, native host/update contracts and an installer-only LAN planning window. It is not a complete installer, a registered service or a claim of successful tablet access. Previous evidence remains in `NATIVE-PORTABILITY.md`; the current checkpoint supersedes its dependency status.

## Installation boundary

Stage one installs private dependencies, provisions the reviewed service/data boundaries, starts the server, establishes web reachability and opens the existing web app. Stage two is the existing web UI for parent/child setup, WhatsApp QR and chat permissions. It is not recreated in WPF. Existing families go to normal login.

`WebHandoff` consumes the existing `complete`/`ready`/`closed` states from `/api/setup/status`. Existing families discard any obsolete capability; fresh setup uses the existing short-lived `/#setup=...` fragment. The token is excluded from object serialization and diagnostic string output. Closed/expired setup never silently creates a new family. Only configured loopback or private-LAN HTTPS origins are accepted. This is a tested contract, not a wired transport: the eventual service-to-owner capability channel still needs authenticated, owner-only IPC. Do not relax the existing administrator-owned/read-only bootstrap ACL to make the UI read it.

`LanPlanWindow` enumerates current private IPv4 adapters read-only, requires explicit selection, explains changing IPs and per-device local-CA trust, and saves only an `ActionsAuthorized=false` plan. It makes no firewall, CA, DNS, service or router changes. Host health and observed second-device reachability are distinct; a completed network installation requires the latter and confirmed trust. Child entry is an optional end-to-end integration check through the existing web UI. The window does not generate an active link/QR, claim a stable hostname or report successful reachability. A reliable GUI launch route remains unavailable, so this screen is compiled but has not been visually inspected.

Managed DNS-01 certificates remain an unselected future provider; no domain, DNS credentials, tunnel or infrastructure was created. Internet access is disabled. Local CA trust cannot silently extend to tablets. The future execution consent must identify the exact program, TCP 443, Private profile and LocalSubnet scope and handle a port/address change without disturbing unrelated listeners.

## Windows package content and private runtimes

`python -m installer.windows.bundle --prepared <build-tree> --output <new.zip> --profile full` builds offline from already prepared files. It never installs, downloads, starts a service or runs npm on the family's computer. It hashes every file and writes deterministic ZIP metadata, preserving an existing output. It refuses live state/private files, reparse/symlink/hardlink inputs, unsafe/case-colliding paths, missing production modules, unsupported PE architectures and incomplete runtime/provenance records.

The `full` content contract requires Node 24, source/production modules/built web assets, the app-private self-contained .NET helper, ffmpeg, Caddy and legal/inventory records. The separate `core-fixture` profile deliberately requires only Node/app/helper. Every candidate records `familyInstallationReady=false`; it cannot be fed into the existing fixture-only installer as production. A successful content check does not verify a release signature or authorize activation.

`installer/build.ps1 -Publish` now publishes Platform as a **self-contained folder**, including `.deps.json`, `.runtimeconfig.json`, CLR and runtime DLLs. This permits inspection of all runtime files and avoids single-file extraction of the service helper into a user's profile cache. Setup and Host.Slice remain self-contained single-file development artifacts. Generated root `native/` is ignored in Git.

`pe.py` checks bounded PE headers/imports and x64 or pure AnyCPU IL. `bundle.py` checks the private .NET dependency manifest and resolves native imports against an explicit Windows OS contract list or the same packaged directory. It never searches developer PATH, globally installed .NET or arbitrary package folders. Native Node addons alone may import the fixed hosting `runtime/node/node.exe`. VC runtime DLLs must be included when imported. This is a static dependency audit; dynamic loads, supported Windows versions, CPU features and AV behavior still require machine acceptance.

`installer/windows/prepare_core_fixture.py` prepares a developer fixture from a clean source checkpoint and explicitly supplied private Node/.NET tooling plus an existing npm cache. It runs `npm ci --omit=dev --ignore-scripts --offline` **on the build machine**, with isolated npm configuration and no network fallback. Input tooling must first be extracted from verified official archives. It does not establish that chain of trust itself and is not a release pipeline. Rebuild Platform from the same source checkpoint first. Failed preparation leaves its new partial directory for diagnosis; it never deletes or overwrites an installation.

`tests/windows-package-runtime.test.mjs` can be launched using the prepared private Node executable, its `app/node_modules/tsx/dist/loader.mjs` loader and `YAHAD_PREPARED_WINDOWS_CORE=<prepared>`. It creates synthetic private data under temporary storage, binds only loopback, forces the mock connector and sets the child PATH empty. It proves the packaged app starts, reports the current source schema, excludes a second writer on another port, closes through inherited IPC and restarts the same synthetic store. It does not exercise ffmpeg, TLS, native SCM hosting or installation.

## Native host/update ownership

`AppLifecycle` serializes lifecycle transitions with a bounded deadline. `IPrivateAppLauncher` owns a validated private executable and contained app session; `IAppRecoveryBarrier` must allow every start. Graceful root exit with code zero plus a fully empty contained process tree are required before an internal `QuiescentApp` grant reaches the sole `IAppUpdateAuthority`.

`JobProcess.WaitForTreeEmptyAsync` uses the retained native Job Object accounting handle, not PID scanning. Tests demonstrate that an exited root with a living child cannot pass this barrier. A cancelled/stuck/unclean shutdown cannot activate an update. Uncertain activation or failed new health leaves recovery required; the host does not automatically restart the old payload or restore/overwrite a database after a possible schema write.

These are interfaces/controller and tested native process primitives, **not concrete production SCM/updater/recovery adapters**. The future updater must preserve policy/pins, durable journals, backups and recovery barriers. The installer owns bootstrap/host; the updater owns versioned payloads. No second update authority was added.

Quota integration is coordinated separately. The latest parent update targets **schema 5**, commit `d5f787fe1a924a0689541abed1a24c6c185dd004`; the parent reports independent Linux validation passed (305 JavaScript tests, typecheck/build, migrations 1/2/3/4 to 5 and 365 Python tests, with 57 root-gated skips). This branch makes no quota/store/schema/recovery edits. Its runtime test imports the current schema instead of hardcoding a number. The two branches' independent Windows ChatCelebration filename fixes must be deduplicated at integration.

## Dependency supply status

| Component | Evidence now | Remaining gate |
| --- | --- | --- |
| Node | Official private Node 24.22.2, production dependency restore entirely offline; real packaged execution with empty PATH | Release pipeline provenance/signature verification and clean Windows acceptance |
| .NET helper | Complete private 10.0.14 runtime folder, dependency manifest and PE audit; actual app uses it | Servicing policy, protected service payload/identity and clean supported OS acceptance |
| Caddy | Official v2.11.7 archive SHA256 matched release API; SHA512 matched official checksums; extracted and PE-inspected without execution | Bundle integration, TLS config with trust auto-install disabled, explicit consent, network/trust/reboot acceptance |
| ffmpeg | Full-candidate input/provenance/legal contract and failure tests | A verified Windows build from official source plus required libopus/codec features, licenses/source/relink information and media tests |
| Python/OpenSSL | Windows production replacement interfaces remain incomplete; Linux candidate explicitly inventories them | Windows updater/recovery adapters; Linux app-private runtime/ABI integration and real amd64/Pi arm64 acceptance |

The source app currently invokes ffmpeg for voice-to-Opus conversion and JPEG previews (`src/media.ts`). This is local media processing. FFmpeg publishes source and links community Windows builders; those are not FFmpeg-published binaries. Under the delegated official-source restriction, no community build or unrelated installed Gyan binary was repackaged. This host lacks a Windows C++ build toolchain; no compiler, MSYS, machine runtime or global dependency was installed to fill that gap. Full production packaging remains incomplete and fails closed when ffmpeg is absent.

The observed Caddy checksum file uses SHA512. An initial comparison using SHA256 failed; its algorithm was reconciled against the actual 128-character official record, and both the API SHA256 and checksum-file SHA512 then matched. No executable was run to resolve the mismatch, and no checksum check was bypassed.

## Remaining acceptance

Clean supported Windows x64 with no runtimes; SCM before login and dedicated identity; authenticated host/UI IPC; private environment/handle inheritance and full descendant cleanup; updater/backup/recovery with schema 5 coordination; cancellation/reboot resume; offline/disk-full/AV-lock failures; data-preserving uninstall/reinstall; LAN/TLS/second-device access; and actual UI/RTL/resizing remain required. Linux amd64 and Pi arm64 each need real runtime and install acceptance; no general distro or Windows ARM64 promise is made. No deployment, signing purchase/key enrollment, live service, firewall/CA mutation or current-release change was performed.

Primary references: [PE format](https://learn.microsoft.com/en-us/windows/win32/debug/pe-format), [Job Object query](https://learn.microsoft.com/en-us/windows/win32/api/jobapi2/nf-jobapi2-queryinformationjobobject), [active process accounting](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_basic_accounting_information), [Windows BCryptPrimitives contract](https://learn.microsoft.com/en-us/windows/win32/seccng/processprng), [Node addon loading](https://github.com/nodejs/node-gyp/blob/main/src/win_delay_load_hook.cc), [FFmpeg distribution sources](https://ffmpeg.org/download.html), [FFmpeg licensing](https://ffmpeg.org/legal.html), [Caddy installation](https://caddyserver.com/docs/install), [Caddy signature verification](https://caddyserver.com/docs/signature-verification).
