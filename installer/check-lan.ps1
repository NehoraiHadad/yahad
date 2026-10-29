param([Parameter(Mandatory=$true)][string]$DotNet)
$ErrorActionPreference='Stop'
$taskEnvironmentNames=@('DOTNET_ROOT','DOTNET_CLI_HOME','NUGET_PACKAGES','APPDATA','LOCALAPPDATA','TEMP','TMP','DOTNET_GENERATE_ASPNET_CERTIFICATE','DOTNET_SKIP_FIRST_TIME_EXPERIENCE','DOTNET_CLI_TELEMETRY_OPTOUT','DOTNET_ADD_GLOBAL_TOOLS_TO_PATH','DOTNET_NOLOGO','DOTNET_EnableDiagnostics','YAHAD_LAN_UI_OUTPUT')
$previousTaskEnvironment=@{}
foreach($name in $taskEnvironmentNames){$previousTaskEnvironment[$name]=[Environment]::GetEnvironmentVariable($name,'Process')}
try {
$taskScratch=Join-Path $PSScriptRoot '.scratch/lan-final'
$env:DOTNET_ROOT=Split-Path ([IO.Path]::GetFullPath($DotNet))
$env:DOTNET_CLI_HOME=Join-Path $taskScratch 'home'
$env:NUGET_PACKAGES=Join-Path $taskScratch 'nuget'
$env:APPDATA=Join-Path $taskScratch 'appdata'
$env:LOCALAPPDATA=Join-Path $taskScratch 'localappdata'
$env:TEMP=Join-Path $taskScratch 'temp'
$env:TMP=$env:TEMP
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH='false'
$env:DOTNET_NOLOGO='1'
$env:DOTNET_EnableDiagnostics='0'
foreach($folder in @($env:DOTNET_CLI_HOME,$env:NUGET_PACKAGES,$env:APPDATA,$env:LOCALAPPDATA,$env:TEMP)){New-Item -ItemType Directory -Path $folder -Force | Out-Null}
$offlineConfig=Join-Path $taskScratch 'NuGet.Config'
[IO.File]::WriteAllText($offlineConfig,'<configuration><packageSources><clear/></packageSources></configuration>')
foreach($project in @('Yahad.Install.Tests','Yahad.FirstInstall.Tests','Yahad.Production.Tests','Yahad.Recovery.Tests')){
 & $DotNet build (Join-Path $PSScriptRoot "$project/$project.csproj") --disable-build-servers -m:1 -p:UseSharedCompilation=false "-p:RestoreConfigFile=$offlineConfig" --nologo
 if($LASTEXITCODE -ne 0){throw "Build failed: $project"}
}
& $DotNet (Join-Path $PSScriptRoot 'Yahad.Install.Tests/bin/Debug/net10.0-windows/Yahad.Install.Tests.dll') --lan-only
if($LASTEXITCODE -ne 0){throw 'LAN contract tests failed'}
& $DotNet (Join-Path $PSScriptRoot 'Yahad.FirstInstall.Tests/bin/Debug/net10.0-windows/Yahad.FirstInstall.Tests.dll') --lan-runtime-only
if($LASTEXITCODE -ne 0){throw 'LAN runtime tests failed'}
$env:YAHAD_LAN_UI_OUTPUT=Join-Path $taskScratch 'ui'
& $DotNet (Join-Path $PSScriptRoot 'Yahad.FirstInstall.Tests/bin/Debug/net10.0-windows/Yahad.FirstInstall.Tests.dll') --lan-integration-only
if($LASTEXITCODE -ne 0){throw 'LAN integration and synthetic UI tests failed'}
$sourceRepo=Split-Path $PSScriptRoot
foreach($project in @('Yahad.FirstInstall.Tests','Yahad.Production.Tests')){
 & $DotNet (Join-Path $PSScriptRoot "$project/bin/Debug/net10.0-windows/$project.dll") --store-schema-only $sourceRepo
 if($LASTEXITCODE -ne 0){throw "Source schema parser tests failed: $project"}
}
$schemaFixture=Join-Path $taskScratch 'schema-package'
New-Item -ItemType Directory -Path (Join-Path $schemaFixture 'legal') -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $schemaFixture 'legal/build-inputs.json'),'{"databaseSchema":6}')
& $DotNet (Join-Path $PSScriptRoot 'Yahad.Recovery.Tests/bin/Debug/net10.0-windows/Yahad.Recovery.Tests.dll') --store-schema-only $schemaFixture
if($LASTEXITCODE -ne 0){throw 'Recovery schema parser tests failed'}
} finally {
 foreach($name in $taskEnvironmentNames){[Environment]::SetEnvironmentVariable($name,$previousTaskEnvironment[$name],'Process')}
}
