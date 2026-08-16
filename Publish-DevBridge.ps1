[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$DeploymentRoot,
    [switch]$DeployCompanion,
    [switch]$SkipIfIdentical,
    [switch]$CompanionOnly,
    [string]$CoordinatorOutput,
    [string]$RimBridgeSdkPath
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$coordinatorProject = Join-Path $repoRoot 'Source\Coordinator\DevBridge.Coordinator.csproj'
$companionProject = Join-Path $repoRoot 'Source\BridgeTools\DevBridge2.BridgeTools.csproj'
$companionOutput = Join-Path $repoRoot ('Source\BridgeTools\bin\' + $Configuration)
$companionDll = Join-Path $companionOutput 'DevBridge2.BridgeTools.dll'

function Invoke-DotNet {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Get-FileSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)

    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString(
            $sha256.ComputeHash([System.IO.File]::ReadAllBytes($Path)))).Replace('-', '')
    } finally {
        $sha256.Dispose()
    }
}

if (-not (Test-Path -LiteralPath $companionProject -PathType Leaf)) {
    throw "Companion project was not found: $companionProject"
}

if (-not $CompanionOnly) {
    if (-not (Test-Path -LiteralPath $coordinatorProject -PathType Leaf)) {
        throw "Coordinator project was not found: $coordinatorProject"
    }

    if ([string]::IsNullOrWhiteSpace($CoordinatorOutput)) {
        $CoordinatorOutput = Join-Path $repoRoot 'Coordinator'
    }
    New-Item -ItemType Directory -Force -Path $CoordinatorOutput | Out-Null
    Invoke-DotNet @(
        'publish', $coordinatorProject,
        '-c', $Configuration,
        '-r', 'win-x64',
        '--self-contained', 'false',
        '-o', $CoordinatorOutput,
        '--nologo'
    )
}

# Rebuild the companion so a successful publish cannot redeploy an old output DLL.
$companionBuildArguments = @(
    'build', $companionProject,
    '-c', $Configuration,
    '-t:Rebuild',
    '--nologo'
)
if ([string]::IsNullOrWhiteSpace($RimBridgeSdkPath)) {
    $RimBridgeSdkPath = [Environment]::GetEnvironmentVariable('DEVBRIDGE_RIMBRIDGE_SDK_PATH')
}
if (-not [string]::IsNullOrWhiteSpace($RimBridgeSdkPath)) {
    if (-not (Test-Path -LiteralPath $RimBridgeSdkPath -PathType Leaf) -or
        -not [string]::Equals((Split-Path -Leaf $RimBridgeSdkPath), 'RimBridgeServer.Sdk.dll', [StringComparison]::OrdinalIgnoreCase)) {
        throw "RimBridge SDK override must be an existing RimBridgeServer.Sdk.dll: $RimBridgeSdkPath"
    }
    try {
        $sdkAssembly = [System.Reflection.AssemblyName]::GetAssemblyName((Resolve-Path -LiteralPath $RimBridgeSdkPath).Path)
    } catch {
        throw "RimBridge SDK override is not a readable managed assembly: $RimBridgeSdkPath"
    }
    $RimBridgeSdkPath = (Resolve-Path -LiteralPath $RimBridgeSdkPath).Path
    $companionBuildArguments += '-p:RimBridgeSdkPath=' + $RimBridgeSdkPath
    Write-Host "Using host RimBridgeServer.Sdk assembly $($sdkAssembly.Version): $RimBridgeSdkPath"
} else {
    Write-Warning 'No host SDK override supplied; the companion build will use the NuGet SDK reference. For a live host, pass -RimBridgeSdkPath or set DEVBRIDGE_RIMBRIDGE_SDK_PATH.'
}
Invoke-DotNet $companionBuildArguments

if (-not (Test-Path -LiteralPath $companionDll -PathType Leaf)) {
    throw "Companion build succeeded but produced no $companionDll."
}

$sdkOutput = Get-ChildItem -LiteralPath $companionOutput -Filter 'RimBridgeServer.Sdk.dll' -File -ErrorAction SilentlyContinue
if ($null -ne $sdkOutput) {
    throw "The companion build output contains RimBridgeServer.Sdk.dll. The SDK is host-supplied and must not be deployed."
}

Write-Host "Built companion: $companionDll"

if (-not $DeployCompanion) {
    Write-Host 'Companion deployment was not requested. Use -DeployCompanion with -DeploymentRoot <active DevBridge2 mod root>.'
    exit 0
}

if ([string]::IsNullOrWhiteSpace($DeploymentRoot)) {
    $DeploymentRoot = $repoRoot
}
if (-not (Test-Path -LiteralPath $DeploymentRoot -PathType Container)) {
    throw "Companion deployment target does not exist: $DeploymentRoot"
}
$DeploymentRoot = (Resolve-Path -LiteralPath $DeploymentRoot).Path

