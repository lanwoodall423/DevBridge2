[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$planner = Join-Path $PSScriptRoot 'validation-plan.ps1'

function Invoke-Plan {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][string[]]$Files,
        [string]$RepositoryRoot = $repoRoot,
        [string]$BaseRevision,
        [string]$HeadRevision,
        [switch]$Full
    )

    $arguments = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $planner,
        '-RepositoryRoot', $RepositoryRoot
    )
    if ($Files.Count -gt 0) {
        $arguments += @('-ChangedFile', ($Files -join ','))
    }
    $arguments += '-Json'
    if (-not [string]::IsNullOrWhiteSpace($BaseRevision)) {
        $arguments += @('-BaseRevision', $BaseRevision)
    }
    if (-not [string]::IsNullOrWhiteSpace($HeadRevision)) {
        $arguments += @('-HeadRevision', $HeadRevision)
    }
    if ($Full) { $arguments += '-Full' }

    $output = & pwsh @arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "validation planner failed: $($output -join "`n")"
    }
    return ($output -join "`n") | ConvertFrom-Json
}

function Invoke-PlanFromGitRange {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Base,
        [Parameter(Mandatory = $true)][string]$Head
    )

    $arguments = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $planner,
        '-RepositoryRoot', $Root, '-BaseRevision', $Base, '-HeadRevision', $Head, '-Json'
    )
    $output = & pwsh @arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "validation planner git-range invocation failed: $($output -join "`n")"
    }
    return ($output -join "`n") | ConvertFrom-Json
}

