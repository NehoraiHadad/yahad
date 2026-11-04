# Real Store and data-lease handoff behind the real launcher, without a service.
# Needs npm dependencies and the helper from build.ps1 -Publish.
param(
    [Parameter(Mandatory=$true)][string]$DotNet,
    [Parameter(Mandatory=$true)][string]$Node,
    [Parameter(Mandatory=$true)][string]$PackageCache,
    [string]$Helper = (Join-Path $PSScriptRoot 'artifacts/Yahad.Platform.Bundle')
)
$ErrorActionPreference='Stop'
$names=@('DOTNET_ROOT','DOTNET_CLI_HOME','NUGET_PACKAGES','APPDATA','LOCALAPPDATA','TEMP','TMP','DOTNET_CLI_TELEMETRY_OPTOUT','DOTNET_NOLOGO','DOTNET_ADD_GLOBAL_TOOLS_TO_PATH','DOTNET_EnableDiagnostics','PATH')
$previous=@{};foreach($name in $names){$previous[$name]=[Environment]::GetEnvironmentVariable($name,'Process')}
try {
    $scratch=Join-Path $PSScriptRoot '.scratch/store'
    foreach($name in @('home','appdata','temp')){New-Item -ItemType Directory -Path (Join-Path $scratch $name) -Force | Out-Null}
    $env:DOTNET_ROOT=Split-Path ([IO.Path]::GetFullPath($DotNet));$env:DOTNET_CLI_HOME=Join-Path $scratch 'home'
    $env:NUGET_PACKAGES=[IO.Path]::GetFullPath($PackageCache);$env:APPDATA=Join-Path $scratch 'appdata';$env:LOCALAPPDATA=$env:APPDATA
    $env:TEMP=Join-Path $scratch 'temp';$env:TMP=$env:TEMP
    $env:DOTNET_CLI_TELEMETRY_OPTOUT='1';$env:DOTNET_NOLOGO='1';$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH='false';$env:DOTNET_EnableDiagnostics='0'
    $env:PATH=(Split-Path ([IO.Path]::GetFullPath($Node)))+';'+(Join-Path $env:SystemRoot 'System32')
    & $DotNet build (Join-Path $PSScriptRoot 'Yahad.FirstInstall.Tests/Yahad.FirstInstall.Tests.csproj') -c Release --nologo ('-p:RestoreConfigFile='+(Join-Path $PSScriptRoot 'NuGet.Config'))
    if($LASTEXITCODE -ne 0){throw 'Store test compilation failed'}
    & (Join-Path $PSScriptRoot 'Yahad.FirstInstall.Tests/bin/Release/net10.0-windows/Yahad.FirstInstall.Tests.exe') --store-integration-only ([IO.Path]::GetFullPath($Node)) (Split-Path $PSScriptRoot) ([IO.Path]::GetFullPath($Helper)) (Join-Path $PSScriptRoot 'artifacts/store-tests')
    if($LASTEXITCODE -ne 0){throw 'Store tests failed'}
} finally {foreach($name in $names){[Environment]::SetEnvironmentVariable($name,$previous[$name],'Process')}}
