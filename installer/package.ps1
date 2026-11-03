# Build-machine tool. Never launches installation, changes services or publishes.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$NodeDirectory,
    [Parameter(Mandatory=$true)][string]$NpmCache,
    [Parameter(Mandatory=$true)][string]$VendorDirectory,
    [string]$DotNet = 'dotnet',
    [string]$Python = 'python',
    [string]$PackageCache,
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
    # PATH may hold several matches; use the first, as the shell would.
    $dotnetExe = (Get-Command $DotNet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $pythonExe = (Get-Command $Python -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $nodeRoot = (Resolve-Path -LiteralPath $NodeDirectory).Path
    $nodeExe = Join-Path $nodeRoot 'node.exe'
    $npmCli = Join-Path $nodeRoot 'node_modules/npm/bin/npm-cli.js'
    $vendorRoot = (Resolve-Path -LiteralPath $VendorDirectory).Path
    $npmRoot = (Resolve-Path -LiteralPath $NpmCache).Path
    $dotnetRoot = Split-Path $dotnetExe -Parent
    foreach ($inputFile in @($nodeExe,$npmCli,(Join-Path $nodeRoot 'LICENSE'),(Join-Path $dotnetRoot 'LICENSE.txt'),(Join-Path $dotnetRoot 'ThirdPartyNotices.txt'))) {
        if (!(Test-Path -LiteralPath $inputFile -PathType Leaf)) { throw "Missing build input: $inputFile" }
    }
    $nodeVersion = & $nodeExe --version
    if ($LASTEXITCODE -ne 0 -or $nodeVersion -notmatch '^v24\.') { throw 'Node 24 required' }
    $commit = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[a-f0-9]{40}$') { throw 'Committed Git checkout required' }
    function RequireFrozenSource {
        $head = & git rev-parse HEAD
        if ($LASTEXITCODE -ne 0 -or $head.Trim() -ne $commit) { throw 'Source commit changed' }
        $changes = & git status --porcelain
        if ($LASTEXITCODE -ne 0 -or $changes) { throw 'Commit source changes before packaging' }
    }
    RequireFrozenSource
    $env:PYTHONDONTWRITEBYTECODE = '1'
    # Same pinned-input validator used by the existing payload builder.
    & $pythonExe -c 'import sys; from pathlib import Path; from installer.windows import dependencies as d; p=Path(sys.argv[1]); d.validate_staged({x.relative_to(p).as_posix():x for x in p.rglob("*") if x.is_file()}); print("Verified vendor inputs")' $vendorRoot
    if ($LASTEXITCODE -ne 0) { throw 'Pinned vendor validation failed' }
    $version = (Get-Content package.json -Raw | ConvertFrom-Json).version
    $output = Join-Path $repo ('.local/packages/' + $commit.Substring(0,12))
    $platform = Join-Path $PSScriptRoot 'artifacts/Yahad.Platform.Bundle'
    if ((Test-Path -LiteralPath $output) -or (Test-Path -LiteralPath $platform)) {
        throw 'Fresh package output and installer/artifacts/Yahad.Platform.Bundle required; use a clean checkout'
    }
    if ($CheckOnly) { Write-Output "INPUTS_OK source=$commit output=$output"; return }
    New-Item -ItemType Directory -Path $output | Out-Null
    $env:DOTNET_ROOT = $dotnetRoot
    $env:DOTNET_CLI_HOME = Join-Path $output 'dotnet-home'
    $env:NUGET_PACKAGES = if ($PackageCache) { [IO.Path]::GetFullPath($PackageCache) } else { Join-Path $PSScriptRoot '.scratch/nuget' }
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
    $env:PYTHONDONTWRITEBYTECODE = '1'
    function RunStep([string]$Name, [string]$Program, [string[]]$Arguments) {
        RequireFrozenSource
        Write-Output "BUILDING $Name"
        & $Program @Arguments *> (Join-Path $output ($Name + '.log'))
        if ($LASTEXITCODE -ne 0) {
            Get-Content -LiteralPath (Join-Path $output ($Name + '.log')) -Tail 20
            throw "Build failed: $Name (see $output)"
        }
        Write-Output "BUILT $Name"
    }
    # Build assets from this checkout, never from a previous candidate's dist/.
    RunStep 'web' $nodeExe @($npmCli,'run','build')
    $restore = '-p:RestoreConfigFile=' + (Join-Path $PSScriptRoot 'NuGet.Config')
    $common = @('-c','Release','-r','win-x64','--self-contained','true','-p:RuntimeFrameworkVersion=10.0.14','-p:UseSharedCompilation=false','-p:DebugType=None','-p:DebugSymbols=false',$restore,'-m:1')
    foreach ($project in @('Yahad.Platform','Yahad.Authority','Yahad.ServiceHost')) {
        $destination = if ($project -eq 'Yahad.Platform') { $platform } else { Join-Path $output $project }
        RunStep $project $dotnetExe (@('publish',"installer/$project/$project.csproj") + $common + @('-o',$destination))
    }
    $app = Join-Path $output 'app'
    New-Item -ItemType Directory -Path $app | Out-Null
    $appZip = Join-Path $app "yahad-app-native-$version.zip"
    RunStep 'app-payload' $pythonExe @('-m','installer.windows.first_install_package','payload','--repo',$repo,'--core',"$app/core",'--vendors',"$app/vendors",'--full',"$app/full",'--node',$nodeRoot,'--dotnet',$dotnetRoot,'--npm-cache',$npmRoot,'--verified-vendor-source',$vendorRoot,'--archive',$appZip)
    $system = Join-Path $output 'system'
    RunStep 'system-package' $pythonExe @('-m','installer.windows.full_system_package','--repo',$repo,'--payload-zip',$appZip,'--authority',"$output/Yahad.Authority",'--runtime-host',"$output/Yahad.ServiceHost",'--helper',"$output/Yahad.Authority",'--node-root',$nodeRoot,'--output',$system)
    $setup = Join-Path $output 'setup'
    RunStep 'setup-publish' $dotnetExe (@('publish','installer/Yahad.Setup/Yahad.Setup.csproj') + $common + @('-p:PublishSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true',"-p:SetupPayloadDirectory=$system",'-o',$setup))
    Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination (Join-Path $setup 'LICENSE')
    # Audit mode only: no install/uninstall/UAC or service operations.
    $executable = Join-Path $setup 'Yahad.Setup.exe'
    $start = [Diagnostics.ProcessStartInfo]::new($executable)
    $start.UseShellExecute = $false
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
        $observed | Set-Content -LiteralPath (Join-Path $output 'verify-package.log') -Encoding utf8
        $stderr.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $output 'verify-package.stderr.log') -Encoding utf8
        if ($audit.ExitCode -ne 0) { throw 'Embedded package verification failed' }
    } finally { $audit.Dispose() }
    $verified = $observed | ConvertFrom-Json
    $manifest = Get-Content -LiteralPath (Join-Path $system 'install-manifest.json') -Raw | ConvertFrom-Json
    if ($verified.sourceCommit -ne $commit -or $verified.setupSourceCommit -ne $commit -or $verified.coreSourceCommit -ne $commit -or $verified.embeddedZip -ne $manifest.ZipSha256 -or $verified.files -ne $manifest.Files.Count -or $verified.systemOperationsInvoked -ne $false) { throw 'Embedded source/package identity mismatch' }
    RequireFrozenSource
    [ordered]@{sourceCommit=$commit;sourceTree=(& git rev-parse 'HEAD^{tree}').Trim();version=$version;setupSha256=(Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant();verification=$verified;installed=$false;published=$false} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'package-build.json') -Encoding utf8
    Write-Output "PACKAGE_BUILT $executable"
} finally {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
    Pop-Location
}
