# Windows app package: updates without a full Setup

Status: **design plan, not implemented.** This document changes no code, release
format or installed machine. Line references are to `origin/main` at `2e12ea0`.
Revision 2 addresses an independent review (33 findings); the appendix maps each
finding to where it's handled. Revision 3 re-examines the mechanism itself
(section 2a), moves `Yahad.Platform` to the infrastructure track, and makes the
drops the bridge: the maintainer confirmed that no Windows install exists
anywhere (section 11). A
second independent review of revision 3 found six more defects, all accepted
(end of the appendix).

> Update 2026-11-15: FFmpeg was later removed from Yahad entirely (voice and previews are made in the browser and only validated by the server). Every FFmpeg reference below, including `runtime\ffmpeg` and the host's FFmpeg resolution, is superseded: the infrastructure tree holds only Node, the .NET pieces and Caddy, and the packagers refuse a `runtime/ffmpeg` path.

## 1. Problem

On Windows, the only update unit is the whole signed Setup. The Updater fetches
`/releases/latest`, verifies `yahad-windows-<v>.json(.sig)`, downloads
`Yahad.Setup-<v>.exe` and runs it with `--quiet-upgrade`
(`installer/Yahad.Updater/UpdaterEngine.cs:129-171`, `UpdateLauncher.cs:12`).
That Setup embeds the entire install package: the .NET service host, the
Updater, Node 24, Caddy and the app
(`installer/Yahad.Setup/InstallPackage.cs:13-14,37`).

