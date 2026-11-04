# Windows installer architecture review

Read-only review of `installer/` at branch `windows-install-acceptance` (2026-11-03). Goal: shrink the custom installer/updater while keeping what matters for one family PC.

## Size and coverage today

- **Product C#:** 20,661 lines in 1.7 MB (about 83 characters per line, so it is denser than the line count suggests). The app in `src/` is 5,751 lines in 0.3 MB.
- **By subsystem (approximate):**

  | Subsystem | Lines |
  |---|---|
  | Full-system update kernel + policy | 2,700 |
  | Recovery-helper A/B bootstrap | 2,270 |
  | App update intake/journal/probation | 1,930 |
  | Authority IPC + SCM sessions | 1,900 |
  | Activation/admission + backup engine | 1,800 |
  | Offline migration | 1,660 |
  | LAN/Caddy | 1,400 |
  | ACL/object boundary | 1,170 |
  | Owner approval | 780 |
  | Fixtures | 520 |

- **Services:** 4 in the full profile (`Runtime`, `Authority`, `RecoveryA`, `RecoveryB`; `installer/Yahad.Setup/NativeFullSystemSetupHost.cs:29`), plus an optional LAN proxy.
- **CI coverage:** `installer/machine-acceptance.ps1:62-75` exercises only install → ready → uninstall-with-delete. **Update, rollback, migration, the recovery helpers, keep-data uninstall and reinstall over existing data have no machine coverage, and no C# test project runs in CI** (`.github/workflows/windows-install.yml:76-83`).
- **Update trust:** releases are **not signed**. The trust anchor is `"https-github-unsigned"` (`installer/Yahad.Authority/FullSystemInstallerEnrollment.cs:28`, `NativeCandidateIntake.cs:20`), and the docs accept this explicitly (`docs/GITHUB-RUNTIME-UPDATES.md:16-19`). Most of the 21k lines defend against local service-to-service attackers and crash states. None of them defends against the realistic remote threat: a malicious release.

## 1. Mechanisms

