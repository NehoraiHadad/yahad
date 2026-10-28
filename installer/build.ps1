param([string]$DotNet = 'dotnet', [switch]$Publish)
$ErrorActionPreference = 'Stop'
$installerRoot = $PSScriptRoot
$buildEnvironmentNames = @('DOTNET_CLI_HOME', 'NUGET_PACKAGES', 'APPDATA', 'DOTNET_CLI_TELEMETRY_OPTOUT', 'DOTNET_NOLOGO', 'DOTNET_ADD_GLOBAL_TOOLS_TO_PATH', 'DOTNET_ROOT', 'TEMP', 'TMP')
$previousBuildEnvironment = @{}
foreach ($name in $buildEnvironmentNames) { $previousBuildEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
$env:DOTNET_CLI_HOME = Join-Path $installerRoot '.scratch/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $installerRoot '.scratch/nuget'
# NuGet also probes per-user configuration even with RestoreConfigFile. Keep
# build settings/cache private to this process; do not read the owner's config.
$env:APPDATA = Join-Path $installerRoot '.scratch/appdata'
New-Item -ItemType Directory $env:APPDATA -Force | Out-Null
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$env:DOTNET_ROOT = Split-Path (Get-Command $DotNet).Source
$env:TEMP = Join-Path $installerRoot '.scratch/temp'
$env:TMP = $env:TEMP
New-Item -ItemType Directory $env:TEMP -Force | Out-Null
$restoreConfig = Join-Path $installerRoot 'NuGet.Config'
foreach ($project in @('Yahad.Install.Tests', 'Yahad.Host.Slice', 'Yahad.Setup', 'Yahad.Platform')) {
    & $DotNet build (Join-Path $installerRoot "$project/$project.csproj") -c Release --nologo "-p:RestoreConfigFile=$restoreConfig"
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $project" }
}
New-Item -ItemType Directory (Join-Path $installerRoot 'artifacts') -Force | Out-Null
& (Join-Path $installerRoot 'Yahad.Install.Tests/bin/Release/net10.0-windows/Yahad.Install.Tests.exe') --report (Join-Path $installerRoot 'artifacts/test-results.json')
if ($LASTEXITCODE -ne 0) { throw 'Fixture tests failed' }
if ($Publish) {
    foreach ($project in @('Yahad.Setup', 'Yahad.Host.Slice', 'Yahad.Platform')) {
        & $DotNet publish (Join-Path $installerRoot "$project/$project.csproj") -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o (Join-Path $installerRoot "artifacts/$project") --nologo "-p:RestoreConfigFile=$restoreConfig"
        if ($LASTEXITCODE -ne 0) { throw "Publish failed: $project" }
    }
    # Build-owned app-private helper for local synthetic app tests. Never a live
    # service location and never installed into a global runtime directory.
    $nativeFolder = Join-Path (Split-Path $installerRoot) 'native'
    New-Item -ItemType Directory $nativeFolder -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $installerRoot 'artifacts/Yahad.Platform/Yahad.Platform.exe') -Destination (Join-Path $nativeFolder 'Yahad.Platform.exe')
}
} finally {
    foreach ($name in $buildEnvironmentNames) { [Environment]::SetEnvironmentVariable($name, $previousBuildEnvironment[$name], 'Process') }
}
