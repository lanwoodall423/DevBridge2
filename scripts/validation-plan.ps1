[CmdletBinding()]
param(
    [string]$RepositoryRoot,
    [string]$BaseRevision,
    [string]$HeadRevision,
    [Alias('ChangedFiles', 'Path')]
    [string[]]$ChangedFile,
    [switch]$Full,
    [switch]$Conservative,
    [switch]$Json
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
} else {
    $RepositoryRoot = [System.IO.Path]::GetFullPath($RepositoryRoot)
}
Set-Location -LiteralPath $RepositoryRoot

$stageOrder = @(
    'static-invariants',
    'working-tree-whitespace',
    'validation-planner-matrix',
    'bridge-tools-build',
    'coordinator-suite',
    'coordinator-build',
    'fake-rimworld-build',
    'dev-plan-matrix',
    'dev-publish-matrix',
    'live-stack-matrix',
    'process-e2e'
)

$stageDescriptions = [ordered]@{
    'static-invariants' = 'Repository contract and generated-artifact invariants'
    'working-tree-whitespace' = 'Changed-file whitespace checks'
    'validation-planner-matrix' = 'Run deterministic validation-planner matrix'
    'coordinator-suite' = 'Restore/build Coordinator.Tests transitively and run the applicable offline coordinator suite'
    'coordinator-build' = 'Restore/build the Coordinator host for process-level tests'
    'bridge-tools-build' = 'Locked restore/build BridgeTools and verify the companion output'
    'fake-rimworld-build' = 'Restore/build the FakeRimWorld process host'
    'dev-plan-matrix' = 'Run the development-plan script matrix'
    'dev-publish-matrix' = 'Run the development publish/hash/deployment matrix'
    'live-stack-matrix' = 'Run the offline live-stack orchestration/configuration matrix'
    'process-e2e' = 'Run the process-level FakeRimWorld E2E suite'
}

function Get-RelativeRepositoryPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $candidate = $Path.Replace('\', '/')
    if ([System.IO.Path]::IsPathRooted($candidate)) {
        $candidate = [System.IO.Path]::GetRelativePath(
            $RepositoryRoot,
            [System.IO.Path]::GetFullPath($candidate))
    }
    $candidate = $candidate.Replace('\', '/').TrimStart('./')
    if ([string]::IsNullOrWhiteSpace($candidate) -or $candidate -eq '..' -or
        $candidate.StartsWith('../', [StringComparison]::Ordinal)) {
        throw "Changed file is outside the repository: $Path"
    }
    return $candidate
}

function Invoke-GitLines {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $values = @(& git @Arguments 2>$null)
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
    return @($values | ForEach-Object {
        ([string]$_).TrimEnd("`r", "`n")
    } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}

function Test-GitRevision {
    param([Parameter(Mandatory = $true)][string]$Revision)

    if ([string]::IsNullOrWhiteSpace($Revision)) { return $false }
    $null = & git rev-parse --verify ($Revision + '^{commit}') 2>$null
    return $LASTEXITCODE -eq 0
}

function Add-ChangeRecord {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][System.Collections.Generic.List[object]]$Records,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][System.Collections.Generic.HashSet[string]]$Keys,
        [Parameter(Mandatory = $true)][string]$Status,
        [Parameter(Mandatory = $true)][string]$Path,
        [string]$OldPath
    )

    $normalizedPath = Get-RelativeRepositoryPath $Path
    $normalizedOldPath = if ([string]::IsNullOrWhiteSpace($OldPath)) {
        $null
    } else {
        Get-RelativeRepositoryPath $OldPath
    }
    $key = '{0}|{1}|{2}' -f $Status, $normalizedOldPath, $normalizedPath
    if (-not $Keys.Add($key)) { return }
    [void]$Records.Add([ordered]@{
        status = $Status
        path = $normalizedPath
        oldPath = $normalizedOldPath
    })
}

function Add-DiffLines {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][System.Collections.Generic.List[object]]$Records,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][System.Collections.Generic.HashSet[string]]$Keys,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    foreach ($line in (Invoke-GitLines $Arguments)) {
        $parts = $line -split "`t"
        if ($parts.Count -lt 2) {
            throw "Could not parse git diff name-status record: $line"
        }
        $status = [string]$parts[0]
        $kind = $status.Substring(0, 1).ToUpperInvariant()
        if ($kind -in @('R', 'C')) {
            if ($parts.Count -lt 3) {
                throw "Could not parse renamed/copied git diff record: $line"
            }
            Add-ChangeRecord $Records $Keys $kind $parts[2] $parts[1]
        } else {
            Add-ChangeRecord $Records $Keys $kind $parts[1]
        }
    }
}

