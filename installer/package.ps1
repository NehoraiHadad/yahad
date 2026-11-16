# Build-machine tool. Never launches installation, changes services or publishes.
#
# One release from this checkout (the release commit, HEAD) and one infrastructure
# build (docs/WINDOWS-APP-PACKAGE-PLAN.md 4.1, 5, 10):
# - the infrastructure tree is built once per infrastructure version into
#   -InfraCache/<version>/ (installer/windows/infra_package.py) and reused by every
#   later release whose infrastructure inputs (installer/windows/infra_inputs.py)
#   are the same; the same version with other inputs refuses, INFRA_BUMP_REQUIRED;
# - the app tree is staged from this checkout; the distributable Setup is published
#   from -InfraSource, a clean checkout of the cached infrastructure's source
#   commit, so every infrastructure image, the Setup and its Install.Core report
#   that one commit, while the embedded app reports this one.
#
# Outputs under -Output (a new directory; default .local/packages/<commit 12>):
#   infra-publish/<project>/  the .NET publishes, only when the infrastructure is built
#   app-stage/app/...          the staged app tree
#   system/install.zip, system/install-manifest.json   the Setup's embedded package
#   app/yahad-windows-app-<v>.zip                      the app-only asset
#   setup/Yahad.Setup.exe, setup/LICENSE               the distributable Setup
#   release/ (with -ReleaseKey only): Yahad.Setup-<v>.exe, yahad-windows-app-<v>.zip
#     and the unsigned yahad-windows-release-<v>.json (installer/windows/release_manifest.py)
#   package-build.json, *.log
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$NodeDirectory,
    [Parameter(Mandatory=$true)][string]$NpmCache,
    [Parameter(Mandatory=$true)][string]$VendorDirectory,
    [string]$DotNet = 'dotnet',
    [string]$Python = 'python',
    [string]$PackageCache,
    # A fully qualified new directory; the default is .local/packages/<commit>.
    [string]$Output,
    # The DER public key this build trusts for upgrades. Never a private key.
    # Without one the Setup installs but refuses every upgrade, and no release/ is written.
    [string]$ReleaseKey,
    # Infrastructure builds by version; the default is .local/infra-cache.
    [string]$InfraCache,
    # The checkout the infrastructure is built and the Setup published from; the
    # default is this checkout.
    [string]$InfraSource,
    [switch]$CheckOnly
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Push-Location -LiteralPath $repo
$names = @('DOTNET_ROOT','DOTNET_CLI_HOME','NUGET_PACKAGES','DOTNET_CLI_TELEMETRY_OPTOUT','DOTNET_NOLOGO','PYTHONDONTWRITEBYTECODE')
$previous = @{}
foreach ($name in $names) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    if ($PSVersionTable.PSVersion.Major -lt 7 -or !$IsWindows) { throw 'Windows x64 and PowerShell 7 required' }
    $env:PYTHONDONTWRITEBYTECODE = '1'
    # PATH may hold several matches; use the first, as the shell would.
    $dotnetExe = (Get-Command $DotNet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $pythonExe = (Get-Command $Python -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $nodeRoot = (Resolve-Path -LiteralPath $NodeDirectory).Path
    $nodeExe = Join-Path $nodeRoot 'node.exe'
    $npmCli = Join-Path $nodeRoot 'node_modules/npm/bin/npm-cli.js'
    $vendorRoot = (Resolve-Path -LiteralPath $VendorDirectory).Path
    $npmRoot = (Resolve-Path -LiteralPath $NpmCache).Path
    $dotnetRoot = Split-Path $dotnetExe -Parent
    foreach ($inputFile in @($npmCli,(Join-Path $dotnetRoot 'LICENSE.txt'),(Join-Path $dotnetRoot 'ThirdPartyNotices.txt'))) {
        if (!(Test-Path -LiteralPath $inputFile -PathType Leaf)) { throw "Missing build input: $inputFile" }
    }
    foreach ($path in @($Output,$InfraCache,$InfraSource)) {
        if ($path -and ![IO.Path]::IsPathFullyQualified($path)) { throw "Fully qualified path required: $path" }
    }
    $infraSourceRoot = if ($InfraSource) { (Resolve-Path -LiteralPath $InfraSource).Path } else { $repo }
    $cacheRoot = if ($InfraCache) { $InfraCache } else { Join-Path $repo '.local/infra-cache' }

    function GitIn([string]$Checkout, [string[]]$Arguments) {
        $result = & git -C $Checkout @Arguments
        if ($LASTEXITCODE -ne 0) { throw "git failed in $Checkout" }
        return $result
    }
    function PythonLine([string[]]$Arguments) {
        # One JSON or text line on success; the refusal text otherwise.
        $result = & $pythonExe @Arguments
        if ($LASTEXITCODE -ne 0) { throw "Refused: python $($Arguments[0..1] -join ' ')" }
        return ($result | Select-Object -Last 1)
    }
    $commit = (GitIn $repo @('rev-parse','HEAD')).Trim()
    if ($commit -notmatch '^[a-f0-9]{40}$') { throw 'Committed Git checkout required' }
    $infraHead = (GitIn $infraSourceRoot @('rev-parse','HEAD')).Trim()
    function RequireFrozenSource {
        foreach ($checkout in @(@($repo,$commit),@($infraSourceRoot,$infraHead))) {
            if ((GitIn $checkout[0] @('rev-parse','HEAD')).Trim() -ne $checkout[1]) { throw "Source commit changed: $($checkout[0])" }
            if (GitIn $checkout[0] @('status','--porcelain')) { throw "Commit source changes before packaging: $($checkout[0])" }
        }
    }
    RequireFrozenSource
    # The build tool is the pinned Node the infrastructure ships (dependency-pins.json "node").
    PythonLine @('-m','installer.windows.dependencies','verify-node','--directory',$nodeRoot) | Out-Null
    PythonLine @('-m','installer.windows.dependencies','verify-vendors','--directory',$vendorRoot) | Out-Null
    Write-Output 'Verified Node and vendor inputs'
    # The release's infrastructure inputs are the infrastructure source's: the
    # infrastructure this release ships is the one its own commit describes.
    $infraDigest = PythonLine @('installer/windows/infra_inputs.py','digest','--repo',$repo)
    $sourceDigest = PythonLine @('installer/windows/infra_inputs.py','digest','--repo',$infraSourceRoot)
    if ($infraDigest -ne $sourceDigest) {
        throw "INFRA_SOURCE_MISMATCH: this checkout's infrastructure inputs ($infraDigest) differ from -InfraSource ($sourceDigest)"
    }
    $version = (Get-Content package.json -Raw | ConvertFrom-Json).version
    $output = if ($Output) { $Output } else { Join-Path $repo ('.local/packages/' + $commit.Substring(0,12)) }
    $releaseKeyId = $null
    if ($ReleaseKey) {
        $ReleaseKey = (Resolve-Path -LiteralPath $ReleaseKey).Path
        $keyBytes = [IO.File]::ReadAllBytes($ReleaseKey)
        # A P-256 SubjectPublicKeyInfo is always this size; a PEM or private key is not.
        if ($keyBytes.Length -ne 91) { throw 'ReleaseKey must be a DER P-256 public key (key.spki)' }
        $releaseKeyId = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($keyBytes)).ToLowerInvariant().Substring(0,16)
    }
    $keyArgument = if ($releaseKeyId) { $releaseKeyId } else { 'none' }
    $plan = PythonLine @('-m','installer.windows.infra_package','plan','--cache',$cacheRoot,'--source',$infraSourceRoot,'--release-key-id',$keyArgument) | ConvertFrom-Json
    if (Test-Path -LiteralPath $output) { throw 'Fresh package output required' }
    if ($CheckOnly) { Write-Output "INPUTS_OK source=$commit infraSource=$infraHead infra=$($plan.action):$($plan.infrastructureVersion) output=$output"; return }
    New-Item -ItemType Directory -Path $output | Out-Null
    $env:DOTNET_ROOT = $dotnetRoot
    $env:DOTNET_CLI_HOME = Join-Path $output 'dotnet-home'
    $env:NUGET_PACKAGES = if ($PackageCache) { [IO.Path]::GetFullPath($PackageCache) } else { Join-Path $PSScriptRoot '.scratch/nuget' }
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
    function RunStep([string]$Name, [string]$Program, [string[]]$Arguments) {
        RequireFrozenSource
        Write-Host "BUILDING $Name"
        $log = Join-Path $output ($Name + '.log')
        & $Program @Arguments *> $log
        if ($LASTEXITCODE -ne 0) {
            Get-Content -LiteralPath $log -Tail 20 | Write-Host
            throw "Build failed: $Name (see $output)"
        }
        Write-Host "BUILT $Name"
        return (Get-Content -LiteralPath $log | Select-Object -Last 1)
    }
    $restore = '-p:RestoreConfigFile=' + (Join-Path $infraSourceRoot 'installer/NuGet.Config')
    $common = @('-c','Release','-r','win-x64','--self-contained','true','-p:RuntimeFrameworkVersion=10.0.14','-p:UseSharedCompilation=false','-p:DebugType=None','-p:DebugSymbols=false',$restore,'-m:1')
    # Every publish carries it, so no step can reuse a keyless Install.Core.
    if ($ReleaseKey) { $common += "-p:YahadReleaseKey=$ReleaseKey" }
    $singleFile = @('-p:PublishSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true')
    $setupProject = Join-Path $infraSourceRoot 'installer/Yahad.Setup/Yahad.Setup.csproj'
    if ($plan.action -eq 'build') {
        $published = Join-Path $output 'infra-publish'
        foreach ($project in @('Yahad.Platform','Yahad.ServiceHost','Yahad.Updater')) {
            RunStep $project $dotnetExe (@('publish',(Join-Path $infraSourceRoot "installer/$project/$project.csproj")) + $common + @('-o',(Join-Path $published $project))) | Out-Null
        }
        # The payload-free engine (plan 4.4), installed as infra\<version>\setup\.
        RunStep 'engine-publish' $dotnetExe (@('publish',$setupProject) + $common + $singleFile + @('-o',(Join-Path $published 'engine'))) | Out-Null
        RunStep 'infra-build' $pythonExe @('-m','installer.windows.infra_package','build','--cache',$cacheRoot,'--source',$infraSourceRoot,'--release-key-id',$keyArgument,
            '--host',(Join-Path $published 'Yahad.ServiceHost'),'--updater',(Join-Path $published 'Yahad.Updater'),'--platform',(Join-Path $published 'Yahad.Platform'),
            '--engine',(Join-Path $published 'engine/Yahad.Setup.exe'),'--node',$nodeRoot,'--dotnet',$dotnetRoot,'--vendors',$vendorRoot) | Out-Null
    }
    $entryPath = Join-Path $cacheRoot $plan.infrastructureVersion
    # Reused or new, the entry is audited by bytes before anything embeds it.
    $infra = PythonLine @('-m','installer.windows.infra_package','verify','--entry',$entryPath) | ConvertFrom-Json
    if ($infra.sourceCommit -ne $infraHead -or $infra.inputDigest -ne $infraDigest -or $infra.releaseKeyId -ne $releaseKeyId) { throw 'Cached infrastructure identity mismatch' }
    # Build assets from this checkout, never from a previous candidate's dist/.
    RunStep 'web' $nodeExe @($npmCli,'run','build') | Out-Null
    $stage = Join-Path $output 'app-stage'
    RunStep 'app-stage' $pythonExe @('-m','installer.windows.prepare_core_fixture','--repo',$repo,'--output',$stage,'--node',$nodeRoot,'--cache',$npmRoot) | Out-Null
    $system = Join-Path $output 'system'
    $appDirectory = Join-Path $output 'app'
    New-Item -ItemType Directory -Path $appDirectory | Out-Null
    $appArchive = Join-Path $appDirectory "yahad-windows-app-$version.zip"
    $package = RunStep 'system-package' $pythonExe @('-m','installer.windows.setup_package','--repo',$repo,'--app-stage',$stage,'--infra-entry',$entryPath,'--output',$system,'--app-archive',$appArchive) | ConvertFrom-Json
    $setup = Join-Path $output 'setup'
    RunStep 'setup-publish' $dotnetExe (@('publish',$setupProject) + $common + $singleFile + @("-p:SetupPayloadDirectory=$system",'-o',$setup)) | Out-Null
    Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination (Join-Path $setup 'LICENSE')
    # Audit mode only: no install/uninstall/UAC or service operations. Setup's
    # manifest requires administrator rights; RunAsInvoker starts this read-only
    # audit with the build step's own token, elevated or not, and never prompts.
    $executable = Join-Path $setup 'Yahad.Setup.exe'
    $start = [Diagnostics.ProcessStartInfo]::new($executable)
    $start.UseShellExecute = $false
    $start.Environment['__COMPAT_LAYER'] = 'RunAsInvoker'
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.ArgumentList.Add('--verify-package')
    $audit = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $audit.StandardOutput.ReadToEndAsync()
        $stderr = $audit.StandardError.ReadToEndAsync()
        if (!$audit.WaitForExit(60000)) { throw "Read-only package audit still active; PID=$($audit.Id). Do not launch a duplicate." }
        $observed = $stdout.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $output 'verify-package.log'), $observed, [Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText((Join-Path $output 'verify-package.stderr.log'), $stderr.GetAwaiter().GetResult(), [Text.UTF8Encoding]::new($false))
        if ($audit.ExitCode -ne 0) { throw 'Embedded package verification failed' }
    } finally { $audit.Dispose() }
    $verified = $observed | ConvertFrom-Json
    $manifest = Get-Content -LiteralPath (Join-Path $system 'install-manifest.json') -Raw | ConvertFrom-Json
    # Each half against its own identity: the infrastructure, the Setup and its
    # Install.Core against the cached build; the app against this release.
    if ($verified.infrastructure.version -ne $infra.infrastructureVersion -or $manifest.Infrastructure.Version -ne $infra.infrastructureVersion -or
        $verified.infrastructure.sourceCommit -ne $infra.sourceCommit -or $verified.setupSourceCommit -ne $infra.sourceCommit -or
        $verified.coreSourceCommit -ne $infra.sourceCommit -or $verified.infrastructure.treeSha256 -ne $infra.treeSha256 -or
        $manifest.Infrastructure.TreeSha256 -ne $infra.treeSha256) { throw 'Embedded infrastructure identity mismatch' }
    if ($verified.app.version -ne $version -or $manifest.App.Version -ne $version -or $verified.app.sourceCommit -ne $commit -or
        $verified.app.treeSha256 -ne $package.appTreeSha256 -or $manifest.App.TreeSha256 -ne $package.appTreeSha256) { throw 'Embedded app identity mismatch' }
    if ($verified.embeddedZip -ne $manifest.ZipSha256 -or $verified.files -ne $manifest.Files.Count -or $verified.systemOperationsInvoked -ne $false) { throw 'Embedded package identity mismatch' }
    if ($verified.windowingLoaded -ne $false) { throw 'An unattended Setup mode loaded WPF' }
    if ($verified.releaseKeyId -ne $releaseKeyId) { throw 'Embedded release key mismatch' }
    $releaseManifest = $null
    if ($releaseKeyId) {
        # The unsigned release. Signing is the maintainer's own local step.
        $release = Join-Path $output 'release'
        New-Item -ItemType Directory -Path $release | Out-Null
        Copy-Item -LiteralPath $executable -Destination (Join-Path $release "Yahad.Setup-$version.exe")
        Copy-Item -LiteralPath $appArchive -Destination (Join-Path $release "yahad-windows-app-$version.zip")
        $releaseManifest = Join-Path $release "yahad-windows-release-$version.json"
        $createdUnix = (GitIn $repo @('show','-s','--format=%ct',$commit)).Trim()
        PythonLine @('-m','installer.windows.release_manifest','--install-manifest',(Join-Path $system 'install-manifest.json'),
            '--setup',(Join-Path $release "Yahad.Setup-$version.exe"),'--app-archive',(Join-Path $release "yahad-windows-app-$version.zip"),
            '--key-id',$releaseKeyId,'--created-unix',$createdUnix,'--output',$releaseManifest) | Out-Null
        Write-Output "RELEASE_UNSIGNED $releaseManifest"
        Write-Output "SIGN_WITH node scripts/windows-release-key.mjs sign <key.pem> `"$releaseManifest`""
    } else {
        Write-Warning 'Built without -ReleaseKey: this Setup installs but refuses every upgrade.'
    }
    RequireFrozenSource
    $record = [ordered]@{sourceCommit=$commit;sourceTree=(GitIn $repo @('rev-parse','HEAD^{tree}')).Trim();version=$version;
        infrastructureVersion=$infra.infrastructureVersion;infraSourceCommit=$infra.sourceCommit;infraInputDigest=$infra.inputDigest;
        infraTreeSha256=$infra.treeSha256;infraReused=($plan.action -eq 'reuse');appTreeSha256=$package.appTreeSha256;
        appArchive=$appArchive;releaseManifest=$releaseManifest;
        setupSha256=(Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant();verification=$verified;installed=$false;published=$false}
    [IO.File]::WriteAllText((Join-Path $output 'package-build.json'), (($record | ConvertTo-Json -Depth 8) -replace "`r`n", "`n") + "`n", [Text.UTF8Encoding]::new($false))
    Write-Output "PACKAGE_BUILT $executable"
} finally {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
    Pop-Location
}
