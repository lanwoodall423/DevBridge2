[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$publisher = Join-Path $PSScriptRoot 'dev-publish.ps1'
$managedFixtureRoot = Join-Path $repoRoot 'TestSupport\RimWorldManagedFixtures'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('DevBridge2-dev-publish-tests-' + $PID)

function Invoke-Required {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$Description
    )
    $output = & $FilePath @Arguments 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed (exit $LASTEXITCODE):`n$($output.Trim())"
    }
}

function Build-ManagedFixture {
    param([Parameter(Mandatory = $true)][string]$OutputPath)
    New-Item -ItemType Directory -Force -Path $OutputPath | Out-Null
    foreach ($project in @('Assembly-CSharp.csproj', 'UnityEngine.CoreModule.csproj')) {
        $projectName = [IO.Path]::GetFileNameWithoutExtension($project)
        $intermediate = Join-Path $testRoot "fixture-obj\$projectName"
        Invoke-Required 'dotnet' @(
            'build', (Join-Path $managedFixtureRoot $project),
            '--configuration', 'Release', '--output', $OutputPath, '--nologo',
            "-p:BaseIntermediateOutputPath=$intermediate\",
            "-p:MSBuildProjectExtensionsPath=$intermediate\"
        ) "managed assembly fixture $project build"
    }
}

function Invoke-Publish {
    param(
        [Parameter(Mandatory = $true)][string]$DeploymentRoot,
        [Parameter(Mandatory = $true)][string]$ChangedFile,
        [string]$ManagedDirectory
    )
    $arguments = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $publisher,
        '-DeploymentRoot', $DeploymentRoot, '-ChangedFile', $ChangedFile, '-Json'
    )
    if (-not [string]::IsNullOrWhiteSpace($ManagedDirectory)) {
        $arguments += @('-RimWorldManagedDir', $ManagedDirectory)
    }
    $output = & pwsh @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dev-publish failed for $ChangedFile."
    }
    return ($output -join "`n" | ConvertFrom-Json)
}

