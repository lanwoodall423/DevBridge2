[CmdletBinding()]
param(
    [switch]$UpdatePackages,
    [string]$BaseRevision,
    [string]$HeadRevision,
    [Alias('ChangedFiles', 'Path')]
    [string[]]$ChangedFile,
    [switch]$Full,
    [switch]$Conservative,
    [switch]$InvariantsOnly,
    [switch]$Json
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location -LiteralPath $repoRoot

$plannerPath = Join-Path $repoRoot 'scripts\validation-plan.ps1'
$coordinatorProject = 'Source\Coordinator\DevBridge.Coordinator.csproj'
$testsProject = 'Source\Coordinator.Tests\DevBridge.Coordinator.Tests.csproj'
$bridgeToolsProject = 'Source\BridgeTools\DevBridge2.BridgeTools.csproj'
$fakeRimWorldProject = 'Source\FakeRimWorld\FakeRimWorld.csproj'
$bridgeToolsOutput = Join-Path $repoRoot 'Source\BridgeTools\bin\Release'
$restoreMode = if ($UpdatePackages) { '--force-evaluate' } else { '--locked-mode' }

function Limit-Output {
    param(
        [AllowEmptyString()][string]$Text,
        [int]$Limit = 6000
    )

    if ([string]::IsNullOrWhiteSpace($Text)) { return '<no output>' }
    $normalized = $Text.Trim()
    if ($normalized.Length -le $Limit) { return $normalized }
    return $normalized.Substring(0, $Limit) + "`n...[truncated to $Limit characters]"
}

function Invoke-Required {
    param(
        [Parameter(Mandatory = $true)][string]$Description,
        [Parameter(Mandatory = $true)][string]$Command,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    $captured = @(& $Command @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    $output = ($captured | ForEach-Object { [string]$_ }) -join "`n"
    if ($exitCode -ne 0) {
        throw "$Description failed with exit code $exitCode.`n$(Limit-Output $output)"
    }
    if (-not $Json) {
        Write-Host ('PASS ' + $Description)
    }
}

function Get-ValidationPlan {
    $arguments = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $plannerPath,
        '-Json'
    )
    if (-not [string]::IsNullOrWhiteSpace($BaseRevision)) {
        $arguments += @('-BaseRevision', $BaseRevision)
    }
    if (-not [string]::IsNullOrWhiteSpace($HeadRevision)) {
        $arguments += @('-HeadRevision', $HeadRevision)
    }
    if ($null -ne $ChangedFile -and $ChangedFile.Count -gt 0) {
        $arguments += '-ChangedFile'
        # A child pwsh invocation binds one native argument to an array
        # parameter. The planner accepts comma-separated paths to preserve
        # multi-file explicit overrides across that process boundary.
        $arguments += (@($ChangedFile) -join ',')
    }
    if ($Full) { $arguments += '-Full' }
    if ($Conservative -or $UpdatePackages) { $arguments += '-Conservative' }

    $captured = @(& pwsh @arguments 2>&1)
    $exitCode = $LASTEXITCODE
    $output = ($captured | ForEach-Object { [string]$_ }) -join "`n"
    if ($exitCode -ne 0) {
        throw "validation-plan.ps1 failed with exit code $exitCode.`n$(Limit-Output $output)"
    }
    try {
        return $output | ConvertFrom-Json
    } catch {
        throw "validation-plan.ps1 did not return valid JSON: $($_.Exception.Message)`n$(Limit-Output $output)"
    }
}

function Test-StaticInvariants {
    $testProjectText = Get-Content -LiteralPath $testsProject -Raw
    $bridgeToolsProjectText = Get-Content -LiteralPath $bridgeToolsProject -Raw
    $modProjectText = Get-Content -LiteralPath 'Source\Mod\DevBridge2.csproj' -Raw
    if ($testProjectText -match '<Compile Include="\.\.\\Coordinator\\') {
        throw 'Coordinator.Tests must reference Coordinator.Core/Coordinator projects instead of linking Coordinator implementation sources.'
    }
    if ($testProjectText -notmatch '<ProjectReference Include="\.\.\\Coordinator\.Core\\DevBridge\.Coordinator\.Core\.csproj"') {
        throw 'Coordinator.Tests must reference DevBridge.Coordinator.Core.'
    }
    if ($modProjectText -match '\.\.\\Coordinator\\(State\\DevBridgeSchemaVersions|QuicktestActivationCore|QuicktestFailureArtifact)\.cs') {
        throw 'The Mod project must use the extracted Coordinator.Core shared sources.'
    }

    $trackedFiles = @(git ls-files)
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not inspect the Git index.'
    }

    $trackedCoordinatorArtifacts = @($trackedFiles | Where-Object {
        $_ -like 'Coordinator_build/*' -or
        ($_ -like 'Coordinator/*' -and $_ -ne 'Coordinator/.gitkeep') -or
        $_ -match '(^|/)DevBridge\.Coordinator\.(exe|dll|pdb|deps\.json|runtimeconfig\.json)$'
    })
    if ($trackedCoordinatorArtifacts.Count -gt 0) {
        throw "Generated coordinator artifacts are tracked:`n$($trackedCoordinatorArtifacts -join "`n")"
    }

    $trackedBuildProducts = @($trackedFiles | Where-Object {
        $_ -match '(^|/)(bin|obj)/'
    })
    if ($trackedBuildProducts.Count -gt 0) {
        throw "Generated .NET build products are tracked:`n$($trackedBuildProducts -join "`n")"
    }

    $trackedProprietaryAssemblies = @($trackedFiles | Where-Object {
        $_ -match '(?i)(^|/)(RimWorld|Assembly-CSharp|UnityEngine[^/]*)\.dll$' -or
        $_ -match '(?i)(^|/)RimBridgeServer\.Sdk\.dll$'
    })
    if ($trackedProprietaryAssemblies.Count -gt 0) {
        throw "Proprietary RimWorld/host SDK assemblies are tracked:`n$($trackedProprietaryAssemblies -join "`n")"
    }

    $compatibilityPath = Join-Path $repoRoot 'RimBridgeProtocolCompatibility.json'
    if (-not (Test-Path -LiteralPath $compatibilityPath -PathType Leaf)) {
        throw "RimBridge protocol compatibility metadata was not found: $compatibilityPath"
    }
    try {
        $compatibility = Get-Content -LiteralPath $compatibilityPath -Raw | ConvertFrom-Json
    } catch {
        throw "RimBridge protocol compatibility metadata is not valid JSON: $($_.Exception.Message)"
    }
    $sdkMatch = [regex]::Match($bridgeToolsProjectText,
        '<PackageReference\s+Include="RimBridgeServer\.Sdk"\s+Version="([^"]+)"',
        [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if (-not $sdkMatch.Success) {
        throw 'BridgeTools must declare a fallback RimBridgeServer.Sdk package version.'
    }
    $declaredSdkVersion = $sdkMatch.Groups[1].Value
    $metadataSdkVersion = [string]$compatibility.bridgeTools.sdkPackageVersion
    $contractSource = Get-Content -LiteralPath 'Source\Coordinator.Core\Integrations\RimBridge\RimBridgeProtocolContract.cs' -Raw
    if ([string]$compatibility.gabp.envelopeVersion -ne 'gabp/1' -or
        [int]$compatibility.gabp.major -ne 1) {
        throw 'The checked-in GABP compatibility contract must explicitly describe gabp/1.'
    }
    if ($metadataSdkVersion -ne $declaredSdkVersion -or
        $contractSource -notmatch [regex]::Escape("BridgeToolsSdkPackageVersion = `"$declaredSdkVersion`"")) {
        throw "RimBridgeServer.Sdk version drift: project=$declaredSdkVersion metadata=$metadataSdkVersion contract source is not aligned."
    }
    if (@($compatibility.rimBridgeServer.testedVersions).Count -gt 0 -and
        [string]::IsNullOrWhiteSpace([string]$compatibility.rimBridgeServer.supportStatement)) {
        throw 'Every claimed RimBridgeServer version must have a compatibility support statement.'
    }
    $protocolFixtureSource = Get-Content -LiteralPath 'Source\Coordinator.Tests\RimBridgeProtocolContractTests.cs' -Raw
    if ($protocolFixtureSource -notmatch 'TestRimBridgeProtocolCompatibilityContract' -or
        $testProjectText -notmatch 'RimBridgeProtocolContractTests\.cs') {
        throw 'GABP compatibility metadata must have a checked-in offline contract test.'
    }

    $globalPath = Join-Path $repoRoot 'global.json'
    try {
        $globalConfig = Get-Content -LiteralPath $globalPath -Raw | ConvertFrom-Json
    } catch {
        throw "global.json is missing or invalid: $($_.Exception.Message)"
    }
    if ([string]$globalConfig.sdk.version -ne '8.0.424' -or
        [string]$globalConfig.sdk.rollForward -ne 'disable') {
        throw 'global.json must pin the exact supported .NET SDK 8.0.424 without roll-forward.'
    }
    $lockPath = Join-Path $repoRoot 'Source\BridgeTools\packages.lock.json'
    if (-not (Test-Path -LiteralPath $lockPath -PathType Leaf)) {
        throw 'BridgeTools packages.lock.json is required for locked restore.'
    }
    $lockText = Get-Content -LiteralPath $lockPath -Raw
    if ($lockText -notmatch '"RimBridgeServer\.Sdk"' -or
        $lockText -notmatch '"resolved"\s*:\s*"2\.0\.0"' -or
        $lockText -notmatch '"contentHash"\s*:\s*"[^"]+"') {
        throw 'BridgeTools packages.lock.json does not pin the expected SDK package and content hash.'
    }
    foreach ($nullableFile in @(
        'Source\Coordinator.Core\Integrations\RimBridge\RimBridgeProtocolContract.cs',
        'Source\Coordinator\CoordinatorIpcProtocol.cs'
    )) {
        $nullableText = Get-Content -LiteralPath $nullableFile -Raw
        if ($nullableText -notmatch '(?m)^#nullable\s+enable\s*$') {
            throw "New protocol boundary $nullableFile must opt into nullable analysis."
        }
    }
    $propsText = Get-Content -LiteralPath 'Source\Directory.Build.props' -Raw
    $versionMatch = [regex]::Match($propsText,
        '<DevBridgeProductVersion>\s*([^<\s]+)\s*</DevBridgeProductVersion>',
        [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if (-not $versionMatch.Success) {
        throw 'Source/Directory.Build.props does not expose the authoritative product version.'
    }
    $authoritativeVersion = $versionMatch.Groups[1].Value
    $aboutMetadata = [xml](Get-Content -LiteralPath 'About\About.xml' -Raw)
    if ([string]$aboutMetadata.ModMetaData.modVersion -ne $authoritativeVersion) {
        throw 'About/About.xml does not match the authoritative product version.'
    }
    $changelogText = Get-Content -LiteralPath 'CHANGELOG.md' -Raw
    if ($changelogText -notmatch "(?m)^##\s+$([regex]::Escape($authoritativeVersion))\s*$") {
        throw "CHANGELOG.md has no explicit release heading for $authoritativeVersion."
    }

    # Existing generated output can be checked cheaply, but its absence is not
    # a source invariant failure for docs-only or coordinator-only plans.
    if (Test-Path -LiteralPath $bridgeToolsOutput -PathType Container) {
        Test-BridgeToolsOutput
    }
}

function Test-BridgeToolsOutput {
    param([switch]$RequireOutput)

    if (-not (Test-Path -LiteralPath $bridgeToolsOutput -PathType Container)) {
        if ($RequireOutput) {
            throw "BridgeTools build output was not found: $bridgeToolsOutput"
        }
        return
    }
    $sdkOutput = @(Get-ChildItem -LiteralPath $bridgeToolsOutput -Filter 'RimBridgeServer.Sdk.dll' -File -Recurse -ErrorAction SilentlyContinue)
    if ($sdkOutput.Count -gt 0) {
        throw "BridgeTools build output contains RimBridgeServer.Sdk.dll:`n$($sdkOutput.FullName -join "`n")"
    }
}

function Invoke-Stage {
    param([Parameter(Mandatory = $true)][string]$Stage)

    switch ($Stage) {
        'static-invariants' {
            Test-StaticInvariants
            if (-not $Json) { Write-Host 'PASS Repository static invariants' }
        }
        'working-tree-whitespace' {
            Invoke-Required 'Working-tree whitespace check' 'git' @('diff', '--check')
            Invoke-Required 'Staged whitespace check' 'git' @('diff', '--check', '--cached')
            if (-not [string]::IsNullOrWhiteSpace($BaseRevision) -and
                -not [string]::IsNullOrWhiteSpace($HeadRevision) -and
                [bool]$plan.changeContextValid) {
                Invoke-Required 'Base/head diff whitespace check' 'git' @('diff', '--check', $BaseRevision, $HeadRevision)
            }
        }
        'validation-planner-matrix' {
            Invoke-Required 'Run deterministic validation impact-planner matrix' 'pwsh' @(
                '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', 'scripts\validation-plan.tests.ps1'
            )
        }
        'coordinator-suite' {
            $previousOfflineTestScope = $env:DEVBRIDGE_OFFLINE_TEST_SCOPE
            try {
                if (@($plan.selectedStages) -contains 'bridge-tools-build') {
                    Remove-Item Env:DEVBRIDGE_OFFLINE_TEST_SCOPE -ErrorAction SilentlyContinue
                } else {
                    $env:DEVBRIDGE_OFFLINE_TEST_SCOPE = 'coordinator'
                }
                Invoke-Required 'Restore Coordinator.Tests and transitive Coordinator projects' 'dotnet' @(
                    'restore', $testsProject, $restoreMode, '--nologo'
                )
                Invoke-Required 'Build Coordinator.Tests and transitive Coordinator projects Release' 'dotnet' @(
                    'build', $testsProject, '--configuration', 'Release', '--no-restore', '--nologo'
                )
                Invoke-Required 'Run applicable offline coordinator test suite' 'dotnet' @(
                    'run', '--project', $testsProject, '--configuration', 'Release', '--no-build', '--no-restore'
                )
            } finally {
                if ($null -eq $previousOfflineTestScope) {
                    Remove-Item Env:DEVBRIDGE_OFFLINE_TEST_SCOPE -ErrorAction SilentlyContinue
                } else {
                    $env:DEVBRIDGE_OFFLINE_TEST_SCOPE = $previousOfflineTestScope
                }
            }
        }
        'coordinator-build' {
            Invoke-Required 'Restore Coordinator and transitive Coordinator.Core' 'dotnet' @(
                'restore', $coordinatorProject, $restoreMode, '--nologo'
            )
            Invoke-Required 'Build Coordinator and transitive Coordinator.Core Release' 'dotnet' @(
                'build', $coordinatorProject, '--configuration', 'Release', '--no-restore', '--nologo'
            )
        }
        'bridge-tools-build' {
            Invoke-Required 'Restore BridgeTools' 'dotnet' @(
                'restore', $bridgeToolsProject, $restoreMode, '--nologo'
            )
            Invoke-Required 'Build BridgeTools Release' 'dotnet' @(
                'build', $bridgeToolsProject, '--configuration', 'Release', '--no-restore', '--nologo'
            )
            Test-BridgeToolsOutput -RequireOutput
            if (-not $Json) { Write-Host 'PASS BridgeTools output excludes RimBridgeServer.Sdk.dll' }
        }
        'fake-rimworld-build' {
            Invoke-Required 'Restore FakeRimWorld process host' 'dotnet' @(
                'restore', $fakeRimWorldProject, $restoreMode, '--nologo'
            )
            Invoke-Required 'Build FakeRimWorld process host Release' 'dotnet' @(
                'build', $fakeRimWorldProject, '--configuration', 'Release', '--no-restore', '--nologo'
            )
        }
        'dev-plan-matrix' {
            Invoke-Required 'Run deterministic development-plan matrix' 'pwsh' @(
                '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', 'scripts\dev-plan.tests.ps1'
            )
        }
        'dev-publish-matrix' {
            Invoke-Required 'Run development artifact hash/deployment matrix' 'pwsh' @(
                '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', 'scripts\dev-publish.tests.ps1'
            )
        }
        'live-stack-matrix' {
            Invoke-Required 'Run live-stack smoke offline orchestration/config tests' 'pwsh' @(
                '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', 'scripts\live-stack-smoke.tests.ps1'
            )
        }
        'process-e2e' {
            Invoke-Required 'Run process-level FakeRimWorld E2E suite' 'pwsh' @(
                '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', 'scripts\process-e2e.tests.ps1'
            )
        }
        default {
            throw "Unknown validation stage selected by the planner: $Stage"
        }
    }
}

function Write-PlanSummary {
    param(
        [Parameter(Mandatory = $true)]$Plan,
        [Parameter(Mandatory = $true)][string[]]$ExecutionStages
    )

    if ($Json) { return }
    Write-Host 'DevBridge impact-aware validation'
    Write-Host ('  Changed inputs: ' + $Plan.changedInputCount)
    Write-Host ('  Change class:   ' + $Plan.changeClass)
    Write-Host ('  Planner mode:   ' + $Plan.mode + $(if ($Plan.status -eq 'conservative') { ' (conservative)' } else { '' }))
    Write-Host ('  Selected stages: ' + $ExecutionStages.Count)
    foreach ($stage in @($Plan.selectedValidation | Where-Object { $ExecutionStages -contains $_.stage })) {
        Write-Host ('    + ' + $stage.stage + ': ' + $stage.reason)
    }
    foreach ($stage in @($Plan.skippedValidation)) {
        Write-Host ('    - ' + $stage.stage + ': ' + $stage.reason)
    }
    if ($null -ne $Plan.conservativeEscalation) {
        Write-Host '  Conservative escalation:'
        foreach ($reason in @($Plan.conservativeEscalation)) {
            Write-Host ('    ! ' + $reason)
        }
    }
    if ($InvariantsOnly) {
        Write-Host '  Explicit live-stack safety mode: invariants and whitespace only.'
    }
}

$plan = $null
$executedStages = [System.Collections.Generic.List[string]]::new()
try {
    $plan = Get-ValidationPlan
    $executionStages = if ($InvariantsOnly) {
        @('static-invariants', 'working-tree-whitespace')
    } else {
        @($plan.selectedStages)
    }
    Write-PlanSummary $plan $executionStages
    foreach ($stage in $executionStages) {
        Invoke-Stage $stage
        [void]$executedStages.Add($stage)
    }

    $report = [ordered]@{
        schemaVersion = 'devbridge-validation/v2'
        status = 'pass'
        plan = $plan
        executedStages = @($executedStages)
        selectedStages = @($executionStages)
        invariantsOnly = [bool]$InvariantsOnly
    }
    if ($Json) {
        Write-Output ($report | ConvertTo-Json -Depth 16)
    } else {
        Write-Host ("`nVALIDATION PASS ($($executionStages.Count) stages)")
    }
    exit 0
} catch {
    $message = $_.Exception.Message
    if ($Json) {
        $failureReport = [ordered]@{
            schemaVersion = 'devbridge-validation/v2'
            status = 'fail'
            plan = $plan
            executedStages = @($executedStages)
            failure = $message
        }
        Write-Output ($failureReport | ConvertTo-Json -Depth 16)
        exit 1
    }
    throw
}
