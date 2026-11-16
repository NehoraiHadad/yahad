# Windows native builds

This directory contains the native installer, the Windows service host and
their tests. The current package remains a development candidate:
[Runtime installation acceptance is incomplete](../docs/VALIDATION.md).
Building a package does not install it or prove native acceptance.

## Build code and run isolated tests

Use Windows x64, .NET 10 SDK, Node 24 and PowerShell 7. Start with
[application setup](../docs/DEVELOPMENT.md).

```powershell
./installer/build.ps1
./installer/ci-test-suites.ps1 -Suite Install -DotNet (Get-Command dotnet).Source -Node (Get-Command node).Source -PackageCache C:/build-cache/nuget -Output .local/suites/Install
```

`build.ps1 -Publish` publishes the Setup executable and the Platform helper. It
does **not** create the complete installer.

## Standalone installer

The tracked `installer/package.ps1` replaces machine-specific private build wrappers.
It calls the existing Python package validators and native projects. It derives the
version from package.json, requires clean committed source and new output, rebuilds
web assets, publishes the project components, assembles the full payload and
embeds it in one self-contained Setup executable. It then runs **read-only**
`--verify-package` and checks exact source/ZIP identities. It does not install,
register services, change ACLs/firewall/trust or publish a release.

Required external inputs:

1. Official Node **24.x Windows x64 ZIP**, extracted with its LICENSE and npm CLI.
2. .NET 10 SDK with its LICENSE/ThirdPartyNotices; publishing restores the currently
   pinned private runtime **10.0.14**. SDK/package downloads require network access
   on the build machine.
3. A populated npm cache. From this checkout run
   `npm ci --ignore-scripts --cache C:/build-cache/npm` once with network available.
   Application packaging subsequently uses production-only, offline restore and
   refuses a missing cache entry rather than falling back to the network.
4. Caddy vendor inputs staged by the existing pinned-input verifier.

Exact vendor URLs, archive hashes, checksum-document hashes and selected member
hashes are in [dependency-pins.json](windows/dependency-pins.json). Download that
archive and checksum document explicitly to a local build-input directory, then:

```powershell
python -m installer.windows.dependencies stage --caddy-archive C:/build-inputs/caddy.zip --caddy-checksums C:/build-inputs/caddy-checksums.txt --output .local/vendors
```

Use the exact files named by the pins, even if your local filenames differ.
The verifier does not download or execute vendor inputs. Do not replace a pinned
binary without reviewing pins, runtime behavior and licenses together.

From a **clean committed checkout**, with npm development dependencies installed:

```powershell
$inputs = @{
    NodeDirectory = 'C:/build-tools/node-v24.22.2-win-x64' # the archive pinned in installer/windows/dependency-pins.json
    DotNet = 'C:/build-tools/dotnet/dotnet.exe'
    Python = 'python'
    NpmCache = 'C:/build-cache/npm'
    PackageCache = 'C:/build-cache/nuget'
    VendorDirectory = '.local/vendors'
}
./installer/package.ps1 @inputs -CheckOnly
./installer/package.ps1 @inputs -InfraCache C:/build-cache/yahad-infra
```

#### Infrastructure cache and the bump rule

