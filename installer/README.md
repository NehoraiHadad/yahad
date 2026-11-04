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
4. FFmpeg/Caddy vendor inputs staged by the existing pinned-input verifier.

Exact vendor URLs, archive hashes, checksum-document hashes and selected member
hashes are in [dependency-pins.json](windows/dependency-pins.json). Download those
archives and checksum documents explicitly to a local build-input directory, then:

```powershell
python -m installer.windows.dependencies stage --ffmpeg-archive C:/build-inputs/ffmpeg.zip --ffmpeg-checksums C:/build-inputs/ffmpeg-checksums.sha256 --caddy-archive C:/build-inputs/caddy.zip --caddy-checksums C:/build-inputs/caddy-checksums.txt --output .local/vendors
```

Use the exact files named by the pins, even if your local filenames differ.
The verifier does not download or execute vendor inputs. Do not replace a pinned
binary without reviewing pins, runtime behavior and licenses together.

From a **clean committed checkout**, with npm development dependencies installed:

```powershell
$inputs = @{
    NodeDirectory = 'C:/build-tools/node-v24.x.x-win-x64'
    DotNet = 'C:/build-tools/dotnet/dotnet.exe'
    Python = 'python'
    NpmCache = 'C:/build-cache/npm'
    PackageCache = 'C:/build-cache/nuget'
    VendorDirectory = '.local/vendors'
}
./installer/package.ps1 @inputs -CheckOnly
./installer/package.ps1 @inputs
```

### Release signing

The Updater's upgrades (`Yahad.Setup.exe --quiet-upgrade <dir>`) accept only a release signed
by the key the installation trusts. Running a newer Setup by hand over an older
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
   `scripts/release-updater.mjs package-github`, build and sign Windows, and check the full set of eight assets.
   It prints version, commit, key id and asset sizes, and you type the version to confirm. Only then does
   it push the commit and tag atomically and run `gh release create ... --latest` (needs `gh auth login`);
   afterwards run `git pull --ff-only`. Nothing is pushed or published before the confirmation.

One release carries both platforms (`yachad-release.json`, `yachad-app.zip`, `yachad-updater.json` and
`yachad-updater.zip` for the Pi;
`yahad-windows-<v>.json`, `.json.sig` and `Yahad.Setup-<v>.exe` for Windows, and the same Setup as `Yahad.Setup.exe` for the landing page's fixed link), because both updaters read
GitHub's single `/releases/latest`; see [GITHUB-RUNTIME-UPDATES.md](../docs/GITHUB-RUNTIME-UPDATES.md).
Building Windows alone from a clean checkout is `node scripts/release-windows.mjs build <key.pem> --out <new-dir>`,
which writes the three Windows files and never publishes.

Installation writes the build's key to `%ProgramFiles%\Yahad.FirstInstall\release-key.spki`,
and that file is the trust root from then on. A machine installed from a keyless
build stays without one, so it needs a keyed reinstall before it can upgrade.

Replace the example tool paths with your actual extracted tools. The Node directory
must contain `node.exe`, `LICENSE` and `node_modules/npm/bin/npm-cli.js`; a path to
node.exe alone is insufficient. The dotnet directory must include its legal files.

Output: `.local/packages/<12-character-source-commit>/setup/Yahad.Setup.exe`,
the project LICENSE, native/system manifests, package hashes and local step logs.
The app helper is generated at `installer/artifacts/Yahad.Platform.Bundle/`, where
the existing payload builder expects it. The script refuses a pre-existing package
output or helper bundle to avoid mixing commits; use a clean build checkout.
It does not copy old outputs into backup folders or silently overwrite them.

The wrapper itself has syntax/preflight checks; its complete new entry point still
requires a full packaging run. The underlying commands produced earlier candidates,
but no current successful real installation is claimed.

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