function Get-ChangedRecords {
    $records = [System.Collections.Generic.List[object]]::new()
    $keys = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $context = 'worktree'
    $contextValid = $true
    $contextReason = $null

    if ($null -ne $ChangedFile -and $ChangedFile.Count -gt 0) {
        foreach ($value in $ChangedFile) {
            foreach ($file in [regex]::Split([string]$value, '[,;]') |
                    Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) {
                Add-ChangeRecord $records $keys 'M' $file.Trim()
            }
        }
        $context = 'explicit'
    } elseif (-not [string]::IsNullOrWhiteSpace($BaseRevision) -or
            -not [string]::IsNullOrWhiteSpace($HeadRevision)) {
        $context = 'revision-range'
        if ([string]::IsNullOrWhiteSpace($BaseRevision) -or
            [string]::IsNullOrWhiteSpace($HeadRevision)) {
            $contextValid = $false
            $contextReason = 'Both BaseRevision and HeadRevision are required for revision-range planning.'
        } elseif (-not (Test-GitRevision $BaseRevision) -or
                -not (Test-GitRevision $HeadRevision)) {
            $contextValid = $false
            $contextReason = "The supplied validation base/head could not be resolved: $BaseRevision..$HeadRevision."
        } else {
            Add-DiffLines $records $keys @(
                'diff', '--name-status', '--find-renames', '--diff-filter=ACDMRTUXB',
                $BaseRevision, $HeadRevision
            )
        }
    } else {
        Add-DiffLines $records $keys @('diff', '--name-status', '--diff-filter=ACDMRTUXB', 'HEAD')
        Add-DiffLines $records $keys @('diff', '--cached', '--name-status', '--diff-filter=ACDMRTUXB')
        foreach ($file in (Invoke-GitLines @('ls-files', '--others', '--exclude-standard'))) {
            Add-ChangeRecord $records $keys 'A' $file
        }
    }

    # A revision-range checkout is normally clean in CI. Including local worktree
    # changes as well prevents a developer from accidentally under-testing an
    # uncommitted edit while reusing a CI base/head pair locally.
    if ($context -eq 'revision-range' -and $contextValid) {
        Add-DiffLines $records $keys @('diff', '--name-status', '--diff-filter=ACDMRTUXB', 'HEAD')
        Add-DiffLines $records $keys @('diff', '--cached', '--name-status', '--diff-filter=ACDMRTUXB')
        foreach ($file in (Invoke-GitLines @('ls-files', '--others', '--exclude-standard'))) {
            Add-ChangeRecord $records $keys 'A' $file
        }
    }

    return [pscustomobject]@{
        records = @($records | Sort-Object -Property path, oldPath, status)
        context = $context
        valid = $contextValid
        reason = $contextReason
    }
}