A release has two halves with separate identities
([plan section 10](../docs/WINDOWS-APP-PACKAGE-PLAN.md#10-release-tooling)):
the **infrastructure** (service host, Updater, Platform helper, Setup engine,
Node, Caddy and the .NET runtime), versioned by `<YahadInfrastructureVersion>` in
`installer/Directory.Build.props` with its `ContractMin`/`ContractMax` range, and the
**app** (server, web assets, deploy files), versioned by `package.json` and requiring
the contract in `package.json` `yahadInfrastructureContract`.

`-InfraCache <dir>` keeps each infrastructure build in `<dir>/<infrastructure-version>/`
with `infra-build.json` (version, source commit, input digest, tree hash). A later
package whose infrastructure input digest equals the cached one reuses those exact
bytes; the audit checks them against `infra-tree.json`. `-InfraSource <checkout>` names
the checkout at the infrastructure's source commit that the distributable Setup is
published from (default: the current checkout). The release commands
(`scripts/release-windows.mjs build`, `install`, `publish`) pass it themselves when the
cache entry for HEAD's infrastructure version was built at another commit: a git worktree
at that commit (docs/WINDOWS-APP-PACKAGE-PLAN.md section 10).

The digest is `python installer/windows/infra_inputs.py digest [--commit <sha>]`: it
covers the infrastructure projects (not their tests), `Directory.Build.props`,
`NuGet.Config`, `package.ps1`, `installer/windows/*`, the Node and Caddy pins and the
committed release key. The rule is strict both ways: a digest that differs from the
latest release's infrastructure commit requires a newer infrastructure version, and an
equal digest requires the same version. A cached version whose digest differs is refused
with `INFRA_BUMP_REQUIRED`. Changing the contract range is a separate, reviewed decision.

### Release signing

A Windows release is one format-2 manifest, `yahad-windows-release-<v>.json`, signed
over the prefix `yahad-release-v2\n` plus its raw bytes into `yahad-windows-release-<v>.json.sig`.
The Updater picks a track from it: a newer infrastructure version takes the **Setup
track** (the staged `Yahad.Setup-<v>.exe --quiet-upgrade <dir>`, which carries the app
too); the same infrastructure with a newer app takes the **app track** (the installed
engine's `--apply-app <dir>` with `yahad-windows-app-<v>.zip`, leaving services,
firewall and Caddy untouched); an app that needs a contract the release's own
infrastructure does not offer is refused with `APP_NEEDS_INFRASTRUCTURE`. Both tracks
accept only a release signed by the key the installation trusts. Running a newer Setup by hand over an older
installation makes the same move: the administrator elevated that image, and it must carry the same key. There is no override switch, environment
variable or registry value. The private key stays on the maintainer's PC: it is
never committed, never uploaded and never a CI or GitHub secret, and CI signs
its test releases with a key generated and destroyed inside the job.

Until the maintainer does the one-time step below, no build trusts any key. Such
a Setup installs normally and refuses every upgrade; `--verify-package` reports
`"releaseKeyId":null` and `"upgrades":"refused: built without a release key"`,
and `package.ps1` prints a warning.

Maintainer runbook. The key stays on this PC: no GitHub secret, no Authenticode
certificate (Windows SmartScreen warns on the first install; that is accepted).

1. **Once: create the key** outside any checkout (the tool refuses a directory inside one).
   The commands below look for it at `~/yahad-release-key/key.pem` (pass `--key <path>` to use another):

   ```powershell
   node scripts/windows-release-key.mjs keygen $HOME/yahad-release-key
   ```

   Back up `key.pem` in a password manager. Losing it means existing installations
   can never be upgraded again, and anyone holding it can sign their upgrades.
2. **Once: commit the public half**, `key.spki` (91 bytes), as `installer/release-key.spki`.
   It is public. Until it is committed, both commands below refuse to run.
3. **Install on this PC from the current commit**:

   ```powershell
   npm run windows:install
   ```

   Needs a clean tree. It clones HEAD into a temp folder, runs `npm ci`, builds and signs
   there (same `installer/package.ps1` and signing tool CI uses, inputs from this checkout's
   `.local/`), copies `Yahad.Setup-<version>.exe` to `.local/windows-install/`, deletes the
   clone, and starts Setup, which shows Windows' single UAC prompt. It publishes nothing.
4. **Release** (maintainer only):

   ```powershell
   npm run release:publish            # add --dry-run to stop before pushing; --version X.Y.Z to override the patch bump
   ```

   Requires a clean checkout of `main` equal to `origin/main`, so the Raspberry Pi never receives an
   unmerged branch. Everything happens in a throwaway clone: bump the patch version (refused unless above
   the latest release), commit, `npm ci`, build the app, package the Linux assets with
   `scripts/release.mjs package-https`, package the root updater with
   `scripts/release-updater.mjs package-github`, build and sign Windows (reusing the cached
   infrastructure when the bump rule above says it is unchanged), and check the full set of nine assets.
   It prints version, commit, key id and asset sizes, and you type the version to confirm. Only then does
   it push the commit and tag atomically and run `gh release create ... --latest` (needs `gh auth login`);
   afterwards run `git pull --ff-only`. Nothing is pushed or published before the confirmation.

One release carries both platforms (`yachad-release.json`, `yachad-app.zip`, `yachad-updater.json` and
`yachad-updater.zip` for the Pi;
`yahad-windows-release-<v>.json`, `yahad-windows-release-<v>.json.sig`, `yahad-windows-app-<v>.zip`
and `Yahad.Setup-<v>.exe` for Windows, and the same Setup as `Yahad.Setup.exe` for the landing page's
fixed link), because both updaters read GitHub's single `/releases/latest`; see
[GITHUB-RUNTIME-UPDATES.md](../docs/GITHUB-RUNTIME-UPDATES.md). Before publishing, the command checks the
signature against the committed key, that the manifest names this version, the Setup bytes and the app
zip's size and hash, that the zip's `app-tree.json` matches `app.treeSha256`, and that the
package embedded in the Setup is the one the manifest signs: it runs that Setup's
`--verify-package` (which checks the embedded zip, its members and both tree records against its
own install manifest) and compares the reported infrastructure and app identity, contract,
schema, migrations, minimum-from pair and release key with the signed manifest. The Setup is a
compressed single-file bundle, so its embedded bytes are not visible from outside it.
Building Windows alone from a clean checkout is `node scripts/release-windows.mjs build <key.pem> --out <new-dir>`,
which writes the four Windows files and never publishes.

CI never sees this key. `installer/ci-build-upgrade-set.ps1` generates a throwaway key in the
job, builds the acceptance upgrade set from the exact commit with one shared `-InfraCache`
(base `n`, app-only `app`, an app that exits after migrating, an infrastructure bump `n1`, a
broken app on `n1`, a contract-too-high manifest, `n1`'s manifest naming another Setup's bytes
and foreign, unsigned and tampered manifests on both tracks, each staged in a directory named
by its version), signs it with `scripts/windows-release-key.mjs sign` and destroys the key with the job.

Installation writes the build's key to `%ProgramFiles%\Yahad.FirstInstall\release-key.spki`,
and that file is the trust root from then on. A machine installed from a keyless
build stays without one, so it needs a keyed reinstall before it can upgrade.

Replace the example tool paths with your actual extracted tools. The Node directory
must contain `node.exe`, `LICENSE` and `node_modules/npm/bin/npm-cli.js`; a path to
node.exe alone is insufficient. The dotnet directory must include its legal files.

Output: `.local/packages/<12-character-source-commit>/setup/Yahad.Setup.exe`; under
`release/`, the unsigned `yahad-windows-release-<v>.json`, `yahad-windows-app-<v>.zip` and
`Yahad.Setup-<v>.exe`; the project LICENSE, native/system manifests, package hashes and local step logs.
The Platform helper is published into the package output beside the service host
and the Updater and packaged as infrastructure (`platform/`); the app payload never
carries it. The script refuses a pre-existing package output to avoid mixing
commits; use a clean build checkout.
It does not copy old outputs into backup folders or silently overwrite them.

The wrapper itself has syntax/preflight checks; its complete new entry point still
requires a full packaging run. The underlying commands produced earlier candidates,
but no current successful real installation is claimed.

## Installed layout

Binaries are administrator-owned under `%ProgramFiles%\Yahad.FirstInstall`; state
lives under `%ProgramData%\Yahad.FirstInstall`
([plan section 4](../docs/WINDOWS-APP-PACKAGE-PLAN.md)):

```text
release-key.spki
active.json                       the active pair: infrastructure and app versions with their tree hashes
infra\<infrastructure-version>\
    host\Yahad.ServiceHost.exe    Runtime service (--service) and home-network service (--lan-service)
    updater\Yahad.Updater.exe     Updater service (--service)
    platform\Yahad.Platform.exe
    runtime\node\node.exe, runtime\caddy\caddy.exe
    legal\
    setup\Yahad.Setup.exe         payload-free engine: repair, uninstall, Apps & Features
    infra-tree.json
apps\<app-version>\
    app\                          server, web assets, deploy files
    app-tree.json
```

`active.json` is camelCase JSON, format 1. The three service images, the firewall
rule's program (the infrastructure's Caddy), the Repair task and the Apps & Features
`UninstallString` all point into the active `infra\<version>`. Setup keeps exactly the
active pair and the previous committed pair; the two pairs may share one
infrastructure directory. The installer record (`installer-state.json`) is format 7
with `Active` and `Previous` pairs. An installation recorded in any other format, or
one that still has a `versions\` directory, is refused with
`LEGACY_INSTALL_UNSUPPORTED` before anything is written.

Only install, `--verify-package` and `--quiet-upgrade` open the embedded install
package. Uninstall, `--repair` and `--apply-app` run the installed engine without it.
An upgrade whose infrastructure version is newer moves the three images to the new
`infra\` directory; one with an identical infrastructure tree, whether the app track's
`--apply-app` or a newer Setup run by hand, changes only `apps\` and the pointer.

## Distribution and installation are separate

An unsigned executable can show an unknown publisher in Windows. Keep the existing
updater trust/integrity controls. Include project/component notices and complete
the [distribution review](../docs/DISTRIBUTION-LICENSE-REVIEW.md); a generated inventory
with `completeLicenseReview:false` does not establish redistribution clearance.

Actual installation creates protected product folders, service identities and IPC
boundaries and needs its own explicit scope. LAN/firewall/certificate choices,
real WhatsApp, destructive uninstall and native update application are separate
acceptance actions. The source build commands above perform none of them.

Architecture references: [protected transactions](../docs/NATIVE-PROTECTED-TRANSACTIONS.md),
[service host](../docs/NATIVE-SERVICE-HOST.md), [runtime health](../docs/NATIVE-RUNTIME-HEALTH.md),
[uninstall](UNINSTALL.md).
