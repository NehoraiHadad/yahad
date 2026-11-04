# Private runtime packaging and stage-one handoff

Latest private FFmpeg/Caddy and recovery checkpoint: [Private dependency and recovery checkpoint](NATIVE-DEPENDENCIES-CHECKPOINT.md).

> The installer design this page assumed (a privileged Authority, recovery helpers, a C++ bootstrap and an installer LAN planning window) was removed. The current design is in [installer-architecture-review.md](installer-architecture-review.md); the runtime package contents and dependency supply status below still apply.

This increment adds an auditable Windows runtime **candidate**. It is not a complete installer, a registered service or a claim of successful tablet access. Previous evidence remains in `NATIVE-PORTABILITY.md`; the current checkpoint supersedes its dependency status.

## Installation boundary

Stage one installs private dependencies, provisions the reviewed service/data boundaries, starts the server, establishes web reachability and opens the existing web app. Stage two is the existing web UI for parent/child setup, WhatsApp QR and chat permissions. It is not recreated in WPF. Existing families go to normal login.



Managed DNS-01 certificates remain an unselected future provider; no domain, DNS credentials, tunnel or infrastructure was created. Internet access is disabled. Local CA trust cannot silently extend to tablets. The future execution consent must identify the exact program, TCP 443, Private profile and LocalSubnet scope and handle a port/address change without disturbing unrelated listeners.

## Windows package content and private runtimes

`python -m installer.windows.bundle --prepared <build-tree> --output <new.zip> --profile full` builds offline from already prepared files. It never installs, downloads, starts a service or runs npm on the family's computer. It hashes every file and writes deterministic ZIP metadata, preserving an existing output. It refuses live state/private files, reparse/symlink/hardlink inputs, unsafe/case-colliding paths, missing production modules, unsupported PE architectures and incomplete runtime/provenance records.

The `full` content contract requires Node 24, the prebuilt server bundle (`app/server/*.mjs`, pinned by `legal/server-bundle.json`)/built web assets, the app-private self-contained .NET helper, ffmpeg, Caddy and legal/inventory records. The separate `core-fixture` profile deliberately requires only Node/app/helper. Every candidate records `familyInstallationReady=false`; it cannot be fed into the existing fixture-only installer as production. A successful content check does not verify a release signature or authorize activation.

`installer/build.ps1 -Publish` now publishes Platform as a **self-contained folder**, including `.deps.json`, `.runtimeconfig.json`, CLR and runtime DLLs. This permits inspection of all runtime files and avoids single-file extraction of the service helper into a user's profile cache. Setup remains a self-contained single-file development artifact. Generated root `native/` is ignored in Git.

`pe.py` checks bounded PE headers/imports and x64 or pure AnyCPU IL. `bundle.py` checks the private .NET dependency manifest and resolves native imports against an explicit Windows OS contract list or the same packaged directory. It never searches developer PATH, globally installed .NET or arbitrary package folders. The payload carries no Node addons, so no image may import `node.exe`. VC runtime DLLs must be included when imported. This is a static dependency audit; dynamic loads, supported Windows versions, CPU features and AV behavior still require machine acceptance.

`installer/windows/prepare_core_fixture.py` prepares a developer fixture from a clean source checkpoint and explicitly supplied private Node/.NET tooling plus an existing npm cache. It runs `npm ci --ignore-scripts --offline` **on the build machine** in an isolated temporary build root, with isolated npm configuration and no network fallback, then `scripts/build-server.mjs` bundles the server entries with the locked esbuild; no `src/`, `node_modules` or loader is shipped. Input tooling must first be extracted from verified official archives. It does not establish that chain of trust itself and is not a release pipeline. Rebuild Platform from the same source checkpoint first. Failed preparation leaves its new partial directory for diagnosis; it never deletes or overwrites an installation.

`tests/windows-package-runtime.test.mjs` can be launched using the prepared private Node executable, this checkout's `node_modules/tsx/dist/loader.mjs` loader (for the harness only; the app child runs `app/server/main.mjs` with no loader) and `YAHAD_PREPARED_WINDOWS_CORE=<prepared>`. It creates synthetic private data under temporary storage, binds only loopback, forces the mock connector and sets the child PATH empty. It proves the packaged app starts, reports the schema recorded in `legal/build-inputs.json`, excludes a second writer on another port, closes through inherited IPC and restarts the same synthetic store. It does not exercise ffmpeg, TLS, native SCM hosting or installation.

