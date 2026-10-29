param(
    [Parameter(Mandatory=$true)][string]$DotNet,
    [Parameter(Mandatory=$true)][string]$Node,
    [Parameter(Mandatory=$true)][string]$PackageCache,
    [string]$PreparedPayload
)
$ErrorActionPreference='Stop'
$names=@('DOTNET_ROOT','DOTNET_CLI_HOME','NUGET_PACKAGES','APPDATA','LOCALAPPDATA','TEMP','TMP','DOTNET_CLI_TELEMETRY_OPTOUT','DOTNET_NOLOGO','DOTNET_ADD_GLOBAL_TOOLS_TO_PATH','DOTNET_EnableDiagnostics','PATH')
$previous=@{};foreach($name in $names){$previous[$name]=[Environment]::GetEnvironmentVariable($name,'Process')}
try {
    $scratch=Join-Path $PSScriptRoot '.scratch/production'
    foreach($name in @('home','appdata','temp')){New-Item -ItemType Directory -Path (Join-Path $scratch $name) -Force | Out-Null}
    $env:DOTNET_ROOT=Split-Path ([IO.Path]::GetFullPath($DotNet));$env:DOTNET_CLI_HOME=Join-Path $scratch 'home'
    $env:NUGET_PACKAGES=[IO.Path]::GetFullPath($PackageCache);$env:APPDATA=Join-Path $scratch 'appdata';$env:LOCALAPPDATA=$env:APPDATA
    $env:TEMP=Join-Path $scratch 'temp';$env:TMP=$env:TEMP
    $env:DOTNET_CLI_TELEMETRY_OPTOUT='1';$env:DOTNET_NOLOGO='1';$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH='false';$env:DOTNET_EnableDiagnostics='0'
    $env:PATH=(Split-Path ([IO.Path]::GetFullPath($Node)))+';'+(Join-Path $env:SystemRoot 'System32')
    $restore='-p:RestoreConfigFile='+(Join-Path $PSScriptRoot 'NuGet.Config')
    & $DotNet build (Join-Path $PSScriptRoot 'Yahad.Production.Tests/Yahad.Production.Tests.csproj') -c Release --nologo $restore
    if($LASTEXITCODE -ne 0){throw 'Production contract compilation failed'}
    New-Item -ItemType Directory -Path (Join-Path $PSScriptRoot 'artifacts') -Force | Out-Null
    $arguments=@('--node',[IO.Path]::GetFullPath($Node),'--repo',[IO.Path]::GetFullPath((Split-Path $PSScriptRoot)),'--helper',(Join-Path $PSScriptRoot 'artifacts/Yahad.Platform.Bundle'),'--report',(Join-Path $PSScriptRoot 'artifacts/production-tests.json'))
    if($PreparedPayload){$arguments+=@('--prepared',[IO.Path]::GetFullPath($PreparedPayload))}
    & (Join-Path $PSScriptRoot 'Yahad.Production.Tests/bin/Release/net10.0-windows/Yahad.Production.Tests.exe') @arguments
    if($LASTEXITCODE -ne 0){throw 'Production contracts failed'}

} finally {foreach($name in $names){[Environment]::SetEnvironmentVariable($name,$previous[$name],'Process')}}
