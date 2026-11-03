# Windows native builds

This directory contains the native installer, Windows service hosts, protected
updater and their tests. The current package remains a development candidate:
[Runtime installation acceptance is incomplete](../docs/VALIDATION.md).
Building a package does not install it or prove native acceptance.

## Build code and run isolated tests

Use Windows x64, .NET 10 SDK, Node 24 and PowerShell 7. Start with
[application setup](../docs/DEVELOPMENT.md).

```powershell
./installer/build.ps1
./tests/windows-updater/run.ps1 -DotNet (Get-Command dotnet).Source
```

`build.ps1 -Publish` publishes the Setup executable and the Platform helper. It
does **not** create the complete app/updater installer.

## Standalone app + updater installer

The tracked `installer/package.ps1` replaces machine-specific private build wrappers.
It calls the existing Python package validators and native projects. It derives the
version from package.json, requires clean committed source and new output, rebuilds
web assets, publishes the four project components, assembles the full payload and
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