function Get-Impact {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Status
    )

    $normalized = $Path.Replace('\', '/')
    $lower = $normalized.ToLowerInvariant()
    $impact = [ordered]@{
        class = 'unknown'
        reason = 'No repository impact rule matched this path.'
        coordinatorSuite = $false
        coordinatorBuild = $false
        bridgeTools = $false
        fakeRimWorld = $false
        devPlan = $false
        devPublish = $false
        liveStack = $false
        processE2E = $false
        validationPlanner = $false
        protocol = $false
        full = $true
    }

    if ($lower -match '^(runtime|coordinator|bridgetools|1\.6/assemblies)/' -or
        $lower -match '^source/.+/(bin|obj)/' -or $lower -match '^artifacts/') {
        $impact.class = 'ignored-generated'
        $impact.reason = 'Generated output is not a source input.'
        $impact.full = $false
        return [pscustomobject]$impact
    }

    if ($lower -match '\.csproj$') {
        $impact.class = 'build-configuration'
        $impact.reason = 'Project configuration can change transitive restore/build behavior.'
        $impact.full = $true
        return [pscustomobject]$impact
    }

    if ($lower -match '(^|/)(coordinatoripcprotocol|.*ipc.*)\.cs$') {
        $impact.class = 'process-e2e'
        $impact.reason = 'Coordinator IPC/process boundary.'
        $impact.coordinatorSuite = $true
        $impact.fakeRimWorld = $true
        $impact.processE2E = $true
        $impact.full = $false
        return [pscustomobject]$impact
    }

    if ($lower -eq 'rimbridgeprotocolcompatibility.json' -or
        $lower -match '(^|/)(rimbridgeprotocol|protocol|schema|compatibility)[^/]*\.(json|cs|md)$' -or
        $lower -match '^source/(coordinator\.core|coordinator|bridgetools)/.*(protocol|schema|compatib|rimbridge)') {
        $impact.class = 'protocol-compatibility'
        $impact.reason = 'Protocol/schema/compatibility boundary.'
        $impact.coordinatorSuite = $true
        $impact.bridgeTools = $true
        $impact.fakeRimWorld = $true
        $impact.processE2E = $true
        $impact.protocol = $true
        $impact.full = $false
        return [pscustomobject]$impact
    }

    if ($lower -match '(^|/)packages\.lock\.json$') {
        $impact.class = 'build-configuration'
        $impact.reason = 'Locked package graph changed; restore/build/test surfaces must be revalidated conservatively.'
        $impact.full = $true
        return [pscustomobject]$impact
    }

    if ($lower -match '^source/bridge\.tools/' -or $lower -match '^source/bridgetools/') {
        $impact.class = 'bridge-tools'
        $impact.reason = 'Canonical BridgeTools companion project.'
        $impact.coordinatorSuite = $true
        $impact.bridgeTools = $true
        $impact.full = $false
        return [pscustomobject]$impact
    }

    if ($lower -match '^source/fakerimworld/') {
        $impact.class = 'fake-rimworld'
        $impact.reason = 'FakeRimWorld process host.'
        $impact.fakeRimWorld = $true
        $impact.processE2E = $true
        $impact.full = $false
        return [pscustomobject]$impact
    }

    if ($lower -match '^source/coordinator\.tests/') {
        $impact.class = 'coordinator-tests'
        $impact.reason = 'Offline coordinator test project.'
        $impact.coordinatorSuite = $true
        $impact.full = $false
        if ($lower -match 'bridgetools') {
            $impact.reason = 'Coordinator offline tests that exercise the BridgeTools deployment contract.'
            $impact.bridgeTools = $true
        }
        return [pscustomobject]$impact
    }

    if ($lower -match '^source/coordinator\.core/') {
        $impact.class = 'coordinator-core'
        $impact.reason = 'Coordinator.Core shared implementation.'
        $impact.coordinatorSuite = $true
        $impact.full = $false
        if ($lower -match '/(process|lifecycle|state)/|(^|/)(coordinatoripcprotocol|.*protocol).*\.cs$') {
            $impact.processE2E = $true
        }
        return [pscustomobject]$impact
    }

    if ($lower -match '^source/coordinator/') {
        $impact.class = 'coordinator'
        $impact.reason = 'Coordinator executable host.'
        $impact.coordinatorSuite = $true
        $impact.full = $false
        if ($lower -match '(ipc|lifecycle|process|program|protocol)') {
            $impact.processE2E = $true
        }
        return [pscustomobject]$impact
    }

    if ($lower -match '^source/mod/' -or $lower -match '(^|/)about/about\.xml$' -or
        $lower -match '(^|/)(patches|defs|languages|textures|sounds|1\.6)/') {
        $impact.class = 'runtime-mod'
        $impact.reason = 'RimWorld-loaded or packaged runtime input requires conservative offline validation.'
        $impact.full = $true
        return [pscustomobject]$impact
    }

    if ($lower -match '^testrecipes/live-stack|^developmentprojects/|^scripts/(live-stack-smoke|mod-test)') {
        $impact.class = 'live-stack'
        $impact.reason = 'Live-stack offline orchestration or machine-validated configuration.'
        $impact.liveStack = $true
        $impact.full = $false
        return [pscustomobject]$impact
    }

    if ($lower -match '^testrecipes/') {
        $impact.class = 'machine-validated-input'
        $impact.reason = 'Repository-owned test recipe consumed by validation tooling.'
        $impact.liveStack = $true
        $impact.full = $false
        return [pscustomobject]$impact
    }

    if ($lower -match '^scripts/dev-plan(\.tests)?\.ps1$') {
        $impact.class = 'dev-plan'
        $impact.reason = 'Development-plan planner or deterministic matrix.'
        $impact.devPlan = $true
        $impact.full = $false
        return [pscustomobject]$impact
    }
    if ($lower -eq 'scripts/validation-plan.tests.ps1') {
        $impact.class = 'validation-planner'
        $impact.reason = 'Deterministic validation impact-planner matrix.'
        $impact.validationPlanner = $true
        $impact.full = $false
        return [pscustomobject]$impact
    }
    if ($lower -eq 'scripts/validation-plan.ps1') {
        $impact.class = 'validation-planner'
        $impact.reason = 'Validation planner implementation affects stage selection safety.'
        $impact.validationPlanner = $true
        $impact.full = $true
        return [pscustomobject]$impact
    }
    if ($lower -match '(^|/)(publish-devbridge\.ps1|scripts/dev-publish(\.tests)?\.ps1)$') {
        $impact.class = 'dev-publish'
        $impact.reason = 'Development publishing/deployment or artifact hash matrix.'
        $impact.devPublish = $true
        $impact.full = $false
        return [pscustomobject]$impact
    }
    if ($lower -match '^scripts/process-e2e\.tests\.ps1$' -or $lower -eq 'devbridge.cmd') {
        $impact.class = 'process-e2e'
        $impact.reason = 'Process IPC/lifecycle/E2E boundary.'
        $impact.coordinatorBuild = $true
        $impact.fakeRimWorld = $true
        $impact.processE2E = $true
        $impact.full = $false
        return [pscustomobject]$impact
    }

    if ($lower -match '(^|/)(directory\.build\.(props|targets)|directory\.packages\.props|global\.json|nuget\.config|packages\.lock\.json|[^/]+\.sln)$' -or
        $lower -match '^\.github/workflows/') {
        $impact.class = 'build-configuration'
        $impact.reason = 'Build, package, SDK, lock-file, or CI configuration can affect every validation surface.'
        $impact.full = $true
        return [pscustomobject]$impact
    }

    if ($lower -match '(^|/)(docs?/|readme|start_here|maintenance|changelog|license|contributing)' -or
        $lower -match '\.(md|txt|rst)$') {
        $impact.class = 'docs-only'
        $impact.reason = 'Documentation with no machine-validated runtime role.'
        $impact.full = $false
        return [pscustomobject]$impact
    }

    # A deletion, rename, copy, conflict, or otherwise unusual status is
    # handled by the caller as conservative even when the path is recognizable.
    return [pscustomobject]$impact
}

