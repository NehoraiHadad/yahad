# CI only. Builds and signs the releases machine acceptance upgrades through, from
# throwaway commits made in worktrees inside the job; the patches below never exist
# in the committed tree, and the product has no switch that imitates them:
#   n/       HEAD as committed.
#   n1/      version +1, schema +1 with a real migration.
#   broken/  version +2, schema +2 with a real migration; /health/ready answers 503.
#   refused/ releases that must be refused: foreign key, no signature, tampered.
# -Key is a directory with key.pem and key.spki made for this job by
# scripts/windows-release-key.mjs. It is never uploaded; only key.spki reaches a build.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Key,
    [Parameter(Mandatory=$true)][string]$NodeDirectory,
    [Parameter(Mandatory=$true)][string]$NpmCache,
    [Parameter(Mandatory=$true)][string]$VendorDirectory,
    [Parameter(Mandatory=$true)][string]$DotNet,
    [Parameter(Mandatory=$true)][string]$PackageCache,
    [Parameter(Mandatory=$true)][string]$Output
)
$ErrorActionPreference = 'Stop'
if (!$env:RUNNER_TEMP) { throw 'CI only: this script makes throwaway commits and needs RUNNER_TEMP' }
$repo = Split-Path $PSScriptRoot -Parent
Push-Location -LiteralPath $repo
$worktrees = [Collections.Generic.List[string]]::new()
try {
    $keyDir = (Resolve-Path -LiteralPath $Key).Path
    $spki = Join-Path $keyDir 'key.spki'
    $node = Join-Path (Resolve-Path -LiteralPath $NodeDirectory).Path 'node.exe'
    $npm = Join-Path (Split-Path $node -Parent) 'node_modules/npm/bin/npm-cli.js'
    $tool = Join-Path $repo 'scripts/windows-release-key.mjs'
    $inputs = @{
        NodeDirectory = Split-Path $node -Parent
        NpmCache = (Resolve-Path -LiteralPath $NpmCache).Path
        VendorDirectory = (Resolve-Path -LiteralPath $VendorDirectory).Path
        DotNet = (Resolve-Path -LiteralPath $DotNet).Path
        PackageCache = $PSCmdlet.SessionState.Path.GetUnresolvedProviderPathFromPSPath($PackageCache)
        ReleaseKey = $spki
    }
    New-Item -ItemType Directory -Force -Path $Output | Out-Null
    $set = (Resolve-Path -LiteralPath $Output).Path
    function Run([string]$Program, [string[]]$Arguments) {
        & $Program @Arguments | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "$Program failed: $($Arguments -join ' ')" }
    }
    function Sign([string]$PrivateKey, [string]$Manifest) { Run $node @($tool, 'sign', $PrivateKey, $Manifest) }
    function ReleaseManifest([string]$Directory) {
        $found = @(Get-ChildItem -LiteralPath $Directory -Filter 'yahad-windows-*.json')
        if ($found.Count -ne 1) { throw "Expected one release manifest in $Directory" }
        return $found[0]
    }
    function WriteFile([string]$Path, [string]$Text) { [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false)) }
    # Exactly one match, or the fixture no longer says what this script believes.
    function ReplaceOnce([string]$Path, [string]$Anchor, [string]$Replacement) {
        $text = [IO.File]::ReadAllText($Path)
        $parts = $text.Split([string[]]@($Anchor), [StringSplitOptions]::None)
        if ($parts.Count -ne 2) { throw "Expected exactly one '$Anchor' in $Path, found $($parts.Count - 1)" }
        WriteFile $Path ($parts[0] + $Replacement + $parts[1])
    }
    function BumpVersion([string]$Version) {
        $parts = $Version.Split('.')
        if ($parts.Count -ne 3) { throw "Unexpected version $Version" }
        return '{0}.{1}.{2}' -f $parts[0], $parts[1], ([int]$parts[2] + 1)
    }
    # One more real schema: declared, migrated and refused by the release before it.
    function BumpSchema([string]$Tree, [int]$From, [string]$StoreAnchor) {
        $schema = Join-Path $Tree 'src/database-schema.ts'
        ReplaceOnce $schema "DATABASE_SCHEMA = $From;" "DATABASE_SCHEMA = $($From + 1);"
        $declared = [regex]::Match([IO.File]::ReadAllText($schema), 'DATABASE_MIGRATIONS_FROM = \[([^\]]*)\]')
        if (!$declared.Success) { throw 'DATABASE_MIGRATIONS_FROM not found' }
        ReplaceOnce $schema $declared.Value "DATABASE_MIGRATIONS_FROM = [$($declared.Groups[1].Value), $From]"
        $migration = "this.db.exec('BEGIN IMMEDIATE; CREATE TABLE IF NOT EXISTS ci_upgrade_marker_$($From + 1)(id INTEGER PRIMARY KEY); UPDATE schema_version SET version=$($From + 1) WHERE version=$From; COMMIT;');"
        ReplaceOnce (Join-Path $Tree 'src/store.ts') $StoreAnchor "$StoreAnchor $migration"
        return $migration
    }
    function NewVariant([string]$Name, [string]$Base, [scriptblock]$Patch) {
        $tree = Join-Path $env:RUNNER_TEMP "wt-$Name"
        Run git @('worktree', 'add', '--detach', $tree, $Base)
        $worktrees.Add($tree)
        Push-Location -LiteralPath $tree
        try {
            $current = (Get-Content package.json -Raw | ConvertFrom-Json).version
            Run $node @($npm, 'version', (BumpVersion $current), '--no-git-tag-version', '--ignore-scripts')
            $result = & $Patch $tree
            Run git @('-c', 'user.name=ci', '-c', 'user.email=ci@invalid', 'commit', '-q', '-am', "CI fixture: $Name")
            Run $node @($npm, 'ci', '--offline', '--cache', $inputs.NpmCache, '--no-audit', '--no-fund')
            & (Join-Path $tree 'installer/package.ps1') @inputs -Output (Join-Path $set $Name) | Out-Host
            $commit = (& git rev-parse HEAD).Trim()
        } finally { Pop-Location }
        Sign (Join-Path $keyDir 'key.pem') (ReleaseManifest (Join-Path $set "$Name/release")).FullName
        return [pscustomobject]@{ Commit=$commit; Patch=$result }
    }

    $schema = [int][regex]::Match([IO.File]::ReadAllText((Join-Path $repo 'src/database-schema.ts')), 'DATABASE_SCHEMA = (\d+);').Groups[1].Value
    if ($schema -lt 1) { throw 'DATABASE_SCHEMA not found' }

    & (Join-Path $repo 'installer/package.ps1') @inputs -Output (Join-Path $set 'n') | Out-Host
    Sign (Join-Path $keyDir 'key.pem') (ReleaseManifest (Join-Path $set 'n/release')).FullName

    $n1 = NewVariant 'n1' 'HEAD' { param($Tree) BumpSchema $Tree $schema 'migrateChildAccess(this.db);' }
    $null = NewVariant 'broken' $n1.Commit {
        param($Tree)
        $null = BumpSchema $Tree ($schema + 1) $n1.Patch
        $ready = 'return { ok: true, version: String(info.version), databaseSchema: DATABASE_SCHEMA };'
        ReplaceOnce (Join-Path $Tree 'src/server.ts') $ready "if (info) return reply.code(503).send({ ok: false }); $ready"
    }

    # Well-formed releases of the next version, each wrong in one way only. No
    # Setup travels with them: the signature decides before anything else is read.
    $good = ReleaseManifest (Join-Path $set 'n1/release')
    $refused = Join-Path $set 'refused'
    foreach ($case in 'foreign', 'unsigned', 'tampered') { New-Item -ItemType Directory -Force -Path (Join-Path $refused $case) | Out-Null }
    $foreignKey = Join-Path $env:RUNNER_TEMP 'foreign-release-key'
    Run $node @($tool, 'keygen', $foreignKey)
    try {
        $foreignId = (Get-FileHash -LiteralPath (Join-Path $foreignKey 'key.spki') -Algorithm SHA256).Hash.ToLowerInvariant().Substring(0, 16)
        $trustedId = (Get-FileHash -LiteralPath $spki -Algorithm SHA256).Hash.ToLowerInvariant().Substring(0, 16)
        $foreign = Join-Path $refused "foreign/$($good.Name)"
        Copy-Item -LiteralPath $good.FullName -Destination $foreign
        ReplaceOnce $foreign "`"keyId`":`"$trustedId`"" "`"keyId`":`"$foreignId`""
        Sign (Join-Path $foreignKey 'key.pem') $foreign
    } finally { Remove-Item -LiteralPath $foreignKey -Recurse -Force -ErrorAction SilentlyContinue }
    Copy-Item -LiteralPath $good.FullName -Destination (Join-Path $refused "unsigned/$($good.Name)")
    $tampered = Join-Path $refused "tampered/$($good.Name)"
    Copy-Item -LiteralPath $good.FullName -Destination $tampered
    Copy-Item -LiteralPath ($good.FullName + '.sig') -Destination ($tampered + '.sig')
    # Still valid JSON and still a valid manifest; only the signature can tell.
    $text = [IO.File]::ReadAllText($tampered)
    $created = [regex]::Match($text, '"createdUnix":(\d+)')
    if (!$created.Success) { throw 'createdUnix not found' }
    ReplaceOnce $tampered $created.Value ('"createdUnix":' + ([long]$created.Groups[1].Value + 1))
    Write-Output "UPGRADE_SET_BUILT $set"
} finally {
    foreach ($tree in $worktrees) { & git worktree remove --force $tree }
    Pop-Location
}
