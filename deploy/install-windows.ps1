param(
    [string]$Distribution = 'Ubuntu',
    [Parameter(Mandatory=$true)][string]$Origin,
    [switch]$StartAtLogin,
    [switch]$PlanOnly,
    [switch]$CheckOnly
)
# Thin Windows lifecycle adapter: the same Linux app, installer and root worker
# run in WSL2. Never port POSIX security checks into weakened Windows equivalents.
$ErrorActionPreference = 'Stop'
if ($Distribution -notmatch '^[A-Za-z0-9._-]+$') { throw 'Use an installed WSL distribution name.' }
if ($Origin -notmatch '^https://[0-9.]+$') { throw 'Use https://PRIVATE_LAN_IPV4.' }
$source = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$taskName = 'Yachad-' + $Distribution
$commands = @('WSL2 systemd prerequisites', 'Run shared Linux guided installer', 'Verify healthy app, immediate update requests and fallback timers')
if ($StartAtLogin) { $commands += 'Register current-user logon task keeping the selected WSL distro awake' }
if ($PlanOnly) { $commands; return }
# These probes may start the selected distro, but never provision it or elevate.
if (-not (Get-Command wsl.exe -ErrorAction SilentlyContinue)) { throw 'NOT READY: WSL is missing. This is a WSL2 launcher, not a native Windows installer. Follow https://learn.microsoft.com/windows/wsl/install to prepare Ubuntu, then rerun with -CheckOnly. No Windows features or tasks were changed.' }
function Invoke-WslProbe {
    param([string[]]$Arguments)
    # Windows PowerShell may wrap native stderr as errors. Capture it quietly so
    # failed probes still produce our actionable message, not a command cascade.
    $ErrorActionPreference = 'Continue'
    $PSNativeCommandUseErrorActionPreference = $false
    $lines = @(& wsl.exe @Arguments 2>&1)
    [pscustomobject]@{ Code = $LASTEXITCODE; Lines = $lines }
}
$listing = Invoke-WslProbe -Arguments @('--list','--verbose')
if ($listing.Code -ne 0) { throw 'NOT READY: Windows could not list WSL distributions. Open WSL and resolve its startup issue first; see docs/INSTALL.md. No installation was attempted.' }
$distroText = ($listing.Lines -join "`n").Replace([string][char]0, '')
$escapedDistribution = [regex]::Escape($Distribution)
if ($distroText -notmatch ("(?m)^\s*\*?\s*" + $escapedDistribution + "\s+.+?\s+(?<WslVersion>[12])\s*$")) {
    throw "NOT READY: '$Distribution' is not listed as an installed WSL distribution. Check 'wsl --list --verbose' and use its exact name with -Distribution. See docs/INSTALL.md for owner-managed setup; nothing was converted or installed."
}
if ($Matches.WslVersion -ne '2') { throw "NOT READY: '$Distribution' uses WSL1; Yachad requires WSL2. Follow the WSL instructions in docs/INSTALL.md before retrying. No distribution was converted or changed." }
$probe = Invoke-WslProbe -Arguments @('--distribution',$Distribution,'--exec','/bin/sh','-c','for tool in /usr/bin/python3 /usr/bin/sudo /usr/bin/wslpath; do if [ ! -x "$tool" ]; then echo "MISSING: $tool"; fi; done')
if ($probe.Code -ne 0) { throw "NOT READY: '$Distribution' could not start its Linux shell. Open that distribution and resolve its startup problem, then retry -CheckOnly. No installation was attempted." }
$missing = @($probe.Lines | Where-Object { "$_" -match '^MISSING: /usr/bin/(python3|sudo|wslpath)$' })
if ($missing.Count) { throw ("NOT READY: Linux tools are missing in '$Distribution': " + ($missing -join ', ') + '. Prepare Python 3, sudo and WSL utilities inside that distribution, then retry -CheckOnly. Installing them on Windows is not enough. No installation was attempted.') }
$pathOutput = Invoke-WslProbe -Arguments @('--distribution',$Distribution,'--exec','/usr/bin/wslpath','-u',$source)
$linuxPath = ($pathOutput.Lines -join "`n").Trim()
if ($pathOutput.Code -ne 0 -or $linuxPath -notmatch '^/[A-Za-z0-9_./ -]+$') { throw 'NOT READY: Cannot read this extracted release from WSL. Extract the complete release into a simple local folder, then retry -CheckOnly. No installation was attempted.' }
$readiness = Invoke-WslProbe -Arguments @('--distribution',$Distribution,'--exec','/usr/bin/env','PATH=/usr/local/bin:/usr/bin:/bin','/usr/bin/python3',($linuxPath + '/deploy/install.py'),'--check-only','--origin',$Origin)
$readiness.Lines | ForEach-Object { Write-Output "$_" }
if ($readiness.Code -ne 0) { throw 'NOT READY: Resolve the prerequisite list above inside the selected WSL distribution, then retry -CheckOnly. No installer or Windows task was started.' }
if ($CheckOnly) { Write-Output 'CHECK COMPLETE: prerequisites passed; no installation changes made. This is not a network, HTTPS, reboot or existing-install acceptance test.'; return }
# Interactive sudo is handled in the Linux owner terminal. No password is saved.
& wsl.exe --distribution $Distribution --exec /usr/bin/sudo /usr/bin/python3 ($linuxPath + '/deploy/install.py') --origin $Origin
if ($LASTEXITCODE -eq 2) { Write-Output 'CANCELED: installation was not approved. No Windows task was registered or started.'; return }
if ($LASTEXITCODE -ne 0) { throw 'PAUSED: Linux installation did not finish and may have partial setup state. No Windows task was registered or started. Keep existing data and keys; inspect the message above and follow the interrupted-setup section in docs/INSTALL.md. Do not use this installer for routine upgrades.' }
foreach ($unit in @('yachad.service','yachad-update-request.path','yachad-update.timer','yachad-update-backup-expiry.timer')) {
    # Multi-unit is-active succeeds when any unit is active; verify each one.
    & wsl.exe --distribution $Distribution --exec /usr/bin/systemctl is-active --quiet $unit
    if ($LASTEXITCODE -ne 0) { throw 'App/update request path/timers inactive; existing state preserved. No Windows task was registered.' }
    & wsl.exe --distribution $Distribution --exec /usr/bin/systemctl is-enabled --quiet $unit
    if ($LASTEXITCODE -ne 0) { throw 'App/update request path/timers not enabled; existing preference preserved. No Windows task was registered.' }
}
if ($StartAtLogin) {
    $arguments = '--distribution ' + $Distribution + ' --exec /usr/bin/sleep infinity'
    $existing = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    if ($existing) {
        if ($existing.Actions.Count -ne 1 -or $existing.Actions[0].Execute -ne 'wsl.exe' -or $existing.Actions[0].Arguments -ne $arguments) { throw 'An unrelated task uses this name; it was preserved.' }
    } else {
        $action = New-ScheduledTaskAction -Execute 'wsl.exe' -Argument $arguments
        $trigger = New-ScheduledTaskTrigger -AtLogOn -User ([System.Security.Principal.WindowsIdentity]::GetCurrent().Name)
        $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
        Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Settings $settings -Description 'Keep Yachad WSL services available while this Windows user is signed in.' | Out-Null
    }
    Start-ScheduledTask -TaskName $taskName
}
Write-Output 'COMPLETE: WSL2 Linux services and update triggers verified. Fresh installs enable auto; existing update policy is preserved. Windows must remain awake; WSL must remain running. LAN tablet routing and local CA trust need acceptance on your Windows network.'