$aboutPath = Join-Path $DeploymentRoot 'About\About.xml'
if (-not (Test-Path -LiteralPath $aboutPath -PathType Leaf)) {
    throw "Companion deployment target is not a DevBridge2 mod root; missing $aboutPath"
}
try {
    [xml]$about = Get-Content -LiteralPath $aboutPath -Raw
    $packageId = [string]$about.ModMetaData.packageId
} catch {
    throw "Companion deployment target has unreadable About/About.xml: $aboutPath"
}
if (-not [string]::Equals($packageId.Trim(), 'lan.devbridge2', [StringComparison]::OrdinalIgnoreCase)) {
    throw "Companion deployment target is not lan.devbridge2 (packageId='$packageId'): $DeploymentRoot"
}

$modsRoot = Split-Path -Parent $DeploymentRoot
$rimWorldRoot = Split-Path -Parent $modsRoot
if ([string]::IsNullOrWhiteSpace($modsRoot) -or [string]::IsNullOrWhiteSpace($rimWorldRoot) -or
    -not [string]::Equals((Split-Path -Leaf $modsRoot), 'Mods', [StringComparison]::OrdinalIgnoreCase)) {
    throw "Companion deployment target must be an active mod directly under a RimWorld Mods directory: $DeploymentRoot"
}

$modFolderName = Split-Path -Leaf $DeploymentRoot
$destinationDirectory = Join-Path (Join-Path $rimWorldRoot 'BridgeTools') $modFolderName
New-Item -ItemType Directory -Force -Path $destinationDirectory | Out-Null
$sdkDestination = Join-Path $destinationDirectory 'RimBridgeServer.Sdk.dll'
if (Test-Path -LiteralPath $sdkDestination -PathType Leaf) {
    throw "Refusing to deploy beside RimBridgeServer.Sdk.dll. Remove the manually copied SDK from $destinationDirectory; RimBridgeServer supplies it."
}

# Older revisions incorrectly put the companion under the mod root. Remove only
# that exact generated artifact so RimWorld no longer treats BridgeTools as a mod.
$legacyDirectory = Join-Path $DeploymentRoot 'BridgeTools'
$legacyDll = Join-Path $legacyDirectory 'DevBridge2.BridgeTools.dll'
if (Test-Path -LiteralPath $legacyDll -PathType Leaf) {
    Remove-Item -LiteralPath $legacyDll -Force
    Write-Host "Removed stale mod-local companion: $legacyDll"
}
if (Test-Path -LiteralPath $legacyDirectory -PathType Container) {
    $legacyEntries = @(Get-ChildItem -LiteralPath $legacyDirectory -Force)
    if ($legacyEntries.Count -eq 0) {
        Remove-Item -LiteralPath $legacyDirectory -Force
    } elseif ($legacyEntries.Count -gt 0) {
        throw "The obsolete mod-local BridgeTools directory contains unmanaged files: $legacyDirectory"
    }
}

$destinationDll = Join-Path $destinationDirectory 'DevBridge2.BridgeTools.dll'
$sourceHash = Get-FileSha256 $companionDll
$existingHash = $null
if (Test-Path -LiteralPath $destinationDll -PathType Leaf) {
    $existingHash = Get-FileSha256 $destinationDll
}
if ($SkipIfIdentical -and $null -ne $existingHash -and
    [string]::Equals($sourceHash, $existingHash, [StringComparison]::OrdinalIgnoreCase)) {
    Write-Host "Companion deployment is a byte-identical no-op: $destinationDll"
    if (Test-Path -LiteralPath $sdkDestination -PathType Leaf) {
        throw "Companion deployment verification found RimBridgeServer.Sdk.dll in the deployment directory."
    }
    exit 0
}
$temporaryDll = Join-Path $destinationDirectory ('.DevBridge2.BridgeTools.dll.' + $PID + '.tmp')
try {
    Copy-Item -LiteralPath $companionDll -Destination $temporaryDll -Force
    Move-Item -LiteralPath $temporaryDll -Destination $destinationDll -Force
} finally {
    if (Test-Path -LiteralPath $temporaryDll -PathType Leaf) {
        Remove-Item -LiteralPath $temporaryDll -Force
    }
}

$deployedHash = Get-FileSha256 $destinationDll
if (-not [string]::Equals($sourceHash, $deployedHash, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Companion deployment verification failed; deployed DLL does not match the successful build."
}
if (Test-Path -LiteralPath $sdkDestination -PathType Leaf) {
    throw "Companion deployment verification found RimBridgeServer.Sdk.dll in the deployment directory."
}

Write-Host "Deployed exactly: $destinationDll"
Write-Host 'RimBridgeServer.Sdk.dll was not copied; RimBridgeServer supplies the host SDK.'