| # | Mechanism | Threat addressed | Verdict |
|---|---|---|---|
| 1 | Per-service virtual accounts, 4 services (`Yahad.Setup/NativeFullService.cs:27-37`) | A compromised Node/Baileys process gaining machine rights. Authority can't reconfigure its own service (`docs/NATIVE-WINDOWS-AUTHORITY-PLAN.md:164-179`) | **SIMPLIFY.** Keep `NT SERVICE\Yahad` for the Runtime, the network-facing part that matters, and for the LAN proxy. Replace Authority + RecoveryA/B with one updater (see §3). |
| 2 | Per-file ACL roles: 9 roles (`Install.Core/InstalledFileRoles.cs:5-46`), path→role map (`InstalledStateFiles.cs:30-48`), 703-line validator (`WindowsFileBoundary.cs`), process/token DACLs (`InstalledProcessObjectAccess.cs`, 272 lines) | Runtime vs. Authority account separation; rename races between check and use (`WindowsFileBoundary.cs:450-470`) | **SIMPLIFY.** Use two inherited ACLs: the install dir (Program Files defaults: admins write, Runtime reads) and the data dir (SYSTEM/Admins full, Runtime modify, inheritance broken). With a single non-admin account there is no peer to race. Drop the process/token DACLs and custody transfer, which exist only so Authority can adopt the Runtime's job (`NATIVE-WINDOWS-AUTHORITY-PLAN.md:169-174`). |
| 3 | Re-hashing the payload on every start (`ServiceHost/InstalledActivation.cs:96-98`, `:210-230`): every file is SHA-256'd and every handle is held, up to 50k files / 2 GB; the trees are re-enumerated before launch (`:236`) | Partially written or tampered payload in ProgramData that the Authority wrote (no doc states the threat). Anyone who can rewrite the payload can also rewrite `activation.json` and the digests. | **DROP.** Put the payload in Program Files, where only admins can write. Verify hashes once at install/upgrade. Slow starts are a direct consequence of this check. |
| 4 | A/B recovery helpers (`Install.Core/BootstrapRecoveryProvisioningContract.cs:16-24`, `Authority/SystemBootstrap*.cs`, `SystemBootstrapRecoverySlots.cs:26-80`) | A broken Authority binary, or a crash during self-replacement, with "at least one runnable helper closure" kept (`docs/WINDOWS-FULL-UPDATE-INSTALLER-HANDOFF.md:94-101`) | **DROP.** The need disappears when the updater never replaces itself in place. It launches the *new* release's Setup as a separate process (§3), and a SYSTEM boot/daily repair task re-runs the retained Setup. |
| 5 | GitHub updates with journal, probation and rollback, including self-update (`InstalledUpdateTransaction.cs:7-9`, `InstalledUpdateJournal.cs:17-19`, `ProbationRuntime.cs:31-58`, `InstalledFullSystemExecutionKernel.cs:583-717`) | Bad release, crash mid-update, lost acknowledgement restoring old data over new writes | **SIMPLIFY.** Keep: download → verify → stop → swap → start → health check (`/health/ready`) → roll back to the retained previous version. Replace the journal with one state file plus idempotent steps. Drop probation "effect holds". **Add** a release signature (below). Two parallel pipelines (`yahad-app-native-*` vs `yahad-system-native-*`, `SystemCandidateManifest.cs:35-42`) become one asset. |
| 6 | Owner binding: SID captured before UAC, challenge ledger, WPF Owner app, owner pipe (`OwnerApprovalLedger.cs:40-107`, `AuthorityEndpointRouter.cs:82-87`, `Yahad.Owner/`) | A compromised app or web account minting update approvals; a different admin approving UAC (`NATIVE-WINDOWS-AUTHORITY-PLAN.md:105-112`) | **DROP.** The asset (family data) is already writable by the Runtime account, so approval can't protect it. The docs concede it doesn't stop malware running as the owner (`:161-166`). With signed releases and a version floor, the most a compromised app can do is install a genuine newer release. The Auto/Notify mode setting stays in the app's admin UI. |
| 7 | Named-pipe peer and image-hash checks (`Yahad.Setup/SetupElevation.cs:39-58`, `:107-117`) | Spoofed elevated helper; capturing the *unelevated* owner SID for #6 | **DROP** together with #6. Use a `requireAdministrator` manifest: one UAC prompt, one process, no temp copy, no pipe. Cost: the WPF UI runs elevated. |
| 8 | Offline migration sandbox: AppContainer with no capabilities, plus job and scoped ACL grants (`ServiceHost/NativeMigrationSandbox.cs:100-181`, `Authority/NativeMigrationScopeAccess.cs:21-61`, `InstalledDataMigrationTransaction.cs`; about 1,660 lines) | A malicious or buggy migration touching live data or the network | **DROP.** It sandboxes `native-migrate-v1`, which calls the *same* migration functions (`src/native-migrate-v1.ts:8-10`) that `Store` runs in-process with full data access on every open (`src/store.ts:60-80`), from the same release. Keep the useful part: take a SQLite backup before the upgrade and restore it on rollback. |
| 9 | Encrypted backup/restore engine with job-held quiescence (`Install.Core/NativeRecovery.cs`, 671 lines) | Restoring while the app writes; backup confidentiality | **SIMPLIFY.** Stop the service, take a SQLite backup copy, and store it in the data dir under the same ACL. Encryption adds nothing on the same disk under the same ACL. |
| 10 | Intake crash journal (`NativeIntakeRecovery*.cs`, 435 lines) | Partially downloaded candidate | **DROP.** Download to a temp file, verify, rename. On restart, delete leftovers. |
| 11 | Fixture/test code in product assemblies (`FixtureInstaller.cs`, `NativeFixtureLauncher.cs`, `FixtureBundle.cs`, `SignedFixtureAdmission.cs`, `FixtureAdmissionDocuments.cs`; about 520 lines) and test seams (`JobProcess.cs:8-12`) | None in production | **DROP.** Move it to test projects. |
| 12 | Legacy single-service path (`NativeServiceInstall.cs`, `WindowsFileBoundary.cs:27-45`) | None; it was never published | **DROP.** |
| 13 | LAN HTTPS proxy: Caddy under its own account, private CA, owned firewall rule (`ServiceHost/LanProxyService.cs:10-14`, `Setup/NativeLanInstall.cs`, `NativeLanFirewall.cs`) | The app reading the CA private key; leftover firewall rules | **KEEP** (feature). Trim it once its ACL code moves onto the two-ACL model. |
| 14 | Job objects for the Node child (`Install.Core/JobProcess.cs`) | Orphaned node/ffmpeg processes after a service stop | **KEEP.** About 100 lines with kill-on-close. |
| 15 | *(New)* Release signature: ECDSA P-256 over the release manifest. The key is embedded at install; the verifier already exists in-repo (`ServiceHost/FixtureAdmissionDocuments.cs:33-44`). | GitHub account or repo compromise, the one threat the current design accepts | **ADD.** About 60 lines, plus keeping the signing key out of CI or in a protected environment. |

