[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$smokeScript = Join-Path $PSScriptRoot 'live-stack-smoke.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('DevBridge2-live-stack-tests-' + $PID)

function Assert-True {
    param([Parameter(Mandatory = $true)]$Condition, [Parameter(Mandatory = $true)][string]$Message)
    if (-not $Condition) { throw $Message }
}

function Invoke-Plan {
    param(
        [Parameter(Mandatory = $true)][string]$DevRoot,
        [Parameter(Mandatory = $true)][string]$GameRoot,
        [Parameter(Mandatory = $true)][string]$TestRoot,
        [Parameter(Mandatory = $true)][string]$ErrorRoot
    )
    $arguments = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $smokeScript,
        '-Plan', '-Json',
        '-DevBridgeRoot', $DevRoot,
        '-RimWorldRoot', $GameRoot,
        '-RimTestRoot', $TestRoot,
        '-RimTestPath', (Join-Path $TestRoot 'rimtest.cmd'),
        '-RimErrorPath', (Join-Path $ErrorRoot 'rimerror.cmd')
    )
    $output = & pwsh @arguments 2>&1
    $exitCode = $LASTEXITCODE
    $text = ($output -join [Environment]::NewLine).Trim()
    $json = $null
    foreach ($line in ($text -split '\r?\n')) {
        try {
            $candidate = $line.Trim() | ConvertFrom-Json -ErrorAction Stop
            if ($null -ne $candidate) { $json = $candidate }
        } catch { }
    }
    return [pscustomobject]@{ ExitCode = $exitCode; Text = $text; Json = $json }
}

function Invoke-Live {
    param(
        [Parameter(Mandatory = $true)][string]$DevRoot,
        [Parameter(Mandatory = $true)][string]$GameRoot,
        [Parameter(Mandatory = $true)][string]$TestRoot,
        [Parameter(Mandatory = $true)][string]$ErrorRoot,
        [Parameter(Mandatory = $true)][string]$ReportPath,
        [Parameter(Mandatory = $true)][string]$ErrorStorePath
    )
    $arguments = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $smokeScript,
        '-Json', '-TimeoutSeconds', '180',
        '-DevBridgeRoot', $DevRoot,
        '-RimWorldRoot', $GameRoot,
        '-RimTestRoot', $TestRoot,
        '-RimTestPath', (Join-Path $TestRoot 'rimtest.cmd'),
        '-RimErrorPath', (Join-Path $ErrorRoot 'rimerror.cmd'),
        '-RimErrorStorePath', $ErrorStorePath,
        '-ReportPath', $ReportPath
    )
    $output = & pwsh @arguments 2>&1
    $exitCode = $LASTEXITCODE
    $text = ($output -join [Environment]::NewLine).Trim()
    $json = $null
    foreach ($line in ($text -split '\r?\n')) {
        try {
            $candidate = $line.Trim() | ConvertFrom-Json -ErrorAction Stop
            if ($null -ne $candidate) { $json = $candidate }
        } catch { }
    }
    return [pscustomobject]@{ ExitCode = $exitCode; Text = $text; Json = $json }
}

