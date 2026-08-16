[CmdletBinding()]
param(
    [switch]$UpdatePackages
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location -LiteralPath $repoRoot

$coordinatorCoreProject = 'Source\Coordinator.Core\DevBridge.Coordinator.Core.csproj'
$coordinatorProject = 'Source\Coordinator\DevBridge.Coordinator.csproj'
$testsProject = 'Source\Coordinator.Tests\DevBridge.Coordinator.Tests.csproj'
$bridgeToolsProject = 'Source\BridgeTools\DevBridge2.BridgeTools.csproj'
$fakeRimWorldProject = 'Source\FakeRimWorld\FakeRimWorld.csproj'
$bridgeToolsOutput = Join-Path $repoRoot 'Source\BridgeTools\bin\Release'
$restoreMode = if ($UpdatePackages) { '--force-evaluate' } else { '--locked-mode' }

function Invoke-Required {
    param(
        [Parameter(Mandatory = $true)][string]$Description,
        [Parameter(Mandatory = $true)][string]$Command,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    Write-Host "`n== $Description =="
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Command $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

Invoke-Required 'Restore Coordinator.Core' 'dotnet' @(
    'restore', $coordinatorCoreProject, $restoreMode, '--nologo'
)
Invoke-Required 'Restore Coordinator' 'dotnet' @(
    'restore', $coordinatorProject, $restoreMode, '--nologo'
)
Invoke-Required 'Restore Coordinator.Tests' 'dotnet' @(
    'restore', $testsProject, $restoreMode, '--nologo'
)
Invoke-Required 'Restore BridgeTools' 'dotnet' @(
    'restore', $bridgeToolsProject, $restoreMode, '--nologo'
)
Invoke-Required 'Restore FakeRimWorld process host' 'dotnet' @(
    'restore', $fakeRimWorldProject, $restoreMode, '--nologo'
)

Invoke-Required 'Build Coordinator.Core Release' 'dotnet' @(
    'build', $coordinatorCoreProject, '--configuration', 'Release', '--no-restore', '--nologo'
)
Invoke-Required 'Build Coordinator Release' 'dotnet' @(
    'build', $coordinatorProject, '--configuration', 'Release', '--no-restore', '--nologo'
)
Invoke-Required 'Build Coordinator.Tests Release' 'dotnet' @(
    'build', $testsProject, '--configuration', 'Release', '--no-restore', '--nologo'
)
Invoke-Required 'Build BridgeTools Release' 'dotnet' @(
    'build', $bridgeToolsProject, '--configuration', 'Release', '--no-restore', '--nologo'
)
Invoke-Required 'Build FakeRimWorld process host Release' 'dotnet' @(
    'build', $fakeRimWorldProject, '--configuration', 'Release', '--no-restore', '--nologo'
)
Invoke-Required 'Run complete offline coordinator test suite' 'dotnet' @(
    'run', '--project', $testsProject, '--configuration', 'Release', '--no-build', '--no-restore'
)
Invoke-Required 'Run deterministic development-plan/publish matrix' 'pwsh' @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', 'scripts\dev-plan.tests.ps1'
)
Invoke-Required 'Run development artifact hash/deployment matrix' 'pwsh' @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', 'scripts\dev-publish.tests.ps1'
)
Invoke-Required 'Run process-level FakeRimWorld E2E suite' 'pwsh' @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', 'scripts\process-e2e.tests.ps1'
)

Invoke-Required 'Check working-tree whitespace' 'git' @('diff', '--check')
Invoke-Required 'Check staged whitespace' 'git' @('diff', '--check', '--cached')

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

if (-not (Test-Path -LiteralPath $bridgeToolsOutput -PathType Container)) {
    throw "BridgeTools build output was not found: $bridgeToolsOutput"
}
$sdkOutput = @(Get-ChildItem -LiteralPath $bridgeToolsOutput -Filter 'RimBridgeServer.Sdk.dll' -File -Recurse -ErrorAction SilentlyContinue)
if ($sdkOutput.Count -gt 0) {
    throw "BridgeTools build output contains RimBridgeServer.Sdk.dll:`n$($sdkOutput.FullName -join "`n")"
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

Write-Host "`nVALIDATION PASS"
