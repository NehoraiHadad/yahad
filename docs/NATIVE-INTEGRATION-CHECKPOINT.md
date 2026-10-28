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
