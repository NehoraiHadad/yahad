# Real machine acceptance for the upgrade set of installer/ci-build-upgrade-set.ps1
# (n, app, unhealthy, n1, broken, contract, refused). Every check is what a person
# or administrator observes (Setup results, services, HTTP answers, the account they
# created), so it keeps gating when the mechanism behind it changes. One installation
# is carried through every move; pairs are (infrastructure, app):
#   install n (I, A) -> ready, with the two ACLs proven by Windows -> create the first family over the app's own HTTP API ->
#   the parent reads and changes update settings through app, Platform and the
#   Updater's pipe -> restart the service and time it -> repair stopped services
#   and a missing task -> the Repair task repairs as SYSTEM -> refuse foreign,
#   unsigned and tampered releases on both tracks, another release's Setup, and an app
#   whose contract its own infrastructure does not serve -> a home-network proxy is placed ->
#   the parent installs app (I, A+1) on the app track through the Updater, which keeps
#   running and reports the new app; firewall rule and Caddyfile untouched, help page from
#   A+1 -> the parent installs unhealthy (I, A+2), which rolls back to (I, A+1) with the
#   data restored and the help page from A+1 again -> broken (I+1, A+4) on the Setup track
#   rolls everything back to (I, A+1), leaving the paused proxy stopped -> the same upgrade
#   killed mid-way is undone by repair -> the parent installs n1 (I+1, A+3) on the Setup
#   track through the Updater, the ACLs proven again -> refuse replays on both tracks ->
#   uninstall keeping data -> reinstall -> the same account signs in -> uninstall deleting
#   data -> nothing remains -> install n again and run app's Setup by hand: the same
#   infrastructure, so only the app moves -> refuse the older Setup -> uninstall deleting
#   data -> nothing remains.
# Only the Updater's download from GitHub into its staging folder is not driven
# here: GitHub carries no release signed by CI's throwaway key and the source URL is
# fixed by design. The Updater's real check against GitHub does run; the download
# path has unit tests in Yahad.FirstInstall.Tests.
# Neither is the home-network enable itself, which only a person can start and whose
# https origin would end this script's http session: PlaceLanProxy leaves what enable
# leaves, a real service and real Caddy on a real address. The upgrade, rollback, repair
# and uninstall that act on it are the product's own.
# Changes services and machine folders, so it runs only on a disposable
# administrator machine (CI), never a family PC. Diagnostics and summary.json
# are written even on failure. The one-time setup link and the generated
# password live only in this process's memory; neither is written or printed.
[CmdletBinding()]
param(
    # The signed upgrade set from installer/ci-build-upgrade-set.ps1.
    [Parameter(Mandatory=$true)][string]$Set,
    [Parameter(Mandatory=$true)][string]$Output,
    [switch]$Disposable
)
$ErrorActionPreference = 'Stop'
if (!$Disposable) { throw 'Machine acceptance changes services and machine folders; pass -Disposable on a throwaway machine only' }
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
$lanService = 'Yahad.FirstInstall.Lan'
$lanRoot = Join-Path $env:ProgramData $lanService
$lanAddress = $null
$lanRuleName = $null
# Layout the installer no longer creates; each must stay absent.
$removedProgramFilesEntries = @('payloads', 'bootstrap', 'system-slots', '.authority', 'intake', 'versions')
$removedProgramDataEntries = @('update-authority.json', 'activation.json', 'epoch.json', 'web.json', 'bootstrap.json')
$account = $null
$origin = $null
$startSeconds = $null
$upgradeSeconds = $null
$rollbackSeconds = $null
$repairSeconds = $null
$resumeSeconds = $null
$appUpdateSeconds = $null
$appRollbackSeconds = $null
$setDirectory = (Resolve-Path -LiteralPath $Set).Path
$index = Get-Content -LiteralPath (Join-Path $setDirectory 'upgrade-set.json') -Raw | ConvertFrom-Json
if ($index.format -ne 1) { throw "upgrade-set.json is format $($index.format), expected 1" }
# A release in the set, by the exact names of plan 5.1: its manifest, the version it
# names, both tracks' files and what its infrastructure and app say of themselves.
function ReleaseIn([string]$Directory, [string]$Version) {
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "upgrade-set.json names no valid version for $Directory" }
    $manifest = Join-Path $Directory "yahad-windows-release-$Version.json"
    $value = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
    if ($value.format -ne 2 -or $value.product -ne 'yahad-windows-release' -or $value.version -ne $Version) { throw "$manifest is not the format-2 release $Version" }
    if ($value.infrastructure.setup.name -ne "Yahad.Setup-$Version.exe" -or $value.app.archive.name -ne "yahad-windows-app-$Version.zip") { throw "$manifest names other files" }
    return [pscustomobject]@{
        Version=$Version; Directory=$Directory; Manifest=$manifest; Value=$value
        Infra=$value.infrastructure.version; Schema=[int]$value.app.databaseSchema
        SetupName=$value.infrastructure.setup.name; ArchiveName=$value.app.archive.name
        Setup=(Join-Path $Directory $value.infrastructure.setup.name)
    }
}
function Variant([string]$Name) { ReleaseIn (Join-Path $setDirectory "$Name\release") $index.releases.$Name }
$n = Variant 'n'
$app = Variant 'app'
$unhealthy = Variant 'unhealthy'
$n1 = Variant 'n1'
$broken = Variant 'broken'
$contract = ReleaseIn (Join-Path $setDirectory 'contract') $index.contract
$refusedDirectory = Join-Path $setDirectory 'refused'
if ($index.refused.app -ne $app.Version -or $index.refused.setup -ne $n1.Version) { throw 'The refused stages are not of app and n1' }
# What each move below means, from the signed manifests themselves.
if ($app.Infra -ne $n.Infra -or $app.Value.infrastructure.treeSha256 -ne $n.Value.infrastructure.treeSha256 -or $unhealthy.Value.infrastructure.treeSha256 -ne $n.Value.infrastructure.treeSha256) { throw 'app and unhealthy are not on n''s infrastructure' }
if ($broken.Value.infrastructure.treeSha256 -ne $n1.Value.infrastructure.treeSha256 -or $n1.Infra -eq $n.Infra) { throw 'broken is not on n1''s new infrastructure' }
if ($app.Schema -ne $n.Schema + 1 -or $unhealthy.Schema -ne $app.Schema + 1 -or $n1.Schema -ne $app.Schema + 1) { throw 'The app and Setup moves do not each change the schema' }
if ($contract.Value.app.requiresContract -le $contract.Value.infrastructure.contractMax) { throw 'The contract release needs a contract its infrastructure serves' }
# Setup runs for installation; from the reinstall on, the newer release's.
$setupPath = $n.Setup
# Stages for runs that are not the Updater's, in exactly the Updater's shape. Setup
# reads its stage only; nothing here is a product location.
$stageRoot = Join-Path ([IO.Path]::GetTempPath()) ('yahad-acceptance-stages-' + [Guid]::NewGuid().ToString('N'))
$stages = @{}
$upgradeState = Join-Path $programData 'upgrade-state.json'
$updaterRoot = Join-Path $programData 'updater'
$lastUpgrade = Join-Path $updaterRoot 'last-upgrade.json'
$taskPath = '\Yahad\'
$taskName = 'Repair'
$parentSession = $null
$liveCheck = $null
function InfraRoot([string]$Infra) { Join-Path $programFiles "infra\$Infra" }
function AppRoot([string]$Version) { Join-Path $programFiles "apps\$Version" }
# The payload-free engine of an infrastructure: the Repair task, Apps & Features and
# every uninstall run it.
function EngineOf([string]$Infra) { Join-Path (InfraRoot $Infra) 'setup\Yahad.Setup.exe' }
function HostImage([string]$Infra) { Join-Path (InfraRoot $Infra) 'host\Yahad.ServiceHost.exe' }
function UpdaterCommand([string]$Infra) { '"' + (Join-Path (InfraRoot $Infra) 'updater\Yahad.Updater.exe') + '" --service' }
$activePointer = Join-Path $programFiles 'active.json'
# A stage holds exactly one track's files by their exact names (plan 9.3): the
# manifest, its signature and the app archive (app) or the Setup (setup).
function NewStage($Release, [string]$Track, [string]$Path) {
    $file = switch ($Track) { 'app' { $Release.ArchiveName } 'setup' { $Release.SetupName } default { throw "Unknown track $Track" } }
    if (Test-Path -LiteralPath $Path) { throw "$Path already exists" }
    New-Item -ItemType Directory -Force -Path $Path | Out-Null
    foreach ($name in (Split-Path $Release.Manifest -Leaf), ((Split-Path $Release.Manifest -Leaf) + '.sig'), $file) {
        Copy-Item -LiteralPath (Join-Path $Release.Directory $name) -Destination (Join-Path $Path $name)
    }
}
function StageOf($Release, [string]$Track) {
    $key = "$($Release.Version)/$Track"
    if (!$stages.ContainsKey($key)) { $stages[$key] = Join-Path $stageRoot "$($Release.Version)-$Track"; NewStage $Release $Track $stages[$key] }
    return $stages[$key]
}
# The staged release's identity as the Updater offers it (plan 9.3): SHA-256 of the
# track, a line feed and the raw manifest bytes.
function Fingerprint($Release, [string]$Track) {
    $bytes = [byte[]]@([Text.Encoding]::ASCII.GetBytes("$Track`n")) + [IO.File]::ReadAllBytes($Release.Manifest)
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}
function UpdaterProcess { (Get-CimInstance -ClassName Win32_Service -Filter "Name='$updater'").ProcessId }
# Every file of an infrastructure tree with its size and last write: a tree nobody
# extracted, replaced or rewrote again reads the same.
function InfraSnapshot([string]$Infra) {
    $root = InfraRoot $Infra
    (@(Get-ChildItem -LiteralPath $root -Recurse -Force -File | ForEach-Object { '{0}|{1}|{2}' -f $_.FullName.Substring($root.Length), $_.Length, $_.LastWriteTimeUtc.Ticks }) | Sort-Object) -join "`n"
}

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
function RequireRepairTask([string]$Infra) {
    $task = RepairTaskOf
    if (!$task) { throw 'The Yahad\Repair task is missing' }
    if ($task.Principal.UserId -notin 'SYSTEM', 'S-1-5-18') { throw "The Repair task runs as $($task.Principal.UserId)" }
    if ($task.Actions.Count -ne 1 -or $task.Actions[0].Execute -ne (EngineOf $Infra) -or $task.Actions[0].Arguments -notlike '--repair --result *') { throw "The Repair task runs $($task.Actions[0].Execute) $($task.Actions[0].Arguments)" }
    if ($task.Triggers.Count -ne 2) { throw "The Repair task has $($task.Triggers.Count) triggers, expected boot and daily" }
}
function RequireNoRepairTask { if (RepairTaskOf) { throw 'The Yahad\Repair task remains' } }
# What the parent's own browser does: the live app -> Platform -> pipe -> Updater path.
function UpdatesStatus {
    $status = Invoke-RestMethod -Uri "$origin/api/parent/updates" -WebSession $parentSession -TimeoutSec 30
    if ($status.configured -ne $true) { throw "Updates are not configured through the app; reason $($status.reason)" }
    return $status
}
function UpdatesRequest($Body) {
    $csrf = (Invoke-RestMethod -Uri "$origin/api/me" -WebSession $parentSession -TimeoutSec 30).csrf
    Invoke-RestMethod -Method Post -Uri "$origin/api/parent/updates" -Body (Json $Body) -ContentType 'application/json' -WebSession $parentSession -TimeoutSec 30 -Headers @{ Origin=$origin; 'X-CSRF-Token'=$csrf }
}
# The Updater's own install path from the parent's click on. The release is placed
# where the Updater's download would leave it (the staging folder only administrators
# can write), holding only its track's files and nothing vouching for it but its
# signature: the Updater re-verifies it, selects its track, offers it, and launches
# the installed engine (app) or the staged Setup (setup) detached. $Prepare runs once
# the stage exists, before the Updater looks. Returns the Updater's process id at the
# click and the result the launched engine or Setup wrote.
function InstallThroughUpdater($Release, [string]$Track, [string]$Name, [scriptblock]$Prepare = {}) {
    $stage = Join-Path $updaterRoot "staging\$($Release.Version)"
    NewStage $Release $Track $stage
    & $Prepare
    Restart-Service -Name $updater
    $status = UpdatesStatus
    $fingerprint = Fingerprint $Release $Track
    if ($status.phase -ne 'available' -or $status.latestVersion -ne $Release.Version -or $status.installTarget.fingerprint -ne $fingerprint) { throw "The Updater offers $($status.phase)/$($status.latestVersion)/$($status.installTarget.fingerprint), expected $($Release.Version)/$fingerprint" }
    # A target that is not the staged release is refused before anything runs.
    $stale = $false
    try { UpdatesRequest @{ action='install'; target=@{ version=$Release.Version; fingerprint=('0' * 64) } } | Out-Null } catch { $stale = [int]$_.Exception.Response.StatusCode -eq 409 }
    if (!$stale) { throw 'An install of another target was not refused' }
    $process = UpdaterProcess
    UpdatesRequest @{ action='install'; target=@{ version=$Release.Version; fingerprint=$fingerprint } } | Out-Null
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    while ($true) {
        $result = $null
        # The engine or Setup may still be writing it.
        try { if (Test-Path -LiteralPath $lastUpgrade) { $result = Get-Content -LiteralPath $lastUpgrade -Raw | ConvertFrom-Json } } catch { }
        if ($result) { break }
        if ($deadline.Elapsed -ge [TimeSpan]::FromMinutes(10)) { throw "The $Track install the Updater launched wrote no result" }
        Start-Sleep -Seconds 1
    }
    Copy-Item -LiteralPath $lastUpgrade -Destination (Join-Path $out "$Name-result.json")
    return [pscustomobject]@{ Process=$process; Result=$result }
}
# What the result says of the move, in the person's terms: from which pair to which.
function RequireMove($Result, [string]$Code, $From, $To, [bool]$RolledBack) {
    if ($Result.Code -ne $Code) { throw "The move to $($To.Version) ended with $($Result.Code), expected $Code" }
    if ($Result.FromVersion -ne $From.Version -or $Result.ToVersion -ne $To.Version -or $Result.FromInfrastructureVersion -ne $From.Infra -or $Result.ToInfrastructureVersion -ne $To.Infra) {
        throw "The result reports $($Result.FromInfrastructureVersion)/$($Result.FromVersion) -> $($Result.ToInfrastructureVersion)/$($Result.ToVersion), expected $($From.Infra)/$($From.Version) -> $($To.Infra)/$($To.Version)"
    }
    if ([bool]$Result.RolledBack -ne $RolledBack) { throw "The result reports RolledBack=$($Result.RolledBack), expected $RolledBack" }
}
function RollbackSecondsOf($Result) {
    $began = @($Result.Phases | Where-Object Phase -eq 'rolling_back')
    if ($began.Count -ne 1) { throw 'The result does not show the rollback' }
    return [math]::Round($Result.Seconds - $began[0].Seconds, 1)
}
# The staged release leaves once the Updater has read the result; a Setup still
# exiting holds its image, and the next minute's pass takes the folder.
function RequireStageGone($Release) {
    $stage = Join-Path $updaterRoot "staging\$($Release.Version)"
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    while (Test-Path -LiteralPath $stage) {
        if ($deadline.Elapsed -ge [TimeSpan]::FromMinutes(3)) { throw "Release $($Release.Version) stayed in the staging folder" }
        Start-Sleep -Seconds 5
    }
}
# What a person sees of a whole installation at one release: it answers as that
# release, the family is theirs, the service points at it and nothing is pending.
function RequireInstalled([string]$Version, [int]$Schema, [string]$Infra, [string]$PreviousVersion = $null, [string]$PreviousInfra = $null) {
    $health = Health
    if ($health.version -ne $Version -or $health.databaseSchema -ne $Schema) { throw "Running $($health.version)/schema $($health.databaseSchema), expected $Version/schema $Schema" }
    if ((SetupState) -ne 'complete') { throw 'Family is missing' }
    SignIn
    RequireServiceConfiguration
    if ((ImagePathOf) -ne ('"' + (HostImage $Infra) + '" --service')) { throw "ImagePath is $(ImagePathOf), expected infrastructure $Infra" }
    if ((ImagePathOf $updater) -ne (UpdaterCommand $Infra)) { throw "Updater ImagePath is $(ImagePathOf $updater), expected infrastructure $Infra" }
    if ((Get-Service -Name $updater).Status -ne 'Running') { throw "$updater is not running at $Version" }
    if ((UpdatesStatus).currentVersion -ne $Version) { throw "The Updater reports the wrong version, expected $Version" }
    RequireRepairTask $Infra
    if (Test-Path -LiteralPath $upgradeState) { throw 'An upgrade record remains' }
    RequireActivePair $Version $Infra
    RequireRecordedPairs $Version $Infra $PreviousVersion $PreviousInfra
    # Exactly the active pair and the previous committed pair; two pairs may share an infrastructure directory.
    $expectedInfras = @(@($Infra, $PreviousInfra) | Where-Object { $_ } | Sort-Object -Unique)
    $expectedApps = @(@($Version, $PreviousVersion) | Where-Object { $_ } | Sort-Object -Unique)
    $presentInfras = @(Get-ChildItem -LiteralPath (Join-Path $programFiles 'infra') -Directory | ForEach-Object Name | Sort-Object)
    if (($presentInfras -join ',') -ne ($expectedInfras -join ',')) { throw "Infrastructure directories are $($presentInfras -join ','), expected $($expectedInfras -join ',')" }
    $presentApps = @(Get-ChildItem -LiteralPath (Join-Path $programFiles 'apps') -Directory | ForEach-Object Name | Sort-Object)
    if (($presentApps -join ',') -ne ($expectedApps -join ',')) { throw "App directories are $($presentApps -join ','), expected $($expectedApps -join ',')" }
    $entry = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Yahad.FirstInstall'
    if ($entry.DisplayVersion -ne $Version) { throw "Apps and Features shows $($entry.DisplayVersion), expected $Version" }
    if ($entry.UninstallString -notlike "*$(EngineOf $Infra)*") { throw "Apps and Features uninstalls with $($entry.UninstallString), expected the engine of infrastructure $Infra" }
}
# active.json names this pair, and the tree records it names are the ones on disk; the
# engine the Repair task and Apps & Features run is a member of the infrastructure tree.
function RequireActivePair([string]$Version, [string]$Infra) {
    $pointer = Get-Content -LiteralPath $activePointer -Raw | ConvertFrom-Json
    if ($pointer.format -ne 1) { throw "active.json is format $($pointer.format), expected 1" }
    if ($pointer.infrastructure.version -ne $Infra) { throw "active.json names infrastructure $($pointer.infrastructure.version), expected $Infra" }
    if ($pointer.app.version -ne $Version) { throw "active.json names app $($pointer.app.version), expected $Version" }
    if ($pointer.app.requiresContract -lt $pointer.infrastructure.contractMin -or $pointer.app.requiresContract -gt $pointer.infrastructure.contractMax) { throw 'active.json pairs an app with an infrastructure outside its contract' }
    $infraTree = Join-Path (InfraRoot $Infra) 'infra-tree.json'
    $appTree = Join-Path (AppRoot $Version) 'app-tree.json'
    if ((Get-FileHash -LiteralPath $infraTree -Algorithm SHA256).Hash -ne $pointer.infrastructure.treeSha256) { throw 'infra-tree.json is not the tree active.json names' }
    if ((Get-FileHash -LiteralPath $appTree -Algorithm SHA256).Hash -ne $pointer.app.treeSha256) { throw 'app-tree.json is not the tree active.json names' }
    $member = @((Get-Content -LiteralPath $infraTree -Raw | ConvertFrom-Json).files | Where-Object path -eq 'setup/Yahad.Setup.exe')
    if ($member.Count -ne 1) { throw 'infra-tree.json does not list the Setup engine exactly once' }
    if ((Get-FileHash -LiteralPath (EngineOf $Infra) -Algorithm SHA256).Hash -ne $member[0].sha256) { throw "The engine of infrastructure $Infra is not the one its tree lists" }
}
# installer-state.json (format 7): Active, and Previous when a previous committed pair is retained.
function RequireRecordedPairs([string]$Version, [string]$Infra, [string]$PreviousVersion, [string]$PreviousInfra) {
    $record = Get-Content -LiteralPath (Join-Path $programData 'installer-state.json') -Raw | ConvertFrom-Json
    if ($record.Format -ne 7) { throw "The installer record is format $($record.Format), expected 7" }
    if ($record.Active.Infrastructure.Version -ne $Infra -or $record.Active.App.Version -ne $Version) { throw "The record's active pair is $($record.Active.Infrastructure.Version)/$($record.Active.App.Version), expected $Infra/$Version" }
    if ($PreviousVersion) {
        if ($record.Previous.Infrastructure.Version -ne $PreviousInfra -or $record.Previous.App.Version -ne $PreviousVersion) { throw "The record's previous pair is $($record.Previous.Infrastructure.Version)/$($record.Previous.App.Version), expected $PreviousInfra/$PreviousVersion" }
    } elseif ($record.Previous) { throw 'The record keeps a previous pair, expected none' }
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
# also on non-crash exits), and launched from its own infrastructure folder. Read from
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
    $expectedImage = '^"' + [regex]::Escape($programFiles) + '\\infra\\\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?\\host\\Yahad\.ServiceHost\.exe" --service$'
    if ($details.PathName -notmatch $expectedImage) { throw "$runtime ImagePath does not match infra\<version>\host\Yahad.ServiceHost.exe --service: $($details.PathName)" }
    RequireFailureActions $runtime '1:5000,1:30000,1:120000'
    $self = Get-CimInstance -ClassName Win32_Service -Filter "Name='$updater'"
    if (!$self) { throw "$updater is not registered with the SCM" }
    if ($self.StartMode -ne 'Auto' -or !$self.DelayedAutoStart) { throw "$updater start mode is $($self.StartMode) delayed=$($self.DelayedAutoStart), expected delayed Auto" }
    if ($self.StartName -ne 'LocalSystem') { throw "$updater runs as $($self.StartName), expected LocalSystem" }
    $expectedSelf = '^"' + [regex]::Escape($programFiles) + '\\infra\\\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?\\updater\\Yahad\.Updater\.exe" --service$'
    if ($self.PathName -notmatch $expectedSelf) { throw "$updater ImagePath does not match infra\<version>\updater\Yahad.Updater.exe --service: $($self.PathName)" }
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
function LanProxySid {
    $match = [regex]::Match((& sc.exe showsid $lanService | Out-String), 'S-1-5-80(-\d+)+')
    if (!$match.Success) { throw "sc.exe gave no service SID for $lanService" }
    return $match.Value
}
function LanCaddy([string]$Infra) { Join-Path (InfraRoot $Infra) 'runtime\caddy\caddy.exe' }
function LanImage([string]$Infra) { '"' + (HostImage $Infra) + '" --lan-service' }
function LanRule {
    foreach ($rule in (New-Object -ComObject HNetCfg.FwPolicy2).Rules) { if ($rule.Name -eq $lanRuleName) { return $rule } }
}
function NewLanDirectory([string]$Path, [string]$Descriptor) {
    $security = [Security.AccessControl.DirectorySecurity]::new()
    $security.SetSecurityDescriptorSddlForm($Descriptor)
    [IO.FileSystemAclExtensions]::Create([IO.DirectoryInfo]::new($Path), $security)
}
# What a finished home-network enable leaves: the two ACLs of NativeLanInstall, its
# records, LanDeployment's Caddyfile and firewall rule, and the service. A machine
# without a private address fails here, naming what it has.
function PlaceLanProxy([string]$Infra) {
    $adapters = @([Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces() | Where-Object OperationalStatus -eq 'Up')
    $choices = @(foreach ($adapter in $adapters) {
        $properties = $adapter.GetIPProperties()
        foreach ($unicast in $properties.UnicastAddresses) {
            if ($unicast.Address.AddressFamily -eq 'InterNetwork') { [pscustomobject]@{ Id=$adapter.Id; Address=$unicast.Address.ToString(); Routed=$properties.GatewayAddresses.Count -gt 0 } }
        }
    })
    $choice = $choices | Where-Object Address -match '^(10\.|172\.(1[6-9]|2\d|3[01])\.|192\.168\.)' | Sort-Object Routed -Descending | Select-Object -First 1
    if (!$choice) { throw "No private IPv4 address on an adapter that is up: $(($choices | ForEach-Object Address) -join ',')" }
    Write-Output ("LAN address={0} category={1}" -f $choice.Address, ((Get-NetConnectionProfile | ForEach-Object { "$($_.InterfaceAlias):$($_.NetworkCategory)" }) -join ','))
    $script:lanAddress = $choice.Address
    $installation = Get-Content -LiteralPath (Join-Path $programData 'installer-state.json') -Raw | ConvertFrom-Json
    $script:lanRuleName = "Yahad.LAN.$($installation.InstallationId).TCP.443"

    $sid = LanProxySid
    $administrators = 'O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)'
    $serviceData = "$administrators(A;OICI;0x1301bf;;;$sid)(A;OICI;0x1301bf;;;OW)"
    NewLanDirectory $lanRoot "$administrators(A;OICI;0x1200a9;;;$sid)"
    foreach ($name in 'data', 'temporary') { NewLanDirectory (Join-Path $lanRoot $name) $serviceData }
    New-Item -ItemType Directory -Path (Join-Path $lanRoot 'join-public') | Out-Null
    $records = @{
        'proxy.json' = Json @{ AdapterId=$choice.Id; Address=$choice.Address }
        'owner.json' = Json @{ Phase='enabled'; AdapterId=$choice.Id; Address=$choice.Address; HttpHelp=$false; RootSha256='' }
        'Caddyfile' = @(
            '{', '    admin off', '    persist_config off', '    skip_install_trust', '    auto_https disable_redirects',
            '    log {', '        output discard', '    }',
            '    storage file_system {', ('        root "' + (Join-Path $lanRoot 'data').Replace('\', '/') + '"'), '    }',
            '    servers {', '        protocols h1 h2', '    }', '}',
            "https://$($choice.Address) {", "    bind $($choice.Address)", '    tls internal',
            '    @ownerOnly path /health /health/* /api/setup /api/setup/*', '    respond @ownerOnly 404',
            "    reverse_proxy 127.0.0.1:$($installation.Port) {", '        flush_interval -1', '    }', '}', ''
        ) -join "`n"
    }
    foreach ($name in $records.Keys) { [IO.File]::WriteAllText((Join-Path $lanRoot $name), $records[$name], [Text.UTF8Encoding]::new($false)) }

    $rule = New-Object -ComObject HNetCfg.FWRule
    $rule.Name = $lanRuleName
    $rule.Description = "Yahad home network; installation $($installation.InstallationId); TCP 443"
    $rule.Grouping = "Yahad.LAN.$($installation.InstallationId)"
    $rule.ApplicationName = LanCaddy $Infra
    $rule.Protocol = 6
    $rule.Direction = 1
    $rule.LocalPorts = '443'
    $rule.RemotePorts = '*'
    $rule.LocalAddresses = $choice.Address
    $rule.RemoteAddresses = 'LocalSubnet'
    $rule.InterfaceTypes = 'All'
    $rule.Profiles = 2
    $rule.Action = 1
    $rule.EdgeTraversal = $false
    $rule.Enabled = $true
    (New-Object -ComObject HNetCfg.FwPolicy2).Rules.Add($rule)

    & sc.exe create $lanService binPath= (LanImage $Infra) obj= "NT SERVICE\$lanService" start= delayed-auto type= own DisplayName= 'Yahad home network HTTPS' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc.exe create ended $LASTEXITCODE" }
    & sc.exe sidtype $lanService unrestricted | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc.exe sidtype ended $LASTEXITCODE" }
    & sc.exe privs $lanService SeChangeNotifyPrivilege | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc.exe privs ended $LASTEXITCODE" }
    Start-Service -Name $lanService
}
# What enable publishes once Caddy has made its authority.
function PublishLanRoot {
    $root = [Security.Cryptography.X509Certificates.X509Certificate2]::new((Join-Path $lanRoot 'data\pki\authorities\local\root.crt'))
    $script:lanRootDer = $root.RawData
    $script:lanRootSha256 = ($root.GetCertHashString([Security.Cryptography.HashAlgorithmName]::SHA256) -replace '(..)(?=.)', '$1:')
    [IO.File]::WriteAllBytes((Join-Path $lanRoot 'join-public\certificate.crt'), $lanRootDer)
    $owner = Get-Content -LiteralPath (Join-Path $lanRoot 'owner.json') -Raw | ConvertFrom-Json
    $owner.RootSha256 = $lanRootSha256
    [IO.File]::WriteAllText((Join-Path $lanRoot 'owner.json'), (Json $owner), [Text.UTF8Encoding]::new($false))
}
# Each app of the set marks its help page template with its version; the page must
# come from the active app, and only from it.
function RequireHelpPageOf([string]$Text, [string]$Version) {
    $marks = [regex]::Matches($Text, '<meta name="yahad-ci-app" content="([^"]*)">')
    if ($marks.Count -ne 1 -or $marks[0].Groups[1].Value -ne $Version) { throw "The help page comes from app $(@($marks | ForEach-Object { $_.Groups[1].Value }) -join ','), expected $Version" }
}
function RequireHelpPageFile([string]$Version) { RequireHelpPageOf ([IO.File]::ReadAllText((Join-Path $lanRoot 'join-public\index.html'))) $Version }
# What the app track may never touch (plan 7, 6a): the proxy configuration and the
# installation's firewall rules, byte for byte and field for field.
function LanServedState {
    $rules = foreach ($rule in (New-Object -ComObject HNetCfg.FwPolicy2).Rules) {
        if ("$($rule.Grouping)" -like 'Yahad.LAN.*') {
            (@('Name', 'Description', 'Grouping', 'ApplicationName', 'Protocol', 'Direction', 'LocalPorts', 'RemotePorts', 'LocalAddresses', 'RemoteAddresses', 'InterfaceTypes', 'Profiles', 'Action', 'EdgeTraversal', 'Enabled') | ForEach-Object { "$_=$($rule.$_)" }) -join ';'
        }
    }
    if (!@($rules).Count) { throw 'The installation has no home-network firewall rule' }
    return ((Get-FileHash -LiteralPath (Join-Path $lanRoot 'Caddyfile') -Algorithm SHA256).Hash + "`n" + ((@($rules) | Sort-Object) -join "`n"))
}
function RequireLanServedUnchanged([string]$Before) { if ((LanServedState) -cne $Before) { throw 'The Caddyfile or the home-network firewall rules changed' } }
# A device that opens HTTPS directly reaches the active app's help page and the
# certificate, and the application, the owner's paths and the public files' methods
# stay what they were.
function RequireLanHelpOverHttps([string]$Version) {
    function LanGet([string]$Path, [string]$Method = 'GET') { Invoke-WebRequest -Uri "https://$lanAddress$Path" -Method $Method -SkipCertificateCheck -SkipHttpErrorCheck -UseBasicParsing -TimeoutSec 15 }
    $page = LanGet '/home-network'
    if ($page.StatusCode -ne 200 -or "$($page.Headers['Content-Type'])" -notmatch '^text/html') { throw "The help page answered $($page.StatusCode) $($page.Headers['Content-Type'])" }
    if (!$page.Content.Contains($lanRootSha256) -or !$page.Content.Contains("https://$lanAddress") -or $page.Content.Contains('__LAN_HTTPS_ORIGIN__')) { throw 'The help page is not this release''s page for this installation' }
    RequireHelpPageOf $page.Content $Version
    RequireHelpPageFile $Version
    if ("$($page.Headers['Content-Security-Policy'])" -notmatch "default-src 'none'") { throw 'The help page lost its content policy' }
    $certificate = LanGet '/certificate.crt'
    if ($certificate.StatusCode -ne 200 -or "$($certificate.Headers['Content-Type'])" -ne 'application/pkix-cert') { throw "The certificate answered $($certificate.StatusCode) $($certificate.Headers['Content-Type'])" }
    if ([Convert]::ToBase64String($certificate.Content) -ne [Convert]::ToBase64String($lanRootDer)) { throw 'The certificate served is not the installation''s root' }
    $application = LanGet '/'
    if ($application.StatusCode -ne 200 -or "$($application.Headers['Content-Security-Policy'])" -match "default-src 'none'") { throw 'The proxy no longer serves the application' }
    if ((LanGet '/health').StatusCode -ne 404) { throw 'The owner''s health path is reachable from the network' }
    if ("$((LanGet '/home-network' 'POST').Headers['Content-Security-Policy'])" -match "default-src 'none'") { throw 'The help page accepted a POST' }
}
function PauseLanProxy {
    Stop-Service -Name $lanService
    & sc.exe config $lanService start= demand | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc.exe config ended $LASTEXITCODE" }
}
function ResumeLanProxy {
    & sc.exe config $lanService start= delayed-auto | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc.exe config ended $LASTEXITCODE" }
    Start-Service -Name $lanService
}
# The proxy's service, its firewall rule and, when it should run, its two processes
# and its HTTPS answer are all at this release.
function RequireLanProxyAt([string]$Infra, [bool]$Running) {
    if ((ImagePathOf $lanService) -ne (LanImage $Infra)) { throw "The proxy's ImagePath is $(ImagePathOf $lanService), expected infrastructure $Infra" }
    $rule = LanRule
    if (!$rule) { throw 'The proxy has no firewall rule' }
    if ($rule.ApplicationName -ne (LanCaddy $Infra)) { throw "The firewall rule names $($rule.ApplicationName) for $($rule.LocalAddresses), expected infrastructure $Infra" }
    $service = Get-CimInstance -ClassName Win32_Service -Filter "Name='$lanService'"
    if (!$Running) {
        if ($service.State -ne 'Stopped') { throw "The proxy is $($service.State), expected Stopped" }
        return
    }
    if ($service.State -ne 'Running') { throw "The proxy is $($service.State) at infrastructure $Infra, expected Running" }
    $image = Get-CimInstance -ClassName Win32_Process -Filter "ProcessId=$($service.ProcessId)"
    $caddy = @(Get-CimInstance -ClassName Win32_Process -Filter "ParentProcessId=$($service.ProcessId) AND Name='caddy.exe'")
    if ($image.ExecutablePath -ne (HostImage $Infra)) { throw "The proxy runs $($image.ExecutablePath), expected infrastructure $Infra" }
    if ($caddy.Count -ne 1 -or $caddy[0].ExecutablePath -ne (LanCaddy $Infra)) { throw "Caddy runs $($caddy.ExecutablePath -join ','), expected infrastructure $Infra" }
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    while ($true) {
        try { $null = Invoke-WebRequest -Uri "https://$lanAddress/" -SkipCertificateCheck -SkipHttpErrorCheck -UseBasicParsing -TimeoutSec 5; break }
        catch { if ($deadline.Elapsed -ge [TimeSpan]::FromMinutes(1)) { throw "The proxy does not answer on https://$lanAddress/ at infrastructure ${Infra}: $($_.Exception.Message)" } }
        Start-Sleep -Seconds 1
    }
}
$effectiveAccessSource = @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
public static class YahadEffectiveAccess
{
    [StructLayout(LayoutKind.Sequential)] private struct Request { public int Desired; public IntPtr Self; public IntPtr Types; public int TypeCount; public IntPtr Optional; }
    [StructLayout(LayoutKind.Sequential)] private struct Reply { public int Length; public IntPtr Granted; public IntPtr Sacl; public IntPtr Error; }
    [StructLayout(LayoutKind.Sequential)] private struct Group { public IntPtr Sid; public int Attributes; }
    [DllImport("authz.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool AuthzInitializeResourceManager(int flags, IntPtr check, IntPtr add, IntPtr free, string name, out IntPtr manager);
    [DllImport("authz.dll", SetLastError = true)] private static extern bool AuthzInitializeContextFromSid(int flags, byte[] sid, IntPtr manager, IntPtr expiry, long id, IntPtr arguments, out IntPtr context);
    [DllImport("authz.dll", SetLastError = true)] private static extern bool AuthzAddSidsToContext(IntPtr context, Group[] sids, int count, IntPtr restricted, int restrictedCount, out IntPtr updated);
    [DllImport("authz.dll", SetLastError = true)] private static extern bool AuthzAccessCheck(int flags, IntPtr context, ref Request request, IntPtr audit, byte[] descriptor, IntPtr others, int otherCount, ref Reply reply, IntPtr results);
    [DllImport("authz.dll")] private static extern bool AuthzFreeContext(IntPtr context);
    [DllImport("authz.dll")] private static extern bool AuthzFreeResourceManager(IntPtr manager);
    private const int MaximumAllowed = 0x02000000, NoAudit = 1, SkipTokenGroups = 2, GroupEnabled = 4;
    private static byte[] Bytes(string sid) { var value = new SecurityIdentifier(sid); var bytes = new byte[value.BinaryLength]; value.GetBinaryForm(bytes, 0); return bytes; }
    // The first SID is the principal, the rest are the groups its token would carry.
    public static int Granted(byte[] descriptor, string[] sids)
    {
        IntPtr manager, context, grouped;
        if (!AuthzInitializeResourceManager(NoAudit, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, "yahad-acceptance", out manager)) throw new Win32Exception();
        var groups = new Group[sids.Length - 1];
        try
        {
            if (!AuthzInitializeContextFromSid(SkipTokenGroups, Bytes(sids[0]), manager, IntPtr.Zero, 0, IntPtr.Zero, out context)) throw new Win32Exception();
            try
            {
                for (var i = 0; i < groups.Length; i++)
                {
                    var bytes = Bytes(sids[i + 1]);
                    groups[i].Sid = Marshal.AllocHGlobal(bytes.Length); groups[i].Attributes = GroupEnabled;
                    Marshal.Copy(bytes, 0, groups[i].Sid, bytes.Length);
                }
                if (!AuthzAddSidsToContext(context, groups, groups.Length, IntPtr.Zero, 0, out grouped)) throw new Win32Exception();
                var memory = Marshal.AllocHGlobal(8);
                try
                {
                    var request = new Request { Desired = MaximumAllowed };
                    var reply = new Reply { Length = 1, Granted = memory, Error = memory + 4 };
                    if (!AuthzAccessCheck(0, grouped, ref request, IntPtr.Zero, descriptor, IntPtr.Zero, 0, ref reply, IntPtr.Zero)) throw new Win32Exception();
                    return Marshal.ReadInt32(memory + 4) == 0 ? Marshal.ReadInt32(memory) : 0;
                }
                finally { Marshal.FreeHGlobal(memory); AuthzFreeContext(grouped); }
            }
            finally { foreach (var group in groups) if (group.Sid != IntPtr.Zero) Marshal.FreeHGlobal(group.Sid); AuthzFreeContext(context); }
        }
        finally { AuthzFreeResourceManager(manager); }
    }
}
'@
# What Windows' own access check grants these SIDs on a path as it stands now.
function EffectiveRights([string]$Path, [string[]]$Sids) {
    if (-not ('YahadEffectiveAccess' -as [type])) { Add-Type -TypeDefinition $effectiveAccessSource }
    [YahadEffectiveAccess]::Granted((Get-Acl -LiteralPath $Path).GetSecurityDescriptorBinaryForm(), $Sids)
}
# Ran by the Runtime account itself, so its answers come from its real token.
$accessProbeSource = @'
$plan = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('PLAN')) | ConvertFrom-Json
$answer = [ordered]@{ user = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value; refused = 0; wrongly = @(); broken = @(); modified = 0 }
$name = '.access-probe-' + [Guid]::NewGuid().ToString('N')
foreach ($directory in $plan.refuse) {
    try { $file = Join-Path $directory $name; [IO.File]::WriteAllText($file, 'probe'); [IO.File]::Delete($file); $answer.wrongly += $directory }
    catch [UnauthorizedAccessException] { $answer.refused++ }
    catch { $answer.broken += "$directory $($_.Exception.GetType().Name)" }
}
foreach ($file in $plan.sealed) {
    try { $held = [IO.File]::Open($file, 'Open', 'Write', 'ReadWrite,Delete'); $held.Dispose(); $answer.wrongly += $file }
    catch [UnauthorizedAccessException] { $answer.refused++ }
    catch { $answer.broken += "$file $($_.Exception.GetType().Name)" }
}
foreach ($directory in $plan.modify) {
    try {
        $file = Join-Path $directory $name; $moved = $file + '.moved'
        [IO.File]::WriteAllText($file, 'probe'); [IO.File]::AppendAllText($file, 'probe'); [IO.File]::Move($file, $moved)
        if ([IO.File]::ReadAllText($moved) -cne 'probeprobe') { throw 'read back' }
        [IO.File]::Delete($moved); $answer.modified++
    } catch { $answer.broken += "$directory $($_.Exception.GetType().Name)" }
}
[IO.File]::WriteAllText($plan.result, ($answer | ConvertTo-Json -Compress))
'@
function RunAccessProbe([string]$Sid, [string[]]$Refuse, [string[]]$Sealed, [string[]]$Modify, [string]$Result) {
    Remove-Item -LiteralPath $Result -Force -ErrorAction SilentlyContinue
    $plan = @{ refuse = $Refuse; sealed = $Sealed; modify = $Modify; result = $Result } | ConvertTo-Json -Compress
    $body = $accessProbeSource.Replace('PLAN', [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($plan)))
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($body))
    $probeTask = 'YahadAcceptanceAccessProbe'
    $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -NonInteractive -EncodedCommand $encoded"
    $principal = New-ScheduledTaskPrincipal -UserId "NT SERVICE\$runtime" -LogonType ServiceAccount -RunLevel Limited
    Register-ScheduledTask -TaskName $probeTask -Action $action -Principal $principal -Settings (New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries) -Force | Out-Null
    try {
        Start-ScheduledTask -TaskName $probeTask
        $deadline = [Diagnostics.Stopwatch]::StartNew()
        $answer = $null
        while (!$answer) {
            try { if (Test-Path -LiteralPath $Result) { $answer = Get-Content -LiteralPath $Result -Raw | ConvertFrom-Json } } catch { }
            if ($answer) { break }
            if ($deadline.Elapsed -ge [TimeSpan]::FromMinutes(2)) { throw "The Runtime account's access probe wrote no result (task result $((Get-ScheduledTaskInfo -TaskName $probeTask).LastTaskResult))" }
            Start-Sleep -Seconds 1
        }
    }
    finally {
        Stop-ScheduledTask -TaskName $probeTask -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $probeTask -Confirm:$false
    }
    Remove-Item -LiteralPath $Result -Force
    if ($answer.user -ne $Sid) { throw "The access probe ran as $($answer.user), not the Runtime account $Sid" }
    if (@($answer.broken).Count) { throw "The access probe could not decide: $(@($answer.broken) -join '; ')" }
    if (@($answer.wrongly).Count) { throw "The Runtime account could write: $(@($answer.wrongly) -join '; ')" }
    if ($answer.refused -ne $Refuse.Count + $Sealed.Count -or $answer.modified -ne $Modify.Count) { throw 'The access probe did not cover every path' }
}
# The two ACLs as Windows enforces them: the Runtime account changes nothing but the
# data, and no ordinary user reaches anything in the machine's state folder.
function RequireAccessBoundaries([string]$Infra, [string]$Version) {
    $state = Get-Content -LiteralPath (Join-Path $programData 'installer-state.json') -Raw | ConvertFrom-Json
    $data = Join-Path $programData "datasets\$($state.DataId)\data"
    $temporary = Join-Path $programData 'temporary'
    $sid = (New-Object Security.Principal.NTAccount "NT SERVICE\$runtime").Translate([Security.Principal.SecurityIdentifier]).Value
    $runtimeToken = @($sid, 'S-1-5-6', 'S-1-5-80-0', 'S-1-5-32-545', 'S-1-5-11', 'S-1-1-0')
    $userToken = @('S-1-5-21-1-2-3-1001', 'S-1-5-32-545', 'S-1-5-4', 'S-1-5-11', 'S-1-2-0', 'S-1-1-0')
    $anyChange = 0x2 -bor 0x4 -bor 0x10 -bor 0x40 -bor 0x100 -bor 0x10000 -bor 0x40000 -bor 0x80000
    $modify = 0x1 -bor 0x2 -bor 0x4 -bor 0x20 -bor 0x80 -bor 0x100 -bor 0x10000
    $engine = EngineOf $Infra
    $installFiles = @($engine, $activePointer)

    $everything = @(Get-ChildItem -LiteralPath $programData -Recurse -Force -ErrorAction Stop)
    $writable = @($data, $temporary)
    $inWritable = { param($Path) foreach ($root in $writable) { if ($Path -eq $root -or $Path.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) { return $true } }; $false }
    $stateDirectories = @($programData) + @($everything | Where-Object { $_.PSIsContainer -and !(& $inWritable $_.FullName) } | ForEach-Object FullName)
    $stateFiles = @($everything | Where-Object { !$_.PSIsContainer -and !(& $inWritable $_.FullName) } | ForEach-Object FullName)
    $installDirectories = @($programFiles, (Join-Path $programFiles 'infra'), (Join-Path $programFiles 'apps'), (InfraRoot $Infra), (Join-Path (InfraRoot $Infra) 'host'), (Join-Path (InfraRoot $Infra) 'updater'), (Join-Path (InfraRoot $Infra) 'setup'), (AppRoot $Version))
    $proxyToken = @((LanProxySid), 'S-1-5-6', 'S-1-5-80-0', 'S-1-5-32-545', 'S-1-5-11', 'S-1-1-0')

    foreach ($path in @($installDirectories + $installFiles + $stateDirectories + $stateFiles)) {
        try { $granted = EffectiveRights $path $runtimeToken } catch [Management.Automation.ItemNotFoundException] { continue }
        if ($granted -band $anyChange) { throw ('The Runtime account holds change rights 0x{0:x} on {1}' -f $granted, $path) }
        if (!($granted -band 0x1)) { throw ('The Runtime account cannot read {0}' -f $path) }
    }
    foreach ($item in @($everything | Where-Object { & $inWritable $_.FullName }) + @(Get-Item -LiteralPath $data, $temporary)) {
        try { $granted = EffectiveRights $item.FullName $runtimeToken } catch [Management.Automation.ItemNotFoundException] { continue }
        if (($granted -band $modify) -ne $modify) { throw ('The Runtime account holds only 0x{0:x} on {1}' -f $granted, $item.FullName) }
        if ($granted -band (0x40000 -bor 0x80000)) { throw ('The Runtime account can change the ACL of {0}' -f $item.FullName) }
    }
    foreach ($path in @($programData) + @($everything | ForEach-Object FullName)) {
        try { $granted = EffectiveRights $path $userToken } catch [Management.Automation.ItemNotFoundException] { continue }
        if ($granted -ne 0) { throw ('Users hold rights 0x{0:x} on {1}' -f $granted, $path) }
    }
    # Apps & Features starts the infrastructure's engine unelevated, and the shell reads its
    # manifest before it can ask for elevation; this has to hold or removal never opens.
    foreach ($path in @($installDirectories + $engine)) {
        $granted = EffectiveRights $path $userToken
        if ($granted -band $anyChange) { throw ('Users hold change rights 0x{0:x} on {1}' -f $granted, $path) }
        if (($granted -band 0x21) -ne 0x21) { throw ('Users hold only 0x{0:x} on {1}, so Apps & Features cannot start removal' -f $granted, $path) }
    }

    # The proxy's account reads the binary tree to run from it, and nothing of the state.
    foreach ($path in @($installDirectories + $installFiles + (LanCaddy $Infra))) {
        try { $granted = EffectiveRights $path $proxyToken } catch [Management.Automation.ItemNotFoundException] { continue }
        if ($granted -band $anyChange) { throw ('The proxy account holds change rights 0x{0:x} on {1}' -f $granted, $path) }
        if (!($granted -band 0x1)) { throw ('The proxy account cannot read {0}' -f $path) }
    }
    foreach ($path in @($programData) + @($everything | ForEach-Object FullName)) {
        try { $granted = EffectiveRights $path $proxyToken } catch [Management.Automation.ItemNotFoundException] { continue }
        if ($granted -ne 0) { throw ('The proxy account holds rights 0x{0:x} on {1}' -f $granted, $path) }
    }

    $sealedFiles = @($installFiles + (Join-Path $programData 'installer-state.json'), (Join-Path $programData 'installed.json'), (Join-Path $programData "datasets\$($state.DataId)\master-key"))
    RunAccessProbe $sid ($installDirectories + $stateDirectories) $sealedFiles $writable (Join-Path $data ('.access-probe-result-' + [Guid]::NewGuid().ToString('N')))
}
function RequireRemovedServices { if ((YahadServices).Count) { throw ('Services remain after removal: ' + ((YahadServices).Name -join ',')) } }
function RequireNothingLeft {
    RequireRemovedServices
    foreach ($path in $programData, $programFiles, $lanRoot, "$programFiles.lan-uninstall.json", (Join-Path $env:windir 'System32\Tasks\Yahad')) { if (Test-Path -LiteralPath $path) { throw "$path remains after removal" } }
    RequireNoRepairTask
}
function Collect {
    @(YahadServices | ForEach-Object { [ordered]@{ Name=$_.Name; Status=[string]$_.Status; StartType=[string]$_.StartType; ImagePath=(Get-CimInstance -ClassName Win32_Service -Filter "Name='$($_.Name)'").PathName } }) | ConvertTo-Json | Set-Content -Encoding utf8NoBOM (Join-Path $out 'services.json')
    if (Test-Path -LiteralPath $upgradeState) { Copy-Item -LiteralPath $upgradeState -Destination (Join-Path $out 'upgrade-state.json') }
    if (Test-Path -LiteralPath $activePointer) { Copy-Item -LiteralPath $activePointer -Destination (Join-Path $out 'active.json') }
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
        if ((YahadServices).Count -or (Test-Path -LiteralPath $programData) -or (Test-Path -LiteralPath $programFiles) -or (Test-Path -LiteralPath $lanRoot)) { throw 'Machine is not clean: an existing Yahad installation is present' }
    }
    $install = $null
    Phase 'install' { $script:install = RunSetup @('--quiet-install', '--updates', 'notify') (Join-Path $out 'install-result.json') @('--setup-link', 'stdout') }
    Phase 'ready' {
        RequireInstalledAndReady $install.Result
        RequireServiceConfiguration
        RequireAccessBoundaries $n.Infra $n.Version
        if ((SetupState) -ne 'ready') { throw 'Fresh install does not offer first-family setup' }
    }
    Phase 'expired-link' {
        # Fifteen minutes passing, without waiting them: the app reads this file only as it starts.
        $capability = Join-Path $programData 'Secrets\setup-capability.json'
        Stop-Service -Name $runtime
        $record = Get-Content -LiteralPath $capability -Raw | ConvertFrom-Json
        $record.expiresAt = 1
        [IO.File]::WriteAllBytes($capability, [Text.UTF8Encoding]::new($false).GetBytes(($record | ConvertTo-Json -Compress -Depth 5)))
        Start-Service -Name $runtime
        if ((SetupState) -ne 'closed') { throw 'An expired setup link still reports setup as open' }
        $expired = $install.Stdout.Trim()
        $script:install = RunSetup @('--quiet-install', '--updates', 'notify') (Join-Path $out 'renew-result.json') @('--setup-link', 'stdout')
        if ((SetupState) -ne 'ready') { throw 'Running Setup again did not reopen first-family setup' }
        if ($install.Stdout.Trim() -eq $expired) { throw 'Running Setup again handed back the expired link' }
        $expired = $null
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
        if ($status.currentVersion -ne $n.Version) { throw "The Updater reports $($status.currentVersion), expected $($n.Version)" }
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
        $repair = RunSetupExpecting (EngineOf $n.Infra) @('--repair') (Join-Path $out 'repair-result.json') 'REPAIRED' 0
        $script:repairSeconds = $repair.Seconds
        RequireInstalled $n.Version $n.Schema $n.Infra
        $null = RunSetupExpecting (EngineOf $n.Infra) @('--repair') (Join-Path $out 'repair-again-result.json') 'NOTHING_TO_REPAIR' 0
    }
    Phase 'repair-task' {
        # The task itself, in the session and token it has at 3 a.m.: SYSTEM runs the
        # infrastructure's payload-free engine, which finds nothing to do and says so.
        RequireRepairTask $n.Infra
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
        # Stages of the next release on each track, each wrong only in its signature: the
        # installed engine refuses the app stage, n1's own Setup refuses its Setup stage.
        foreach ($case in 'foreign', 'unsigned', 'tampered') {
            $null = RunSetupExpecting (EngineOf $n.Infra) @('--apply-app', (Join-Path $refusedDirectory "app\$case")) (Join-Path $out "refuse-app-$case-result.json") 'RELEASE_SIGNATURE_INVALID' 2
            $stage = Join-Path $refusedDirectory "setup\$case"
            $null = RunSetupExpecting (Join-Path $stage $n1.SetupName) @('--quiet-upgrade', $stage) (Join-Path $out "refuse-setup-$case-result.json") 'RELEASE_SIGNATURE_INVALID' 2
        }
        # A valid release is installed only by its own signed Setup.
        $null = RunSetupExpecting $n.Setup @('--quiet-upgrade', (StageOf $n1 'setup')) (Join-Path $out 'refuse-mismatch-result.json') 'RELEASE_SETUP_MISMATCH' 2
        # An app needing a contract its own infrastructure does not serve is a defect of
        # the release on every machine (plan 9.1), never an app-track update.
        $null = RunSetupExpecting (EngineOf $n.Infra) @('--apply-app', $contract.Directory) (Join-Path $out 'refuse-contract-result.json') 'APP_NEEDS_INFRASTRUCTURE' 2
        RequireInstalled $n.Version $n.Schema $n.Infra
    }
    $lanServed = $null
    Phase 'lan-proxy' {
        PlaceLanProxy $n.Infra
        RequireLanProxyAt $n.Infra $true
        PublishLanRoot
        $script:lanServed = LanServedState
    }
    $updaterBefore = $null
    $infraBefore = $null
    Phase 'app-update' {
        $script:infraBefore = InfraSnapshot $n.Infra
        # The staging folder exists only while it holds a release, so it is checked now.
        $move = InstallThroughUpdater $app 'app' 'app-update' { RequireAccessBoundaries $n.Infra $n.Version }
        RequireMove $move.Result 'UPGRADED' $n $app $false
        $script:updaterBefore = $move.Process
        $script:appUpdateSeconds = $move.Result.Seconds
    }
    Phase 'app-updated' {
        # The Updater was never stopped (plan 7): it reads the result the engine left and
        # reports the pair now active, not the one it started with (plan 9.2).
        $status = UpdatesStatus
        if ($status.phase -ne 'healthy' -or $status.lastRequest.action -ne 'install' -or $status.lastRequest.state -ne 'succeeded') { throw "After the app install the Updater reports $($status.phase)/$($status.lastRequest.action)/$($status.lastRequest.state)" }
        if ((UpdaterProcess) -ne $updaterBefore) { throw 'An app update restarted the Updater' }
        RequireStageGone $app
        RequireInstalled $app.Version $app.Schema $n.Infra $n.Version $n.Infra
        if ((InfraSnapshot $n.Infra) -cne $infraBefore) { throw 'An app update changed the infrastructure tree' }
        RequireAccessBoundaries $n.Infra $app.Version
        RequireLanProxyAt $n.Infra $true
        RequireLanServedUnchanged $lanServed
        RequireLanHelpOverHttps $app.Version
    }
    Phase 'app-unhealthy' {
        # The app migrates the database, then never becomes ready.
        $move = InstallThroughUpdater $unhealthy 'app' 'app-unhealthy'
        RequireMove $move.Result 'ROLLED_BACK_UNHEALTHY' $app $unhealthy $true
        $script:appRollbackSeconds = RollbackSecondsOf $move.Result
        $script:updaterBefore = $move.Process
    }
    Phase 'app-rolled-back' {
        $status = UpdatesStatus
        if ($status.phase -ne 'rolled_back' -or $status.lastRequest.action -ne 'install' -or $status.lastRequest.state -ne 'failed' -or $status.lastRequest.error -ne 'health') { throw "After the unhealthy app the Updater reports $($status.phase)/$($status.lastRequest.action)/$($status.lastRequest.state)/$($status.lastRequest.error)" }
        if ((UpdaterProcess) -ne $updaterBefore) { throw 'An app rollback restarted the Updater' }
        # Refused, so neither kept nor offered again.
        RequireStageGone $unhealthy
        if ($status.installTarget) { throw "The Updater still offers $($status.installTarget.version)" }
        # The earlier app, with the data it had before the unhealthy app migrated it.
        RequireInstalled $app.Version $app.Schema $n.Infra $n.Version $n.Infra
        if ((InfraSnapshot $n.Infra) -cne $infraBefore) { throw 'An app rollback changed the infrastructure tree' }
        RequireLanProxyAt $n.Infra $true
        RequireLanServedUnchanged $lanServed
        RequireLanHelpOverHttps $app.Version
    }
    Phase 'broken-upgrade' {
        # The Setup track from (I, A+1) to (I+1, A+4), whose app never becomes ready. A
        # paused proxy moves with the release and is not started by it.
        PauseLanProxy
        $stage = StageOf $broken 'setup'
        $result = RunSetupExpecting (Join-Path $stage $broken.SetupName) @('--quiet-upgrade', $stage) (Join-Path $out 'broken-result.json') 'ROLLED_BACK_UNHEALTHY' 4
        RequireMove $result 'ROLLED_BACK_UNHEALTHY' $app $broken $true
        $script:rollbackSeconds = RollbackSecondsOf $result
    }
    Phase 'rolled-back' {
        # Images, firewall rule, pointer, data and help page are the earlier pair's again.
        RequireInstalled $app.Version $app.Schema $n.Infra $n.Version $n.Infra
        RequireLanProxyAt $n.Infra $false
        RequireHelpPageFile $app.Version
    }
    Phase 'repair-resume' {
        ResumeLanProxy
        RequireLanProxyAt $n.Infra $true
        # A real kill, with no help from the product: the broken release has migrated
        # the database and is failing health when its Setup disappears.
        $stage = StageOf $broken 'setup'
        $killed = Start-Process -FilePath (Join-Path $stage $broken.SetupName) -ArgumentList @('--quiet-upgrade', ('"' + $stage + '"'), '--result', ('"' + (Join-Path $out 'killed-result.json') + '"')) -PassThru -WindowStyle Hidden
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
        # Until commit the Repair task stays on the from-pair's engine (plan 8.1).
        RequireRepairTask $n.Infra
        $result = RunSetupExpecting (EngineOf $n.Infra) @('--repair') (Join-Path $out 'resume-result.json') 'REPAIRED' 0
        if (!$result.RolledBack) { throw 'Repair did not roll back the interrupted upgrade' }
        $script:resumeSeconds = $result.Seconds
    }
    Phase 'resumed' {
        RequireInstalled $app.Version $app.Schema $n.Infra $n.Version $n.Infra
        RequireLanProxyAt $n.Infra $true
    }
    Phase 'upgrade' {
        $move = InstallThroughUpdater $n1 'setup' 'upgrade' {
            RequireAccessBoundaries $n.Infra $app.Version
            # An installation from before Users could read the binaries: the Setup track
            # has to bring it to the current ACL, which the check after it requires.
            & icacls.exe $programFiles /remove:g '*S-1-5-32-545' /Q | Out-Null
            if ($LASTEXITCODE -ne 0 -or ((EffectiveRights $programFiles @('S-1-5-21-1-2-3-1001', 'S-1-5-32-545')) -ne 0)) { throw 'Could not recreate the older binary ACL' }
        }
        RequireMove $move.Result 'UPGRADED' $app $n1 $false
        $script:upgradeSeconds = $move.Result.Seconds
    }
    Phase 'upgraded' {
        # The new Updater reads what the Setup left and tells the parent, whose session
        # a real upgrade leaves signed in.
        $status = UpdatesStatus
        if ($status.phase -ne 'healthy' -or $status.lastRequest.action -ne 'install' -or $status.lastRequest.state -ne 'succeeded') { throw "After the install the Updater reports $($status.phase)/$($status.lastRequest.action)/$($status.lastRequest.state)" }
        RequireStageGone $n1
        RequireInstalled $n1.Version $n1.Schema $n1.Infra $app.Version $n.Infra
        RequireAccessBoundaries $n1.Infra $n1.Version
        RequireLanProxyAt $n1.Infra $true
        RequireLanHelpOverHttps $n1.Version
    }
    Phase 'refuse-replay' {
        # Releases this installation has moved past, on both tracks: not an update for it.
        $stage = StageOf $n 'setup'
        $null = RunSetupExpecting (Join-Path $stage $n.SetupName) @('--quiet-upgrade', $stage) (Join-Path $out 'refuse-downgrade-result.json') 'RELEASE_NOT_NEWER' 2
        $stage = StageOf $n1 'setup'
        $null = RunSetupExpecting (Join-Path $stage $n1.SetupName) @('--quiet-upgrade', $stage) (Join-Path $out 'refuse-same-result.json') 'RELEASE_NOT_NEWER' 2
        $null = RunSetupExpecting (EngineOf $n1.Infra) @('--apply-app', (StageOf $app 'app')) (Join-Path $out 'refuse-app-replay-result.json') 'RELEASE_NOT_NEWER' 2
        RequireInstalled $n1.Version $n1.Schema $n1.Infra $app.Version $n.Infra
    }
    # Reinstall from here belongs to n1; removal runs its installed, payload-free engine.
    # The trees removed include the previous pair's, which an app update made.
    $setupPath = $n1.Setup
    Phase 'uninstall-keep' {
        $removed = RunSetup @('--quiet-uninstall', '--data', 'keep') (Join-Path $out 'uninstall-keep-result.json') @() (EngineOf $n1.Infra)
        if ($removed.Result.DataPreserved -ne $true) { throw 'Keep-data removal did not report preserved data' }
    }
    Phase 'kept' {
        RequireRemovedServices
        if (!(Test-Path -LiteralPath $programData)) { throw "$programData was removed by a keep-data uninstall" }
        if (Test-Path -LiteralPath $programFiles) { throw "$programFiles remains after a keep-data uninstall" }
        RequireNoRepairTask
        if (!(Test-Path -LiteralPath (Join-Path $updaterRoot 'state.json'))) { throw 'A keep-data uninstall lost the update mode' }
        if (Test-Path -LiteralPath (Join-Path $updaterRoot 'staging')) { throw 'A keep-data uninstall kept downloaded releases' }
        if (LanRule) { throw 'A keep-data uninstall left the firewall rule' }
        if (!(Test-Path -LiteralPath (Join-Path $lanRoot 'data'))) { throw 'A keep-data uninstall lost the home-network authority' }
        if ((Get-Content -LiteralPath (Join-Path $lanRoot 'owner.json') -Raw | ConvertFrom-Json).Phase -ne 'disabled') { throw 'A keep-data uninstall left the home network enabled' }
        if (Test-Path -LiteralPath "$programFiles.lan-uninstall.json") { throw 'The home-network removal receipt remains' }
    }
    $reinstall = $null
    Phase 'reinstall' { $script:reinstall = RunSetup @('--quiet-install', '--updates', 'notify') (Join-Path $out 'reinstall-result.json') @('--setup-link', 'stdout') }
    Phase 'persisted' {
        RequireInstalledAndReady $reinstall.Result
        RequireServiceConfiguration
        if ($reinstall.Stdout.Trim()) { throw 'Reinstall offered a new setup link over kept data' }
        if ((SetupState) -ne 'complete') { throw 'Kept family is missing after reinstall' }
        RequireInstalled $n1.Version $n1.Schema $n1.Infra
    }
    Phase 'uninstall' { RunSetup @('--quiet-uninstall', '--data', 'delete') (Join-Path $out 'uninstall-result.json') @() (EngineOf $n1.Infra) | Out-Null }
    Phase 'removed' { RequireNothingLeft }
    # What someone with an older release gets from the landing page: the downloaded Setup,
    # run by hand. Its infrastructure is the installed one byte for byte, so only its app
    # moves (plan 8.2): the Updater keeps running and infra\ is not touched. The older
    # install's setup link expired meanwhile.
    Phase 'setup-over-older' {
        $older = RunSetup @('--quiet-install', '--updates', 'notify') (Join-Path $out 'older-install-result.json') @('--setup-link', 'stdout') $n.Setup
        $script:origin = ([Uri]$older.Result.Url).GetLeftPart([UriPartial]::Authority)
        $capability = Join-Path $programData 'Secrets\setup-capability.json'
        Stop-Service -Name $runtime
        $record = Get-Content -LiteralPath $capability -Raw | ConvertFrom-Json
        $record.expiresAt = 1
        [IO.File]::WriteAllBytes($capability, [Text.UTF8Encoding]::new($false).GetBytes(($record | ConvertTo-Json -Compress -Depth 5)))
        Start-Service -Name $runtime
        if ((SetupState) -ne 'closed') { throw 'An expired setup link still reports setup as open' }
        $process = UpdaterProcess
        $infra = InfraSnapshot $n.Infra
        $moved = RunSetup @('--quiet-install', '--updates', 'notify') (Join-Path $out 'setup-over-older-result.json') @('--setup-link', 'stdout') $app.Setup
        if ((UpdaterProcess) -ne $process) { throw 'A Setup on the installed infrastructure restarted the Updater' }
        if ((InfraSnapshot $n.Infra) -cne $infra) { throw 'A Setup on the installed infrastructure changed its tree' }
        if ((Health).version -ne $app.Version) { throw "Setup over an older release left $((Health).version) running" }
        if ((SetupState) -ne 'ready') { throw 'Setup over an older release did not reopen first-family setup' }
        $match = [regex]::Match($moved.Stdout.Trim(), '^(http://127\.0\.0\.1:\d+/)#setup=([A-Za-z0-9_-]{43})$')
        if (!$match.Success) { throw 'Setup over an older release did not hand back a setup link' }
        if ($moved.Stdout.Trim() -eq $older.Stdout.Trim()) { throw 'Setup over an older release handed back the expired link' }
        $suffix = [Guid]::NewGuid().ToString('N').Substring(0, 10)
        $script:account = [pscustomobject]@{ Family="Acceptance family $suffix"; Name="acceptance-$suffix"; Password=[Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(24)) }
        $body = Json @{ familyName=$account.Family; name=$account.Name; password=$account.Password; confirmation=$account.Password }
        Invoke-RestMethod -Method Post -Uri "$origin/api/setup" -Body $body -ContentType 'application/json' -SessionVariable created -TimeoutSec 60 `
            -Headers @{ Origin=$origin; 'X-Setup-Token'=$match.Groups[2].Value } | Out-Null
        $script:familyId = RequireSignedInAccount $created
        $older = $null; $moved = $null; $match = $null; $body = $null
        RequireInstalled $app.Version $app.Schema $n.Infra $n.Version $n.Infra
        $null = RunSetupExpecting $n.Setup @('--quiet-install', '--updates', 'notify') (Join-Path $out 'refuse-older-setup-result.json') 'INSTALL_DOWNGRADE_REFUSED' 1
        RequireInstalled $app.Version $app.Schema $n.Infra $n.Version $n.Infra
    }
    # Removal after an app-only move: the previous pair shares the infrastructure.
    Phase 'uninstall-moved' { RunSetup @('--quiet-uninstall', '--data', 'delete') (Join-Path $out 'uninstall-moved-result.json') @() (EngineOf $n.Infra) | Out-Null }
    Phase 'removed-moved' { RequireNothingLeft }
    $state = 'Passed'
}
catch { $state = 'Failed'; Write-Output "FAILED $($_.Exception.Message)"; throw }
finally {
    $account = $null
    Remove-Item -LiteralPath $stageRoot -Recurse -Force -ErrorAction SilentlyContinue
    try { Collect } catch { Write-Output "COLLECTION_FAILED $($_.Exception.Message)" }
    [ordered]@{ State=$state; FailedPhase=$failedPhase; Phases=$phases; StartSeconds=$startSeconds; AppUpdateSeconds=$appUpdateSeconds; AppRollbackSeconds=$appRollbackSeconds; UpgradeSeconds=$upgradeSeconds; RollbackSeconds=$rollbackSeconds; RepairSeconds=$repairSeconds; ResumeSeconds=$resumeSeconds; LiveCheck=$liveCheck; TotalSeconds=[math]::Round(((Get-Date)-$started).TotalSeconds, 1) } |
        ConvertTo-Json -Depth 4 | Set-Content -Encoding utf8NoBOM (Join-Path $out 'summary.json')
}