function Add-UniqueString {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][System.Collections.Generic.HashSet[string]]$Set,
        [Parameter(Mandatory = $true)][string]$Value
    )
    [void]$Set.Add($Value)
}

function Get-Plan {
    $changed = Get-ChangedRecords
    $classes = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $reasons = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $selected = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $coordinatorSuite = $false
    $coordinatorBuild = $false
    $bridgeTools = $false
    $fakeRimWorld = $false
    $devPlan = $false
    $devPublish = $false
    $liveStack = $false
    $processE2E = $false
    $validationPlanner = $false
    $requiresFull = -not $changed.valid
    $escalationReasons = [System.Collections.Generic.List[string]]::new()
    if (-not $changed.valid) {
        [void]$escalationReasons.Add($changed.reason)
    }

    $classified = [System.Collections.Generic.List[object]]::new()
    foreach ($record in $changed.records) {
        $impact = Get-Impact $record.path $record.status
        [void]$classes.Add($impact.class)
        if ($impact.reason) { [void]$reasons.Add($impact.reason) }
        foreach ($oldPath in @($record.oldPath)) {
            if (-not [string]::IsNullOrWhiteSpace([string]$oldPath)) {
                $oldImpact = Get-Impact $oldPath $record.status
                [void]$classes.Add($oldImpact.class)
                if ($oldImpact.reason) { [void]$reasons.Add($oldImpact.reason) }
            }
        }
        $coordinatorSuite = $coordinatorSuite -or [bool]$impact.coordinatorSuite
        $coordinatorBuild = $coordinatorBuild -or [bool]$impact.coordinatorBuild
        $bridgeTools = $bridgeTools -or [bool]$impact.bridgeTools
        $fakeRimWorld = $fakeRimWorld -or [bool]$impact.fakeRimWorld
        $devPlan = $devPlan -or [bool]$impact.devPlan
        $devPublish = $devPublish -or [bool]$impact.devPublish
        $liveStack = $liveStack -or [bool]$impact.liveStack
        $processE2E = $processE2E -or [bool]$impact.processE2E
        $validationPlanner = $validationPlanner -or [bool]$impact.validationPlanner
        if ([bool]$impact.full) {
            $requiresFull = $true
            [void]$escalationReasons.Add("$($record.path): $($impact.reason)")
        }
        if ($record.status -in @('D', 'R', 'C', 'U', 'T', 'X', 'B')) {
            $requiresFull = $true
            [void]$escalationReasons.Add("$($record.status) change requires conservative validation: $($record.path)")
        }
        [void]$classified.Add([ordered]@{
            status = $record.status
            path = $record.path
            oldPath = $record.oldPath
            changeClass = $impact.class
            reason = $impact.reason
        })
    }

    if ($Full -or $Conservative) {
        $requiresFull = $true
        [void]$escalationReasons.Add($(if ($Full) { 'Explicit full validation override.' } else { 'Explicit conservative validation override.' }))
    }

    if ($requiresFull) {
        foreach ($stage in $stageOrder) { Add-UniqueString $selected $stage }
        if (-not $changed.valid) {
            [void]$reasons.Add('Invalid or missing base/head context escalates to the complete safe validation set.')
        }
    } else {
        Add-UniqueString $selected 'static-invariants'
        Add-UniqueString $selected 'working-tree-whitespace'
        if ($coordinatorSuite) { Add-UniqueString $selected 'coordinator-suite' }
        if ($coordinatorBuild) { Add-UniqueString $selected 'coordinator-build' }
        if ($bridgeTools) { Add-UniqueString $selected 'bridge-tools-build' }
        if ($fakeRimWorld) { Add-UniqueString $selected 'fake-rimworld-build' }
        if ($devPlan) { Add-UniqueString $selected 'dev-plan-matrix' }
        if ($devPublish) { Add-UniqueString $selected 'dev-publish-matrix' }
        if ($liveStack) { Add-UniqueString $selected 'live-stack-matrix' }
        if ($processE2E) { Add-UniqueString $selected 'process-e2e' }
        if ($validationPlanner) { Add-UniqueString $selected 'validation-planner-matrix' }
        if ($processE2E -and -not $coordinatorSuite) {
            Add-UniqueString $selected 'coordinator-build'
        }
        if ($processE2E -and -not $fakeRimWorld) {
            Add-UniqueString $selected 'fake-rimworld-build'
        }
    }

    # A coordinator suite already restores/builds the host and Core through
    # project references. Keep the standalone host stage only for process E2E
    # plans that do not otherwise need the suite.
    if ($selected.Contains('coordinator-suite')) {
        [void]$selected.Remove('coordinator-build')
    }

    $selectedInOrder = @($stageOrder | Where-Object { $selected.Contains($_) })
    $selectedReasons = [ordered]@{}
    foreach ($stage in $selectedInOrder) {
        $selectedReasons[$stage] = switch ($stage) {
            'static-invariants' { 'Always selected; protects repository contracts without a build.'; break }
            'working-tree-whitespace' { 'Always selected; checks the current and requested diff.'; break }
            'validation-planner-matrix' { 'The deterministic validation impact planner or its tests changed.'; break }
            'coordinator-suite' { 'Coordinator/Core/test impact or a protocol contract requires the offline suite.'; break }
            'coordinator-build' { 'Process E2E requires a host build and no coordinator suite is already selected.'; break }
            'bridge-tools-build' { 'BridgeTools or protocol impact requires a locked companion build.'; break }
            'fake-rimworld-build' { 'FakeRimWorld or process E2E impact requires the process host build.'; break }
            'dev-plan-matrix' { 'Development-plan scripts/configuration changed.'; break }
            'dev-publish-matrix' { 'Development publishing/deployment scripts changed.'; break }
            'live-stack-matrix' { 'Live-stack offline orchestration/configuration changed.'; break }
            'process-e2e' { 'IPC, lifecycle, process, FakeRimWorld, or protocol boundary changed.'; break }
        }
    }

    $skipped = [System.Collections.Generic.List[object]]::new()
    foreach ($stage in $stageOrder) {
        if ($selected.Contains($stage)) { continue }
        $reason = switch ($stage) {
            'coordinator-build' {
                if ($selected.Contains('coordinator-suite')) { 'Covered transitively by coordinator-suite.' }
                else { 'No process E2E host build is required by the changed inputs.' }
                break
            }
            'coordinator-suite' { 'No Coordinator/Core/test or protocol impact was detected.'; break }
            'bridge-tools-build' { 'No BridgeTools or protocol impact was detected.'; break }
            'fake-rimworld-build' { 'No FakeRimWorld or process E2E impact was detected.'; break }
            'dev-plan-matrix' { 'Development-plan scripts/configuration were not changed.'; break }
            'dev-publish-matrix' { 'Development publishing/deployment scripts were not changed.'; break }
            'live-stack-matrix' { 'Live-stack orchestration/configuration was not changed.'; break }
            'process-e2e' { 'No process IPC/lifecycle/E2E boundary was detected.'; break }
            'validation-planner-matrix' { 'The validation impact planner was not changed.'; break }
            default { 'Not selected by the impact planner.'; break }
        }
        [void]$skipped.Add([ordered]@{ stage = $stage; reason = $reason })
    }

    $meaningfulClasses = @($classes | Where-Object { $_ -ne 'ignored-generated' } | Sort-Object)
    $overallClass = if ($meaningfulClasses.Count -eq 0) {
        if ($changed.records.Count -gt 0) { 'generated-only' } else { 'none' }
    } elseif ($meaningfulClasses.Count -eq 1) {
        $meaningfulClasses[0]
    } else {
        'mixed'
    }
    $status = if ($requiresFull) { 'conservative' } else { 'ready' }
    $escalation = @($escalationReasons | Sort-Object -Unique)
    $changeList = @($classified | Sort-Object -Property path, oldPath, status)

    return [ordered]@{
        schemaVersion = 'devbridge-validation-plan/v1'
        repositoryRoot = $RepositoryRoot
        status = $status
        mode = if ($Full) { 'full' } elseif ($Conservative) { 'conservative' } else { 'automatic' }
        changeContext = $changed.context
        baseRevision = if ([string]::IsNullOrWhiteSpace($BaseRevision)) { $null } else { $BaseRevision }
        headRevision = if ([string]::IsNullOrWhiteSpace($HeadRevision)) { $null } else { $HeadRevision }
        changeContextValid = [bool]$changed.valid
        changedInputs = $changeList
        changedInputCount = $changeList.Count
        changeClasses = $meaningfulClasses
        changeClass = $overallClass
        conservativeEscalation = if ($escalation.Count -gt 0) { $escalation } else { $null }
        reasons = @($reasons | Sort-Object)
        selectedStages = @($selectedInOrder)
        selectedValidation = @($selectedInOrder | ForEach-Object {
            [ordered]@{
                stage = $_
                description = $stageDescriptions[$_]
                reason = $selectedReasons[$_]
            }
        })
        skippedValidation = @($skipped)
        totalSelectedStages = $selectedInOrder.Count
    }
}

$plan = Get-Plan
$jsonText = $plan | ConvertTo-Json -Depth 12

if ($Json) {
    Write-Output $jsonText
    exit 0
}

Write-Host 'DevBridge validation impact plan'
Write-Host ('  Changed inputs:   ' + $plan.changedInputCount)
Write-Host ('  Change class:     ' + $plan.changeClass)
Write-Host ('  Context:           ' + $plan.changeContext + $(if (-not $plan.changeContextValid) { ' (conservative)' } else { '' }))
Write-Host ('  Selected stages:   ' + $plan.totalSelectedStages)
foreach ($stage in $plan.selectedValidation) {
    Write-Host ('    + ' + $stage.stage + ': ' + $stage.reason)
}
if ($plan.skippedValidation.Count -gt 0) {
    Write-Host '  Skipped stages:'
    foreach ($stage in $plan.skippedValidation) {
        Write-Host ('    - ' + $stage.stage + ': ' + $stage.reason)
    }
}
if ($null -ne $plan.conservativeEscalation) {
    Write-Host '  Conservative escalation:'
    foreach ($reason in $plan.conservativeEscalation) { Write-Host ('    ! ' + $reason) }
}
Write-Host "`nMachine-readable plan:"
Write-Output $jsonText
