[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$planScript = Join-Path $PSScriptRoot 'dev-plan.ps1'

function Get-Plan {
    param(
        [Parameter(Mandatory = $true)][string]$Files
    )
    $json = & pwsh -NoProfile -ExecutionPolicy Bypass -File $planScript `
        -ChangedFile $Files -Json
    if ($LASTEXITCODE -ne 0) {
        throw "dev-plan failed for $Files."
    }
    return $json | ConvertFrom-Json
}

function Assert-Equal {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()]$Actual,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()]$Expected,
        [Parameter(Mandatory = $true)][string]$Message
    )
    if ($Actual -is [array] -or $Expected -is [array]) {
        if (($Actual -join ',') -ne ($Expected -join ',')) {
            throw "$Message. Expected '$($Expected -join ',')', got '$($Actual -join ',')'."
        }
    }
    elseif ($Actual -ne $Expected) {
        throw "$Message. Expected '$Expected', got '$Actual'."
    }
}

function Assert-Plan {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Files,
        [Parameter(Mandatory = $true)][string]$Class,
        [string[]]$Build = @(),
        [string[]]$Deploy = @(),
        [Parameter(Mandatory = $true)][string]$Refresh,
        [Parameter(Mandatory = $true)][AllowNull()]$Restart
    )
    $plan = Get-Plan $Files
    Assert-Equal $plan.changeClass $Class "$Name classification"
    Assert-Equal @($plan.build) $Build "$Name build set"
    Assert-Equal @($plan.deploy) $Deploy "$Name deploy set"
    Assert-Equal $plan.requiredRefresh $Refresh "$Name refresh"
    if ($null -eq $Restart) {
        if ($null -ne $plan.rimWorldRestartRequired) {
            throw "$Name must report unknown RimWorld restart state."
        }
    }
    else {
        Assert-Equal ([bool]$plan.rimWorldRestartRequired) ([bool]$Restart) "$Name restart"
    }
    Write-Host "PASS $Name"
}

Assert-Plan 'README only' 'README.md' 'docs-only' -Refresh 'none' -Restart $false
Assert-Plan 'tests only' 'Source/Coordinator.Tests/OfflineTests.cs' 'tests-only' -Refresh 'none' -Restart $false
Assert-Plan 'Coordinator.Core only' 'Source/Coordinator.Core/CoordinatorCore.cs' 'coordinator-core' `
    -Build @('coordinator') -Deploy @('coordinator') -Refresh 'coordinator' -Restart $false
Assert-Plan 'Coordinator host only' 'Source/Coordinator/Program.cs' 'coordinator-host' `
    -Build @('coordinator') -Deploy @('coordinator') -Refresh 'coordinator' -Restart $false
Assert-Plan 'BridgeTools only' 'Source/BridgeTools/DevBridgeGenerationTools.cs' 'BridgeTools' `
    -Build @('bridgeTools') -Deploy @('bridgeTools') -Refresh 'unknown' -Restart $null
Assert-Plan 'Mod C# only' 'Source/Mod/DevBridge2Mod.cs' 'RimWorld-mod-assembly' `
    -Build @('rimworld-mod') -Deploy @('rimworld-mod') -Refresh 'rimworld' -Restart $true
Assert-Plan 'About/XML only' 'About/About.xml' 'RimWorld-content/xml' `
    -Deploy @('rimworld-content') -Refresh 'rimworld' -Restart $true
Assert-Plan 'TestRecipes only' 'TestRecipes/quicktest-smoke.json' 'test-recipes' `
    -Deploy @('test-recipes') -Refresh 'none' -Restart $false
Assert-Plan 'Coordinator plus mod' 'Source/Coordinator/Program.cs,Source/Mod/DevBridge2Mod.cs' 'mixed' `
    -Build @('coordinator', 'rimworld-mod') -Deploy @('coordinator', 'rimworld-mod') -Refresh 'rimworld' -Restart $true

$generated = Get-Plan 'Coordinator/DevBridge.Coordinator.dll'
Assert-Equal $generated.changeClass 'none' 'generated artifact classification'
Assert-Equal @($generated.build) @() 'generated artifact build set'
Assert-Equal @($generated.deploy) @() 'generated artifact deploy set'

$dryRun = & pwsh -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'dev-publish.ps1') `
    -ChangedFile 'Source/Coordinator.Core/CoordinatorCore.cs' -DryRun -Json |
    ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $dryRun.schemaVersion -ne 'devbridge-dev-publish/v1' -or
    $dryRun.plan.build -notcontains 'coordinator' -or $dryRun.plan.rimWorldRestartRequired) {
    throw 'Coordinator dry-run publish plan was not minimal or incorrectly requested a RimWorld restart.'
}
Write-Host 'PASS coordinator dry-run publish plan'

Write-Host 'DEV PLAN TESTS PASS'
