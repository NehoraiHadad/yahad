# Real machine acceptance for a built Yahad.Setup.exe. Every check is what a
# person or administrator observes (Setup results, services, HTTP answers, the
# account they created), so it keeps gating when the mechanism behind it changes:
#   install -> ready -> create the first family over the app's own HTTP API ->
#   the parent reads and changes update settings through app, Platform and the
#   Updater's pipe -> restart the service and time it -> repair stopped services
#   and a missing task -> the Repair task repairs as SYSTEM -> refuse foreign,
#   unsigned and tampered releases -> the parent installs a signed staged release
#   with a schema change through the Updater -> refuse a replay -> a broken release
#   rolls both services back with the data restored -> an upgrade killed mid-way is
#   undone by repair -> uninstall keeping data -> reinstall -> the same account signs
#   in -> uninstall deleting data -> nothing remains.
# Only the Updater's download from GitHub into its staging folder is not driven
# here: GitHub carries no release signed by CI's throwaway key and the source URL is
# fixed by design. The Updater's real check against GitHub does run; the download
# path has unit tests in Yahad.FirstInstall.Tests.
# Changes services and machine folders, so it runs only on a disposable
# administrator machine (CI), never a family PC. Diagnostics and summary.json
# are written even on failure. The one-time setup link and the generated
# password live only in this process's memory; neither is written or printed.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Setup,
    # Signed release directories from installer/ci-build-upgrade-set.ps1.
    [Parameter(Mandatory=$true)][string]$Base,
    [Parameter(Mandatory=$true)][string]$Upgrade,
    [Parameter(Mandatory=$true)][string]$Broken,
    [Parameter(Mandatory=$true)][string]$Refused,
    [Parameter(Mandatory=$true)][string]$Output,
    [switch]$Disposable
)
$ErrorActionPreference = 'Stop'
if (!$Disposable) { throw 'Machine acceptance changes services and machine folders; pass -Disposable on a throwaway machine only' }
$setupPath = (Resolve-Path -LiteralPath $Setup).Path
New-Item -ItemType Directory -Force -Path $Output | Out-Null
$out = (Resolve-Path -LiteralPath $Output).Path
$started = Get-Date
$phases = [ordered]@{}
$state = 'NotTerminal'
$failedPhase = $null
$programData = Join-Path $env:ProgramData 'Yahad.FirstInstall'
$programFiles = Join-Path $env:ProgramFiles 'Yahad.FirstInstall'
$runtime = 'Yahad.Acceptance'
$updater = 'Yahad.Acceptance.Updater'
$installedServices = @($runtime, $updater)
$runningServices = @($runtime, $updater)
# Layout the installer no longer creates; each must stay absent.
$removedProgramFilesEntries = @('payloads', 'bootstrap', 'system-slots', '.authority', 'intake')
$removedProgramDataEntries = @('update-authority.json', 'activation.json', 'epoch.json', 'web.json', 'bootstrap.json')
$account = $null
$origin = $null
$startSeconds = $null
$upgradeSeconds = $null
$rollbackSeconds = $null
$repairSeconds = $null
$resumeSeconds = $null
$baseDirectory = (Resolve-Path -LiteralPath $Base).Path
$upgradeDirectory = (Resolve-Path -LiteralPath $Upgrade).Path
$brokenDirectory = (Resolve-Path -LiteralPath $Broken).Path
$refusedDirectory = (Resolve-Path -LiteralPath $Refused).Path
function ReleaseOf([string]$Directory) {
    $file = @(Get-ChildItem -LiteralPath $Directory -Filter 'yahad-windows-*.json')
    if ($file.Count -ne 1) { throw "Expected one release manifest in $Directory" }
    return Get-Content -LiteralPath $file[0].FullName -Raw | ConvertFrom-Json
}
$baseRelease = ReleaseOf $baseDirectory
$upgradeRelease = ReleaseOf $upgradeDirectory
$brokenRelease = ReleaseOf $brokenDirectory
$upgradeSetup = Join-Path $upgradeDirectory $upgradeRelease.setup.name
$brokenSetup = Join-Path $brokenDirectory $brokenRelease.setup.name
$baseSetup = Join-Path $baseDirectory $baseRelease.setup.name
$upgradeState = Join-Path $programData 'upgrade-state.json'
$updaterRoot = Join-Path $programData 'updater'
$lastUpgrade = Join-Path $updaterRoot 'last-upgrade.json'
$taskPath = '\Yahad\'
$taskName = 'Repair'
$parentSession = $null
$liveCheck = $null
function RetainedSetup([string]$Version) { Join-Path $programFiles "versions\$Version\Yahad.Setup.exe" }

