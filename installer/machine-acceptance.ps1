# Real machine acceptance for a built Yahad.Setup.exe. Every check is what a
# person or administrator observes (Setup results, services, HTTP answers, the
# account they created), so it keeps gating when the mechanism behind it changes:
#   install -> ready -> create the first family over the app's own HTTP API ->
#   uninstall keeping data -> reinstall -> the same account signs in ->
#   uninstall deleting data -> nothing remains.
# Changes services and machine folders, so it runs only on a disposable
# administrator machine (CI), never a family PC. Diagnostics and summary.json
# are written even on failure. The one-time setup link and the generated
# password live only in this process's memory; neither is written or printed.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Setup,
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
# Present in today's full profile; the planned single-updater design changes
# this list, and that change must be made here deliberately.
$installedServices = @($runtime, 'Yahad.Acceptance.Authority', 'Yahad.Acceptance.Authority.RecoveryA', 'Yahad.Acceptance.Authority.RecoveryB')
$runningServices = @($runtime, 'Yahad.Acceptance.Authority')
$account = $null
$origin = $null

function Phase([string]$Name, [scriptblock]$Body) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    try { & $Body }
    catch { if (!$script:failedPhase) { $script:failedPhase = $Name }; throw }
    finally { $phases[$Name] = [math]::Round($timer.Elapsed.TotalSeconds, 1); Write-Output ("PHASE {0} {1}s" -f $Name, $phases[$Name]) }
}
# Returns the parsed result file and anything Setup wrote to standard output.
# Standard output is returned to the caller, never echoed.
function RunSetup([string[]]$Arguments, [string]$Result) {
    $start = [Diagnostics.ProcessStartInfo]::new($setupPath)
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    foreach ($argument in $Arguments + @('--result', $Result)) { $start.ArgumentList.Add($argument) }
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
function SetupState { (Invoke-RestMethod -Uri "$origin/api/setup/status" -TimeoutSec 30).state }
function Json($Value) { $Value | ConvertTo-Json -Compress }
function RequireSignedInAccount($Session) {
    $me = Invoke-RestMethod -Uri "$origin/api/me" -WebSession $Session -TimeoutSec 30
    if ($me.user.name -ne $account.Name -or $me.user.role -ne 'parent') { throw 'Signed-in account is not the acceptance parent' }
    return $me.user.familyId
}
function RequireRemovedServices { if ((YahadServices).Count) { throw ('Services remain after removal: ' + ((YahadServices).Name -join ',')) } }
function Collect {
    YahadServices | Select-Object Name, Status, StartType | ConvertTo-Json | Set-Content -Encoding utf8NoBOM (Join-Path $out 'services.json')
    $since = $started.AddMinutes(-1)
    # The Yahad source exists only after a startup failure wrote to it; a
    # ProviderName filter for an unregistered source throws, so filter after.
    Get-WinEvent -FilterHashtable @{ LogName='Application'; StartTime=$since } -ErrorAction SilentlyContinue |
        Where-Object ProviderName -eq 'Yahad' | ForEach-Object { [ordered]@{ Time=$_.TimeCreated.ToString('o'); Id=$_.Id; Data=@($_.Properties | ForEach-Object Value) } } |
        ConvertTo-Json -Depth 4 | Set-Content -Encoding utf8NoBOM (Join-Path $out 'yahad-events.json')
    Get-WinEvent -FilterHashtable @{ LogName='System'; ProviderName='Service Control Manager'; StartTime=$since } -ErrorAction SilentlyContinue |
        Where-Object { $_.Message -like '*Yahad*' } | ForEach-Object { '{0:o} {1} {2}' -f $_.TimeCreated, $_.Id, ($_.Message -replace "`r?`n", ' ') } |
        Set-Content -Encoding utf8NoBOM (Join-Path $out 'scm-events.txt')
    Get-ChildItem -LiteralPath $env:ProgramFiles -Force -Filter 'Yahad.FirstInstall.*.json' -ErrorAction SilentlyContinue |
        ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $out $_.Name) }
}

try {
    Phase 'preflight' {
        $principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
        $policy = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
        Write-Output ("MACHINE os={0} admin={1} EnableLUA={2} ConsentPromptBehaviorAdmin={3}" -f [Environment]::OSVersion.Version, $principal.IsInRole('Administrators'), $policy.EnableLUA, $policy.ConsentPromptBehaviorAdmin)
        if ((YahadServices).Count -or (Test-Path -LiteralPath $programData) -or (Test-Path -LiteralPath $programFiles)) { throw 'Machine is not clean: an existing Yahad installation is present' }
    }
    $install = $null
    Phase 'install' { $script:install = RunSetup @('--quiet-install', '--updates', 'notify', '--setup-link', 'stdout') (Join-Path $out 'install-result.json') }
    Phase 'ready' {
        RequireInstalledAndReady $install.Result
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
    Phase 'uninstall-keep' {
        $removed = RunSetup @('--quiet-uninstall', '--data', 'keep') (Join-Path $out 'uninstall-keep-result.json')
        if ($removed.Result.DataPreserved -ne $true) { throw 'Keep-data removal did not report preserved data' }
    }
    Phase 'kept' {
        RequireRemovedServices
        if (!(Test-Path -LiteralPath $programData)) { throw "$programData was removed by a keep-data uninstall" }
    }
    $reinstall = $null
    Phase 'reinstall' { $script:reinstall = RunSetup @('--quiet-install', '--updates', 'notify', '--setup-link', 'stdout') (Join-Path $out 'reinstall-result.json') }
    Phase 'persisted' {
        RequireInstalledAndReady $reinstall.Result
        if ($reinstall.Stdout.Trim()) { throw 'Reinstall offered a new setup link over kept data' }
        if ((SetupState) -ne 'complete') { throw 'Kept family is missing after reinstall' }
        $body = Json @{ name=$account.Name; password=$account.Password }
        Invoke-RestMethod -Method Post -Uri "$origin/api/login" -Body $body -ContentType 'application/json' -SessionVariable signedIn -TimeoutSec 60 -Headers @{ Origin=$origin } | Out-Null
        if ((RequireSignedInAccount $signedIn) -ne $familyId) { throw 'Signed-in account belongs to a different family after reinstall' }
    }
    Phase 'uninstall' { RunSetup @('--quiet-uninstall', '--data', 'delete') (Join-Path $out 'uninstall-result.json') | Out-Null }
    Phase 'removed' {
        RequireRemovedServices
        foreach ($path in $programData, $programFiles) { if (Test-Path -LiteralPath $path) { throw "$path remains after removal" } }
    }
    $state = 'Passed'
}
catch { $state = 'Failed'; Write-Output "FAILED $($_.Exception.Message)"; throw }
finally {
    $account = $null
    try { Collect } catch { Write-Output "COLLECTION_FAILED $($_.Exception.Message)" }
    [ordered]@{ State=$state; FailedPhase=$failedPhase; Phases=$phases; TotalSeconds=[math]::Round(((Get-Date)-$started).TotalSeconds, 1);
        NotCovered=[ordered]@{
            'upgrade-n-to-n+1'='No local upgrade path: Setup over a committed install keeps the installed version, and the updater accepts only stable releases of NehoraiHadad/yahad from api.github.com.'
            'broken-n+1-rollback'='Depends on the upgrade path above.'
        } } |
        ConvertTo-Json -Depth 4 | Set-Content -Encoding utf8NoBOM (Join-Path $out 'summary.json')
}
