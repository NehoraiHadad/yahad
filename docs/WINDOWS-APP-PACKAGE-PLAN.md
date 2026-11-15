# Windows app package: updates without a full Setup

Status: **design plan, not implemented.** No code, release format or installed
machine changes with this document. Line references are to `origin/main` at
`2e12ea0`.

## 1. Problem

On Windows, the only update unit is the whole signed Setup. The Updater fetches
`/releases/latest`, verifies `yahad-windows-<v>.json(.sig)`, downloads
`Yahad.Setup-<v>.exe` and runs it with `--quiet-upgrade`
(`installer/Yahad.Updater/UpdaterEngine.cs:131-171`, `UpdateLauncher.cs:12`).
That Setup embeds the entire install package: .NET service host, Updater,
Node 24, FFmpeg, Caddy and the app (`installer/Yahad.Setup/InstallPackage.cs:13-14,37`).

So a few-line app fix (for example PR #26, message receipts) costs:

- a full rebuild of the .NET projects and the Setup (`installer/package.ps1:86-102`);
- a download of the whole Setup on every family computer (capped at 512 MiB,
  `installer/Yahad.Install.Core/ReleaseManifest.cs:16`);
- an extraction of every component into a new `versions\<v>\` tree
  (`installer/Yahad.Setup/UpgradeHost.cs:190-205`);
- switching all three service images, Runtime, Updater and LAN proxy
  (`UpgradeHost.cs:174`; `installer/Yahad.Setup/NativeService.cs:149-153`);
- moving the LAN firewall rules, which are bound to the per-version Caddy path
  (`installer/Yahad.Setup/NativeLanInstall.cs:237-242`).

Linux already separates the two: `yachad-app.zip` carries the app, and
`yachad-updater.zip` carries only the root worker (`scripts/release.mjs:33-44`,
`scripts/release-updater.mjs:83-100`, `docs/GITHUB-RUNTIME-UPDATES.md:24-29`).

## 2. Goal

- **Infrastructure track:** Setup changes only when the infrastructure changes.
  It still arrives automatically through the Updater, as today.
- **App track:** every other release is a small signed app package. The installed
  machine applies it without touching services, runtimes, firewall or ACLs.
- **No new trust or recovery model.** Reuse the existing key, manifest
  verification, backup, health gate, rollback and repair task.

## 3. What can only change through Setup

The table lists what each component is and which track changes it. Evidence is
the current owner of each operation.

| Component | Why Setup owns it | Track |
| --- | --- | --- |
| Service registration, accounts, failure actions, service DACL | Only Setup writes the SCM (`NativeService.cs:36-37,97-109,128-129`; `Yahad.Install.Core/NativeScm.cs:24-25` is read and control only) | Infrastructure |
| File ACLs on the state and binary roots | `NativeInstallFiles.cs:19-37`, `NativeSetupHost.cs:22-30,87-90,139-140` | Infrastructure |
| Firewall rules (with LAN consent) | `NativeLanFirewall.cs:10`, bound to the Caddy image path | Infrastructure |
| SYSTEM Repair task | `RepairTask.cs:22-47` | Infrastructure |
| Trust key `release-key.spki` | Written at first install (`NativeSetupHost.cs:149-155`), pinned against the embedded key (`ReleaseManifest.cs:82-86`) | Manual only (see below) |
| Service host, Updater, Setup engine (.NET, self-contained) | Their code is the upgrade machinery | Infrastructure |
| Node 24, FFmpeg, Caddy | Paths are resolved by the host (`Yahad.ServiceHost/InstalledLaunch.cs:54-64`) | Infrastructure |
| `server/main.mjs`, `dist/`, `fixtures/`, `native/Yahad.Platform.exe` | Run as the restricted Runtime account; no privileged operation | **App** |
| Database schema migrations | Already run inside the app at startup (`src/store.ts:60,77-85`, `src/db.ts:8-40`) | **App** |

**Is there anything that can never arrive later?** Only the trust root. Everything
else in the Infrastructure track is still delivered automatically by the Updater,
just as a full Setup run. Two cases need a person:

1. Changing the trust key (by design: `ReleaseManifest.cs:82-86`).
2. A Setup engine or Updater broken badly enough that it can't apply the next
   release. The repair task and rollback limit this risk today (`RepairHost.cs:8-60`).
   This plan doesn't change that.

`Yahad.Platform.exe` belongs to the App track, even though it's native. It runs
under the Runtime account and talks to the Updater pipe. That makes the pipe
protocol part of the contract between the tracks (section 5).

## 4. The coupling to break

The service host derives everything from its **own compiled version**:
`InstalledLayout.VersionRoot(ApplicationRelease.Version)`, then
`payload\runtime\node\node.exe` and `payload\app` under it
(`Yahad.ServiceHost/InstalledLaunch.cs:51-54`; `ApplicationRelease.cs:10-34` reads
the embedded `package.json` version). The Updater has the same rule and refuses
to run from any other version folder (`Yahad.Updater/UpdaterService.cs:31-35`).
So app version, host version and folder are one number today.

The plan gives the two tracks separate identities.

### Layout

```
%ProgramFiles%\Yahad.FirstInstall\
  infra\<infra-version>\   host\, updater\, runtime\{node,ffmpeg,caddy}\, Yahad.Setup.exe
  apps\<app-version>\      app\{server\main.mjs, dist\, fixtures\, native\}, app-tree.json
  release-key.spki
%ProgramData%\Yahad.FirstInstall\
  active-app.json          { format, version, treeSha256 }   (SYSTEM writes, Runtime reads)
```

- **Infrastructure version:** its own semver, stored in `installer/Directory.Build.props`.
  The host and Updater compile it in instead of `package.json`. It also defines an
  integer **infrastructure contract** `N`, raised whenever the app needs something
  new from it: Node major, FFmpeg/Caddy behaviour, host environment variables
  (`InstalledLaunch.cs:60-64`), Updater pipe protocol (`UpdaterProtocol.cs:6-9,34`).
- **App version:** stays `package.json`. It's what `/health/ready` reports
  (`src/server.ts:791-795`).
- **Active app pointer:** `active-app.json`, written atomically (temporary file,
  then `MoveFileEx` with replace and write-through). The host reads it at every
  start, checks that `apps\<version>\` exists, and checks that `app-tree.json`
  hashes to `treeSha256`. It refuses to start on any mismatch.

  The switch becomes **one atomic file replace**. Today it's three sequential SCM
  calls (`UpgradeHost.cs:67-70,174`).
- **Firewall:** Caddy lives under `infra\`, so an app update never moves firewall
  rules. The LAN help page still refreshes on an app update, because it's
  generated from the app payload (`NativeLanInstall.cs:230-236`). That moves into
  the app apply sequence.

## 5. App package format

New release assets for Windows:

- `yahad-windows-app-<v>.zip`: exactly the `app\` tree. Same member rules as
  today's payload (no `src/`, no `node_modules/`;
  `installer/windows/setup_package.py:154-155`), with bounded size and count.
- `yahad-windows-app-<v>.json`: the manifest.
- `yahad-windows-app-<v>.json.sig`: 64-byte ECDSA P-256 signature, same key as
  today (`scripts/windows-release-key.mjs:11,29-38`).

The manifest is strict camelCase, rejects duplicate fields and is at most 4096
bytes, like `ReleaseManifest.cs:13-21`:

```json
{
  "format": 1,
  "product": "yahad-windows-app",
  "version": "0.7.1",
  "sourceCommit": "<40 hex>",
  "archive": { "name": "yahad-windows-app-0.7.1.zip", "sha256": "<hex>", "bytes": 1234567 },
  "treeSha256": "<hex of app-tree.json>",
  "requiresInfrastructure": 1,
  "databaseSchema": 6,
  "databaseMigrationsFrom": [1, 2, 3, 4, 5],
  "keyId": "<16 hex>",
  "createdUnix": 0
}
```

- **Domain separation:** the signed message prefix is `yahad-app-v1\n`, not
  `yahad-release-v1\n` (`ReleaseManifest.cs:47`). A Setup manifest signature can
  never verify as an app manifest, and the reverse.
- **`requiresInfrastructure`:** when it's above the installed contract, the app
  track refuses the package. The Updater then reports "needs installer update"
  and falls back to the Setup in the same release (section 7).
- **`databaseMigrationsFrom`:** brings the Linux rule (`deploy/updater.py:89-120`)
  to Windows. Today Windows checks only `installedSchema <= DatabaseSchema`
  (`ReleaseManifest.cs:99`), so a source the app cannot migrate is discovered only
  when the app refuses it at startup (`src/db.ts:16-18`).

## 6. Apply sequence

**Who applies it.** The Updater (LocalSystem) launches the **installed** Setup
engine `infra\<current>\Yahad.Setup.exe --apply-app <staged-dir> --result <file>`.
It's detached, as today (`UpdateLauncher.cs:7-18`).

- **Why the installed Setup:** it already holds the elevated state machine,
  backup, health gate, rollback and repair state (`UpgradeHost.cs:107-269`,
  `UpgradeState.cs`).
- **Why not the Updater:** the Updater would need a second copy of all of that
  (`Yahad.Updater.csproj` references only `Install.Core`).
- The installed Setup is protected by the binary-root ACL (admins and SYSTEM
  write only).

`AppUpgradeHost` (new, beside `UpgradeHost`) persists each phase to
`upgrade-state.json` with `kind: "app"`. The repair task then resumes or rolls
back a crash at any step.

1. **Admit.** Require admin, the install lock, state `committed`, and no leftover
   upgrade state (as in `UpgradeHost.cs:107-127`). Verify the signature before
   parsing, then the archive size and hash against the manifest. Require
   `version > active`, `requiresInfrastructure <= installed contract`, and that
   the live schema is the target or in `databaseMigrationsFrom`. Check disk
   space for the app tree plus a database copy.
2. **Prepare** (no downtime). Extract to `apps\<v>.partial` with the existing
   no-replace extraction (`UpgradeHost.cs:194-204`). Write `app-tree.json`,
   recheck `treeSha256`, then rename to `apps\<v>`.
3. **Stop** the Runtime only. The Updater keeps running. It doesn't hold the app,
   and the engine runs detached. The LAN proxy keeps running, and requests fail
   during the switch exactly as during a Runtime restart today.
4. **Back up** the database only when the schema changes (`DatabaseBackup.cs:15-32`;
   `UpgradeState.cs:43`).
5. **Switch:** an atomic replace of `active-app.json`. Refresh the LAN help page
   if LAN is enabled.
6. **Start and gate health:** start the Runtime, then
   `UpgradeFiles.RequireHealthyAsync` with version = app version and the target
   schema (3 consecutive passes within 300 s; `UpgradeHost.cs:53-64`).
7. **Commit:** keep the active and previous app trees, remove older ones and their
   backups (`UpgradeHost.cs:255-269`).
8. **Roll back** on any failure after step 5:
   - stop the Runtime;
   - restore the pointer to the previous version;
   - restore the database if it was backed up;
   - start the Runtime and gate health against the old version and schema;
   - report `ROLLED_BACK_UNHEALTHY`.

   A failure in steps 1-2 only discards the partial tree. Result codes reuse the
   existing mapping (`UpdaterEngine.cs:23-32`), plus `APP_NEEDS_INFRASTRUCTURE`.

Compared with a full upgrade, an app update doesn't stop the Updater or proxy,
doesn't change any service image or firewall rule, and doesn't extract runtimes.

## 7. Release selection and tooling

**What a release always carries.** `/releases/latest` stays the only source on
both platforms (`GitHubReleaseSource.cs:14`; `deploy/updater.py:678`). Each
release still carries every asset a platform might need, so neither platform
stalls (`scripts/release-windows.mjs:89-111`, `docs/GITHUB-RUNTIME-UPDATES.md:284-288`):

- the four Linux assets, unchanged;
- the three app-package assets;
- the full Setup set: `yahad-windows-<v>.json(.sig)`, `Yahad.Setup-<v>.exe`, and
  `Yahad.Setup.exe`, which the landing page links for new installs.

**How the Windows Updater decides:**

1. If the release's app manifest verifies, and `requiresInfrastructure` is within
   the installed contract, use the app track.
2. Otherwise, if the release's Setup carries a newer infrastructure version, use
   the Setup track. That Setup also brings the new app.
3. Otherwise report `APP_NEEDS_INFRASTRUCTURE`, a state for the maintainer to fix
   in the release. Never a silent "up to date".

**Does every release still rebuild the Setup?** Assembling one, yes, because new
installs need the latest app. Rebuilding the infrastructure, no:

- `release-windows.mjs` builds the infrastructure once per infrastructure version
  and caches it by content hash: the host, Updater and Setup-engine .NET
  publishes, plus the runtimes.
- Each release only re-embeds the new `app\` tree and re-signs.
- Today every release republishes every .NET project (`installer/package.ps1:86-102`).

**Versions.**

- The app version keeps the current rule (`chooseVersion`,
  `release-windows.mjs:303-310`).
- The infrastructure version changes only when files under `installer/` (other
  than docs and tests) or the pinned runtime inputs change. The release command
  refuses a release where those changed without a version bump, and the reverse.

## 8. Moving existing installs

Machines on 0.6.x only understand the Setup track.

1. **Bridge release:** the first release with the new layout is a full Setup
   upgrade, taken by the existing Updater with no change on the machine. Its
   `UpgradeHost` migrates the old layout:
   - from `versions\<old>\`, it creates `infra\<infra-v>\` and `apps\<app-v>\`;
   - it writes `active-app.json`;
   - it switches the images to `infra\`;
   - it moves the firewall to the new Caddy path.

   The bridge keeps `versions\<old>` until commit, so rollback restores the old
   images and old layout exactly.
2. **Later releases:** bridged machines take the app track. Machines still on
   0.6.x keep taking the full Setup, which is still present in every release, and
   are bridged by it.
3. **Retirement:** `minimumFromVersion` (today hard-coded `0.0.0`,
   `installer/package.ps1:141`) can later be raised to the bridge version. This
   retires the old-layout migration code. It's a separate, deliberate decision.

## 9. Test plan

- **Unit (`Yahad.Install.Tests`):**
  - app manifest parsing and strictness;
  - signature domain separation in both directions;
  - `requiresInfrastructure` and `databaseMigrationsFrom` admission;
  - Updater selection for each combination of present and missing assets;
  - atomic pointer write;
  - host refusal on a missing tree or wrong `treeSha256`.
- **State machine:** a crash or kill injected at each persisted phase of
  `AppUpgradeHost`, then repair. Expect "resume to committed" or "roll back to
  previous", never a mixed state. Cover a schema-changing and a
  non-schema-changing app.
- **CI upgrade sets** (`installer/ci-build-upgrade-set.ps1`, `ci-test-suites.ps1`):
  - 0.6.x → bridge (full);
  - bridge → app-only;
  - app-only unhealthy → rollback;
  - an app needing newer infrastructure → falls back to Setup;
  - LAN enabled: firewall rules are unchanged after an app update.
- **Linux:** no behaviour change. The existing `npm run test:updater` suite must
  stay green with the extra assets present in releases.
- **Machine acceptance** (`installer/machine-acceptance.ps1`) on the ASUS test
  machine, before any release uses the app track. Windows runtime startup there
  is still under diagnosis (README "Windows installer status").

## 10. Risks

- **Contract drift.** An app built against host behaviour the installed
  infrastructure doesn't have.
  Mitigation: the contract number, plus a CI check that runs the new app on the
  oldest supported infrastructure.
- **Pointer integrity.** The host trusts `active-app.json`.
  Mitigation: SYSTEM-only write ACL, tree hash verified at every start, and
  refuse rather than guess.
- **Two engines, one data set.** `UpgradeHost` and `AppUpgradeHost` must share
  the install lock and `upgrade-state.json`, so they can never overlap.
- **Bridge complexity.** It's the one release that moves layout, images and
  firewall together. It needs the full CI upgrade-set coverage above, and a
  machine run, before it's published.
- **Bridge assumes a working Windows updater.** Today the Windows updater isn't
  yet proven on a family machine, so the bridge depends on that validation.

## 11. Open questions for the maintainer

1. **Infrastructure version scheme:** an independent semver, or tied to the app
   version (e.g. the app version at the last infrastructure change)?
2. **Updater self-update:** should the Updater itself eventually move to a
   Linux-style self-updating worker? This plan keeps it on the Setup track.
3. **First install:** should a fresh install embed the app (as planned, works
   offline), or download the latest app package at install time (smaller Setup,
   needs network)?
4. **Auto window:** app-only updates are much lighter. Should they still wait for
   the 03:00-04:59 Auto window (`UpdaterEngine.cs:46-47`), or apply when the
   parent requests them?
5. **When to retire 0.6.x support** (raise `minimumFromVersion`)?