function Phase([string]$Name, [scriptblock]$Body) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    try { & $Body }
    catch { if (!$script:failedPhase) { $script:failedPhase = $Name }; throw }
    finally { $phases[$Name] = [math]::Round($timer.Elapsed.TotalSeconds, 1); Write-Output ("PHASE {0} {1}s" -f $Name, $phases[$Name]) }
}
# Returns the parsed result file and anything Setup wrote to standard output.
# Standard output is returned to the caller, never echoed.
function RunSetup([string[]]$Arguments, [string]$Result, [string[]]$Options = @(), [string]$Image = $setupPath) {
    $start = [Diagnostics.ProcessStartInfo]::new($Image)
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    foreach ($argument in @($Arguments) + @('--result', $Result) + @($Options)) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEnd()
        $process.WaitForExit()
        $exit = $process.ExitCode
    } finally { $process.Dispose() }
    if (!(Test-Path -LiteralPath $Result)) { throw "Setup exited $exit without a result" }
    $value = Get-Content -LiteralPath $Result -Raw | ConvertFrom-Json
    Write-Output ("SETUP {0} ok={1} code={2} exit={3} seconds={4}" -f $Arguments[0], $value.Ok, $value.Code, $exit, $value.Seconds) | Out-Host
    if (!$value.Ok -or $exit -ne 0) { throw "Setup $($Arguments[0]) failed: $($value.Code)" }
    return [pscustomobject]@{ Result=$value; Stdout=$stdout }
}
# A Setup expected to end in this exact outcome, success or refusal.
function RunSetupExpecting([string]$Image, [string[]]$Arguments, [string]$Result, [string]$Code, [int]$Exit) {
    $process = Start-Process -FilePath $Image -ArgumentList (@($Arguments | ForEach-Object { '"' + $_ + '"' }) + @('--result', ('"' + $Result + '"'))) -Wait -PassThru -WindowStyle Hidden
    if (!(Test-Path -LiteralPath $Result)) { throw "Setup exited $($process.ExitCode) without a result" }
    $value = Get-Content -LiteralPath $Result -Raw | ConvertFrom-Json
    Write-Output ("SETUP {0} ok={1} code={2} exit={3} seconds={4}" -f $Arguments[0], $value.Ok, $value.Code, $process.ExitCode, $value.Seconds) | Out-Host
    if ($value.Code -ne $Code -or $process.ExitCode -ne $Exit) { throw "Setup $($Arguments[0]) ended $($value.Code)/$($process.ExitCode), expected $Code/$Exit" }
    return $value
}
function Health { Invoke-RestMethod -Uri "$origin/health/ready" -TimeoutSec 30 }
function ImagePathOf([string]$Name = $runtime) { (Get-CimInstance -ClassName Win32_Service -Filter "Name='$Name'").PathName }
function SignIn {
    $body = Json @{ name=$account.Name; password=$account.Password }
    Invoke-RestMethod -Method Post -Uri "$origin/api/login" -Body $body -ContentType 'application/json' -SessionVariable session -TimeoutSec 60 -Headers @{ Origin=$origin } | Out-Null
    if ((RequireSignedInAccount $session) -ne $familyId) { throw 'The acceptance parent signed in to a different family' }
    $script:parentSession = $session
}
function RepairTaskOf { Get-ScheduledTask -TaskPath $taskPath -TaskName $taskName -ErrorAction SilentlyContinue }
function RequireRepairTask([string]$Version) {
    $task = RepairTaskOf
    if (!$task) { throw 'The Yahad\Repair task is missing' }
    if ($task.Principal.UserId -notin 'SYSTEM', 'S-1-5-18') { throw "The Repair task runs as $($task.Principal.UserId)" }
    if ($task.Actions.Count -ne 1 -or $task.Actions[0].Execute -ne (RetainedSetup $Version) -or $task.Actions[0].Arguments -notlike '--repair --result *') { throw "The Repair task runs $($task.Actions[0].Execute) $($task.Actions[0].Arguments)" }
    if ($task.Triggers.Count -ne 2) { throw "The Repair task has $($task.Triggers.Count) triggers, expected boot and daily" }
}
function RequireNoRepairTask { if (RepairTaskOf) { throw 'The Yahad\Repair task remains' } }
# What the parent's own browser does: the live app -> Platform -> pipe -> Updater path.
function UpdatesStatus {
    $status = Invoke-RestMethod -Uri "$origin/api/parent/updates" -WebSession $parentSession -TimeoutSec 30
    if ($status.configured -ne $true) { throw 'Updates are not configured through the app; the Platform refused or could not verify the Updater' }
    return $status
}
function UpdatesRequest($Body) {
    $csrf = (Invoke-RestMethod -Uri "$origin/api/me" -WebSession $parentSession -TimeoutSec 30).csrf
    Invoke-RestMethod -Method Post -Uri "$origin/api/parent/updates" -Body (Json $Body) -ContentType 'application/json' -WebSession $parentSession -TimeoutSec 30 -Headers @{ Origin=$origin; 'X-CSRF-Token'=$csrf }
}
# What a person sees of a whole installation at one release: it answers as that
# release, the family is theirs, the service points at it and nothing is pending.
function RequireInstalled([string]$Version, [int]$Schema, [string[]]$Retained) {
    $health = Health
    if ($health.version -ne $Version -or $health.databaseSchema -ne $Schema) { throw "Running $($health.version)/schema $($health.databaseSchema), expected $Version/schema $Schema" }
    if ((SetupState) -ne 'complete') { throw 'Family is missing' }
    SignIn
    RequireServiceConfiguration
    if ((ImagePathOf) -notlike "*\versions\$Version\host\*") { throw "ImagePath is $(ImagePathOf), expected version $Version" }
    if ((ImagePathOf $updater) -ne ('"' + (Join-Path $programFiles "versions\$Version\updater\Yahad.Updater.exe") + '" --service')) { throw "Updater ImagePath is $(ImagePathOf $updater), expected version $Version" }
    if ((Get-Service -Name $updater).Status -ne 'Running') { throw "$updater is not running at $Version" }
    if ((UpdatesStatus).currentVersion -ne $Version) { throw "The Updater reports the wrong version, expected $Version" }
    RequireRepairTask $Version
    if (Test-Path -LiteralPath $upgradeState) { throw 'An upgrade record remains' }
    $present = @(Get-ChildItem -LiteralPath (Join-Path $programFiles 'versions') -Directory | ForEach-Object Name | Sort-Object)
    if (($present -join ',') -ne (($Retained | Sort-Object) -join ',')) { throw "Version directories are $($present -join ','), expected $($Retained -join ',')" }
    $display = (Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Yahad.FirstInstall').DisplayVersion
    if ($display -ne $Version) { throw "Apps and Features shows $display, expected $Version" }
}
function YahadServices { @(Get-Service -Name 'Yahad*' -ErrorAction SilentlyContinue) }
function RequireInstalledAndReady($Result) {
    foreach ($name in $installedServices) { if (!(Get-Service -Name $name -ErrorAction SilentlyContinue)) { throw "$name is not installed" } }
    foreach ($name in $runningServices) {
        $service = Get-Service -Name $name
        if ($service.Status -ne 'Running') { throw "$name is $($service.Status)" }
    }
    $script:origin = ([Uri]$Result.Url).GetLeftPart([UriPartial]::Authority)
    $response = Invoke-WebRequest -Uri "$origin/health/ready" -UseBasicParsing -TimeoutSec 30
    if ($response.StatusCode -ne 200) { throw "health/ready returned $($response.StatusCode)" }
}
# Automatic start, restarted by the SCM after a failure (counter reset after a day,
# also on non-crash exits), and launched from its own versioned folder. Read from
# the SCM registry keys so the check does not depend on the display language of sc.exe.
function RequireFailureActions([string]$Name, [string]$Expected) {
    $key = Get-ItemProperty -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Services\$Name"
    $bytes = [byte[]]$key.FailureActions
    if (!$bytes -or $bytes.Length -lt 20) { throw "$Name has no SCM failure actions" }
    $reset = [BitConverter]::ToUInt32($bytes, 0)
    $count = [BitConverter]::ToUInt32($bytes, 12)
    $offset = [BitConverter]::ToUInt32($bytes, 16)
    if ($reset -ne 86400) { throw "$Name failure reset period is $reset, expected 86400" }
    if ($count -ne 3 -or $bytes.Length -lt $offset + 8 * $count) { throw "$Name has $count failure actions, expected 3" }
    $actual = for ($i = 0; $i -lt $count; $i++) { '{0}:{1}' -f [BitConverter]::ToUInt32($bytes, $offset + 8 * $i), [BitConverter]::ToUInt32($bytes, $offset + 8 * $i + 4) }
    if (($actual -join ',') -ne $Expected) { throw "$Name failure actions are $($actual -join ','), expected $Expected" }
    if ($key.FailureActionsOnNonCrashFailures -ne 1) { throw "$Name FailureActionsOnNonCrashFailures is $($key.FailureActionsOnNonCrashFailures), expected 1" }
}
function RequireServiceConfiguration {
    $details = Get-CimInstance -ClassName Win32_Service -Filter "Name='$runtime'"
    if (!$details) { throw "$runtime is not registered with the SCM" }
    if ($details.StartMode -ne 'Auto') { throw "$runtime start mode is $($details.StartMode), expected Auto" }
    if ($details.StartName -ne "NT SERVICE\$runtime") { throw "$runtime runs as $($details.StartName), expected NT SERVICE\$runtime" }
    $expectedImage = '^"' + [regex]::Escape($programFiles) + '\\versions\\\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?\\host\\Yahad\.ServiceHost\.exe" --service$'
    if ($details.PathName -notmatch $expectedImage) { throw "$runtime ImagePath does not match versions\<version>\host\Yahad.ServiceHost.exe --service: $($details.PathName)" }
    RequireFailureActions $runtime '1:5000,1:30000,1:120000'
    $self = Get-CimInstance -ClassName Win32_Service -Filter "Name='$updater'"
    if (!$self) { throw "$updater is not registered with the SCM" }
    if ($self.StartMode -ne 'Auto' -or !$self.DelayedAutoStart) { throw "$updater start mode is $($self.StartMode) delayed=$($self.DelayedAutoStart), expected delayed Auto" }
    if ($self.StartName -ne 'LocalSystem') { throw "$updater runs as $($self.StartName), expected LocalSystem" }
    $expectedSelf = '^"' + [regex]::Escape($programFiles) + '\\versions\\\d+\.\d+\.\d+\\updater\\Yahad\.Updater\.exe" --service$'
    if ($self.PathName -notmatch $expectedSelf) { throw "$updater ImagePath does not match versions\<version>\updater\Yahad.Updater.exe --service: $($self.PathName)" }
    RequireFailureActions $updater '1:60000,1:300000,1:600000'
    foreach ($entry in $removedProgramFilesEntries) { if (Test-Path -LiteralPath (Join-Path $programFiles $entry)) { throw "$entry exists under $programFiles; the current layout has none" } }
    foreach ($entry in $removedProgramDataEntries) { if (Test-Path -LiteralPath (Join-Path $programData $entry)) { throw "$entry exists under $programData; the current layout has none" } }
}
function SetupState { (Invoke-RestMethod -Uri "$origin/api/setup/status" -TimeoutSec 30).state }
function Json($Value) { $Value | ConvertTo-Json -Compress }
function RequireSignedInAccount($Session) {
    $me = Invoke-RestMethod -Uri "$origin/api/me" -WebSession $Session -TimeoutSec 30
    if ($me.user.name -ne $account.Name -or $me.user.role -ne 'parent') { throw 'Signed-in account is not the acceptance parent' }
    return $me.user.familyId
}
function RequireRemovedServices { if ((YahadServices).Count) { throw ('Services remain after removal: ' + ((YahadServices).Name -join ',')) } }
function Collect {
    @(YahadServices | ForEach-Object { [ordered]@{ Name=$_.Name; Status=[string]$_.Status; StartType=[string]$_.StartType; ImagePath=(Get-CimInstance -ClassName Win32_Service -Filter "Name='$($_.Name)'").PathName } }) | ConvertTo-Json | Set-Content -Encoding utf8NoBOM (Join-Path $out 'services.json')
    if (Test-Path -LiteralPath $upgradeState) { Copy-Item -LiteralPath $upgradeState -Destination (Join-Path $out 'upgrade-state.json') }
    foreach ($name in 'state.json', 'last-upgrade.json', 'repair-result.json') {
        $file = Join-Path $updaterRoot $name
        if (Test-Path -LiteralPath $file) { Copy-Item -LiteralPath $file -Destination (Join-Path $out "updater-$name") }
    }
    $since = $started.AddMinutes(-1)
    # The Yahad source exists only after a startup failure wrote to it; a
    # ProviderName filter for an unregistered source throws, so filter after.
    Get-WinEvent -FilterHashtable @{ LogName='Application'; StartTime=$since } -ErrorAction SilentlyContinue |
        Where-Object ProviderName -eq 'Yahad' | ForEach-Object { [ordered]@{ Time=$_.TimeCreated.ToString('o'); Id=$_.Id; Data=@($_.Properties | ForEach-Object Value) } } |
        ConvertTo-Json -Depth 4 | Set-Content -Encoding utf8NoBOM (Join-Path $out 'yahad-events.json')
    Get-WinEvent -FilterHashtable @{ LogName='System'; ProviderName='Service Control Manager'; StartTime=$since } -ErrorAction SilentlyContinue |
        Where-Object { $_.Message -like '*Yahad*' } | ForEach-Object { '{0:o} {1} {2}' -f $_.TimeCreated, $_.Id, ($_.Message -replace "`r?`n", ' ') } |
        Set-Content -Encoding utf8NoBOM (Join-Path $out 'scm-events.txt')
}

try {
    Phase 'preflight' {
        $principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
        $policy = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
        Write-Output ("MACHINE os={0} admin={1} EnableLUA={2} ConsentPromptBehaviorAdmin={3}" -f [Environment]::OSVersion.Version, $principal.IsInRole('Administrators'), $policy.EnableLUA, $policy.ConsentPromptBehaviorAdmin)
        if ((YahadServices).Count -or (Test-Path -LiteralPath $programData) -or (Test-Path -LiteralPath $programFiles)) { throw 'Machine is not clean: an existing Yahad installation is present' }
    }
    $install = $null
    Phase 'install' { $script:install = RunSetup @('--quiet-install', '--updates', 'notify') (Join-Path $out 'install-result.json') @('--setup-link', 'stdout') }
    Phase 'ready' {
        RequireInstalledAndReady $install.Result
        RequireServiceConfiguration
        if ((SetupState) -ne 'ready') { throw 'Fresh install does not offer first-family setup' }
    }
    $familyId = $null
    Phase 'create-family' {
        # The same private link the Setup window opens; parse without echoing it.
        $link = $install.Stdout.Trim()
        $match = [regex]::Match($link, '^(http://127\.0\.0\.1:\d+/)#setup=([A-Za-z0-9_-]{43})$')
        if (!$match.Success) { throw 'Setup did not hand back a well-formed setup link' }
        if (([Uri]$match.Groups[1].Value).GetLeftPart([UriPartial]::Authority) -ne $origin) { throw 'Setup link points at a different origin than the result' }
        $suffix = [Guid]::NewGuid().ToString('N').Substring(0, 10)
        $script:account = [pscustomobject]@{ Family="Acceptance family $suffix"; Name="acceptance-$suffix"; Password=[Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(24)) }
        $body = Json @{ familyName=$account.Family; name=$account.Name; password=$account.Password; confirmation=$account.Password }
        Invoke-RestMethod -Method Post -Uri "$origin/api/setup" -Body $body -ContentType 'application/json' -SessionVariable created -TimeoutSec 60 `
            -Headers @{ Origin=$origin; 'X-Setup-Token'=$match.Groups[2].Value } | Out-Null
        $link = $null; $install.Stdout = $null
        $script:familyId = RequireSignedInAccount $created
        if ((SetupState) -ne 'complete') { throw 'Setup still open after the family was created' }
    }
    Phase 'updater-status' {
        SignIn
        $status = UpdatesStatus
        if ($status.enabled -ne $true) { throw 'A build with a release key reports updates as unavailable' }
        if ($status.mode -ne 'notify') { throw "The Updater's mode is $($status.mode), expected the administrator's choice" }
        if ($status.currentVersion -ne $baseRelease.version) { throw "The Updater reports $($status.currentVersion), expected $($baseRelease.version)" }
        if ($status.modeControlAllowed -ne $true) { throw 'The parent cannot change the update mode' }
        # The mode goes through the pipe and survives the Updater's own restart.
        foreach ($mode in 'auto', 'notify') {
            UpdatesRequest @{ action='set_mode'; mode=$mode } | Out-Null
            Restart-Service -Name $updater
            $after = UpdatesStatus
            if ($after.mode -ne $mode -or $after.lastModeRequest.state -ne 'applied') { throw "The mode is $($after.mode)/$($after.lastModeRequest.state) after asking for $mode" }
        }
    }
    Phase 'updater-check' {
        # The real check against GitHub. Its outcome depends on GitHub and on CI's
        # rate limit, but it must end, and only in one of these ways.
        $before = (UpdatesStatus).checkedAt
        UpdatesRequest @{ action='check' } | Out-Null
        $deadline = [Diagnostics.Stopwatch]::StartNew()
        while ($true) {
            $status = UpdatesStatus
            if ($status.lastRequest.action -eq 'check' -and $status.lastRequest.state -ne 'running') { break }
            if ($deadline.Elapsed -ge [TimeSpan]::FromMinutes(3)) { throw 'The update check did not finish' }
            Start-Sleep -Seconds 2
        }
        if ($status.lastRequest.error -notin $null, 'network', 'signature') { throw "The update check ended with $($status.lastRequest.error)" }
        if ($status.checkedAt -eq $before) { throw 'The update check did not record itself' }
        $script:liveCheck = if ($status.lastRequest.error) { $status.lastRequest.error } else { 'ok' }
        Write-Output "LIVE_CHECK $liveCheck"
        if (Test-Path -LiteralPath (Join-Path $updaterRoot 'staging')) { throw 'A check against the public release staged something CI never signed' }
    }
    Phase 'restart' {
        # What the SCM does for a stopped service: stop the Runtime, then start it
        # again. Measured from the start request to the first 200 from
        # /health/ready, bounded by the Runtime's own five-minute admission budget.
        Stop-Service -Name $runtime
        if ((Get-Service -Name $runtime).Status -ne 'Stopped') { throw "$runtime is $((Get-Service -Name $runtime).Status) after stop" }
        $timer = [Diagnostics.Stopwatch]::StartNew()
        Start-Service -Name $runtime
        $deadline = [TimeSpan]::FromMinutes(5)
        while ($true) {
            try { if ((Invoke-WebRequest -Uri "$origin/health/ready" -UseBasicParsing -TimeoutSec 5).StatusCode -eq 200) { break } } catch { }
            if ($timer.Elapsed -ge $deadline) { throw 'health/ready did not answer 200 within the admission budget after restart' }
            Start-Sleep -Milliseconds 250
        }
        $script:startSeconds = [math]::Round($timer.Elapsed.TotalSeconds, 1)
        Write-Output ("START {0}s" -f $startSeconds)
        RequireInstalledAndReady $install.Result
        if ((SetupState) -ne 'complete') { throw 'Family is missing after restart' }
    }
    $version = $baseRelease.version
    $schema = $baseRelease.databaseSchema
    Phase 'repair' {
        # Services an administrator (or a crash loop) left stopped and unconfigured,
        # and a task someone deleted.
        foreach ($name in $installedServices) {
            Stop-Service -Name $name
            & sc.exe failure $name reset= 0 actions= '' | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Could not clear the failure actions' }
            & sc.exe failureflag $name 0 | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Could not clear the failure flag' }
        }
        Unregister-ScheduledTask -TaskPath $taskPath -TaskName $taskName -Confirm:$false
        $repair = RunSetupExpecting (RetainedSetup $version) @('--repair') (Join-Path $out 'repair-result.json') 'REPAIRED' 0
        $script:repairSeconds = $repair.Seconds
        RequireInstalled $version $schema @($version)
        $null = RunSetupExpecting (RetainedSetup $version) @('--repair') (Join-Path $out 'repair-again-result.json') 'NOTHING_TO_REPAIR' 0
    }
    Phase 'repair-task' {
        # The task itself, in the session and token it has at 3 a.m.: SYSTEM runs the
        # retained single-file Setup, which finds nothing to do and says so.
        RequireRepairTask $version
        $taskResult = Join-Path $updaterRoot 'repair-result.json'
        Remove-Item -LiteralPath $taskResult -Force -ErrorAction SilentlyContinue
        Start-ScheduledTask -TaskPath $taskPath -TaskName $taskName
        $deadline = [Diagnostics.Stopwatch]::StartNew()
        while ($true) {
            $value = $null
            try { if (Test-Path -LiteralPath $taskResult) { $value = Get-Content -LiteralPath $taskResult -Raw | ConvertFrom-Json } } catch { }
            if ($value) { break }
            if ($deadline.Elapsed -ge [TimeSpan]::FromMinutes(5)) { throw 'The Repair task wrote no result' }
            Start-Sleep -Seconds 1
        }
        if ($value.Code -ne 'NOTHING_TO_REPAIR') { throw "The Repair task ended with $($value.Code)" }
    }
    Phase 'refuse-release' {
        foreach ($case in 'foreign', 'unsigned', 'tampered') {
            $null = RunSetupExpecting $upgradeSetup @('--quiet-upgrade', (Join-Path $refusedDirectory $case)) (Join-Path $out "refuse-$case-result.json") 'RELEASE_SIGNATURE_INVALID' 2
        }
        $null = RunSetupExpecting (RetainedSetup $version) @('--quiet-upgrade', (Join-Path $refusedDirectory 'foreign')) (Join-Path $out 'refuse-retained-result.json') 'RELEASE_SIGNATURE_INVALID' 2
        # A valid release is installed only by its own signed Setup.
        $null = RunSetupExpecting $baseSetup @('--quiet-upgrade', $upgradeDirectory) (Join-Path $out 'refuse-mismatch-result.json') 'RELEASE_SETUP_MISMATCH' 2
        RequireInstalled $version $schema @($version)
    }
    Phase 'upgrade' {
        # The Updater's own install path from the parent's click on. The release is
        # placed where the Updater's download would leave it (the staging folder only
        # administrators can write) with nothing vouching for it but its signature: the
        # Updater re-verifies it, offers it, launches its Setup detached, and is itself
        # stopped and replaced by that Setup.
        $stage = Join-Path $updaterRoot "staging\$($upgradeRelease.version)"
        New-Item -ItemType Directory -Force -Path $stage | Out-Null
        Copy-Item -Path (Join-Path $upgradeDirectory '*') -Destination $stage
        Restart-Service -Name $updater
        $status = UpdatesStatus
        $manifest = Join-Path $upgradeDirectory "yahad-windows-$($upgradeRelease.version).json"
        $fingerprint = (Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($status.phase -ne 'available' -or $status.latestVersion -ne $upgradeRelease.version -or $status.installTarget.fingerprint -ne $fingerprint) { throw "The Updater offers $($status.phase)/$($status.latestVersion)/$($status.installTarget.fingerprint), expected $($upgradeRelease.version)/$fingerprint" }
        # A target that is not the staged release is refused before anything runs.
        $stale = $false
        try { UpdatesRequest @{ action='install'; target=@{ version=$upgradeRelease.version; fingerprint=('0' * 64) } } | Out-Null } catch { $stale = [int]$_.Exception.Response.StatusCode -eq 409 }
        if (!$stale) { throw 'An install of another target was not refused' }
        UpdatesRequest @{ action='install'; target=@{ version=$upgradeRelease.version; fingerprint=$fingerprint } } | Out-Null
        $deadline = [Diagnostics.Stopwatch]::StartNew()
        while ($true) {
            $result = $null
            # The Setup may still be writing it.
            try { if (Test-Path -LiteralPath $lastUpgrade) { $result = Get-Content -LiteralPath $lastUpgrade -Raw | ConvertFrom-Json } } catch { }
            if ($result) { break }
            if ($deadline.Elapsed -ge [TimeSpan]::FromMinutes(10)) { throw 'The Setup the Updater launched wrote no result' }
            Start-Sleep -Seconds 1
        }
        Copy-Item -LiteralPath $lastUpgrade -Destination (Join-Path $out 'upgrade-result.json')
        if ($result.Code -ne 'UPGRADED') { throw "The Updater's install ended with $($result.Code)" }
        if ($result.FromVersion -ne $version -or $result.ToVersion -ne $upgradeRelease.version -or $result.RolledBack) { throw 'Upgrade reported the wrong versions' }
        $script:upgradeSeconds = $result.Seconds
    }
    Phase 'upgraded' {
        # The new Updater reads what the Setup left and tells the parent, whose session
        # a real upgrade leaves signed in.
        $status = UpdatesStatus
        if ($status.phase -ne 'healthy' -or $status.lastRequest.action -ne 'install' -or $status.lastRequest.state -ne 'succeeded') { throw "After the install the Updater reports $($status.phase)/$($status.lastRequest.action)/$($status.lastRequest.state)" }
        # The Setup image may be finishing its exit; the next minute's pass takes the folder.
        $stage = Join-Path $updaterRoot "staging\$($upgradeRelease.version)"
        $deadline = [Diagnostics.Stopwatch]::StartNew()
        while (Test-Path -LiteralPath $stage) {
            if ($deadline.Elapsed -ge [TimeSpan]::FromMinutes(3)) { throw 'The installed release stayed in the staging folder' }
            Start-Sleep -Seconds 5
        }
        if ($upgradeRelease.databaseSchema -ne $schema + 1) { throw 'The upgrade fixture does not change the schema' }
        RequireInstalled $upgradeRelease.version $upgradeRelease.databaseSchema @($version, $upgradeRelease.version)
        $retained = (Get-FileHash -LiteralPath (RetainedSetup $upgradeRelease.version) -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($retained -ne $upgradeRelease.setup.sha256) { throw 'The retained Setup is not the signed one' }
    }
    Phase 'refuse-replay' {
        $null = RunSetupExpecting $baseSetup @('--quiet-upgrade', $baseDirectory) (Join-Path $out 'refuse-downgrade-result.json') 'RELEASE_NOT_NEWER' 2
        $null = RunSetupExpecting $upgradeSetup @('--quiet-upgrade', $upgradeDirectory) (Join-Path $out 'refuse-same-result.json') 'RELEASE_NOT_NEWER' 2
    }
    Phase 'broken-upgrade' {
        $result = RunSetupExpecting $brokenSetup @('--quiet-upgrade', $brokenDirectory) (Join-Path $out 'broken-result.json') 'ROLLED_BACK_UNHEALTHY' 4
        if (!$result.RolledBack) { throw 'A broken release did not report its rollback' }
        $began = @($result.Phases | Where-Object Phase -eq 'rolling_back')
        if ($began.Count -ne 1) { throw 'The result does not show the rollback' }
        $script:rollbackSeconds = [math]::Round($result.Seconds - $began[0].Seconds, 1)
    }
    Phase 'rolled-back' { RequireInstalled $upgradeRelease.version $upgradeRelease.databaseSchema @($version, $upgradeRelease.version) }
    Phase 'repair-resume' {
        # A real kill, with no help from the product: the broken release has migrated
        # the database and is failing health when its Setup disappears.
        $killed = Start-Process -FilePath $brokenSetup -ArgumentList @('--quiet-upgrade', ('"' + $brokenDirectory + '"'), '--result', ('"' + (Join-Path $out 'killed-result.json') + '"')) -PassThru -WindowStyle Hidden
        try {
            $deadline = [Diagnostics.Stopwatch]::StartNew()
            while ($true) {
                $seen = $null
                # The record is replaced atomically but may be open for a moment.
                try { if (Test-Path -LiteralPath $upgradeState) { $seen = (Get-Content -LiteralPath $upgradeState -Raw | ConvertFrom-Json).Phase } } catch { }
                if ($seen -eq 'started') { break }
                if ($killed.HasExited) { throw "Setup ended early with $($killed.ExitCode)" }
                if ($deadline.Elapsed -ge [TimeSpan]::FromMinutes(6)) { throw "Upgrade never started the new release; last phase $seen" }
                Start-Sleep -Milliseconds 200
            }
            # The window in which the Runtime migrates; it reaches the database first.
            $migrated = [Diagnostics.Stopwatch]::StartNew()
            while ($migrated.Elapsed -lt [TimeSpan]::FromSeconds(120)) {
                $status = 0
                try { Invoke-WebRequest -Uri "$origin/health/ready" -UseBasicParsing -TimeoutSec 5 | Out-Null } catch { if ($_.Exception.Response) { $status = [int]$_.Exception.Response.StatusCode } }
                if ($status -eq 503) { break }
                Start-Sleep -Milliseconds 500
            }
            if ($status -ne 503) { throw 'The broken release never answered health' }
        } finally { if (!$killed.HasExited) { $killed.Kill() }; $killed.WaitForExit() }
        if (!(Test-Path -LiteralPath $upgradeState)) { throw 'A killed upgrade left no record' }
        if ((Test-Path -LiteralPath (Join-Path $out 'killed-result.json'))) { throw 'The killed Setup finished before it was killed' }
        $result = RunSetupExpecting (RetainedSetup $upgradeRelease.version) @('--repair') (Join-Path $out 'resume-result.json') 'REPAIRED' 0
        if (!$result.RolledBack) { throw 'Repair did not roll back the interrupted upgrade' }
        $script:resumeSeconds = $result.Seconds
    }
    Phase 'resumed' { RequireInstalled $upgradeRelease.version $upgradeRelease.databaseSchema @($version, $upgradeRelease.version) }
    # Removal and reinstall from here belong to the upgraded release.
    $setupPath = $upgradeSetup
    Phase 'uninstall-keep' {
        $removed = RunSetup @('--quiet-uninstall', '--data', 'keep') (Join-Path $out 'uninstall-keep-result.json')
        if ($removed.Result.DataPreserved -ne $true) { throw 'Keep-data removal did not report preserved data' }
    }
    Phase 'kept' {
        RequireRemovedServices
        if (!(Test-Path -LiteralPath $programData)) { throw "$programData was removed by a keep-data uninstall" }
        if (Test-Path -LiteralPath $programFiles) { throw "$programFiles remains after a keep-data uninstall" }
        RequireNoRepairTask
        if (!(Test-Path -LiteralPath (Join-Path $updaterRoot 'state.json'))) { throw 'A keep-data uninstall lost the update mode' }
        if (Test-Path -LiteralPath (Join-Path $updaterRoot 'staging')) { throw 'A keep-data uninstall kept downloaded releases' }
    }
    $reinstall = $null
    Phase 'reinstall' { $script:reinstall = RunSetup @('--quiet-install', '--updates', 'notify') (Join-Path $out 'reinstall-result.json') @('--setup-link', 'stdout') }
    Phase 'persisted' {
        RequireInstalledAndReady $reinstall.Result
        RequireServiceConfiguration
        if ($reinstall.Stdout.Trim()) { throw 'Reinstall offered a new setup link over kept data' }
        if ((SetupState) -ne 'complete') { throw 'Kept family is missing after reinstall' }
        RequireInstalled $upgradeRelease.version $upgradeRelease.databaseSchema @($upgradeRelease.version)
    }
    Phase 'uninstall' { RunSetup @('--quiet-uninstall', '--data', 'delete') (Join-Path $out 'uninstall-result.json') | Out-Null }
    Phase 'removed' {
        RequireRemovedServices
        foreach ($path in $programData, $programFiles, (Join-Path $env:windir 'System32\Tasks\Yahad')) { if (Test-Path -LiteralPath $path) { throw "$path remains after removal" } }
        RequireNoRepairTask
    }
    $state = 'Passed'
}
catch { $state = 'Failed'; Write-Output "FAILED $($_.Exception.Message)"; throw }
finally {
    $account = $null
    try { Collect } catch { Write-Output "COLLECTION_FAILED $($_.Exception.Message)" }
    [ordered]@{ State=$state; FailedPhase=$failedPhase; Phases=$phases; StartSeconds=$startSeconds; UpgradeSeconds=$upgradeSeconds; RollbackSeconds=$rollbackSeconds; RepairSeconds=$repairSeconds; ResumeSeconds=$resumeSeconds; LiveCheck=$liveCheck; TotalSeconds=[math]::Round(((Get-Date)-$started).TotalSeconds, 1) } |
        ConvertTo-Json -Depth 4 | Set-Content -Encoding utf8NoBOM (Join-Path $out 'summary.json')
}
