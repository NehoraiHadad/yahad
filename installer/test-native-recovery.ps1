param(
    [Parameter(Mandatory=$true)][string]$DotNet,
    [Parameter(Mandatory=$true)][string]$Node,
    [Parameter(Mandatory=$true)][string]$PackageCache,
    [string]$PreparedApp
)
$ErrorActionPreference='Stop'
$names=@('DOTNET_ROOT','DOTNET_CLI_HOME','NUGET_PACKAGES','APPDATA','LOCALAPPDATA','TEMP','TMP','DOTNET_CLI_TELEMETRY_OPTOUT','DOTNET_NOLOGO','DOTNET_ADD_GLOBAL_TOOLS_TO_PATH','PATH')
$previous=@{}
foreach($name in $names){$previous[$name]=[Environment]::GetEnvironmentVariable($name,'Process')}
try {
    $scratch=Join-Path $PSScriptRoot '.scratch/recovery'
    foreach($name in @('home','appdata','temp')){New-Item -ItemType Directory -Path (Join-Path $scratch $name) -Force | Out-Null}
    $env:DOTNET_ROOT=Split-Path ([IO.Path]::GetFullPath($DotNet))
    $env:DOTNET_CLI_HOME=Join-Path $scratch 'home'
    $env:NUGET_PACKAGES=[IO.Path]::GetFullPath($PackageCache)
    $env:APPDATA=Join-Path $scratch 'appdata'
    $env:LOCALAPPDATA=$env:APPDATA
    $env:TEMP=Join-Path $scratch 'temp';$env:TMP=$env:TEMP
    $env:DOTNET_CLI_TELEMETRY_OPTOUT='1';$env:DOTNET_NOLOGO='1';$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH='false'
    $env:PATH=(Split-Path ([IO.Path]::GetFullPath($Node)))+';'+(Join-Path $env:SystemRoot 'System32')
    $project=Join-Path $PSScriptRoot 'Yahad.Recovery.Tests/Yahad.Recovery.Tests.csproj'
    & $DotNet build $project -c Release --nologo ('-p:RestoreConfigFile='+ (Join-Path $PSScriptRoot 'NuGet.Config'))
    if($LASTEXITCODE -ne 0){throw 'Recovery test compilation failed'}
    New-Item -ItemType Directory -Path (Join-Path $PSScriptRoot 'artifacts') -Force | Out-Null
    $recoveryArguments=@('--run',[IO.Path]::GetFullPath($Node),(Join-Path $PSScriptRoot 'artifacts/native-recovery-tests.json'))
    if($PreparedApp){$recoveryArguments+=[IO.Path]::GetFullPath($PreparedApp)}
    & (Join-Path $PSScriptRoot 'Yahad.Recovery.Tests/bin/Release/net10.0-windows/Yahad.Recovery.Tests.exe') @recoveryArguments
    if($LASTEXITCODE -ne 0){throw 'Recovery fixture tests failed'}
} finally {
    foreach($name in $names){[Environment]::SetEnvironmentVariable($name,$previous[$name],'Process')}
}
