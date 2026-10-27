param(
    [string]$Distribution = 'Ubuntu',
    [Parameter(Mandatory=$true)][string]$Origin,
    [switch]$StartAtLogin,
    [switch]$PlanOnly
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
if (-not (Get-Command wsl.exe -ErrorAction SilentlyContinue)) { throw 'Install WSL2 and Ubuntu first: https://learn.microsoft.com/windows/wsl/install . No Windows feature changes were made.' }
& wsl.exe --distribution $Distribution --exec /usr/bin/test -d /run/systemd/system
if ($LASTEXITCODE -ne 0) { throw 'Enable systemd in this WSL2 distribution first. See docs/INSTALL.md. No tasks were registered.' }
$linuxPath = (& wsl.exe --distribution $Distribution --exec /usr/bin/wslpath -u $source).Trim()
if ($LASTEXITCODE -ne 0 -or $linuxPath -notmatch '^/[A-Za-z0-9_./ -]+$') { throw 'Cannot resolve the inspected release path in WSL.' }
# Interactive sudo is handled in the Linux owner terminal. No password is saved.
& wsl.exe --distribution $Distribution --exec /usr/bin/sudo /usr/bin/python3 ($linuxPath + '/deploy/install.py') --origin $Origin
if ($LASTEXITCODE -ne 0) { throw 'Linux installer did not finish; application state preserved. No Windows task was registered.' }
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
