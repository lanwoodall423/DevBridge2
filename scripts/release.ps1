[CmdletBinding()]
param(
    [ValidateSet('Release')]
    [string]$Configuration = 'Release',
    [string]$OutputRoot,
    [string]$RimWorldManagedDir,
    [string]$RimBridgeSdkPath,
    [switch]$DryRun,
    [switch]$AllowDirty
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location -LiteralPath $repoRoot
$propsPath = Join-Path $repoRoot 'Source\Directory.Build.props'
$coordinatorProject = Join-Path $repoRoot 'Source\Coordinator\DevBridge.Coordinator.csproj'
$bridgeToolsProject = Join-Path $repoRoot 'Source\BridgeTools\DevBridge2.BridgeTools.csproj'
$modProject = Join-Path $repoRoot 'Source\Mod\DevBridge2.csproj'
$validationScript = Join-Path $repoRoot 'scripts\validate.ps1'

function Invoke-Required {
    param(
        [Parameter(Mandatory = $true)][string]$Command,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$Description
    )

    Write-Host "`n== $Description =="
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Command $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Get-AuthoritativeProductVersion {
    $text = Get-Content -LiteralPath $propsPath -Raw
    $match = [regex]::Match($text,
        '<DevBridgeProductVersion>\s*([^<\s]+)\s*</DevBridgeProductVersion>',
        [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if (-not $match.Success -or $match.Groups[1].Value -notmatch '^\d+\.\d+\.\d+$') {
        throw "Source/Directory.Build.props does not contain a valid authoritative product version."
    }
    return $match.Groups[1].Value
}

function Get-GitValue {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $value = & git @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
    return ([string]$value).Trim()
}

function Get-FullPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    return [System.IO.Path]::GetFullPath($Path)
}

function Assert-SafeChildPath {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Parent
    )

    $fullPath = Get-FullPath $Path
    $fullParent = (Get-FullPath $Parent).TrimEnd('\') + '\'
    if (-not $fullPath.StartsWith($fullParent, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to manage a release path outside its output root: $fullPath"
    }
}

function Reset-GeneratedDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $Path | Out-Null
}

function Copy-RuntimeFile {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$RelativePath,
        [Parameter(Mandatory = $true)][string]$PackageRoot
    )

    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
        throw "Required release file was not produced: $Source"
    }
    $destination = Join-Path $PackageRoot ($RelativePath -replace '/', '\')
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
    Copy-Item -LiteralPath $Source -Destination $destination -Force
}

function Get-PackageFileRecords {
    param([Parameter(Mandatory = $true)][string]$PackageRoot)

    return @(Get-ChildItem -LiteralPath $PackageRoot -Recurse -File | ForEach-Object {
        $relative = [System.IO.Path]::GetRelativePath($PackageRoot, $_.FullName).Replace('\', '/')
        [pscustomobject]@{
            Path = $relative
            FullPath = $_.FullName
            Bytes = $_.Length
            Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
        }
    } | Sort-Object Path)
}

function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Content
    )

    [System.IO.File]::WriteAllText($Path, $Content,
        [System.Text.UTF8Encoding]::new($false))
}

$productVersion = Get-AuthoritativeProductVersion
$revision = Get-GitValue @('rev-parse', 'HEAD')
$status = @(git status --porcelain=v1 --untracked-files=all)
if ($LASTEXITCODE -ne 0) {
    throw 'Could not inspect the source tree status.'
}
$isDirty = $status.Count -gt 0
if ($isDirty -and (-not $DryRun -or -not $AllowDirty)) {
    throw "Release requires a clean source tree. Use -DryRun -AllowDirty only for a non-release local package check. Dirty entries:`n$($status -join "`n")"
}
if ($AllowDirty -and -not $DryRun) {
    throw '-AllowDirty is valid only with -DryRun; a release must never publish a dirty build.'
}
if ($isDirty) {
    Write-Warning 'Running a dirty dry-run. The package identity will include .dirty and is not publishable.'
}

$aboutPath = Join-Path $repoRoot 'About\About.xml'
$about = [xml](Get-Content -LiteralPath $aboutPath -Raw)
$aboutVersion = [string]$about.ModMetaData.modVersion
if (-not [string]::Equals($aboutVersion.Trim(), $productVersion, [StringComparison]::Ordinal)) {
    throw "About/About.xml version '$aboutVersion' does not match authoritative version '$productVersion'."
}
$changelog = Get-Content -LiteralPath (Join-Path $repoRoot 'CHANGELOG.md') -Raw
if ($changelog -notmatch "(?m)^##\s+$([regex]::Escape($productVersion))\s*$") {
    throw "CHANGELOG.md has no explicit release heading for $productVersion."
}

$sourceRevisionId = $revision + $(if ($isDirty) { '.dirty' } else { '' })
$informationalVersion = $productVersion + '+' + $sourceRevisionId
$buildDirty = if ($isDirty) { 'true' } else { 'false' }
$buildProperties = @(
    '-p:ContinuousIntegrationBuild=true',
    ('-p:SourceRevisionId=' + $sourceRevisionId),
    ('-p:DevBridgeBuildDirty=' + $buildDirty)
)

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot 'artifacts\release'
}
$OutputRoot = Get-FullPath $OutputRoot
$packageName = 'DevBridge2-' + $productVersion
$releaseRoot = Join-Path $OutputRoot $packageName
$stagingRoot = Join-Path $OutputRoot ('.' + $packageName + '-staging')
Assert-SafeChildPath $releaseRoot $OutputRoot
Assert-SafeChildPath $stagingRoot $OutputRoot
New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
Reset-GeneratedDirectory $stagingRoot
Reset-GeneratedDirectory $releaseRoot

Invoke-Required 'pwsh' @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $validationScript
) 'Run locked Phase 1+ validation suite'

$coordinatorPublish = Join-Path $stagingRoot 'coordinator-publish'
New-Item -ItemType Directory -Force -Path $coordinatorPublish | Out-Null
Invoke-Required 'dotnet' @(
    'restore', $coordinatorProject,
    '-r', 'win-x64',
    '--locked-mode', '--nologo'
) 'Restore Coordinator win-x64 publish assets'
Invoke-Required 'dotnet' (@(
    'publish', $coordinatorProject,
    '-c', $Configuration,
    '-r', 'win-x64',
    '--self-contained', 'false',
    '-o', $coordinatorPublish,
    '--no-restore', '--nologo'
) + $buildProperties) 'Publish Coordinator Release'

$companionBuildArguments = @(
    'build', $bridgeToolsProject,
    '-c', $Configuration,
    '-t:Rebuild',
    '--no-restore', '--nologo'
) + $buildProperties
if ([string]::IsNullOrWhiteSpace($RimBridgeSdkPath)) {
    $RimBridgeSdkPath = [Environment]::GetEnvironmentVariable('DEVBRIDGE_RIMBRIDGE_SDK_PATH')
}
if (-not [string]::IsNullOrWhiteSpace($RimBridgeSdkPath)) {
    if (-not (Test-Path -LiteralPath $RimBridgeSdkPath -PathType Leaf) -or
        -not [string]::Equals((Split-Path -Leaf $RimBridgeSdkPath),
            'RimBridgeServer.Sdk.dll', [StringComparison]::OrdinalIgnoreCase)) {
        throw "RimBridge SDK override must be an existing RimBridgeServer.Sdk.dll: $RimBridgeSdkPath"
    }
    $RimBridgeSdkPath = (Resolve-Path -LiteralPath $RimBridgeSdkPath).Path
    $companionBuildArguments += '-p:RimBridgeSdkPath=' + $RimBridgeSdkPath
}
Invoke-Required 'dotnet' $companionBuildArguments 'Build BridgeTools Release'

$companionDll = Join-Path $repoRoot 'Source\BridgeTools\bin\Release\DevBridge2.BridgeTools.dll'
if (-not (Test-Path -LiteralPath $companionDll -PathType Leaf)) {
    throw "BridgeTools build did not produce $companionDll."
}
$sdkOutput = @(Get-ChildItem -LiteralPath (Split-Path -Parent $companionDll) -Filter 'RimBridgeServer.Sdk.dll' -File -Recurse -ErrorAction SilentlyContinue)
if ($sdkOutput.Count -gt 0) {
    throw "BridgeTools output contains RimBridgeServer.Sdk.dll:`n$($sdkOutput.FullName -join "`n")"
}

$modBuilt = $false
$managedCandidate = $RimWorldManagedDir
if ([string]::IsNullOrWhiteSpace($managedCandidate)) {
    $managedCandidate = [Environment]::GetEnvironmentVariable('DEVBRIDGE_RIMWORLD_MANAGED_DIR')
}
if ([string]::IsNullOrWhiteSpace($managedCandidate)) {
    $managedCandidate = Join-Path $repoRoot '..\..\RimWorldWin64_Data\Managed'
}
if ((Test-Path -LiteralPath (Join-Path $managedCandidate 'Assembly-CSharp.dll') -PathType Leaf) -and
    (Test-Path -LiteralPath (Join-Path $managedCandidate 'UnityEngine.CoreModule.dll') -PathType Leaf)) {
    $managedCandidate = (Resolve-Path -LiteralPath $managedCandidate).Path
    $modOutput = Join-Path $stagingRoot 'mod-output'
    New-Item -ItemType Directory -Force -Path $modOutput | Out-Null
    Invoke-Required 'dotnet' (@(
        'restore', $modProject, '--locked-mode', '--nologo'
    )) 'Restore RimWorld Mod project'
    Invoke-Required 'dotnet' (@(
        'build', $modProject,
        '-c', $Configuration,
        '--no-restore', '--nologo',
        ('-p:RimWorldManagedDir=' + $managedCandidate),
        ('-p:OutputPath=' + ($modOutput.TrimEnd('\') + '\'))
    ) + $buildProperties) 'Build RimWorld Mod Release'
    $modDll = Join-Path $modOutput 'DevBridge2.dll'
    if (-not (Test-Path -LiteralPath $modDll -PathType Leaf)) {
        throw "RimWorld Mod build did not produce $modDll."
    }
    $modBuilt = $true
} else {
    Write-Warning 'RimWorld managed assemblies were not configured; packaging coordinator/BridgeTools runtime without 1.6/Assemblies/DevBridge2.dll.'
}

Copy-RuntimeFile (Join-Path $repoRoot 'DevBridge.cmd') 'DevBridge.cmd' $releaseRoot
Copy-RuntimeFile (Join-Path $repoRoot 'LoadFolders.xml') 'LoadFolders.xml' $releaseRoot
Copy-RuntimeFile (Join-Path $repoRoot 'README.md') 'README.md' $releaseRoot
Copy-RuntimeFile (Join-Path $repoRoot 'START_HERE.md') 'START_HERE.md' $releaseRoot
Copy-RuntimeFile (Join-Path $repoRoot 'MAINTENANCE.md') 'MAINTENANCE.md' $releaseRoot
Copy-RuntimeFile (Join-Path $repoRoot 'CHANGELOG.md') 'CHANGELOG.md' $releaseRoot
Copy-RuntimeFile (Join-Path $repoRoot 'RimBridgeProtocolCompatibility.json') 'RimBridgeProtocolCompatibility.json' $releaseRoot
Copy-RuntimeFile (Join-Path $repoRoot 'docs\architecture.md') 'docs/architecture.md' $releaseRoot

$recipeRoot = Join-Path $repoRoot 'TestRecipes'
if (-not (Test-Path -LiteralPath $recipeRoot -PathType Container)) {
    throw "Required repository-owned recipe directory was not found: $recipeRoot"
}
$recipeFiles = @(Get-ChildItem -LiteralPath $recipeRoot -Recurse -File |
    Sort-Object FullName)
foreach ($recipeFile in $recipeFiles) {
    $relativeRecipePath = [System.IO.Path]::GetRelativePath($recipeRoot, $recipeFile.FullName).Replace('\', '/')
    Copy-RuntimeFile $recipeFile.FullName ('TestRecipes/' + $relativeRecipePath) $releaseRoot
}

$packageAboutPath = Join-Path $releaseRoot 'About\About.xml'
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $packageAboutPath) | Out-Null
$about.ModMetaData.modVersion = $productVersion
$settings = [System.Xml.XmlWriterSettings]::new()
$settings.Encoding = [System.Text.UTF8Encoding]::new($false)
$settings.Indent = $true
$settings.NewLineChars = "`r`n"
$writer = [System.Xml.XmlWriter]::Create($packageAboutPath, $settings)
try { $about.Save($writer) } finally { $writer.Dispose() }

$requiredCoordinatorFiles = @(
    'DevBridge.Coordinator.exe',
    'DevBridge.Coordinator.dll',
    'DevBridge.Coordinator.Core.dll',
    'DevBridge.Coordinator.deps.json',
    'DevBridge.Coordinator.runtimeconfig.json'
)
foreach ($fileName in $requiredCoordinatorFiles) {
    Copy-RuntimeFile (Join-Path $coordinatorPublish $fileName) ('Coordinator/' + $fileName) $releaseRoot
}
Copy-RuntimeFile $companionDll 'BridgeTools/DevBridge2.BridgeTools.dll' $releaseRoot
if ($modBuilt) {
    Copy-RuntimeFile $modDll '1.6/Assemblies/DevBridge2.dll' $releaseRoot
}

$allowedPaths = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($path in @(
    'DevBridge.cmd', 'LoadFolders.xml', 'README.md', 'START_HERE.md', 'MAINTENANCE.md',
    'CHANGELOG.md', 'RimBridgeProtocolCompatibility.json', 'docs/architecture.md',
    'About/About.xml', 'BridgeTools/DevBridge2.BridgeTools.dll',
    'Coordinator/DevBridge.Coordinator.exe', 'Coordinator/DevBridge.Coordinator.dll',
    'Coordinator/DevBridge.Coordinator.Core.dll', 'Coordinator/DevBridge.Coordinator.deps.json',
    'Coordinator/DevBridge.Coordinator.runtimeconfig.json'
)) { [void]$allowedPaths.Add($path) }
foreach ($recipeFile in $recipeFiles) {
    $relativeRecipePath = [System.IO.Path]::GetRelativePath($recipeRoot, $recipeFile.FullName).Replace('\', '/')
    [void]$allowedPaths.Add('TestRecipes/' + $relativeRecipePath)
}
if ($modBuilt) { [void]$allowedPaths.Add('1.6/Assemblies/DevBridge2.dll') }

foreach ($record in Get-PackageFileRecords $releaseRoot) {
    if (-not $allowedPaths.Contains($record.Path)) {
        throw "Unexpected file in release package: $($record.Path)"
    }
}

$coordinatorPackagePath = Join-Path $releaseRoot 'Coordinator\DevBridge.Coordinator.dll'
$assemblyProductVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo(
    $coordinatorPackagePath).ProductVersion
if (-not [string]::IsNullOrWhiteSpace($assemblyProductVersion) -and
    -not [string]::Equals($assemblyProductVersion.Trim(), $informationalVersion,
        [StringComparison]::Ordinal)) {
    throw "Coordinator informational version '$assemblyProductVersion' does not match '$informationalVersion'."
}

$fileRecords = @(Get-PackageFileRecords $releaseRoot)
$manifest = [ordered]@{
    contract = 'devbridge-release/v1'
    productVersion = $productVersion
    informationalVersion = $informationalVersion
    sourceRevision = $revision
    dirty = $isDirty
    buildConfiguration = $Configuration
    coordinatorProtocolVersion = 2
    modBuilt = $modBuilt
    modBuildNote = if ($modBuilt) { 'RimWorld managed assemblies were available and the mod was built.' } else { 'RimWorld managed assemblies were unavailable; local host integration build remains required.' }
    files = @($fileRecords | ForEach-Object {
        [ordered]@{ path = $_.Path; bytes = $_.Bytes; sha256 = $_.Sha256 }
    })
}
$manifestPath = Join-Path $releaseRoot 'release-manifest.json'
Write-Utf8NoBom $manifestPath (($manifest | ConvertTo-Json -Depth 8) + "`r`n")

$checksumRecords = @(Get-PackageFileRecords $releaseRoot | Where-Object { $_.Path -ne 'SHA256SUMS.txt' })
$checksumText = (($checksumRecords | ForEach-Object { "$($_.Sha256)  $($_.Path)" }) -join "`r`n") + "`r`n"
Write-Utf8NoBom (Join-Path $releaseRoot 'SHA256SUMS.txt') $checksumText

$finalUnexpected = @(Get-PackageFileRecords $releaseRoot | Where-Object {
    $_.Path -notin @('release-manifest.json', 'SHA256SUMS.txt') -and -not $allowedPaths.Contains($_.Path)
})
if ($finalUnexpected.Count -gt 0) {
    throw "Unexpected final release files:`n$($finalUnexpected.Path -join "`n")"
}

Remove-Item -LiteralPath $stagingRoot -Recurse -Force
Write-Host "`nRELEASE PACKAGE PASS"
Write-Host "Package: $releaseRoot"
Write-Host "Identity: $informationalVersion"
Write-Host "Mod built: $modBuilt"
Write-Host "Checksums: $(Join-Path $releaseRoot 'SHA256SUMS.txt')"
if ($DryRun) { Write-Host 'Mode: dry-run (not publishable)' }