So a few-line app fix (for example PR #26, message receipts) costs:

- a rebuild of every .NET project and the Setup (`installer/package.ps1:86-102`);
- a download of the whole Setup on every family computer: **294 MB** for
  `Yahad.Setup-0.6.4.exe` (release `v0.6.4`), under a 512 MiB cap
  (`installer/Yahad.Install.Core/ReleaseManifest.cs:16`). The app's own share
  is small: the server bundle `main.mjs` is 10.3 MB and `dist/` is under 1 MB
  (CI run 42003566492, `app-payload.log` and `web.log`). Caddy is 51 MiB
  (`installer/windows/dependency-pins.json`); the former 151 MiB media dependency
  was removed on 2026-11-15;
- an extraction of every component into a new `versions\<v>\` tree
  (`installer/Yahad.Setup/UpgradeHost.cs:190-205`);
- a switch of all three service images: Runtime, Updater and LAN proxy
  (`UpgradeHost.cs:174`; `installer/Yahad.Setup/NativeService.cs:149-152`);
- a move of the LAN firewall rules, which are bound to the per-version Caddy path
  (`installer/Yahad.Setup/NativeLanInstall.cs:237-242`).

Linux already separates the two: `yachad-app.zip` carries the app, and
`yachad-updater.zip` carries only the root worker (`scripts/release.mjs:33-44`;
the single `updater.py` member at `scripts/release-updater.mjs:35-45`;
`docs/GITHUB-RUNTIME-UPDATES.md:24-29`).

## 2. Goal

- **Infrastructure track:** Setup changes only when the infrastructure changes.
  It still arrives automatically through the Updater, as today.
- **App track:** every other release is a small signed app package. The
  installed machine applies it without touching service registrations, images,
  runtimes, firewall or ACLs.
- **No new trust model.** Same key, same signature scheme with its own domain
  prefix, same backup, health gate, rollback and repair task. The recovery model
  is extended to (infrastructure, app) pairs, not replaced.

## 2a. Is the mechanism itself right?

This section re-asks the question from scratch, including whether .NET is the
right base at all.

**What has to be native.** These requirements come from running a service on a
family PC with no one logged in, and no language choice removes them:
- the Runtime as a Windows service under a restricted virtual account;
- an Updater running as SYSTEM;
- ACLs on the install and data roots;
- a kernel data lease handed to the Node process (`ServiceProcess.cs:89-99`);
- a pipe client that verifies the server's PID against the SCM
  (`UpdaterClient.cs:97-102`).

Node can't be an SCM service without a host, and it has no API for
`DuplicateHandle` or for checking a named-pipe server's identity. So there is
always a small native layer. The question is only which one.

**Alternatives already evaluated.** On 2026-11-03,
`docs/installer-architecture-review.md` §2 checked them against their docs:
- Velopack: per-user install, no services;
- WiX/MSI: install-time rollback only, a second toolchain, and `NT SERVICE\`
  accounts not documented;
- Inno Setup: no updater, no upgrade rollback;
- WinSW/NSSM: unmaintained, and virtual accounts not documented.

That review then cut the C# from about 20,700 lines to about 5,000, each step
gated by machine acceptance (same file, "Status"). A rewrite in Go or Rust would
redo proven SCM, ACL, job and pipe code and add no property this plan needs.
**Verdict:** keep C#/.NET for the native layer.

**What was actually wrong.** Two defects, both in the design rather than the
language:

1. **One version number for everything** (section 4). This is what turns a
   10 MB app fix into a 294 MB Setup. The plan's main change fixes it.
2. **`Yahad.Platform` is on the wrong track.** Its five commands are the native
   half of the infrastructure contract (`installer/Yahad.Platform/Program.cs`):
   - `--describe`;
   - `windows-updates`, the Updater pipe client;
   - `read-secret`, a no-follow file read;
   - `adopt-host-lease`, validating the host's lease handoff;
   - `lock-data`.

   It compiles `Yahad.Install.Core`, which the host, Updater and Setup share. It
   changes exactly when that contract changes. Yet it ships in the app tree
   (`src/platform-files.ts:47`, `src/windows-update-transport.ts:31`) and brings
   a private .NET runtime with it (`installer/windows/bundle.py:19-24,80-89`).
   That is the only native code in the app tree, the only reason the app tree
   needs a .NET runtime, and the only code the two tracks share.

   **Revision 3 moves Platform to infrastructure.** The app track then ships
   **no native code at all**: only JavaScript, HTML and data that the
   infrastructure's own Node runs. The package's size is then set by the app
   itself, about 11 MB before compression. A new native capability needs a new
   contract, and so a Setup, which is correct for privileged-boundary code.

**Not changed by this plan, noted for the infrastructure track.** These shrink
the rare Setup download and don't affect the app track:
- the host, Updater and Platform each carry their own self-contained .NET
  runtime (decision 6);

## 3. What can only change through Setup

| Component | Owner today (evidence) | Track |
| --- | --- | --- |
| Service registration, accounts, service DACL, failure actions | Only Setup writes the SCM: create `NativeService.cs:36-48`, DACL `:108`, startup and failure actions `:139-148,183-193`. `Yahad.Install.Core/NativeScm.cs:24-25` is transport only | Infrastructure |
| File ACLs on the state and binary roots | `NativeInstallFiles.cs:19-37`, `NativeSetupHost.cs:22-30,87-90,139-140` | Infrastructure |
| Firewall rules (with LAN consent) | `NativeLanFirewall.cs`, bound to the Caddy path (`NativeLanInstall.cs:237-242`) | Infrastructure |
| Caddyfile (proxy configuration) | Generated by infrastructure code (`LanDeployment.cs:45-69`), loaded at proxy start, admin API off (`:53`; `LanProxyService.cs:82`) | Infrastructure |
| SYSTEM Repair task | `RepairTask.cs:22-48` | Infrastructure |
| Trust key `release-key.spki` | Written at first install (`NativeSetupHost.cs:149-155`), pinned against the embedded key (`ReleaseManifest.cs:82-86`) | Manual only |
| Service host, Updater, Setup engine (.NET, self-contained) | They are the upgrade machinery | Infrastructure |
| `Yahad.Platform` (the app's native helper) | The native half of the contract; compiles `Install.Core` (section 2a) | Infrastructure (moved) |
| Node 24, Caddy | Node resolved by the host (`Yahad.ServiceHost/InstalledLaunch.cs`); Caddy by `LanProxyPaths.Caddy` (`LanProxyService.cs:19,82`) | Infrastructure |
| `app\package.json`, `server\main.mjs`, `dist\`, `fixtures\`, `deploy\` | JavaScript, HTML and data run by the infrastructure's Node as the restricted Runtime account; no privileged operation and no native code (`installer/windows/bundle.py:18-19`) | **App** |
| Database schema migrations | Run inside the app at startup (`src/store.ts:60,76-85`, `src/db.ts:8-18`) | **App** |

**Can anything never arrive later?** Only the trust root. Everything else in the
infrastructure track is still delivered automatically by the Updater. Two cases
need a person:

1. Changing the trust key (by design: `ReleaseManifest.cs:82-86`).
2. A Setup engine or Updater broken badly enough that it can't apply the next
   release. The repair task and rollback limit this today (`RepairHost.cs`). This
   plan doesn't change that.

## 4. Identities, layout and records

Today the host derives everything from its own compiled version:
`InstalledLayout.VersionRoot(ApplicationRelease.Version)`, then
`payload\runtime\node\node.exe` and `payload\app`
(`InstalledLaunch.cs:51,54`; `ApplicationRelease.cs:10` reads the embedded
`package.json` version). The Updater refuses any other folder
(`UpdaterService.cs:31-35`), Platform accepts only the Updater image of its own
version (`Yahad.Platform/UpdaterClient.cs:97-105`), and the LAN proxy runs
`LanProxyPaths.Caddy(ApplicationRelease.Version)` (`LanProxyService.cs:82`). App
version, host version and folder are one number. This plan gives them separate
identities.

### 4.1 Layout

```
%ProgramFiles%\Yahad.FirstInstall\        (binary root, BinaryAcl)
  release-key.spki
  active.json                             the active pair (4.3)
  infra\<infra-version>\
    host\  updater\  platform\  runtime\{node,caddy}\  legal\
    setup\Yahad.Setup.exe                 payload-free Setup engine (4.4)
    infra-tree.json                       member record of this tree
  apps\<app-version>\
    app-tree.json                         member record, shipped in the archive (5.3)
    app\{package.json, server\main.mjs, dist\, fixtures\, deploy\, legal\}
```

`legal\` is split by owner. Today one `legal/` covers the whole Setup
(`installer/windows/bundle.py:22-26`). The infra tree keeps `runtime-lock.json`,
the Node, .NET and Caddy licenses and an infra SBOM. The app tree carries
what the server build writes, `server-bundle.json` and
`ThirdPartyNotices-server.txt` (`scripts/build-server.mjs:103-111`), plus an app
SBOM. A new app then never sits beside stale notices for its own dependencies.

`active.json` lives in the binary root, not the state root. It describes
binaries, it is removed with them at uninstall, and the Runtime, the LAN proxy
service and Platform can already read that root (`NativeSetupHost.cs:30`).

### 4.2 ACLs

`infra\`, `apps\`, every `.partial` tree and `active.json` are created under the
binary root and **inherit** `BinaryAcl` (`NativeSetupHost.cs:30`). That ACL gives
full control to SYSTEM and Administrators, and read to the Runtime, the LAN
proxy and Users (`NativeInstallFiles.cs:19,24`). Nothing gets an explicit ACL of
its own. Commit re-applies the tree ACL, as today (`UpgradeHost.cs:264`).

The write boundary is therefore "SYSTEM and Administrators", the same one as for
the binaries the pointer selects. It is not "SYSTEM only", and a Setup started
by hand by an administrator is inside it, as it is today.

### 4.3 Versions and the active pointer

- **Infrastructure version:** its own version (decision 1), compiled into
  the host, Updater and engine from `installer/Directory.Build.props` in place of
  `package.json`. It carries the supported **infrastructure contract** range
  `contractMin..contractMax` (section 6).
- **App version:** stays `package.json`. `/health/ready` reports it by reading
  `package.json` from its working directory (`src/server.ts:794`). So the host
  keeps cwd = the active `apps\<v>\app`, as today (`InstalledLaunch.cs:69`), and
  `app\package.json` is a required member.
- **`active.json`:** strict JSON, at most 4096 bytes:

  ```json
  { "format": 1,
    "infrastructure": { "version": "1.0.0", "contractMin": 1, "contractMax": 1, "treeSha256": "<hex>" },
    "app": { "version": "0.7.1", "requiresContract": 1, "treeSha256": "<hex>" } }
  ```

  It is written atomically: a temporary file in the same directory, then
  `MoveFileEx` with replace and write-through. Only the Setup engine writes it.
  Its readers:
  - **Service host** (Runtime and LAN modes): refuses to start unless
    `infrastructure.version` equals its own compiled version and
    the contract range equals its own, and that `app.requiresContract` is in
    that range. It then resolves Node and
    Platform from its `infra\` tree, Caddy likewise (`LanProxyService.cs:19,82`),
    and the app from `apps\<app.version>\`. It passes the Platform path to Node
    as `YAHAD_PLATFORM_HELPER`. That replaces the app-relative
    `../native/Yahad.Platform.exe` (`src/platform-files.ts:47`,
    `src/windows-update-transport.ts:31`). Today the app reads no
    environment variable here: it builds that default path and only checks that
    it is an absolute `.exe` (`platform-files.ts:58`). The new app reads only
    `YAHAD_PLATFORM_HELPER`, with no fallback, and refuses a missing or invalid
    value. Every direct launcher passes it too (the migration list in section 10).
  - **Updater:** its own infra version is compiled in. It reads the current app
    version from the pointer at every decision (section 9.2).
  - **Platform:** it is infrastructure now, so its compiled infra version is
    the Updater's. `RequireServer` keeps comparing against its own compiled
    version, and every PID, state, account and image check stays
    (`UpdaterClient.cs:97-105`). It only has to switch from
    `ApplicationRelease.Version` to the compiled infrastructure version.

### 4.4 The installed Setup engine

The distributable `Yahad.Setup-<v>.exe` embeds the app (`Yahad.Setup.csproj:9`),
and its package must match its own version and commit
(`InstallPackage.cs:23-24`). Two app releases on the same infrastructure would
therefore produce different bytes for the same `infra\<v>\` path.

So the installed engine is a separate artifact:
- the same `Yahad.Setup` project, published **without** `SetupPayloadDirectory`;
- built once per infrastructure version;
- shipped as an infra member `setup\Yahad.Setup.exe`.

That engine serves `--apply-app`, `--repair`, `--uninstall` and the Apps &
Features entry (`NativeSetupHost.cs:186`). None of these modes may require an
embedded payload. **Today uninstall does require one.** `SetupOperation.ExecuteAsync`
opens `InstallPackage.Embedded()` before it branches on the operation
(`SetupOperation.cs:45`), and a missing resource fails with
`PACKAGE_NOT_EMBEDDED` (`InstallPackage.cs:44`). So this is a required code
change, not a check. The uninstall and repair paths are built without
`InstallPackage`, and only the operations that consume the package open it
(install, `--verify-package`, `--quiet-upgrade`; `SetupEntry.cs:28`,
`UpgradeHost.cs:120`). Tests run both uninstall modes, quiet and from Apps &
Features, on the payload-free engine.

How the per-release distributable Setup is built from it is decision 7.

### 4.5 Installer record (`InstallerState`, format 7)

`InstallerState` binds one `Version`, its digests, files and `Previous` to a
single release (`InstallState.cs:12-31`). Format 7 replaces that with pairs:

- `Infrastructure`: version, contract range, source commit, engine SHA-256, the
  `infra-tree.json` digest and its members;
- `App`: version, source commit, `treeSha256`, members, and the **database
  schema the committed app runs**;
- `Previous`: the last committed **pair**, with the same fields.

Retention is defined over pairs. The binary root holds exactly the active pair
and `Previous`. They may share one `infra\` tree. A rollback target is always a
pair that was once committed and health-gated together, so it is known to be
compatible. Keeping only "the previous app" would not guarantee that it fits the
infrastructure being returned to.

The recorded schema is the admission source (section 7, step 1). It equals the
stored `schema_version` for any committed app. A commit, a first install's
included (8.3), is written only after health reports that app version and that
schema, and the app refuses to start when the stored schema
differs after migration (`src/store.ts:84-85`; `src/db.ts:14-18`).

There is no format-6 reader. No 0.6.x install exists (section 11), so a format-6
record is refused like any unknown format.

## 5. Release format

### 5.1 Manifests

One signed manifest, `yahad-windows-release-<v>.json(.sig)` (format 2), replaces
today's `yahad-windows-<v>.json`. No machine reads the old one (section 11), so
it isn't published any more. The new manifest signs both tracks in one message,
so the Updater decides from facts that were authenticated together:

  ```json
  { "format": 2, "product": "yahad-windows-release", "version": "0.7.1",
    "sourceCommit": "<40 hex>", "keyId": "<16 hex>", "createdUnix": 0,
    "infrastructure": { "version": "1.0.0", "contractMin": 1, "contractMax": 1, "sourceCommit": "<40 hex>",
      "treeSha256": "<hex>", "setup": { "name": "Yahad.Setup-0.7.1.exe", "sha256": "<hex>", "bytes": 0 } },
    "app": { "archive": { "name": "yahad-windows-app-0.7.1.zip", "sha256": "<hex>", "bytes": 0 },
      "treeSha256": "<hex>", "requiresContract": 1,
      "databaseSchema": 6, "databaseMigrationsFrom": [1, 2, 3, 4, 5] },
    "minimumFrom": { "app": "0.0.0", "infrastructure": "0.0.0" } }
  ```

  It is strict camelCase, rejects duplicates and unknown fields, and is at most
  4096 bytes. Its signed prefix is `yahad-release-v2\n`, so a signature over today's
  format (`yahad-release-v1\n`, `ReleaseManifest.cs:47`) never verifies as one.
  The v1 verifier and its manifest type are removed.

`databaseMigrationsFrom` brings the Linux rule (`deploy/updater.py:89+`) to
Windows. Today Windows checks `minimumFromVersion`, that the release is newer,
and, only when a live schema was obtained, `installedSchema <= DatabaseSchema`
(`ReleaseManifest.cs:98-99`). The Updater passes `null` there
(`UpdaterEngine.cs:143`), and Setup asks health (`UpgradeHost.cs:118`). So a
source the app cannot migrate is found only when the app refuses it at startup
(`src/db.ts:16-18`).

### 5.2 App archive

`yahad-windows-app-<v>.zip` holds exactly `app-tree.json` and members under
`app/`. Its validator, `AppPackage`, is new. It shares the existing name rules
and the no-replace extraction primitive (`InstallPackage.cs:52-79,97-101`),
because `InstallPackage` itself admits only `host/`, `updater/` and `payload/`
and requires runtimes and services (`:37,99`):

- **Closed namespace:** `app-tree.json` plus `app/…`, using the same name rules
  as today. Paths compare case-insensitively, and duplicates or collisions are
  refused.
- **Regular files only**, with the same attribute checks as `InstallPackage.cs:34`.
- **Bounds:** at most 50000 members, 512 MiB per member, 2 GiB in total
  (`bundle.py:27`). The archive bytes and SHA-256 must match the manifest before
  the zip is opened.
- **Required members:** `app/package.json`, `app/server/main.mjs`,
  `app/dist/index.html`, `app/deploy/lan-capabilities.json` and
  `app/deploy/lan-help.html` (`NativeLanInstall.cs:102-106`), plus
  `app/legal/server-bundle.json` and `app/legal/ThirdPartyNotices-server.txt`
  (section 10).
- **Forbidden:**
  - `app/src/`, `app/node_modules/`, `app/package-lock.json`
    (`installer/windows/setup_package.py:160-162`);
  - `app/native/`;
  - any member that is a PE image (the `MZ` header), whatever its name;
  - any member named `*.node`, `*.exe`, `*.dll`, `*.ps1`, `*.cmd` or `*.bat`.

  The server is one self-contained ESM file with no `node_modules`
  (`bundle.py:16-17`), so nothing legitimate is lost.
- **Per-member check:** every member is size- and SHA-256-checked while it is
  extracted (as `InstallPackage.cs:69-71`).
- **Exact match:** the archive's member set must equal `app-tree.json`'s list
  exactly.

### 5.3 `app-tree.json` (and `infra-tree.json`)

The builder produces it and the archive carries it. The installer never
re-serialises it: it extracts the bytes and requires
`SHA-256(bytes) == app.treeSha256`. Contract:

- UTF-8 without BOM, at most 16 MiB, strict JSON:
  `{"format":1,"version":…,"sourceCommit":…,"files":[{"path":…,"size":…,"sha256":…}]}`.
- `files` are sorted by ordinal path and unique case-insensitively. Each path is
  relative to `apps\<v>\`, uses `/`, and starts with `app/`. Sizes are
  non-negative integers, and digests are lowercase hex.
- `infra-tree.json` has the same format, over `host/`, `updater/`, `platform/`,
  `runtime/`, `legal/` and `setup/`.

**What the host verifies at start.** The pointer's `treeSha256` must match the
tree-record bytes, every recorded member must exist with its recorded size, and
nothing else may be present. Per-member hashes are verified at extraction and by
`--repair`, not at every start. The security boundary for a committed tree is
the binary-root ACL (4.2), as it is for `versions\` today, which the host never
hashes either. This guarantee is stated as exactly that and no more.

## 6. Contract between the tracks

Each infrastructure release declares the contracts it supports as a contiguous
range, `contractMin..contractMax`; the pointer and the record carry both. An app
declares one `requiresContract`, and it is admitted only when
`contractMin ≤ requiresContract ≤ contractMax`. A bare `requiresContract ≤ N`
would be wrong, because the contracts in the code are exact, not cumulative:
`HostLeaseGrant` is accepted only at format 1 (`WindowsFileBoundary.cs:113`), and
Platform replies are checked against an exact schema (`src/platform-files.ts:41`).
So an infrastructure that drops an old behaviour raises `contractMin`, and an
older app is refused rather than started into a broken contract. A contract
covers everything an app relies on from infrastructure:

- process launch: the environment variables, including `YAHAD_PLATFORM_HELPER`,
  and the working directory (`InstalledLaunch.cs:63-69`), Node major and
  Caddy behaviour;
- the Platform command set and reply schema (`installer/Yahad.Platform/Program.cs`):
  `windows-updates`, `read-secret`, `adopt-host-lease`, `lock-data`;
- the data-lease handoff: `YAHAD_NATIVE_LEASE_FILE/NONCE` and the
  `HostLeaseGrant` format (`ServiceProcess.cs:89-99`; `src/platform-files.ts:76-82`);
- the native control channel: ready and stop frames (`src/native-control.ts:3-5`);
- the Updater pipe protocol and its status names (`UpdaterProtocol.cs:6-9`);
- the `active.json` format and the health response shape (`src/server.ts:791-795`).

Adding a contract raises `contractMax`. Dropping one raises `contractMin`. Both
are deliberate. CI runs every supported old app on new infrastructure, and the
new app on every supported infrastructure (section 12).

## 7. App apply sequence (`--apply-app`)

**Who applies it.** The Updater (LocalSystem) launches the installed engine
`infra\<current>\setup\Yahad.Setup.exe --apply-app <staged-dir> --result <file>`,
detached, as today (`UpdateLauncher.cs:7-19`). The installed engine already holds
the elevated state machine, backup, health gate, rollback and repair state
(`UpgradeHost.cs:107-269`). The Updater would need a second copy of all of that.

`AppUpgradeHost` persists every phase to `upgrade-state.json` (format 2,
`kind: "app"`, from-pair and to-pair). The repair task points at the same
engine, `infra\<active>\setup`. An app apply never deletes an `infra\` tree, so
that engine can resume or undo every phase.

| # | Phase | Action | On failure |
| --- | --- | --- | --- |
| 1 | `admitting` | Require admin, the install lock, record phase `committed`, and no upgrade state (`UpgradeHost.cs:112-115`). Verify the signature over raw bytes, then the strict parse. Read the **exact** file `yahad-windows-release-<v>.json`, never a glob (`UpgradeHost.cs:141`). Check the archive size and hash. Require app version > active, `requiresContract` in the installed contract range (section 6), and that `infrastructure.version` equals the installed one (otherwise this is a Setup-track release). Require that the **recorded schema** (4.5) is the target or is in `databaseMigrationsFrom`. This needs no running Runtime, so an app that fixes a startup failure is still admitted. Check free disk for the tree and a database copy. | Refuse. Nothing has changed. |
| 2 | `preparing` | Extract to `apps\<v>.partial` with `AppPackage` and no-replace commits. Check `app-tree.json` against `treeSha256`. Rename to `apps\<v>`. | Discard: remove the partial and the target. |
| 3 | `fencing` | Clear Runtime failure actions (`ClearFailureActions`, as `UpgradeHost.cs:169`), so the SCM cannot restart the old app behind the engine. | Discard: restore failure actions. |
| 4 | `stopping` | Stop the LAN proxy if present (`StopForUpgradeAsync`, as `:170`), then the Runtime. The Updater keeps running: it holds no app file, and the engine is detached. | Discard: start the Runtime and the proxy, then restore failure actions. |
| 5 | `stopped` | Take the **data lease** (`WindowsFileBoundary.AcquireDataLease`). It proves no process holds the database, and the engine holds it until step 8. The backup runs under this held lease. `DatabaseBackup.Create/Restore` (`DatabaseBackup.cs:23,38`) gain an overload that takes it instead of acquiring their own. If the schema changes, take the backup. | Discard (pointer unchanged): release the lease, start the Runtime and the proxy, then restore failure actions. |
| 6 | `switching` | Atomically replace `active.json` with the to-pair. Regenerate the LAN help page from the **new** app (6a). | Roll back. |
| 7 | `started` | Release the lease (the host takes it at start: `InstalledLaunch.cs:60`). Start the Runtime. Gate on health: version = new app, schema = target, 3 consecutive passes within 300 s (`UpgradeHost.cs:53-64`). | Roll back. |
| 8 | `healthy` | Start the proxy (as `:178`), then restore failure actions (`EnableStartup`). | Roll back. |
| 9 | `committed` | Write the format-7 record with the new `App`, the old pair as `Previous`, and the target schema. Remove trees and backups no longer referenced by the active pair or `Previous` (as `:255-269`). Delete the upgrade state. | `UPGRADE_COMMIT_INCOMPLETE`. Repair finishes the commit (idempotent). |

**Roll back** (from phases 6-8): stop the proxy and Runtime, and take the lease.
Restore `active.json` to the from-pair. Restore the database if it was backed
up. Regenerate the help page from the restored app. Release the lease, start
the Runtime, and gate health against the old version and schema. Then start the
proxy, restore failure actions, and report `ROLLED_BACK_UNHEALTHY`. Rollback
failure reports `ROLLBACK_FAILED`, and repair retries, as today
(`UpgradeHost.cs:206-247`).

Every exit path, whether discard, rollback or commit, ends with failure actions
restored and the lease released. Repair replays the same rule from the persisted
phase.

**6a. LAN served files.** Today `RefreshServedFiles` writes both the help page
and the Caddyfile (`NativeLanInstall.cs:230-236`), and it reads the template
through `application.Files` and `versions\<v>\payload` (`:91-98`). It is split:

- `RefreshHelpPage(appTree)` reads `deploy\lan-help.html` and
  `lan-capabilities.json` from the active app tree, verified against
  `app-tree.json`. It writes `join-public\index.html`, which Caddy serves directly
  (`LanDeployment.cs:67`).
- `WriteProxyConfig()` writes the Caddyfile. It belongs to the infrastructure
  track and LAN configuration only. The app track never calls it.

**What an app update does not do:** it never stops the Updater, never changes a
service image, registration or firewall rule, and never extracts runtimes. The
proxy is stopped from step 4 to step 8, as in a full upgrade. So no LAN client
writes during the gate, and a schema rollback cannot discard work acknowledged
to a LAN user. The PC's own loopback browser can still reach the Runtime during
the gate, exactly as in today's full upgrade (decision 8).

## 8. Infrastructure track (Setup) with pairs

### 8.1 Upgrade (`--quiet-upgrade`)

Today `UpgradeHost` derives the target folder, the three images and rollback
from one `From/To` (`UpgradeHost.cs:77-79,174,241`). It is generalised to pairs:
from `(infraA, appX)` to `(infraB, appY)`, where `appY` is embedded in the Setup.

- **Prepare:** `infra\B` and `apps\Y`.
- **Switch:** the three images to `infra\B` (Runtime, Updater, proxy), the
  firewall to `infra\B`'s Caddy, `WriteProxyConfig`, `RefreshHelpPage(Y)`, and
  the pointer to `(B, Y)`.
- **Roll back:** to the from-pair. Images, firewall and pointer return to
  `(A, X)`, and the database is restored when the schema changed. The from-pair
  was committed together, so it is compatible by construction.

The phases, fencing, lease rule and exit rule are those of section 7.

**Repair never runs from a tree it may delete.** Today `RepairHost` refuses
to undo from inside the target tree (`RepairHost.cs:28`), and rollback deletes
every member of it (`UpgradeHost.cs:250`). That rule stays: until commit, the
repair task stays on the from-pair's engine `infra\A\setup`, and only the
commit re-registers it to `infra\B\setup`. Both engines know format 7. Any
engine whose next action deletes its own tree first relaunches from a private
copy. The precedent is uninstall, which copies itself into a SYSTEM- and
Administrators-only directory and restarts from there (`SetupOperation.cs:81-88`).

### 8.2 A full Setup whose infrastructure is already installed

An administrator may run a newer Setup over a machine whose infrastructure is
the same version (`UpgradeHost.Applies`, `:85-90`). The engine compares its
embedded infra members against the installed `infra-tree.json`:

- **Identical:** it applies only its embedded app, through the section 7 state
  machine. `infra\` is untouched.
- **Different bytes under the same infra version:** refuse with
  `INFRA_IDENTITY_MISMATCH`. That is a release-tooling defect, never something
  to overwrite.

The manual path also enforces `minimumFrom` and the schema rule. Today
`FromThisSetupAsync` checks neither `minimumFromVersion` nor migration sources
(`:91-106`).

### 8.3 First install

`InstallFlow` runs prepare, service publishing, optional LAN, then start
(`InstallFlow.cs:37-41`). Today it extracts into `versions\<v>` and saves
`committed` inside `PublishServiceAsync` (`NativeSetupHost.cs:95-98,159-162`).
New order:

1. **Prepare:** extract `infra\<i>` and `apps\<a>`, each with the existing
   owned-members-plus-partials resume rule (`NativeSetupHost.cs:93-98`) over both
   inventories. Then write `active.json`, idempotently.
2. **Provision:** create the services with images under `infra\<i>`, then
   register the repair task and the Apps & Features entry, both on the installed
   engine. Save `provisioned`, not `committed`.
3. **Start:** LAN, then start. The health gate checks `ok`, the app version and
   the schema. Today it checks only `ok` (`NativeSetupHost.cs:205`), and
   `committed` is saved before the first start (`NativeSetupHost.cs:162`).
4. **Commit:** save `committed` with the reported schema.

A crash in steps 1-3 resumes from the `preparing` or `provisioned` record. Every
step checks before it writes.

**Over an existing install.** The engine accepts only a format-7 record (a
kept `uninstalled` one included) and the new layout. A `versions\` layout or any
other record format is refused before anything is written, with
`LEGACY_INSTALL_UNSUPPORTED`. The message says to remove that development build
completely first. No such install exists (section 11), so nothing is carried
over.

### 8.4 Uninstall

`RemovalIntent` knows only `versions\<v>` and the key (`RemovalIntent.cs:19-31`),
and removal refuses unlisted files (`NativeSetupHost.cs:277,315`). The removal
inventory becomes:
- the key and `active.json`;
- every member of the active and `Previous` `infra-tree.json` and `app-tree.json`;
- the installed engine.

The receipt and the `Inspect` rules stay unchanged. The uninstall string points
at the installed engine.

## 9. Updater

### 9.1 Selection

The Updater reads only the format-2 manifest. A newer tag
without it is "not an update for this machine", as today for a tag without
Windows assets (`UpdaterEngine.cs:135-138`). After the signature and parse:

1. If `infrastructure.version` is newer than the installed one and the app is
   newer, take the **Setup track**. Its Setup carries the new app too, so
   infrastructure fixes such as a Node security update are never skipped. Every
   release carries an app, so a release whose app is not newer is not an update on
   either track: the Setup track would downgrade or replay the app.
2. Otherwise, if the app is newer, `infrastructure.version` equals the installed
   one, and `requiresContract` is in its contract range, take the **app track**.
3. Otherwise, if the app needs a contract outside the release's own
   infrastructure range, report `APP_NEEDS_INFRASTRUCTURE`. That is a defect in the
   release, never a silent "up to date".

### 9.2 Live identity

Today the engine takes `current` once, at construction
(`UpdaterService.cs:39`; `UpdaterEngine.cs:53`). It uses it for status, the
"newer" check, rediscovery and staging cleanup (`:106,133,175`). Ingesting a
result does not change it (`:229-231`).

After an app update the Updater keeps running, so `current` becomes a function
that reads `active.json`. Every decision re-reads it, and `Ingest` re-reads it
before `Rediscover` and `CleanStaging`, after a commit and after a rollback.
`status.current` stays the app version, so `src/updates.ts` is unchanged.

### 9.3 Staging and targets

A staged release is `(version, track, fingerprint)`. The fingerprint is the
SHA-256 of `track || "\n" || manifest bytes`; today it is a hash of the Setup
manifest alone (`UpdaterEngine.cs:17,190`). Each track's stage holds only that
track's files:
- **app track:** the manifest, its signature and the app zip;
- **Setup track:** the manifest, its signature and the Setup.

A change of track, for example a Setup release appearing over a staged app
release, deletes the stage. An `install` carrying the old fingerprint then gets
`STALE_TARGET` (`:199-204`). The engine reads exact names in the stage, never a
glob.

### 9.4 Failure classes and result codes

| Condition | Code | Error class (`UpdaterRules`, `updates.ts:9`) | Fallback |
| --- | --- | --- | --- |
| No format-2 manifest in a newer release | none | not an update | none |
| Bad signature, key or manifest | `RELEASE_SIGNATURE_INVALID`, `…_KEY_UNTRUSTED`, `…_MANIFEST_INVALID` | `signature`, version refused | **none**: a bad signature never selects another path |
| App archive size or hash mismatch | `RELEASE_APP_SIZE`, `RELEASE_APP_HASH` | `archive`, retry next check | none |
| App validator refusal | `APP_PACKAGE_*` | `archive` | none |
| Contract too high, no newer infrastructure | `APP_NEEDS_INFRASTRUCTURE` | `incompatible`, version refused | none (release defect) |
| Schema or `minimumFrom` not admissible | `RELEASE_UNSUPPORTED_FROM` | `incompatible` | none |
| Same infra version, different bytes | `INFRA_IDENTITY_MISMATCH` | `internal` | none |
| Host: bad pointer or tree | `ACTIVE_POINTER_INVALID` (startup diagnostic) | n/a | repair |

The new codes are added to `SetupOperation`'s code sets (`SetupOperation.cs:66-72`)
and to `UpdaterRules.Ending` and `CheckError` (`UpdaterEngine.cs:23-42`). They map
onto the existing `updates.ts` error enum, so the app's status schema doesn't
change.

## 10. Release tooling

**Assets.** `/releases/latest` stays the only source for both platforms
(`GitHubReleaseSource.cs:14`; `deploy/updater.py:678`). Every release carries
every asset a platform might need (`docs/GITHUB-RUNTIME-UPDATES.md:284-288`):

- the four Linux assets, unchanged;

- `yahad-windows-release-<v>.json(.sig)`;
- `yahad-windows-app-<v>.zip`;
- `Yahad.Setup-<v>.exe` and `Yahad.Setup.exe` (the landing page link).

`collectReleaseAssets` today accepts exactly eight names (`scripts/release-windows.mjs:89-97`).
It moves to this exact set of nine names, and checks:
- the signature against the committed key;
- that the manifest names this version and the Setup bytes;
- that the app zip matches its size and hash, and that the `app-tree.json` inside
  it matches `treeSha256`;
- that the package embedded in the Setup is the one the manifest signs. The Setup is a
  compressed single-file bundle, so the check runs the release's own Setup with
  `--verify-package` (which verifies the embedded zip, its members and both tree records)
  and compares its reported identity field by field with the signed manifest.

**Signing stays local.** `windows-release-key.mjs sign` always uses
`yahad-release-v1\n` (`scripts/windows-release-key.mjs:11,36`). It switches to
`yahad-release-v2\n` and refuses any manifest that is not format 2 of
`yahad-windows-release`. The manifest is signed locally with the maintainer's
key. No CI or GitHub secret is involved.

**Provenance per component.** Today every native artifact must report the
release's app version and commit (`installer/windows/application_version.py:46-51`;
`setup_package.py:293-295`), and the Setup audit requires one source identity
(`installer/package.ps1:127`). A cached infrastructure build would fail both. So:
- **Infrastructure artifacts** (host, Updater, Platform, engine, runtimes)
  report `infrastructureVersion` and `infraSourceCommit`, and are checked
  against the cached infra record: version, commit and per-member hashes.
- **App artifacts** (the server bundle, `dist/`, fixtures, `deploy/`) carry the
  app version and the release commit, checked as today. No native validation
  remains on the app side.
- **The audit** checks each half against its own identity, and checks the
  embedded infra tree against the cache by bytes.

**Building infrastructure once.** The infrastructure build runs at an infra
version's first release, from that commit. It is cached by
`infra-tree.json` digest. Later releases reuse it and build only the app
and the distributable Setup (decision 7).

**Version bump rule, per component.** "Anything under `installer/`" is not the
right test, because infra inputs also live outside it. The release command
computes an **infrastructure input digest** over:
- `installer/Yahad.ServiceHost`, `Yahad.Updater`, `Yahad.Platform`,
  `Yahad.Setup` and `Yahad.Install.Core`, excluding tests;
- `installer/Directory.Build.props`, `NuGet.Config` and the infra build scripts
  (`package.ps1` infra steps, `installer/windows/*`);
- the pinned .NET runtime version (`package.ps1:88`) and the Node and
  Caddy lock;
- the release key.

If the digest differs from the last infra version's recorded digest, an
infrastructure bump is required, and the reverse also refuses. With Platform in
infrastructure, no source is shared between the tracks, so every input belongs
to exactly one digest. The app's inputs are what `bundle.py` packs. Changing
the contract range is a separate, reviewed decision, proven by the CI
compatibility runs.

The app version keeps `chooseVersion` (`release-windows.mjs:303-310`).

**Platform-path migration.** Every place that puts the helper under the app or
launches the app without the host has to change in the same step, with no
fallback path left behind:
- the dev build copying the helper into `native`: `installer/build.ps1:37`;
- the fixture builder's `--describe` check, its copy to `app/native` and its
  runtime-lock entry: `installer/windows/prepare_core_fixture.py:46,54,95`;
- the bundle validator's helper requirements and paths:
  `installer/windows/bundle.py:20,80-91,133`, the package requirement in
  `InstallPackage.cs:37` and the audit in `setup_package.py:293`;
- harnesses that start the app directly and must pass an absolute
  `YAHAD_PLATFORM_HELPER`: `Yahad.FirstInstall.Tests/PackagedNativeStartupProbe.cs:37`,
  `tests/windows-package-runtime.test.mjs:46`, `StoreFixture.cs:65,134`,
  `native-launch-http.test.mjs:46`, `platform-native.test.ts:15`;
- test package inventories that pin the old path:
  `FullInstallerIntegrationTests.cs:213` and the test `Program.cs:272`.

**Legal files per track.** `build-server.mjs` writes the app's
`server-bundle.json` and `ThirdPartyNotices-server.txt`, which are used to check
sources and dependencies (`setup_package.py:174`). They move to `app/legal/`,
are listed in `app-tree.json` and are checked against the app's identity. The
infra licenses and `runtime-lock.json` stay in `infra\<v>\legal\`, and each
track gets its own SBOM.

## 11. Existing installs: none, so no bridge

The maintainer confirmed that no Windows install exists on any computer. The
installer was a development candidate that was never accepted on a family
computer (`README.md` "Windows installer status", `docs/VALIDATION.md`). So the
first release with the new layout is the first Windows release anyone installs:
- no bridge release, no legacy manifest, no format-6 or format-1 upgrade-state
  reader, and no `versions\` code path are built, so there is no migration code
  to retire later;
- a leftover development build is refused (8.3, `LEGACY_INSTALL_UNSUPPORTED`)
  and removed by hand;
- today's `versions\` code is replaced, not kept beside the new layout.

The Linux host runs a manual install that may be overwritten if needed. This
plan changes no Linux asset, and Linux needs no migration either.

## 12. Test plan

- **Unit (`Yahad.Install.Tests`):**
  - the format-2 manifest's strictness and size;
  - a v1-prefixed signature never verifies under v2;
  - `AppPackage`: namespace, collisions, bounds, required members, exact match
    with `app-tree.json`;
  - tree-record hashing;
  - selection over every combination of infra newer, equal and contract;
  - live identity after commit and rollback;
  - track change leading to `STALE_TARGET`;
  - code mappings;
  - the atomic pointer;
  - host refusal on a mismatch of infra version, contract, tree record or
    member set;
  - Platform `RequireServer` against the compiled infra version, and the app
    refusing a missing `YAHAD_PLATFORM_HELPER`;
    - the `AppPackage` refusal of PE images and native-module members;
  - every direct launcher in the section 10 migration list passing
    `YAHAD_PLATFORM_HELPER`, and nothing under `app/native/` in any inventory.
- **Payload-free engine:** quiet uninstall and Apps & Features uninstall, in both
  data modes, plus `--repair` and `--apply-app`, all on the installed engine with
  no embedded package.
- **First install and reinstall:** a kill at each of prepare, provision, start
  and commit. Expect no `committed` record before health reports the app version
  and schema. A reinstall over a kept format-7 record with an older stored schema
  migrates after a backup, and one outside `databaseMigrationsFrom` is refused
  before the first start. A `versions\` layout or a format-6 record is refused
  with `LEGACY_INSTALL_UNSUPPORTED`, and nothing is written.
- **State machine:** inject a kill at each persisted phase of `AppUpgradeHost`,
  and of the pair-aware `UpgradeHost`, then run the real repair task.
  - Expect "committed" or "the exact from-pair", never mixed. Failure actions
    are restored and no lease is left held.
  - Cover schema-changing and non-schema apps, and a failure in stop and in
    backup with the pointer never moved.
    - Include an infra rollback that deletes the tree of the engine running it
    (private-copy relaunch, 8.1).
- **CI upgrade sets** (`installer/ci-build-upgrade-set.ps1`, `ci-test-suites.ps1`):
  - first install of the new layout → app-only;
  - app-only unhealthy → rollback;
  - app → Setup-track infra upgrade → rollback to the earlier pair;
  - an app needing a newer contract → `APP_NEEDS_INFRASTRUCTURE`;
  - a same-infra manual Setup;
  - first install and resumed first install;
  - uninstall in both modes after app updates;
  - with LAN: firewall rules and Caddyfile are unchanged after an app update,
    and the help page matches the active app after a commit and after a
    rollback.
- **Contract compatibility:** every supported app on every supported
  infrastructure, in both directions (old app on new infrastructure, new app on
  the oldest infrastructure whose range contains its contract), covering lease
  adoption, native control, Platform commands, the Updater pipe and health. An app
  whose contract is below `contractMin` is refused, not started.
- **Release tooling:** `collectReleaseAssets` and `sign` refusal cases, a cached
  infra provenance audit, and the infra input digest bump rule.
- **Linux:** no behaviour change. `npm run test:updater` stays green with the
  extra assets present.
- **Machine acceptance** (`installer/machine-acceptance.ps1`) on the ASUS test
  machine before any release uses the app track. Windows runtime startup there
  is still under diagnosis (README "Windows installer status").

## 13. Risks

- **Contract drift:** an app relies on host behaviour the infrastructure
  doesn't provide. Mitigation: the section 6 contract and the CI compatibility
  run.
- **Pointer integrity:** the host trusts `active.json`. Mitigation: the
  binary-root ACL (SYSTEM and Administrators write), the infra-version and
  contract match, the tree-record and member-set check, and refusing rather
  than guessing.
- **One data set, three engines:** `UpgradeHost`, `AppUpgradeHost` and repair
  share the install lock and one `upgrade-state.json`, so they never overlap.

- **The Windows updater is not yet proven on a family machine.** Every track
  depends on it. Machine acceptance comes before the app track's first use.
- **Moving Platform changes the app-to-helper path.** A new app on an
  infrastructure without `YAHAD_PLATFORM_HELPER` must refuse to start. It must
  not fall back to an app-relative path. The new layout's first infrastructure
  version sets contract range 1..1 with this variable.

## 14. Decisions

The maintainer accepted every recommendation (2026-11-15):

1. **Infrastructure version:** an independent semver, moved by the section 10
   bump rule.
2. **Updater self-update:** not now. The Updater stays on the Setup track.
3. **First install:** the Setup embeds the app, so it works offline.
4. **Auto window:** app-only updates also wait for 03:00-04:59
   (`UpdaterEngine.cs:46-47`), because the apply restarts the Runtime. A manual
   "update now" stays available.
5. **Retiring 0.6.x:** nothing to retire, because no install exists (section 11).
6. **Shrinking the infrastructure download:** NativeAOT for the host, Updater and
   Platform, as its own infrastructure-track change with
   an AOT test in CI. Neither blocks this plan.
7. **Per-release distributable Setup:** publish `Yahad.Setup` from a worktree at
   the infra source commit, embedding the cached infra and the new app. No new
   executable.
8. **Loopback writes during the health gate:** kept as today
   (`UpgradeHost.cs:171,176-177,242`). A read-only maintenance state is its own
   later change, for both tracks.
9. **Existing installs:** none, so there is no bridge (section 11).

## 15. Implementation order

Each phase is its own PR, built and tested locally. No release is published.

1. **Platform to infrastructure:** the host passes `YAHAD_PLATFORM_HELPER`, with
   the section 10 migration list.
2. **Layout and record:** `infra\`, `apps\`, `active.json` and format 7, with
   the 4.4 and 8.3 fixes: a payload-free uninstall, and commit only after health.
3. **App package and release format:** `AppPackage`, the format-2 manifest, and
   the section 10 tooling.
4. **App apply and the Updater:** `--apply-app` with rollback, and track
   selection.
5. **Tests and CI:** section 12.

A full install on a real test machine comes before the first app-only update
(section 13).

## Appendix: review disposition

All 33 findings were checked against the source at `2e12ea0`, and all were
accepted. Two were narrowed:
- **#12:** the health value isn't stale while the app is healthy, because
  startup refuses a mismatched stored schema (`store.ts:84`). The real defect is
  that admission depends on a running Runtime.
- **#13:** loopback writes during the gate already exist today (decision 8).

| Finding | Where handled |
| --- | --- |
| Reference fixes (5) | §1, §3, §5.1, §5.2 |
| 1 Bridge repair | §11: no bridge is built, because no install exists |
| 2 Installer record | §4.5 |
| 3 Updater `current` | §9.2 |
| 4 Platform `RequireServer` | §2a, §4.3 (Platform moved to infrastructure) |
| 5 App precedence | §9.1 |
| 6 Signed infra metadata | §5.1 |
| 7 Infra upgrade after app updates | §8.1, §4.5 |
| 8 First install | §8.3 |
| 9 Uninstall | §8.4 |
| 10 Failure before switch | §7 table |
| 11 Fencing and lease | §7 steps 3, 5 |
| 12 Schema source | §4.5, §7 step 1 |
| 13 LAN writes during gate | §7, Q8 |
| 14 `package.json` and cwd | §4.3, §5.2 |
| 15 `app/deploy` and the LAN reader | §5.2, §7 6a |
| 16 Tree verification scope | §5.3 |
| 17 `app-tree.json` contract | §5.3 |
| 18 ACLs | §4.2 |
| 19 `RefreshServedFiles` and rollback | §7 6a, rollback |
| 20 App validator | §5.2 |
| 21 Infra-cache provenance | §10 |
| 22 Setup identity in `infra\` | §4.4, §8.2, Q7 |
| 23 Private .NET runtime | §2a: Platform moved to infrastructure, no runtime in the app package; Q6 for infra size |
| 24 Bump rule | §10 |
| 25 Lease and control contracts | §6, §12 |
| 26 Signing and asset collection | §10 |
| 27 Staging and target identity | §9.3, §7 step 1 |
| 28 Fallback, codes, retirement | §9.4, §11 |

### Revision 3 review

A second independent review checked revision 3 against the source. All six
findings were verified at the cited lines and accepted:

| Finding | Evidence | Handled in |
|---|---|---|
| P1: the payload-free engine can't uninstall, because the package is opened before the operation branch | `SetupOperation.cs:45`, `InstallPackage.cs:44` | §4.4, §12 |
| P1: repair would run from the tree its rollback deletes | `RepairHost.cs:28`, `UpgradeHost.cs:250` | §8.1, §12; the bridge itself is dropped (§11) |
| P2: first install saves `committed` before health, which checks only `ok`; a kept format-6 record has no schema | `NativeSetupHost.cs:162,205`, `InstallState.cs:12`, `store.ts:60` | §4.5, §8.3, §12 |
| P2: `requiresContract ≤ N` is wrong for exact, non-cumulative contracts | `WindowsFileBoundary.cs:113`, `platform-files.ts:41` | §4.3, §5.1, §6, §7, §9.1, §12 |
| P2: the Platform-path move missed build, fixture, validator and harness sites, and "as today" misdescribed the default path | `platform-files.ts:47,58`, `build.ps1:37`, `prepare_core_fixture.py:46`, `bundle.py:20` | §4.3, §10, §12 |
| P2: the app's legal and bundle metadata had no owner | `build-server.mjs:103-111`, `bundle.py:23` | §4.1, §5.2, §5.3, §10 |

The review agreed with the main verdict: the split is right and C#/.NET stays.
Platform belongs to infrastructure. The §5.2 ban blocks nothing the app needs,
because SQLite comes from `node:sqlite` (`store.ts:4`) and `sharp` stays outside
the bundle (`build-server.mjs:24`).
