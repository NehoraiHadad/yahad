# Native launcher and schema-5 integration

This checkpoint supersedes the source-transfer/integration status in `NATIVE-DEPENDENCIES-CHECKPOINT.md`. It does not change its distribution, machine-installation or trust acceptance gates.

The verified Library ZIP `libfile_57028d67b75c8191a9a29d54978ff0aa`, version 1, SHA256 `1acba0bb9d55c1a22f26e26c4a2d4e754b218dd5d6bb35886e65204ef88a9d7f`, supplied the Git bundle through `d5f787fe1a924a0689541abed1a24c6c185dd004`, with released baseline `4c41d5ef80ea349d0f678ee9c572e4975a2d776d` as prerequisite. Identity, bytes, Git prerequisites and actual NTFS Library metadata were checked before import. The Library helper's missing Windows xattr implementation was supplied through a process-local real NTFS EA adapter; it did not omit metadata, modify the helper, or change global settings.

Integration is on `feat/native-install-integration` in `C:\projects\yahad-native-integration`. The prior installer branch remains at `3bf951b04ed00426c76d7c2df197360ad7b2504d`. Native audit fixes are committed at `020f0c0`; the exact quota head is merged without independently rewriting its schema/business logic. The case-colliding celebration rename is reconciled as one `ChatCelebrationView.tsx` React view importing one `chatCelebrationState.ts` state module.

## Observed validation

- TypeScript typecheck and production Vite build passed.
- Full combined Windows JavaScript suite: **308 passed, 0 failed, 4 skipped**, out of 312. The skips are the existing setup-CLI POSIX-mode requirement, encrypted backup's POSIX boundary, SSE plus encrypted-backup integration pending a native backup adapter, and POSIX symlink-collision creation requiring an unapproved Windows privilege. They are explicit limitations, not passing machine-acceptance results. Ordinary SSE shutdown tests run on Windows with private fixture ACLs and an existing data directory.
- Test assertions for queue/wake file confidentiality now inspect actual Windows DACL allow principals rather than POSIX numeric permission bits. Production secret readers retain their stricter link and ACL checks.
- Real Store migrations using synthetic data: **1/2/3/4 to 5 passed**, preserving credentials, sessions, grants, encrypted history and connector auth; foreign-key checks passed. Evidence: `evidence/native-integration/migration-matrix.json`.
- Synthetic Python recovery tests: **10 passed**. Windows and Linux core package-format tests: **35 passed**. These are non-installing tests; they do not establish a real recovery receipt or working service.
- Native .NET harness: **79 passed**. Real Job Object tests verify failed-health and timeout cleanup of a root and grandchild. The clean environment drops inherited hooks/search overrides and retains private runtime paths.
- Actual native fixture host, private pipe, private Node and application HTTP flow passed with empty PATH and poisoned host Node/FFmpeg variables. Existing synthetic family receives the existing web login handoff; voice upload is converted by private FFmpeg to Opus. Image upload is exercised with the mock connector. Actual JPEG thumbnail conversion is separately covered by the extracted dependency tests; mock upload alone is not thumbnail proof.

## Remaining acceptance

The launcher is explicitly a developer runtime fixture, not trusted production intake. Production signed artifact authentication, durable authenticated journal/backup observations, native backup/restore, actual SCM identity/service installation and owner-only control IPC remain incomplete. Recovery planning booleans are not observed service facts. No database restore runs in this slice.

Clean supported Windows x64 without development tools, before-login service behavior, reboot/cancellation recovery, disk-full/AV-lock failures, data-preserving uninstall/reinstall, actual installer UI/RTL, LAN/HTTPS/tablet trust and Linux amd64/Pi arm64 installations require disposable-machine acceptance. No service, firewall, certificate trust, router, DNS, provider enrollment or public exposure was changed. No release, PR or website was published. There is no pending approval or denied action.

Exact corresponding FFmpeg/dependency sources, durable release-pipeline input retention and complete transitive notices/licenses remain distribution gates. The current inventory is a development inventory, not a complete legal SBOM. Archive hash/closure checks do not verify a release signature. `familyInstallationReady` remains false.


## Final combined payload

The app, self-contained helper and packager source are `ae406ed5e2e9f1757df16b5e3ea954b939e0f824`. The helper assembly's informational version independently contains that same commit. This replaces the earlier `80fbead` app/helper payload for this integration checkpoint. The official baseline and previous installer branch are unchanged.

`installer/artifacts/yahad-combined-ae406ed-win-x64.zip`: **187,602,075 bytes**, SHA256 **`1a193dd9df1d678b4442665f931062f5ce63d29f528d3154222e9f8b4d1dd8bc`**. All **9,303 files**, **204 PE modules**, **501,482,401 payload bytes** passed new-directory extraction, exact size/hash and complete dependency/manifest verification. `releaseSignatureVerified` and `familyInstallationReady` are both false.

The offline builder restored **172 actual installed production packages**. `legal/installed-npm-inventory.json` records their actual package names/versions, declared licenses, lockfile registry integrity/source and hashes/paths of included package-root legal files; it excludes absent optional platform packages. This is useful inventory, not a completed CycloneDX or legal compliance claim. Private Node 24.22.2, self-contained .NET 10.0.14, the pinned BtbN FFmpeg build and official Caddy 2.11.7 are included. FFmpeg build/license output and Caddy's actual Go build-info are retained. Existing mock/demo fixture assets are packaged so isolated app tests do not overlay unmanifested files.

The newly extracted payload passed **6/6 runtime tests with zero skips**: actual app startup/schema-5 readiness, duplicate-owner exclusion, private IPC shutdown/restart/disconnect, application media conversion (Opus/JPEG and WAV/AAC-MP4/WebM/PNG/JPEG inputs), and Caddy metadata/adaptation only. The .NET native fixture host separately launches that same extracted app with a clean empty-PATH environment despite hostile inherited Node/FFmpeg/search options, serves synthetic family voice/image HTTP flows, hands existing families to normal web login, stops through the private pipe/Node IPC and permits data lease reacquisition. The image mock HTTP flow proves upload; the direct actual app media-function test proves JPEG conversion.

Final evidence: `combined-archive.json`, `combined-payload-inputs.json`, `combined-extracted-runtime.tap`, `combined-node-final.tap`, `migration-matrix.json`, `dotnet-tests.json`, `package-python.txt`, `vendor-python.txt` and `recovery-python.txt` under `evidence/native-integration`. Combined package tests total **50 passed** (36 Windows-format/dependency/network-plan tests and 14 Linux candidate tests); recovery tests add 10 passed. All process/service recovery observations beyond the actual local fixture remain synthetic. No operation is in flight after this checkpoint.