try {
    New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
    $devRoot = Join-Path $testRoot 'DevBridge2'
    $gameRoot = Join-Path $testRoot 'RimWorld'
    $testRootFake = Join-Path $testRoot 'RimTest'
    $errorRoot = Join-Path $testRoot 'RimError'
    $fixtureRoot = Join-Path $devRoot 'fixture'
    New-Item -ItemType Directory -Force -Path @(
        (Join-Path $devRoot 'scripts'),
        (Join-Path $devRoot 'DevelopmentProjects'),
        (Join-Path $devRoot 'Source\BridgeTools'),
        (Join-Path $devRoot 'Runtime'),
        $fixtureRoot,
        (Join-Path $fixtureRoot 'deployed'),
        (Join-Path $gameRoot 'RimWorldWin64_Data\Managed'),
        (Join-Path $gameRoot 'Mods\RimBridgeServer\About'),
        (Join-Path $gameRoot 'Mods\Frontier\About'),
        (Join-Path $gameRoot 'Mods\Frontier\1.6\Assemblies'),
        $testRootFake,
        $errorRoot
    ) | Out-Null
    Set-Content -LiteralPath (Join-Path $devRoot 'DevBridge.cmd') -Value @'
@echo off
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0fake-owner.ps1" %*
exit /b %ERRORLEVEL%
'@ -Encoding ascii
    Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts\mod-test.ps1') -Destination (Join-Path $devRoot 'scripts\mod-test.ps1')
    Copy-Item -LiteralPath (Join-Path $repoRoot 'global.json') -Destination (Join-Path $devRoot 'global.json')
    Set-Content -LiteralPath (Join-Path $gameRoot 'RimWorldWin64.exe') -Value 'fixture' -Encoding ascii
    Set-Content -LiteralPath (Join-Path $gameRoot 'Version.txt') -Value '1.6.test rev0' -Encoding ascii
    Set-Content -LiteralPath (Join-Path $gameRoot 'Mods\RimBridgeServer\About\About.xml') -Value '<ModMetaData><packageId>brrainz.rimbridgeserver</packageId><modVersion>2.1.test</modVersion></ModMetaData>' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $gameRoot 'Mods\Frontier\About\About.xml') -Value '<ModMetaData><packageId>lan.frontier</packageId><modVersion>fixture.test</modVersion></ModMetaData>' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $fixtureRoot 'fixture.csproj') -Value '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net472</TargetFramework><LangVersion>latest</LangVersion><AssemblyName>Fixture</AssemblyName><Version>1.0.0</Version><AssemblyVersion>1.0.0.0</AssemblyVersion><FileVersion>1.0.0.0</FileVersion><InformationalVersion>fixture.test</InformationalVersion><Nullable>disable</Nullable><ImplicitUsings>disable</ImplicitUsings><Deterministic>true</Deterministic><ContinuousIntegrationBuild>true</ContinuousIntegrationBuild><DebugType>None</DebugType><DebugSymbols>false</DebugSymbols></PropertyGroup></Project>' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $fixtureRoot 'Fixture.cs') -Value 'public static class Fixture { public const int Version = 1; }' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $devRoot 'DevelopmentProjects\live-stack-fixture.json') -Value (@{
        schemaVersion = 'devbridge-mod-development/v1'
        project = 'frontier'
        sourceProject = 'fixture/fixture.csproj'
        configuration = 'Release'
        expectedAssembly = 'Fixture.dll'
        deploymentTarget = '1.6/Assemblies/Fixture.dll'
        testRecipe = 'live-stack-smoke'
    } | ConvertTo-Json -Depth 5) -Encoding utf8
    Set-Content -LiteralPath (Join-Path $devRoot 'Source\BridgeTools\DevBridge2.BridgeTools.csproj') -Value '<Project><ItemGroup><PackageReference Include="RimBridgeServer.Sdk" Version="2.0.0" /></ItemGroup></Project>' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $devRoot 'RimBridgeProtocolCompatibility.json') -Value (@{
        contractVersion = 1
        gabp = @{ major = 1; envelopeVersion = 'gabp/1' }
        rimBridgeServer = @{ testedVersions = @(); supportStatement = 'none' }
        bridgeTools = @{ sdkPackage = 'RimBridgeServer.Sdk'; sdkPackageVersion = '2.0.0' }
    } | ConvertTo-Json -Depth 8) -Encoding utf8
    Set-Content -LiteralPath (Join-Path $testRootFake 'rimtest.cmd') -Value @'
@echo off
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0fake-rimtest.ps1" %*
exit /b %ERRORLEVEL%
'@ -Encoding ascii
    Set-Content -LiteralPath (Join-Path $errorRoot 'rimerror.cmd') -Value @'
