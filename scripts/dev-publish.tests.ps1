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

    $identicalCoordinator = Invoke-Publish $coordinatorRoot 'Source/Coordinator/Program.cs'
    Assert-True (-not $identicalCoordinator.deployRequired) 'identical coordinator rebuild must be a deploy no-op'
    Assert-True (-not $identicalCoordinator.coordinatorShutdown) 'identical coordinator rebuild must not shut down'
    Assert-True ($identicalCoordinator.requiredRefresh -eq 'none') 'identical coordinator rebuild must not refresh'
    Write-Host 'PASS identical coordinator artifact is a no-op'

    $runtimeConfig = Join-Path $coordinatorRoot 'Coordinator\DevBridge.Coordinator.runtimeconfig.json'
    [IO.File]::AppendAllText($runtimeConfig, "`r`n")
    $changedCoordinator = Invoke-Publish $coordinatorRoot 'Source/Coordinator/Program.cs'
    Assert-True $changedCoordinator.deployRequired 'changed coordinator artifact must deploy'
    Assert-True $changedCoordinator.coordinatorShutdown 'changed coordinator artifact must use graceful shutdown'
    Assert-True (-not $changedCoordinator.rimWorldRestartRequired) 'changed coordinator artifact must not restart RimWorld'
    Write-Host 'PASS changed coordinator artifact uses graceful shutdown'

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