function Invoke-PublishFailure {
    param(
        [Parameter(Mandatory = $true)][string]$DeploymentRoot,
        [Parameter(Mandatory = $true)][string]$ChangedFile,
        [string]$ManagedDirectory
    )
    $arguments = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $publisher,
        '-DeploymentRoot', $DeploymentRoot, '-ChangedFile', $ChangedFile, '-Json'
    )
    if (-not [string]::IsNullOrWhiteSpace($ManagedDirectory)) {
        $arguments += @('-RimWorldManagedDir', $ManagedDirectory)
    }
    $output = & pwsh @arguments 2>&1
    $exitCode = $LASTEXITCODE
    Assert-True ($exitCode -ne 0) "dev-publish unexpectedly succeeded for failure case $ChangedFile."
    try {
        return (($output | ForEach-Object { [string]$_ }) -join "`n" | ConvertFrom-Json)
    }
    catch {
        throw "dev-publish failure did not return structured JSON for $ChangedFile (exit $exitCode):`n$($output -join "`n")"
    }
}

function Assert-True {
    param([Parameter(Mandatory = $true)]$Condition, [Parameter(Mandatory = $true)][string]$Message)
    if (-not $Condition) { throw $Message }
}

try {
    New-Item -ItemType Directory -Force -Path $testRoot | Out-Null

    # Build/deploy once, rebuild the identical bytes, then change only the
    # deployed runtimeconfig (valid JSON whitespace) to prove the graceful
    # shutdown path is taken before a changed coordinator replacement.
    $coordinatorRoot = Join-Path $testRoot 'coordinator'
    $firstCoordinator = Invoke-Publish $coordinatorRoot 'Source/Coordinator/Program.cs'
    Assert-True $firstCoordinator.deployRequired 'initial coordinator publish must deploy'
    Assert-True (-not $firstCoordinator.rimWorldRestartRequired) 'coordinator update must not restart RimWorld'
    $firstCoordinatorArtifacts = @($firstCoordinator.artifacts | Where-Object { $_.component -eq 'coordinator' })
    Assert-True ($firstCoordinatorArtifacts.Count -eq 5) 'initial coordinator publish must report every runtime artifact'
    Assert-True (@($firstCoordinatorArtifacts | Where-Object { -not $_.identityVerified }).Count -eq 0) 'initial coordinator publish must verify every deployed identity'

    $identicalCoordinator = Invoke-Publish $coordinatorRoot 'Source/Coordinator/Program.cs'
    Assert-True (-not $identicalCoordinator.deployRequired) 'identical coordinator rebuild must be a deploy no-op'
    Assert-True (-not $identicalCoordinator.coordinatorShutdown) 'identical coordinator rebuild must not shut down'
    Assert-True ($identicalCoordinator.requiredRefresh -eq 'none') 'identical coordinator rebuild must not refresh'
    Assert-True (@($identicalCoordinator.artifacts | Where-Object { $_.component -eq 'coordinator' -and $_.reconciliationAction -ne 'unchanged' }).Count -eq 0) 'identical coordinator publish must report unchanged reconciliation'
    Write-Host 'PASS identical coordinator artifact is a no-op'

    $runtimeConfig = Join-Path $coordinatorRoot 'Coordinator\DevBridge.Coordinator.runtimeconfig.json'
    [IO.File]::AppendAllText($runtimeConfig, "`r`n")
    $changedCoordinator = Invoke-Publish $coordinatorRoot 'Source/Coordinator/Program.cs'
    Assert-True $changedCoordinator.deployRequired 'changed coordinator artifact must deploy'
    Assert-True $changedCoordinator.coordinatorShutdown 'changed coordinator artifact must use graceful shutdown'
    Assert-True (-not $changedCoordinator.rimWorldRestartRequired) 'changed coordinator artifact must not restart RimWorld'
    $changedCoordinatorArtifacts = @($changedCoordinator.artifacts | Where-Object { $_.component -eq 'coordinator' })
    Assert-True (@($changedCoordinatorArtifacts | Where-Object { -not $_.identityVerified }).Count -eq 0) 'changed coordinator publish must verify every deployed identity'
    Assert-True (@($changedCoordinatorArtifacts | Where-Object { $_.sourceSha256 -ne $_.deployedSha256 }).Count -eq 0) 'changed coordinator publish must reconcile source and destination hashes'
    Write-Host 'PASS changed coordinator artifact uses graceful shutdown'

    # A locked destination must fail before any partial content replacement and
    # return a bounded, machine-readable recovery result. Once the lock is
    # released, an interrupted temporary file must be cleaned up automatically.
    $recipeRoot = Join-Path $testRoot 'recipes'
    $recipeFile = 'TestRecipes/quicktest-smoke.json'
    $recipeReport = Invoke-Publish $recipeRoot $recipeFile
    $recipeDestination = Join-Path $recipeRoot 'TestRecipes\quicktest-smoke.json'
    $recipeBeforeLock = [IO.File]::ReadAllBytes($recipeDestination)
    [IO.File]::AppendAllText($recipeDestination, " `n")
    $recipeStaleBytes = [IO.File]::ReadAllBytes($recipeDestination)
    $recipeLock = [IO.File]::Open($recipeDestination, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $lockedRecipe = Invoke-PublishFailure $recipeRoot $recipeFile
    }
    finally {
        $recipeLock.Dispose()
    }
    Assert-True ($lockedRecipe.errorCode -eq 'DEVBRIDGE_DEPLOYMENT_DESTINATION_LOCKED') ('locked destination must return DEVBRIDGE_DEPLOYMENT_DESTINATION_LOCKED; got ' + ($lockedRecipe | ConvertTo-Json -Compress))
    Assert-True (-not $lockedRecipe.recoveryAttempted) 'content lock must not claim coordinator recovery was attempted'
    Assert-True ([Linq.Enumerable]::SequenceEqual($recipeStaleBytes, [IO.File]::ReadAllBytes($recipeDestination))) 'locked destination must remain byte-for-byte unchanged'

    $interruptedTemp = Join-Path (Split-Path -Parent $recipeDestination) ('.' + (Split-Path -Leaf $recipeDestination) + '.interrupted.tmp')
    [IO.File]::WriteAllText($interruptedTemp, 'interrupted deployment marker')
    $repairedRecipe = Invoke-Publish $recipeRoot $recipeFile
    Assert-True (-not (Test-Path -LiteralPath $interruptedTemp -PathType Leaf)) 'stale deployment temporary must be removed during reconciliation'
    $repairedRecipeArtifact = @($repairedRecipe.artifacts | Where-Object { $_.component -eq 'test-recipes' })[0]
    Assert-True $repairedRecipeArtifact.identityVerified 'repaired recipe deployment must verify identity'
    Assert-True ($repairedRecipeArtifact.reconciliationAction -eq 'replaced') 'repaired recipe deployment must report replacement'
    Assert-True ([Linq.Enumerable]::SequenceEqual($recipeBeforeLock, [IO.File]::ReadAllBytes($recipeDestination))) 'repaired recipe destination must match the source bytes'
    Write-Host 'PASS locked destination and interrupted temporary reconciliation are bounded and recoverable'

    $modRoot = Join-Path $testRoot 'mod'
    $managed = Join-Path $testRoot 'RimWorldWin64_Data\Managed'
    Build-ManagedFixture $managed
    $modReport = Invoke-Publish $modRoot 'Source/Mod/DevBridge2Mod.cs' $managed
    Assert-True ($modReport.plan.build -contains 'rimworld-mod') 'mod plan must build the mod assembly'
    Assert-True ($modReport.deployed -contains 'rimworld-mod') 'changed mod artifact must deploy'
    Assert-True ([bool]$modReport.rimWorldRestartRequired) 'changed mod artifact must require a RimWorld restart'
    Assert-True ($modReport.loadedCodeStatus -like '*not-proven*') 'mod publish must not claim loaded code'
    Write-Host 'PASS changed mod artifact reports restart and loaded-code uncertainty'

    $rimWorldRoot = Join-Path $testRoot 'rimworld'
    $companionModRoot = Join-Path $rimWorldRoot 'Mods\DevBridge2'
    New-Item -ItemType Directory -Force -Path (Join-Path $companionModRoot 'About') | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot 'About\About.xml') `
        -Destination (Join-Path $companionModRoot 'About\About.xml') -Force
    $bridgeReport = Invoke-Publish $companionModRoot 'Source/BridgeTools/DevBridgeGenerationTools.cs'
    Assert-True ($bridgeReport.plan.build -contains 'bridgeTools') 'BridgeTools plan must build the companion'
    Assert-True ($bridgeReport.deployed -contains 'bridgeTools') 'BridgeTools must deploy to the canonical location'
    Assert-True ($null -eq $bridgeReport.rimWorldRestartRequired) 'BridgeTools reload support must remain unknown'
    Assert-True ($bridgeReport.loadedCodeStatus -like '*not-proven*') 'BridgeTools publish must not claim loaded code'
    $canonical = Join-Path $rimWorldRoot 'BridgeTools\DevBridge2\DevBridge2.BridgeTools.dll'
    Assert-True (Test-Path -LiteralPath $canonical -PathType Leaf) 'BridgeTools must use the canonical sibling deployment path'
    Write-Host 'PASS canonical BridgeTools deployment remains reload-unknown'

    Write-Host 'DEV PUBLISH TESTS PASS'
}
finally {
    if ([IO.Directory]::Exists($testRoot)) {
        [IO.Directory]::Delete($testRoot, $true)
    }
}