function Assert-True {
    param([Parameter(Mandatory = $true)]$Condition, [Parameter(Mandatory = $true)][string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-Equal {
    param(
        [Parameter(Mandatory = $true)]$Actual,
        [Parameter(Mandatory = $true)]$Expected,
        [Parameter(Mandatory = $true)][string]$Message
    )
    if ($Actual -ne $Expected) {
        throw "$Message. Expected '$Expected', got '$Actual'."
    }
}

function Assert-Contains {
    param(
        [Parameter(Mandatory = $true)]$Values,
        [Parameter(Mandatory = $true)][string]$Value,
        [Parameter(Mandatory = $true)][string]$Message
    )
    if (@($Values) -notcontains $Value) { throw "$Message. Missing '$Value'." }
}

function Assert-NotContains {
    param(
        [Parameter(Mandatory = $true)]$Values,
        [Parameter(Mandatory = $true)][string]$Value,
        [Parameter(Mandatory = $true)][string]$Message
    )
    if (@($Values) -contains $Value) { throw "$Message. Unexpected '$Value'." }
}

function Assert-Selected {
    param(
        [Parameter(Mandatory = $true)]$Plan,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][string[]]$Selected,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][string[]]$Skipped
    )
    foreach ($stage in $Selected) {
        Assert-Contains $Plan.selectedStages $stage "Expected stage selection for $($Plan.changeClass)"
    }
    foreach ($stage in $Skipped) {
        Assert-NotContains $Plan.selectedStages $stage "Expected unrelated stage to be skipped for $($Plan.changeClass)"
    }
}

$docs = Invoke-Plan @('README.md')
Assert-Equal $docs.changeClass 'docs-only' 'docs classification'
Assert-Selected $docs @('static-invariants', 'working-tree-whitespace') @(
    'coordinator-suite', 'bridge-tools-build', 'fake-rimworld-build',
    'validation-planner-matrix', 'dev-plan-matrix', 'dev-publish-matrix', 'live-stack-matrix', 'process-e2e'
)
Write-Host 'PASS docs-only skips every expensive matrix'

$core = Invoke-Plan @('Source/Coordinator.Core/CoordinatorCore.cs')
Assert-Equal $core.changeClass 'coordinator-core' 'Coordinator.Core classification'
Assert-Selected $core @('coordinator-suite') @(
    'coordinator-build', 'bridge-tools-build', 'fake-rimworld-build',
    'validation-planner-matrix', 'dev-plan-matrix', 'dev-publish-matrix', 'live-stack-matrix', 'process-e2e'
)
Write-Host 'PASS Coordinator.Core selects the transitive coordinator suite only'

$coordinator = Invoke-Plan @('Source/Coordinator/CoordinatorHost.cs')
Assert-Equal $coordinator.changeClass 'coordinator' 'Coordinator classification'
Assert-Selected $coordinator @('coordinator-suite') @('bridge-tools-build', 'fake-rimworld-build', 'process-e2e')
Write-Host 'PASS Coordinator selects coordinator tests without unrelated matrices'

$tests = Invoke-Plan @('Source/Coordinator.Tests/OfflineTests.cs')
Assert-Equal $tests.changeClass 'coordinator-tests' 'Coordinator.Tests classification'
Assert-Selected $tests @('coordinator-suite') @('bridge-tools-build', 'dev-publish-matrix', 'live-stack-matrix', 'process-e2e')
Write-Host 'PASS Coordinator.Tests selects only its owning suite'

$bridgeTests = Invoke-Plan @('Source/Coordinator.Tests/BridgeToolsDeploymentTests.cs')
Assert-Equal $bridgeTests.changeClass 'coordinator-tests' 'BridgeTools deployment test classification'
Assert-Selected $bridgeTests @('bridge-tools-build', 'coordinator-suite') @('fake-rimworld-build', 'process-e2e', 'dev-publish-matrix')
Write-Host 'PASS BridgeTools deployment tests select their companion prerequisite'

$bridge = Invoke-Plan @('Source/BridgeTools/DevBridgeGenerationTools.cs')
Assert-Equal $bridge.changeClass 'bridge-tools' 'BridgeTools classification'
Assert-Selected $bridge @('coordinator-suite', 'bridge-tools-build') @('fake-rimworld-build', 'process-e2e', 'dev-publish-matrix')
Write-Host 'PASS BridgeTools selects locked build and offline integration coverage'

$fake = Invoke-Plan @('Source/FakeRimWorld/Program.cs')
Assert-Equal $fake.changeClass 'fake-rimworld' 'FakeRimWorld classification'
Assert-Selected $fake @('coordinator-build', 'fake-rimworld-build', 'process-e2e') @('coordinator-suite', 'bridge-tools-build', 'live-stack-matrix')
Write-Host 'PASS FakeRimWorld selects its build and dependent process E2E'

$devPlan = Invoke-Plan @('scripts/dev-plan.ps1')
Assert-Equal $devPlan.changeClass 'dev-plan' 'dev-plan classification'
Assert-Selected $devPlan @('dev-plan-matrix') @('coordinator-suite', 'dev-publish-matrix', 'live-stack-matrix', 'process-e2e')
Write-Host 'PASS dev-plan selects only the development-plan matrix'

$plannerTests = Invoke-Plan @('scripts/validation-plan.tests.ps1')
Assert-Equal $plannerTests.changeClass 'validation-planner' 'validation planner classification'
Assert-Selected $plannerTests @('validation-planner-matrix') @('coordinator-suite', 'bridge-tools-build', 'fake-rimworld-build', 'dev-plan-matrix', 'dev-publish-matrix', 'live-stack-matrix', 'process-e2e')
Write-Host 'PASS planner test edits select only the planner matrix'

$devPublish = Invoke-Plan @('scripts/dev-publish.ps1')
Assert-Equal $devPublish.changeClass 'dev-publish' 'dev-publish classification'
Assert-Selected $devPublish @('dev-publish-matrix') @('coordinator-suite', 'dev-plan-matrix', 'live-stack-matrix', 'process-e2e')
Write-Host 'PASS dev-publish selects only the publishing matrix'

$live = Invoke-Plan @('scripts/live-stack-smoke.ps1')
Assert-Equal $live.changeClass 'live-stack' 'live-stack classification'
Assert-Selected $live @('live-stack-matrix') @('coordinator-suite', 'dev-publish-matrix', 'process-e2e')
Write-Host 'PASS live-stack selects only offline live orchestration tests'

$process = Invoke-Plan @('Source/Coordinator/CoordinatorIpcProtocol.cs')
Assert-Equal $process.changeClass 'process-e2e' 'IPC host classification'
Assert-Selected $process @('coordinator-suite', 'fake-rimworld-build', 'process-e2e') @('bridge-tools-build', 'dev-publish-matrix', 'live-stack-matrix')
Write-Host 'PASS IPC boundary selects process E2E and its minimum prerequisites'

$compatibility = Invoke-Plan @('RimBridgeProtocolCompatibility.json')
Assert-Equal $compatibility.status 'ready' 'compatibility plan status'
Assert-Equal $compatibility.changeClass 'protocol-compatibility' 'compatibility classification'
Assert-Selected $compatibility @('coordinator-suite', 'bridge-tools-build', 'fake-rimworld-build', 'process-e2e') @('dev-plan-matrix', 'dev-publish-matrix', 'live-stack-matrix')
Write-Host 'PASS compatibility changes cover Coordinator, BridgeTools, FakeRimWorld, and process contracts'

$lock = Invoke-Plan @('Source/BridgeTools/packages.lock.json')
Assert-Equal $lock.status 'conservative' 'package-lock plan status'
Assert-Selected $lock @('validation-planner-matrix', 'coordinator-suite', 'bridge-tools-build', 'fake-rimworld-build', 'dev-plan-matrix', 'dev-publish-matrix', 'live-stack-matrix', 'process-e2e') @()
Write-Host 'PASS package-lock changes conservatively select the complete safe set'

$project = Invoke-Plan @('global.json')
Assert-Equal $project.status 'conservative' 'build configuration plan status'
Assert-Selected $project @('validation-planner-matrix', 'coordinator-suite', 'bridge-tools-build', 'fake-rimworld-build', 'dev-plan-matrix', 'dev-publish-matrix', 'live-stack-matrix', 'process-e2e') @()
Write-Host 'PASS project/SDK configuration conservatively selects the complete safe set'

$projectFile = Invoke-Plan @('Source/Coordinator/DevBridge.Coordinator.csproj')
Assert-Equal $projectFile.status 'conservative' 'project-file plan status'
Assert-Selected $projectFile @('validation-planner-matrix', 'coordinator-suite', 'bridge-tools-build', 'fake-rimworld-build', 'dev-plan-matrix', 'dev-publish-matrix', 'live-stack-matrix', 'process-e2e') @()
Write-Host 'PASS project-file changes conservatively select the complete safe set'

$mixed = Invoke-Plan -Files @('Source/Coordinator/Program.cs', 'scripts/dev-plan.ps1')
Assert-Equal $mixed.changeClass 'mixed' 'mixed classification'
Assert-Selected $mixed @('coordinator-suite', 'dev-plan-matrix', 'fake-rimworld-build', 'process-e2e') @('bridge-tools-build', 'dev-publish-matrix', 'live-stack-matrix')
Write-Host 'PASS mixed known changes union only their affected stages'

$unknown = Invoke-Plan @('new/unknown-impact.bin')
Assert-Equal $unknown.status 'conservative' 'unknown plan status'
Assert-Selected $unknown @('validation-planner-matrix', 'coordinator-suite', 'bridge-tools-build', 'fake-rimworld-build', 'dev-plan-matrix', 'dev-publish-matrix', 'live-stack-matrix', 'process-e2e') @()
Write-Host 'PASS unknown paths conservatively select the complete safe set'

$explicitFull = Invoke-Plan @('README.md') -Full
Assert-Equal $explicitFull.mode 'full' 'explicit full mode'
Assert-Equal $explicitFull.status 'conservative' 'explicit full status'
Assert-Selected $explicitFull @('validation-planner-matrix', 'coordinator-suite', 'bridge-tools-build', 'fake-rimworld-build', 'dev-plan-matrix', 'dev-publish-matrix', 'live-stack-matrix', 'process-e2e') @()
Write-Host 'PASS explicit full override is available without changing the canonical command'

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('DevBridge2-validation-plan-tests-' + $PID)
try {
    New-Item -ItemType Directory -Force -Path (Join-Path $tempRoot 'Source\Coordinator.Core') | Out-Null
    Set-Content -LiteralPath (Join-Path $tempRoot 'Source\Coordinator.Core\Old.cs') -Value 'class Old {}' -Encoding utf8
    & git -C $tempRoot init --quiet
    & git -C $tempRoot config user.email 'validation-plan-tests@example.invalid'
    & git -C $tempRoot config user.name 'validation-plan-tests'
    & git -C $tempRoot add .
    & git -C $tempRoot commit --quiet -m baseline
    & git -C $tempRoot mv 'Source/Coordinator.Core/Old.cs' 'Source/Coordinator.Core/New.cs'
    & git -C $tempRoot commit --quiet -m rename
    $rename = Invoke-PlanFromGitRange $tempRoot 'HEAD~1' 'HEAD'
    Assert-Equal $rename.status 'conservative' 'rename plan status'
    Assert-True (@($rename.changedInputs | Where-Object { $_.status -eq 'R' }).Count -eq 1) 'rename status must be preserved from Git'
    Write-Host 'PASS rename changes conservatively escalate with status preserved'

    New-Item -ItemType Directory -Force -Path (Join-Path $tempRoot 'Source\FakeRimWorld') | Out-Null
    Set-Content -LiteralPath (Join-Path $tempRoot 'Source\FakeRimWorld\Deleted.cs') -Value 'class Deleted {}' -Encoding utf8
    & git -C $tempRoot add .
    & git -C $tempRoot commit --quiet -m add-delete-fixture
    Remove-Item -LiteralPath (Join-Path $tempRoot 'Source\FakeRimWorld\Deleted.cs') -Force
    & git -C $tempRoot commit --quiet -am delete
    $deleted = Invoke-PlanFromGitRange $tempRoot 'HEAD~1' 'HEAD'
    Assert-Equal $deleted.status 'conservative' 'delete plan status'
    Assert-True (@($deleted.changedInputs | Where-Object { $_.status -eq 'D' }).Count -eq 1) 'delete status must be preserved from Git'
    Write-Host 'PASS delete changes conservatively escalate with status preserved'
} finally {
    if ([IO.Directory]::Exists($tempRoot)) {
        Get-ChildItem -LiteralPath $tempRoot -Force -Recurse -ErrorAction SilentlyContinue |
            ForEach-Object { $_.Attributes = 'Normal' }
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}

$invalidBase = Invoke-Plan -Files @() -BaseRevision 'missing-validation-base' -HeadRevision 'HEAD'
Assert-Equal $invalidBase.status 'conservative' 'invalid base status'
Assert-True (-not [bool]$invalidBase.changeContextValid) 'invalid base must be reported as invalid'
Assert-Selected $invalidBase @('validation-planner-matrix', 'coordinator-suite', 'bridge-tools-build', 'fake-rimworld-build', 'dev-plan-matrix', 'dev-publish-matrix', 'live-stack-matrix', 'process-e2e') @()
Write-Host 'PASS invalid base/head context conservatively escalates'

Write-Host 'VALIDATION PLAN TESTS PASS'