## Native host/update ownership

Removed. The lifecycle, update and recovery machinery this section described (`IAppRecoveryBarrier`, `IAppUpdateAuthority`, the Authority and recovery services) no longer exists. Updates are now installed by `Setup --quiet-upgrade`, launched by the `Yahad.Updater` service, with a health-checked rollback; see [installer-architecture-review.md](installer-architecture-review.md) §3 and its status section.

Quota integration is coordinated separately. The latest parent update targets **schema 5**, commit `d5f787fe1a924a0689541abed1a24c6c185dd004`; the parent reports independent Linux validation passed (305 JavaScript tests, typecheck/build, migrations 1/2/3/4 to 5 and 365 Python tests, with 57 root-gated skips). This branch makes no quota/store/schema/recovery edits. Its runtime test reads the recorded build schema instead of hardcoding a number. The two branches' independent Windows ChatCelebration filename fixes must be deduplicated at integration.

## Dependency supply status

| Component | Evidence now | Remaining gate |
| --- | --- | --- |
| Node | Official private Node 24.22.2, production dependency restore entirely offline; real packaged execution with empty PATH | Release pipeline provenance/signature verification and clean Windows acceptance |
| .NET helper | Complete private 10.0.14 runtime folder, dependency manifest and PE audit; actual app uses it | Servicing policy, protected service payload/identity and clean supported OS acceptance |
| Caddy | Official v2.11.7 archive SHA256 matched release API; SHA512 matched official checksums; extracted and PE-inspected without execution | Bundle integration, TLS config with trust auto-install disabled, explicit consent, network/trust/reboot acceptance |
| ffmpeg | Full-candidate input/provenance/legal contract and failure tests | A verified Windows build from official source plus required libopus/codec features, licenses/source/relink information and media tests |
| Python/OpenSSL | Windows production replacement interfaces remain incomplete; Linux candidate explicitly inventories them | Windows updater (see the architecture review); Linux app-private runtime/ABI integration and real amd64/Pi arm64 acceptance |

The source app currently invokes ffmpeg for voice-to-Opus conversion and JPEG previews (`src/media.ts`). This is local media processing. FFmpeg publishes source and links community Windows builders; those are not FFmpeg-published binaries. Under the delegated official-source restriction, no community build or unrelated installed Gyan binary was repackaged. This host lacks a Windows C++ build toolchain; no compiler, MSYS, machine runtime or global dependency was installed to fill that gap. Full production packaging remains incomplete and fails closed when ffmpeg is absent.

The observed Caddy checksum file uses SHA512. An initial comparison using SHA256 failed; its algorithm was reconciled against the actual 128-character official record, and both the API SHA256 and checksum-file SHA512 then matched. No executable was run to resolve the mismatch, and no checksum check was bypassed.

## Remaining acceptance

Clean supported Windows x64 with no runtimes; SCM before login and dedicated identity; private environment/handle inheritance and full descendant cleanup; updater upgrade/rollback acceptance; cancellation/reboot resume; offline/disk-full/AV-lock failures; data-preserving uninstall/reinstall; LAN/TLS/second-device access; and actual UI/RTL/resizing remain required. Linux amd64 and Pi arm64 each need real runtime and install acceptance; no general distro or Windows ARM64 promise is made. No deployment, signing purchase/key enrollment, live service, firewall/CA mutation or current-release change was performed.

Primary references: [PE format](https://learn.microsoft.com/en-us/windows/win32/debug/pe-format), [Job Object query](https://learn.microsoft.com/en-us/windows/win32/api/jobapi2/nf-jobapi2-queryinformationjobobject), [active process accounting](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_basic_accounting_information), [Windows BCryptPrimitives contract](https://learn.microsoft.com/en-us/windows/win32/seccng/processprng), [Node addon loading](https://github.com/nodejs/node-gyp/blob/main/src/win_delay_load_hook.cc), [FFmpeg distribution sources](https://ffmpeg.org/download.html), [FFmpeg licensing](https://ffmpeg.org/legal.html), [Caddy installation](https://caddyserver.com/docs/install), [Caddy signature verification](https://caddyserver.com/docs/signature-verification).