## 2. Standard building blocks (checked against docs)

- **Velopack:** does not fit.
  - The default `Setup.exe` is per-user (`%LocalAppData%`). Machine-wide install is MSI only (docs.velopack.io/packaging/installer).
  - The docs say nothing about Windows services, Update.exe self-update or rollback.
  - Update verification is size/hash only; signing is optional and covers Authenticode of the binaries, not the update feed.
  - It would lose services and machine-wide operation with no user logged in, which are the core requirements.
- **WiX/MSI:** the strongest replacement for the install step.
  - `ServiceInstall`, `util:ServiceConfig` and `PermissionEx` are available, and MSI rolls back failed installs transactionally (learn.microsoft.com, *Rollback Installation*).
  - `MajorUpgrade Schedule="afterInstallInitialize"` restores the old product when an upgrade fails.
  - Losses:
    - No updater.
    - Rollback covers install-time failures only, not "installed but unhealthy".
    - `NT SERVICE\` accounts in `ServiceInstall` are not documented (unverified).
    - Custom data actions need their own rollback authoring.
    - It adds a second toolchain.
- **Inno Setup:** one UAC via `PrivilegesRequired=admin`, and `[Dirs] Permissions` for ACLs.
  - Services need `sc.exe` in `[Run]` or Pascal `[Code]`.
  - No updater, and no documented upgrade rollback.
  - Losses: transactional safety, plus the WPF consent/LAN screens, which would have to be rewritten in Pascal.
- **WinSW / NSSM:** not needed.
  - WinSW's last release was 2023-01 (v2.12.0 / v3.0.0-alpha.11). NSSM's last was 2.24 (2014), with a 2017 pre-release.
  - Neither documents virtual accounts.
  - The existing C# SCM host (`ServiceHost/ServiceRuntime.cs`, 305 lines) is smaller than the integration work.
- **`sc.exe` + scheduled task:**
  - `schtasks /Create /RU SYSTEM` is supported; it needs `/xml` or two tasks to get both a daily and an on-start trigger.
  - That `sc config obj= "NT SERVICE\x"` works without a password is **not stated** in the `sc config` doc. The repo's `CreateServiceW` path is already proven on CI, so keep it.
  - This is close to the recommended design. Its gap is that the app can't trigger "update now" on a SYSTEM task, so use a small updater service instead.

**Conclusion:** trim the existing C#; don't change toolchains. WiX is the only replacement that adds a property we lack (MSI install rollback), and the health-based rollback is still needed on top of it.

## 3. Target design

**Components:**

- **Setup.exe** (`requireAdministrator`): install, upgrade, repair, uninstall, WPF UI and LAN. It keeps `versions\<v>\` in Program Files with the current and previous versions.
  - `--quiet-upgrade <dir>` stops the services, takes a DB backup, switches the service ImagePath to the new version dir and starts the service.
  - If `/health/ready` fails within N minutes, it switches back and restores the backup *only if the schema changed*.
  - A state file makes every step resumable.
- **Yahad** service (`NT SERVICE\Yahad`): ServiceHost → job → `node main.mjs`. Migrations run in-process as they do today.
- **Yahad.Updater** service (LocalSystem): polls GitHub daily and serves one pipe (DACL: `NT SERVICE\Yahad`) with `status|check|install|set-mode`.
  - It verifies the release-manifest signature before unpacking, enforces version > current, then launches the release's Setup `--quiet-upgrade` in a breakaway process and exits.
  - Updating the updater is just part of the upgrade.
- **Task `Yahad.Repair`** (SYSTEM, on start + daily): runs the retained Setup `--repair` if the state file shows an unfinished upgrade or a stopped service.

**Estimated size:** about 3,500 lines (Setup 2,000 including LAN and UI; ServiceHost 500; Updater 600; shared 400). That is roughly 80% less.

**Language:** stay in C#. The SCM, ACL, job and pipe P/Invoke is already written and proven on CI. Node can't be an SCM service without a host, and Rust would be a rewrite.

### Removal steps (each gated by machine acceptance)

0. **Extend acceptance first.** Build two packages from one commit with different versions, then add phases for: upgrade N→N+1 keeping data, a deliberately broken N+1 rolling back to N, keep-data uninstall followed by reinstall, and the `Yahad.Acceptance.Authority.Recovery*` services. Run the C# suites in CI. Baseline the current code against them.
1. Delete fixtures and test seams from the product assemblies, plus the legacy single-service path (about 900 lines).
2. Drop the per-start payload re-hash, move the payload to Program Files and verify it at install only. Record start time in `summary.json`.
3. Drop owner binding: `Yahad.Owner`, the ledger, delegation, the owner pipe, the shortcuts and `owner-intent.json` checks (`InstalledActivation.cs:67-70`).
4. Switch Setup to `requireAdministrator` and delete the `SetupElevation` pipe and temp-copy path.
5. Drop the migration sandbox and scope ACLs. Upgrades take a backup and the Store migrates. Add a schema-bump fixture to the step-0 upgrade phase.
6. **a)** Implement `Setup --quiet-upgrade` with health rollback, and have CI drive it directly. **b)** Add the release signature and the Updater service that launches it. **c)** Delete Authority, RecoveryA/B, the full-system kernel, the journals and the intake recovery (about 8,000 lines).
7. Collapse the ACLs to two inherited ACLs and delete the role validators and the process/custody DACLs. Add an `icacls` assertion that the Runtime account can't write the install dir.
8. Replace `NativeRecovery` with a plain SQLite backup/restore.

### Status (2026-11-04)

Steps 0-8 are done on `windows-install-acceptance`, each gated by machine acceptance. The last green run is 41785039763.

- **Size:** product C# went from 20,687 to about 5,000 lines. The 3,500 estimate was low: shared code that several processes rely on is about 1,200 lines, not 400, and LAN and rollback account for most of the rest of the gap.
- **Timings in that run (seconds):** install 42, start 1.1, upgrade 31, rollback 12.4, repair 12, resume after a killed Setup 12.7.
- **Beyond the plan:** the LAN proxy runs from the release folder, so upgrades move it. The data ACL has an OWNER RIGHTS entry, so the Runtime cannot re-permission files it creates.
- **Not verified:** the real UAC prompt and browser opening; the real LAN enable screen (CI places what it leaves); a download from a real signed GitHub release; repairing a service that failed to start, and repair moving the LAN proxy; a real Store outside the test suites. No release key exists yet, so builds refuse every upgrade until the maintainer creates one (`installer/README.md`).

### Risks accepted

- **Updater runs as LocalSystem.** A parsing bug before signature verification could become a SYSTEM compromise. Mitigations: verify the signature over raw bytes first, HTTPS only, a small code surface. This is the same model as browser updaters.
- **Runtime account compromise** (Baileys/Node exploit) still yields all family data. That is unchanged from today, because it already has Data modify. The new exposure is that it can trigger installation of a *genuine, newer* signed release.
- **Data loss on rollback.** A rollback that restores the DB loses the messages written during the failed health window (minutes). Today's probation "effect hold" prevents that.
- **No recovery helper if both fail.** If the retained Setup and the updater are both broken, recovery is a manual run of `Setup --repair`, not a reinstall.
- **New key-management duty** for the signing key. In exchange, this closes the GitHub-compromise gap the current design accepts.
- **Unchanged:** local admins, malware running as the owner, and SmartScreen warnings for an unsigned Setup.
