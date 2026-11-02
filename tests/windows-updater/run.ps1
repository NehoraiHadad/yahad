param([Parameter(Mandatory=$true)][string]$DotNet,[string]$PackageCache)
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$scratch=Join-Path $workspace '.local/windows-updater-policy'
$names=@('DOTNET_ROOT','DOTNET_CLI_HOME','NUGET_PACKAGES','APPDATA','LOCALAPPDATA','TEMP','TMP','DOTNET_CLI_TELEMETRY_OPTOUT','DOTNET_NOLOGO','DOTNET_ADD_GLOBAL_TOOLS_TO_PATH','DOTNET_GENERATE_ASPNET_CERTIFICATE','DOTNET_SKIP_FIRST_TIME_EXPERIENCE','DOTNET_EnableDiagnostics')
$previous=@{};foreach($name in $names){$previous[$name]=[Environment]::GetEnvironmentVariable($name,'Process')}
try {
    foreach($name in @('home','appdata','temp','nuget')){New-Item -ItemType Directory -Path (Join-Path $scratch $name) -Force | Out-Null}
    $env:DOTNET_ROOT=Split-Path ([IO.Path]::GetFullPath($DotNet))
    $env:DOTNET_CLI_HOME=Join-Path $scratch 'home';$env:APPDATA=Join-Path $scratch 'appdata';$env:LOCALAPPDATA=$env:APPDATA
    $env:NUGET_PACKAGES=if($PackageCache){[IO.Path]::GetFullPath($PackageCache)}else{Join-Path $scratch 'nuget'}
    $env:TEMP=Join-Path $scratch 'temp';$env:TMP=$env:TEMP
    $env:DOTNET_CLI_TELEMETRY_OPTOUT='1';$env:DOTNET_NOLOGO='1';$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH='false'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false';$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1';$env:DOTNET_EnableDiagnostics='0'
    & $DotNet build (Join-Path $PSScriptRoot 'Yahad.Production.Tests.csproj') -c Release --nologo "-p:RestoreConfigFile=$workspace/installer/NuGet.Config"
    if($LASTEXITCODE -ne 0){throw 'Updater policy test compilation failed'}
    & $DotNet (Join-Path $PSScriptRoot 'bin/Release/net10.0-windows/Yahad.Production.Tests.dll')
    if($LASTEXITCODE -ne 0){throw 'Updater policy tests failed'}
} finally {foreach($name in $names){[Environment]::SetEnvironmentVariable($name,$previous[$name],'Process')}}
