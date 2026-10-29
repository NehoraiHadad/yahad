# Private dependency and recovery checkpoint

Historical checkpoint at `3bf951b`. See `NATIVE-INTEGRATION-CHECKPOINT.md` for the completed source import, native audit fixes, schema-5 integration and rebuilt combined payload.

This supersedes the supply-status section of `NATIVE-RUNTIME-PACKAGING.md`. Windows now has a **full binary-content candidate** with private Node, production modules, self-contained .NET helper, FFmpeg and Caddy. This is not a family installer, Windows service installation or completed distribution/legal acceptance. `familyInstallationReady` remains false.

## Fixed vendor inputs

`installer/windows/dependency-pins.json` records exact archive URLs/checksums, publisher/origin, version, license and every selected runtime/license file's size/SHA256. `dependencies.py` works offline: it checks the entire archive, the independently named publisher checksum file and its exact archive entry, then checks selected member bytes before writing a new directory. It refuses overwriting an existing target, unsafe/link/colliding ZIP entries, changed licenses, missing or extra runtime DLLs and self-asserted vendor provenance. No family machine downloads or runs npm during this workflow.

- **FFmpeg**: BtbN, `autobuild-2026-10-28-19-47`, `ffmpeg-n9.0.2-37-gae19868753-win64-lgpl-shared-9.0.zip`. The actual binary reports `n9.0.2-37-gae19868753-20261028`. Archive SHA256 `96c8534b790325d1bf8b611b80716e97b501dc28680a75e5b548e74409f4b895`; publisher checksum-file SHA256 `72b4c1fe06bfb3aea6aecc1f1647d2d4c26a80760253f1931ef46f18ed09d9bc`. This is an **upstream-linked vendor build**, not an FFmpeg-published binary. Only ffmpeg.exe, its seven sibling libav DLLs and the upstream license are selected; ffplay/ffprobe and development import libraries are excluded.
- **Caddy**: official caddyserver v2.11.7 Windows amd64 archive. Archive SHA256 `0a1edc0b799512051c57071ce0e798d3f2cf67dc98171366f1d2b326072e3b06`; official checksum-file SHA256 `5d27b76b95f496638d79208c55250317b8755fd9e90005f7466aa32956944a90`. Its published archive entry uses SHA512; both that exact entry and the release-API SHA256 matched. Binary, upstream LICENSE and README bytes are pinned.

The existing static PE audit covers each private runtime DLL and every production native Node module. D2D1 is now an explicit documented Windows desktop OS dependency, not an arbitrary DLL search exception. Missing VC/private libraries still fail. The vendor's minimum claim is Windows 10 22H2; this branch does not extend it to Server Core, old Windows, ARM64 or a clean-machine result. Actual supported OS acceptance remains required.

FFmpeg's runtime output confirms shared libraries, libopus and version-3 licensing, with GPL/nonfree switches absent. This enables replacing compatible sibling LGPL libraries. Included legal material contains the vendor LGPL-3.0-or-later text, its incorporated GNU GPLv3 terms and the exact Go 1.26.8 standard-library license. Sources/hashes for supplemental texts are recorded; Caddy build-info and FFmpeg build configuration/license output are also preserved in the candidate.

**Distribution is still gated**: exact corresponding FFmpeg/dependency sources and complete transitive notices/licenses need completion/review; Caddy's Go module inventory is available from actual build-info, but its transitive license collection is incomplete. The generated `Caddy-NOTICES.txt` explicitly identifies itself as packaging attribution, not an upstream NOTICE or a completed third-party audit. Hash verification is not Sigstore/release-signature verification. No signing credentials, updater key enrollment, paid license or service were created.

BtbN retains only a short window of daily builds. The fixed URL may disappear later; build inputs must be retained in the future authorized release pipeline. The family payload embeds the exact binaries and does not depend on that URL at runtime. No mirror, release or website was published here.

## Tests and local tooling

The actual private binaries were executed only for metadata, local in-memory media conversion and Caddy adaptation. These commands passed without developer tools on PATH. Tests exercise the existing `src/media.ts` functions, Opus round-trip, JPEG output, PNG/JPEG inputs and browser-style AAC-MP4/WebM inputs. The final extracted-candidate test evidence records which run passed; no real recordings or family/provider data is used.

`network_config.py` only generates a Caddyfile plan for a canonical RFC1918 IPv4 address and a loopback Node backend. It disables the admin endpoint, automatic redirect listener and trust installation, binds the selected address and configures internal TLS. The test calls **`caddy adapt` only**, without `--validate`, `run`, `start` or `trust`; the adapted JSON is checked for the intended private bind, no admin and `install_trust=false`, and no Caddy data/CA directory is created. No listener, firewall rule, certificate, trust-store change, DNS or router/tunnel action is performed. Actual tablet trust/reachability remains unproved.