@echo off
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0fake-rimerror.ps1" %*
exit /b %ERRORLEVEL%
'@ -Encoding ascii
    Set-Content -LiteralPath (Join-Path $devRoot 'fake-owner.ps1') -Value @'
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$InputArgs)
$values = @($InputArgs)
$root = $null
$rootIndex = [Array]::IndexOf($values, '--root')
if ($rootIndex -ge 0 -and $rootIndex + 1 -lt $values.Count) { $root = $values[$rootIndex + 1] }
if ([string]::IsNullOrWhiteSpace($root)) { $root = $PSScriptRoot }
Add-Content -LiteralPath (Join-Path $root 'owner.log') -Value (($values -join ' '))
$statePath = Join-Path (Join-Path $root 'Runtime') 'mod-development-artifact.json'
$generationMarker = Join-Path $root 'fake-generation-2'
$generation = if (Test-Path -LiteralPath $generationMarker -PathType Leaf) { 2 } else { 1 }
$workflowIndex = [Array]::IndexOf($values, '--workflow-id')
$workflow = if ($workflowIndex -ge 0 -and $workflowIndex + 1 -lt $values.Count) { $values[$workflowIndex + 1] } else { 'workflow-fake' }
function Emit([object]$Value) { $Value | ConvertTo-Json -Depth 20 -Compress }
if ($values -contains 'status') {
    Emit @{ success = $true; state = 'READY'; gameState = 'READY'; generation = $generation; launchId = 'launch-fake'; rimworldPid = 4242; requestedProjects = @('frontier'); rimBridge = @{ CompanionVerified = $true; Version = '2.1.test' }; leases = @() }
    exit 0
}
if ($values -contains 'begin') {
    Emit @{ success = $true; leaseId = 'lease-11111111111111111111111111111111'; generation = $generation }
    exit 0
}
if ($values -contains 'show') {
    Emit @{ success = $true; recipe = @{ projects = @('frontier') } }
    exit 0
}
if ($values -contains 'plan') {
    Emit @{ success = $true; profileFingerprint = 'profile-fake'; alreadySatisfied = (Test-Path -LiteralPath $statePath -PathType Leaf); projectResolution = @{ profileFingerprint = 'profile-fake' } }
    exit 0
}
if ($values -contains 'run') {
    $diagnostic = $values -contains 'live-stack-diagnostic'
    $operationId = if ($diagnostic) { 'op-diagnostic-fake' } else { 'op-semantic-fake' }
    $tool = if ($diagnostic) { 'rimbridge/get_operation' } else { 'rimbridge/ping' }
    Emit @{ success = $true; workflowId = $workflow; runId = 'run-fake'; evidenceId = 'evidence-fake'; generation = 2; operations = @(@{ tool = $tool; operationId = $operationId; workflowId = $workflow; generation = 2; success = (-not $diagnostic); errorCode = if ($diagnostic) { 'RIMBRIDGE_OPERATION_NOT_FOUND' } else { $null } }) }
    exit 0
}
if ($values -contains 'stop') {
    Emit @{ success = $true; maintenanceReady = $true; gameState = 'STOPPED'; generation = $generation }
    exit 0
}
if ($values -contains 'wait-ready') {
    Set-Content -LiteralPath $generationMarker -Value 'ready' -Encoding ascii
    Emit @{ success = $true; state = 'READY'; gameState = 'READY'; generation = 2; maintenanceReady = $false; requestedProjects = @('frontier'); profileFingerprint = 'profile-fake'; launchId = 'launch-fake'; rimBridge = @{ CompanionVerified = $true; Version = '2.1.test' } }
    exit 0
}
if ($values -contains 'ensure-ready' -or $values -contains 'renew' -or $values -contains 'end' -or $values -contains 'register' -or $values -contains 'release' -or $values -contains 'resolve') {
    Emit @{ success = $true; registrationId = 'registration-fake'; projectResolution = @{ profileFingerprint = 'profile-fake' } }
    exit 0
}
Emit @{ success = $true }
'@ -Encoding utf8
    Set-Content -LiteralPath (Join-Path $testRootFake 'fake-rimtest.ps1') -Value @'
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$InputArgs)
$values = @($InputArgs)
function Emit([object]$Value) { $Value | ConvertTo-Json -Depth 20 -Compress }
if ($values -contains 'capabilities') {
    Emit @{ status = 'ok'; capabilities = @(@{ id = 'rimbridge/ping' }, @{ id = 'rimworld/get_screen_targets' }, @{ id = 'rimworld/take_screenshot' }); totalMatches = 3; truncated = $false }
    exit 0
}
if ($values -contains 'targets') {
    Emit @{ status = 'ok'; targets = @(@{ id = 'main-menu' }) }
    exit 0
}
if ($values -contains 'screenshot') {
    $path = Join-Path $PSScriptRoot 'capture.png'
    Set-Content -LiteralPath $path -Value 'fake screenshot' -Encoding ascii
    Emit @{ status = 'ok'; path = $path; operationId = 'op-screenshot-fake'; workflowId = 'workflow-fake'; evidenceId = 'evidence-screenshot-fake' }
    exit 0
}
Emit @{ status = 'ok' }
'@ -Encoding utf8
    Set-Content -LiteralPath (Join-Path $errorRoot 'fake-rimerror.ps1') -Value @'
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$InputArgs)
$values = @($InputArgs)
function OptionValue([string]$Name) {
    $index = [Array]::IndexOf($values, $Name)
    if ($index -ge 0 -and $index + 1 -lt $values.Count) { return $values[$index + 1] }
    return $null
}
function Emit([object]$Value) { $Value | ConvertTo-Json -Depth 20 -Compress }
$store = OptionValue '--store'
if ($values -contains 'ingest') {
    $integrationPath = OptionValue '--integration'
    $integration = Get-Content -LiteralPath $integrationPath -Raw | ConvertFrom-Json
    $operation = @($integration.rimBridge.operations)[0]
    @{ items = @(@{ id = 'diagnostic-fake'; op = [string]$operation.operationId; workflowId = [string]$integration.devBridge.workflowId; bridgeGen = [int]$integration.devBridge.generation; corr = 'high' }) } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $store -Encoding utf8
    Emit @{ status = 'clean'; errors = 0; warnings = 0 }
    exit 0
}
if ($values -contains 'export') {
    if (Test-Path -LiteralPath $store -PathType Leaf) { Emit (Get-Content -LiteralPath $store -Raw | ConvertFrom-Json) }
    else { Emit @{ items = @() } }
    exit 0
}
Emit @{ status = 'clean'; errors = 0; warnings = 0 }
'@ -Encoding utf8

    $plan = Invoke-Plan $devRoot $gameRoot $testRootFake $errorRoot
    Assert-True ($plan.ExitCode -eq 0) ('valid offline plan must exit zero: ' + $plan.ExitCode + ' ' + $plan.Text)
    Assert-True ($null -ne $plan.Json) 'valid offline plan must emit JSON'
    Assert-True ($plan.Json.status -eq 'plan' -and $plan.Json.success) 'valid offline plan must report plan success'
    Assert-True ($plan.Json.preflight.status -eq 'ready') 'valid offline plan must report ready preflight'
    Assert-True ($plan.Json.runtime.rimWorldVersion -eq '1.6.test rev0') 'plan must parse RimWorld version'
    Assert-True ($plan.Json.runtime.rimBridgeServerVersion -eq '2.1.test') 'plan must parse RimBridgeServer version'
    Assert-True ($plan.Json.fixture.deploymentTarget -eq '1.6/Assemblies/Fixture.dll') 'live fixture must target a RimWorld loadable assembly path'
    Assert-True ($plan.Json.fixture.sourceFingerprint -match '^[0-9a-f]{64}$') 'plan must compute a bounded source fingerprint'
    Assert-True ($plan.Text.Length -le 32768) 'plan JSON must remain bounded'
    Write-Host 'PASS live smoke valid plan parsing'

    $sentinel = Join-Path $devRoot 'owner-invoked.txt'
    Assert-True (-not (Test-Path -LiteralPath $sentinel)) 'plan mode must not invoke the lifecycle owner'

    $liveReportPath = Join-Path $devRoot 'Runtime\live-stack-smoke-test.json'
    $liveStorePath = Join-Path $devRoot 'Runtime\rimerror-test.json'
    $live = Invoke-Live $devRoot $gameRoot $testRootFake $errorRoot $liveReportPath $liveStorePath
    Assert-True ($live.ExitCode -eq 0) ('offline smoke orchestration must pass: ' + $live.ExitCode + ' ' + $live.Text)
    Assert-True ($live.Json.status -eq 'pass' -and $live.Json.success) 'offline smoke orchestration must report pass'
    Assert-True ($live.Json.fixture.deploymentRoot -like '*Mods\Frontier') 'smoke must deploy into the active project mod'
    Assert-True ($live.Json.fixture.deploymentDecision -eq 'deployed') 'first smoke transaction must deploy the fixture'
    Assert-True ($live.Json.fixture.builtArtifactSha256 -eq $live.Json.fixture.deployedArtifactSha256) 'smoke must hash-verify the deployed fixture'
    Assert-True ([bool]$live.Json.fixture.loadedArtifactFreshnessProven) 'smoke must prove loaded-artifact freshness'
    Assert-True ([bool]$live.Json.recipe.success -and $live.Json.capabilities.status -eq 'ok') 'smoke must run semantic recipe and capability discovery'
    Assert-True ($live.Json.ui.status -eq 'ok' -and $live.Json.ui.evidenceId) 'smoke must retain bounded UI evidence'
    Assert-True ([bool]$live.Json.diagnostic.rimError.correlated) 'smoke must correlate its controlled diagnostic'
    Assert-True ([bool]$live.Json.cleanup.leaseEnded -and [bool]$live.Json.cleanup.activeLeaseConfirmedAbsent) 'smoke must clean up its owner lease'
    Assert-True ([bool]$live.Json.compatibility.updated) 'successful smoke must record compatibility metadata'
    Assert-True ($live.Text.Length -le 32768) 'full smoke JSON must remain bounded'
    $stopCountBeforeNoOp = @(Select-String -LiteralPath (Join-Path $devRoot 'owner.log') -Pattern '\bstop\b').Count
    Write-Host 'PASS live smoke offline orchestration'

    $secondLive = Invoke-Live $devRoot $gameRoot $testRootFake $errorRoot (Join-Path $devRoot 'Runtime\live-stack-smoke-test-second.json') (Join-Path $devRoot 'Runtime\rimerror-test-second.json')
    Assert-True ($secondLive.ExitCode -eq 0 -and $secondLive.Json.status -eq 'pass') ('identical offline smoke orchestration must pass: ' + $secondLive.ExitCode + ' ' + $secondLive.Text)
    Assert-True ($secondLive.Json.fixture.deploymentDecision -eq 'unchanged') 'identical smoke transaction must avoid deployment'
    $stopCountAfterNoOp = @(Select-String -LiteralPath (Join-Path $devRoot 'owner.log') -Pattern '\bstop\b').Count
    Assert-True ($stopCountAfterNoOp -eq $stopCountBeforeNoOp) 'identical smoke transaction must avoid an unnecessary lifecycle stop'
    Write-Host 'PASS live smoke offline no-op orchestration'

    Remove-Item -LiteralPath (Join-Path $gameRoot 'Mods\RimBridgeServer') -Recurse -Force
    New-Item -ItemType Directory -Force -Path (Join-Path $gameRoot 'Mods\_quarantine\RimBridgeServer\About') | Out-Null
    Set-Content -LiteralPath (Join-Path $gameRoot 'Mods\_quarantine\RimBridgeServer\About\About.xml') -Value '<ModMetaData><packageId>brrainz.rimbridgeserver</packageId><modVersion>quarantined</modVersion></ModMetaData>' -Encoding utf8
    $blocked = Invoke-Plan $devRoot $gameRoot $testRootFake $errorRoot
    Assert-True ($blocked.ExitCode -eq 2) 'missing live prerequisite must use the blocked exit code'
    Assert-True ($blocked.Json.status -eq 'blocked' -and $blocked.Json.failure.code -eq 'LIVE_PREREQUISITE_MISSING') 'missing live prerequisite must fail clearly'
    Assert-True ($blocked.Json.failure.nextAction -like '*brrainz.rimbridgeserver*') 'missing RimBridgeServer must provide an actionable next step'
    Assert-True (-not [bool]$blocked.Json.preflight.checks.rimBridgeServer) 'a quarantined RimBridgeServer must not satisfy the active prerequisite'
    Write-Host 'PASS live smoke prerequisite failure'

    New-Item -ItemType Directory -Force -Path (Join-Path $gameRoot 'Mods\RimBridgeServer\About') | Out-Null
    Set-Content -LiteralPath (Join-Path $gameRoot 'Mods\RimBridgeServer\About\About.xml') -Value '<ModMetaData><packageId>brrainz.rimbridgeserver</packageId><modVersion>2.1.test</modVersion></ModMetaData>' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $devRoot 'RimBridgeProtocolCompatibility.json') -Value '{not-json' -Encoding ascii
    $invalid = Invoke-Plan $devRoot $gameRoot $testRootFake $errorRoot
    Assert-True ($invalid.Json.failure.code -eq 'LIVE_COMPATIBILITY_METADATA_INVALID') 'invalid compatibility metadata must be rejected'
    Write-Host 'PASS live smoke compatibility parsing failure'

    Write-Host 'LIVE STACK SMOKE TESTS PASS'
}
finally {
    if ([IO.Directory]::Exists($testRoot)) {
        [IO.Directory]::Delete($testRoot, $true)
    }
}
