# Disposable command mocks only: never registers a real task or starts WSL.
$ErrorActionPreference='Stop'
$global:Calls=[System.Collections.Generic.List[object]]::new()
$global:WslMissing=$false; $global:DistroList='  Ubuntu Running 2'; $global:ProbeMissing=$false; $global:FailCheck=$false; $global:Interrupt=$false; $global:FailList=$false; $global:FailShell=$false; $global:BadPath=$false
function global:Get-Command { param($Name,$ErrorAction) if (-not $global:WslMissing) { [pscustomobject]@{Name='wsl.exe'} } }
$global:Canceled=$false; $global:FailDisabled=$false; $global:FailInstall=$false; $global:FailRequestPath=$false; $global:Existing=$null
function global:wsl.exe {
    $global:Calls.Add(@($args))
    $global:LASTEXITCODE=0
    if ($args -contains '--list') { $global:DistroList; if ($global:FailList) {$global:LASTEXITCODE=1} }
    if ($args -contains '/bin/sh' -and $global:ProbeMissing) { 'MISSING: /usr/bin/python3'; 'MISSING: /usr/bin/sudo' }
    if ($args -contains '/bin/sh' -and $global:FailShell) { $global:LASTEXITCODE=1 }
    if ($args -contains '/usr/bin/wslpath') { if ($global:BadPath) { '/bad;path' } else { '/mnt/c/synthetic/release' } }
    if ($args -contains '--check-only' -and $global:FailCheck) { $global:LASTEXITCODE=1 }
    if ($args -contains '/usr/bin/sudo' -and $global:Interrupt) { $global:LASTEXITCODE=130 }
    if ($args -contains '/usr/bin/sudo' -and $global:Canceled) { $global:LASTEXITCODE=2 }
    if ($args -contains '/usr/bin/sudo' -and $global:FailInstall) { $global:LASTEXITCODE=1 }
    if ($args -contains 'is-enabled' -and $global:FailDisabled) { $global:LASTEXITCODE=1 }
    if ($args -contains 'is-active' -and $args -contains 'yachad-update-request.path' -and $global:FailRequestPath) { $global:LASTEXITCODE=1 }
}
function global:Get-ScheduledTask { param($TaskName,$ErrorAction) $global:Existing }
function global:New-ScheduledTaskAction { param($Execute,$Argument) [pscustomobject]@{Execute=$Execute;Arguments=$Argument} }
function global:New-ScheduledTaskTrigger { param([switch]$AtLogOn,$User) 'synthetic-trigger' }
function global:New-ScheduledTaskSettingsSet { param($ExecutionTimeLimit,[switch]$StartWhenAvailable,[switch]$AllowStartIfOnBatteries,[switch]$DontStopIfGoingOnBatteries) 'synthetic-settings' }
function global:Register-ScheduledTask { param($TaskName,$Action,$Trigger,$Settings,$Description) $global:Calls.Add('REGISTER'); $global:Existing=[pscustomobject]@{Actions=@($Action)} }
function global:Start-ScheduledTask { param($TaskName) $global:Calls.Add('START') }
$script=Join-Path $PSScriptRoot '../deploy/install-windows.ps1'
& $script -Origin https://192.168.1.10 -PlanOnly | Out-Null
if ($global:Calls.Count -ne 0) { throw 'Plan mutated platform' }
& $script -Origin https://192.168.1.10 -StartAtLogin | Out-Null
if (-not $global:Calls.Contains('REGISTER') -or -not $global:Calls.Contains('START')) { throw 'Missing background lifecycle' }
$global:Calls.Clear()
& $script -Origin https://192.168.1.10 -StartAtLogin | Out-Null
if ($global:Calls.Contains('REGISTER')) { throw 'Repeated run replaced task' }
$global:Calls.Clear();$global:FailInstall=$true
try { & $script -Origin https://192.168.1.10 -StartAtLogin | Out-Null; throw 'Expected failure' } catch { if ($_.Exception.Message -eq 'Expected failure') { throw } }
if ($global:Calls.Contains('REGISTER') -or $global:Calls.Contains('START')) { throw 'Failed install activated background task' }
$global:Calls.Clear();$global:FailInstall=$false;$global:Existing=[pscustomobject]@{Actions=@([pscustomobject]@{Execute='unrelated.exe';Arguments=''})}
try { & $script -Origin https://192.168.1.10 -StartAtLogin | Out-Null; throw 'Expected conflict' } catch { if ($_.Exception.Message -eq 'Expected conflict') { throw } }
if ($global:Calls.Contains('REGISTER') -or $global:Calls.Contains('START')) { throw 'Conflicting task overwritten' }
$global:Calls.Clear()
try { & $script -Origin https://192.168.1.10 -Distribution 'bad;command' -StartAtLogin | Out-Null; throw 'Expected invalid input' } catch { if ($_.Exception.Message -eq 'Expected invalid input') { throw } }
if ($global:Calls.Count) { throw 'Invalid distribution reached platform' }
$global:Calls.Clear();$global:Existing=$null;$global:FailRequestPath=$true
try { & $script -Origin https://192.168.1.10 -StartAtLogin | Out-Null; throw 'Expected missing request path failure' } catch { if ($_.Exception.Message -eq 'Expected missing request path failure') { throw } }
if ($global:Calls.Contains('REGISTER') -or $global:Calls.Contains('START')) { throw 'Unverified request path activated background task' }
$global:Calls.Clear();$global:FailRequestPath=$false;$global:FailDisabled=$true
try { & $script -Origin https://192.168.1.10 -StartAtLogin | Out-Null; throw 'Expected disabled unit failure' } catch { if ($_.Exception.Message -eq 'Expected disabled unit failure') { throw } }
if ($global:Calls.Contains('REGISTER') -or $global:Calls.Contains('START')) { throw 'Disabled service preference activated background task' }
$global:Calls.Clear();$global:FailDisabled=$false;$global:Canceled=$true
$result = & $script -Origin https://192.168.1.10 -StartAtLogin
if (($result -join ' ') -notmatch 'CANCELED:') { throw 'Cancel did not explain outcome' }
if ($global:Calls.Contains('REGISTER') -or $global:Calls.Contains('START')) { throw 'Canceled installer activated background task' }
$global:Canceled=$false
function Assert-PreflightStops([string]$Message) {
    $global:Calls.Clear()
    try { & $script -Origin https://192.168.1.10 -StartAtLogin | Out-Null; throw 'Expected preflight failure' }
    catch { if ($_.Exception.Message -notmatch $Message) { throw } }
    if ($global:Calls.Contains('REGISTER') -or $global:Calls.Contains('START')) { throw 'Preflight activated task' }
    foreach ($call in $global:Calls) { if ($call -contains '/usr/bin/sudo') { throw 'Preflight reached installer' } }
}
$global:WslMissing=$true; Assert-PreflightStops 'WSL is missing'; $global:WslMissing=$false
$global:DistroList=' Debian Running 2'; Assert-PreflightStops 'not listed'; $global:DistroList=' Ubuntu Running 1'
Assert-PreflightStops 'uses WSL1'; $global:DistroList='* Ubuntu Stopped 2'
$global:FailList=$true; Assert-PreflightStops 'could not list'; $global:FailList=$false
$global:FailShell=$true; Assert-PreflightStops 'could not start'; $global:FailShell=$false
$global:ProbeMissing=$true; Assert-PreflightStops 'python3.*sudo'; $global:ProbeMissing=$false
$global:BadPath=$true; Assert-PreflightStops 'Cannot read'; $global:BadPath=$false
$global:FailCheck=$true; Assert-PreflightStops 'prerequisite list'; $global:FailCheck=$false
$global:Calls.Clear()
& $script -Origin https://192.168.1.10 -CheckOnly -StartAtLogin | Out-Null
foreach ($call in $global:Calls) { if ($call -contains '/usr/bin/sudo') { throw 'Check-only reached installer' } }
if ($global:Calls.Contains('REGISTER') -or $global:Calls.Contains('START')) { throw 'Check-only activated task' }
$global:Calls.Clear(); $global:Interrupt=$true
try { & $script -Origin https://192.168.1.10 -StartAtLogin | Out-Null; throw 'Expected interruption' }
catch { if ($_.Exception.Message -notmatch 'PAUSED:') { throw } }
if ($global:Calls.Contains('REGISTER') -or $global:Calls.Contains('START')) { throw 'Interrupted install activated task' }
'WINDOWS_ADAPTER_TESTS_OK: 19 cases; WSL/service/task mutations mocked'
