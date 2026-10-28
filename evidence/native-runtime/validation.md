# Private runtime / native lifecycle checkpoint — 2026-10-28 UTC

Worktree `C:/projects/yahad-native-install`; branch `feat/native-installer-slice`. Baseline remains v0.5.2 `4c41d5ef80ea349d0f678ee9c572e4975a2d776d`, tree `f4dde9cc1dc2d2abd374929a436d425bbc2e9f63`. No tag, release, live installation or unrelated checkout was modified.

Validated local source increments:

- `aee97a7`: deterministic offline Windows runtime candidate builder, bounded PE/private-DLL audit, private .NET folder publication, reproducible offline production-dependency fixture preparation and packaged-app integration test.
- `53eafa0`: serialized native lifecycle/recovery/update interfaces, whole-Job process-tree barrier, installer-only LAN planning and existing-web handoff contracts/UI.
- `80fbeade8e5749e0e0e1808b207990ff235e1672`: scope, supply status, source references and acceptance documentation. The final prepared core candidate is from this clean source checkpoint.

| Validation | Observed result | Scope |
| --- | --- | --- |
| .NET four-project builds | Passed, zero warnings/errors | Core, tests, fixture host, WPF Setup and Platform dependencies; Windows developer host |
| .NET harness | **65 passed, 0 failed**; exact JSON retained | Synthetic files/processes, real Windows ACL/handle/Job Object APIs; lifecycle adapters are fakes, not SCM |
| Windows package suite | **18 passed, 0 failed** | Synthetic PE/package records, no fixture executable runs; missing/corrupt runtime, undeclared namespace, private DLL closure, self-contained .NET, private-state and no-overwrite guards |
| Linux candidate suite | **14 passed, 0 failed** in combined run before the final Windows-only namespace test | Synthetic ELF/package records; Linux source was unchanged afterward; no Linux executable or install ran |
| Offline core preparation | **172 production packages restored**, lifecycle scripts disabled, `--offline`, no network fallback | Committed developer preparation tool from clean `80fbead`; private official tooling and existing task cache |
| Initial packaged mock app | **1 passed, 0 failed, 0 skipped**, 21.2 seconds | Pre-commit prepared tree, actual Node 24.22.2, production modules and private CLR, empty child PATH; final extracted-candidate run recorded separately |
| Final native artifacts | Setup and Host.Slice single-file; Platform complete folder; hashes retained | Published from `80fbead`; no installer/service launch or host installation |

The .NET harness adds a real native case where the root exits with code zero while its contained descendant remains alive. The Job Object cannot be declared empty until that descendant exits. The lifecycle controller tests shutdown/tree-empty ordering, recovery refusal, unclean/stuck/cancelled shutdown, uncertain activation and failed new health without generic downgrade or database restore. Production launcher/SCM/updater/recovery adapters remain unfinished.

The package audit enumerates every payload file and static native module. It checks included .NET runtime/dependency manifests and explicit Windows OS DLL contracts, plus sibling private DLLs and the fixed Node executable for `.node` imports. It does not scan developer DLL directories or run binaries. Static imports do not prove dynamic loads or clean-machine compatibility.

Actual mock-app tests bind only loopback and use synthetic private data, mock connector and `ALLOW_REAL_WHATSAPP=no`. PATH cannot locate Node/npm/Python/dotnet/ffmpeg/Caddy; Node is launched using the absolute packaged executable. App readiness, current imported schema, a competing writer on another port, inherited IPC shutdown, restart and IPC-disconnect cleanup are exercised. No media/TLS/SCM operation is implied.

No new full typecheck/web-build run was needed for this increment: it changes no app/web TypeScript source, schema or quota UI. The previously validated source/build remain in the earlier evidence. New native and Python boundaries received their own relevant checks. Hebrew XAML source has real Unicode Hebrew (479 characters in MainWindow and 1174 in LanPlanWindow), but this is an encoding check, not visual/RTL/resizing acceptance.

## Supply and UI gates

