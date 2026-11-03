# Builds and runs one existing synthetic C# suite for CI attribution. No suite
# here installs, starts services or changes machine folders. The full console
# output and a small summary (pass/fail counts and seconds) land in -Output.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidateSet('Install','FirstInstall','ServiceHost')][string]$Suite,
    [Parameter(Mandatory=$true)][string]$DotNet,
    [Parameter(Mandatory=$true)][string]$Node,
    [Parameter(Mandatory=$true)][string]$PackageCache,
    [Parameter(Mandatory=$true)][string]$Output
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $Output | Out-Null
$out = (Resolve-Path -LiteralPath $Output).Path
$dotnetExe = [IO.Path]::GetFullPath($DotNet)
$nodeExe = [IO.Path]::GetFullPath($Node)
$project = @{ Install='Yahad.Install.Tests'; FirstInstall='Yahad.FirstInstall.Tests'; ServiceHost='Yahad.ServiceHost.Tests' }[$Suite]
$report = Join-Path $out "$project.report.json"
$arguments = @{ Install=@('--report', $report); FirstInstall=@('--full-installer-only'); ServiceHost=@('--node', $nodeExe, '--report', $report) }[$Suite]
$log = Join-Path $out "$project.log"
$scratch = Join-Path $PSScriptRoot ".scratch/ci-$Suite"
foreach ($name in 'home', 'temp') { New-Item -ItemType Directory -Force -Path (Join-Path $scratch $name) | Out-Null }
$env:DOTNET_ROOT = Split-Path $dotnetExe
$env:DOTNET_CLI_HOME = Join-Path $scratch 'home'
$env:NUGET_PACKAGES = [IO.Path]::GetFullPath($PackageCache)
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'; $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$env:TEMP = Join-Path $scratch 'temp'; $env:TMP = $env:TEMP
$timer = [Diagnostics.Stopwatch]::StartNew()
$state = 'Failed'; $passed = $null; $failed = $null; $buildSeconds = $null; $exit = $null
try {
    & $dotnetExe build (Join-Path $PSScriptRoot "$project/$project.csproj") -c Release --nologo ('-p:RestoreConfigFile=' + (Join-Path $PSScriptRoot 'NuGet.Config')) *> (Join-Path $out "$project.build.log")
    if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath (Join-Path $out "$project.build.log") -Tail 30; throw "$project build failed" }
    $buildSeconds = [math]::Round($timer.Elapsed.TotalSeconds, 1)
    $exe = Join-Path $PSScriptRoot "$project/bin/Release/net10.0-windows/$project.exe"
    & $exe @arguments 2>&1 | Tee-Object -FilePath $log | Where-Object { $_ -like 'FAIL *' -or $_ -match '^\d+ passed; \d+ failed' -or $_ -like '{*' }
    $exit = $LASTEXITCODE
    $lines = Get-Content -LiteralPath $log
    $passed = @($lines | Where-Object { $_ -like 'PASS *' }).Count
    $failed = @($lines | Where-Object { $_ -like 'FAIL *' }).Count
    if ($exit -eq 0 -and $failed -eq 0) { $state = 'Passed' }
    if ($state -ne 'Passed') { throw "$project reported $failed failing test(s), exit $exit" }
}
finally {
    [ordered]@{ Suite=$project; Arguments=($arguments | ForEach-Object { if ($_ -eq $nodeExe) { 'node.exe' } elseif ($_ -eq $report) { 'report.json' } else { $_ } }); State=$state; Passed=$passed; Failed=$failed; ExitCode=$exit; BuildSeconds=$buildSeconds; TotalSeconds=[math]::Round($timer.Elapsed.TotalSeconds, 1) } |
        ConvertTo-Json | Set-Content -Encoding utf8NoBOM (Join-Path $out "$project.summary.json")
    Write-Output ("SUITE {0} {1} passed={2} failed={3} {4}s" -f $project, $state, $passed, $failed, [math]::Round($timer.Elapsed.TotalSeconds, 1))
}
