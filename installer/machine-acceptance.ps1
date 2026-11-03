# Real machine acceptance for a built Yahad.Setup.exe: unattended install,
# readiness, then full removal. Changes services and machine folders, so it
# runs only on a disposable administrator machine (CI), never a family PC.
# Diagnostics are collected even on failure; the setup token is never written.
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
$programData = Join-Path $env:ProgramData 'Yahad.FirstInstall'
$programFiles = Join-Path $env:ProgramFiles 'Yahad.FirstInstall'

function Phase([string]$Name, [scriptblock]$Body) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    try { & $Body } finally { $phases[$Name] = [math]::Round($timer.Elapsed.TotalSeconds, 1); Write-Output ("PHASE {0} {1}s" -f $Name, $phases[$Name]) }
}
function RunSetup([string[]]$Arguments, [string]$Result) {
    $process = Start-Process -FilePath $setupPath -ArgumentList ($Arguments + @('--result', $Result)) -PassThru -Wait
    if (!(Test-Path -LiteralPath $Result)) { throw "Setup exited $($process.ExitCode) without a result" }
    $value = Get-Content -LiteralPath $Result -Raw | ConvertFrom-Json
    Write-Host ("SETUP {0} ok={1} code={2} exit={3}" -f $Arguments[0], $value.Ok, $value.Code, $process.ExitCode)
    if (!$value.Ok -or $process.ExitCode -ne 0) { throw "Setup $($Arguments[0]) failed: $($value.Code)" }
    return $value
}
function YahadServices { @(Get-Service -Name 'Yahad*' -ErrorAction SilentlyContinue) }
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
    Phase 'install' { $script:install = RunSetup @('--quiet-install', '--updates', 'notify') (Join-Path $out 'install-result.json') }
    Phase 'ready' {
        foreach ($name in 'Yahad.Acceptance', 'Yahad.Acceptance.Authority') {
            $service = Get-Service -Name $name
            if ($service.Status -ne 'Running') { throw "$name is $($service.Status)" }
        }
        $origin = ([Uri]$install.Url).GetLeftPart([UriPartial]::Authority)
        $response = Invoke-WebRequest -Uri "$origin/health/ready" -UseBasicParsing -TimeoutSec 30
        if ($response.StatusCode -ne 200) { throw "health/ready returned $($response.StatusCode)" }
    }
    Phase 'uninstall' { RunSetup @('--quiet-uninstall', '--data', 'delete') (Join-Path $out 'uninstall-result.json') | Out-Null }
    Phase 'removed' {
        if ((YahadServices).Count) { throw 'Services remain after removal' }
        foreach ($path in $programData, $programFiles) { if (Test-Path -LiteralPath $path) { throw "$path remains after removal" } }
    }
    $state = 'Passed'
}
catch { $state = 'Failed'; Write-Output "FAILED $($_.Exception.Message)"; throw }
finally {
    try { Collect } catch { Write-Output "COLLECTION_FAILED $($_.Exception.Message)" }
    [ordered]@{ State=$state; Phases=$phases; TotalSeconds=[math]::Round(((Get-Date)-$started).TotalSeconds, 1) } |
        ConvertTo-Json | Set-Content -Encoding utf8NoBOM (Join-Path $out 'summary.json')
}