Official Caddy v2.11.7 was downloaded/extracted and **not executed**. Archive SHA256 from release API: `0a1edc0b799512051c57071ce0e798d3f2cf67dc98171366f1d2b326072e3b06`. Official checksum-file SHA256 from API: `5d27b76b95f496638d79208c55250317b8755fd9e90005f7466aa32956944a90`. The checksum file's entries are SHA512; the exact archive entry matched after correcting an initial SHA256/512 assumption. Its PE audit reports x64 and kernel32 import only. No Caddy process, CA, trust store or TLS configuration was created. Signature/Sigstore verification and production integration are still gates.

The actual app uses ffmpeg in `src/media.ts` to convert voice recordings to Opus and produce JPEG previews locally. FFmpeg's official site publishes source and points to community Windows builders. The full candidate requires an official-source build, codec/libopus feature proof and legal/provenance records. No community binary or installed Gyan copy was repackaged. No Windows C++ toolchain was found; no compiler/MSYS/global runtime installation was attempted. The real core tree is deliberately refused by the full profile because ffmpeg is missing.

GUI action status is unchanged from the earlier checkpoint. Target: the then-built `C:/Users/nehor/Documents/Codex/2026-10-28/task/yahad/installer/artifacts/Yahad.Setup/Yahad.Setup.exe`, opening for visual inspection only. After explicit owner approval, the supported launch route returned unrelated Visual Studio Installer (`C:/Program Files (x86)/Microsoft Visual Studio/Installer/setup.exe`). Read-only reconciliation found no Yahad window/process. No unrelated installer was operated. This is an app-routing failure, not proof Yahad ran; no approval is pending, and the parent said not to retry the unreliable route now. New UI remains compiled but unobserved; no screenshots are claimed.

Stage one owns installing/starting the server and reaching/opening the web app. Stage two remains existing web family/WhatsApp setup. LAN planning emits no authority, distinguishes host health from actual second-device access and makes no stable-name/trusted-certificate claim. An authenticated owner-only capability handoff, QR/link delivery, network consent execution and actual tablet trust/reachability are pending. Internet exposure and managed DNS infrastructure remain unselected and untouched.

Latest coordinated quota target: **schema 5**, `d5f787fe1a924a0689541abed1a24c6c185dd004`. The parent reports its independent Linux checks passed. This branch does not change quota/store/schema/recovery files. The independent Windows ChatCelebration filename fixes still need integration deduplication.

No current approval is pending. Remaining production work requires engineering plus disposable-machine acceptance: complete private dependencies; genuine service/identity/IPC/updater/backup/recovery adapters; clean Windows x64 without runtimes and before-login behavior; cancellation/reboot/offline/disk-full/AV-lock; data-preserving uninstall/reinstall; private LAN/TLS/second-device access; observed UI/RTL/resizing; and separate Linux amd64/Pi arm64 installs. Specific authorization would be required before using this working computer for service, firewall/CA or security changes. No deployment, Actions run, signing purchase/enrollment, real provider/family data or public infrastructure was used.

## Actual archive verification

The `80fbead` core candidate was built offline, then extracted into a new isolated directory. Exact manifest membership, per-file sizes and SHA256 hashes were checked while streaming every file; the extracted tree passed the PE/private-runtime audit again. **9,271 files**, **195 PE modules**, **289,600,919 payload bytes**, **102,723,267 ZIP bytes**. ZIP SHA256: `d7a467b642bb9c8f233641bb4d8101d3a255348e0a93c81c5f48ca544216648c`. Exact machine-readable result: `core-package.json`.

Artifact: `C:/projects/yahad-native-install/installer/artifacts/yahad-core-80fbead-win-x64.zip`. It is a developer core candidate, not a family installer. The full profile refused this actual tree with `MISSING_PAYLOAD: runtime/ffmpeg/ffmpeg.exe`; `familyInstallationReady` is false. No in-flight build was restarted or duplicated. The initial inventory caught legitimate dependency test-data directories, a documented Windows BCryptPrimitives import and a native addon import of its fixed Node host; these were resolved explicitly without a broad DLL search or removal of required payload files.

The final **extracted archive** runtime integration test passed: **1 passed, 0 failed, 0 skipped**, 12.0 seconds. Raw TAP: `extracted-package.tap`. This uses the extracted private Node, production modules and self-contained CLR, not the earlier prepared directory. It checks startup/readiness, second-writer refusal, graceful IPC shutdown, same-store restart and parent IPC-disconnect cleanup with an empty child PATH. No further test run is in flight.
