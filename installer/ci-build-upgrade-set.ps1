# CI only. Builds and signs the format-2 releases (docs/WINDOWS-APP-PACKAGE-PLAN.md
# 5.1) that machine acceptance moves through, from throwaway commits made in worktrees
# inside the job; the patches below never exist in the committed tree, and the product
# has no switch that imitates them. With A, S and I the app version, schema and
# infrastructure version of HEAD:
#   n/          HEAD as committed: (I, A), schema S.
#   app/        (I, A+1), schema S+1 with a real migration: the app track.
#   unhealthy/  from app/: (I, A+2), schema S+2, migrates and then exits before it serves:
#               app-track rollback. The Runtime stops on its own, so the engine's health
#               gate ends at once instead of waiting out its window.
#   n1/         from app/: (I+1, A+3), schema S+2: the Setup track.
#   broken/     from n1/: (I+1, A+4), schema S+3, 503: Setup-track rollback from (I, A+1),
#               and the upgrade killed mid-way that repair undoes.
#   contract/<A+1>/  app/'s release re-signed with requiresContract above its own
#               infrastructure's range: APP_NEEDS_INFRASTRUCTURE (plan 9.1).
#   mismatch/<A+3>/  n1/'s release re-signed naming another Setup's bytes, beside n1/'s
#               own Setup: RELEASE_SETUP_MISMATCH.
#   refused/<track>/<case>/<version>/  well-formed stages wrong only in their signature:
#               foreign key, no signature, tampered. app/ is from app/, setup/ from n1/.
# Each of these is a stage as the Updater writes one (plan 9.3): a directory named by its
# version holding exactly that track's three files; --apply-app reads the version from it.
# Every build shares one -InfraCache, as releases do (plan 10): app/ and unhealthy/
# reuse n/'s infrastructure build and broken/ reuses n1/'s, so I and I+1 are each built
# once. Each variant's LAN help page names its app version, so acceptance can tell
# which app wrote the page it serves. upgrade-set.json names every version.
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
    # A new directory: every infrastructure in it is built in this job, with this job's key.
    [Parameter(Mandatory=$true)][string]$InfraCache,
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
    $cache = $PSCmdlet.SessionState.Path.GetUnresolvedProviderPathFromPSPath($InfraCache)
    # A cache from elsewhere could hold an infrastructure compiled for another key.
    if (Test-Path -LiteralPath $cache) { throw "InfraCache $cache already exists; this job builds its own" }
    New-Item -ItemType Directory -Path $cache | Out-Null
    New-Item -ItemType Directory -Force -Path $Output | Out-Null
    $set = (Resolve-Path -LiteralPath $Output).Path
    function Run([string]$Program, [string[]]$Arguments) {
        & $Program @Arguments | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "$Program failed: $($Arguments -join ' ')" }
    }
    function Sign([string]$PrivateKey, [string]$Manifest) { Run $node @($tool, 'sign', $PrivateKey, $Manifest) }
    function WriteFile([string]$Path, [string]$Text) { [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false)) }
    # Exactly one match, or the fixture no longer says what this script believes.
    function ReplaceOnce([string]$Path, [string]$Anchor, [string]$Replacement) {
        $text = [IO.File]::ReadAllText($Path)
        $parts = $text.Split([string[]]@($Anchor), [StringSplitOptions]::None)
        if ($parts.Count -ne 2) { throw "Expected exactly one '$Anchor' in $Path, found $($parts.Count - 1)" }
        WriteFile $Path ($parts[0] + $Replacement + $parts[1])
    }
    # One scalar member of a manifest, replaced in its raw bytes so nothing else moves.
    function ReplaceMember([string]$Path, [string]$Name, [string]$ValuePattern, [string]$Value) {
        $text = [IO.File]::ReadAllText($Path)
        $found = [regex]::Matches($text, '"' + $Name + '"\s*:\s*' + $ValuePattern)
        if ($found.Count -ne 1) { throw "Expected exactly one $Name in $Path, found $($found.Count)" }
        WriteFile $Path ($text.Substring(0, $found[0].Index) + "`"$Name`":$Value" + $text.Substring($found[0].Index + $found[0].Length))
    }
    function BumpVersion([string]$Version, [int]$By = 1) {
        $parts = $Version.Split('.')
        if ($parts.Count -ne 3) { throw "Unexpected version $Version" }
        return '{0}.{1}.{2}' -f $parts[0], $parts[1], ([int]$parts[2] + $By)
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
    # The app migrates the database, then exits before it ever serves: the Runtime
    # service stops on its own during startup.
    function MakeExitAfterMigrating([string]$Tree) {
        ReplaceOnce (Join-Path $Tree 'src/main.ts') "  stage = 'listen';" "  process.exit(startupExit.app);`n  stage = 'listen';"
    }
    # The app starts, migrates and serves, but its readiness never passes.
    function MakeUnhealthy([string]$Tree) {
        $ready = 'return { ok: true, version: String(info.version), databaseSchema: DATABASE_SCHEMA };'
        ReplaceOnce (Join-Path $Tree 'src/server.ts') $ready "if (info) return reply.code(503).send({ ok: false }); $ready"
    }
    # The help page template names the app it came from; one mark per page.
    function MarkHelpPage([string]$Tree, [string]$Version) {
        $page = Join-Path $Tree 'deploy/lan-help.html'
        $mark = "<meta name=`"yahad-ci-app`" content=`"$Version`">"
        $marked = [regex]::Matches([IO.File]::ReadAllText($page), '<meta name="yahad-ci-app" content="[^"]*">')
        if ($marked.Count -gt 1) { throw "$page carries $($marked.Count) app marks" }
        if ($marked.Count -eq 1) { ReplaceOnce $page $marked[0].Value $mark }
        else { ReplaceOnce $page '<meta name="referrer" content="no-referrer">' "<meta name=`"referrer`" content=`"no-referrer`">`n  $mark" }
    }
    # Packages a clean checkout into <set>/<Name>/ against the shared cache, then signs
    # its manifest, read by its exact name.
    function Package([string]$Tree, [string]$Name, [string]$Version, [string]$InfraSource) {
        & (Join-Path $Tree 'installer/package.ps1') @inputs -InfraCache $cache -InfraSource $InfraSource -Output (Join-Path $set $Name) | Out-Host
        $directory = Join-Path $set "$Name/release"
        $manifest = Join-Path $directory "yahad-windows-release-$Version.json"
        $value = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
        if ($value.format -ne 2 -or $value.product -ne 'yahad-windows-release' -or $value.version -ne $Version) { throw "$manifest is not the format-2 release $Version" }
        foreach ($file in $value.app.archive.name, $value.infrastructure.setup.name) {
            if (!(Test-Path -LiteralPath (Join-Path $directory $file) -PathType Leaf)) { throw "$Name/release has no $file" }
        }
        Sign (Join-Path $keyDir 'key.pem') $manifest
        return [pscustomobject]@{ Name=$Name; Version=$Version; Directory=$directory; Manifest=$manifest; Value=$value }
    }
    # A variant is a throwaway commit on $Base at $Version: the app moves, the
    # infrastructure only when asked. Its Setup is published from $InfraSource, a
    # checkout at the infrastructure's commit (decision 7); $null means this one.
    function NewVariant([string]$Name, [string]$Base, [string]$Version, [bool]$Infrastructure, [scriptblock]$Patch, [string]$InfraSource) {
        $tree = Join-Path $env:RUNNER_TEMP "wt-$Name"
        Run git @('worktree', 'add', '--detach', $tree, $Base)
        $worktrees.Add($tree)
        Push-Location -LiteralPath $tree
        try {
            Run $node @($npm, 'version', $Version, '--no-git-tag-version', '--ignore-scripts')
            if ($Infrastructure) {
                $props = Join-Path $tree 'installer/Directory.Build.props'
                $infrastructure = [regex]::Matches([IO.File]::ReadAllText($props), '<YahadInfrastructureVersion>([^<]*)</YahadInfrastructureVersion>')
                if ($infrastructure.Count -ne 1) { throw "Expected one YahadInfrastructureVersion in $props" }
                ReplaceOnce $props $infrastructure[0].Value "<YahadInfrastructureVersion>$(BumpVersion $infrastructure[0].Groups[1].Value)</YahadInfrastructureVersion>"
            }
            $result = & $Patch $tree
            MarkHelpPage $tree $Version
            Run git @('-c', 'user.name=ci', '-c', 'user.email=ci@invalid', 'commit', '-q', '-am', "CI fixture: $Name")
            Run $node @($npm, 'ci', '--offline', '--cache', $inputs.NpmCache, '--no-audit', '--no-fund')
            $commit = (& git rev-parse HEAD).Trim()
        } finally { Pop-Location }
        $release = Package $tree $Name $Version $(if ($InfraSource) { $InfraSource } else { $tree })
        return [pscustomobject]@{ Commit=$commit; Tree=$tree; Patch=$result; Release=$release }
    }
    # Two releases on one infrastructure must name it identically, or selection
    # refuses the second with INFRA_IDENTITY_MISMATCH (plan 8.1).
    function RequireSameInfrastructure($Release, $Of) {
        foreach ($name in 'version', 'contractMin', 'contractMax', 'sourceCommit', 'treeSha256') {
            if ($Release.Value.infrastructure.$name -ne $Of.Value.infrastructure.$name) { throw "$($Release.Name) has infrastructure $name $($Release.Value.infrastructure.$name), $($Of.Name) has $($Of.Value.infrastructure.$name)" }
        }
    }
    # A stage holds exactly its track's three files (plan 9.3): the manifest, its
    # signature when it has one, and the app archive or the Setup.
    function TrackFile($Release, [string]$Track) {
        switch ($Track) { 'app' { $Release.Value.app.archive.name } 'setup' { $Release.Value.infrastructure.setup.name } default { throw "Unknown track $Track" } }
    }

    $baseVersion = (Get-Content -LiteralPath (Join-Path $repo 'package.json') -Raw | ConvertFrom-Json).version
    $schema = [int][regex]::Match([IO.File]::ReadAllText((Join-Path $repo 'src/database-schema.ts')), 'DATABASE_SCHEMA = (\d+);').Groups[1].Value
    if ($schema -lt 1) { throw 'DATABASE_SCHEMA not found' }

    $n = Package $repo 'n' $baseVersion $repo
    $app = NewVariant 'app' 'HEAD' (BumpVersion $baseVersion 1) $false { param($Tree) BumpSchema $Tree $schema 'migrateChildAccess(this.db);' } $repo
    $unhealthy = NewVariant 'unhealthy' $app.Commit (BumpVersion $baseVersion 2) $false {
        param($Tree)
        $null = BumpSchema $Tree ($schema + 1) $app.Patch
        MakeExitAfterMigrating $Tree
    } $repo
    $n1 = NewVariant 'n1' $app.Commit (BumpVersion $baseVersion 3) $true { param($Tree) BumpSchema $Tree ($schema + 1) $app.Patch } $null
    $broken = NewVariant 'broken' $n1.Commit (BumpVersion $baseVersion 4) $false {
        param($Tree)
        $null = BumpSchema $Tree ($schema + 2) $n1.Patch
        MakeUnhealthy $Tree
    } $n1.Tree

    # What the set has to be for acceptance to mean anything, checked from the signed manifests.
    RequireSameInfrastructure $app.Release $n
    RequireSameInfrastructure $unhealthy.Release $n
    RequireSameInfrastructure $broken.Release $n1.Release
    if ($n1.Release.Value.infrastructure.version -ne (BumpVersion $n.Value.infrastructure.version)) { throw 'n1 does not move the infrastructure' }
    $built = @(Get-ChildItem -LiteralPath $cache -Directory | ForEach-Object Name | Sort-Object)
    $expected = @($n.Value.infrastructure.version, $n1.Release.Value.infrastructure.version | Sort-Object)
    if (($built -join ',') -ne ($expected -join ',')) { throw "The infrastructure cache holds $($built -join ','), expected one build each of $($expected -join ',')" }

    # app/'s release, wrong only in the contract its app needs; signed by the trusted key.
    $contract = Join-Path $set "contract/$($app.Release.Version)"
    New-Item -ItemType Directory -Path $contract | Out-Null
    $contractManifest = Join-Path $contract (Split-Path $app.Release.Manifest -Leaf)
    Copy-Item -LiteralPath $app.Release.Manifest -Destination $contractManifest
    ReplaceMember $contractManifest 'requiresContract' '\d+' ([string]([int]$app.Release.Value.infrastructure.contractMax + 1))
    Sign (Join-Path $keyDir 'key.pem') $contractManifest
    Copy-Item -LiteralPath (Join-Path $app.Release.Directory (TrackFile $app.Release 'app')) -Destination $contract

    # n1/'s release naming n/'s Setup bytes, signed by the trusted key, beside n1/'s own
    # Setup: only the Setup a signed release names installs it.
    $mismatch = Join-Path $set "mismatch/$($n1.Release.Version)"
    New-Item -ItemType Directory -Path $mismatch | Out-Null
    $mismatchManifest = Join-Path $mismatch (Split-Path $n1.Release.Manifest -Leaf)
    Copy-Item -LiteralPath $n1.Release.Manifest -Destination $mismatchManifest
    if ($n.Value.infrastructure.setup.sha256 -eq $n1.Release.Value.infrastructure.setup.sha256) { throw 'n and n1 have the same Setup' }
    ReplaceMember $mismatchManifest 'sha256' "`"$($n1.Release.Value.infrastructure.setup.sha256)`"" "`"$($n.Value.infrastructure.setup.sha256)`""
    Sign (Join-Path $keyDir 'key.pem') $mismatchManifest
    Copy-Item -LiteralPath (Join-Path $n1.Release.Directory (TrackFile $n1.Release 'setup')) -Destination $mismatch

    # Well-formed stages of the next release on each track, each wrong in its signature only.
    $refused = Join-Path $set 'refused'
    $foreignKey = Join-Path $env:RUNNER_TEMP 'foreign-release-key'
    Run $node @($tool, 'keygen', $foreignKey)
    try {
        $foreignId = (Get-FileHash -LiteralPath (Join-Path $foreignKey 'key.spki') -Algorithm SHA256).Hash.ToLowerInvariant().Substring(0, 16)
        $trustedId = (Get-FileHash -LiteralPath $spki -Algorithm SHA256).Hash.ToLowerInvariant().Substring(0, 16)
        foreach ($source in @(@{ Track='app'; Release=$app.Release }, @{ Track='setup'; Release=$n1.Release })) {
            $good = $source.Release
            $file = Join-Path $good.Directory (TrackFile $good $source.Track)
            $name = Split-Path $good.Manifest -Leaf
            $cases = @{}
            foreach ($case in 'foreign', 'unsigned', 'tampered') {
                $cases[$case] = Join-Path $refused "$($source.Track)/$case/$($good.Version)"
                New-Item -ItemType Directory -Force -Path $cases[$case] | Out-Null
                Copy-Item -LiteralPath $good.Manifest -Destination (Join-Path $cases[$case] $name)
                Copy-Item -LiteralPath $file -Destination $cases[$case]
            }
            $foreign = Join-Path $cases.foreign $name
            ReplaceMember $foreign 'keyId' "`"$trustedId`"" "`"$foreignId`""
            Sign (Join-Path $foreignKey 'key.pem') $foreign
            $tampered = Join-Path $cases.tampered $name
            Copy-Item -LiteralPath ($good.Manifest + '.sig') -Destination ($tampered + '.sig')
            # Still valid JSON and still a valid manifest; only the signature can tell.
            $created = [long]$good.Value.createdUnix
            ReplaceMember $tampered 'createdUnix' "$created(?!\d)" ([string]($created + 1))
        }
    } finally { Remove-Item -LiteralPath $foreignKey -Recurse -Force -ErrorAction SilentlyContinue }

    $index = [ordered]@{
        format = 1
        releases = [ordered]@{ n=$n.Version; app=$app.Release.Version; unhealthy=$unhealthy.Release.Version; n1=$n1.Release.Version; broken=$broken.Release.Version }
        contract = $app.Release.Version
        mismatch = $n1.Release.Version
        refused = [ordered]@{ app=$app.Release.Version; setup=$n1.Release.Version }
    }
    WriteFile (Join-Path $set 'upgrade-set.json') (($index | ConvertTo-Json -Depth 3) + "`n")
    Write-Output "UPGRADE_SET_BUILT $set"
} finally {
    foreach ($tree in $worktrees) { & git worktree remove --force $tree }
    Pop-Location
}