Reproduction on a development machine with verified cached inputs:

1. `python -m installer.windows.dependencies stage` with the four archive/checksum paths and a new output directory.
2. `python -m installer.windows.dependencies augment --core <verified-core-tree> --dependencies <vendor-tree> --output <new-full-tree>`.
3. `python -m installer.windows.bundle --prepared <full-tree> --output <new.zip> --profile full`.
4. `python -m installer.windows.verify_archive --archive <zip> --output <new-extraction> --report <new-report.json>`.
5. Run `tests/windows-package-runtime.test.mjs` and `tests/windows-dependencies-runtime.test.mjs` with the extracted private Node/tsx loader and their explicit prepared-root variables. The Caddy test also receives a file generated by `network_config.caddyfile('192.168.4.5',3000)`.

The verifier checks bounded archive membership, paths, regular-file types, every extracted size/hash, the re-derived entire candidate manifest and native/vendor closure. It is a development checker, not production signed-package activation. Failed new outputs are retained for diagnosis; existing installations are never overlaid or removed.

The actual full candidate extends the byte-verified `80fbead` app/helper snapshot from the previous core artifact, whose SHA256 was `d7a467b642bb9c8f233641bb4d8101d3a255348e0a93c81c5f48ca544216648c`. `legal/build-inputs.json` distinguishes that app source from this slice's packager source. New pure recovery logic is in source/bootstrap development code, not a claim that an SCM/updater host was installed or integrated into that app payload.

## Recovery and ownership

`UpdateRecovery.cs` adds pure decisions for staged/quiesced/backup/activation/start/healthy/rollback checkpoints. An exclusive data lease and an empty complete process tree are mandatory before a recovery action. Policy/pin drift refuses recovery rather than silently restoring older settings. A possible candidate start never triggers an automatic schema downgrade. Explicit owner rollback additionally requires a matching complete backup receipt and a schema the old release can read. Payload-only rollback is limited to durable proof that candidate execution never started. An interrupted restore requires reconciliation, not an automatic repeated write. Healthy restart checks the active release and its schema range.

The 4-to-5 schema numbers in these tests are synthetic compatibility fixtures; no quota migration or database code was changed. Real receipt authentication, durable journal I/O, SCM lifecycle/identity, backup/restore and owner IPC adapters remain engineering work. The planner does not restore a database, flip a pointer, install a service or grant itself update authority. Bootstrap owns the service host; the sole existing app updater owns versioned payloads, including private Caddy/FFmpeg. No Caddy self-upgrade path is used.

## Two-stage boundary and integration

Stage one starts/exposes the app and securely hands off to its existing web UI. Stage two remains family/children/WhatsApp/permissions setup in that UI. No second setup flow was added. Existing families must receive normal login. Authenticated owner-only capability transport and actual access-path execution remain pending; local host health is not proof of tablet access or universal trust. Managed DNS-01 certificates and Internet exposure remain unselected.

Schema-5 quota target remains `d5f787fe1a924a0689541abed1a24c6c185dd004`. It is not present as a Git object in this checkout, and the task directory contains only the three earlier installer source bundles. A verified source transfer of that commit is required before the requested separate clean integration checkpoint. No integration was guessed, no schema/recovery files were edited, and the independent ChatCelebration rename must be deduplicated then. The current binary-content candidate explicitly does **not** contain quota schema 5.

Remaining acceptance includes genuine native SCM/identity/IPC and update recovery; clean supported Windows x64 without runtimes and before-login behavior; cancellation/reboot, disk-full/AV-lock and offline failures; data-preserving uninstall/reinstall; actual UI/RTL and LAN/TLS/tablet access; and separate Linux amd64/Pi arm64 installs. Working-machine service/firewall/CA/security changes are still unauthorized. GUI routing remains unverified from the earlier Visual Studio Installer misselection; it was not retried. There is no pending approval or new executable-source rejection in this slice.

Primary references: [FFmpeg download publishers](https://ffmpeg.org/download.html), [BtbN variants and retention](https://github.com/BtbN/FFmpeg-Builds), [FFmpeg licensing](https://ffmpeg.org/legal.html), [Caddy official distribution](https://caddyserver.com/docs/install), [Caddy adapt](https://caddyserver.com/docs/command-line#caddy-adapt), [trust opt-out](https://caddyserver.com/docs/caddyfile/options#skip-install-trust), [Windows Direct2D contract](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nf-d2d1-d2d1createfactory).
