param(
    [Parameter(Mandatory=$true)][string]$DotNet,
    [Parameter(Mandatory=$true)][string]$Node,
    [Parameter(Mandatory=$true)][string]$PackageCache,
    [switch]$Publish
)
$ErrorActionPreference='Stop'
$names=@('DOTNET_ROOT','DOTNET_CLI_HOME','NUGET_PACKAGES','APPDATA','LOCALAPPDATA','TEMP','TMP','DOTNET_CLI_TELEMETRY_OPTOUT','DOTNET_NOLOGO','DOTNET_ADD_GLOBAL_TOOLS_TO_PATH','DOTNET_EnableDiagnostics','PATH')
$previous=@{};foreach($name in $names){$previous[$name]=[Environment]::GetEnvironmentVariable($name,'Process')}
try {
    $scratch=Join-Path $PSScriptRoot '.scratch/service-host'
    foreach($name in @('home','appdata','temp')){New-Item -ItemType Directory -Path (Join-Path $scratch $name) -Force | Out-Null}
    $env:DOTNET_ROOT=Split-Path ([IO.Path]::GetFullPath($DotNet));$env:DOTNET_CLI_HOME=Join-Path $scratch 'home'
    $env:NUGET_PACKAGES=[IO.Path]::GetFullPath($PackageCache);$env:APPDATA=Join-Path $scratch 'appdata';$env:LOCALAPPDATA=$env:APPDATA
    $env:TEMP=Join-Path $scratch 'temp';$env:TMP=$env:TEMP
    $env:DOTNET_CLI_TELEMETRY_OPTOUT='1';$env:DOTNET_NOLOGO='1';$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH='false';$env:DOTNET_EnableDiagnostics='0'
    $env:PATH=(Split-Path ([IO.Path]::GetFullPath($Node)))+';'+(Join-Path $env:SystemRoot 'System32')
    $restore='-p:RestoreConfigFile='+(Join-Path $PSScriptRoot 'NuGet.Config')
    & $DotNet build (Join-Path $PSScriptRoot 'Yahad.ServiceHost.Tests/Yahad.ServiceHost.Tests.csproj') -c Release --nologo $restore
    if($LASTEXITCODE -ne 0){throw 'Service host contract compilation failed'}
    New-Item -ItemType Directory -Path (Join-Path $PSScriptRoot 'artifacts') -Force | Out-Null
    & (Join-Path $PSScriptRoot 'Yahad.ServiceHost.Tests/bin/Release/net10.0-windows/Yahad.ServiceHost.Tests.exe') --node ([IO.Path]::GetFullPath($Node)) --report (Join-Path $PSScriptRoot 'artifacts/service-host-tests.json')
    if($LASTEXITCODE -ne 0){throw 'Service host contracts failed'}
    if($Publish){
        # Folder publication keeps every CLR/bootstrap file explicit and hashed;
        # the service never needs runtime extraction into an implicit machine TEMP.
        & $DotNet publish (Join-Path $PSScriptRoot 'Yahad.ServiceHost/Yahad.ServiceHost.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=None -o (Join-Path $PSScriptRoot 'artifacts/Yahad.ServiceHost') --nologo $restore
        if($LASTEXITCODE -ne 0){throw 'Self-contained service host publication failed'}
        & (Join-Path $PSScriptRoot 'artifacts/Yahad.ServiceHost/Yahad.ServiceHost.exe') --describe
        if($LASTEXITCODE -ne 0){throw 'Non-SCM artifact description failed'}
    }
} finally {foreach($name in $names){[Environment]::SetEnvironmentVariable($name,$previous[$name],'Process')}}
